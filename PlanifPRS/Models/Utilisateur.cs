using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;

namespace PlanifPRS.Models
{
    [Table("Utilisateurs")]
    public class Utilisateur
    {
        // === PROPRIÉTÉS DE BASE ===

        public int Id { get; set; }
        public string? Matricule { get; set; }
        public string? Nom { get; set; }
        public string? Prenom { get; set; }
        public string? LoginWindows { get; set; }
        public string? Mail { get; set; }
        public string? Service { get; set; }
        public DateTime? DateImport { get; set; }
        public DateTime? DateDeleted { get; set; }
        public string? Droits { get; set; }

        /// <summary>
        /// Indique si l'utilisateur est chef de service
        /// </summary>
        public bool? EstChefService { get; set; }

        // === RELATIONS ===

        public ICollection<JalonUtilisateur> JalonUtilisateurs { get; set; } = new List<JalonUtilisateur>();

        // === PROPRIÉTÉS CALCULÉES (NotMapped) ===

        /// <summary>
        /// Nom complet de l'utilisateur
        /// </summary>
        [NotMapped]
        public string NomComplet => $"{Prenom} {Nom}".Trim();

        /// <summary>
        /// Vérifie si l'utilisateur est chef de service (non nullable)
        /// </summary>
        [NotMapped]
        public bool IsChefService => EstChefService == true;

        /// <summary>
        /// Vérifie si l'utilisateur est administrateur
        /// </summary>
        [NotMapped]
        public bool IsAdmin =>
            !string.IsNullOrWhiteSpace(Droits) &&
            Droits.Equals("admin", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Vérifie si l'utilisateur est validateur
        /// </summary>
        [NotMapped]
        public bool IsValidateur =>
            !string.IsNullOrWhiteSpace(Droits) &&
            Droits.Equals("validateur", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Vérifie si l'utilisateur est Chef de Projet (CDP)
        /// </summary>
        [NotMapped]
        public bool IsCdp =>
            !string.IsNullOrWhiteSpace(Droits) &&
            Droits.Equals("cdp", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Vérifie si l'utilisateur a des droits étendus (admin, validateur, CDP ou chef de service)
        /// </summary>
        [NotMapped]
        public bool HasExtendedRights => IsAdmin || IsValidateur || IsCdp || IsChefService;

        /// <summary>
        /// Récupère le nom du service nettoyé (sans préfixe technique)
        /// Exemple : "LOG_001 Logistique" → "Logistique"
        /// </summary>
        [NotMapped]
        public string ServiceClean
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Service)) return "";

                // Pattern pour détecter format "XXX_999 Nom Service"
                var match = Regex.Match(Service, @"^[A-Z]+_[0-9]+\s+(.+)$");
                return match.Success ? match.Groups[1].Value.Trim() : Service.Trim();
            }
        }

        /// <summary>
        /// Récupère le préfixe du service (ex: "LOG_001" depuis "LOG_001 Logistique")
        /// Utilisé pour filtrer tous les utilisateurs du même service
        /// </summary>
        [NotMapped]
        public string ServicePrefix
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Service)) return "";

                // Pattern pour extraire "XXX_999"
                var match = Regex.Match(Service, @"^([A-Z]+_[0-9]+)");
                if (match.Success)
                    return match.Groups[1].Value;

                // Fallback : prendre le premier mot
                var parts = Service.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 ? parts[0] : Service;
            }
        }

        /// <summary>
        /// Badge pour l'affichage dans l'interface utilisateur
        /// </summary>
        [NotMapped]
        public string RoleBadge
        {
            get
            {
                if (IsAdmin)
                    return "Administrateur";

                if (IsValidateur)
                    return "Validateur";

                if (IsCdp)
                    return "Chef de Projet";

                if (IsChefService)
                {
                    var serviceName = ServiceClean;
                    return string.IsNullOrWhiteSpace(serviceName)
                        ? "Chef de Service"
                        : $"Chef de Service ({serviceName})";
                }

                return "Utilisateur";
            }
        }

        /// <summary>
        /// Classe CSS pour le badge selon le rôle
        /// </summary>
        [NotMapped]
        public string RoleBadgeClass
        {
            get
            {
                if (IsAdmin) return "badge bg-danger";
                if (IsValidateur) return "badge bg-primary";
                if (IsCdp) return "badge bg-success";
                if (IsChefService) return "badge bg-warning text-dark";
                return "badge bg-secondary";
            }
        }

        /// <summary>
        /// Icône Font Awesome selon le rôle
        /// </summary>
        [NotMapped]
        public string RoleIcon
        {
            get
            {
                if (IsAdmin) return "fa-shield-alt";
                if (IsValidateur) return "fa-check-double";
                if (IsCdp) return "fa-project-diagram";
                if (IsChefService) return "fa-users-cog";
                return "fa-user";
            }
        }

        /// <summary>
        /// Vérifie si l'utilisateur est actif (non supprimé)
        /// </summary>
        [NotMapped]
        public bool IsActive => !DateDeleted.HasValue;

        /// <summary>
        /// Initiales de l'utilisateur (pour avatar)
        /// </summary>
        [NotMapped]
        public string Initiales
        {
            get
            {
                var initiales = "";
                if (!string.IsNullOrWhiteSpace(Prenom) && Prenom.Length > 0)
                    initiales += Prenom[0];
                if (!string.IsNullOrWhiteSpace(Nom) && Nom.Length > 0)
                    initiales += Nom[0];
                return initiales.ToUpper();
            }
        }

        /// <summary>
        /// Retourne une représentation textuelle de l'utilisateur
        /// </summary>
        public override string ToString()
        {
            return $"{NomComplet} ({LoginWindows}) - {RoleBadge}";
        }
    }
}