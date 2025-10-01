using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.Linq;

namespace PlanifPRS.Models
{
    public class ChecklistModele
    {
        public int Id { get; set; }

        [Required, MaxLength(100)]
        public string Nom { get; set; }

        [MaxLength(500)]
        public string? Description { get; set; }

        [MaxLength(100)]
        public string? FamilleEquipement { get; set; }

        public DateTime DateCreation { get; set; } = DateTime.Now;

        [MaxLength(100)]
        public string CreatedByLogin { get; set; }

        public bool Actif { get; set; } = true;

        [ValidateNever]
        public virtual ICollection<ChecklistElementModele> Elements { get; set; } = new List<ChecklistElementModele>();

        // Propriétés calculées
        [NotMapped]
        public int NombreElements => Elements?.Count ?? 0;

        [NotMapped]
        public int NombreElementsObligatoires => Elements?.Count(e => e.Obligatoire) ?? 0;

        [NotMapped]
        public string FamilleAffichage => !string.IsNullOrEmpty(FamilleEquipement) ? FamilleEquipement : "Générique";
    }

    public class ChecklistElementModele
    {
        public int Id { get; set; }


        public int ChecklistModeleId { get; set; }

        [Required, MaxLength(100)]
        public string Categorie { get; set; }

        [MaxLength(100)]
        public string? SousCategorie { get; set; }

        [Required, MaxLength(255)]
        public string Libelle { get; set; }

        [Range(1, 5)]
        public int Priorite { get; set; } = 3;

        public bool Obligatoire { get; set; }

        // Délai par défaut en jours depuis le début de la PRS
        public int DelaiDefautJours { get; set; } = 1;

        public int? GroupeId { get; set; }               // Nullable = aucun groupe
        public GroupeUtilisateurs Groupe { get; set; }   // Navigation (optionnelle)

        [ValidateNever]
        public virtual ChecklistModele ChecklistModele { get; set; }

        // Propriété calculée pour l'affichage
        [NotMapped]
        public string CategorieComplete
        {
            get
            {
                if (!string.IsNullOrEmpty(SousCategorie))
                    return $"{Categorie} - {SousCategorie}";
                return Categorie;
            }
        }

        [NotMapped]
        public string IconeCategorie
        {
            get
            {
                return Categorie?.ToLower() switch
                {
                    "produit" => "fas fa-box",
                    "documentation" => "fas fa-file-alt",
                    "process" => "fas fa-cogs",
                    "matière" => "fas fa-cubes",
                    "production" => "fas fa-industry",
                    _ => "fas fa-check-circle"
                };
            }
        }

        [NotMapped]
        public string CouleurCategorie
        {
            get
            {
                return Categorie?.ToLower() switch
                {
                    "produit" => "#007bff",
                    "documentation" => "#28a745",
                    "process" => "#fd7e14",
                    "matière" => "#6f42c1",
                    "production" => "#dc3545",
                    _ => "#6c757d"
                };
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

        [NotMapped]
        public string CouleurPriorite
        {
            get
            {
                return Priorite switch
                {
                    1 => "#dc3545", // Rouge
                    2 => "#fd7e14", // Orange
                    3 => "#ffc107", // Jaune
                    4 => "#007bff", // Bleu
                    5 => "#6c757d", // Gris
                    _ => "#ffc107"
                };
            }
        }
    }
}