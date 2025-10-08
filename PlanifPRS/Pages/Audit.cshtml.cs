using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Models;
using System.Text.Json;

namespace PlanifPRS.Pages.Prs
{
    public class AuditModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;
        private readonly ILogger<AuditModel> _logger;

        public AuditModel(PlanifPrsDbContext context, ILogger<AuditModel> logger)
        {
            _context = context;
            _logger = logger;
        }

        [BindProperty] public Models.Prs Prs { get; set; } = new();

        public SelectList LigneList { get; set; } =
            new(new List<SelectListItem>(), "Value", "Text");

        public string? Flash { get; set; }

        // Param GET
        [BindProperty(SupportsGet = true)] public string? EventType { get; set; }
        [BindProperty(SupportsGet = true)] public string? EventDetails { get; set; }
        [BindProperty(SupportsGet = true)] public string? DateDebut { get; set; }
        [BindProperty(SupportsGet = true)] public string? DateFin { get; set; }
        [BindProperty(SupportsGet = true)] public bool Quick { get; set; }

        // Affectations (hidden JSON)
        [BindProperty] public string? AffectationsData { get; set; }

        // Listes sources
        public List<Utilisateur> UtilisateursList { get; set; } = new();
        public List<GroupeUtilisateurs> GroupesList { get; set; } = new();

        private class AffectationDto
        {
            public int id { get; set; }
            public string type { get; set; } = "";
            public string? name { get; set; }
            public string? info { get; set; }
            public string? email { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!HasRequiredRole())
                return Redirect("/AccessDenied");

            try
            {
                await ChargerLignesAsync();
                await ChargerAffectationsSourcesAsync();
                InitialiserDatesDefaut();

                // Pré‑sélection du type si absent
                if (string.IsNullOrEmpty(EventType))
                    EventType = "Audit";

                // Injecter pour la vue (pré-sélection cartes + détails)
                ViewData["PreselectedEventType"] = EventType;
                if (!string.IsNullOrEmpty(EventDetails))
                    ViewData["PreselectedEventDetails"] = EventDetails;

                if (!string.IsNullOrEmpty(DateDebut) && DateTime.TryParse(DateDebut, out var d1))
                    Prs.DateDebut = d1;
                if (!string.IsNullOrEmpty(DateFin) && DateTime.TryParse(DateFin, out var d2))
                    Prs.DateFin = d2;

                if (string.IsNullOrEmpty(Prs.Equipement))
                    Prs.Equipement = EventType; // sync

                if (Quick) ViewData["QuickMode"] = true;

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur OnGet Audit");
                ModelState.AddModelError(string.Empty, $"Erreur : {ex.Message}");
                return Page();
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!HasRequiredRole())
                return Redirect("/AccessDenied");

            try
            {
                await ChargerLignesAsync();
                await ChargerAffectationsSourcesAsync();

                var et = Request.Form["EventType"].ToString();
                var details = Request.Form["EventDetails"].ToString();
                var equipHidden = Request.Form["Prs.Equipement"].ToString();

                if (!ValiderFormulaire(et, details))
                    return Page();

                await ConstruirePrsAsync(et, details, equipHidden);

                _context.Prs.Add(Prs);
                await _context.SaveChangesAsync();
                _logger.LogInformation("[AUDIT] PRS créée Id={Id}", Prs.Id);

                await InsererAffectationsAsync();

                TempData["SuccessMessage"] =
                    $"✅ {et} '{details}' planifié(e) avec succès pour le {Prs.DateDebut:dd/MM/yyyy à HH:mm}.";
                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur OnPost Audit");
                ModelState.AddModelError(string.Empty, $"Erreur : {ex.Message}");
                return Page();
            }
        }

        #region Helpers

        private bool HasRequiredRole()
        {
            try
            {
                var login = User.Identity?.Name?.Split('\\').LastOrDefault();
                if (string.IsNullOrEmpty(login)) return false;
                var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login);
                if (user == null || user.DateDeleted.HasValue) return false;
                var droitsAutorises = new[] { "admin", "cdp", "process", "maintenance", "validateur" };
                var droit = user.Droits?.ToLower() ?? "";
                return droitsAutorises.Contains(droit);
            }
            catch { return false; }
        }

        private void InitialiserDatesDefaut()
        {
            var now = DateTime.Now;
            var baseTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            if (Prs.DateDebut == default) Prs.DateDebut = baseTime;
            if (Prs.DateFin == default) Prs.DateFin = baseTime.AddHours(2);
        }

        private async Task ChargerLignesAsync()
        {
            try
            {
                var rows = await _context.Lignes
                    .Where(l => l.Activation == true && !string.IsNullOrEmpty(l.Nom))
                    .OrderBy(l => l.Nom)
                    .Select(l => new SelectListItem { Value = l.Id.ToString(), Text = l.Nom! })
                    .ToListAsync();

                LigneList = new SelectList(rows, "Value", "Text");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur chargement lignes");
                LigneList = new SelectList(new List<SelectListItem>(), "Value", "Text");
            }
        }

        private async Task ChargerAffectationsSourcesAsync()
        {
            UtilisateursList = await _context.Utilisateurs
                .Where(u => !u.DateDeleted.HasValue)
                .OrderBy(u => u.Nom)
                .ToListAsync();

            GroupesList = await _context.GroupesUtilisateurs
                .Include(g => g.Membres)
                .OrderBy(g => g.NomGroupe)
                .ToListAsync();
        }

        private bool ValiderFormulaire(string eventType, string details)
        {
            var ok = true;
            if (string.IsNullOrEmpty(eventType) ||
                !new[] { "Audit", "Intervention", "Visite Client" }.Contains(eventType))
            {
                ModelState.AddModelError(string.Empty, "Type d'événement invalide.");
                ok = false;
            }
            if (string.IsNullOrWhiteSpace(details))
            {
                ModelState.AddModelError(string.Empty, "Les détails de l'événement sont requis.");
                ok = false;
            }
            if (Prs.DateFin <= Prs.DateDebut)
            {
                ModelState.AddModelError("Prs.DateFin", "La date de fin doit être postérieure à la date de début.");
                ok = false;
            }
            if (Prs.LigneId <= 0)
            {
                ModelState.AddModelError("Prs.LigneId", "Sélection d'une ligne requise.");
                ok = false;
            }

            // Nettoyage champs non utilisés
            ModelState.Remove("Prs.Statut");
            ModelState.Remove("Prs.ReferenceProduit");
            ModelState.Remove("Prs.Quantite");
            ModelState.Remove("Prs.BesoinOperateur");
            ModelState.Remove("Prs.PresenceClient");

            return ok;
        }

        private async Task ConstruirePrsAsync(string eventType, string details, string equipHidden)
        {
            var login = User.Identity?.Name?.Split('\\').LastOrDefault();
            Prs.Titre = $"{eventType} - {details}";
            // Priorité à l’équipement hidden si cohérent sinon fallback eventType
            Prs.Equipement = !string.IsNullOrWhiteSpace(equipHidden) ? equipHidden : eventType;
            Prs.FamilleId = await GetFamilleIdAsync(eventType);
            Prs.CreatedByLogin = login;

            var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login);
            var droit = user?.Droits?.ToLower() ?? "";
            var canValidate = new[] { "admin", "validateur" }.Contains(droit);
            Prs.Statut = canValidate ? "Validé" : "En attente";
            Prs.DateCreation = DateTime.Now;
            Prs.DerniereModification = DateTime.Now;
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
                var p = command.CreateParameter();
                p.ParameterName = "@eventType";
                p.Value = eventType;
                command.Parameters.Add(p);

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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetFamilleIdAsync fallback");
                return null;
            }
        }

        private async Task InsererAffectationsAsync()
        {
            if (Prs.Id <= 0) return;
            if (string.IsNullOrWhiteSpace(AffectationsData)) return;

            try
            {
                var trimmed = AffectationsData.Trim();
                if (string.IsNullOrEmpty(trimmed)) return;

                List<AffectationDto>? list;
                try
                {
                    list = JsonSerializer.Deserialize<List<AffectationDto>>(trimmed,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    _logger.LogWarning("AffectationsData JSON invalide: {Data}", trimmed);
                    return;
                }

                if (list == null || !list.Any()) return;

                var login = User.Identity?.Name?.Split('\\').LastOrDefault();
                int created = 0;

                foreach (var dto in list)
                {
                    if (dto.id <= 0) continue;
                    if (dto.type != "Utilisateur" && dto.type != "Groupe") continue;

                    var entity = new PrsAffectation
                    {
                        PrsId = Prs.Id,
                        TypeAffectation = dto.type,
                        DateAffectation = DateTime.Now,
                        AffectePar = login
                    };

                    if (dto.type == "Utilisateur") entity.UtilisateurId = dto.id;
                    else entity.GroupeId = dto.id;

                    _context.PrsAffectations.Add(entity);
                    created++;
                }

                if (created > 0)
                {
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("[AUDIT] {Count} affectations insérées pour PRS {Id}", created, Prs.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur insertion affectations PRS (Audit)");
            }
        }

        #endregion
    }
}