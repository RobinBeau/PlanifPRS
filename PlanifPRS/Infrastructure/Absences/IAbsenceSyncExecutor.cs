namespace PlanifPRS.Infrastructure.Absences;

public interface IAbsenceSyncExecutor
{
    Task<bool> RunDailySyncIfNeededAsync(bool force, CancellationToken ct);
}