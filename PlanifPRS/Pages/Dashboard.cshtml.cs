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

        // ✅ NOUVEAU : Chef de Service
        public bool IsChefService { get; private set; } = false;
        public string ServiceName { get; private set; } = "";
        public ServiceStatsVM ServiceStats { get; private set; } = new();
        public List<UtilisateurServiceVM> UtilisateursService { get; private set; } = new();
        public List<TacheServiceVM> TachesServiceEnRetard { get; private set; } = new();

        public List<TacheServiceVM> TachesServiceAVenir { get; private set; } = new();

        [TempData] public string Flash { get; set; }
        [TempData] public string ErrorMessage { get; set; }
        public bool IsManagerView { get; private set; } = false;
        public string CurrentUserLogin { get; private set; } = "-";
        public string CurrentUserRole { get; private set; } = "-";

        public int DefaultDueSoonDays { get; private set; } = 7;

        // Vue globale (admin uniquement - toutes les PRS)
        public SummaryVM Summary { get; private set; } = new();
        public List<ChecklistItemVM> OverdueItems { get; private set; } = new();
        public List<ChecklistItemVM> DueSoonItems { get; private set; } = new();

        // Qui me concernent (affectations)
        public SummaryVM MyAssignedSummary { get; private set; } = new();
        public List<ChecklistItemVM> MyAssignedOverdueItems { get; private set; } = new();
        public List<ChecklistItemVM> MyAssignedDueSoonItems { get; private set; } = new();

        // Que j'ai créées
        public SummaryVM MyCreatedSummary { get; private set; } = new();
        public List<ChecklistItemVM> MyCreatedOverdueItems { get; private set; } = new();
        public List<ChecklistItemVM> MyCreatedDueSoonItems { get; private set; } = new();

        public AdminSummaryVM AdminSummary { get; private set; } = new();
        public List<PrsCardVM> UpcomingPrs { get; private set; } = new(); // Qui me concernent
        public List<PrsCardVM> AllUpcomingPrs { get; private set; } = new(); // Toutes (admin)
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

                // ✅ NOUVEAU : Détection chef de service
                IsChefService = user.IsChefService;
                ServiceName = user.ServiceClean;

                _logger.LogInformation($"UserId: {_userId}, Role: {CurrentUserRole}, IsManager: {IsManagerView}, IsChefService: {IsChefService}");

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

                // ✅ NOUVEAU : Charger les stats du service pour le chef
                if (IsChefService)
                {
                    await ChargerStatsServiceAsync(user, DateTime.Today);
                }

                // ============ CHECKLISTS ============
                _logger.LogInformation("Loading checklists...");

                List<ChecklistItemVM> allItems = new();
                List<ChecklistItemVM> myAssignedItems = new();
                List<ChecklistItemVM> myCreatedItems = new();

                // 1. Charger les affectations de l'utilisateur (qui me concernent)
                var checklistsByUser = await _context.ChecklistAffectations
                    .AsNoTracking()
                    .Where(a => a.UtilisateurId.HasValue && a.UtilisateurId.Value == _userId)
                    .Select(a => a.ChecklistId)
                    .ToListAsync();

                List<int> checklistsByGroup = new();
                if (_myGroupIds.Any())
                {
                    checklistsByGroup = await _context.ChecklistAffectations
                        .AsNoTracking()
                        .Where(a => a.GroupeId.HasValue && _myGroupIds.Contains(a.GroupeId.Value))
                        .Select(a => a.ChecklistId)
                        .ToListAsync();
                }

                var myChecklistIds = checklistsByUser
                    .Concat(checklistsByGroup)
                    .Distinct()
                    .ToList();

                _logger.LogInformation($"Assigned checklists: {myChecklistIds.Count}");

                if (myChecklistIds.Any())
                {
                    var checklistIdsParam = string.Join(",", myChecklistIds);
                    var checklists = await _context.PrsChecklists
                        .FromSqlRaw($@"
                            SELECT * 
                            FROM [dbo].[PRS_Checklist] 
                            WHERE [Id] IN ({checklistIdsParam})
                        ")
                        .AsNoTracking()
                        .ToListAsync();

                    if (checklists.Any())
                    {
                        var prsIdsFromChecklists = checklists.Select(c => c.PRSId).Distinct().ToList();
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

                        myAssignedItems = checklists
                            .Where(c => prsDict.ContainsKey(c.PRSId))
                            .Select(c => new { c, p = prsDict[c.PRSId] })
                            .OrderBy(x => x.p.DateDebut)
                            .ThenBy(x => x.c.Priorite)
                            .ThenBy(x => x.c.DelaiDefautJours)
                            .ThenBy(x => x.c.Categorie)
                            .ThenBy(x => x.c.SousCategorie)
                            .Select(r => CreateChecklistItemVM(r.c, r.p))
                            .ToList();

                        _logger.LogInformation($"My assigned checklists loaded: {myAssignedItems.Count}");
                    }
                }

                // 2. Charger les PRS créées par l'utilisateur (que j'ai créées)
                _logger.LogInformation($"Searching for PRS created by: '{CurrentUserLogin}'");

                var myCreatedPrs = await _context.Prs.AsNoTracking()
                    .Where(p => p.CreatedByLogin == CurrentUserLogin && p.Statut != "Supprimé")
                    .ToListAsync();

                _logger.LogInformation($"Created PRS found: {myCreatedPrs.Count}");

                if (myCreatedPrs.Any())
                {
                    foreach (var prs in myCreatedPrs)
                    {
                        _logger.LogInformation($"  - PRS #{prs.Id}: {prs.Titre} (CreatedBy: '{prs.CreatedByLogin}')");
                    }
                }

                var myCreatedPrsIds = myCreatedPrs.Select(p => p.Id).ToList();

                if (myCreatedPrsIds.Any())
                {
                    var createdPrsIdsParam = string.Join(",", myCreatedPrsIds);
                    var createdChecklists = await _context.PrsChecklists
                        .FromSqlRaw($@"
                            SELECT * 
                            FROM [dbo].[PRS_Checklist] 
                            WHERE [PRSId] IN ({createdPrsIdsParam})
                        ")
                        .AsNoTracking()
                        .ToListAsync();

                    _logger.LogInformation($"Checklists for created PRS: {createdChecklists.Count}");

                    if (createdChecklists.Any())
                    {
                        var createdPrsDict = myCreatedPrs.ToDictionary(p => p.Id);

                        myCreatedItems = createdChecklists
                            .Where(c => createdPrsDict.ContainsKey(c.PRSId))
                            .Select(c => new { c, p = createdPrsDict[c.PRSId] })
                            .OrderBy(x => x.p.DateDebut)
                            .ThenBy(x => x.c.Priorite)
                            .ThenBy(x => x.c.DelaiDefautJours)
                            .ThenBy(x => x.c.Categorie)
                            .ThenBy(x => x.c.SousCategorie)
                            .Select(r => CreateChecklistItemVM(r.c, r.p))
                            .ToList();

                        _logger.LogInformation($"My created checklists loaded: {myCreatedItems.Count}");
                    }
                }
                else
                {
                    _logger.LogWarning($"No PRS created by '{CurrentUserLogin}' found in database");
                }

                // 3. Pour les admins : charger TOUTES les checklists
                if (IsManagerView)
                {
                    var allValidPrsIds = await _context.Prs.AsNoTracking()
                        .Where(p => p.Statut != "Supprimé")
                        .Select(p => p.Id)
                        .ToListAsync();

                    _logger.LogInformation($"All valid PRS IDs (admin): {allValidPrsIds.Count}");

                    if (allValidPrsIds.Any())
                    {
                        var allValidPrsIdsParam = string.Join(",", allValidPrsIds);
                        var allChecklists = await _context.PrsChecklists
                            .FromSqlRaw($@"
                                SELECT * 
                                FROM [dbo].[PRS_Checklist] 
                                WHERE [PRSId] IN ({allValidPrsIdsParam})
                            ")
                            .AsNoTracking()
                            .ToListAsync();

                        var allPrsDict = await _context.Prs
                            .FromSqlRaw($@"
                                SELECT * 
                                FROM [dbo].[PRS] 
                                WHERE [Id] IN ({allValidPrsIdsParam})
                            ")
                            .AsNoTracking()
                            .ToDictionaryAsync(p => p.Id);

                        allItems = allChecklists
                            .Where(c => allPrsDict.ContainsKey(c.PRSId))
                            .Select(c => new { c, p = allPrsDict[c.PRSId] })
                            .OrderBy(x => x.p.DateDebut)
                            .ThenBy(x => x.c.Priorite)
                            .ThenBy(x => x.c.DelaiDefautJours)
                            .ThenBy(x => x.c.Categorie)
                            .ThenBy(x => x.c.SousCategorie)
                            .Select(r => CreateChecklistItemVM(r.c, r.p))
                            .ToList();

                        _logger.LogInformation($"All checklists loaded (admin): {allItems.Count}");
                    }
                }

                // Calculer les résumés pour "Toutes" (admin uniquement)
                if (IsManagerView && allItems.Any())
                {
                    var allOpen = allItems.Where(i => !i.IsValidated).ToList();
                    var allLate = allOpen.Where(i => i.DueDate < DateTime.Today).ToList();
                    var allToday = allOpen.Where(i => i.DaysLeft == 0).ToList();
                    var allSoon = allOpen.Where(i => i.DaysLeft > 0 && i.DaysLeft <= DefaultDueSoonDays).ToList();

                    Summary = new SummaryVM
                    {
                        TotalOpenItems = allOpen.Count,
                        LateCount = allLate.Count,
                        TodayCount = allToday.Count,
                        DueSoonCount = allSoon.Count
                    };

                    OverdueItems = allLate.OrderBy(i => i.DueDate).Take(8).ToList();
                    DueSoonItems = allOpen.Where(i => i.DaysLeft >= 0).OrderBy(i => i.DaysLeft).ThenBy(i => i.Priorite).Take(10).ToList();
                }

                // Calculer les résumés pour "Qui me concernent"
                if (myAssignedItems.Any())
                {
                    var myAssignedOpen = myAssignedItems.Where(i => !i.IsValidated).ToList();
                    var myAssignedLate = myAssignedOpen.Where(i => i.DueDate < DateTime.Today).ToList();
                    var myAssignedToday = myAssignedOpen.Where(i => i.DaysLeft == 0).ToList();
                    var myAssignedSoon = myAssignedOpen.Where(i => i.DaysLeft > 0 && i.DaysLeft <= DefaultDueSoonDays).ToList();

                    MyAssignedSummary = new SummaryVM
                    {
                        TotalOpenItems = myAssignedOpen.Count,
                        LateCount = myAssignedLate.Count,
                        TodayCount = myAssignedToday.Count,
                        DueSoonCount = myAssignedSoon.Count
                    };

                    MyAssignedOverdueItems = myAssignedLate.OrderBy(i => i.DueDate).Take(8).ToList();
                    MyAssignedDueSoonItems = myAssignedOpen.Where(i => i.DaysLeft >= 0).OrderBy(i => i.DaysLeft).ThenBy(i => i.Priorite).Take(10).ToList();
                }

                // Calculer les résumés pour "Que j'ai créées"
                if (myCreatedItems.Any())
                {
                    var myCreatedOpen = myCreatedItems.Where(i => !i.IsValidated).ToList();
                    var myCreatedLate = myCreatedOpen.Where(i => i.DueDate < DateTime.Today).ToList();
                    var myCreatedToday = myCreatedOpen.Where(i => i.DaysLeft == 0).ToList();
                    var myCreatedSoon = myCreatedOpen.Where(i => i.DaysLeft > 0 && i.DaysLeft <= DefaultDueSoonDays).ToList();

                    MyCreatedSummary = new SummaryVM
                    {
                        TotalOpenItems = myCreatedOpen.Count,
                        LateCount = myCreatedLate.Count,
                        TodayCount = myCreatedToday.Count,
                        DueSoonCount = myCreatedSoon.Count
                    };

                    MyCreatedOverdueItems = myCreatedLate.OrderBy(i => i.DueDate).Take(8).ToList();
                    MyCreatedDueSoonItems = myCreatedOpen.Where(i => i.DaysLeft >= 0).OrderBy(i => i.DaysLeft).ThenBy(i => i.Priorite).Take(10).ToList();
                }

                _logger.LogInformation($"Summary - All: {Summary.TotalOpenItems}, Assigned: {MyAssignedSummary.TotalOpenItems}, Created: {MyCreatedSummary.TotalOpenItems}");

                // ============ PRS À VENIR ============
                _logger.LogInformation("Loading upcoming PRS...");

                var prsByUser = await _context.PrsAffectations
                    .AsNoTracking()
                    .Where(a => a.UtilisateurId.HasValue && a.UtilisateurId.Value == _userId)
                    .Select(a => a.PrsId)
                    .ToListAsync();

                List<int> prsByGroup = new();
                if (_myGroupIds.Any())
                {
                    prsByGroup = await _context.PrsAffectations
                        .AsNoTracking()
                        .Where(a => a.GroupeId.HasValue && _myGroupIds.Contains(a.GroupeId.Value))
                        .Select(a => a.PrsId)
                        .ToListAsync();
                }

                var prsIdsAssigned = prsByUser.Concat(prsByGroup).Distinct().ToList();

                _logger.LogInformation($"PRS IDs assigned to user: {prsIdsAssigned.Count}");

                // Charger toutes les PRS à venir
                var allUpcomingPrsList = await _context.Prs.AsNoTracking()
                    .Where(p => p.DateDebut >= DateTime.Today && p.Statut != "Supprimé")
                    .OrderBy(p => p.DateDebut)
                    .ThenBy(p => p.Titre)
                    .ToListAsync();

                _logger.LogInformation($"All upcoming PRS: {allUpcomingPrsList.Count}");

                // Pour les admins : stocker TOUTES les PRS à venir
                if (IsManagerView)
                {
                    AllUpcomingPrs = allUpcomingPrsList
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

                    _logger.LogInformation($"AllUpcomingPrs (admin): {AllUpcomingPrs.Count}");
                }

                // Pour tout le monde : PRS à venir qui me concernent (affectations)
                if (prsIdsAssigned.Any())
                {
                    UpcomingPrs = allUpcomingPrsList
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

                    _logger.LogInformation($"UpcomingPrs (assigned): {UpcomingPrs.Count}");
                }
                else
                {
                    _logger.LogInformation("No assigned PRS - UpcomingPrs will be empty");
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

                // ============ ALERTES PARENT/ENFANT ============
                _logger.LogInformation("Loading parent/child alerts...");

                var allChildPrs = await _context.Prs.AsNoTracking()
                    .Where(p => p.PrsParentId != null &&
                               p.Statut != "Supprimé" &&
                               (p.Equipement == "Finition" || p.Equipement.Contains("Finition")))
                    .ToListAsync();

                var parentIds = allChildPrs.Select(c => c.PrsParentId.Value).Distinct().ToList();

                var parentPrsDict = new Dictionary<int, Models.Prs>();
                if (parentIds.Any())
                {
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

                // ============ VUE GLOBALE (ADMIN) ============
                if (IsManagerView)
                {
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
                }

                // Alertes d'absence
                await BuildAbsenceAlertsFromJsonAsync(DateTime.Today, IsManagerView ? null : prsIdsAssigned);

                _logger.LogInformation("========== DASHBOARD END ==========");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DASHBOARD][GET] Erreur chargement");
                ErrorMessage = $"Erreur lors du chargement du tableau de bord: {ex.Message}";
            }

            return Page();
        }

        private async Task ChargerStatsServiceAsync(Models.Utilisateur chef, DateTime today)
        {
            try
            {
                var servicePrefix = chef.ServicePrefix;
                _logger.LogInformation($"[DASHBOARD][CHEF] Chargement stats service: {servicePrefix}");

                // Récupérer tous les utilisateurs du service
                var utilisateursService = await _context.Utilisateurs
                    .AsNoTracking()
                    .Where(u => !u.DateDeleted.HasValue
                             && u.Service != null
                             && u.Service.StartsWith(servicePrefix))
                    .OrderBy(u => u.Nom)
                    .ThenBy(u => u.Prenom)
                    .ToListAsync();

                var userIds = utilisateursService.Select(u => u.Id).ToList();

                // ✅ AJOUTER ICI : Charger TOUTES les affectations UNE SEULE FOIS
                var affectationsService = await _context.ChecklistAffectations
                    .AsNoTracking()
                    .Where(a => a.UtilisateurId != null)
                    .Select(a => new { a.UtilisateurId, a.ChecklistId })
                    .ToListAsync();

                _logger.LogInformation($"[DASHBOARD][CHEF] {affectationsService.Count} affectations chargées");
                _logger.LogInformation($"[DASHBOARD][CHEF] {utilisateursService.Count} utilisateurs dans le service");

                if (!userIds.Any())
                {
                    _logger.LogWarning($"[DASHBOARD][CHEF] Aucun utilisateur trouvé dans le service {servicePrefix}");
                    ServiceStats = new ServiceStatsVM { NombreUtilisateurs = 0 };
                    UtilisateursService = new List<UtilisateurServiceVM>();
                    TachesServiceEnRetard = new List<TacheServiceVM>();
                    return;
                }

                // ✅ Construire la requête SQL avec paramètres
                var userIdsParam = string.Join(",", userIds);

                _logger.LogInformation($"[DASHBOARD][CHEF] Requête SQL pour userIds: {userIdsParam}");

                // ✅ Requête SQL brute sécurisée
                var tachesServiceIds = await _context.ChecklistAffectations
                    .FromSqlRaw(@"
        SELECT DISTINCT ChecklistId 
        FROM ChecklistAffectations 
        WHERE UtilisateurId IN (" + userIdsParam + @")
    ")
                    .AsNoTracking()
                    .Select(a => a.ChecklistId)
                    .ToListAsync();

                _logger.LogInformation($"[DASHBOARD][CHEF] {tachesServiceIds.Count} tâches trouvées via SQL brut");

                _logger.LogInformation($"[DASHBOARD][CHEF] {tachesServiceIds.Count} affectations trouvées");

                if (!tachesServiceIds.Any())
                {
                    ServiceStats = new ServiceStatsVM
                    {
                        NombreUtilisateurs = utilisateursService.Count,
                        TotalTaches = 0,
                        TachesValidees = 0,
                        TachesEnCours = 0,
                        TachesEnRetard = 0,
                        TauxCompletion = 0
                    };
                    UtilisateursService = new List<UtilisateurServiceVM>();
                    TachesServiceEnRetard = new List<TacheServiceVM>();
                    return;
                }

                // Récupérer les tâches avec SQL brut
                var tachesIdsParam = string.Join(",", tachesServiceIds);
                var tachesService = await _context.PrsChecklists
                    .FromSqlRaw($@"SELECT * FROM [dbo].[PRS_Checklist] WHERE [Id] IN ({tachesIdsParam})")
                    .AsNoTracking()
                    .ToListAsync();

                _logger.LogInformation($"[DASHBOARD][CHEF] {tachesService.Count} tâches trouvées");

                if (!tachesService.Any())
                {
                    ServiceStats = new ServiceStatsVM { NombreUtilisateurs = utilisateursService.Count };
                    UtilisateursService = new List<UtilisateurServiceVM>();
                    TachesServiceEnRetard = new List<TacheServiceVM>();
                    return;
                }

                // Récupérer les PRS associées
                var prsIds = tachesService.Select(t => t.PRSId).Distinct().ToList();
                var prsIdsParam = string.Join(",", prsIds);
                var prsList = await _context.Prs
                    .FromSqlRaw($@"SELECT * FROM [dbo].[PRS] WHERE [Id] IN ({prsIdsParam}) AND [Statut] != 'Supprimé'")
                    .AsNoTracking()
                    .ToListAsync();

                var prsDict = prsList.ToDictionary(p => p.Id);

                var tachesServiceValides = tachesService
                    .Where(t => prsDict.ContainsKey(t.PRSId))
                    .ToList();

                _logger.LogInformation($"[DASHBOARD][CHEF] {tachesServiceValides.Count} tâches valides (PRS non supprimées)");

                // === STATS GLOBALES DU SERVICE ===
                var totalTaches = tachesServiceValides.Count;
                var tachesValidees = tachesServiceValides.Count(t => t.EstCoche);
                var tachesEnRetard = tachesServiceValides.Count(t =>
                    !t.EstCoche &&
                    t.DateEcheance.HasValue &&
                    t.DateEcheance.Value.Date < today);
                var tachesEnCours = totalTaches - tachesValidees - tachesEnRetard;

                ServiceStats = new ServiceStatsVM
                {
                    NombreUtilisateurs = utilisateursService.Count,
                    TotalTaches = totalTaches,
                    TachesValidees = tachesValidees,
                    TachesEnCours = tachesEnCours,
                    TachesEnRetard = tachesEnRetard,
                    TauxCompletion = totalTaches > 0 ? (int)((double)tachesValidees / totalTaches * 100) : 0
                };

                // === STATS PAR UTILISATEUR ===
                UtilisateursService = new List<UtilisateurServiceVM>();

                foreach (var user in utilisateursService)
                {
                    var userTachesIds = await _context.ChecklistAffectations
                        .AsNoTracking()
                        .Where(a => a.UtilisateurId == user.Id)
                        .Select(a => a.ChecklistId)
                        .ToListAsync();

                    var userTaches = tachesServiceValides
                        .Where(t => userTachesIds.Contains(t.Id))
                        .ToList();

                    var userTotal = userTaches.Count;
                    var userValidees = userTaches.Count(t => t.EstCoche);
                    var userRetard = userTaches.Count(t =>
                        !t.EstCoche &&
                        t.DateEcheance.HasValue &&
                        t.DateEcheance.Value.Date < today);

                    UtilisateursService.Add(new UtilisateurServiceVM
                    {
                        Id = user.Id,
                        NomComplet = user.NomComplet,
                        TotalTaches = userTotal,
                        TachesValidees = userValidees,
                        TachesEnRetard = userRetard,
                        TauxCompletion = userTotal > 0 ? (int)((double)userValidees / userTotal * 100) : 0
                    });
                }

                // === TOP 10 TÂCHES EN RETARD DU SERVICE ===
                // ✅ Filtrer EN MÉMOIRE depuis affectationsService (déjà chargé plus haut)
                var affectationsMap = affectationsService
                    .Where(a => tachesServiceIds.Contains(a.ChecklistId))
                    .ToList();

                var userDict = utilisateursService.ToDictionary(u => u.Id);

                TachesServiceEnRetard = tachesServiceValides
                    .Where(t => !t.EstCoche && t.DateEcheance.HasValue && t.DateEcheance.Value.Date < today)
                    .OrderBy(t => t.DateEcheance)
                    .Take(10)
                    .Select(t =>
                    {
                        var prs = prsDict.ContainsKey(t.PRSId) ? prsDict[t.PRSId] : null;
                        var affectation = affectationsMap.FirstOrDefault(a => a.ChecklistId == t.Id);
                        var userName = "Non assigné";

                        if (affectation != null &&
                            affectation.UtilisateurId.HasValue &&
                            userDict.ContainsKey(affectation.UtilisateurId.Value))
                        {
                            userName = userDict[affectation.UtilisateurId.Value].NomComplet;
                        }

                        return new TacheServiceVM
                        {
                            Id = t.Id,
                            Libelle = t.Libelle ?? t.Tache ?? "Tâche sans nom",
                            PrsTitre = prs?.Titre ?? "N/A",
                            DateEcheance = t.DateEcheance,
                            JoursRetard = t.DateEcheance.HasValue
                                ? (int)(today - t.DateEcheance.Value.Date).TotalDays
                                : 0,
                            Priorite = t.Priorite > 0 ? t.Priorite : 3,
                            UtilisateurAssigne = userName
                        };
                    })
                    .ToList();

                _logger.LogInformation($"[DASHBOARD][CHEF] ✅ Stats OK: Users={utilisateursService.Count}, Total={totalTaches}, Retard={tachesEnRetard}");

                // ✅ NOUVEAU : Top 10 tâches à venir (7 prochains jours)
                var dans7Jours = today.AddDays(7);
                TachesServiceAVenir = tachesServiceValides
                    .Where(t => !t.EstCoche &&
                                t.DateEcheance.HasValue &&
                                t.DateEcheance.Value.Date >= today &&
                                t.DateEcheance.Value.Date <= dans7Jours)
                    .OrderBy(t => t.DateEcheance)
                    .Take(10)
                    .Select(t =>
                    {
                        var prs = prsDict.ContainsKey(t.PRSId) ? prsDict[t.PRSId] : null;
                        var affectation = affectationsMap.FirstOrDefault(a => a.ChecklistId == t.Id);
                        var userName = "Non assigné";

                        if (affectation != null && affectation.UtilisateurId.HasValue &&
                            userDict.ContainsKey(affectation.UtilisateurId.Value))
                        {
                            userName = userDict[affectation.UtilisateurId.Value].NomComplet;
                        }

                        return new TacheServiceVM
                        {
                            Id = t.Id,
                            Libelle = t.Libelle ?? t.Tache ?? "Tâche sans nom",
                            PrsTitre = prs?.Titre ?? "N/A",
                            DateEcheance = t.DateEcheance,
                            JoursRetard = 0, // Pas en retard, c'est à venir
                            Priorite = t.Priorite > 0 ? t.Priorite : 3,
                            UtilisateurAssigne = userName
                        };
                    })
                    .ToList();

                _logger.LogInformation($"[DASHBOARD][CHEF] {TachesServiceAVenir.Count} tâches à venir trouvées");

                _logger.LogInformation($"[DASHBOARD][CHEF] Stats chargées: Total={totalTaches}, Retard={tachesEnRetard}, Utilisateurs={utilisateursService.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DASHBOARD][CHEF] Erreur chargement stats service");
                ServiceStats = new ServiceStatsVM();
                UtilisateursService = new List<UtilisateurServiceVM>();
                TachesServiceEnRetard = new List<TacheServiceVM>();
            }
        }

        private ChecklistItemVM CreateChecklistItemVM(Models.PrsChecklist c, Models.Prs p)
        {
            var delai = c.DelaiDefautJours > 0 ? c.DelaiDefautJours : 1;
            var due = c.DateEcheance.HasValue ? c.DateEcheance.Value.Date : p.DateDebut.Date.AddDays(delai);
            var daysLeft = (int)Math.Floor((due - DateTime.Today).TotalDays);
            return new ChecklistItemVM
            {
                Id = c.Id,
                PrsId = p.Id,
                PrsTitre = p.Titre,
                Categorie = c.Categorie,
                SousCategorie = c.SousCategorie,
                Libelle = string.IsNullOrWhiteSpace(c.Libelle) ? c.Tache : c.Libelle,
                Priorite = c.Priorite > 0 ? c.Priorite : 3,
                DelaiJours = delai,
                DueDate = due,
                DaysLeft = daysLeft,
                Source = c.DateEcheance.HasValue ? "Fixée" : "Calculée",
                IsValidated = c.EstCoche,
                DateValidation = c.DateValidation,
                ValidePar = c.ValidePar
            };
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
                if (item == null) { ErrorMessage = "tâche introuvable."; return RedirectToPage(); }

                item.EstCoche = true;
                item.DateValidation = DateTime.Now;
                item.ValidePar = login;
                item.Statut = true;

                await _context.SaveChangesAsync();
                Flash = "tâche validé.";
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
                if (item == null) { ErrorMessage = "tâche introuvable."; return RedirectToPage(); }

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

        public class ServiceStatsVM
        {
            public int NombreUtilisateurs { get; set; }
            public int TotalTaches { get; set; }
            public int TachesValidees { get; set; }
            public int TachesEnCours { get; set; }
            public int TachesEnRetard { get; set; }
            public int TauxCompletion { get; set; }
        }

        public class UtilisateurServiceVM
        {
            public int Id { get; set; }
            public string NomComplet { get; set; }
            public int TotalTaches { get; set; }
            public int TachesValidees { get; set; }
            public int TachesEnRetard { get; set; }
            public int TauxCompletion { get; set; }
        }

        public class TacheServiceVM
        {
            public int Id { get; set; }
            public string Libelle { get; set; }
            public string PrsTitre { get; set; }
            public DateTime? DateEcheance { get; set; }
            public int JoursRetard { get; set; }
            public int Priorite { get; set; }
            public string UtilisateurAssigne { get; set; }
        }
    }
}