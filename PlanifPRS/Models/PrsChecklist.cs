using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace PlanifPRS.Models
{
    public class PrsChecklist
    {
        [Key]
        public int Id { get; set; }

        [ForeignKey("Prs")]
        public int PRSId { get; set; }

        // Propriétés existantes (compatibilité)
        [MaxLength(200)]
        public string? Tache { get; set; }

        public bool? Statut { get; set; }

        [MaxLength(500)]
        public string? Commentaire { get; set; }

        public int? FamilleId { get; set; }

        // Nouvelles propriétés pour l'extension
        [MaxLength(100)]
        public string? Categorie { get; set; }

        [MaxLength(100)]
        public string? SousCategorie { get; set; }

        [MaxLength(255)]
        public string? Libelle { get; set; }

        [Range(1, 5)]
        public int Priorite { get; set; } = 3;

        public bool Obligatoire { get; set; } = false;

        public bool EstCoche { get; set; } = false;

        // Délai en jours depuis le début de la PRS
        public int DelaiDefautJours { get; set; } = 1;



        public ICollection<ChecklistAffectation> Affectations { get; set; } = new List<ChecklistAffectation>();

        // Gestion des dates
        public DateTime? DateEcheance { get; set; }

        public DateTime? DateValidation { get; set; }

        [MaxLength(100)]
        public string? ValidePar { get; set; }

        [MaxLength(100)]
        public string? CreatedByLogin { get; set; }

        public DateTime DateCreation { get; set; } = DateTime.Now;


        // Sources possibles
        public int? ChecklistModeleSourceId { get; set; }
        public int? PrsSourceId { get; set; }

        // Navigation properties
        [ValidateNever]
        public virtual Prs Prs { get; set; }

        [ValidateNever]
        public virtual PrsFamille? Famille { get; set; }

        [ValidateNever]
        public virtual ChecklistModele? ChecklistModeleSource { get; set; }

        [ValidateNever]
        public virtual Prs? PrsSource { get; set; }

        // Propriétés calculées pour l'affichage
        [NotMapped]
        public string LibelleAffichage => !string.IsNullOrEmpty(Libelle) ? Libelle : Tache;

        [NotMapped]
        public bool EstValide => EstCoche || (Statut.HasValue && Statut.Value);

        [NotMapped]
        public bool EstEnRetard => DateEcheance.HasValue && DateTime.Now > DateEcheance.Value && !EstValide;

        [NotMapped]
        public string StatutAffichage
        {
            get
            {
                if (EstCoche) return "✅ Validé";
                if (Statut.HasValue && Statut.Value) return "✅ OK";
                if (Statut.HasValue && !Statut.Value) return "❌ NOK";
                if (EstEnRetard) return "⚠️ En retard";
                return "⏳ En attente";
            }
        }

        [NotMapped]
        public string CssClass
        {
            get
            {
                if (EstCoche || (Statut.HasValue && Statut.Value)) return "table-success";
                if (Statut.HasValue && !Statut.Value) return "table-danger";
                if (EstEnRetard) return "table-danger";
                if (Obligatoire) return "table-warning";
                return "";
            }
        }

        [NotMapped]
        public string PrioriteLibelle
        {
            get
            {
                return Priorite switch
                {
                    1 => "🔴 Critique",
                    2 => "🟠 Haute",
                    3 => "🟡 Normale",
                    4 => "🟢 Basse",
                    5 => "⚪ Très basse",
                    _ => "🟡 Normale"
                };
            }
        }
    }
}