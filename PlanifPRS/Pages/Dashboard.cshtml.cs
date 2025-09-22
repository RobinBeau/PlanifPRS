using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PlanifPRS.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlanifPRS.Pages
{
    public class DashboardModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;
        private readonly ILogger<DashboardModel> _logger;

        public DashboardModel(PlanifPrsDbContext context, ILogger<DashboardModel> logger)
        {
            _context = context;
            _logger = logger;
        }

        [TempData] public string Flash { get; set; }
        [TempData] public string ErrorMessage { get; set; }

        public string CurrentUserLogin { get; private set; } = "-";
        public string CurrentUserRole { get; private set; } = "-";
        public bool IsManagerView { get; private set; } = false;

        public int DefaultDueSoonDays { get; private set; } = 7;

        public SummaryVM Summary { get; private set; } = new();
        public AdminSummaryVM AdminSummary { get; private set; } = new();
        public List<ChecklistItemVM> OverdueItems { get; private set; } = new();
        public List<ChecklistItemVM> DueSoonItems { get; private set; } = new();
        public List<PrsCardVM> UpcomingPrs { get; private set; } = new();
        public List<ActivityVM> RecentPrs { get; private set; } = new();

        // Alerts: PRS enfant (Finition) commence avant la fin de la PRS parente (CMS)
        public List<ParentChildAlertVM> ParentChildAlerts { get; private set; } = new();

        private int _userId;
        private List<int> _myGroupIds = new();

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                // Contexte utilisateur
                CurrentUserLogin = GetCurrentUserLogin();
                var user = await _context.Utilisateurs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.LoginWindows == CurrentUserLogin);

                if (user == null)
                {
                    ErrorMessage = "⚠️ Utilisateur non trouvé dans le système.";
                    return RedirectToPage("/Index");
                }

                _userId = user.Id;
                CurrentUserRole = string.IsNullOrWhiteSpace(user.Droits) ? "-" : user.Droits;
                IsManagerView = IsManager(user.Droits);

                // Groupes dont je suis membre (via Membres)
                _myGroupIds = await _context.GroupesUtilisateurs
                    .AsNoTracking()
                    .Where(g => g.Actif && g.Membres.Any(m => m.UtilisateurId == _userId))
                    .Select(g => g.Id)
                    .Distinct()
                    .ToListAsync();

                var today = DateTime.Today;

                // Pré-calcul des IDs des éléments et PRS affectés (pour éviter sous-requêtes Any => CTE)
                List<int> myChecklistIds = new();
                List<int> prsIdsAssigned = new();

                if (!IsManagerView)
                {
                    myChecklistIds = await _context.ChecklistAffectations
                        .AsNoTracking()
                        .Where(a =>
                            (a.UtilisateurId.HasValue && a.UtilisateurId.Value == _userId) ||
                            (a.GroupeId.HasValue && _myGroupIds.Contains(a.GroupeId.Value)))
                        .Select(a => a.ChecklistId)
                        .Distinct()
                        .ToListAsync();

                    prsIdsAssigned = await _context.PrsAffectations
                        .AsNoTracking()
                        .Where(a =>
                            (a.UtilisateurId.HasValue && a.UtilisateurId.Value == _userId) ||
                            (a.GroupeId.HasValue && _myGroupIds.Contains(a.GroupeId.Value)))
                        .Select(a => a.PrsId)
                        .Distinct()
                        .ToListAsync();
                }

                // Base checklist (permissions) - filtrage par liste d'IDs
                // EXCLUSION des PRS "Supprimé"
                var baseChecklist = IsManagerView
                    ? (from c in _context.PrsChecklists.AsNoTracking()
                       join p in _context.Prs.AsNoTracking().Where(p => p.Statut != "Supprimé") on c.PRSId equals p.Id
                       select new { c, p })
                    : (from c in _context.PrsChecklists.AsNoTracking()
                       join p in _context.Prs.AsNoTracking().Where(p => p.Statut != "Supprimé") on c.PRSId equals p.Id
                       where myChecklistIds.Contains(c.Id)
                       select new { c, p });

                var rows = await baseChecklist
                    .OrderBy(x => x.p.DateDebut)
                    .ThenBy(x => x.c.Priorite)
                    .ThenBy(x => x.c.DelaiDefautJours)
                    .ThenBy(x => x.c.Categorie)
                    .ThenBy(x => x.c.SousCategorie)
                    .ToListAsync();

                var items = rows.Select(r =>
                {
                    var delai = r.c.DelaiDefautJours > 0 ? r.c.DelaiDefautJours : 1;
                    var due = r.c.DateEcheance.HasValue ? r.c.DateEcheance.Value.Date : r.p.DateDebut.Date.AddDays(delai);
                    var daysLeft = (int)Math.Floor((due - today).TotalDays);
                    return new ChecklistItemVM
                    {
                        Id = r.c.Id,
                        PrsId = r.p.Id,
                        PrsTitre = r.p.Titre,
                        Categorie = r.c.Categorie,
                        SousCategorie = r.c.SousCategorie,
                        Libelle = string.IsNullOrWhiteSpace(r.c.Libelle) ? r.c.Tache : r.c.Libelle,
                        Priorite = r.c.Priorite > 0 ? r.c.Priorite : 3,
                        DelaiJours = delai,
                        DueDate = due,
                        DaysLeft = daysLeft,
                        Source = r.c.DateEcheance.HasValue ? "Fixée" : "Calculée",
                        IsValidated = r.c.EstCoche,
                        DateValidation = r.c.DateValidation,
                        ValidePar = r.c.ValidePar
                    };
                }).ToList();

                // Synthèse perso
                var open = items.Where(i => !i.IsValidated).ToList();
                var late = open.Where(i => i.DueDate < today).ToList();
                var todayList = open.Where(i => i.DaysLeft == 0).ToList();
                var soon = open.Where(i => i.DaysLeft > 0 && i.DaysLeft <= DefaultDueSoonDays).ToList();

                Summary = new SummaryVM
                {
                    TotalOpenItems = open.Count,
                    LateCount = late.Count,
                    TodayCount = todayList.Count,
                    DueSoonCount = soon.Count
                };

                OverdueItems = late
                    .OrderBy(i => i.DueDate)
                    .Take(8)
                    .ToList();

                DueSoonItems = open
                    .Where(i => i.DaysLeft >= 0)
                    .OrderBy(i => i.DaysLeft)
                    .ThenBy(i => i.Priorite)
                    .Take(10)
                    .ToList();

                // PRS à venir (exclure Supprimé)
                var prsQuery = _context.Prs.AsNoTracking()
                    .Where(p => p.DateDebut >= today && p.Statut != "Supprimé");

                if (!IsManagerView)
                {
                    prsQuery = prsQuery.Where(p => prsIdsAssigned.Contains(p.Id));
                }

                UpcomingPrs = await prsQuery
                    .OrderBy(p => p.DateDebut)
                    .ThenBy(p => p.Titre)
                    .Take(8)
                    .Select(p => new PrsCardVM
                    {
                        Id = p.Id,
                        Titre = p.Titre,
                        Statut = string.IsNullOrWhiteSpace(p.Statut) ? "En attente" : p.Statut,
                        DateDebut = p.DateDebut,
                        DateFin = p.DateFin
                    })
                    .ToListAsync();

                // Activité récente PRS (exclure Supprimé)
                var recentQuery = _context.Prs.AsNoTracking()
                    .Where(p => p.Statut != "Supprimé");
                if (!IsManagerView)
                {
                    recentQuery = recentQuery.Where(p => prsIdsAssigned.Contains(p.Id));
                }

                RecentPrs = await recentQuery
                    .OrderByDescending(p => p.DerniereModification)
                    .Take(10)
                    .Select(p => new ActivityVM
                    {
                        Id = p.Id,
                        Titre = p.Titre,
                        Statut = string.IsNullOrWhiteSpace(p.Statut) ? "En attente" : p.Statut,
                        CreatedBy = p.CreatedByLogin,
                        DerniereModification = p.DerniereModification
                    })
                    .ToListAsync();

                // ALERTES: Conflits parent/enfant (enfant "Finition" démarre avant fin parent "CMS")
                // On filtre "Supprimé" et on tolère d'anciens enregistrements avec emojis via Contains("Finition")
                var basePrs = _context.Prs.AsNoTracking().Where(p => p.Statut != "Supprimé");

                var conflictsQuery =
                    from child in basePrs
                    join parent in basePrs on child.PrsParentId equals parent.Id
                    where child.PrsParentId != null
                          && (child.Equipement == "Finition" || child.Equipement.Contains("Finition"))
                          && child.DateDebut < parent.DateFin
                    select new { child, parent };

                if (!IsManagerView)
                {
                    conflictsQuery = conflictsQuery.Where(x =>
                        prsIdsAssigned.Contains(x.child.Id) || prsIdsAssigned.Contains(x.parent.Id));
                }

                ParentChildAlerts = await conflictsQuery
                    .OrderBy(x => x.child.DateDebut)
                    .Take(50)
                    .Select(x => new ParentChildAlertVM
                    {
                        EnfantId = x.child.Id,
                        EnfantTitre = x.child.Titre,
                        EnfantDateDebut = x.child.DateDebut,
                        ParentId = x.parent.Id,
                        ParentTitre = x.parent.Titre,
                        ParentDateFin = x.parent.DateFin
                    })
                    .ToListAsync();

                // Vue manager (exclure Supprimé dans toutes les métriques)
                if (IsManagerView)
                {
                    var allPrs = _context.Prs
                        .AsNoTracking()
                        .Where(p => p.Statut != "Supprimé"); // EXCLUSION

                    var enAttenteCount = await allPrs.CountAsync(p => (p.Statut ?? "") == "En attente");
                    var aReValiderCount = await allPrs.CountAsync(p => (p.Statut ?? "") == "À re-valider");
                    var valideesCount = await allPrs.CountAsync(p => (p.Statut ?? "") == "Validé");

                    var pendingList = await allPrs
                        .Where(p => (p.Statut ?? "") == "En attente" || (p.Statut ?? "") == "À re-valider")
                        .OrderBy(p => p.DateDebut)
                        .ThenBy(p => p.Statut)
                        .Take(6)
                        .Select(p => new AdminPrsVM
                        {
                            Id = p.Id,
                            Titre = p.Titre,
                            Statut = p.Statut,
                            DateDebut = p.DateDebut,
                            DateFin = p.DateFin,
                            CreatedBy = p.CreatedByLogin,
                            DerniereModification = p.DerniereModification
                        })
                        .ToListAsync();

                    AdminSummary = new AdminSummaryVM
                    {
                        TotalPrs = await allPrs.CountAsync(), // déjà sans Supprimé
                        PrsEnAttente = enAttenteCount,
                        PrsAReValider = aReValiderCount,
                        PrsValidees = valideesCount,
                        UsersActifs = await _context.Utilisateurs.AsNoTracking().CountAsync(u => u.DateDeleted == null),
                        GroupesActifs = await _context.GroupesUtilisateurs.AsNoTracking().CountAsync(g => g.Actif),
                        PrsEnAttenteList = pendingList
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DASHBOARD][GET] Erreur chargement");
                ErrorMessage = $"Erreur lors du chargement du tableau de bord: {ex.Message}";
            }

            return Page();
        }

        // Validation depuis le dashboard
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostValidateChecklistAsync(int checklistId)
        {
            try
            {
                var login = GetCurrentUserLogin();
                var user = await _context.Utilisateurs.FirstOrDefaultAsync(u => u.LoginWindows == login);
                if (user == null) { ErrorMessage = "Utilisateur inconnu."; return RedirectToPage(); }

                var isManager = IsManager(user.Droits);

                var item = await _context.PrsChecklists.FirstOrDefaultAsync(c => c.Id == checklistId);
                if (item == null) { ErrorMessage = "Élément introuvable."; return RedirectToPage(); }

                if (!isManager)
                {
                    var myGroupIds = await _context.GroupesUtilisateurs
                        .Where(g => g.Actif && g.Membres.Any(m => m.UtilisateurId == user.Id))
                        .Select(g => g.Id)
                        .Distinct()
                        .ToListAsync();

                    bool authorized = await _context.ChecklistAffectations.AnyAsync(a =>
                        a.ChecklistId == item.Id &&
                        (
                            (a.UtilisateurId.HasValue && a.UtilisateurId.Value == user.Id) ||
                            (a.GroupeId.HasValue && myGroupIds.Contains(a.GroupeId.Value))
                        ));

                    if (!authorized)
                    {
                        ErrorMessage = "Vous n'êtes pas autorisé à valider cet élément.";
                        return RedirectToPage();
                    }
                }

                item.EstCoche = true;
                item.DateValidation = DateTime.Now;
                item.ValidePar = login;
                item.Statut = true;

                await _context.SaveChangesAsync();
                Flash = "Élément validé.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DASHBOARD][POST] Erreur validation {Id}", checklistId);
                ErrorMessage = "Erreur lors de la validation.";
            }
            return RedirectToPage();
        }

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostUnvalidateChecklistAsync(int checklistId)
        {
            try
            {
                var login = GetCurrentUserLogin();
                var user = await _context.Utilisateurs.FirstOrDefaultAsync(u => u.LoginWindows == login);
                if (user == null) { ErrorMessage = "Utilisateur inconnu."; return RedirectToPage(); }

                var isManager = IsManager(user.Droits);

                var item = await _context.PrsChecklists.FirstOrDefaultAsync(c => c.Id == checklistId);
                if (item == null) { ErrorMessage = "Élément introuvable."; return RedirectToPage(); }

                if (!isManager)
                {
                    var myGroupIds = await _context.GroupesUtilisateurs
                        .Where(g => g.Actif && g.Membres.Any(m => m.UtilisateurId == user.Id))
                        .Select(g => g.Id)
                        .Distinct()
                        .ToListAsync();

                    bool authorized = await _context.ChecklistAffectations.AnyAsync(a =>
                        a.ChecklistId == item.Id &&
                        (
                            (a.UtilisateurId.HasValue && a.UtilisateurId.Value == user.Id) ||
                            (a.GroupeId.HasValue && myGroupIds.Contains(a.GroupeId.Value))
                        ));

                    if (!authorized)
                    {
                        ErrorMessage = "Vous n'êtes pas autorisé à annuler cette validation.";
                        return RedirectToPage();
                    }
                }

                item.EstCoche = false;
                item.DateValidation = null;
                item.ValidePar = null;
                item.Statut = false;

                await _context.SaveChangesAsync();
                Flash = "Validation annulée.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DASHBOARD][POST] Erreur annulation {Id}", checklistId);
                ErrorMessage = "Erreur lors de l'annulation de la validation.";
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

        // VMs
        public class SummaryVM
        {
            public int TotalOpenItems { get; set; }
            public int LateCount { get; set; }
            public int TodayCount { get; set; }
            public int DueSoonCount { get; set; }
        }

        public class AdminSummaryVM
        {
            public int TotalPrs { get; set; }
            public int PrsEnAttente { get; set; }
            public int PrsAReValider { get; set; }
            public int PrsValidees { get; set; }
            public int UsersActifs { get; set; }
            public int GroupesActifs { get; set; }
            public List<AdminPrsVM> PrsEnAttenteList { get; set; } = new();
        }

        public class AdminPrsVM
        {
            public int Id { get; set; }
            public string Titre { get; set; }
            public string Statut { get; set; }
            public DateTime DateDebut { get; set; }
            public DateTime DateFin { get; set; }
            public string CreatedBy { get; set; }
            public DateTime DerniereModification { get; set; }
        }

        public class ChecklistItemVM
        {
            public int Id { get; set; }
            public int PrsId { get; set; }
            public string PrsTitre { get; set; }
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
        }

        public class PrsCardVM
        {
            public int Id { get; set; }
            public string Titre { get; set; }
            public string Statut { get; set; }
            public DateTime DateDebut { get; set; }
            public DateTime DateFin { get; set; }
        }

        public class ActivityVM
        {
            public int Id { get; set; }
            public string Titre { get; set; }
            public string Statut { get; set; }
            public string CreatedBy { get; set; }
            public DateTime DerniereModification { get; set; }
        }

        public class ParentChildAlertVM
        {
            public int EnfantId { get; set; }
            public string EnfantTitre { get; set; }
            public DateTime EnfantDateDebut { get; set; }
            public int ParentId { get; set; }
            public string ParentTitre { get; set; }
            public DateTime ParentDateFin { get; set; }
        }
    }
}