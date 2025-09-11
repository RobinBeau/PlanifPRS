namespace PlanifPRS.Infrastructure.Absences;

public interface IUserEmailProvider
{
    Task<IReadOnlyList<string>> GetActiveUserEmailsAsync(CancellationToken ct);
}