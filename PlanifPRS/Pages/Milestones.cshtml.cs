using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlanifPRS.Pages
{
    public class MilestonesModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;

        public MilestonesModel(PlanifPrsDbContext context)
        {
            _context = context;
        }

        // Options PRS pour l'UI (liste des titres disponibles)
        public List<string> PrsOptions { get; set; } = new List<string>();

        // Éléments de checklist à afficher
        public List<ChecklistItemVM> ChecklistItems { get; set; } = new List<ChecklistItemVM>();

        public async Task<IActionResult> OnGetAsync()
        {
            // Vérification des droits d'accès
            var login = User.Identity?.Name?.Split('\\').LastOrDefault();
            var user = await _context.Utilisateurs
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.LoginWindows == login);

            if (user == null)
            {
                TempData["ErrorMessage"] = "⚠️ Utilisateur non trouvé dans le système.";
                return RedirectToPage("/Index");
            }

            var droitsAutorises = new[] { "admin", "cdp", "validateur" };
            var seeAll = droitsAutorises.Contains((user.Droits ?? "").ToLower());

            try
            {
                var today = DateTime.Today;

                // Base query
                var query = from c in _context.PrsChecklists.AsNoTracking()
                            join p in _context.Prs.AsNoTracking().Where(p => p.Statut != "Supprimé") on c.PRSId equals p.Id // EXCLUSION PRS supprimées
                            select new { c, p };

                // Permissions: si non autorisé, ne voir que les éléments assignés (directement ou via groupe)
                if (!seeAll)
                {
                    var myId = user.Id;

                    // Groupes dont l'utilisateur est membre
                    var myGroupIds = await _context.GroupeUtilisateurs
                        .AsNoTracking()
                        .Where(g => g.UtilisateurId == myId)
                        .Select(g => g.GroupeId)
                        .Distinct()
                        .ToListAsync();

                    query = from qp in query
                            where _context.ChecklistAffectations.Any(a =>
                                a.ChecklistId == qp.c.Id &&
                                (
                                    (a.UtilisateurId.HasValue && a.UtilisateurId.Value == myId) ||
                                    (a.GroupeId.HasValue && myGroupIds.Contains(a.GroupeId.Value))
                                ))
                            select qp;
                }

                // Construire la liste des PRS disponibles (après filtre permissions)
                PrsOptions = await query
                    .Select(x => x.p.Titre)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToListAsync();

                // Récupération finale (pas de filtre serveur par PRS: filtre côté client en JS)
                var rows = await query
                    .OrderBy(x => x.p.DateDebut)
                    .ThenBy(x => x.c.Priorite)
                    .ThenBy(x => x.c.DelaiDefautJours)
                    .ThenBy(x => x.c.Categorie)
                    .ThenBy(x => x.c.SousCategorie)
                    .ToListAsync();

                var items = new List<ChecklistItemVM>(rows.Count);
                foreach (var r in rows)
                {
                    int delai = r.c.DelaiDefautJours > 0 ? r.c.DelaiDefautJours : 1;
                    var due = r.c.DateEcheance.HasValue
                        ? r.c.DateEcheance.Value.Date
                        : r.p.DateDebut.Date.AddDays(delai);

                    int daysLeft = (int)Math.Floor((due - today).TotalDays);
                    bool isValidated = r.c.EstCoche;
                    bool isLate = !isValidated && daysLeft < 0;
                    bool isDueSoon = !isValidated && daysLeft >= 0 && daysLeft <= 3; // fenêtre "proche" par défaut

                    // État PRS pour filtrage client: "attente" | "revalider" | "valide" | "autre"
                    string prsState = "autre";
                    if (string.Equals(r.p.Statut ?? "", "En attente", StringComparison.OrdinalIgnoreCase))
                        prsState = "attente";
                    else if (string.Equals(r.p.Statut ?? "", "À re-valider", StringComparison.OrdinalIgnoreCase))
                        prsState = "revalider";
                    else if (string.Equals(r.p.Statut ?? "", "Validé", StringComparison.OrdinalIgnoreCase))
                        prsState = "valide";

                    items.Add(new ChecklistItemVM
                    {
                        Id = r.c.Id,
                        PrsId = r.p.Id,
                        PrsTitre = r.p.Titre,
                        PrsState = prsState,
                        Categorie = r.c.Categorie,
                        SousCategorie = r.c.SousCategorie,
                        Libelle = string.IsNullOrWhiteSpace(r.c.Libelle) ? r.c.Tache : r.c.Libelle,
                        Priorite = r.c.Priorite > 0 ? r.c.Priorite : 3,
                        DelaiJours = delai,
                        DueDate = due,
                        DaysLeft = daysLeft,
                        Source = r.c.DateEcheance.HasValue ? "Fixée" : "Calculée",
                        IsValidated = isValidated,
                        DateValidation = r.c.DateValidation,
                        ValidePar = r.c.ValidePar,
                        IsLate = isLate,
                        IsDueSoon = isDueSoon
                    });
                }

                ChecklistItems = items;
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"❌ Erreur lors du chargement des tâches: {ex.Message}";
                ChecklistItems = new List<ChecklistItemVM>();
            }

            return Page();
        }

        // Valider un élément de checklist
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostValidateChecklistAsync(int checklistId)
        {
            try
            {
                var item = await _context.PrsChecklists.FirstOrDefaultAsync(c => c.Id == checklistId);
                if (item == null)
                {
                    TempData["ErrorMessage"] = "❌ Tâche introuvable.";
                    return RedirectToPage();
                }

                item.EstCoche = true;
                item.DateValidation = DateTime.Now;
                item.ValidePar = User.Identity?.Name?.Split('\\').LastOrDefault();
                // Statut booléen comme demandé
                item.Statut = true;

                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = "✅ Tâche validée.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"❌ Erreur lors de la validation: {ex.Message}";
            }

            return RedirectToPage();
        }

        // Annuler la validation d'un élément de checklist
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostUnvalidateChecklistAsync(int checklistId)
        {
            try
            {
                var item = await _context.PrsChecklists.FirstOrDefaultAsync(c => c.Id == checklistId);
                if (item == null)
                {
                    TempData["ErrorMessage"] = "❌ Tâche introuvable.";
                    return RedirectToPage();
                }

                item.EstCoche = false;
                item.DateValidation = null;
                item.ValidePar = null;
                // Alignement avec booléen
                item.Statut = false;

                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = "✅ Validation annulée.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"❌ Erreur lors de l'annulation: {ex.Message}";
            }

            return RedirectToPage();
        }

        // VM pour l'affichage des éléments de checklist
        public class ChecklistItemVM
        {
            public int Id { get; set; }
            public int PrsId { get; set; }
            public string PrsTitre { get; set; }
            public string PrsState { get; set; } // "attente" | "revalider" | "valide" | "autre" (pour filtrage client)
            public string Categorie { get; set; }
            public string SousCategorie { get; set; }
            public string Libelle { get; set; }
            public int Priorite { get; set; }
            public int DelaiJours { get; set; }
            public DateTime DueDate { get; set; }
            public int DaysLeft { get; set; }
            public string Source { get; set; }
            public bool IsValidated { get; set; }
            public DateTime? DateValidation { get; set; }
            public string ValidePar { get; set; }
            public bool IsLate { get; set; }
            public bool IsDueSoon { get; set; }
        }
    }
}