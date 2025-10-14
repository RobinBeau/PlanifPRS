using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Models;
using PlanifPRS.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PlanifPRS.Pages.Prs
{
    public class CreateModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;
        private readonly FileService _fileService;
        private readonly ChecklistService _checklistService;
        private readonly ILogger<CreateModel> _logger;
        private readonly NotificationService _notificationService; // AJOUT

        public CreateModel(PlanifPrsDbContext context, FileService fileService, ChecklistService checklistService, ILogger<CreateModel> logger, NotificationService notificationService) // AJOUT param
        {
            _context = context;
            _fileService = fileService;
            _checklistService = checklistService;
            _logger = logger;
            _notificationService = notificationService; // AJOUT
        }

        // Remplace entièrement ton handler par celui-ci
        public async Task<IActionResult> OnGetChecklistModeleItemsAsync(int id)
        {
            try
            {
                var items = await _context.ChecklistElementModeles
    .AsNoTracking()
    .Include(e => e.Groupe) // AJOUT pour récupérer le nom
    .Where(e => e.ChecklistModeleId == id)
    // Tri demandé : Catégorie DESC, Délai DESC, puis Priorité ASC, puis SousCategorie, Libellé
    .OrderByDescending(e => e.Categorie)
    .ThenByDescending(e => e.DelaiDefautJours)
    .ThenBy(e => e.Priorite)
    .ThenBy(e => e.SousCategorie)
    .ThenBy(e => e.Libelle)
    .Select(e => new
    {
        id = e.Id,
        libelle = e.Libelle,
        tache = e.Libelle,
        categorie = e.Categorie,
        sousCategorie = e.SousCategorie,
        priorite = e.Priorite,
        delaiDefautJours = e.DelaiDefautJours,
        obligatoire = e.Obligatoire,
        groupeId = e.GroupeId,                                 // AJOUT
        groupeNom = e.Groupe != null ? e.Groupe.NomGroupe : null // AJOUT
    })
    .ToListAsync();

                return new JsonResult(new { items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CREATE] OnGetChecklistModeleItemsAsync échec pour ModeleId={Id}", id);
                return new JsonResult(new { items = Array.Empty<object>() });
            }
        }

        [BindProperty]
        public string? AffectationsData { get; set; }

        [BindProperty]
        public string ChecklistAffectationsData { get; set; }

        public IList<Utilisateur> Utilisateurs { get; set; }
        public IList<GroupeUtilisateurs> GroupesUtilisateurs { get; set; }

        [BindProperty]
        public Models.Prs Prs { get; set; }


        public List<PlanifPRS.Models.Prs> PrsParentOptions { get; set; } = new();

        [BindProperty]
        public List<IFormFile> UploadedFiles { get; set; }

        [BindProperty]
        public string PrsFolderLinks { get; set; }

        [BindProperty]
        public string ChecklistData { get; set; }

        public SelectList LigneList { get; set; }

        public IList<PrsFamille> Familles { get; set; }

        // Propriété ajoutée pour les modèles de checklist
        public IList<ChecklistModele> ChecklistModeles { get; set; }

        [TempData]
        public string Flash { get; set; }

        [TempData]
        public string ErrorMessage { get; set; }

        // Propriété pour vérifier les droits utilisateur
        public bool IsAdminOrValidateur => HasRequiredRole();

        // Propriété pour obtenir le login de l'utilisateur actuel
        public string CurrentUserLogin => GetCurrentUserLogin();

        public async Task OnGetAsync()
        {
            await ChargerDonneesAsync();
            await LoadPrsParentOptionsAsync();

            var now = DateTime.Now;
            var rounded = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);

            // Valeurs par défaut
            Prs = new Models.Prs
            {
                Statut = IsAdminOrValidateur ? "Validé" : "En attente",
                DateDebut = rounded,
                DateFin = rounded.AddHours(1)
            };

            if (Request.Query.ContainsKey("start"))
            {
                if (DateTime.TryParse(Request.Query["start"], out var parsedStart))
                {
                    Prs.DateDebut = parsedStart;
                    Prs.DateFin = parsedStart.AddHours(1);
                }
            }
        }



        public async Task LoadPrsParentOptionsAsync()
        {
            // Date limite : 2 mois avant aujourd'hui
            var dateLimit = DateTime.Now.AddMonths(-2);

            PrsParentOptions = await _context.Prs
                .Where(p => p.Equipement == "CMS"
                         && p.Statut != "Supprimé"
                         && p.DateFin >= dateLimit)  // ✅ Exclure PRS terminées depuis plus de 2 mois
                .OrderByDescending(p => p.DateCreation)
                .ToListAsync();
        }
        // Validation côté serveur dans OnPostAsync()
        private bool ValidatePrsParentDates()
        {
            if (Prs.Equipement == "✨ Finition" && Prs.PrsParentId.HasValue)
            {
                var prsParent = _context.Prs.Find(Prs.PrsParentId.Value);
                if (prsParent != null && prsParent.DateFin >= Prs.DateDebut)
                {
                    ModelState.AddModelError("Prs.PrsParentId",
                        "La PRS parent doit être terminée avant le début de la PRS finition.");
                    return false;
                }
            }
            return true;
        }

        // ✅ NOUVELLE MÉTHODE POUR VÉRIFIER LA DISPONIBILITÉ
        public async Task<IActionResult> OnGetCheckAvailabilityAsync(string dateDebut, string dateFin, string affectationsData)
        {
            try
            {
                if (!DateTime.TryParse(dateDebut, out var debut) || !DateTime.TryParse(dateFin, out var fin))
                {
                    return new JsonResult(new { success = false, message = "Dates invalides" });
                }

                var conflits = new List<object>();

                if (!string.IsNullOrEmpty(affectationsData))
                {
                    var affectations = JsonSerializer.Deserialize<List<AffectationDto>>(affectationsData, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (affectations?.Any() == true)
                    {
                        foreach (var affectation in affectations)
                        {
                            if (affectation.type == "Utilisateur")
                            {
                                var utilisateurConflits = await VerifierConflitsUtilisateur(affectation.id, debut, fin);
                                conflits.AddRange(utilisateurConflits);
                            }
                            else if (affectation.type == "Groupe")
                            {
                                var groupeConflits = await VerifierConflitsGroupe(affectation.id, debut, fin);
                                conflits.AddRange(groupeConflits);
                            }
                        }
                    }
                }

                return new JsonResult(new
                {
                    success = true,
                    hasConflicts = conflits.Any(),
                    conflicts = conflits
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la vérification de disponibilité");
                return new JsonResult(new { success = false, message = "Erreur lors de la vérification" });
            }
        }

        private async Task<List<object>> VerifierConflitsUtilisateur(int utilisateurId, DateTime debut, DateTime fin)
        {
            var conflits = new List<object>();

            var utilisateur = await _context.Utilisateurs.FindAsync(utilisateurId);
            if (utilisateur == null) return conflits;

            // Vérifier les conflits directs (affectations utilisateur)
            var conflitsDirects = await _context.PrsAffectations
                .Where(a => a.UtilisateurId == utilisateurId)
                .Include(a => a.Prs)
                .Where(a => a.Prs.Statut != "Supprimé" && a.Prs.DateDebut < fin && a.Prs.DateFin > debut) // ⚠ Exclure PRS supprimées
                .Select(a => new
                {
                    type = "direct",
                    utilisateur = $"{utilisateur.Prenom} {utilisateur.Nom}",
                    prsId = a.Prs.Id,
                    prsTitre = a.Prs.Titre,
                    dateDebut = a.Prs.DateDebut,
                    dateFin = a.Prs.DateFin
                })
                .ToListAsync();

            conflits.AddRange(conflitsDirects);

            // Vérifier les conflits via groupes
            var groupesUtilisateur = await _context.GroupesUtilisateurs
                .Where(g => g.Actif && g.Membres.Any(m => m.UtilisateurId == utilisateurId))
                .Select(g => g.Id)
                .ToListAsync();

            if (groupesUtilisateur.Any())
            {
                var conflitsGroupes = await _context.PrsAffectations
                    .Where(a => a.GroupeId.HasValue && groupesUtilisateur.Contains(a.GroupeId.Value))
                    .Include(a => a.Prs)
                    .Include(a => a.Groupe)
                    .Where(a => a.Prs.Statut != "Supprimé" && a.Prs.DateDebut < fin && a.Prs.DateFin > debut) // ⚠ Exclure PRS supprimées
                    .Select(a => new
                    {
                        type = "groupe",
                        utilisateur = $"{utilisateur.Prenom} {utilisateur.Nom}",
                        groupe = a.Groupe.NomGroupe,
                        prsId = a.Prs.Id,
                        prsTitre = a.Prs.Titre,
                        dateDebut = a.Prs.DateDebut,
                        dateFin = a.Prs.DateFin
                    })
                    .ToListAsync();

                conflits.AddRange(conflitsGroupes);
            }

            return conflits;
        }

        private async Task<List<object>> VerifierConflitsGroupe(int groupeId, DateTime debut, DateTime fin)
        {
            var conflits = new List<object>();

            var groupe = await _context.GroupesUtilisateurs
                .Include(g => g.Membres)
                .ThenInclude(m => m.Utilisateur)
                .FirstOrDefaultAsync(g => g.Id == groupeId);

            if (groupe == null) return conflits;

            // Vérifier les conflits pour chaque membre du groupe
            foreach (var membre in groupe.Membres)
            {
                var utilisateurConflits = await VerifierConflitsUtilisateur(membre.UtilisateurId, debut, fin);
                conflits.AddRange(utilisateurConflits);
            }

            return conflits;
        }

        public async Task<IActionResult> OnPostCheckConflictsAsync()
        {
            try
            {
                var affectationsData = Request.Form["affectationsData"].ToString();
                var dateDebutStr = Request.Form["dateDebut"].ToString();
                var dateFinStr = Request.Form["dateFin"].ToString();

                if (!DateTime.TryParse(dateDebutStr, out var dateDebut) ||
                    !DateTime.TryParse(dateFinStr, out var dateFin))
                {
                    return new JsonResult(new { hasConflicts = false });
                }

                if (string.IsNullOrEmpty(affectationsData))
                {
                    return new JsonResult(new { hasConflicts = false });
                }

                var affectations = JsonSerializer.Deserialize<List<AffectationDto>>(affectationsData, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (affectations?.Any() != true)
                {
                    return new JsonResult(new { hasConflicts = false });
                }

                var conflits = new List<object>();
                foreach (var affectation in affectations)
                {
                    if (affectation.type == "Utilisateur")
                    {
                        var utilisateurConflits = await VerifierConflitsUtilisateur(affectation.id, dateDebut, dateFin);
                        conflits.AddRange(utilisateurConflits);
                    }
                    else if (affectation.type == "Groupe")
                    {
                        var groupeConflits = await VerifierConflitsGroupe(affectation.id, dateDebut, dateFin);
                        conflits.AddRange(groupeConflits);
                    }
                }

                if (conflits.Any())
                {
                    var conflitMessages = conflits.Select(c =>
                    {
                        var conflit = c as dynamic;
                        if (conflit.type == "direct")
                        {
                            return $"{conflit.utilisateur} est déjà affecté(e) à la PRS #{conflit.prsId} '{conflit.prsTitre}' du {((DateTime)conflit.dateDebut):dd/MM/yyyy HH:mm} au {((DateTime)conflit.dateFin):dd/MM/yyyy HH:mm}";
                        }
                        else if (conflit.type == "groupe")
                        {
                            return $"{conflit.utilisateur} fait partie du groupe '{conflit.groupe}' qui est déjà affecté à la PRS #{conflit.prsId} '{conflit.prsTitre}' du {((DateTime)conflit.dateDebut):dd/MM/yyyy HH:mm} au {((DateTime)conflit.dateFin):dd/MM/yyyy HH:mm}";
                        }
                        return "Conflit détecté";
                    }).Distinct().ToList();

                    return new JsonResult(new
                    {
                        hasConflicts = true,
                        conflitMessages = conflitMessages
                    });
                }

                return new JsonResult(new { hasConflicts = false });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la vérification des conflits");
                return new JsonResult(new { hasConflicts = false });
            }
        }

        // ===== AJOUT 1 : Normalisation texte équipement =====
        private string NormalizeEquipement(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();
            if (raw.Contains("CMS", StringComparison.OrdinalIgnoreCase)) return "CMS";
            if (raw.Contains("Finition", StringComparison.OrdinalIgnoreCase)) return "Finition";
            return raw;
        }

        // ===== AJOUT 2 : Endpoint AJAX pour filtrer les lignes =====
        // Appel depuis la vue : ?handler=LignesByEquipement&equipement=CMS
        public async Task<IActionResult> OnGetLignesByEquipementAsync(string equipement)
        {
            var norm = NormalizeEquipement(equipement);

            int? typeSecteur = norm switch
            {
                "CMS" => 1,
                "Finition" => 2,
                _ => null
            };

            if (!typeSecteur.HasValue)
                return new JsonResult(new { lignes = Array.Empty<object>() });

            var lignes = await (from l in _context.Lignes
                                join s in _context.Secteurs on l.IdSecteur equals s.Id
                                where l.Activation == true
                                      && s.IdTypeSecteur == typeSecteur.Value
                                      && l.Nom != null && l.Nom != ""
                                      && !l.Nom.StartsWith("CV ")
                                orderby l.Nom
                                select new { id = l.Id, nom = l.Nom })
                               .ToListAsync();

            return new JsonResult(new { lignes });
        }

        

        public async Task<IActionResult> OnPostAsync()
        {
            _logger.LogInformation($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Début de OnPostAsync");
            _logger.LogInformation($"Fichiers reçus: {UploadedFiles?.Count ?? 0}");
            _logger.LogInformation($"PrsFolderLinks reçu: {PrsFolderLinks}");
            _logger.LogInformation($"ChecklistData reçu: {ChecklistData}");
            _logger.LogInformation($"AffectationsData reçu: {AffectationsData}");
            _logger.LogInformation($"ChecklistAffectationsData reçu: {ChecklistAffectationsData}");

            await ChargerDonneesAsync();

            if (Prs.DateDebut >= Prs.DateFin)
            {
                ModelState.AddModelError(string.Empty, "La date de début doit être antérieure à la date de fin.");
            }

            if (Prs.LigneId == 0)
            {
                ModelState.AddModelError("Prs.LigneId", "La sélection d'une ligne est obligatoire.");
            }
           

            // Définir automatiquement le statut en fonction des droits de l'utilisateur
            Prs.Statut = IsAdminOrValidateur ? "Validé" : "En attente";

            if (!ModelState.IsValid)
            {
                _logger.LogWarning("Formulaire invalide. Erreurs: " +
                    string.Join("; ", ModelState.Values
                        .SelectMany(v => v.Errors)
                        .Select(e => e.ErrorMessage)));
                return Page();
            }

            try
            {
                _logger.LogInformation("Création d'une nouvelle PRS");

                Prs.DateCreation = DateTime.Now;
                Prs.DerniereModification = DateTime.Now;
                Prs.CreatedByLogin = GetCurrentUserLogin();

                Prs.Titre = CleanEmojis(Prs.Titre);
                Prs.Equipement = CleanEmojis(Prs.Equipement);
                Prs.BesoinOperateur = CleanEmojis(Prs.BesoinOperateur);
                Prs.PresenceClient = CleanEmojis(Prs.PresenceClient);

                if (!IsAdminOrValidateur && Request.Form.ContainsKey("weekMode") && Request.Form["weekMode"] == "true")
                {
                    if (Request.Form.ContainsKey("selectedWeek") &&
                        DateTime.TryParse(Request.Form["selectedWeek"], out var weekStartDate))
                    {
                        var mondayStart = GetMondayOfWeek(weekStartDate);
                        var sundayEnd = mondayStart.AddDays(7);

                        Prs.DateDebut = mondayStart;
                        Prs.DateFin = sundayEnd;
                    }
                }

                if (!IsAdminOrValidateur || string.IsNullOrWhiteSpace(Prs.CouleurPRS))
                {
                    Prs.CouleurPRS = null;
                }

                _context.Prs.Add(Prs);
                await _context.SaveChangesAsync();
                _logger.LogInformation($"PRS créée avec succès. ID: {Prs.Id}, Titre: {Prs.Titre}");

                // GESTION DES AFFECTATIONS PRS
                await TraiterAffectationsPrsAsync();

                // GESTION DE LA CHECKLIST ET SES AFFECTATIONS
                await TraiterChecklistsEtAffectationsAsync();

                // GESTION DES FICHIERS ET LIENS
                await TraiterFichiersEtLiensAsync();

                // NOTIFICATIONS (création) - après toutes les écritures
                try
                {
                    await _notificationService.EnvoyerNotificationsPRS(Prs.Id, "create");
                }
                catch (Exception notifEx)
                {
                    _logger.LogError(notifEx, "[NOTIF] Erreur notification création PRS {Id}", Prs.Id);
                }

                Flash = "PRS ajoutée avec succès ✅";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Erreur lors de l'ajout de la PRS: {ex.Message}";
                ModelState.AddModelError(string.IsNullOrEmpty(ex.Message) ? string.Empty : ex.Message, "Erreur lors de l'ajout de la PRS.");
                _logger.LogError($"Exception lors de la création de la PRS: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                return Page();
            }
        }

        private async Task TraiterAffectationsPrsAsync()
        {
            if (!string.IsNullOrEmpty(AffectationsData))
            {
                try
                {
                    _logger.LogInformation($"Traitement des affectations PRS: {AffectationsData}");

                    var affectations = JsonSerializer.Deserialize<List<AffectationDto>>(AffectationsData, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (affectations != null && affectations.Any())
                    {
                        int affectationsCount = 0;
                        foreach (var affectation in affectations)
                        {
                            var prsAffectation = new PrsAffectation
                            {
                                PrsId = Prs.Id,
                                TypeAffectation = affectation.type,
                                AffectePar = CurrentUserLogin,
                                DateAffectation = DateTime.Now
                            };

                            if (affectation.type == "Utilisateur")
                            {
                                prsAffectation.UtilisateurId = affectation.id;
                            }
                            else if (affectation.type == "Groupe")
                            {
                                prsAffectation.GroupeId = affectation.id;
                            }

                            _context.PrsAffectations.Add(prsAffectation);
                            affectationsCount++;

                            _logger.LogInformation($"Affectation PRS ajoutée: {affectation.type} ID {affectation.id} pour PRS {Prs.Id}");
                        }

                        await _context.SaveChangesAsync();
                        _logger.LogInformation($"{affectationsCount} affectation(s) PRS créée(s) pour la PRS {Prs.Id}");

                        if (affectationsCount > 0)
                        {
                            Flash += $" {affectationsCount} affectation(s) PRS créée(s).";
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Erreur lors du traitement des affectations PRS: {ex.Message}");
                    ErrorMessage += " Erreur lors de la création des affectations PRS.";
                }
            }
        }

        private async Task TraiterChecklistsEtAffectationsAsync()
        {
            if (string.IsNullOrWhiteSpace(ChecklistData))
            {
                _logger.LogInformation("Aucune donnée de checklist à traiter");
                return;
            }

            try
            {
                _logger.LogInformation($"Traitement des données de checklist: {ChecklistData}");

                var checklistForm = JsonSerializer.Deserialize<ChecklistFormDto>(ChecklistData, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (checklistForm != null)
                {
                    var userLogin = GetCurrentUserLogin();
                    var checklistIds = new List<int>();

                    switch (checklistForm.type)
                    {
                        case "modele":
                            // Si l'utilisateur a sélectionné des éléments => créer une checklist custom avec cette sélection
                            if (checklistForm.elements?.Any() == true)
                            {
                                var elements = checklistForm.elements.Select(e => new PrsChecklist
                                {
                                    Categorie = e.categorie,
                                    SousCategorie = e.sousCategorie,
                                    Libelle = e.libelle,
                                    Tache = e.libelle, // compat
                                    Priorite = e.priorite > 0 ? e.priorite : 3,
                                    DelaiDefautJours = e.delaiDefautJours > 0 ? e.delaiDefautJours : 1,
                                    Obligatoire = e.obligatoire,
                                    EstCoche = false,
                                    Statut = null
                                }).ToList();

                                var success = await _checklistService.CreateCustomChecklistAsync(Prs.Id, elements, userLogin);
                                if (success)
                                {
                                    checklistIds = await GetChecklistIdsForPrs(Prs.Id);
                                    _logger.LogInformation($"Checklist (modele/selection) créée pour PRS {Prs.Id} avec {elements.Count} éléments");
                                    Flash += " Checklist créée à partir du modèle (sélection).";
                                }
                                else
                                {
                                    _logger.LogWarning($"Échec création checklist (modele/selection) pour PRS {Prs.Id}");
                                    ErrorMessage += " Erreur lors de la création de la checklist à partir de la sélection.";
                                }
                            }
                            // Sinon: appliquer le modèle complet
                            else if (checklistForm.sourceId.HasValue)
                            {
                                var success = await _checklistService.ApplyChecklistModeleAsync(Prs.Id, checklistForm.sourceId.Value, userLogin);
                                if (success)
                                {
                                    checklistIds = await GetChecklistIdsForPrs(Prs.Id);
                                    _logger.LogInformation($"Modèle de checklist {checklistForm.sourceId.Value} appliqué (complet) au PRS {Prs.Id}");
                                    Flash += " Checklist créée à partir du modèle (complet).";
                                }
                                else
                                {
                                    _logger.LogWarning($"Échec application modèle {checklistForm.sourceId.Value}");
                                    ErrorMessage += " Erreur lors de l'application du modèle de checklist.";
                                }
                            }
                            break;

                        case "copy":
                            if (checklistForm.elements?.Any() == true)
                            {
                                var elements = checklistForm.elements.Select(e => new PrsChecklist
                                {
                                    Categorie = e.categorie,
                                    SousCategorie = e.sousCategorie,
                                    Libelle = e.libelle,
                                    Tache = e.libelle, // Compatibilité
                                    Priorite = e.priorite > 0 ? e.priorite : 3,
                                    DelaiDefautJours = e.delaiDefautJours > 0 ? e.delaiDefautJours : 1,
                                    Obligatoire = e.obligatoire,
                                    EstCoche = false,
                                    Statut = null
                                }).ToList();

                                var success = await _checklistService.CreateCustomChecklistAsync(Prs.Id, elements, userLogin);
                                if (success)
                                {
                                    // Récupérer les IDs des checklists créées
                                    checklistIds = await GetChecklistIdsForPrs(Prs.Id);
                                    _logger.LogInformation($"Checklist (mode copy/IHM) créée pour le PRS {Prs.Id} avec {elements.Count} éléments");
                                    Flash += " Checklist copiée depuis l'IHM.";
                                }
                                else
                                {
                                    _logger.LogWarning($"Échec de la création de la checklist (mode copy/IHM) pour le PRS {Prs.Id}");
                                    ErrorMessage += " Erreur lors de la création de la checklist.";
                                }
                            }
                            break;

                        case "custom":
                            if (checklistForm.elements?.Any() == true)
                            {
                                var elements = checklistForm.elements.Select(e => new PrsChecklist
                                {
                                    Categorie = e.categorie,
                                    SousCategorie = e.sousCategorie,
                                    Libelle = e.libelle,
                                    Tache = e.libelle, // Compatibilité
                                    Priorite = e.priorite > 0 ? e.priorite : 3,
                                    DelaiDefautJours = e.delaiDefautJours > 0 ? e.delaiDefautJours : 1,
                                    Obligatoire = e.obligatoire,
                                    EstCoche = false,
                                    Statut = null
                                }).ToList();

                                var success = await _checklistService.CreateCustomChecklistAsync(Prs.Id, elements, userLogin);
                                if (success)
                                {
                                    // Récupérer les IDs des checklists créées
                                    checklistIds = await GetChecklistIdsForPrs(Prs.Id);
                                    _logger.LogInformation($"Checklist personnalisée créée pour le PRS {Prs.Id} avec {elements.Count} éléments");
                                    Flash += " Checklist personnalisée créée.";
                                }
                                else
                                {
                                    _logger.LogWarning($"Échec de la création de la checklist personnalisée pour le PRS {Prs.Id}");
                                    ErrorMessage += " Erreur lors de la création de la checklist personnalisée.";
                                }
                            }
                            break;
                    }

                    // Traiter les affectations pour toutes les checklists créées
                    if (checklistIds.Any())
                    {
                        await TraiterAffectationsChecklistAsync(checklistIds);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Erreur lors du traitement des données de checklist: {ex.Message}");
                ErrorMessage += " Erreur lors de la création de la checklist.";
            }
        }

        private async Task<List<int>> GetChecklistIdsForPrs(int prsId)
        {
            return await _context.PrsChecklists
                .Where(c => c.PRSId == prsId)
                .OrderBy(c => c.Id)
                .Select(c => c.Id)
                .ToListAsync();
        }

        private async Task TraiterAffectationsChecklistAsync(List<int> checklistIds)
        {
            if (!checklistIds.Any())
            {
                _logger.LogInformation("Aucune checklist pour les affectations");
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(ChecklistData))
                {
                    _logger.LogInformation("Aucune donnée ChecklistData pour les affectations");
                    return;
                }

                _logger.LogInformation($"Analyse de ChecklistData: {ChecklistData}");

                var checklistForm = JsonSerializer.Deserialize<ChecklistFormDto>(ChecklistData, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (checklistForm?.elements == null || !checklistForm.elements.Any())
                {
                    _logger.LogInformation("Aucune tâche de checklist trouvé");
                    return;
                }

                // Vérifier que le nombre d'éléments correspond au nombre de checklists créées
                if (checklistForm.elements.Count != checklistIds.Count)
                {
                    _logger.LogWarning($"Mismatch: {checklistForm.elements.Count} éléments vs {checklistIds.Count} checklists créées");
                }

                var currentUser = CurrentUserLogin;
                var dateAffectation = DateTime.Now;
                int totalAffectations = 0;

                // Traiter chaque élément individuellement avec sa checklist correspondante
                for (int i = 0; i < Math.Min(checklistForm.elements.Count, checklistIds.Count); i++)
                {
                    var element = checklistForm.elements[i];
                    var checklistId = checklistIds[i];

                    _logger.LogInformation($"Traitement élément {i + 1}: ChecklistId={checklistId}");
                    _logger.LogInformation($"  - assignedUsers: [{string.Join(", ", element.assignedUsers ?? new List<int>())}]");
                    _logger.LogInformation($"  - assignedGroups: [{string.Join(", ", element.assignedGroups ?? new List<int>())}]");

                    // Affectations utilisateurs pour cet élément spécifique
                    if (element.assignedUsers != null && element.assignedUsers.Any())
                    {
                        foreach (var userId in element.assignedUsers)
                        {
                            var affectation = new ChecklistAffectation
                            {
                                ChecklistId = checklistId,
                                UtilisateurId = userId,
                                GroupeId = null,
                                TypeAffectation = "Utilisateur",
                                DateAffectation = dateAffectation,
                                AffectePar = currentUser
                            };

                            _context.ChecklistAffectations.Add(affectation);
                            totalAffectations++;
                            _logger.LogInformation($"Affectation utilisateur créée: ChecklistId={checklistId}, UserId={userId} (élément {i + 1})");
                        }
                    }

                    // Affectations groupes pour cet élément spécifique
                    if (element.assignedGroups != null && element.assignedGroups.Any())
                    {
                        foreach (var groupId in element.assignedGroups)
                        {
                            var affectation = new ChecklistAffectation
                            {
                                ChecklistId = checklistId,
                                UtilisateurId = null,
                                GroupeId = groupId,
                                TypeAffectation = "Groupe",
                                DateAffectation = dateAffectation,
                                AffectePar = currentUser
                            };

                            _context.ChecklistAffectations.Add(affectation);
                            totalAffectations++;
                            _logger.LogInformation($"Affectation groupe créée: ChecklistId={checklistId}, GroupId={groupId} (élément {i + 1})");
                        }
                    }

                    // Log si aucune affectation pour cet élément
                    if ((element.assignedUsers == null || !element.assignedUsers.Any()) &&
                        (element.assignedGroups == null || !element.assignedGroups.Any()))
                    {
                        _logger.LogInformation($"Aucune affectation pour l'élément {i + 1} (ChecklistId={checklistId})");
                    }
                }

                if (totalAffectations > 0)
                {
                    await _context.SaveChangesAsync();
                    _logger.LogInformation($"Total de {totalAffectations} affectations créées pour {checklistIds.Count} checklists individuelles");
                    Flash += $" {totalAffectations} affectation(s) créée(s).";
                }
                else
                {
                    _logger.LogInformation("Aucune affectation à sauvegarder");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Erreur lors de la création des affectations: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                ErrorMessage += " Erreur lors de la création des affectations.";
            }
        }

        private async Task TraiterFichiersEtLiensAsync()
        {
            // UPLOAD FICHIERS
            if (UploadedFiles != null && UploadedFiles.Any())
            {
                _logger.LogInformation($"Traitement de {UploadedFiles.Count} fichiers uploadés");

                var fileResults = await _fileService.SaveMultipleFilesAsync(
                    UploadedFiles,
                    Prs.Id.ToString(),
                    Prs.Titre ?? "PRS"
                );

                int successCount = 0;
                int errorCount = 0;

                foreach (var (Success, FilePath, ErrorMsg) in fileResults)
                {
                    if (Success && !string.IsNullOrEmpty(FilePath))
                    {
                        var file = UploadedFiles[fileResults.IndexOf((Success, FilePath, ErrorMsg))];
                        var prsFichier = new PrsFichier
                        {
                            PrsId = Prs.Id,
                            NomOriginal = file.FileName,
                            CheminFichier = FilePath,
                            TypeMime = file.ContentType,
                            Taille = file.Length,
                            DateUpload = DateTime.Now,
                            UploadParLogin = GetCurrentUserLogin()
                        };

                        _logger.LogInformation($"Ajout du fichier à la BDD: {prsFichier.NomOriginal}");
                        _context.PrsFichiers.Add(prsFichier);
                        successCount++;
                    }
                    else
                    {
                        _logger.LogError($"Erreur lors de l'upload: {ErrorMsg}");
                        errorCount++;
                        ModelState.AddModelError(string.Empty, ErrorMsg);
                    }
                }

                await _context.SaveChangesAsync();
                _logger.LogInformation($"Fichiers traités: {successCount} succès, {errorCount} erreurs");

                if (successCount > 0)
                {
                    Flash += $" {successCount} fichier(s) téléchargé(s) avec succès.";
                }

                if (errorCount > 0)
                {
                    ErrorMessage = $"{errorCount} fichier(s) n'ont pas pu être téléchargés. Vérifiez les erreurs.";
                }
            }

            // TRAITEMENT DES DOSSIERS
            if (!string.IsNullOrEmpty(PrsFolderLinks))
            {
                try
                {
                    _logger.LogInformation($"Traitement des liens de dossiers. JSON reçu: {PrsFolderLinks}");

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };

                    List<FolderLinkDto> folderLinks = null;
                    try
                    {
                        folderLinks = JsonSerializer.Deserialize<List<FolderLinkDto>>(PrsFolderLinks, options);

                        if (folderLinks != null)
                        {
                            foreach (var link in folderLinks)
                            {
                                _logger.LogInformation($"Lien désérialisé - Chemin: '{link.Chemin}', Description: '{link.Description}'");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Erreur lors de la désérialisation: {ex.Message}");

                        folderLinks = new List<FolderLinkDto>();

                        var pathRegex1 = new Regex(@"""path""\s*:\s*""([^""]+)""");
                        var pathRegex2 = new Regex(@"""Chemin""\s*:\s*""([^""]+)""");
                        var descRegex1 = new Regex(@"""description""\s*:\s*""([^""]*)""");
                        var descRegex2 = new Regex(@"""Description""\s*:\s*""([^""]*)""");

                        var pathMatches1 = pathRegex1.Matches(PrsFolderLinks);
                        var pathMatches2 = pathRegex2.Matches(PrsFolderLinks);
                        var descMatches1 = descRegex1.Matches(PrsFolderLinks);
                        var descMatches2 = descRegex2.Matches(PrsFolderLinks);

                        if (pathMatches1.Count > 0)
                        {
                            for (int i = 0; i < pathMatches1.Count; i++)
                            {
                                string path = pathMatches1[i].Groups[1].Value;
                                string desc = (i < descMatches1.Count && descMatches1[i].Groups.Count > 1) ?
                                    descMatches1[i].Groups[1].Value : "";

                                folderLinks.Add(new FolderLinkDto { Chemin = path, Description = desc });
                            }
                        }
                        else if (pathMatches2.Count > 0)
                        {
                            for (int i = 0; i < pathMatches2.Count; i++)
                            {
                                string path = pathMatches2[i].Groups[1].Value;
                                string desc = (i < descMatches2.Count && descMatches2[i].Groups.Count > 1) ?
                                    descMatches2[i].Groups[1].Value : "";

                                folderLinks.Add(new FolderLinkDto { Chemin = path, Description = desc });
                            }
                        }
                    }

                    if (folderLinks != null && folderLinks.Any())
                    {
                        int addedCount = 0;
                        foreach (var link in folderLinks)
                        {
                            if (!string.IsNullOrEmpty(link.Chemin))
                            {
                                string chemin = link.Chemin.Replace("\\\\", "\\");

                                var lienDossier = new LienDossierPrs
                                {
                                    PrsId = Prs.Id,
                                    Chemin = chemin,
                                    Description = link.Description ?? "",
                                    DateAjout = DateTime.Now,
                                    AjouteParLogin = GetCurrentUserLogin()
                                };

                                _logger.LogInformation($"Ajout du lien de dossier: {lienDossier.Chemin}, Description: {lienDossier.Description}");
                                _context.LiensDossierPrs.Add(lienDossier);
                                addedCount++;
                            }
                            else
                            {
                                _logger.LogWarning($"Lien de dossier ignoré car chemin vide. Description: {link.Description}");
                            }
                        }

                        var changesCount = await _context.SaveChangesAsync();
                        _logger.LogInformation($"SaveChanges: {changesCount} enregistrements modifiés pour les liens de dossiers");

                        if (addedCount > 0)
                        {
                            Flash += $" {addedCount} lien(s) de dossier ajouté(s).";
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Aucun lien de dossier valide trouvé dans les données JSON");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Erreur lors du traitement des liens de dossiers: {ex.Message}");
                    _logger.LogError($"Stack trace: {ex.StackTrace}");
                    ErrorMessage += " Erreur lors du traitement des liens de dossiers.";
                }
            }
        }

        // Classes DTO pour la désérialisation
        public class ChecklistAffectationsDataModel
        {
            public List<int> users { get; set; } = new List<int>();
            public List<int> groups { get; set; } = new List<int>();
        }

        // Méthodes existantes conservées...
        private async Task ChargerDonneesAsync()
        {
            ChargerFamilles();
            ChargerLignes();

            // Charger les modèles de checklist
            try
            {
                ChecklistModeles = await _checklistService.GetChecklistModelesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du chargement des modèles de checklist");
                ChecklistModeles = new List<ChecklistModele>();
            }

            // Charger les utilisateurs actifs (tri par nom puis prénom)
            try
            {
                Utilisateurs = await _context.Utilisateurs
                    .Where(u => u.DateDeleted == null)
                    .OrderBy(u => u.Nom)
                    .ThenBy(u => u.Prenom)
                    .ToListAsync();

                _logger.LogInformation($"Chargé {Utilisateurs.Count} utilisateurs");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du chargement des utilisateurs");
                Utilisateurs = new List<Utilisateur>();
            }

            // Charger les groupes actifs (tri par nom de groupe)
            try
            {
                GroupesUtilisateurs = await _context.GroupesUtilisateurs
                    .Where(g => g.Actif)
                    .Include(g => g.Membres)
                    .ThenInclude(gu => gu.Utilisateur)
                    .OrderBy(g => g.NomGroupe)
                    .ToListAsync();

                _logger.LogInformation($"Chargé {GroupesUtilisateurs.Count} groupes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors du chargement des groupes");
                GroupesUtilisateurs = new List<GroupeUtilisateurs>();
            }
        }

        private string GetCurrentUserLogin()
        {
            var fullLogin = User.Identity?.Name;

            if (string.IsNullOrEmpty(fullLogin))
                return "Utilisateur inconnu";

            var loginParts = fullLogin.Split('\\');
            return loginParts.Length > 1 ? loginParts[1] : fullLogin;
        }

        private DateTime GetMondayOfWeek(DateTime date)
        {
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-1 * diff).Date;
        }

        private string CleanEmojis(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            string cleanedText = input;

            // Conserver les lettres accentuées, ne retirer que les emojis/symboles concernés
            // 1) Emojis en paires de substituts (surrogate pairs)
            cleanedText = Regex.Replace(cleanedText, @"[\uD83C-\uDBFF][\uDC00-\uDFFF]", "");

            // 2) Variation selector-16 et Zero-Width Joiner (utilisés par les emojis)
            cleanedText = Regex.Replace(cleanedText, @"[\uFE0F\u200D]", "");

            // 3) Optionnel: retirer quelques gammes de pictogrammes (flèches/dingbats), sans toucher aux accents
            cleanedText = Regex.Replace(cleanedText, @"[\u2190-\u21FF\u2600-\u27BF]", "");

            // Remplacer l'espace insécable par un espace normal plutôt que de supprimer (préserve les mots)
            cleanedText = cleanedText.Replace('\u00A0', ' ');

            // Nettoyage du début de texte (conserve les lettres Unicode)
            cleanedText = Regex.Replace(cleanedText, @"^\s*[^\w]*\s*", "");

            // Mappages explicites des libellés s'ils contiennent des emojis en entrée
            cleanedText = cleanedText.Replace("👨‍🔧 Besoin opérateur", "Besoin opérateur")
                                     .Replace("❌ Aucun", "Aucun")
                                     .Replace("✅ Client présent", "Client présent")
                                     .Replace("❌ Client absent", "Client absent")
                                     .Replace("❓ Non spécifié", "Non spécifié");

            return cleanedText.Trim();
        }

        private bool HasRequiredRole()
        {
            try
            {
                var login = GetCurrentUserLogin();

                if (string.IsNullOrEmpty(login))
                {
                    return false;
                }

                var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login && !u.DateDeleted.HasValue);

                if (user == null)
                {
                    return false;
                }

                var droitsAutorises = new[] { "admin", "validateur" };
                var droitUser = user.Droits?.ToLower() ?? "";

                return droitsAutorises.Contains(droitUser);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Erreur vérification droits: {ex.Message}");
                return false;
            }
        }

        // ✅ NOUVEAU HANDLER : Récupérer les membres d'un groupe
        public async Task<IActionResult> OnGetGroupMembersAsync(int groupId)
        {
            try
            {
                var groupe = await _context.GroupesUtilisateurs
                    .Include(g => g.Membres)
                    .ThenInclude(m => m.Utilisateur)
                    .FirstOrDefaultAsync(g => g.Id == groupId && g.Actif);

                if (groupe == null)
                {
                    return new JsonResult(new { membres = new List<int>() });
                }

                var membresIds = groupe.Membres
                    .Where(m => m.Utilisateur != null && !m.Utilisateur.DateDeleted.HasValue)
                    .Select(m => m.UtilisateurId)
                    .ToList();

                return new JsonResult(new { membres = membresIds });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de la récupération des membres du groupe {GroupId}", groupId);
                return new JsonResult(new { membres = new List<int>() });
            }
        }
        // ✅ NOUVEAU HANDLER : Récupérer checklist + affectations d'une PRS existante
        public async Task<IActionResult> OnGetPrsChecklistWithAffectationsAsync(int prsId)
        {
            try
            {
                _logger.LogInformation("[CREATE] Chargement checklist+affectations pour PRS {PrsId}", prsId);

                // Récupérer la checklist avec ses affectations
                var checklist = await _context.PrsChecklists
                    .Where(c => c.PRSId == prsId)
                    .Select(c => new
                    {
                        id = c.Id,
                        libelle = c.Libelle,
                        categorie = c.Categorie,
                        sousCategorie = c.SousCategorie,
                        priorite = c.Priorite,
                        delaiDefautJours = c.DelaiDefautJours,
                        obligatoire = c.Obligatoire,
                        // Affectations de la tâche
                        affectations = _context.ChecklistAffectations
                            .Where(a => a.ChecklistId == c.Id)
                            .Select(a => new
                            {
                                utilisateurId = a.UtilisateurId,
                                groupeId = a.GroupeId,
                                type = a.TypeAffectation
                            })
                            .ToList()
                    })
                    .ToListAsync();

                // Récupérer les affectations PRS globales
                var prsAffectations = await _context.PrsAffectations
                    .Where(a => a.PrsId == prsId)
                    .Select(a => new
                    {
                        utilisateurId = a.UtilisateurId,
                        groupeId = a.GroupeId,
                        type = a.TypeAffectation,
                        // Infos utilisateur
                        utilisateurNom = a.Utilisateur != null
                            ? a.Utilisateur.Prenom + " " + a.Utilisateur.Nom
                            : null,
                        utilisateurEmail = a.Utilisateur != null
                            ? a.Utilisateur.Mail
                            : null,
                        utilisateurService = a.Utilisateur != null
                            ? a.Utilisateur.Service
                            : null,
                        // Infos groupe
                        groupeNom = a.Groupe != null
                            ? a.Groupe.NomGroupe
                            : null,
                        groupeMembresCount = a.Groupe != null
                            ? a.Groupe.Membres.Count
                            : 0
                    })
                    .ToListAsync();

                _logger.LogInformation("[CREATE] Trouvé {Count} tâches et {AffCount} affectations PRS",
                    checklist.Count, prsAffectations.Count);

                return new JsonResult(new
                {
                    success = true,
                    checklist = checklist,
                    prsAffectations = prsAffectations
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CREATE] Erreur chargement checklist PRS {PrsId}", prsId);
                return new JsonResult(new
                {
                    success = false,
                    message = ex.Message,
                    checklist = new List<object>(),
                    prsAffectations = new List<object>()
                });
            }
        }

        public async Task<IActionResult> OnGetUtilisateursEtGroupesAsync()
        {
            try
            {
                var utilisateurs = await _context.Utilisateurs
                    .Where(u => u.DateDeleted == null)
                    .Select(u => new
                    {
                        Id = u.Id,
                        Nom = u.Nom,
                        Prenom = u.Prenom,
                        LoginWindows = u.LoginWindows
                    })
                    .ToListAsync();

                var groupes = await _context.GroupesUtilisateurs
                    .Where(g => g.Actif)
                    .Select(g => new
                    {
                        Id = g.Id,
                        NomGroupe = g.NomGroupe,
                        Description = g.Description
                    })
                    .ToListAsync();

                return new JsonResult(new
                {
                    utilisateurs = utilisateurs,
                    groupes = groupes
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Erreur lors du chargement des utilisateurs et groupes: {ex.Message}");
                return new JsonResult(new { utilisateurs = new List<object>(), groupes = new List<object>() });
            }
        }

        private void ChargerFamilles()
        {
            try
            {
                var famillesList = new List<PrsFamille>();
                var connection = _context.Database.GetDbConnection();
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT Id, Libelle, CouleurHex 
                    FROM [PlanifPRS].[dbo].[PRS_Famille] 
                    WHERE Libelle IS NOT NULL AND Libelle != ''
                    ORDER BY Libelle";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var idObj = reader[0];
                    var libelleObj = reader[1];
                    var couleurObj = reader[2];

                    int id = 0;
                    if (idObj != null && idObj != DBNull.Value)
                    {
                        if (int.TryParse(idObj.ToString(), out int parsedId))
                        {
                            id = parsedId;
                        }
                    }

                    if (id > 0)
                    {
                        famillesList.Add(new PrsFamille
                        {
                            Id = id,
                            Libelle = libelleObj?.ToString() ?? "Sans nom",
                            CouleurHex = couleurObj?.ToString() ?? "#009dff"
                        });
                    }
                }

                Familles = famillesList;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Erreur ChargerFamilles: {ex.Message}");
                Familles = new List<PrsFamille>();
            }
        }

        private void ChargerLignes()
        {
            try
            {
                var lignesList = new List<SelectListItem>();
                var connection = _context.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT Id, Nom 
                    FROM [PlanifPRS].[dbo].[Lignes] 
                    WHERE activation = 1 AND Nom IS NOT NULL AND Nom != ''
                    ORDER BY Nom";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var idObj = reader[0];
                    var nomObj = reader[1];

                    int id = 0;
                    if (idObj != null && idObj != DBNull.Value)
                    {
                        if (int.TryParse(idObj.ToString(), out int parsedId))
                        {
                            id = parsedId;
                        }
                    }

                    if (id > 0)
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
                _logger.LogError($"Erreur ChargerLignes: {ex.Message}");
                LigneList = new SelectList(new List<SelectListItem>(), "Value", "Text");
            }
        }

        // Classes DTO conservées
        public class FolderLinkDto
        {
            public string Chemin { get; set; }
            public string Description { get; set; }
            public string path { get => Chemin; set => Chemin = value; }
            public string description { get => Description; set => Description = value; }
        }

        public class ChecklistFormDto
        {
            public string type { get; set; }
            public int? sourceId { get; set; }
            public List<ChecklistElementDto> elements { get; set; } = new();
        }

        public class ChecklistElementDto
        {
            public string categorie { get; set; }
            public string sousCategorie { get; set; }
            public string libelle { get; set; }
            public int priorite { get; set; } = 3;
            public int delaiDefautJours { get; set; } = 1;
            public bool obligatoire { get; set; }
            public List<AffectationDto> affectations { get; set; } = new();

            // AJOUTER CES PROPRIÉTÉS
            public List<int> assignedUsers { get; set; } = new();
            public List<int> assignedGroups { get; set; } = new();
        }

        public class AffectationDto
        {
            public int id { get; set; }
            public string type { get; set; } // "Utilisateur" ou "Groupe"
            public string name { get; set; }
            public string info { get; set; }
        }

        public class ChecklistAffectationDto
        {
            public int checklistId { get; set; }
            public List<AffectationDto> affectations { get; set; } = new();
        }
    }
}