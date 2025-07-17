using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Models;
using System.Data;

namespace PlanifPRS.Pages
{
    public class ValidationModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;
        private readonly ILogger<ValidationModel> _logger;

        public ValidationModel(PlanifPrsDbContext context, ILogger<ValidationModel> logger)
        {
            _context = context;
            _logger = logger;
        }

        public IList<Models.Prs> PrsList { get; set; } = new List<Models.Prs>();
        public SelectList LigneList { get; set; } = new(new List<SelectListItem>(), "Value", "Text");
        public int PrsCount => PrsList.Count;

        // Paramètre de débogage
        [BindProperty(SupportsGet = true)]
        public bool Debug { get; set; } = false;

        public async Task<IActionResult> OnGetAsync()
        {
            // Vérification des droits - EXACTEMENT comme CalendarBlock
            if (!HasRequiredRole())
            {
                _logger.LogWarning($"Accès refusé pour l'utilisateur {GetCurrentUserLogin()}");
                return Redirect("/AccessDenied");
            }

            _logger.LogInformation($"Accès à la page validation par {GetCurrentUserLogin()}");

            await ChargerLignesAsync();
            await ChargerPrsAsync();

            return Page();
        }

        private async Task ChargerPrsAsync()
        {
            try
            {
                _logger.LogInformation("=== DÉBUT CHARGEMENT PRS POUR VALIDATION ===");

                // APPROCHE SQL BRUTE pour éviter les problèmes de NULL avec Entity Framework
                var prsList = new List<Models.Prs>();

                try
                {
                    var connection = _context.Database.GetDbConnection();
                    if (connection.State != System.Data.ConnectionState.Open)
                        await connection.OpenAsync();

                    using var command = connection.CreateCommand();
                    command.CommandText = @"
                SELECT 
                    p.Id,
                    ISNULL(p.Titre, '') as Titre,
                    ISNULL(p.Statut, '') as Statut,
                    ISNULL(p.Equipement, '') as Equipement,
                    ISNULL(p.CreatedByLogin, '') as CreatedByLogin,
                    p.DateCreation,
                    ISNULL(p.LigneId, 0) as LigneId,
                    ISNULL(l.Nom, '') as LigneNom
                FROM [PlanifPRS].[dbo].[Prs] p
                LEFT JOIN [PlanifPRS].[dbo].[Lignes] l ON p.LigneId = l.Id
                ORDER BY p.DateCreation DESC";

                    using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        try
                        {
                            var prs = new Models.Prs();

                            // Lecture sécurisée des valeurs
                            prs.Id = reader.IsDBNull("Id") ? 0 : reader.GetInt32("Id");
                            prs.Titre = reader.IsDBNull("Titre") ? "" : reader.GetString("Titre");
                            prs.Statut = reader.IsDBNull("Statut") ? "" : reader.GetString("Statut");
                            prs.Equipement = reader.IsDBNull("Equipement") ? "" : reader.GetString("Equipement");
                            prs.CreatedByLogin = reader.IsDBNull("CreatedByLogin") ? "" : reader.GetString("CreatedByLogin");
                            prs.DateCreation = reader.IsDBNull("DateCreation") ? DateTime.Now : reader.GetDateTime("DateCreation");
                            prs.LigneId = reader.IsDBNull("LigneId") ? 0 : reader.GetInt32("LigneId");

                            // Créer l'objet Ligne si nécessaire
                            var ligneNom = reader.IsDBNull("LigneNom") ? "" : reader.GetString("LigneNom");
                            if (prs.LigneId > 0 && !string.IsNullOrEmpty(ligneNom))
                            {
                                prs.Ligne = new Ligne
                                {
                                    Id = prs.LigneId,
                                    Nom = ligneNom
                                };
                            }

                            prsList.Add(prs);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Erreur lors de la lecture d'une ligne PRS, ignorée");
                            continue;
                        }
                    }

                    _logger.LogInformation($"PRS chargées avec SQL brut: {prsList.Count}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erreur avec SQL brut, tentative avec Entity Framework filtré");

                    // Fallback avec Entity Framework mais en filtrant les NULL
                    try
                    {
                        prsList = await _context.Prs
                            .Where(p => p.Id > 0) // Filtre de base pour éviter les lignes corrompues
                            .Select(p => new Models.Prs
                            {
                                Id = p.Id,
                                Titre = p.Titre ?? "",
                                Statut = p.Statut ?? "",
                                Equipement = p.Equipement ?? "",
                                CreatedByLogin = p.CreatedByLogin ?? "",
                                DateCreation = p.DateCreation,
                                LigneId = p.LigneId
                            })
                            .ToListAsync();

                        // Charger les lignes séparément
                        var lignes = await _context.Lignes.ToListAsync();
                        var lignesDict = lignes.ToDictionary(l => l.Id, l => l);

                        foreach (var prs in prsList)
                        {
                            if (prs.LigneId > 0 && lignesDict.ContainsKey(prs.LigneId))
                            {
                                prs.Ligne = lignesDict[prs.LigneId];
                            }
                        }

                        _logger.LogInformation($"PRS chargées avec Entity Framework filtré: {prsList.Count}");
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "Erreur même avec Entity Framework filtré");
                        throw; // Re-throw pour que l'erreur soit gérée par le catch principal
                    }
                }

                // Sécuriser toutes les propriétés au cas où
                foreach (var prs in prsList)
                {
                    if (prs.Titre == null) prs.Titre = "";
                    if (prs.Statut == null) prs.Statut = "";
                    if (prs.Equipement == null) prs.Equipement = "";
                    if (prs.CreatedByLogin == null) prs.CreatedByLogin = "";
                }

                PrsList = prsList;

                _logger.LogInformation($"PRS finales: {PrsList.Count}");

                // Log sécurisé des premières PRS
                if (PrsList.Any())
                {
                    _logger.LogInformation("Premières PRS:");
                    foreach (var prs in PrsList.Take(3))
                    {
                        var titre = string.IsNullOrEmpty(prs.Titre) ? "Sans titre" : prs.Titre;
                        var statut = string.IsNullOrEmpty(prs.Statut) ? "NULL" : prs.Statut;
                        var type = string.IsNullOrEmpty(prs.Equipement) ? "PRS CMS" : prs.Equipement;
                        var secteur = prs.Ligne?.Nom ?? "Non défini";
                        var createur = string.IsNullOrEmpty(prs.CreatedByLogin) ? "Inconnu" : prs.CreatedByLogin;

                        _logger.LogInformation($"  - PRS #{prs.Id}: {titre} | Statut: {statut} | Type: {type} | Secteur: {secteur} | Par: {createur}");
                    }
                }

                _logger.LogInformation("=== FIN CHARGEMENT PRS POUR VALIDATION ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur générale lors du chargement des PRS");
                _logger.LogError($"Détails: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");

                PrsList = new List<Models.Prs>();
                ModelState.AddModelError(string.Empty, "Erreur lors du chargement des PRS. Vérifiez les logs pour plus de détails.");
            }
        }

        private async Task ChargerLignesAsync()
        {
            try
            {
                var lignesList = new List<SelectListItem>
        {
            new() { Value = "", Text = "Tous les secteurs" }
        };

                try
                {
                    // Approche SQL brute d'abord
                    var connection = _context.Database.GetDbConnection();
                    if (connection.State != System.Data.ConnectionState.Open)
                        await connection.OpenAsync();

                    using var command = connection.CreateCommand();
                    command.CommandText = @"
                SELECT Id, ISNULL(Nom, '') as Nom 
                FROM [PlanifPRS].[dbo].[Lignes] 
                WHERE Nom IS NOT NULL AND Nom != ''
                ORDER BY Nom";

                    using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        try
                        {
                            var id = reader.IsDBNull("Id") ? 0 : reader.GetInt32("Id");
                            var nom = reader.IsDBNull("Nom") ? "" : reader.GetString("Nom");

                            if (id > 0 && !string.IsNullOrWhiteSpace(nom))
                            {
                                lignesList.Add(new SelectListItem
                                {
                                    Value = id.ToString(),
                                    Text = nom.Trim()
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Erreur lors de la lecture d'une ligne, ignorée");
                            continue;
                        }
                    }

                    _logger.LogInformation($"Lignes chargées avec SQL brut: {lignesList.Count - 1}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erreur avec SQL brut pour les lignes, tentative avec Entity Framework");

                    // Fallback avec Entity Framework
                    var lignes = await _context.Lignes
                        .Where(l => l.Id > 0 && l.Nom != null && l.Nom != "")
                        .OrderBy(l => l.Nom)
                        .ToListAsync();

                    foreach (var ligne in lignes)
                    {
                        if (ligne != null && ligne.Id > 0 && !string.IsNullOrWhiteSpace(ligne.Nom))
                        {
                            lignesList.Add(new SelectListItem
                            {
                                Value = ligne.Id.ToString(),
                                Text = ligne.Nom.Trim()
                            });
                        }
                    }

                    _logger.LogInformation($"Lignes chargées avec Entity Framework: {lignesList.Count - 1}");
                }

                LigneList = new SelectList(lignesList, "Value", "Text");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du chargement des lignes");
                LigneList = new SelectList(new List<SelectListItem>
        {
            new() { Value = "", Text = "Tous les secteurs" }
        }, "Value", "Text");
            }
        }
        // Ajouter cette méthode dans ValidationModel
        private string GetEditPageForPrs(Models.Prs prs)
        {
            var equipement = prs.Equipement ?? "";
            var titre = prs.Titre ?? "";

            // VÉRIFIER D'ABORD PAR L'ÉQUIPEMENT
            if (equipement == "Audit" || equipement == "Intervention" || equipement == "Visite Client")
            {
                return "/EditAudit";
            }

            // VÉRIFIER PAR LE TITRE SI ÉQUIPEMENT VIDE
            if (titre.Contains("Audit") || titre.Contains("Intervention") || titre.Contains("Visite Client"))
            {
                return "/EditAudit";
            }

            // PRS CLASSIQUES → PAGE EDIT NORMALE
            return "/Edit";
        }
        private bool HasRequiredRole()
        {
            try
            {
                // EXACTEMENT COMME CALENDARBLOCK
                var loginName = User.Identity?.Name?.Split('\\').LastOrDefault();

                if (string.IsNullOrEmpty(loginName))
                {
                    _logger.LogWarning("Login vide ou null");
                    return false;
                }

                var currentUser = _context.Utilisateurs
                    .FirstOrDefault(u => u.LoginWindows == loginName && u.DateDeleted == null);

                if (currentUser == null)
                {
                    _logger.LogWarning($"Utilisateur non trouvé en base pour le login: {loginName}");
                    return false;
                }

                var userRights = currentUser.Droits?.ToLower();
                var hasRights = userRights == "admin" || userRights == "validateur";

                _logger.LogInformation($"Utilisateur: {currentUser.LoginWindows} | Droits: '{currentUser.Droits}' | Accès autorisé: {hasRights}");

                return hasRights;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la vérification des droits");
                return false;
            }
        }

        private string GetCurrentUserLogin()
        {
            return User.Identity?.Name?.Split('\\').LastOrDefault() ?? "Utilisateur inconnu";
        }
    }
}