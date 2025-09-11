namespace PlanifPRS.Infrastructure.Absences;

public class AbsenceSyncState
{
    public DateTime LastSuccessfulSyncDateUtc { get; set; } = DateTime.MinValue;
    public bool InProgress { get; set; }
}

public interface IAbsenceSyncStateStore
{
    Task<AbsenceSyncState> GetAsync(CancellationToken ct);
    Task UpdateAsync(AbsenceSyncState state, CancellationToken ct);
}