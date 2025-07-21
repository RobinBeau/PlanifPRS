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

namespace PlanifPRS.Pages
{
    public class EditModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;
        private readonly FileService _fileService;
        private readonly ChecklistService _checklistService;
        private readonly ILogger<EditModel> _logger;

        public EditModel(PlanifPrsDbContext context, FileService fileService, ChecklistService checklistService, ILogger<EditModel> logger)
        {
            _context = context;
            _fileService = fileService;
            _checklistService = checklistService;
            _logger = logger;
        }

        [BindProperty]
        public string? AffectationsData { get; set; }

        [BindProperty]
        public string ChecklistAffectationsData { get; set; }

        public IList<Utilisateur> Utilisateurs { get; set; }
        public IList<GroupeUtilisateurs> GroupesUtilisateurs { get; set; }

        [BindProperty]
        public Models.Prs Prs { get; set; }

        [BindProperty]
        public List<IFormFile> UploadedFiles { get; set; }

        [BindProperty]
        public string PrsFolderLinks { get; set; }

        [BindProperty]
        public string ChecklistData { get; set; }

        // Support pour les deux structures de vue
        public SelectList LigneList { get; set; }
        public SelectList FamillesSelectList { get; set; }
        public IList<PrsFamille> Familles { get; set; }

        // Propriété ajoutée pour les modèles de checklist
        public IList<ChecklistModele> ChecklistModeles { get; set; }

        [TempData]
        public string Flash { get; set; }

        [TempData]
        public string ErrorMessage { get; set; }

        // Propriétés pour les données existantes
        public IList<PrsFichier> ExistingFiles { get; set; }
        public IList<PrsChecklist> ExistingChecklists { get; set; }
        public IList<PrsAffectation> ExistingPrsAffectations { get; set; }
        public IList<LienDossierPrs> ExistingFolderLinks { get; set; }

        // Propriété pour indiquer si l'utilisateur peut modifier cette PRS
        public bool CanEditPrs { get; private set; }

        // Propriété pour indiquer si l'utilisateur actuel est admin ou validateur
        public bool IsAdminOrValidateur => HasRequiredRole();

        // Propriété pour obtenir le login de l'utilisateur actuel
        public string CurrentUserLogin => GetCurrentUserLogin();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            _logger.LogInformation($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Accès à Edit avec id={id} | Utilisateur: {CurrentUserLogin}");

            Prs = await _context.Prs.FindAsync(id);

            if (Prs == null)
            {
                return NotFound();
            }

            // Vérifier si l'utilisateur a les droits de modification
            CanEditPrs = CheckEditPermissions(Prs);

            _logger.LogInformation($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Utilisateur {CurrentUserLogin} accède à la PRS {id} en mode {(CanEditPrs ? "modification" : "lecture seule")}");

            // Chargement des données de base et données existantes
            await ChargerDonneesAsync();
            await ChargerDonneesExistantesAsync();

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            _logger.LogInformation($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Début de OnPostAsync Edit");
            _logger.LogInformation($"Fichiers reçus: {UploadedFiles?.Count ?? 0}");
            _logger.LogInformation($"PrsFolderLinks reçu: {PrsFolderLinks}");
            _logger.LogInformation($"ChecklistData reçu: {ChecklistData}");
            _logger.LogInformation($"AffectationsData reçu: {AffectationsData}");
            _logger.LogInformation($"ChecklistAffectationsData reçu: {ChecklistAffectationsData}");

            // Récupérer la PRS d'origine pour vérification des droits
            var originalPrs = await _context.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == Prs.Id);

            // Vérifier si la PRS existe
            if (originalPrs == null)
            {
                return NotFound();
            }

            // Vérifier les droits de modification
            CanEditPrs = CheckEditPermissions(originalPrs);

            if (!CanEditPrs)
            {
                _logger.LogWarning($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Tentative non autorisée de modification de la PRS {Prs.Id} par {CurrentUserLogin}");
                ModelState.AddModelError(string.Empty, "Vous n'avez pas les droits nécessaires pour modifier cette PRS.");
                await ChargerDonneesAsync();
                await ChargerDonneesExistantesAsync();
                return Page();
            }

            // Validation des champs obligatoires
            if (Prs.LigneId == 0)
            {
                ModelState.AddModelError("Prs.LigneId", "La sélection d'une ligne est obligatoire.");
            }

            // Si l'utilisateur n'est pas admin/validateur et qu'on est en mode semaine
            if (!IsAdminOrValidateur && Request.Form.ContainsKey("weekMode") && Request.Form["weekMode"] == "true")
            {
                // Récupérer la semaine sélectionnée
                if (Request.Form.ContainsKey("selectedWeek") &&
                    DateTime.TryParse(Request.Form["selectedWeek"], out var weekStartDate))
                {
                    // Définir la période du lundi 00:00 au lundi suivant 00:00
                    var mondayStart = GetMondayOfWeek(weekStartDate);
                    var sundayEnd = mondayStart.AddDays(7); // Lundi suivant à 00:00:00

                    Prs.DateDebut = mondayStart;
                    Prs.DateFin = sundayEnd;
                }
            }

            // Validation des dates
            if (Prs.DateDebut >= Prs.DateFin)
            {
                ModelState.AddModelError(string.Empty, "La date de début doit être antérieure à la date de fin.");
            }

            if (!ModelState.IsValid)
            {
                _logger.LogWarning("Formulaire invalide. Erreurs: " +
                    string.Join("; ", ModelState.Values
                        .SelectMany(v => v.Errors)
                        .Select(e => e.ErrorMessage)));
                await ChargerDonneesAsync();
                await ChargerDonneesExistantesAsync();
                return Page();
            }

            try
            {
                var prsFromDb = await _context.Prs.FindAsync(Prs.Id);
                if (prsFromDb == null)
                    return NotFound();

                // Préserver certaines informations de l'original
                var dateCreation = prsFromDb.DateCreation;
                var createdByLogin = prsFromDb.CreatedByLogin;
                var couleurOriginal = prsFromDb.CouleurPRS;

                // Mise à jour des champs PRS
                prsFromDb.Titre = CleanEmojis(Prs.Titre);
                prsFromDb.Equipement = CleanEmojis(Prs.Equipement);
                prsFromDb.ReferenceProduit = Prs.ReferenceProduit;
                prsFromDb.Quantite = Prs.Quantite;
                prsFromDb.BesoinOperateur = CleanEmojis(Prs.BesoinOperateur);
                prsFromDb.PresenceClient = CleanEmojis(Prs.PresenceClient);
                prsFromDb.DateDebut = Prs.DateDebut;
                prsFromDb.DateFin = Prs.DateFin;
                prsFromDb.Statut = Prs.Statut;
                prsFromDb.InfoDiverses = Prs.InfoDiverses;
                prsFromDb.FamilleId = Prs.FamilleId;
                prsFromDb.LigneId = Prs.LigneId;
                prsFromDb.DerniereModification = DateTime.Now;
                prsFromDb.DateCreation = dateCreation;
                prsFromDb.CreatedByLogin = createdByLogin;

                // Gestion de la couleur PRS
                if (!IsAdminOrValidateur)
                {
                    // Si pas admin/validateur, on conserve la couleur originale
                    prsFromDb.CouleurPRS = couleurOriginal;
                }
                else if (string.IsNullOrWhiteSpace(Prs.CouleurPRS))
                {
                    prsFromDb.CouleurPRS = null;
                }
                else
                {
                    prsFromDb.CouleurPRS = Prs.CouleurPRS;
                }

                await _context.SaveChangesAsync();
                _logger.LogInformation($"PRS {Prs.Id} mise à jour avec succès par {CurrentUserLogin}");

                // GESTION DES AFFECTATIONS PRS
                await TraiterAffectationsPrsAsync();

                // GESTION DE LA CHECKLIST ET SES AFFECTATIONS
                await TraiterChecklistsEtAffectationsAsync();

                // GESTION DES FICHIERS ET LIENS
                await TraiterFichiersEtLiensAsync();

                Flash = "PRS modifiée avec succès ✅";
                return RedirectToPage("/Index");
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!_context.Prs.Any(e => e.Id == Prs.Id))
                {
                    return NotFound();
                }
                else
                {
                    ModelState.AddModelError(string.Empty, "Erreur de concurrence lors de la modification. Un autre utilisateur a peut-être modifié cette PRS.");
                    await ChargerDonneesAsync();
                    await ChargerDonneesExistantesAsync();
                    return Page();
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Erreur lors de la modification de la PRS: {ex.Message}";
                ModelState.AddModelError(string.Empty, "Erreur lors de la modification de la PRS.");
                _logger.LogError($"Exception lors de la modification de la PRS: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                await ChargerDonneesAsync();
                await ChargerDonneesExistantesAsync();
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
                        // Supprimer les affectations existantes pour cette PRS
                        var existingAffectations = await _context.PrsAffectations
                            .Where(a => a.PrsId == Prs.Id)
                            .ToListAsync();
                        _context.PrsAffectations.RemoveRange(existingAffectations);

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
                        _logger.LogInformation($"{affectationsCount} affectation(s) PRS mise(s) à jour pour la PRS {Prs.Id}");

                        if (affectationsCount > 0)
                        {
                            Flash += $" {affectationsCount} affectation(s) PRS mise(s) à jour.";
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Erreur lors du traitement des affectations PRS: {ex.Message}");
                    ErrorMessage += " Erreur lors de la mise à jour des affectations PRS.";
                }
            }
        }

        private async Task ChargerDonneesExistantesAsync()
        {
            try
            {
                // Charger les fichiers existants
                ExistingFiles = await _context.PrsFichiers
                    .Where(f => f.PrsId == Prs.Id)
                    .OrderBy(f => f.DateUpload)
                    .ToListAsync();

                // Charger les checklists existantes avec leurs affectations
                ExistingChecklists = await _context.PrsChecklists
                    .Where(c => c.PRSId == Prs.Id)
                    .Include(c => c.Affectations)
                        .ThenInclude(a => a.Utilisateur)
                    .Include(c => c.Affectations)
                        .ThenInclude(a => a.Groupe)
                    .OrderBy(c => c.Priorite)
                    .ThenBy(c => c.Id)
                    .ToListAsync();

                // Charger les affectations PRS existantes
                ExistingPrsAffectations = await _context.PrsAffectations
                    .Where(a => a.PrsId == Prs.Id)
                    .Include(a => a.Utilisateur)
                    .Include(a => a.Groupe)
                    .OrderBy(a => a.DateAffectation)
                    .ToListAsync();

                // Charger les liens de dossiers existants
                ExistingFolderLinks = await _context.LiensDossierPrs
                    .Where(l => l.PrsId == Prs.Id)
                    .OrderBy(l => l.DateAjout)
                    .ToListAsync();

                _logger.LogInformation($"Données existantes chargées: {ExistingFiles.Count} fichiers, {ExistingChecklists.Count} checklists, {ExistingPrsAffectations.Count} affectations PRS, {ExistingFolderLinks.Count} liens dossiers");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Erreur lors du chargement des données existantes: {ex.Message}");
                ExistingFiles = new List<PrsFichier>();
                ExistingChecklists = new List<PrsChecklist>();
                ExistingPrsAffectations = new List<PrsAffectation>();
                ExistingFolderLinks = new List<LienDossierPrs>();
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
                            if (checklistForm.sourceId.HasValue)
                            {
                                // Supprimer les checklists existantes
                                await SupprimerChecklistsExistantesAsync();

                                var success = await _checklistService.ApplyChecklistModeleAsync(Prs.Id, checklistForm.sourceId.Value, userLogin);
                                if (success)
                                {
                                    checklistIds = await GetChecklistIdsForPrs(Prs.Id);
                                    _logger.LogInformation($"Modèle de checklist {checklistForm.sourceId.Value} appliqué avec succès au PRS {Prs.Id}");
                                    Flash += " Checklist mise à jour à partir du modèle.";
                                }
                                else
                                {
                                    _logger.LogWarning($"Échec de l'application du modèle de checklist {checklistForm.sourceId.Value}");
                                    ErrorMessage += " Erreur lors de l'application du modèle de checklist.";
                                }
                            }
                            break;

                        case "copy":
                            if (checklistForm.sourceId.HasValue)
                            {
                                // Supprimer les checklists existantes
                                await SupprimerChecklistsExistantesAsync();

                                var success = await _checklistService.CopyChecklistFromPrsAsync(Prs.Id, checklistForm.sourceId.Value, userLogin);
                                if (success)
                                {
                                    checklistIds = await GetChecklistIdsForPrs(Prs.Id);
                                    _logger.LogInformation($"Checklist copiée du PRS {checklistForm.sourceId.Value} vers le PRS {Prs.Id}");
                                    Flash += " Checklist mise à jour à partir d'un autre PRS.";
                                }
                                else
                                {
                                    _logger.LogWarning($"Échec de la copie de la checklist du PRS {checklistForm.sourceId.Value}");
                                    ErrorMessage += " Erreur lors de la copie de la checklist.";
                                }
                            }
                            break;

                        case "custom":
                            if (checklistForm.elements?.Any() == true)
                            {
                                // Supprimer les checklists existantes
                                await SupprimerChecklistsExistantesAsync();

                                var elements = checklistForm.elements.Select(e => new PrsChecklist
                                {
                                    Categorie = e.categorie,
                                    SousCategorie = e.sousCategorie,
                                    Libelle = e.libelle,
                                    Tache = e.libelle,
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
                                    _logger.LogInformation($"Checklist personnalisée mise à jour pour le PRS {Prs.Id} avec {elements.Count} éléments");
                                    Flash += " Checklist personnalisée mise à jour.";
                                }
                                else
                                {
                                    _logger.LogWarning($"Échec de la mise à jour de la checklist personnalisée pour le PRS {Prs.Id}");
                                    ErrorMessage += " Erreur lors de la mise à jour de la checklist personnalisée.";
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
                ErrorMessage += " Erreur lors de la mise à jour de la checklist.";
            }
        }

        private async Task SupprimerChecklistsExistantesAsync()
        {
            // Supprimer les affectations de checklist existantes
            var existingChecklistAffectations = await _context.ChecklistAffectations
                .Where(a => _context.PrsChecklists.Any(c => c.Id == a.ChecklistId && c.PRSId == Prs.Id))
                .ToListAsync();
            _context.ChecklistAffectations.RemoveRange(existingChecklistAffectations);

            // Supprimer les checklists existantes
            var existingChecklists = await _context.PrsChecklists
                .Where(c => c.PRSId == Prs.Id)
                .ToListAsync();
            _context.PrsChecklists.RemoveRange(existingChecklists);

            await _context.SaveChangesAsync();
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
                    _logger.LogInformation("Aucun élément de checklist trouvé");
                    return;
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
                        // Supprimer les liens existants
                        var existingLinks = await _context.LiensDossierPrs
                            .Where(l => l.PrsId == Prs.Id)
                            .ToListAsync();
                        _context.LiensDossierPrs.RemoveRange(existingLinks);

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
                            Flash += $" {addedCount} lien(s) de dossier mis à jour.";
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

        /// <summary>
        /// Obtient la date du lundi de la semaine contenant la date spécifiée
        /// </summary>
        private DateTime GetMondayOfWeek(DateTime date)
        {
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-1 * diff).Date; // .Date pour avoir 00:00:00
        }

        /// <summary>
        /// Vérifie si l'utilisateur actuel a le droit de modifier cette PRS
        /// </summary>
        private bool CheckEditPermissions(Models.Prs prs)
        {
            // Admin ou validateur peut toujours modifier
            if (IsAdminOrValidateur)
            {
                return true;
            }

            // Le créateur de la PRS peut la modifier
            var currentLogin = GetCurrentUserLogin();
            if (!string.IsNullOrEmpty(currentLogin) &&
                !string.IsNullOrEmpty(prs.CreatedByLogin) &&
                currentLogin.Equals(prs.CreatedByLogin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Tous les autres utilisateurs ne peuvent pas modifier
            return false;
        }

        /// <summary>
        /// Obtient le login de l'utilisateur actuel au format approprié
        /// </summary>
        private string GetCurrentUserLogin()
        {
            var fullLogin = User.Identity?.Name;

            if (string.IsNullOrEmpty(fullLogin))
                return "Utilisateur inconnu";

            // Extraction du login depuis le format domain\username
            var loginParts = fullLogin.Split('\\');
            return loginParts.Length > 1 ? loginParts[1] : fullLogin;
        }

        /// <summary>
        /// Vérification des droits utilisateur (admin ou validateur)
        /// </summary>
        private bool HasRequiredRole()
        {
            try
            {
                // Nettoyer le login comme dans votre code Users
                var login = GetCurrentUserLogin();

                if (string.IsNullOrEmpty(login))
                {
                    return false;
                }

                // Chercher l'utilisateur dans la base
                var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login && !u.DateDeleted.HasValue);

                if (user == null)
                {
                    return false;
                }

                // Vérifier les droits requis (admin ou validateur)
                var droitsAutorises = new[] { "admin", "validateur" };
                var droitUser = user.Droits?.ToLower() ?? "";

                return droitsAutorises.Contains(droitUser);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Erreur vérification droits: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Nettoie les emojis et caractères spéciaux d'une chaîne
        /// </summary>
        private string CleanEmojis(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Nettoyer les emojis
            string cleanedText = Regex.Replace(input, @"[\u00A0-\u9999\uD800-\uDFFF]", "", RegexOptions.Compiled);

            // Si le texte commence par des caractères communs avec des emojis comme "🏭 CMS" → "CMS"
            cleanedText = Regex.Replace(cleanedText, @"^\s*[^\w]*\s*", "");

            // Cas spécifiques connus
            cleanedText = cleanedText.Replace("👨‍🔧 Besoin opérateur", "Besoin opérateur");
            cleanedText = cleanedText.Replace("❌ Aucun", "Aucun");
            cleanedText = cleanedText.Replace("✅ Client présent", "Client présent");
            cleanedText = cleanedText.Replace("❌ Client absent", "Client absent");
            cleanedText = cleanedText.Replace("❓ Non spécifié", "Non spécifié");

            return cleanedText.Trim();
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

                FamillesSelectList = new SelectList(famillesList, "Id", "Libelle", Prs.FamilleId);
                Familles = famillesList;
                _logger.LogInformation($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Familles chargées via SQL (Edit): {famillesList.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Erreur SQL familles (Edit): {ex.Message}");
                FamillesSelectList = new SelectList(new List<object>(), "Id", "Libelle");
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
                            Text = nomObj?.ToString() ?? "Sans nom",
                            Selected = Prs?.LigneId == id
                        });
                    }
                }

                LigneList = new SelectList(lignesList, "Value", "Text", Prs?.LigneId);
                _logger.LogInformation($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Lignes chargées via SQL (Edit): {lignesList.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Erreur SQL lignes (Edit): {ex.Message}");
                LigneList = new SelectList(new List<SelectListItem>(), "Value", "Text");
            }
        }

        // Classes DTO pour la désérialisation
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