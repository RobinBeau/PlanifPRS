using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Models;

namespace PlanifPRS.Pages.Prs
{
    public class AuditModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;

        public AuditModel(PlanifPrsDbContext context)
        {
            _context = context;
        }

        [BindProperty]
        public Models.Prs Prs { get; set; } = new();

        public SelectList LigneList { get; set; } = new(new List<SelectListItem>(), "Value", "Text");

        public string? Flash { get; set; }

        // ✅ PROPRIÉTÉS POUR GÉRER LES PARAMÈTRES DE L'URL
        [BindProperty(SupportsGet = true)]
        public string? EventType { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? EventDetails { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? DateDebut { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? DateFin { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool Quick { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            // ✅ MÊME LOGIQUE QUE VOTRE PAGE USERS
            if (!HasRequiredRole())
            {
                return Redirect("/AccessDenied");
            }

            try
            {
                await ChargerLignesAsync();
                InitialiserDatesDefaut();

                // ✅ PRÉ-SÉLECTIONNER LE TYPE D'ÉVÉNEMENT DEPUIS L'URL
                if (!string.IsNullOrEmpty(EventType))
                {
                    ViewData["PreselectedEventType"] = EventType;
                }

                // ✅ PRÉ-REMPLIR LES DÉTAILS SI FOURNIS
                if (!string.IsNullOrEmpty(EventDetails))
                {
                    ViewData["PreselectedEventDetails"] = EventDetails;
                }

                // ✅ PRÉ-REMPLIR LES DATES SI FOURNIES
                if (!string.IsNullOrEmpty(DateDebut) && DateTime.TryParse(DateDebut, out var debut))
                {
                    Prs.DateDebut = debut;
                }

                if (!string.IsNullOrEmpty(DateFin) && DateTime.TryParse(DateFin, out var fin))
                {
                    Prs.DateFin = fin;
                }

                // ✅ INDICATEUR DE CRÉATION RAPIDE
                if (Quick)
                {
                    ViewData["QuickMode"] = true;
                }

                return Page();
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, $"Erreur : {ex.Message}");
                return Page();
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // ✅ MÊME LOGIQUE QUE VOTRE PAGE USERS
            if (!HasRequiredRole())
            {
                return Redirect("/AccessDenied");
            }

            try
            {
                await ChargerLignesAsync();

                var eventType = Request.Form["EventType"].ToString();
                var eventDetails = Request.Form["EventDetails"].ToString();

                if (!ValiderFormulaire(eventType, eventDetails))
                {
                    return Page();
                }

                await ConstruirePrsAsync(eventType, eventDetails);

                _context.Prs.Add(Prs);
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = $"✅ {eventType} '{eventDetails}' planifié(e) avec succès pour le {Prs.DateDebut:dd/MM/yyyy à HH:mm} par {User.Identity?.Name} !";

                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, $"Erreur : {ex.Message}");
                return Page();
            }
        }

        #region ✅ MÉTHODES PRIVÉES

        /// <summary>
        /// ✅ MÊME LOGIQUE QUE VOTRE PAGE USERS - SIMPLE ET EFFICACE
        /// </summary>
        private bool HasRequiredRole()
        {
            try
            {
                // ✅ NETTOYER LE LOGIN COMME DANS VOTRE CODE USERS
                var login = User.Identity?.Name?.Split('\\').LastOrDefault();

                if (string.IsNullOrEmpty(login))
                {
                    return false;
                }

                // ✅ CHERCHER L'UTILISATEUR DANS LA BASE
                var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login);

                if (user == null || user.DateDeleted.HasValue)
                {
                    return false;
                }

                // ✅ VÉRIFIER LES DROITS REQUIS POUR AUDIT
                var droitsAutorises = new[] { "admin", "cdp", "process", "maintenance", "validateur" };
                var droitUser = user.Droits?.ToLower() ?? "";

                return droitsAutorises.Contains(droitUser);
            }
            catch
            {
                return false;
            }
        }

        private void InitialiserDatesDefaut()
        {
            var now = DateTime.Now;
            var rounded = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);

            if (Prs.DateDebut == default)
                Prs.DateDebut = rounded;

            if (Prs.DateFin == default)
                Prs.DateFin = rounded.AddHours(2);
        }

        private async Task ChargerLignesAsync()
        {
            try
            {
                var lignesList = new List<SelectListItem>();
                var connection = _context.Database.GetDbConnection();

                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT Id, Nom 
                    FROM [PlanifPRS].[dbo].[Lignes] 
                    WHERE activation = 1 AND Nom IS NOT NULL AND Nom != ''
                    ORDER BY Nom";

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var idObj = reader["Id"];
                    var nomObj = reader["Nom"];

                    if (idObj != null && idObj != DBNull.Value && int.TryParse(idObj.ToString(), out int id) && id > 0)
                    {
                        lignesList.Add(new SelectListItem
                        {
                            Value = id.ToString(),
                            Text = nomObj?.ToString() ?? "Sans nom"
                        });
                    }
                }

                LigneList = new SelectList(lignesList, "Value", "Text");
            }
            catch (Exception ex)
            {
                LigneList = new SelectList(new List<SelectListItem>(), "Value", "Text");
                throw;
            }
        }

        private bool ValiderFormulaire(string eventType, string eventDetails)
        {
            var isValid = true;

            if (string.IsNullOrEmpty(eventType) || !new[] { "Audit", "Intervention", "Visite Client" }.Contains(eventType))
            {
                ModelState.AddModelError(string.Empty, "Type d'événement invalide.");
                isValid = false;
            }

            // ✅ DÉTAILS OBLIGATOIRES - VALIDATION REMISE
            if (string.IsNullOrWhiteSpace(eventDetails))
            {
                ModelState.AddModelError(string.Empty, "Les détails de l'événement sont requis.");
                isValid = false;
            }

            if (Prs.DateFin <= Prs.DateDebut)
            {
                ModelState.AddModelError("Prs.DateFin", "La date de fin doit être postérieure à la date de début.");
                isValid = false;
            }

            if (Prs.LigneId <= 0)
            {
                ModelState.AddModelError("Prs.LigneId", "Sélection d'une ligne requise.");
                isValid = false;
            }

            ModelState.Remove("Prs.Statut");
            ModelState.Remove("Prs.ReferenceProduit");
            ModelState.Remove("Prs.Quantite");
            ModelState.Remove("Prs.BesoinOperateur");
            ModelState.Remove("Prs.PresenceClient");

            return isValid;
        }

        private async Task ConstruirePrsAsync(string eventType, string eventDetails)
        {
            // ✅ RÉCUPÉRER LE LOGIN UTILISATEUR
            var login = User.Identity?.Name?.Split('\\').LastOrDefault();

            // ✅ DÉTAILS MAINTENANT OBLIGATOIRES
            Prs.Titre = $"{eventType} - {eventDetails}";
            Prs.Equipement = eventType;
            Prs.FamilleId = await GetFamilleIdAsync(eventType);
            Prs.CreatedByLogin = login; // ✅ AJOUT DU LOGIN CRÉATEUR

            // ✅ STATUT SELON LE RÔLE DE L'UTILISATEUR
            var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login);
            var droitUser = user?.Droits?.ToLower() ?? "";
            var isAdminOrValidateur = new[] { "admin", "validateur" }.Contains(droitUser);

            Prs.Statut = isAdminOrValidateur ? "Validé" : "En attente";
            Prs.DateCreation = DateTime.Now;
            Prs.DerniereModification = DateTime.Now;
            Prs.ReferenceProduit = null;
            Prs.Quantite = null;
            Prs.BesoinOperateur = null;
            Prs.PresenceClient = null;
        }

        private async Task<int?> GetFamilleIdAsync(string eventType)
        {
            try
            {
                var connection = _context.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT TOP 1 Id FROM [PlanifPRS].[dbo].[PRS_Famille] WHERE Libelle = @eventType";
                command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@eventType", eventType));

                var result = await command.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                {
                    return Convert.ToInt32(result);
                }

                // ✅ FALLBACK SELON VOS DONNÉES
                return eventType switch
                {
                    "Audit" => 8,
                    "Intervention" => 7,
                    "Visite Client" => 6,
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}