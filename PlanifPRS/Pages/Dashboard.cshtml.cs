using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Infrastructure.Graph;
using System.Text.Json;
using IOFile = System.IO.File;

namespace PlanifPRS.Pages
{
    public class DashboardModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;
        private readonly ILogger<DashboardModel> _logger;
        private readonly IWebHostEnvironment _env;

        public DashboardModel(PlanifPrsDbContext context,
                              ILogger<DashboardModel> logger,
                              IWebHostEnvironment env)
        {
            _context = context;
            _logger = logger;
            _env = env;
        }

        [TempData] public string Flash { get; set; }
        [TempData] public string ErrorMessage { get; set; }
        public bool IsManagerView { get; private set; } = false;
        public string CurrentUserLogin { get; private set; } = "-";
        public string CurrentUserRole { get; private set; } = "-";

        public int DefaultDueSoonDays { get; private set; } = 7;

        public SummaryVM Summary { get; private set; } = new();
        public AdminSummaryVM AdminSummary { get; private set; } = new();
        public List<ChecklistItemVM> OverdueItems { get; private set; } = new();
        public List<ChecklistItemVM> DueSoonItems { get; private set; } = new();
        public List<PrsCardVM> UpcomingPrs { get; private set; } = new();
        public List<ActivityVM> RecentPrs { get; private set; } = new();
        public List<ParentChildAlertVM> ParentChildAlerts { get; private set; } = new();
        public List<PrsAbsenceAlertVM> PrsAbsenceAlerts { get; private set; } = new();

        private int _userId;
        private List<int> _myGroupIds = new();

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                CurrentUserLogin = GetCurrentUserLogin();
                _logger.LogInformation($"========== DASHBOARD START - User: {CurrentUserLogin} ==========");

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

                _logger.LogInformation($"UserId: {_userId}, Role: {CurrentUserRole}, IsManager: {IsManagerView}");

                // Récupération des groupes de l'utilisateur
                var myGroups = await _context.GroupeUtilisateurs
                    .AsNoTracking()
                    .Where(m => m.UtilisateurId == _userId)
                    .Select(m => m.GroupeId)
                    .Distinct()
                    .ToListAsync();

                if (myGroups.Any())
                {
                    _myGroupIds = await _context.GroupesUtilisateurs
                        .AsNoTracking()
                        .Where(g => g.Actif && myGroups.Contains(g.Id))
                        .Select(g => g.Id)
                        .Distinct()
                        .ToListAsync();
                }
                else
                {
                    _myGroupIds = new List<int>();
                }

                _logger.LogInformation($"User groups: {_myGroupIds.Count}");

                var today = DateTime.Today;

                List<int> myChecklistIds = new();
                List<int> prsIdsAssigned = new();

                if (!IsManagerView)
                {
                    _logger.LogInformation("Loading user assignments (non-manager)...");

                    // Affectations de checklists par utilisateur
                    var checklistsByUser = await _context.ChecklistAffectations
                        .AsNoTracking()
                        .Where(a => a.UtilisateurId.HasValue && a.UtilisateurId.Value == _userId)
                        .Select(a => a.ChecklistId)
                        .ToListAsync();

                    _logger.LogInformation($"Checklists by user: {checklistsByUser.Count}");

                    // Affectations de checklists par groupe
                    List<int> checklistsByGroup = new();
                    if (_myGroupIds.Any())
                    {
                        checklistsByGroup = await _context.ChecklistAffectations
                            .AsNoTracking()
                            .Where(a => a.GroupeId.HasValue && _myGroupIds.Contains(a.GroupeId.Value))
                            .Select(a => a.ChecklistId)
                            .ToListAsync();

                        _logger.LogInformation($"Checklists by group: {checklistsByGroup.Count}");
                    }

                    myChecklistIds = checklistsByUser
                        .Concat(checklistsByGroup)
                        .Distinct()
                        .ToList();

                    _logger.LogInformation($"Total myChecklistIds: {myChecklistIds.Count}");

                    // Affectations PRS par utilisateur
                    var prsByUser = await _context.PrsAffectations
                        .AsNoTracking()
                        .Where(a => a.UtilisateurId.HasValue && a.UtilisateurId.Value == _userId)
                        .Select(a => a.PrsId)
                        .ToListAsync();

                    // Affectations PRS par groupe
                    List<int> prsByGroup = new();
                    if (_myGroupIds.Any())
                    {
                        prsByGroup = await _context.PrsAffectations
                            .AsNoTracking()
                            .Where(a => a.GroupeId.HasValue && _myGroupIds.Contains(a.GroupeId.Value))
                            .Select(a => a.PrsId)
                            .ToListAsync();
                    }

                    prsIdsAssigned = prsByUser
                        .Concat(prsByGroup)
                        .Distinct()
                        .ToList();

                    _logger.LogInformation($"Total prsIdsAssigned: {prsIdsAssigned.Count}");
                }

                // ============ CHECKLISTS ============
                _logger.LogInformation("Loading checklists...");
                List<ChecklistItemVM> items;

                if (IsManagerView)
                {
                    _logger.LogInformation("Loading checklists (admin)...");

                    // Pour les admins : tout récupérer avec SQL brut pour éviter l'erreur "WITH"
                    var validPrsIds = await _context.Prs.AsNoTracking()
                        .Where(p => p.Statut != "Supprimé")
                        .Select(p => p.Id)
                        .ToListAsync();

                    _logger.LogInformation($"Valid PRS IDs (admin): {validPrsIds.Count}");

                    if (validPrsIds.Any())
                    {
                        // Utiliser SQL brut pour éviter l'erreur "WITH"
                        var validPrsIdsParam = string.Join(",", validPrsIds);
                        var checklists = await _context.PrsChecklists
                            .FromSqlRaw($@"
                                SELECT * 
                                FROM [dbo].[PRS_Checklist] 
                                WHERE [PRSId] IN ({validPrsIdsParam})
                            ")
                            .AsNoTracking()
                            .ToListAsync();

                        _logger.LogInformation($"Checklists loaded (admin): {checklists.Count}");

                        // Charger les PRS avec SQL brut aussi
                        var prsDict = await _context.Prs
                            .FromSqlRaw($@"
                                SELECT * 
                                FROM [dbo].[PRS] 
                                WHERE [Id] IN ({validPrsIdsParam})
                            ")
                            .AsNoTracking()
                            .ToDictionaryAsync(p => p.Id);

                        _logger.LogInformation($"PRS dictionary loaded (admin): {prsDict.Count}");

                        var rows = checklists
                            .Where(c => prsDict.ContainsKey(c.PRSId))
                            .Select(c => new { c, p = prsDict[c.PRSId] })
                            .OrderBy(x => x.p.DateDebut)
                            .ThenBy(x => x.c.Priorite)
                            .ThenBy(x => x.c.DelaiDefautJours)
                            .ThenBy(x => x.c.Categorie)
                            .ThenBy(x => x.c.SousCategorie)
                            .ToList();

                        items = rows.Select(r =>
                        {
                            var delai = r.c.DelaiDefautJours > 0 ? r.c.DelaiDefautJours : 1;
                            var due = r.c.DateEcheance.HasValue ? r.c.DateEcheance.Value.Date : r.p.DateDebut.Date.AddDays(delai);
                            var daysLeft = (int)Math.Floor((due - DateTime.Today).TotalDays);
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

                        _logger.LogInformation($"ChecklistItemVM created (admin): {items.Count}");
                    }
                    else
                    {
                        items = new List<ChecklistItemVM>();
                    }
                }
                else
                {
                    // NON-MANAGER - Utilisation du SQL brut pour éviter l'erreur "WITH"
                    if (myChecklistIds.Any())
                    {
                        _logger.LogInformation($"Loading user checklists (non-manager): {myChecklistIds.Count}");

                        // ÉTAPE 1 : Charger les checklists avec SQL brut
                        var checklistIdsParam = string.Join(",", myChecklistIds);
                        var checklists = await _context.PrsChecklists
                            .FromSqlRaw($@"
                                SELECT * 
                                FROM [dbo].[PRS_Checklist] 
                                WHERE [Id] IN ({checklistIdsParam})
                            ")
                            .AsNoTracking()
                            .ToListAsync();

                        _logger.LogInformation($"Checklists loaded (non-manager): {checklists.Count}");

                        if (checklists.Any())
                        {
                            // ÉTAPE 2 : Récupérer les IDs des PRS
                            var prsIdsFromChecklists = checklists.Select(c => c.PRSId).Distinct().ToList();

                            // ÉTAPE 3 : Charger les PRS avec SQL brut
                            var prsIdsParam = string.Join(",", prsIdsFromChecklists);
                            var prsList = await _context.Prs
                                .FromSqlRaw($@"
                                    SELECT * 
                                    FROM [dbo].[PRS] 
                                    WHERE [Id] IN ({prsIdsParam}) 
                                    AND [Statut] != 'Supprimé'
                                ")
                                .AsNoTracking()
                                .ToListAsync();

                            var prsDict = prsList.ToDictionary(p => p.Id);

                            // ÉTAPE 4 : Combiner en mémoire
                            var rows = checklists
                                .Where(c => prsDict.ContainsKey(c.PRSId))
                                .Select(c => new { c, p = prsDict[c.PRSId] })
                                .OrderBy(x => x.p.DateDebut)
                                .ThenBy(x => x.c.Priorite)
                                .ThenBy(x => x.c.DelaiDefautJours)
                                .ThenBy(x => x.c.Categorie)
                                .ThenBy(x => x.c.SousCategorie)
                                .ToList();

                            items = rows.Select(r =>
                            {
                                var delai = r.c.DelaiDefautJours > 0 ? r.c.DelaiDefautJours : 1;
                                var due = r.c.DateEcheance.HasValue ? r.c.DateEcheance.Value.Date : r.p.DateDebut.Date.AddDays(delai);
                                var daysLeft = (int)Math.Floor((due - DateTime.Today).TotalDays);
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

                            _logger.LogInformation($"ChecklistItemVM created (non-manager): {items.Count}");
                        }
                        else
                        {
                            _logger.LogWarning("No checklists found for user's IDs");
                            items = new List<ChecklistItemVM>();
                        }
                    }
                    else
                    {
                        _logger.LogInformation("No checklist assignments - empty dashboard");
                        items = new List<ChecklistItemVM>();
                    }
                }

                var open = items.Where(i => !i.IsValidated).ToList();
                var late = open.Where(i => i.DueDate < DateTime.Today).ToList();
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

                // ============ PRS À VENIR ============
                _logger.LogInformation("Loading upcoming PRS...");

                if (IsManagerView || prsIdsAssigned.Any())
                {
                    var allUpcomingPrs = await _context.Prs.AsNoTracking()
                        .Where(p => p.DateDebut >= DateTime.Today && p.Statut != "Supprimé")
                        .OrderBy(p => p.DateDebut)
                        .ThenBy(p => p.Titre)
                        .ToListAsync();

                    if (IsManagerView)
                    {
                        UpcomingPrs = allUpcomingPrs
                            .Take(12)
                            .Select(p => new PrsCardVM
                            {
                                Id = p.Id,
                                Titre = p.Titre,
                                Statut = string.IsNullOrWhiteSpace(p.Statut) ? "En attente" : p.Statut,
                                DateDebut = p.DateDebut,
                                DateFin = p.DateFin
                            })
                            .ToList();
                    }
                    else
                    {
                        UpcomingPrs = allUpcomingPrs
                            .Where(p => prsIdsAssigned.Contains(p.Id))
                            .Take(12)
                            .Select(p => new PrsCardVM
                            {
                                Id = p.Id,
                                Titre = p.Titre,
                                Statut = string.IsNullOrWhiteSpace(p.Statut) ? "En attente" : p.Statut,
                                DateDebut = p.DateDebut,
                                DateFin = p.DateFin
                            })
                            .ToList();
                    }

                    _logger.LogInformation($"Upcoming PRS loaded: {UpcomingPrs.Count}");
                }
                else
                {
                    UpcomingPrs = new List<PrsCardVM>();
                }

                // ============ ACTIVITÉ RÉCENTE ============
                if (IsManagerView)
                {
                    _logger.LogInformation("Loading recent activity (admin)...");

                    var allRecentPrs = await _context.Prs.AsNoTracking()
                        .Where(p => p.Statut != "Supprimé")
                        .OrderByDescending(p => p.DerniereModification)
                        .Take(10)
                        .ToListAsync();

                    RecentPrs = allRecentPrs
                        .Select(p => new ActivityVM
                        {
                            Id = p.Id,
                            Titre = p.Titre,
                            Statut = string.IsNullOrWhiteSpace(p.Statut) ? "En attente" : p.Statut,
                            CreatedBy = p.CreatedByLogin,
                            DerniereModification = p.DerniereModification
                        })
                        .ToList();

                    _logger.LogInformation($"Recent activity loaded: {RecentPrs.Count}");
                }
                else
                {
                    // Ne pas charger pour les non-admins
                    RecentPrs = new List<ActivityVM>();
                }

                // ============ ALERTES PARENT/ENFANT ============
                _logger.LogInformation("Loading parent/child alerts...");

                var allChildPrs = await _context.Prs.AsNoTracking()
                    .Where(p => p.PrsParentId != null &&
                               p.Statut != "Supprimé" &&
                               (p.Equipement == "Finition" || p.Equipement.Contains("Finition")))
                    .ToListAsync();

                _logger.LogInformation($"Child PRS loaded: {allChildPrs.Count}");

                var parentIds = allChildPrs.Select(c => c.PrsParentId.Value).Distinct().ToList();

                var parentPrsDict = new Dictionary<int, Models.Prs>();
                if (parentIds.Any())
                {
                    // Utiliser SQL brut pour éviter l'erreur "WITH"
                    var parentIdsParam = string.Join(",", parentIds);
                    var parentPrsList = await _context.Prs
                        .FromSqlRaw($@"
                            SELECT * 
                            FROM [dbo].[PRS] 
                            WHERE [Id] IN ({parentIdsParam}) 
                            AND [Statut] != 'Supprimé'
                        ")
                        .AsNoTracking()
                        .ToListAsync();

                    parentPrsDict = parentPrsList.ToDictionary(p => p.Id);

                    _logger.LogInformation($"Parent PRS loaded: {parentPrsDict.Count}");
                }

                var conflicts = allChildPrs
                    .Where(child => parentPrsDict.ContainsKey(child.PrsParentId.Value))
                    .Select(child => new
                    {
                        child,
                        parent = parentPrsDict[child.PrsParentId.Value]
                    })
                    .Where(x => x.child.DateDebut < x.parent.DateFin)
                    .ToList();

                if (IsManagerView)
                {
                    ParentChildAlerts = conflicts
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
                        .ToList();
                }
                else if (prsIdsAssigned.Any())
                {
                    ParentChildAlerts = conflicts
                        .Where(x => prsIdsAssigned.Contains(x.child.Id) || prsIdsAssigned.Contains(x.parent.Id))
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
                        .ToList();
                }
                else
                {
                    ParentChildAlerts = new List<ParentChildAlertVM>();
                }

                _logger.LogInformation($"Parent/child alerts loaded: {ParentChildAlerts.Count}");

                // ============ VUE GLOBALE (ADMIN) ============
                if (IsManagerView)
                {
                    _logger.LogInformation("Loading admin summary...");

                    var allPrsForStats = await _context.Prs.AsNoTracking()
                        .Where(p => p.Statut != "Supprimé")
                        .ToListAsync();

                    var enAttenteCount = allPrsForStats.Count(p => p.Statut == "En attente");
                    var aReValiderCount = allPrsForStats.Count(p => p.Statut == "À re-valider");
                    var valideesCount = allPrsForStats.Count(p => p.Statut == "Validé");

                    var pendingList = allPrsForStats
                        .Where(p => p.Statut == "En attente" || p.Statut == "À re-valider")
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
                        .ToList();

                    AdminSummary = new AdminSummaryVM
                    {
                        TotalPrs = allPrsForStats.Count,
                        PrsEnAttente = enAttenteCount,
                        PrsAReValider = aReValiderCount,
                        PrsValidees = valideesCount,
                        UsersActifs = await _context.Utilisateurs.AsNoTracking()
                            .CountAsync(u => u.DateDeleted == null),
                        GroupesActifs = await _context.GroupesUtilisateurs.AsNoTracking()
                            .CountAsync(g => g.Actif),
                        PrsEnAttenteList = pendingList
                    };

                    _logger.LogInformation("Admin summary loaded successfully");
                }

                // Alertes d'absence
                _logger.LogInformation("Loading absence alerts...");
                await BuildAbsenceAlertsFromJsonAsync(DateTime.Today, IsManagerView ? null : prsIdsAssigned);
                _logger.LogInformation($"Absence alerts loaded: {PrsAbsenceAlerts.Count}");

                _logger.LogInformation("========== DASHBOARD END ==========");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DASHBOARD][GET] Erreur chargement");
                ErrorMessage = $"Erreur lors du chargement du tableau de bord: {ex.Message}";
            }

            return Page();
        }

        private async Task BuildAbsenceAlertsFromJsonAsync(DateTime today, List<int>? limitedPrsIds)
        {
            const bool DEBUG_ABSENCES = false;

            try
            {
                var aggregates = await LoadLatestAbsenceSnapshotAsync();
                if (aggregates.Count == 0)
                {
                    if (DEBUG_ABSENCES) _logger.LogInformation("[ABS] Pas de snapshot d'absences.");
                    return;
                }

                const int HORIZON_DAYS = 30;
                var horizon = today.AddDays(HORIZON_DAYS);

                var prsQuery = _context.Prs.AsNoTracking()
                    .Where(p => p.Statut != "Supprimé" &&
                                p.DateFin >= today &&
                                p.DateDebut <= horizon);

                if (limitedPrsIds != null && limitedPrsIds.Any())
                    prsQuery = prsQuery.Where(p => limitedPrsIds.Contains(p.Id));

                var prsList = await prsQuery
                    .Select(p => new { p.Id, p.Titre, p.DateDebut, p.DateFin })
                    .ToListAsync();

                if (prsList.Count == 0)
                {
                    if (DEBUG_ABSENCES) _logger.LogInformation("[ABS] Aucune PRS à couvrir dans la fenêtre.");
                    return;
                }

                var prsIds = prsList.Select(p => p.Id).ToList();

                var direct = await _context.PrsAffectations.AsNoTracking()
                    .Where(a => prsIds.Contains(a.PrsId) &&
                                a.UtilisateurId.HasValue &&
                                a.TypeAffectation == "Utilisateur")
                    .Select(a => new { a.PrsId, UserId = a.UtilisateurId!.Value })
                    .ToListAsync();

                var groupLinks = await _context.PrsAffectations.AsNoTracking()
                    .Where(a => prsIds.Contains(a.PrsId) &&
                                a.GroupeId.HasValue &&
                                a.TypeAffectation == "Groupe")
                    .Select(a => new { a.PrsId, a.GroupeId })
                    .ToListAsync();

                var expanded = new List<(int PrsId, int UserId)>();
                if (groupLinks.Any())
                {
                    var groupIds = groupLinks.Select(g => g.GroupeId!.Value).Distinct().ToList();
                    var members = await _context.GroupeUtilisateurs
                        .AsNoTracking()
                        .Where(m => groupIds.Contains(m.GroupeId))
                        .Select(m => new { m.GroupeId, m.UtilisateurId })
                        .ToListAsync();

                    var map = members
                        .GroupBy(m => m.GroupeId)
                        .ToDictionary(g => g.Key, g => g.Select(x => x.UtilisateurId).Distinct().ToList());

                    foreach (var gl in groupLinks)
                    {
                        if (gl.GroupeId.HasValue && map.TryGetValue(gl.GroupeId.Value, out var users))
                            foreach (var u in users)
                                expanded.Add((gl.PrsId, u));
                    }
                }

                var allPairs = direct
                    .Select(d => (d.PrsId, d.UserId))
                    .Concat(expanded)
                    .Distinct()
                    .ToList();

                if (allPairs.Count == 0)
                {
                    if (DEBUG_ABSENCES) _logger.LogInformation("[ABS] Aucune affectation utilisateur trouvée.");
                    return;
                }

                var userIds = allPairs.Select(p => p.UserId).Distinct().ToList();

                var utilisateurs = await _context.Utilisateurs.AsNoTracking()
                    .Where(u => userIds.Contains(u.Id) && u.DateDeleted == null)
                    .Select(u => new { u.Id, u.Mail, u.LoginWindows })
                    .ToListAsync();

                if (utilisateurs.Count == 0)
                {
                    if (DEBUG_ABSENCES) _logger.LogInformation("[ABS] Liste userIds vide dans la base.");
                    return;
                }

                string FallbackMail(string mail, string login)
                    => string.IsNullOrWhiteSpace(mail)
                        ? (!string.IsNullOrWhiteSpace(login) ? login.Trim().ToLowerInvariant() + "@local" : null)
                        : mail.Trim();

                var mailByUserId = utilisateurs.ToDictionary(
                    u => u.Id,
                    u => FallbackMail(u.Mail ?? "", u.LoginWindows ?? "") ?? $"u{u.Id}@local");

                var labelByUserId = utilisateurs.ToDictionary(
                    u => u.Id,
                    u => string.IsNullOrWhiteSpace(u.LoginWindows) ? (u.Mail ?? $"User#{u.Id}") : u.LoginWindows);

                var eventsByEmail = aggregates
                    .SelectMany(a => a.Events.Select(ev => new
                    {
                        Email = (a.Email ?? "").Trim().ToLowerInvariant(),
                        ev
                    }))
                    .GroupBy(x => x.Email)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.ev).ToList());

                if (DEBUG_ABSENCES)
                    _logger.LogInformation("[ABS DEBUG] Agg={Agg} PRS={PRS} Direct={Dir} GroupLinks={Grp} Expanded={Exp} Users={Usr} EventsIdx={Evt}",
                        aggregates.Count, prsList.Count, direct.Count, groupLinks.Count, expanded.Count, utilisateurs.Count, eventsByEmail.Count);

                var alerts = new List<PrsAbsenceAlertVM>();

                foreach (var prs in prsList)
                {
                    var participants = allPairs
                        .Where(p => p.PrsId == prs.Id)
                        .Select(p => p.UserId)
                        .Distinct()
                        .ToList();

                    if (participants.Count == 0) continue;

                    var absents = new List<string>();

                    foreach (var uid in participants)
                    {
                        if (!mailByUserId.TryGetValue(uid, out var emailRaw) || string.IsNullOrWhiteSpace(emailRaw))
                            continue;

                        var key = emailRaw.Trim().ToLowerInvariant();
                        if (!eventsByEmail.TryGetValue(key, out var evts) || evts.Count == 0)
                            continue;

                        bool overlap = evts.Any(e =>
                            e.IsOutOfOffice &&
                            e.End >= prs.DateDebut &&
                            e.Start <= prs.DateFin);

                        if (overlap)
                        {
                            absents.Add(labelByUserId[uid]);
                            if (DEBUG_ABSENCES)
                            {
                                var firstEvt = evts.First(e => e.IsOutOfOffice &&
                                                               e.End >= prs.DateDebut &&
                                                               e.Start <= prs.DateFin);
                                _logger.LogInformation("[ABS DEBUG] PRS {Id} absent {User} (Evt {S:o}->{E:o})",
                                    prs.Id, labelByUserId[uid], firstEvt.Start, firstEvt.End);
                            }
                        }
                    }

                    if (absents.Count > 0)
                    {
                        alerts.Add(new PrsAbsenceAlertVM
                        {
                            PrsId = prs.Id,
                            Titre = prs.Titre,
                            DateDebut = prs.DateDebut,
                            DateFin = prs.DateFin,
                            AbsentLogins = absents.OrderBy(a => a).ToList()
                        });
                    }
                }

                PrsAbsenceAlerts = alerts
                    .OrderBy(a => a.DateDebut)
                    .Take(60)
                    .ToList();

                if (DEBUG_ABSENCES)
                    _logger.LogInformation("[ABS RESULT] Alerts={Count}", PrsAbsenceAlerts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DASHBOARD] Erreur BuildAbsenceAlertsFromJsonAsync");
            }
        }

        private async Task<List<UserAbsenceAggregate>> LoadLatestAbsenceSnapshotAsync()
        {
            var list = new List<UserAbsenceAggregate>();
            try
            {
                var dir = Path.Combine(_env.ContentRootPath, "Data", "Absences");
                if (!Directory.Exists(dir))
                {
                    _logger.LogInformation("[ABSENCES] Dossier inexistant: {Dir}", dir);
                    return list;
                }

                var files = Directory.GetFiles(dir, "absences-*.json", SearchOption.TopDirectoryOnly);
                if (files.Length == 0)
                {
                    _logger.LogInformation("[ABSENCES] Aucun fichier absences-*.json");
                    return list;
                }

                string? latest = null;
                DateTime maxDate = DateTime.MinValue;

                foreach (var f in files)
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    var parts = name.Split('-', 2);
                    if (parts.Length == 2 && DateTime.TryParseExact(parts[1], "yyyyMMdd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None,
                            out var d))
                    {
                        if (d > maxDate) { maxDate = d; latest = f; }
                    }
                }

                latest ??= files
                    .OrderByDescending(f => IOFile.GetLastWriteTimeUtc(f))
                    .First();

                var json = await IOFile.ReadAllTextAsync(latest);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                var arr = JsonSerializer.Deserialize<List<UserAbsenceAggregate>>(json, opts);
                if (arr != null)
                {
                    list = arr;
                }
                else
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("aggregates", out var agEl) && agEl.ValueKind == JsonValueKind.Array)
                        {
                            var ag2 = JsonSerializer.Deserialize<List<UserAbsenceAggregate>>(agEl.GetRawText(), opts);
                            if (ag2 != null) list = ag2;
                        }
                        else if (doc.RootElement.TryGetProperty("users", out var usersEl) && usersEl.ValueKind == JsonValueKind.Array)
                        {
                            var ag3 = JsonSerializer.Deserialize<List<UserAbsenceAggregate>>(usersEl.GetRawText(), opts);
                            if (ag3 != null) list = ag3;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ABSENCES] Impossible de charger le snapshot JSON");
            }
            return list;
        }

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostValidateChecklistAsync(int checklistId)
        {
            try
            {
                var login = GetCurrentUserLogin();
                var user = await _context.Utilisateurs.FirstOrDefaultAsync(u => u.LoginWindows == login);
                if (user == null) { ErrorMessage = "Utilisateur inconnu."; return RedirectToPage(); }

                var item = await _context.PrsChecklists.FirstOrDefaultAsync(c => c.Id == checklistId);
                if (item == null) { ErrorMessage = "Élément introuvable."; return RedirectToPage(); }

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

                var item = await _context.PrsChecklists.FirstOrDefaultAsync(c => c.Id == checklistId);
                if (item == null) { ErrorMessage = "Élément introuvable."; return RedirectToPage(); }

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

        // ----- VM Classes -----
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

        public class PrsAbsenceAlertVM
        {
            public int PrsId { get; set; }
            public string Titre { get; set; }
            public DateTime DateDebut { get; set; }
            public DateTime DateFin { get; set; }
            public List<string> AbsentLogins { get; set; } = new();
        }
    }
}