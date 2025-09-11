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
}