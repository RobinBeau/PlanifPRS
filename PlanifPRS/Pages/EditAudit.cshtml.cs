using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Models;
using System.Net;

namespace PlanifPRS.Pages.Prs
{
    public class EditAuditModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;

        public EditAuditModel(PlanifPrsDbContext context)
        {
            _context = context;
        }

        [BindProperty]
        public Models.Prs Prs { get; set; } = new();

        public SelectList LigneList { get; set; } = new(new List<SelectListItem>(), "Value", "Text");

        public string? Flash { get; set; }

        // ✅ PROPRIÉTÉS POUR IDENTIFIER LE TYPE D'ÉVÉNEMENT
        public string EventType { get; set; } = "";
        public string EventDetails { get; set; } = "";

        public async Task<IActionResult> OnGetAsync(int? id)
        {
            // ✅ MÊME LOGIQUE QUE VOTRE PAGE USERS
            if (!HasRequiredRole())
            {
                return Redirect("/AccessDenied");
            }

            if (id == null)
            {
                return NotFound();
            }

            try
            {
                var prs = await _context.Prs.FirstOrDefaultAsync(m => m.Id == id);
                if (prs == null)
                {
                    return NotFound();
                }

                // ✅ VÉRIFIER QUE C'EST BIEN UN AUDIT/INTERVENTION/VISITE CLIENT
                if (!IsSpecialEvent(prs.Equipement))
                {
                    return RedirectToPage("./Edit", new { id = id });
                }

                Prs = prs;

                // ✅ DÉCODER IMMÉDIATEMENT APRÈS LE CHARGEMENT
                if (!string.IsNullOrEmpty(Prs.Titre))
                {
                    Prs.Titre = System.Net.WebUtility.HtmlDecode(Prs.Titre);
                }

                if (!string.IsNullOrEmpty(Prs.InfoDiverses))
                {
                    Prs.InfoDiverses = System.Net.WebUtility.HtmlDecode(Prs.InfoDiverses);
                }

                await ChargerLignesAsync();

                // ✅ EXTRAIRE TYPE ET DÉTAILS DEPUIS LE TITRE (maintenant décodé)
                ExtractEventInfo();

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
                    ExtractEventInfo(); // Pour réafficher les données
                    return Page();
                }

                await ConstruirePrsAsync(eventType, eventDetails);

                _context.Update(Prs);
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = $"✅ {eventType} '{eventDetails}' modifié(e) avec succès pour le {Prs.DateDebut:dd/MM/yyyy à HH:mm} par {User.Identity?.Name} !";

                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, $"Erreur : {ex.Message}");
                ExtractEventInfo();
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

                // ✅ VÉRIFIER LES DROITS REQUIS POUR EDITAUDIT
                var droitsAutorises = new[] { "admin", "cdp", "process", "maintenance", "validateur" };
                var droitUser = user.Droits?.ToLower() ?? "";

                return droitsAutorises.Contains(droitUser);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Vérifie si c'est un événement (Audit, Intervention, Visite Client)
        /// </summary>
        private static bool IsSpecialEvent(string? equipement)
        {
            return !string.IsNullOrEmpty(equipement) &&
                   new[] { "Audit", "Intervention", "Visite Client" }.Contains(equipement);
        }

        /// <summary>
        /// Extrait le type et les détails depuis le titre existant
        /// </summary>
        private void ExtractEventInfo()
        {
            if (!string.IsNullOrEmpty(Prs.Titre))
            {
                // Format attendu : "Type - Détails"
                var parts = Prs.Titre.Split(" - ", 2);
                if (parts.Length >= 2)
                {
                    EventType = parts[0].Trim();
                    EventDetails = parts[1].Trim();
                }
                else
                {
                    EventType = Prs.Equipement ?? "";
                    EventDetails = Prs.Titre; // Maintenant déjà décodé
                }
            }
            else
            {
                EventType = Prs.Equipement ?? "";
                EventDetails = "";
            }

            // ✅ PASSER À LA VUE
            ViewData["PreselectedEventType"] = EventType;
            ViewData["PreselectedEventDetails"] = EventDetails;
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

            // ✅ DÉTAILS OBLIGATOIRES
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
            // ✅ DÉTAILS OBLIGATOIRES
            Prs.Titre = $"{eventType} - {eventDetails}";
            Prs.Equipement = eventType;

            // ✅ CONSERVER L'ID DE FAMILLE EXISTANT OU LE METTRE À JOUR
            if (Prs.FamilleId == null || Prs.FamilleId <= 0)
            {
                Prs.FamilleId = await GetFamilleIdAsync(eventType);
            }

            // ✅ STATUT SELON LE RÔLE DE L'UTILISATEUR
            var login = User.Identity?.Name?.Split('\\').LastOrDefault();
            var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login);
            var droitUser = user?.Droits?.ToLower() ?? "";
            var isAdminOrValidateur = new[] { "admin", "validateur" }.Contains(droitUser);

            Prs.Statut = isAdminOrValidateur ? "Validé" : "En attente";
            Prs.DerniereModification = DateTime.Now;



            // ✅ CONSERVER LA DATE DE CRÉATION ORIGINALE
            // Prs.DateCreation = DateTime.Now; // NE PAS MODIFIER

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