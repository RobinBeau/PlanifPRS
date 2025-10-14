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
        private readonly ILogger<MilestonesModel> _logger;

        public MilestonesModel(PlanifPrsDbContext context, ILogger<MilestonesModel> logger)
        {
            _context = context;
            _logger = logger;
        }

        // Infos utilisateur
        public bool IsManagerView { get; private set; } = false;
        public bool IsChefService { get; private set; } = false;
        public string ServiceName { get; private set; } = "";
        public string CurrentUserLogin { get; private set; } = "-";

        // Options PRS pour l'UI
        public List<string> PrsOptions { get; set; } = new List<string>();

        // ✅ 3 LISTES SÉPARÉES
        public List<ChecklistItemVM> AllChecklistItems { get; set; } = new List<ChecklistItemVM>();
        public List<ChecklistItemVM> ServiceChecklistItems { get; set; } = new List<ChecklistItemVM>();
        public List<ChecklistItemVM> MyChecklistItems { get; set; } = new List<ChecklistItemVM>();

        public async Task<IActionResult> OnGetAsync()
        {
            CurrentUserLogin = GetCurrentUserLogin();
            _logger.LogInformation($"[MILESTONES] User: {CurrentUserLogin}");

            var user = await _context.Utilisateurs
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.LoginWindows == CurrentUserLogin);

            if (user == null)
            {
                TempData["ErrorMessage"] = "⚠️ Utilisateur non trouvé dans le système.";
                return RedirectToPage("/Index");
            }

            var userId = user.Id;
            IsManagerView = IsManager(user.Droits);
            IsChefService = user.IsChefService;
            ServiceName = user.ServiceClean;

            _logger.LogInformation($"[MILESTONES] IsManager: {IsManagerView}, IsChefService: {IsChefService}, ServiceName: {ServiceName}");

            try
            {
                var today = DateTime.Today;

                // ✅ 1. VUE ADMIN : TOUTES LES TÂCHES
                if (IsManagerView)
                {
                    _logger.LogInformation("[MILESTONES] Chargement vue ADMIN");
                    AllChecklistItems = await LoadAllChecklistsAsync(today);
                    _logger.LogInformation($"[MILESTONES] Admin: {AllChecklistItems.Count} tâches");
                }

                // ✅ 2. VUE CHEF DE SERVICE : TÂCHES DU SERVICE
                if (IsChefService)
                {
                    _logger.LogInformation("[MILESTONES] Chargement vue CHEF DE SERVICE");
                    ServiceChecklistItems = await LoadServiceChecklistsAsync(user, today);
                    _logger.LogInformation($"[MILESTONES] Service: {ServiceChecklistItems.Count} tâches");
                }

                // ✅ 3. VUE STANDARD : MES TÂCHES ASSIGNÉES
                _logger.LogInformation("[MILESTONES] Chargement vue STANDARD");
                MyChecklistItems = await LoadMyChecklistsAsync(userId, today);
                _logger.LogInformation($"[MILESTONES] Mes tâches: {MyChecklistItems.Count} tâches");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MILESTONES] Erreur chargement");
                TempData["ErrorMessage"] = $"❌ Erreur lors du chargement: {ex.Message}";
            }

            return Page();
        }

        // ✅ CHARGER TOUTES LES TÂCHES (ADMIN) avec LINQ simple
        private async Task<List<ChecklistItemVM>> LoadAllChecklistsAsync(DateTime today)
        {
            try
            {
                // ✅ Charger TOUTES les checklists
                var checklists = await _context.PrsChecklists
                    .AsNoTracking()
                    .ToListAsync();

                _logger.LogInformation($"[MILESTONES] {checklists.Count} checklists chargées");

                var prsIds = checklists.Select(c => c.PRSId).Distinct().ToList();

                // ✅ Charger TOUTES les PRS non supprimées
                var allPrs = await _context.Prs
                    .AsNoTracking()
                    .Where(p => p.Statut != "Supprimé")  // ✅ Pas de Contains()
                    .ToListAsync();

                _logger.LogInformation($"[MILESTONES] {allPrs.Count} PRS chargées");

                // ✅ Filtrer EN MÉMOIRE
                var prsDict = allPrs
                    .Where(p => prsIds.Contains(p.Id))  // ✅ Filtrage en mémoire
                    .ToDictionary(p => p.Id);

                _logger.LogInformation($"[MILESTONES] {prsDict.Count} PRS correspondantes");

                return checklists
                    .Where(c => prsDict.ContainsKey(c.PRSId))
                    .Select(c => CreateChecklistItemVM(c, prsDict[c.PRSId], today))
                    .OrderBy(c => c.DueDate)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MILESTONES] Erreur LoadAllChecklistsAsync");
                return new List<ChecklistItemVM>();
            }
        }

        // ✅ CHARGER LES TÂCHES DU SERVICE (CHEF) - Version avec filtrage mémoire
        private async Task<List<ChecklistItemVM>> LoadServiceChecklistsAsync(Models.Utilisateur chef, DateTime today)
        {
            try
            {
                var monService = chef.Service;
                if (string.IsNullOrWhiteSpace(monService))
                {
                    _logger.LogWarning("[MILESTONES] Service vide");
                    return new List<ChecklistItemVM>();
                }

                // Utilisateurs du service
                var utilisateursService = await _context.Utilisateurs
                    .AsNoTracking()
                    .Where(u => u.Service == monService && u.DateDeleted == null)
                    .ToListAsync();

                if (!utilisateursService.Any())
                {
                    var servicePattern = monService.Contains(" ") ? monService.Split(' ')[0] : monService;
                    utilisateursService = await _context.Utilisateurs
                        .AsNoTracking()
                        .Where(u => u.Service != null && u.Service.StartsWith(servicePattern) && u.DateDeleted == null)
                        .ToListAsync();
                }

                if (!utilisateursService.Any())
                {
                    _logger.LogWarning($"[MILESTONES] Aucun utilisateur trouvé pour le service {monService}");
                    return new List<ChecklistItemVM>();
                }

                var userIds = utilisateursService.Select(u => u.Id).ToList();
                _logger.LogInformation($"[MILESTONES] {userIds.Count} utilisateurs dans le service");

                // ✅ Charger TOUTES les affectations puis filtrer en mémoire
                var affectationsService = await _context.ChecklistAffectations
                    .AsNoTracking()
                    .Where(a => a.UtilisateurId != null)
                    .Select(a => new { a.UtilisateurId, a.ChecklistId })
                    .ToListAsync();

                var tachesServiceIds = affectationsService
                    .Where(a => userIds.Contains(a.UtilisateurId.Value))
                    .Select(a => a.ChecklistId)
                    .Distinct()
                    .ToList();

                if (!tachesServiceIds.Any())
                {
                    _logger.LogWarning("[MILESTONES] Aucune tâche assignée au service");
                    return new List<ChecklistItemVM>();
                }

                _logger.LogInformation($"[MILESTONES] {tachesServiceIds.Count} tâches pour le service");

                // ✅ CORRECTION : Charger TOUTES les checklists puis filtrer en mémoire
                var allChecklists = await _context.PrsChecklists
                    .AsNoTracking()
                    .ToListAsync();

                _logger.LogInformation($"[MILESTONES] {allChecklists.Count} checklists totales chargées");

                // ✅ Filtrer EN MÉMOIRE
                var checklists = allChecklists
                    .Where(c => tachesServiceIds.Contains(c.Id))
                    .ToList();

                _logger.LogInformation($"[MILESTONES] {checklists.Count} checklists filtrées pour le service");

                // PRS
                var prsIds = checklists.Select(c => c.PRSId).Distinct().ToList();
                // ✅ Charger TOUTES les PRS non supprimées
                var allPrs = await _context.Prs
                    .AsNoTracking()
                    .Where(p => p.Statut != "Supprimé")  // ✅ Pas de Contains()
                    .ToListAsync();

                _logger.LogInformation($"[MILESTONES] {allPrs.Count} PRS non supprimées chargées");

                // ✅ Filtrer EN MÉMOIRE
                var prsDict = allPrs
                    .Where(p => prsIds.Contains(p.Id))  // ✅ Filtrage en mémoire
                    .ToDictionary(p => p.Id);

                _logger.LogInformation($"[MILESTONES] {prsDict.Count} PRS correspondantes");

               

                return checklists
                    .Where(c => prsDict.ContainsKey(c.PRSId))
                    .Select(c => CreateChecklistItemVM(c, prsDict[c.PRSId], today))
                    .OrderBy(c => c.DueDate)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MILESTONES] Erreur LoadServiceChecklistsAsync");
                return new List<ChecklistItemVM>();
            }
        }

        private async Task<List<ChecklistItemVM>> LoadMyChecklistsAsync(int userId, DateTime today)
        {
            try
            {
                // ✅ Charger TOUTES les affectations
                var allAffectations = await _context.ChecklistAffectations
                    .AsNoTracking()
                    .Select(a => new { a.ChecklistId, a.UtilisateurId, a.GroupeId })
                    .ToListAsync();

                _logger.LogInformation($"[MILESTONES] {allAffectations.Count} affectations totales chargées");

                // ✅ Filtrer EN MÉMOIRE : Affectations directes
                var myChecklistIds = allAffectations
                    .Where(a => a.UtilisateurId == userId)
                    .Select(a => a.ChecklistId)
                    .ToList();

                _logger.LogInformation($"[MILESTONES] {myChecklistIds.Count} affectations directes trouvées");

                // Affectations par groupes
                var myGroupIds = await _context.GroupeUtilisateurs
                    .AsNoTracking()
                    .Where(g => g.UtilisateurId == userId)
                    .Select(g => g.GroupeId)
                    .ToListAsync();

                _logger.LogInformation($"[MILESTONES] {myGroupIds.Count} groupes trouvés");

                if (myGroupIds.Any())
                {
                    var groupChecklistIds = allAffectations
                        .Where(a => a.GroupeId.HasValue && myGroupIds.Contains(a.GroupeId.Value))
                        .Select(a => a.ChecklistId)
                        .ToList();

                    _logger.LogInformation($"[MILESTONES] {groupChecklistIds.Count} affectations par groupe trouvées");

                    myChecklistIds = myChecklistIds.Concat(groupChecklistIds).Distinct().ToList();
                }

                if (!myChecklistIds.Any())
                {
                    _logger.LogInformation("[MILESTONES] Aucune tâche assignée");
                    return new List<ChecklistItemVM>();
                }

                _logger.LogInformation($"[MILESTONES] {myChecklistIds.Count} tâches assignées (total)");

                // ✅ Charger TOUTES les checklists
                var allChecklists = await _context.PrsChecklists
                    .AsNoTracking()
                    .ToListAsync();

                _logger.LogInformation($"[MILESTONES] {allChecklists.Count} checklists totales chargées");

                // ✅ Filtrer EN MÉMOIRE
                var checklists = allChecklists
                    .Where(c => myChecklistIds.Contains(c.Id))
                    .ToList();

                _logger.LogInformation($"[MILESTONES] {checklists.Count} checklists filtrées");

                if (!checklists.Any())
                {
                    _logger.LogWarning("[MILESTONES] Aucune checklist trouvée après filtrage");
                    return new List<ChecklistItemVM>();
                }

                var prsIds = checklists.Select(c => c.PRSId).Distinct().ToList();

                // ✅ Charger TOUTES les PRS non supprimées
                var allPrs = await _context.Prs
                    .AsNoTracking()
                    .Where(p => p.Statut != "Supprimé")  // ✅ Pas de Contains()
                    .ToListAsync();

                _logger.LogInformation($"[MILESTONES] {allPrs.Count} PRS non supprimées chargées");

                // ✅ Filtrer EN MÉMOIRE
                var prsDict = allPrs
                    .Where(p => prsIds.Contains(p.Id))
                    .ToDictionary(p => p.Id);

                _logger.LogInformation($"[MILESTONES] {prsDict.Count} PRS correspondantes");

                var result = checklists
                    .Where(c => prsDict.ContainsKey(c.PRSId))
                    .Select(c => CreateChecklistItemVM(c, prsDict[c.PRSId], today))
                    .OrderBy(c => c.DueDate)
                    .ToList();

                _logger.LogInformation($"[MILESTONES] {result.Count} tâches retournées");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MILESTONES] Erreur LoadMyChecklistsAsync");
                return new List<ChecklistItemVM>();
            }
        }


        // ✅ CRÉER UN ITEM VM
        private ChecklistItemVM CreateChecklistItemVM(Models.PrsChecklist c, Models.Prs p, DateTime today)
        {
            int delai = c.DelaiDefautJours > 0 ? c.DelaiDefautJours : 1;
            var due = c.DateEcheance.HasValue ? c.DateEcheance.Value.Date : p.DateDebut.Date.AddDays(delai);
            int daysLeft = (int)Math.Floor((due - today).TotalDays);
            bool isValidated = c.EstCoche;
            bool isLate = !isValidated && daysLeft < 0;
            bool isDueSoon = !isValidated && daysLeft >= 0 && daysLeft <= 7;

            string prsState = "autre";
            if (string.Equals(p.Statut ?? "", "En attente", StringComparison.OrdinalIgnoreCase))
                prsState = "attente";
            else if (string.Equals(p.Statut ?? "", "À re-valider", StringComparison.OrdinalIgnoreCase))
                prsState = "revalider";
            else if (string.Equals(p.Statut ?? "", "Validé", StringComparison.OrdinalIgnoreCase))
                prsState = "valide";

            return new ChecklistItemVM
            {
                Id = c.Id,
                PrsId = p.Id,
                PrsTitre = p.Titre,
                PrsState = prsState,
                Categorie = c.Categorie,
                SousCategorie = c.SousCategorie,
                Libelle = string.IsNullOrWhiteSpace(c.Libelle) ? c.Tache : c.Libelle,
                Priorite = c.Priorite > 0 ? c.Priorite : 3,
                DelaiJours = delai,
                DueDate = due,
                DaysLeft = daysLeft,
                Source = c.DateEcheance.HasValue ? "Fixée" : "Calculée",
                IsValidated = isValidated,
                DateValidation = c.DateValidation,
                ValidePar = c.ValidePar,
                IsLate = isLate,
                IsDueSoon = isDueSoon
            };
        }

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
                item.ValidePar = GetCurrentUserLogin();
                item.Statut = true;

                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = "✅ Tâche validée.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MILESTONES] Erreur validation");
                TempData["ErrorMessage"] = $"❌ Erreur: {ex.Message}";
            }

            return RedirectToPage();
        }

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
                item.Statut = false;

                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = "✅ Validation annulée.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MILESTONES] Erreur annulation");
                TempData["ErrorMessage"] = $"❌ Erreur: {ex.Message}";
            }

            return RedirectToPage();
        }

        private string GetCurrentUserLogin()
        {
            var fullLogin = User?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(fullLogin)) return "Utilisateur inconnu";
            var parts = fullLogin.Split('\\');
            return parts.Length > 1 ? parts[1] : fullLogin;
        }

        private bool IsManager(string droits)
        {
            var d = (droits ?? "").ToLower();
            return d == "admin" || d == "cdp" || d == "validateur";
        }

        public class ChecklistItemVM
        {
            public int Id { get; set; }
            public int PrsId { get; set; }
            public string PrsTitre { get; set; }
            public string PrsState { get; set; }
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