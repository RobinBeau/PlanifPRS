using PlanifPRS.Infrastructure.Graph;

namespace PlanifPRS.Infrastructure.Absences;

public interface IAbsenceRepository
{
    Task SaveSnapshotAsync(IEnumerable<UserAbsenceAggregate> aggregates, DateTime dateUtc, CancellationToken ct);
    Task<IReadOnlyList<UserAbsenceAggregate>> GetLatestSnapshotAsync(CancellationToken ct);
}