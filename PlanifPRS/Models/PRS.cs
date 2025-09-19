using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.Linq;

namespace PlanifPRS.Models
{
    public class Prs
    {
        public Prs()
        {
            Checklist = new List<PrsChecklist>();
            Jalons = new List<PrsJalon>();
            Fichiers = new List<PrsFichier>();
            LiensDossier = new List<LienDossierPrs>();
        }

        [Key]
        public int Id { get; set; }

        [MaxLength(200)]
        public string? Titre { get; set; }

        [Required, MaxLength(50)]
        public string Equipement { get; set; }

        [MaxLength(100)]
        public string? ReferenceProduit { get; set; }

        public int? Quantite { get; set; }

        [MaxLength(200)]
        public string? BesoinOperateur { get; set; }

        [MaxLength(200)]
        public string? PresenceClient { get; set; }

        public DateTime DateDebut { get; set; }

        public DateTime DateFin { get; set; }

        [MaxLength(50)]
        public string Statut { get; set; } = "En attente";

        public string? InfoDiverses { get; set; }

        public DateTime DateCreation { get; set; } = DateTime.Now;

        public DateTime DerniereModification { get; set; } = DateTime.Now;

        [MaxLength(7)]
        public string? CouleurPRS { get; set; }

        [MaxLength(100)]
        public string? CreatedByLogin { get; set; }

        // Nouvelles propriétés pour stocker les anciennes dates de validation
        public DateTime? AncienneDateDebut { get; set; }
        public DateTime? AncienneDateFin { get; set; }

        // Ajouter ces propriétés à la classe Prs existante
        public int? PrsParentId { get; set; }

        [ForeignKey("PrsParentId")]
        [ValidateNever]
        public Prs? PrsParent { get; set; }

        // Propriété de navigation pour les enfants
        [ValidateNever]
        public ICollection<Prs> PrsEnfants { get; set; } = new List<Prs>();

        // Relations
        [ValidateNever]
        public List<PrsChecklist> Checklist { get; set; }

        [ValidateNever]
        public List<PrsJalon> Jalons { get; set; }

        [ValidateNever]
        public List<PrsFichier> Fichiers { get; set; }

        [ValidateNever]
        public List<LienDossierPrs> LiensDossier { get; set; }

        public int? FamilleId { get; set; }

        [ForeignKey("FamilleId")]
        [ValidateNever]
        public PrsFamille? Famille { get; set; }

        public int LigneId { get; set; }

        [ValidateNever]
        public Ligne Ligne { get; set; }

        public ICollection<PrsAffectation> Affectations { get; set; } = new List<PrsAffectation>();


        // Propriétés calculées pour la checklist
        [NotMapped]
        public bool AChecklist => Checklist != null && Checklist.Any();

        [NotMapped]
        public int NombreElementsChecklist => Checklist?.Count ?? 0;

        [NotMapped]
        public int NombreElementsValides => Checklist?.Count(c => c.EstValide) ?? 0;

        [NotMapped]
        public int NombreElementsObligatoires => Checklist?.Count(c => c.Obligatoire) ?? 0;

        [NotMapped]
        public int NombreElementsObligatoiresValides => Checklist?.Count(c => c.Obligatoire && c.EstValide) ?? 0;

        [NotMapped]
        public double PourcentageCompletionChecklist
        {
            get
            {
                if (NombreElementsChecklist == 0) return 100;
                return Math.Round((double)NombreElementsValides / NombreElementsChecklist * 100, 1);
            }
        }

        [NotMapped]
        public double PourcentageCompletionObligatoire
        {
            get
            {
                if (NombreElementsObligatoires == 0) return 100;
                return Math.Round((double)NombreElementsObligatoiresValides / NombreElementsObligatoires * 100, 1);
            }
        }

        [NotMapped]
        public string StatutChecklist
        {
            get
            {
                if (!AChecklist) return "Aucune checklist";
                if (PourcentageCompletionObligatoire < 100) return "Éléments obligatoires manquants";
                if (PourcentageCompletionChecklist == 100) return "Complète";
                return "En cours";
            }
        }

        [NotMapped]
        public string CouleurStatutChecklist
        {
            get
            {
                if (!AChecklist) return "#6c757d";
                if (PourcentageCompletionObligatoire < 100) return "#dc3545";
                if (PourcentageCompletionChecklist == 100) return "#28a745";
                return "#ffc107";
            }
        }

        [NotMapped]
        public string IconeStatutChecklist
        {
            get
            {
                if (!AChecklist) return "fas fa-minus-circle";
                if (PourcentageCompletionObligatoire < 100) return "fas fa-exclamation-triangle";
                if (PourcentageCompletionChecklist == 100) return "fas fa-check-circle";
                return "fas fa-clock";
            }
        }
    }
}