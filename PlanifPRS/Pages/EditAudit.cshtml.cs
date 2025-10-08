using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Models;

namespace PlanifPRS.Pages.Prs
{
    public class EditAuditModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;
        private readonly ILogger<EditAuditModel> _logger;

        public EditAuditModel(PlanifPrsDbContext context, ILogger<EditAuditModel> logger)
        {
            _context = context;
            _logger = logger;
        }

        [BindProperty] public Models.Prs Prs { get; set; } = new();

        public SelectList LigneList { get; set; } = new(new List<SelectListItem>(), "Value", "Text");

        public List<Utilisateur> UtilisateursList { get; set; } = new();
        public List<GroupeUtilisateurs> GroupesList { get; set; } = new();
        public List<AffectationDisplay> AffectationsExistantes { get; set; } = new();

        [BindProperty] public string? AffectationsData { get; set; }

        // (laisse ce champ si tu l'utilises encore dans la vue, mais plus nécessaire logiquement)
        [BindProperty] public string? AffectationsToDelete { get; set; }

        public string PreselectedEventType { get; set; } = "";
        public string PreselectedEventDetails { get; set; } = "";
        public bool CanDelete { get; set; }

        private static readonly HashSet<string> TypesSpeciaux = new(StringComparer.OrdinalIgnoreCase)
        {
            "Audit","Intervention","Visite Client"
        };

        public class AffectationDisplay
        {
            public int AffectationId { get; set; }
            public string Type { get; set; } = "";
            public int SourceId { get; set; }
            public string Nom { get; set; } = "";
            public string? Info { get; set; }
            public string? Email { get; set; }
        }

        private class AffectationDto
        {
            public int id { get; set; }
            public string type { get; set; } = "";
            public string? email { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            if (!HasRequiredRole()) return Redirect("/AccessDenied");

            var prs = await _context.Prs.FirstOrDefaultAsync(p => p.Id == id);
            if (prs == null) return NotFound();
            if (!IsSpecial(prs.Equipement))
                return RedirectToPage("./Edit", new { id });

            Prs = prs;
            ExtractTypeEtDetails(prs.Titre, prs.Equipement);

            await ChargerLignesAsync();
            await ChargerSourcesAsync();
            await ChargerAffectationsExistantesAsync(id);

            // AffectationsData = liste finale initiale
            AffectationsData = JsonSerializer.Serialize(
                AffectationsExistantes.Select(a => new { id = a.SourceId, type = a.Type, email = a.Email ?? "" })
            );

            CanDelete = PeutSupprimer(prs);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!HasRequiredRole()) return Redirect("/AccessDenied");

            var original = await _context.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == Prs.Id);
            if (original == null) return NotFound();
            if (!IsSpecial(original.Equipement))
                return RedirectToPage("./Edit", new { id = Prs.Id });

            await ChargerLignesAsync();
            await ChargerSourcesAsync();
            await ChargerAffectationsExistantesAsync(Prs.Id);

            var eventType = Request.Form["EventType"].ToString()?.Trim();
            var eventDetails = Request.Form["EventDetails"].ToString()?.Trim();

            if (!ValiderFormulaire(eventType, eventDetails))
            {
                Prs = original;
                ExtractTypeEtDetails(original.Titre, original.Equipement);
                CanDelete = PeutSupprimer(original);
                return Page();
            }

            original.Titre = $"{eventType} - {eventDetails}";
            original.Equipement = eventType;
            original.InfoDiverses = Prs.InfoDiverses;
            original.LigneId = Prs.LigneId;
            original.DateDebut = Prs.DateDebut;
            original.DateFin = Prs.DateFin;
            original.DerniereModification = DateTime.Now;

            if (original.Statut != "Supprimé")
            {
                if (EstAdminOuValidateur()) original.Statut = "Validé";
                else if (original.Statut == "Validé") original.Statut = "À re-valider";
                else if (original.Statut == "À re-valider") original.Statut = "À re-valider";
                else original.Statut = "En attente";
            }

            _context.Prs.Update(original);
            await _context.SaveChangesAsync();

            await SynchroniserAffectationsParDiffAsync(original.Id);

            TempData["SuccessMessage"] = "✅ Événement mis à jour.";
            return RedirectToPage("/Index");
        }

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostSupprimerAsync(int id)
        {
            if (!HasRequiredRole()) return Redirect("/AccessDenied");
            var prs = await _context.Prs.FirstOrDefaultAsync(p => p.Id == id);
            if (prs == null) return NotFound();
            if (!IsSpecial(prs.Equipement)) return RedirectToPage("./Edit", new { id });

            if (!PeutSupprimer(prs)) return Forbid();

            if (prs.Statut != "Supprimé")
            {
                prs.Statut = "Supprimé";
                prs.DerniereModification = DateTime.Now;
                await _context.SaveChangesAsync();
            }
            TempData["SuccessMessage"] = "Événement supprimé.";
            return RedirectToPage("/Index");
        }

        #region AFFECTATIONS

        private async Task ChargerAffectationsExistantesAsync(int prsId)
        {
            AffectationsExistantes = await _context.PrsAffectations
                .Where(a => a.PrsId == prsId)
                .Include(a => a.Utilisateur)
                .Include(a => a.Groupe).ThenInclude(g => g.Membres)
                .Select(a => new AffectationDisplay
                {
                    AffectationId = a.Id,
                    Type = a.TypeAffectation,
                    SourceId = a.TypeAffectation == "Utilisateur" ? (a.UtilisateurId ?? 0) : (a.GroupeId ?? 0),
                    Nom = a.TypeAffectation == "Utilisateur"
                        ? (a.Utilisateur != null ? (a.Utilisateur.Nom + " " + a.Utilisateur.Prenom).Trim() : "(Utilisateur supprimé)")
                        : (a.Groupe != null ? a.Groupe.NomGroupe : "(Groupe supprimé)"),
                    Info = a.TypeAffectation == "Utilisateur"
                        ? (a.Utilisateur != null ? a.Utilisateur.Service : null)
                        : (a.Groupe != null ? $"{a.Groupe.Membres.Count} membres" : null),
                    Email = a.TypeAffectation == "Utilisateur" ? a.Utilisateur!.Mail : null
                })
                .OrderBy(a => a.Type)
                .ThenBy(a => a.Nom)
                .ToListAsync();
        }

        // NOUVELLE LOGIQUE : DIFF COMPLET
        private async Task SynchroniserAffectationsParDiffAsync(int prsId)
        {
            List<AffectationDto> desired;
            try
            {
                desired = string.IsNullOrWhiteSpace(AffectationsData)
                    ? new List<AffectationDto>()
                    : (JsonSerializer.Deserialize<List<AffectationDto>>(AffectationsData,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<AffectationDto>());
            }
            catch
            {
                desired = new List<AffectationDto>();
            }

            var desiredUsers = desired.Where(d => d.type == "Utilisateur").Select(d => d.id).ToHashSet();
            var desiredGroups = desired.Where(d => d.type == "Groupe").Select(d => d.id).ToHashSet();

            var existing = await _context.PrsAffectations
                .Where(a => a.PrsId == prsId)
                .ToListAsync();

            var existingUsers = existing.Where(e => e.TypeAffectation == "Utilisateur" && e.UtilisateurId.HasValue)
                                        .ToDictionary(e => e.UtilisateurId!.Value, e => e);
            var existingGroups = existing.Where(e => e.TypeAffectation == "Groupe" && e.GroupeId.HasValue)
                                         .ToDictionary(e => e.GroupeId!.Value, e => e);

            // SUPPRESSIONS : tout ce qui n'est plus dans desired
            var toDelete = new List<PrsAffectation>();
            foreach (var kv in existingUsers)
                if (!desiredUsers.Contains(kv.Key)) toDelete.Add(kv.Value);
            foreach (var kv in existingGroups)
                if (!desiredGroups.Contains(kv.Key)) toDelete.Add(kv.Value);

            if (toDelete.Any())
                _context.PrsAffectations.RemoveRange(toDelete);

            // AJOUTS : ceux de desired non déjà présents
            foreach (var u in desiredUsers)
                if (!existingUsers.ContainsKey(u))
                    _context.PrsAffectations.Add(new PrsAffectation
                    {
                        PrsId = prsId,
                        TypeAffectation = "Utilisateur",
                        UtilisateurId = u,
                        DateAffectation = DateTime.Now,
                        AffectePar = GetLogin()
                    });

            foreach (var g in desiredGroups)
                if (!existingGroups.ContainsKey(g))
                    _context.PrsAffectations.Add(new PrsAffectation
                    {
                        PrsId = prsId,
                        TypeAffectation = "Groupe",
                        GroupeId = g,
                        DateAffectation = DateTime.Now,
                        AffectePar = GetLogin()
                    });

            if (toDelete.Any() || desiredUsers.Any(u => !existingUsers.ContainsKey(u)) || desiredGroups.Any(g => !existingGroups.ContainsKey(g)))
                await _context.SaveChangesAsync();
        }

        #endregion

        #region Chargements & Validation

        private async Task ChargerLignesAsync()
        {
            try
            {
                var rows = await _context.Lignes
                    .Where(l => l.Activation == true && l.Nom != null && l.Nom != "")
                    .OrderBy(l => l.Nom)
                    .Select(l => new SelectListItem { Value = l.Id.ToString(), Text = l.Nom! })
                    .ToListAsync();
                LigneList = new SelectList(rows, "Value", "Text");
            }
            catch
            {
                LigneList = new SelectList(new List<SelectListItem>(), "Value", "Text");
            }
        }

        private async Task ChargerSourcesAsync()
        {
            UtilisateursList = await _context.Utilisateurs
                .Where(u => !u.DateDeleted.HasValue)
                .OrderBy(u => u.Nom).ThenBy(u => u.Prenom)
                .ToListAsync();

            GroupesList = await _context.GroupesUtilisateurs
                .Include(g => g.Membres)
                .OrderBy(g => g.NomGroupe)
                .ToListAsync();
        }

        private void ExtractTypeEtDetails(string? titre, string? equipement)
        {
            if (!string.IsNullOrWhiteSpace(titre) && titre.Contains(" - "))
            {
                var parts = titre.Split(" - ", 2, StringSplitOptions.TrimEntries);
                PreselectedEventType = parts[0];
                PreselectedEventDetails = parts.Length > 1 ? parts[1] : "";
            }
            else
            {
                PreselectedEventType = equipement ?? "";
                PreselectedEventDetails = titre ?? "";
            }
        }

        private bool ValiderFormulaire(string? type, string? details)
        {
            var valid = true;
            if (string.IsNullOrWhiteSpace(type) || !IsSpecial(type))
            {
                ModelState.AddModelError(string.Empty, "Type d'événement invalide.");
                valid = false;
            }
            if (string.IsNullOrWhiteSpace(details))
            {
                ModelState.AddModelError(string.Empty, "Les détails de l'événement sont requis.");
                valid = false;
            }
            if (Prs.DateFin <= Prs.DateDebut)
            {
                ModelState.AddModelError("Prs.DateFin", "La date de fin doit être postérieure à la date de début.");
                valid = false;
            }
            if (Prs.LigneId <= 0)
            {
                ModelState.AddModelError("Prs.LigneId", "Sélection d'une ligne requise.");
                valid = false;
            }

            ModelState.Remove("Prs.Statut");
            ModelState.Remove("Prs.ReferenceProduit");
            ModelState.Remove("Prs.Quantite");
            ModelState.Remove("Prs.BesoinOperateur");
            ModelState.Remove("Prs.PresenceClient");
            return valid;
        }

        #endregion

        #region Permissions / Utils

        private bool HasRequiredRole()
        {
            try
            {
                var login = GetLogin();
                if (string.IsNullOrEmpty(login)) return false;
                var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login && !u.DateDeleted.HasValue);
                if (user == null) return false;
                var droitsAutorises = new[] { "admin", "cdp", "process", "maintenance", "validateur" };
                return droitsAutorises.Contains((user.Droits ?? "").ToLower());
            }
            catch { return false; }
        }

        private bool EstAdminOuValidateur()
        {
            try
            {
                var login = GetLogin();
                var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login && !u.DateDeleted.HasValue);
                if (user == null) return false;
                var d = (user.Droits ?? "").ToLower();
                return d == "admin" || d == "validateur";
            }
            catch { return false; }
        }

        private bool PeutSupprimer(Models.Prs prs)
            => EstAdminOuValidateur() ||
               (!string.IsNullOrEmpty(prs.CreatedByLogin) &&
                prs.CreatedByLogin.Equals(GetLogin(), StringComparison.OrdinalIgnoreCase));

        private static bool IsSpecial(string? equipement)
            => !string.IsNullOrEmpty(equipement) && TypesSpeciaux.Contains(equipement);

        private string GetLogin()
        {
            var full = User.Identity?.Name;
            if (string.IsNullOrEmpty(full)) return "";
            var parts = full.Split('\\');
            return parts.Length > 1 ? parts[1] : full;
        }

        #endregion
    }
}