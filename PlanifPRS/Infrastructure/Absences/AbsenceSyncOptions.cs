using System.Collections.Generic;

namespace PlanifPRS.Infrastructure.Absences;

public class AbsenceSyncOptions
{
    public string RunTime { get; set; } = "06:00";
    public string TimeZone { get; set; } = "Europe/Paris";
    public int DaysForward { get; set; } = 30;

    // Fallback manuel si BD vide
    public List<string> Users { get; set; } = new();

    public string OutputDirectory { get; set; } = "Data/Absences";
    public bool LazyTriggerEnabled { get; set; } = true;
    public string? ApiKey { get; set; }

    // Optionnel : filtre sur colonne Service (dbo.Utilisateurs.Service)
    public string? ServiceFilter { get; set; }

    // POINT 1 : getSchedule
    public string? AnchorUserEmail { get; set; }
    public int ScheduleChunkSize { get; set; } = 50;

    // POINT 5 : Cache MailboxSettings
    public string? MailboxCacheDirectory { get; set; } = "Data/Absences/mailbox-cache";
    public int MailboxDefaultRefreshHours { get; set; } = 12;
    public int MailboxEnabledRefreshHours { get; set; } = 6;

    // POINT 9 : Skip utilisateurs “inutiles”
    public int UselessUserRunsThreshold { get; set; } = 3;
    public string? SkipUsersFile { get; set; } = "Data/Absences/skip-users.json";
}