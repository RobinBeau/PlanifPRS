using System.Text.Json;
using PlanifPRS.Infrastructure.Graph;

namespace PlanifPRS.Infrastructure.Absences;

public class JsonAbsenceRepository : IAbsenceRepository
{
    private readonly AbsenceSyncOptions _options;
    private readonly ILogger<JsonAbsenceRepository> _logger;
    private readonly string _root;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public JsonAbsenceRepository(
        Microsoft.Extensions.Options.IOptions<AbsenceSyncOptions> opt,
        ILogger<JsonAbsenceRepository> logger,
        IWebHostEnvironment env)
    {
        _options = opt.Value;
        _logger = logger;
        _root = Path.Combine(env.ContentRootPath, "Data", "Absences");
        Directory.CreateDirectory(_root);
    }

    public async Task SaveSnapshotAsync(IEnumerable<UserAbsenceAggregate> aggregates, DateTime dateUtc, CancellationToken ct)
    {
        var fileName = $"absences-{dateUtc:yyyyMMdd}.json";
        var path = Path.Combine(_root, fileName);
        await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fs, aggregates, JsonOpts, ct);
        }
        _logger.LogInformation("Snapshot sauvegardé: {File}", path);

        var latest = Path.Combine(_root, "latest.json");
        try
        {
            File.Copy(path, latest, true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Impossible de mettre à jour latest.json");
        }
    }

    public async Task<IReadOnlyList<UserAbsenceAggregate>> GetLatestSnapshotAsync(CancellationToken ct)
    {
        var latest = Path.Combine(_root, "latest.json");
        if (!File.Exists(latest)) return Array.Empty<UserAbsenceAggregate>();
        await using var fs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.Read);
        var data = await JsonSerializer.DeserializeAsync<List<UserAbsenceAggregate>>(fs, JsonOpts, ct)
                   ?? new List<UserAbsenceAggregate>();
        return data;
    }
}