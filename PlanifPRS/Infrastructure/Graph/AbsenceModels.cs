namespace PlanifPRS.Infrastructure.Graph;

public class PresenceInfo
{
    public string Email { get; set; } = "";
    public string? Activity { get; set; }
    public string? Availability { get; set; }
    public bool IsOutOfOffice { get; set; }
    public string? OoOMessage { get; set; }
}

public class AbsenceEvent
{
    public string Subject { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public bool IsOutOfOffice { get; set; }
    public string Source { get; set; } = "calendar"; // nouveau champ
}

public class UserAbsenceAggregate
{
    public string Email { get; set; } = "";
    public PresenceInfo? Presence { get; set; }
    public List<AbsenceEvent> Events { get; set; } = new();
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}