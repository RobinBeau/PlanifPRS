using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Models;
using System.Net;
using System.Text.Json;            // HISTO: pour sérialisation diff
using System.Collections.Generic;  // HISTO: pour Dictionary

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

        // PROPRIÉTÉS POUR IDENTIFIER LE TYPE D'ÉVÉNEMENT
        public string EventType { get; set; } = "";
        public string EventDetails { get; set; } = "";

        // autorisation suppression
        public bool CanDelete { get; set; } = false;

        public async Task<IActionResult> OnGetAsync(int? id)
        {
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

                if (!IsSpecialEvent(prs.Equipement))
                {
                    return RedirectToPage("./Edit", new { id = id });
                }

                Prs = prs;

                if (!string.IsNullOrEmpty(Prs.Titre))
                {
                    Prs.Titre = WebUtility.HtmlDecode(Prs.Titre);
                }

                if (!string.IsNullOrEmpty(Prs.InfoDiverses))
                {
                    Prs.InfoDiverses = WebUtility.HtmlDecode(Prs.InfoDiverses);
                }

                await ChargerLignesAsync();
                ExtractEventInfo();

                CanDelete = IsAdminOrValidateur() ||
                            (!string.IsNullOrEmpty(Prs.CreatedByLogin) &&
                             Prs.CreatedByLogin.Equals(GetCurrentUserLogin(), StringComparison.OrdinalIgnoreCase));

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
            if (!HasRequiredRole())
            {
                return Redirect("/AccessDenied");
            }

            try
            {
                await ChargerLignesAsync();

                var eventType = Request.Form["EventType"].ToString();
                var eventDetails = Request.Form["EventDetails"].ToString();

                // HISTO: récupérer l'état original pour diff
                var original = await _context.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == Prs.Id);
                if (original == null) return NotFound();
                if (!IsSpecialEvent(original.Equipement)) return RedirectToPage("./Edit", new { id = Prs.Id });

                if (!ValiderFormulaire(eventType, eventDetails))
                {
                    ExtractEventInfo();
                    CanDelete = IsAdminOrValidateur() ||
                                (!string.IsNullOrEmpty(Prs.CreatedByLogin) &&
                                 Prs.CreatedByLogin.Equals(GetCurrentUserLogin(), StringComparison.OrdinalIgnoreCase));
                    return Page();
                }

                await ConstruirePrsAsync(eventType, eventDetails);

                // préserver création/auteur
                Prs.DateCreation = original.DateCreation;
                Prs.CreatedByLogin = original.CreatedByLogin;

                // HISTO: construire diff avant sauvegarde
                var diffJson = BuildDiffJson(original, Prs);

                _context.Update(Prs);
                await _context.SaveChangesAsync();

                // HISTO: log si modifications
                if (!string.IsNullOrWhiteSpace(diffJson) && diffJson != "{}")
                {
                    await LogHistoriqueAsync(Prs.Id, "Modification", original.Statut, Prs.Statut, diffJson);
                }

                TempData["SuccessMessage"] = $"✅ {eventType} '{eventDetails}' modifié(e) avec succès pour le {Prs.DateDebut:dd/MM/yyyy à HH:mm} par {User.Identity?.Name} !";

                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, $"Erreur : {ex.Message}");
                ExtractEventInfo();
                CanDelete = IsAdminOrValidateur() ||
                            (!string.IsNullOrEmpty(Prs.CreatedByLogin) &&
                             Prs.CreatedByLogin.Equals(GetCurrentUserLogin(), StringComparison.OrdinalIgnoreCase));
                return Page();
            }
        }

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostSupprimerAsync(int id)
        {
            if (!HasRequiredRole()) return Redirect("/AccessDenied");

            var prs = await _context.Prs.FirstOrDefaultAsync(p => p.Id == id);
            if (prs == null) return NotFound();
            if (!IsSpecialEvent(prs.Equipement)) return RedirectToPage("./Edit", new { id });

            bool allowed = IsAdminOrValidateur() ||
                           (!string.IsNullOrEmpty(prs.CreatedByLogin) &&
                            prs.CreatedByLogin.Equals(GetCurrentUserLogin(), StringComparison.OrdinalIgnoreCase));

            if (!allowed) return Forbid();

            if (prs.Statut != "Supprimé")
            {
                var ancienStatut = prs.Statut;
                prs.Statut = "Supprimé";
                prs.DerniereModification = DateTime.Now;

                // diff simple statut
                var diff = "{\"Statut\":{\"old\":\"" + (ancienStatut ?? "") + "\",\"new\":\"Supprimé\"}}";

                await _context.SaveChangesAsync();
                await LogHistoriqueAsync(prs.Id, "Suppression", ancienStatut, "Supprimé", diff);
            }

            return RedirectToPage("/Index");
        }

        #region MÉTHODES PRIVÉES

        private string GetCurrentUserLogin()
        {
            var full = User.Identity?.Name;
            if (string.IsNullOrEmpty(full)) return "";
            var parts = full.Split('\\');
            return parts.Length > 1 ? parts[1] : full;
        }

        private bool IsAdminOrValidateur()
        {
            try
            {
                var login = GetCurrentUserLogin();
                if (string.IsNullOrEmpty(login)) return false;
                var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login && !u.DateDeleted.HasValue);
                if (user == null) return false;
                var d = user.Droits?.ToLower() ?? "";
                return d == "admin" || d == "validateur";
            }
            catch
            {
                return false;
            }
        }

        private bool HasRequiredRole()
        {
            try
            {
                var login = User.Identity?.Name?.Split('\\').LastOrDefault();
                if (string.IsNullOrEmpty(login)) return false;

                var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login);
                if (user == null || user.DateDeleted.HasValue) return false;

                var droitsAutorises = new[] { "admin", "cdp", "process", "maintenance", "validateur" };
                var droitUser = user.Droits?.ToLower() ?? "";

                return droitsAutorises.Contains(droitUser);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSpecialEvent(string? equipement)
        {
            return !string.IsNullOrEmpty(equipement) &&
                   new[] { "Audit", "Intervention", "Visite Client" }.Contains(equipement);
        }

        private void ExtractEventInfo()
        {
            if (!string.IsNullOrEmpty(Prs.Titre))
            {
                var parts = Prs.Titre.Split(" - ", 2);
                if (parts.Length >= 2)
                {
                    EventType = parts[0].Trim();
                    EventDetails = parts[1].Trim();
                }
                else
                {
                    EventType = Prs.Equipement ?? "";
                    EventDetails = Prs.Titre;
                }
            }
            else
            {
                EventType = Prs.Equipement ?? "";
                EventDetails = "";
            }

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
            catch (Exception)
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

            // on ignore vérifications modelstate sur champs non utilisés
            ModelState.Remove("Prs.Statut");
            ModelState.Remove("Prs.ReferenceProduit");
            ModelState.Remove("Prs.Quantite");
            ModelState.Remove("Prs.BesoinOperateur");
            ModelState.Remove("Prs.PresenceClient");

            return isValid;
        }

        private async Task ConstruirePrsAsync(string eventType, string eventDetails)
        {
            Prs.Titre = $"{eventType} - {eventDetails}";
            Prs.Equipement = eventType;

            if (Prs.FamilleId == null || Prs.FamilleId <= 0)
            {
                Prs.FamilleId = await GetFamilleIdAsync(eventType);
            }

            var login = User.Identity?.Name?.Split('\\').LastOrDefault();
            var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login);
            var droitUser = user?.Droits?.ToLower() ?? "";
            var isAdminOrValidateur = new[] { "admin", "validateur" }.Contains(droitUser);

            Prs.Statut = isAdminOrValidateur ? "Validé" : "En attente";
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

        // HISTO: construction diff
        private string BuildDiffJson(Models.Prs original, Models.Prs updated)
        {
            if (original == null || updated == null) return "{}";
            var diffs = new Dictionary<string, object>();

            void Compare<T>(string name, T oldVal, T newVal)
            {
                if (!EqualityComparer<T>.Default.Equals(oldVal, newVal))
                    diffs[name] = new { old = oldVal, @new = newVal };
            }

            Compare("Titre", original.Titre, updated.Titre);
            Compare("Equipement", original.Equipement, updated.Equipement);
            Compare("DateDebut", original.DateDebut.ToString("o"), updated.DateDebut.ToString("o"));
            Compare("DateFin", original.DateFin.ToString("o"), updated.DateFin.ToString("o"));
            Compare("Statut", original.Statut, updated.Statut);
            Compare("InfoDiverses", original.InfoDiverses, updated.InfoDiverses);
            Compare("LigneId", original.LigneId, updated.LigneId);
            Compare("FamilleId", original.FamilleId, updated.FamilleId);

            if (diffs.Count == 0) return "{}";
            return JsonSerializer.Serialize(diffs, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }

        // HISTO: enregistrement historique
        private async Task LogHistoriqueAsync(int prsId, string action, string ancienStatut, string nouveauStatut, string diffJson)
        {
            try
            {
                var entry = new HistoriqueEdit
                {
                    PrsId = prsId,
                    Action = action,
                    AncienStatut = ancienStatut,
                    NouveauStatut = nouveauStatut,
                    UserLogin = GetCurrentUserLogin(),
                    DateAction = DateTime.Now,
                    Changements = string.IsNullOrWhiteSpace(diffJson) ? "{}" : diffJson
                };
                _context.HistoriqueEdit.Add(entry);
                await _context.SaveChangesAsync();
            }
            catch
            {
                // silencieux
            }
        }

        #endregion
    }
}