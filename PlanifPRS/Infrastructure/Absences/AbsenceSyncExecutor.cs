using PlanifPRS.Infrastructure.Graph;

namespace PlanifPRS.Infrastructure.Absences;

public class AbsenceSyncExecutor : IAbsenceSyncExecutor
{
    private readonly AbsenceSyncOptions _options;
    private readonly IAbsenceRepository _repo;
    private readonly IAbsenceSyncStateStore _stateStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AbsenceSyncExecutor> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public AbsenceSyncExecutor(
        Microsoft.Extensions.Options.IOptions<AbsenceSyncOptions> options,
        IAbsenceRepository repo,
        IAbsenceSyncStateStore stateStore,
        IServiceScopeFactory scopeFactory,
        ILogger<AbsenceSyncExecutor> logger)
    {
        _options = options.Value;
        _repo = repo;
        _stateStore = stateStore;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<bool> RunDailySyncIfNeededAsync(bool force, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            var state = await _stateStore.GetAsync(ct);

            var tz = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZone);
            var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
            TimeSpan.TryParse(_options.RunTime, out var runTime);
            var todayTarget = new DateTimeOffset(nowLocal.Date + runTime, nowLocal.Offset);

            bool alreadyDone = state.LastSuccessfulSyncDateUtc.Date == DateTime.UtcNow.Date;
            bool afterTarget = nowLocal >= todayTarget;

            if (!force && (alreadyDone || !afterTarget))
                return false;

            if (state.InProgress)
            {
                _logger.LogInformation("Synchronisation déjà en cours.");
                return false;
            }

            state.InProgress = true;
            await _stateStore.UpdateAsync(state, ct);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var absenceService = scope.ServiceProvider.GetRequiredService<IAbsenceService>();
                var userEmailProvider = scope.ServiceProvider.GetRequiredService<IUserEmailProvider>();

                var emails = await userEmailProvider.GetActiveUserEmailsAsync(ct);
                if (emails.Count == 0)
                {
                    _logger.LogWarning("Aucun utilisateur à synchroniser (liste vide).");
                    return false;
                }

                _logger.LogInformation("Démarrage sync (force={Force}) sur {Count} utilisateurs.", force, emails.Count);
                var from = DateTimeOffset.UtcNow.Date;
                var to = from.AddDays(_options.DaysForward);

                var results = new List<UserAbsenceAggregate>();

                foreach (var user in emails)
                {
                    try
                    {
                        var data = await absenceService.GetUserAbsenceAsync(user, from, to, ct);
                        if (data != null) results.Add(data);
                    }
                    catch (Exception exUser)
                    {
                        _logger.LogWarning(exUser, "Erreur utilisateur {User}", user);
                    }
                }

                await _repo.SaveSnapshotAsync(results, DateTime.UtcNow, ct);
                state.LastSuccessfulSyncDateUtc = DateTime.UtcNow;
                _logger.LogInformation("Sync terminée ({Count} utilisateurs).", results.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Echec sync");
                return false;
            }
            finally
            {
                state.InProgress = false;
                await _stateStore.UpdateAsync(state, ct);
            }
        }
        finally
        {
            _runLock.Release();
        }
    }
}