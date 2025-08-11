using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PlanifPRS.Data;
using PlanifPRS.Models;
using PlanifPRS.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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

        [BindProperty] public Models.Prs Prs { get; set; }

        public SelectList LigneList { get; set; }
        public IList<PrsFamille> Familles { get; set; } = new List<PrsFamille>();
        public IList<Utilisateur> Utilisateurs { get; set; } = new List<Utilisateur>();
        public IList<GroupeUtilisateurs> GroupesUtilisateurs { get; set; } = new List<GroupeUtilisateurs>();
        public IList<ChecklistModele> ChecklistModeles { get; set; } = new List<ChecklistModele>();

        public IList<PrsFichier> PrsFichiers { get; set; } = new List<PrsFichier>();
        public IList<LienDossierPrs> LiensDossiers { get; set; } = new List<LienDossierPrs>();

        public IList<AffectationDisplay> AffectationsExistantesDisplay { get; set; } = new List<AffectationDisplay>();
        public string ChecklistInitialJson { get; set; } = "null";
        public bool HasExistingChecklist { get; private set; } = false;

        [BindProperty] public List<IFormFile> UploadedFiles { get; set; }
        [BindProperty] public string PrsFolderLinks { get; set; }
        [BindProperty] public string ChecklistData { get; set; }
        [BindProperty] public string AffectationsData { get; set; }
        [BindProperty] public string ChecklistAffectationsData { get; set; }
        [BindProperty] public string AffectationsToDelete { get; set; }

        [TempData] public string Flash { get; set; }
        [TempData] public string ErrorMessage { get; set; }

        public bool CanEditPrs { get; private set; }
        public bool IsAdminOrValidateur => HasRequiredRole();
        public string CurrentUserLogin => GetCurrentUserLogin();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            _logger.LogInformation($"[EDIT][GET] id={id} user={CurrentUserLogin}");

            Prs = await _context.Prs.FindAsync(id);
            if (Prs == null) return NotFound();

            CanEditPrs = CheckEditPermissions(Prs);
            _logger.LogInformation($"[EDIT][GET] CanEdit={CanEditPrs} IsAdminOrValidateur={IsAdminOrValidateur}");

            // Valeurs par défaut pour éviter les erreurs "required"
            AffectationsData ??= "[]";
            AffectationsToDelete ??= "[]";

            await ChargerDonneesAsync();
            await ChargerFichiersEtLiensAsync(Prs.Id);
            await ChargerAffectationsExistantesAsync(Prs.Id);

            // IMPORTANT: forcer la valeur initiale du hidden ChecklistData en mode "copy" avec source = PRS actuelle.
            ChecklistInitialJson = JsonSerializer.Serialize(new
            {
                type = "copy",
                sourceId = Prs.Id,
                elements = Array.Empty<object>()
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            // Indique s'il existe une checklist existante
            HasExistingChecklist = await _context.PrsChecklists.AnyAsync(c => c.PRSId == Prs.Id);

            return Page();
        }

        // Endpoint de PREVIEW pour le front (affiche éléments + responsables)
        public async Task<IActionResult> OnGetChecklistPreviewAsync(int prsId)
        {
            try
            {
                var prs = await _context.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == prsId);
                if (prs == null)
                {
                    return new JsonResult(new { success = false, message = "PRS introuvable.", elements = Array.Empty<object>() });
                }

                var elements = await _context.PrsChecklists
                    .Where(c => c.PRSId == prsId)
                    .OrderBy(c => c.Priorite)
                    .ThenBy(c => c.DelaiDefautJours)
                    .ThenBy(c => c.Categorie)
                    .ThenBy(c => c.SousCategorie)
                    .Select(c => new
                    {
                        c.Id,
                        c.Categorie,
                        c.SousCategorie,
                        c.Libelle,
                        c.Tache,
                        c.Priorite,
                        c.DelaiDefautJours,
                        c.Obligatoire,
                        c.EstCoche
                    })
                    .ToListAsync();

                var ids = elements.Select(e => e.Id).ToList();

                var affDict = await _context.ChecklistAffectations
                    .Where(a => ids.Contains(a.ChecklistId))
                    .GroupBy(a => a.ChecklistId)
                    .ToDictionaryAsync(
                        g => g.Key,
                        g => new
                        {
                            Users = g.Where(a => a.UtilisateurId.HasValue).Select(a => a.UtilisateurId!.Value).Distinct().OrderBy(x => x).ToList(),
                            Groups = g.Where(a => a.GroupeId.HasValue).Select(a => a.GroupeId!.Value).Distinct().OrderBy(x => x).ToList()
                        });

                var dtoElements = elements.Select(e => new
                {
                    categorie = e.Categorie,
                    sousCategorie = e.SousCategorie,
                    libelle = string.IsNullOrWhiteSpace(e.Libelle) ? e.Tache : e.Libelle,
                    tache = string.IsNullOrWhiteSpace(e.Tache) ? e.Libelle : e.Tache,
                    priorite = e.Priorite > 0 ? e.Priorite : 3,
                    delaiDefautJours = e.DelaiDefautJours > 0 ? e.DelaiDefautJours : 1,
                    obligatoire = e.Obligatoire,
                    assignedUsers = affDict.TryGetValue(e.Id, out var aff) ? aff.Users : new List<int>(),
                    assignedGroups = affDict.TryGetValue(e.Id, out var aff2) ? aff2.Groups : new List<int>()
                }).ToList();

                int total = elements.Count;
                int completed = elements.Count(i => i.EstCoche);

                return new JsonResult(new
                {
                    success = true,
                    title = prs.Titre,
                    elements = dtoElements,
                    elementsCount = total,
                    completedCount = completed
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EDIT][GET] ChecklistPreview échec pour PRS {PrsId}", prsId);
                return new JsonResult(new { success = false, elements = Array.Empty<object>() });
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // Normalisation des champs + suppression de validations parasites
            if (string.IsNullOrWhiteSpace(AffectationsData)) AffectationsData = "[]";
            if (string.IsNullOrWhiteSpace(AffectationsToDelete)) AffectationsToDelete = "[]";
            ModelState.Remove(nameof(AffectationsData));
            ModelState.Remove(nameof(AffectationsToDelete));

            var originalPrs = await _context.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == Prs.Id);
            if (originalPrs == null) return NotFound();

            CanEditPrs = CheckEditPermissions(originalPrs);
            if (!CanEditPrs)
            {
                ModelState.AddModelError(string.Empty, "Vous n'avez pas les droits nécessaires pour modifier cette PRS.");
                await ChargerDonneesAsync();
                await ChargerFichiersEtLiensAsync(Prs.Id);
                await ChargerAffectationsExistantesAsync(Prs.Id);
                return Page();
            }

            // Mode "semaine" (utilisateurs non-admin)
            if (!IsAdminOrValidateur && Request.Form.ContainsKey("weekMode") && Request.Form["weekMode"] == "true")
            {
                if (Request.Form.ContainsKey("selectedWeek") && DateTime.TryParse(Request.Form["selectedWeek"], out var weekStartDate))
                {
                    var mondayStart = GetMondayOfWeek(weekStartDate);
                    var sundayEnd = mondayStart.AddDays(7);
                    Prs.DateDebut = mondayStart;
                    Prs.DateFin = sundayEnd;
                }
            }

            if (Prs.DateDebut >= Prs.DateFin)
                ModelState.AddModelError(string.Empty, "La date de début doit être antérieure à la date de fin.");
            if (Prs.LigneId == 0)
                ModelState.AddModelError("Prs.LigneId", "La sélection d'une ligne est obligatoire.");

            if (!ModelState.IsValid)
            {
                await ChargerDonneesAsync();
                await ChargerFichiersEtLiensAsync(Prs.Id);
                await ChargerAffectationsExistantesAsync(Prs.Id);
                return Page();
            }

            try
            {
                var prsFromDb = await _context.Prs.FindAsync(Prs.Id);
                if (prsFromDb == null) return NotFound();

                var dateCreation = prsFromDb.DateCreation;
                var createdByLogin = prsFromDb.CreatedByLogin;
                var couleurOriginal = prsFromDb.CouleurPRS;

                prsFromDb.Titre = CleanEmojis(Prs.Titre);
                prsFromDb.Equipement = Prs.Equipement;
                prsFromDb.ReferenceProduit = Prs.ReferenceProduit;
                prsFromDb.Quantite = Prs.Quantite;
                prsFromDb.BesoinOperateur = Prs.BesoinOperateur;
                prsFromDb.PresenceClient = Prs.PresenceClient;
                prsFromDb.DateDebut = Prs.DateDebut;
                prsFromDb.DateFin = Prs.DateFin;
                prsFromDb.Statut = Prs.Statut;
                prsFromDb.InfoDiverses = Prs.InfoDiverses;
                prsFromDb.FamilleId = Prs.FamilleId;
                prsFromDb.LigneId = Prs.LigneId;
                prsFromDb.DerniereModification = DateTime.Now;
                prsFromDb.DateCreation = dateCreation;
                prsFromDb.CreatedByLogin = createdByLogin;

                if (!IsAdminOrValidateur) prsFromDb.CouleurPRS = couleurOriginal;
                else prsFromDb.CouleurPRS = string.IsNullOrWhiteSpace(Prs.CouleurPRS) ? null : Prs.CouleurPRS;

                await _context.SaveChangesAsync();

                await TraiterSuppressionsAffectationsPrsAsync();
                await TraiterAffectationsPrsAsync();
                await TraiterChecklistsEtAffectationsAsync();
                await TraiterFichiersEtLiensAsync();

                Flash = "PRS modifiée avec succès ✅";
                return RedirectToPage("/Index");
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!_context.Prs.Any(e => e.Id == Prs.Id)) return NotFound();
                ModelState.AddModelError(string.Empty, "Erreur de concurrence lors de la modification.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[EDIT][POST] Erreur lors de la modification PRS {Prs.Id}");
                ModelState.AddModelError(string.Empty, $"Une erreur est survenue : {ex.Message}");
            }

            await ChargerDonneesAsync();
            await ChargerFichiersEtLiensAsync(Prs.Id);
            await ChargerAffectationsExistantesAsync(Prs.Id);
            return Page();
        }

        public async Task<IActionResult> OnGetDownloadFileAsync(int id, int fileId)
        {
            var fichier = await _context.PrsFichiers.FirstOrDefaultAsync(f => f.Id == fileId && f.PrsId == id);
            if (fichier == null) return NotFound();

            try
            {
                var path = fichier.CheminFichier;
                if (!System.IO.File.Exists(path)) return NotFound();
                var contentType = string.IsNullOrWhiteSpace(fichier.TypeMime) ? "application/octet-stream" : fichier.TypeMime;
                var fileName = fichier.NomOriginal ?? System.IO.Path.GetFileName(path);
                var stream = System.IO.File.OpenRead(path);
                return File(stream, contentType, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[EDIT][GET] Erreur download fichier {fileId}");
                return NotFound();
            }
        }

        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostDeleteFileAsync(int id, int fileId)
        {
            var prs = await _context.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (prs == null) return NotFound();
            if (!CheckEditPermissions(prs)) return Forbid();

            var fichier = await _context.PrsFichiers.FirstOrDefaultAsync(f => f.Id == fileId && f.PrsId == id);
            if (fichier == null) return NotFound();

            try
            {
                if (!string.IsNullOrWhiteSpace(fichier.CheminFichier) && System.IO.File.Exists(fichier.CheminFichier))
                    System.IO.File.Delete(fichier.CheminFichier);

                _context.PrsFichiers.Remove(fichier);
                await _context.SaveChangesAsync();
                _logger.LogInformation($"[EDIT][POST] Fichier supprimé id={fileId}");
                return new OkResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[EDIT][POST] Erreur suppression fichier {fileId}");
                return StatusCode(500);
            }
        }

        public async Task<IActionResult> OnGetUtilisateursEtGroupesAsync()
        {
            try
            {
                var utilisateurs = await _context.Utilisateurs
                    .Where(u => u.DateDeleted == null)
                    .Select(u => new { Id = u.Id, Nom = u.Nom, Prenom = u.Prenom, LoginWindows = u.LoginWindows })
                    .ToListAsync();

                var groupes = await _context.GroupesUtilisateurs
                    .Where(g => g.Actif)
                    .Select(g => new { Id = g.Id, NomGroupe = g.NomGroupe, Description = g.Description })
                    .ToListAsync();

                return new JsonResult(new { utilisateurs, groupes });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EDIT][GET] Erreur chargement utilisateurs/groupes");
                return new JsonResult(new { utilisateurs = new List<object>(), groupes = new List<object>() });
            }
        }

        private async Task ChargerDonneesAsync()
        {
            await ChargerFamillesAsync();
            await ChargerLignesAsync();

            try { ChecklistModeles = await _checklistService.GetChecklistModelesAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "[EDIT] Erreur chargement modèles checklist"); ChecklistModeles = new List<ChecklistModele>(); }

            try
            {
                Utilisateurs = await _context.Utilisateurs
                    .Where(u => u.DateDeleted == null)
                    .OrderBy(u => u.Nom).ThenBy(u => u.Prenom)
                    .ToListAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "[EDIT] Erreur chargement utilisateurs"); Utilisateurs = new List<Utilisateur>(); }

            try
            {
                GroupesUtilisateurs = await _context.GroupesUtilisateurs
                    .Where(g => g.Actif)
                    .Include(g => g.Membres).ThenInclude(m => m.Utilisateur)
                    .OrderBy(g => g.NomGroupe)
                    .ToListAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "[EDIT] Erreur chargement groupes"); GroupesUtilisateurs = new List<GroupeUtilisateurs>(); }
        }

        private async Task ChargerFichiersEtLiensAsync(int prsId)
        {
            try { PrsFichiers = await _context.PrsFichiers.Where(f => f.PrsId == prsId).OrderBy(f => f.DateUpload).ToListAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "[EDIT] Erreur chargement fichiers PRS"); PrsFichiers = new List<PrsFichier>(); }

            try { LiensDossiers = await _context.LiensDossierPrs.Where(l => l.PrsId == prsId).OrderBy(l => l.DateAjout).ToListAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "[EDIT] Erreur chargement liens PRS"); LiensDossiers = new List<LienDossierPrs>(); }
        }

        private async Task ChargerAffectationsExistantesAsync(int prsId)
        {
            try
            {
                var affectations = await _context.PrsAffectations
                    .Where(a => a.PrsId == prsId)
                    .OrderBy(a => a.DateAffectation)
                    .ToListAsync();

                var usersById = Utilisateurs.ToDictionary(u => u.Id, u => u);
                var groupsById = GroupesUtilisateurs.ToDictionary(g => g.Id, g => g);

                AffectationsExistantesDisplay = affectations.Select(a =>
                {
                    if (a.UtilisateurId.HasValue && usersById.TryGetValue(a.UtilisateurId.Value, out var u))
                    {
                        return new AffectationDisplay { Id = a.Id, Type = "Utilisateur", Name = $"{u.Nom} {u.Prenom}", Info = u.Service };
                    }
                    if (a.GroupeId.HasValue && groupsById.TryGetValue(a.GroupeId.Value, out var g))
                    {
                        return new AffectationDisplay { Id = a.Id, Type = "Groupe", Name = g.NomGroupe, Info = $"{(g.Membres?.Count ?? 0)} membres" };
                    }
                    return new AffectationDisplay { Id = a.Id, Type = "Inconnu", Name = "Inconnu", Info = "" };
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EDIT] Erreur chargement affectations existantes");
                AffectationsExistantesDisplay = new List<AffectationDisplay>();
            }
        }

        private async Task TraiterSuppressionsAffectationsPrsAsync()
        {
            if (string.IsNullOrWhiteSpace(AffectationsToDelete)) return;

            try
            {
                var ids = JsonSerializer.Deserialize<List<int>>(AffectationsToDelete);
                if (ids?.Any() == true)
                {
                    var toRemove = await _context.PrsAffectations.Where(a => ids.Contains(a.Id)).ToListAsync();
                    if (toRemove.Any())
                    {
                        _context.PrsAffectations.RemoveRange(toRemove);
                        await _context.SaveChangesAsync();
                        Flash += $" {toRemove.Count} affectation(s) supprimée(s).";
                        _logger.LogInformation($"[EDIT] Affectations supprimées: {string.Join(",", ids)}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EDIT] Erreur suppression affectations PRS");
                ErrorMessage += " Erreur lors de la suppression des affectations PRS.";
            }
        }

        private async Task TraiterAffectationsPrsAsync()
        {
            if (string.IsNullOrEmpty(AffectationsData)) return;

            try
            {
                var affectations = JsonSerializer.Deserialize<List<AffectationDto>>(AffectationsData, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (affectations != null && affectations.Any())
                {
                    int affectationsCount = 0;
                    foreach (var a in affectations)
                    {
                        var prsAffectation = new PrsAffectation
                        {
                            PrsId = Prs.Id,
                            TypeAffectation = a.type,
                            AffectePar = CurrentUserLogin,
                            DateAffectation = DateTime.Now,
                            UtilisateurId = a.type == "Utilisateur" ? a.id : (int?)null,
                            GroupeId = a.type == "Groupe" ? a.id : (int?)null
                        };
                        _context.PrsAffectations.Add(prsAffectation);
                        affectationsCount++;
                    }
                    await _context.SaveChangesAsync();
                    if (affectationsCount > 0)
                    {
                        Flash += $" {affectationsCount} affectation(s) PRS créée(s).";
                        _logger.LogInformation($"[EDIT] Affectations créées: {affectationsCount}");
                    }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "[EDIT] Erreur traitement affectations PRS"); ErrorMessage += " Erreur lors de la création des affectations PRS."; }
        }

        private async Task TraiterChecklistsEtAffectationsAsync()
        {
            if (string.IsNullOrWhiteSpace(ChecklistData)) return;

            try
            {
                var checklistForm = JsonSerializer.Deserialize<ChecklistFormDto>(ChecklistData, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (checklistForm != null)
                {
                    var userLogin = CurrentUserLogin;
                    var checklistIds = new List<int>();

                    switch (checklistForm.type)
                    {
                        case "modele":
                            if (checklistForm.sourceId.HasValue)
                            {
                                var success = await _checklistService.ApplyChecklistModeleAsync(Prs.Id, checklistForm.sourceId.Value, userLogin);
                                if (success) { checklistIds = await GetChecklistIdsForPrs(Prs.Id); Flash += " Checklist créée à partir du modèle."; }
                                else { ErrorMessage += " Erreur lors de l'application du modèle de checklist."; }
                            }
                            break;

                        case "copy":
                            if (checklistForm.sourceId.HasValue)
                            {
                                // IMPORTANT: si la source est la PRS courante, on n'appelle pas la copie,
                                // on récupère les checklists existantes puis on applique les affectations postées.
                                if (checklistForm.sourceId.Value == Prs.Id)
                                {
                                    _logger.LogInformation("[EDIT] Copie ignorée car source PRS == PRS courante. Application des affectations uniquement.");
                                    checklistIds = await GetChecklistIdsForPrs(Prs.Id);
                                    Flash += " Checklist conservée (copie ignorée).";
                                }
                                else
                                {
                                    var success = await _checklistService.CopyChecklistFromPrsAsync(Prs.Id, checklistForm.sourceId.Value, userLogin);
                                    if (success) { checklistIds = await GetChecklistIdsForPrs(Prs.Id); Flash += " Checklist copiée à partir d'un autre PRS."; }
                                    else { ErrorMessage += " Erreur lors de la copie de la checklist."; }
                                }
                            }
                            break;

                        case "custom":
                            if (checklistForm.elements?.Any() == true)
                            {
                                var elements = checklistForm.elements.Select(e => new PrsChecklist
                                {
                                    PRSId = Prs.Id,
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
                                if (success) { checklistIds = await GetChecklistIdsForPrs(Prs.Id); Flash += " Checklist personnalisée créée."; }
                                else { ErrorMessage += " Erreur lors de la création de la checklist personnalisée."; }
                            }
                            break;
                    }

                    // APPLIQUER LES AFFECTATIONS POUR TOUS LES CAS (y compris copy)
                    if (checklistIds.Any())
                    {
                        await TraiterAffectationsChecklistAsync(checklistIds);
                    }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "[EDIT] Erreur traitement checklists"); ErrorMessage += " Erreur lors de la création de la checklist."; }
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
            if (!checklistIds.Any()) return;

            try
            {
                if (string.IsNullOrEmpty(ChecklistData)) return;

                var checklistForm = JsonSerializer.Deserialize<ChecklistFormDto>(ChecklistData, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (checklistForm?.elements == null || !checklistForm.elements.Any()) return;

                var currentUser = CurrentUserLogin;
                var dateAffectation = DateTime.Now;
                int totalAffectations = 0;

                for (int i = 0; i < Math.Min(checklistForm.elements.Count, checklistIds.Count); i++)
                {
                    var element = checklistForm.elements[i];
                    var checklistId = checklistIds[i];

                    if (element.assignedUsers != null && element.assignedUsers.Any())
                    {
                        foreach (var userId in element.assignedUsers.Distinct())
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
                        }
                    }

                    if (element.assignedGroups != null && element.assignedGroups.Any())
                    {
                        foreach (var groupId in element.assignedGroups.Distinct())
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
                        }
                    }
                }

                if (totalAffectations > 0)
                {
                    await _context.SaveChangesAsync();
                    Flash += $" {totalAffectations} affectation(s) créée(s).";
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "[EDIT] Erreur création affectations checklist"); ErrorMessage += " Erreur lors de la création des affectations."; }
        }

        private async Task TraiterFichiersEtLiensAsync()
        {
            if (UploadedFiles != null && UploadedFiles.Any())
            {
                var fileResults = await _fileService.SaveMultipleFilesAsync(UploadedFiles, Prs.Id.ToString(), Prs.Titre ?? "PRS");
                int successCount = 0, errorCount = 0;

                for (int i = 0; i < fileResults.Count; i++)
                {
                    var (Success, FilePath, ErrorMsg) = fileResults[i];
                    if (Success && !string.IsNullOrEmpty(FilePath))
                    {
                        var file = UploadedFiles[i];
                        var prsFichier = new PrsFichier
                        {
                            PrsId = Prs.Id,
                            NomOriginal = file.FileName,
                            CheminFichier = FilePath,
                            TypeMime = file.ContentType,
                            Taille = file.Length,
                            DateUpload = DateTime.Now,
                            UploadParLogin = CurrentUserLogin
                        };
                        _context.PrsFichiers.Add(prsFichier);
                        successCount++;
                    }
                    else { _logger.LogError($"[EDIT] Erreur upload fichier: {ErrorMsg}"); errorCount++; ModelState.AddModelError(string.Empty, ErrorMsg); }
                }

                await _context.SaveChangesAsync();
                if (successCount > 0) Flash += $" {successCount} fichier(s) téléchargé(s) avec succès.";
                if (errorCount > 0) ErrorMessage = $"{errorCount} fichier(s) n'ont pas pu être téléchargés. Vérifiez les erreurs.";
            }

            if (!string.IsNullOrEmpty(PrsFolderLinks))
            {
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                    List<FolderLinkDto> folderLinks = JsonSerializer.Deserialize<List<FolderLinkDto>>(PrsFolderLinks, options) ?? new();

                    var existing = await _context.LiensDossierPrs.Where(l => l.PrsId == Prs.Id).ToListAsync();
                    var incoming = folderLinks.Where(l => !string.IsNullOrWhiteSpace(l.Chemin))
                                     .Select(l => new { Chemin = (l.Chemin ?? "").Replace("\\\\", "\\"), Description = l.Description ?? "" })
                                     .ToList();

                    var toDelete = existing.Where(e => !incoming.Any(i => string.Equals(i.Chemin, e.Chemin, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (toDelete.Any()) _context.LiensDossierPrs.RemoveRange(toDelete);

                    foreach (var inc in incoming)
                    {
                        var found = existing.FirstOrDefault(e => string.Equals(e.Chemin, inc.Chemin, StringComparison.OrdinalIgnoreCase));
                        if (found == null)
                        {
                            _context.LiensDossierPrs.Add(new LienDossierPrs
                            {
                                PrsId = Prs.Id,
                                Chemin = inc.Chemin,
                                Description = inc.Description,
                                DateAjout = DateTime.Now,
                                AjouteParLogin = CurrentUserLogin
                            });
                        }
                        else if ((found.Description ?? "") != inc.Description)
                        {
                            found.Description = inc.Description;
                        }
                    }

                    await _context.SaveChangesAsync();
                }
                catch (Exception ex) { _logger.LogError(ex, "[EDIT] Erreur traitement liens de dossiers"); ErrorMessage += " Erreur lors du traitement des liens de dossiers."; }
            }
        }

        private async Task ChargerFamillesAsync()
        {
            try
            {
                Familles = await _context.PrsFamilles
                    .Where(f => f != null && !string.IsNullOrEmpty(f.Libelle))
                    .OrderBy(f => f.Libelle)
                    .ToListAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "[EDIT] Erreur EF familles"); Familles = new List<PrsFamille>(); }
        }

        private async Task ChargerLignesAsync()
        {
            try
            {
                var lignes = await _context.Lignes
                    .OrderBy(l => l.Nom)
                    .Select(l => new { l.Id, l.Nom })
                    .ToListAsync();

                LigneList = new SelectList(lignes, "Id", "Nom", Prs?.LigneId);
            }
            catch (Exception ex) { _logger.LogError(ex, "[EDIT] Erreur EF lignes"); LigneList = new SelectList(new List<SelectListItem>(), "Value", "Text"); }
        }

        private DateTime GetMondayOfWeek(DateTime date)
        {
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-1 * diff).Date;
        }

        private bool CheckEditPermissions(Models.Prs prs)
        {
            if (IsAdminOrValidateur) return true;

            var currentLogin = GetCurrentUserLogin();
            if (!string.IsNullOrEmpty(currentLogin) &&
                !string.IsNullOrEmpty(prs.CreatedByLogin) &&
                currentLogin.Equals(prs.CreatedByLogin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private string GetCurrentUserLogin()
        {
            var fullLogin = User.Identity?.Name;
            if (string.IsNullOrEmpty(fullLogin)) return "Utilisateur inconnu";
            var loginParts = fullLogin.Split('\\');
            return loginParts.Length > 1 ? loginParts[1] : fullLogin;
        }

        private bool HasRequiredRole()
        {
            try
            {
                var login = GetCurrentUserLogin();
                if (string.IsNullOrEmpty(login)) return false;

                var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login && !u.DateDeleted.HasValue);
                if (user == null) return false;

                var droitsAutorises = new[] { "admin", "validateur" };
                var droitUser = user.Droits?.ToLower() ?? "";
                return droitsAutorises.Contains(droitUser);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EDIT] Erreur vérification droits");
                return false;
            }
        }

        private string CleanEmojis(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            string cleanedText = Regex.Replace(input, @"[\u00A0-\u9999\uD800-\uDFFF]", "", RegexOptions.Compiled);
            cleanedText = Regex.Replace(cleanedText, @"^\s*[^\w]*\s*", "");
            cleanedText = cleanedText.Replace("👨‍🔧 Besoin opérateur", "Besoin opérateur")
                                     .Replace("❌ Aucun", "Aucun")
                                     .Replace("✅ Client présent", "Client présent")
                                     .Replace("❌ Client absent", "Client absent")
                                     .Replace("❓ Non spécifié", "Non spécifié");
            return cleanedText.Trim();
        }

        public class FolderLinkDto { public string Chemin { get; set; } public string Description { get; set; } }
        public class ChecklistFormDto { public string type { get; set; } public int? sourceId { get; set; } public List<ChecklistElementDto> elements { get; set; } = new(); }
        public class ChecklistElementDto
        {
            public string categorie { get; set; }
            public string sousCategorie { get; set; }
            public string libelle { get; set; }
            public int priorite { get; set; } = 3;
            public int delaiDefautJours { get; set; } = 1;
            public bool obligatoire { get; set; }
            public List<AffectationDto> affectations { get; set; } = new();
            public List<int> assignedUsers { get; set; } = new();
            public List<int> assignedGroups { get; set; } = new();
        }
        public class AffectationDto { public int id { get; set; } public string type { get; set; } public string name { get; set; } public string info { get; set; } }
        public class AffectationDisplay { public int Id { get; set; } public string Type { get; set; } public string Name { get; set; } public string Info { get; set; } }
    }
}