// Fichier d'origine + ajouts HISTORIQUE / SUPPRESSION (marqués)
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
using System.Text.Json.Serialization;
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
        private readonly NotificationService _notificationService; // AJOUT

        public EditModel(PlanifPrsDbContext context, FileService fileService, ChecklistService checklistService, ILogger<EditModel> logger, NotificationService notificationService) // AJOUT param
        {
            _context = context;
            _fileService = fileService;
            _checklistService = checklistService;
            _logger = logger;
            _notificationService = notificationService; // AJOUT
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

        // >>> HISTORIQUE >>>
        public IList<HistoriqueEdit> Historique { get; set; } = new List<HistoriqueEdit>();
        // <<< HISTORIQUE <<<

        // AJOUTER cette propriété dans la classe
        public List<PlanifPRS.Models.Prs> PrsParentOptions { get; set; } = new();

        // AJOUTER cette méthode dans la classe
        public async Task LoadPrsParentOptionsAsync()
        {
            PrsParentOptions = await _context.Prs
                .Where(p => p.Equipement == "CMS" && p.Statut != "Supprimé")
                .OrderByDescending(p => p.DateCreation)
                .ToListAsync();
        }

        // AJOUTER cette méthode de validation dans la classe
        private bool ValidatePrsParentDates()
        {
            if (Prs.Equipement == "Finition" && Prs.PrsParentId.HasValue)
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

        // AJOUTER cette méthode pour vérifier les PRS enfants
        private async Task<bool> ValidateChildrenPrsAsync()
        {
            if (Prs.Equipement == "CMS")
            {
                var enfants = await _context.Prs
                    .Where(p => p.PrsParentId == Prs.Id && p.Statut != "Supprimé")
                    .ToListAsync();

                foreach (var enfant in enfants)
                {
                    if (enfant.DateDebut < Prs.DateFin)
                    {
                        ModelState.AddModelError("Prs.DateFin",
                            $"⚠️ Attention : La PRS finition #{enfant.Id} '{enfant.Titre}' commence le {enfant.DateDebut:dd/MM/yyyy HH:mm} ce qui est avant la fin de cette PRS parent.");
                        return false;
                    }
                }
            }
            return true;
        }
        public async Task<IActionResult> OnGetAsync(int id)
        {
            _logger.LogInformation($"[EDIT][GET] id={id} user={CurrentUserLogin}");

            Prs = await _context.Prs.FindAsync(id);
            if (Prs == null) return NotFound();

            // PRS supprimée = non visible
            if (Prs.Statut == "Supprimé") return Redirect("/Index");

            CanEditPrs = CheckEditPermissions(Prs);
            _logger.LogInformation($"[EDIT][GET] CanEdit={CanEditPrs} IsAdminOrValidateur={IsAdminOrValidateur}");

            AffectationsData ??= "[]";
            AffectationsToDelete ??= "[]";

            await ChargerDonneesAsync();
            await LoadPrsParentOptionsAsync();
            await ChargerFichiersEtLiensAsync(Prs.Id);
            await ChargerAffectationsExistantesAsync(Prs.Id);

            ChecklistInitialJson = JsonSerializer.Serialize(new
            {
                type = "copy",
                sourceId = Prs.Id,
                elements = Array.Empty<object>()
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            HasExistingChecklist = await _context.PrsChecklists.AnyAsync(c => c.PRSId == Prs.Id);

            // >>> HISTORIQUE >>>
            try
            {
                Historique = await _context.HistoriqueEdit
                    .Where(h => h.PrsId == Prs.Id)
                    .OrderByDescending(h => h.DateAction)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EDIT][GET] Erreur chargement historique");
                Historique = new List<HistoriqueEdit>();
            }
            // <<< HISTORIQUE <<<

            return Page();
        }

        // (le reste de ton code original est conservé)

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

            var conflitsDirects = await _context.PrsAffectations
                .Where(a => a.UtilisateurId == utilisateurId)
                .Include(a => a.Prs)
                .Where(a => a.Prs.Id != Prs.Id && a.Prs.Statut != "Supprimé" && a.Prs.DateDebut < fin && a.Prs.DateFin > debut) // Exclusion PRS supprimées
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
                    .Where(a => a.Prs.Id != Prs.Id && a.Prs.Statut != "Supprimé" && a.Prs.DateDebut < fin && a.Prs.DateFin > debut) // Exclusion PRS supprimées
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

            foreach (var membre in groupe.Membres)
            {
                var utilisateurConflits = await VerifierConflitsUtilisateur(membre.UtilisateurId, debut, fin);
                conflits.AddRange(utilisateurConflits);
            }

            return conflits;
        }

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

            if (!string.IsNullOrEmpty(AffectationsData))
            {
                try
                {
                    var affectations = JsonSerializer.Deserialize<List<AffectationDto>>(AffectationsData, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (affectations?.Any() == true)
                    {
                        var conflits = new List<object>();
                        foreach (var affectation in affectations)
                        {
                            if (affectation.type == "Utilisateur")
                            {
                                var utilisateurConflits = await VerifierConflitsUtilisateur(affectation.id, Prs.DateDebut, Prs.DateFin);
                                conflits.AddRange(utilisateurConflits);
                            }
                            else if (affectation.type == "Groupe")
                            {
                                var groupeConflits = await VerifierConflitsGroupe(affectation.id, Prs.DateDebut, Prs.DateFin);
                                conflits.AddRange(groupeConflits);
                            }
                        }

                        if (conflits.Any())
                        {
                            var messages = conflits.Select(c =>
                            {
                                var conflit = c as dynamic;
                                if (conflit.type == "direct")
                                {
                                    return $"⚠️ {conflit.utilisateur} est déjà affecté(e) à la PRS #{conflit.prsId} '{conflit.prsTitre}' du {((DateTime)conflit.dateDebut):dd/MM/yyyy HH:mm} au {((DateTime)conflit.dateFin):dd/MM/yyyy HH:mm}";
                                }
                                else if (conflit.type == "groupe")
                                {
                                    return $"⚠️ {conflit.utilisateur} fait partie du groupe '{conflit.groupe}' qui est déjà affecté à la PRS #{conflit.prsId} '{conflit.prsTitre}' du {((DateTime)conflit.dateDebut):dd/MM/yyyy HH:mm} au {((DateTime)conflit.dateFin):dd/MM/yyyy HH:mm}";
                                }
                                return "Conflit détecté";
                            }).Distinct();

                            foreach (var message in messages)
                            {
                                ModelState.AddModelError(string.Empty, message);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erreur lors de la vérification des conflits d'affectation");
                }
            }
            if (!ValidatePrsParentDates())
            {
                await ChargerDonneesAsync();
                await LoadPrsParentOptionsAsync();
                await ChargerFichiersEtLiensAsync(Prs.Id);
                await ChargerAffectationsExistantesAsync(Prs.Id);
                return Page();
            }

            if (!await ValidateChildrenPrsAsync())
            {
                await ChargerDonneesAsync();
                await LoadPrsParentOptionsAsync();
                await ChargerFichiersEtLiensAsync(Prs.Id);
                await ChargerAffectationsExistantesAsync(Prs.Id);
                return Page();
            }
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
                if (prsFromDb.Statut == "Supprimé") return Redirect("/Index");

                // AJOUT : snapshot des utilisateurs affectés avant modifications
                var anciennesAffectations = await ExtraireUtilisateursAffectesAsync(prsFromDb.Id);

                var dateCreation = prsFromDb.DateCreation;
                var createdByLogin = prsFromDb.CreatedByLogin;
                var couleurOriginal = prsFromDb.CouleurPRS;

                string nouveauStatut;
                DateTime? nouvelleAncienneDateDebut = prsFromDb.AncienneDateDebut;
                DateTime? nouvelleAncienneDateFin = prsFromDb.AncienneDateFin;

                if (IsAdminOrValidateur)
                {
                    nouveauStatut = "Validé";
                    if (prsFromDb.Statut == "À re-valider")
                    {
                        nouvelleAncienneDateDebut = null;
                        nouvelleAncienneDateFin = null;
                    }
                }
                else
                {
                    if (prsFromDb.Statut == "Validé")
                    {
                        nouveauStatut = "À re-valider";
                        nouvelleAncienneDateDebut = prsFromDb.DateDebut;
                        nouvelleAncienneDateFin = prsFromDb.DateFin;
                    }
                    else if (prsFromDb.Statut == "À re-valider")
                    {
                        nouveauStatut = "À re-valider";
                    }
                    else
                    {
                        nouveauStatut = "En attente";
                    }
                }

                prsFromDb.Titre = CleanEmojis(Prs.Titre);
                prsFromDb.Equipement = Prs.Equipement;
                if (Prs.Equipement == "Finition")
                {
                    prsFromDb.PrsParentId = Prs.PrsParentId; // peut être null si aucune sélection
                }
                else
                {
                    prsFromDb.PrsParentId = null; // on nettoie si ce n’est pas une Finition
                }
                prsFromDb.ReferenceProduit = Prs.ReferenceProduit;
                prsFromDb.Quantite = Prs.Quantite;
                prsFromDb.BesoinOperateur = Prs.BesoinOperateur;
                prsFromDb.PresenceClient = Prs.PresenceClient;
                prsFromDb.DateDebut = Prs.DateDebut;
                prsFromDb.DateFin = Prs.DateFin;
                prsFromDb.Statut = nouveauStatut;
                prsFromDb.AncienneDateDebut = nouvelleAncienneDateDebut;
                prsFromDb.AncienneDateFin = nouvelleAncienneDateFin;
                prsFromDb.InfoDiverses = Prs.InfoDiverses;
                prsFromDb.FamilleId = Prs.FamilleId;
                prsFromDb.LigneId = Prs.LigneId;
                prsFromDb.DerniereModification = DateTime.Now;
                prsFromDb.DateCreation = dateCreation;
                prsFromDb.CreatedByLogin = createdByLogin;

                if (!IsAdminOrValidateur) prsFromDb.CouleurPRS = couleurOriginal;
                else prsFromDb.CouleurPRS = string.IsNullOrWhiteSpace(Prs.CouleurPRS) ? null : Prs.CouleurPRS;

                // >>> HISTORIQUE >>> Diff avant Save
                var diffJson = BuildDiffJson(originalPrs, prsFromDb);
                // <<< HISTORIQUE <<<

                await _context.SaveChangesAsync();

                await TraiterSuppressionsAffectationsPrsAsync();
                await TraiterAffectationsPrsAsync();
                await TraiterChecklistsEtAffectationsAsync();
                await TraiterFichiersEtLiensAsync();

                // AJOUT : notifications après modifications complètes
                try
                {
                    await _notificationService.EnvoyerNotificationsPRS(prsFromDb.Id, "edit", anciennesAffectations);
                }
                catch (Exception notifEx)
                {
                    _logger.LogError(notifEx, "[NOTIF] Erreur notification édition PRS {Id}", prsFromDb.Id);
                }

                // >>> HISTORIQUE >>> log si modifications
                if (!string.IsNullOrWhiteSpace(diffJson) && diffJson != "{}")
                {
                    await LogHistoriqueAsync(prsFromDb.Id, "Modification", originalPrs.Statut, prsFromDb.Statut, diffJson);
                }
                // <<< HISTORIQUE <<<

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

        // >>> HISTORIQUE >>> Suppression avec log
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OnPostSupprimerAsync(int id)
        {
            try
            {
                var prs = await _context.Prs.FirstOrDefaultAsync(p => p.Id == id);
                if (prs == null) return NotFound();

                bool autorise = IsAdminOrValidateur ||
                                (!string.IsNullOrEmpty(prs.CreatedByLogin) &&
                                 prs.CreatedByLogin.Equals(CurrentUserLogin, StringComparison.OrdinalIgnoreCase));
                if (!autorise) return Forbid();

                if (prs.Statut == "Supprimé") return Redirect("/Index");

                var ancienStatut = prs.Statut;
                prs.Statut = "Supprimé";
                prs.DerniereModification = DateTime.Now;

                var diff = "{\"Statut\":{\"old\":\"" + (ancienStatut ?? "") + "\",\"new\":\"Supprimé\"}}";

                await _context.SaveChangesAsync();
                await LogHistoriqueAsync(prs.Id, "Suppression", ancienStatut, "Supprimé", diff);
                Flash = "PRS supprimée.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EDIT][POST] Erreur suppression PRS {Id}", id);
                ErrorMessage = "Erreur lors de la suppression.";
            }

            return Redirect("/Index");
        }
        // <<< HISTORIQUE <<<
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
            var raw = AffectationsToDelete.Trim();
            if (raw.Equals("[]", StringComparison.Ordinal) || raw.Equals("\"[]\"", StringComparison.Ordinal) || raw.Equals("null", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                _logger.LogInformation("[EDIT] AffectationsToDelete brut: {raw}", raw);

                var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString };

                List<int>? ids = null;

                if (raw.StartsWith("{"))
                {
                    var wrapper = JsonSerializer.Deserialize<Dictionary<string, List<int>>>(raw, options);
                    if (wrapper != null)
                    {
                        if (wrapper.TryGetValue("ids", out var list1)) ids = list1;
                        else if (wrapper.TryGetValue("affectations", out var list2)) ids = list2;
                    }
                }
                else
                {
                    ids = JsonSerializer.Deserialize<List<int>>(raw, options);
                }

                if (ids == null || ids.Count == 0)
                {
                    _logger.LogInformation("[EDIT] Aucun ID d'affectation à supprimer après parsing.");
                    return;
                }

                var existingIds = await _context.PrsAffectations
                    .Where(a => a.PrsId == Prs.Id)
                    .Select(a => a.Id)
                    .ToListAsync();

                if (existingIds.Count == 0)
                {
                    _logger.LogInformation("[EDIT] Aucune affectation en base pour PRS {PrsId}", Prs.Id);
                    return;
                }

                var toDeleteIds = existingIds.Intersect(ids).ToList();
                if (toDeleteIds.Count == 0)
                {
                    _logger.LogWarning("[EDIT] Aucun ID à supprimer ne correspond aux affectations de la PRS {PrsId}. Demandés={Asked}, Existants={Existing}",
                        Prs.Id, string.Join(",", ids), string.Join(",", existingIds));
                    return;
                }

                foreach (var id in toDeleteIds)
                {
                    _context.PrsAffectations.Remove(new PrsAffectation { Id = id });
                }

                var count = await _context.SaveChangesAsync();
                Flash += $" {toDeleteIds.Count} affectation(s) supprimée(s).";
                _logger.LogInformation("[EDIT] Suppression OK. PRS={PrsId}, DeletedIds={Ids}, Rows={Rows}", Prs.Id, string.Join(",", toDeleteIds), count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EDIT] Erreur suppression affectations PRS | Reçu={raw}", AffectationsToDelete);
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
                            {
                                if (checklistForm.elements?.Any() == true)
                                {
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

                                    _logger.LogInformation("[EDIT] Appel CreateCustomChecklistAsync | PRS: {prsId} | User: {user} | Elements: {count}",
                                        Prs.Id, userLogin, elements.Count);

                                    var success = await _checklistService.CreateCustomChecklistAsync(Prs.Id, elements, userLogin);

                                    if (success)
                                    {
                                        checklistIds = await GetChecklistIdsForPrs(Prs.Id);
                                        _logger.LogInformation("[EDIT] CreateCustomChecklistAsync OK | PRS: {prsId} | Checklists créées: {nb}",
                                            Prs.Id, checklistIds.Count);
                                        Flash += " Checklist copiée depuis l'IHM.";
                                    }
                                    else
                                    {
                                        _logger.LogError("[EDIT] CreateCustomChecklistAsync a renvoyé false sans exception | PRS: {prsId} | Elements: {count}", Prs.Id, elements.Count);
                                        ErrorMessage += " Erreur lors de la création de la checklist (service a retourné false).";
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("[EDIT] Type=copy mais aucun élément fourni par l'IHM.");
                                    ErrorMessage += " Aucun élément de checklist à enregistrer.";
                                }
                                break;
                            }

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

        // >>> HISTORIQUE >>> Méthodes diff et log
        private string BuildDiffJson(Models.Prs original, Models.Prs updated)
        {
            if (original == null || updated == null) return "{}";

            var diffs = new Dictionary<string, object>();

            void Compare<T>(string name, T oldVal, T newVal)
            {
                if (EqualityComparer<T>.Default.Equals(oldVal, newVal)) return;
                diffs[name] = new { old = oldVal, @new = newVal };
            }

            Compare("Titre", original.Titre, updated.Titre);
            Compare("Equipement", original.Equipement, updated.Equipement);
            Compare("ReferenceProduit", original.ReferenceProduit, updated.ReferenceProduit);
            Compare("Quantite", original.Quantite, updated.Quantite);
            Compare("BesoinOperateur", original.BesoinOperateur, updated.BesoinOperateur);
            Compare("PresenceClient", original.PresenceClient, updated.PresenceClient);
            Compare("DateDebut", original.DateDebut.ToString("o"), updated.DateDebut.ToString("o"));
            Compare("DateFin", original.DateFin.ToString("o"), updated.DateFin.ToString("o"));
            Compare("Statut", original.Statut, updated.Statut);
            if (original.AncienneDateDebut.HasValue || updated.AncienneDateDebut.HasValue)
                Compare("AncienneDateDebut",
                    original.AncienneDateDebut?.ToString("o"),
                    updated.AncienneDateDebut?.ToString("o"));
            if (original.AncienneDateFin.HasValue || updated.AncienneDateFin.HasValue)
                Compare("AncienneDateFin",
                    original.AncienneDateFin?.ToString("o"),
                    updated.AncienneDateFin?.ToString("o"));
            Compare("InfoDiverses", original.InfoDiverses, updated.InfoDiverses);
            Compare("FamilleId", original.FamilleId, updated.FamilleId);
            Compare("LigneId", original.LigneId, updated.LigneId);
            Compare("CouleurPRS", original.CouleurPRS, updated.CouleurPRS);

            if (diffs.Count == 0) return "{}";

            return JsonSerializer.Serialize(diffs, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });
        }

        private async Task LogHistoriqueAsync(int prsId, string action, string ancienStatut, string nouveauStatut, string diffJson)
        {
            try
            {
                var entry = new HistoriqueEdit
                {
                    PrsId = prsId,
                    Action = action,
                    AncienStatut = ancienStatut,
                    NouveauStatut = nouveauStatut,
                    UserLogin = CurrentUserLogin,
                    DateAction = DateTime.Now,
                    Changements = string.IsNullOrWhiteSpace(diffJson) ? "{}" : diffJson
                };
                _context.HistoriqueEdit.Add(entry);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HISTORIQUE] Erreur lors de l'enregistrement de l'historique PRS {PrsId}", prsId);
            }
        }
        // <<< HISTORIQUE <<<

        // AJOUT : extraction utilisateurs affectés (direct + via groupes)
        private async Task<List<int>> ExtraireUtilisateursAffectesAsync(int prsId)
        {
            var direct = await _context.PrsAffectations
                .Where(a => a.PrsId == prsId && a.TypeAffectation == "Utilisateur" && a.UtilisateurId.HasValue)
                .Select(a => a.UtilisateurId!.Value)
                .ToListAsync();

            var groupeIds = await _context.PrsAffectations
                .Where(a => a.PrsId == prsId && a.TypeAffectation == "Groupe" && a.GroupeId.HasValue)
                .Select(a => a.GroupeId!.Value)
                .Distinct()
                .ToListAsync();

            List<int> viaGroupes = new();
            if (groupeIds.Any())
            {
                viaGroupes = await _context.GroupesUtilisateurs
                    .Where(g => groupeIds.Contains(g.Id))
                    .SelectMany(g => g.Membres.Select(m => m.UtilisateurId))
                    .ToListAsync();
            }

            return direct.Concat(viaGroupes).Distinct().ToList();
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

            string cleanedText = input;

            cleanedText = Regex.Replace(cleanedText, @"[\uD83C-\uDBFF][\uDC00-\uDFFF]", "");
            cleanedText = Regex.Replace(cleanedText, @"[\uFE0F\u200D]", "");
            cleanedText = Regex.Replace(cleanedText, @"[\u2190-\u21FF\u2600-\u27BF]", "");
            cleanedText = cleanedText.Replace('\u00A0', ' ');
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