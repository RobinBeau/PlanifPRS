using System;

namespace PlanifPRS.Models
{
    public class HistoriqueEdit
    {
        public int Id { get; set; }
        public int PrsId { get; set; }

        // "Modification" | "Suppression"
        public string Action { get; set; } = "";

        public string AncienStatut { get; set; }
        public string NouveauStatut { get; set; } = "";

        public string UserLogin { get; set; } = "";
        public DateTime DateAction { get; set; }  // Heure serveur locale

        // JSON détaillé des changements : { "Champ": { "old": "...", "new": "..." }, ... }
        public string Changements { get; set; } = "{}";

        // Navigation (optionnel)
        public Prs Prs { get; set; }
    }
}