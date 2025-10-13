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
    public class CloneModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;
        private readonly FileService _fileService;
        private readonly ChecklistService _checklistService;
        private readonly ILogger<EditModel> _logger;
        private readonly NotificationService _notificationService; // AJOUT

        public CloneModel(PlanifPrsDbContext context, FileService fileService, ChecklistService checklistService, ILogger<EditModel> logger, NotificationService notificationService) // AJOUT param
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
            await LoadPrsParentOptionsAsync();
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

        // === NOUVEAU : Vérification disponibilité tenant compte des affectations ORIGINALES + ajouts - suppressions ===
        // On ajoute le paramètre affectationsToDelete pour pouvoir exclure celles que l'utilisateur retire visuellement.
        public async Task<IActionResult> OnGetCheckAvailabilityAsync(string dateDebut, string dateFin, string affectationsData, string affectationsToDelete)
        {
            try
            {
                if (!DateTime.TryParse(dateDebut, out var debut) || !DateTime.TryParse(dateFin, out var fin))
                {
                    return new JsonResult(new { success = false, message = "Dates invalides" });
                }

                // 1. Récupérer affectations existantes (PRS source) et les transformer en AffectationDto
                var existingAffectations = await _context.PrsAffectations
                    .Where(a => a.PrsId == Prs.Id)
                    .Select(a => new
                    {
                        AffectationId = a.Id,
                        UserId = a.UtilisateurId,
                        GroupId = a.GroupeId
                    })
                    .ToListAsync();

                // 2. Parser la liste des suppressions (IDs d'affectations existantes)
                var deletedIds = new HashSet<int>();
                if (!string.IsNullOrWhiteSpace(affectationsToDelete))
                {
                    try
                    {
                        var rawDel = affectationsToDelete.Trim();
                        if (!(rawDel.Equals("[]", StringComparison.Ordinal) || rawDel.Equals("\"[]\"", StringComparison.Ordinal) || rawDel.Equals("null", StringComparison.OrdinalIgnoreCase)))
                        {
                            var parsed = JsonSerializer.Deserialize<List<int>>(rawDel, new JsonSerializerOptions
                            {
                                NumberHandling = JsonNumberHandling.AllowReadingFromString
                            }) ?? new List<int>();
                            deletedIds = new HashSet<int>(parsed);
                        }
                    }
                    catch (Exception exDel)
                    {
                        _logger.LogWarning(exDel, "[CLONE][AVAIL] Échec parsing affectationsToDelete, on ignore les suppressions.");
                    }
                }

                // 3. Construire la base = existants - suppressions
                var baseDtos = existingAffectations
                    .Where(a => !deletedIds.Contains(a.AffectationId))
                    .Select(a => new AffectationDto
                    {
                        id = a.UserId ?? a.GroupId ?? 0,
                        type = a.UserId.HasValue ? "Utilisateur" : "Groupe",
                        name = "",
                        info = ""
                    })
                    .Where(d => d.id > 0)
                    .ToList();

                // 4. Parser les ajouts (affectationsData)
                var additions = new List<AffectationDto>();
                if (!string.IsNullOrWhiteSpace(affectationsData) && affectationsData.Trim() != "[]")
                {
                    try
                    {
                        additions = JsonSerializer.Deserialize<List<AffectationDto>>(affectationsData, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }) ?? new List<AffectationDto>();
                    }
                    catch (Exception exAdd)
                    {
                        _logger.LogWarning(exAdd, "[CLONE][AVAIL] Parsing affectationsData échoué, aucun ajout pris en compte.");
                    }
                }

                // 5. Fusion (union) base + additions sur clé type:id
                var merged = new List<AffectationDto>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void AddIfNew(AffectationDto dto)
                {
                    if (dto == null || dto.id <= 0 || string.IsNullOrWhiteSpace(dto.type)) return;
                    var key = $"{dto.type}:{dto.id}";
                    if (seen.Add(key)) merged.Add(dto);
                }
                foreach (var b in baseDtos) AddIfNew(b);
                foreach (var a in additions) AddIfNew(a);

                // 6. Détection des conflits sur la liste fusionnée
                var conflits = new List<object>();
                foreach (var aff in merged)
                {
                    if (aff.type == "Utilisateur")
                    {
                        var utilisateurConflits = await VerifierConflitsUtilisateur(aff.id, debut, fin);
                        conflits.AddRange(utilisateurConflits);
                    }
                    else if (aff.type == "Groupe")
                    {
                        var groupeConflits = await VerifierConflitsGroupe(aff.id, debut, fin);
                        conflits.AddRange(groupeConflits);
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
                _logger.LogError(ex, "[CLONE] Erreur vérification disponibilité");
                return new JsonResult(new { success = false, message = "Erreur lors de la vérification" });
            }
        }

        private async Task<List<object>> VerifierConflitsUtilisateur(int utilisateurId, DateTime debut, DateTime fin)
        {
            var conflits = new List<object>();

            var utilisateur = await _context.Utilisateurs.FindAsync(utilisateurId);
            if (utilisateur == null) return conflits;

            // Conflits directs (exclure la PRS courante pour l'édition)
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

            // Conflits via groupes
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
                                      && s.DateDeleted == null
                                      && s.IdTypeSecteur == typeSecteur.Value
                                      && l.Nom != null && l.Nom != ""
                                orderby l.Nom
                                select new { id = l.Id, nom = l.Nom })
                               .ToListAsync();

            return new JsonResult(new { lignes });
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

            // Vérification disponibilité (affectations saisies + existantes)
            if (!string.IsNullOrEmpty(AffectationsData) || !string.IsNullOrEmpty(AffectationsToDelete))
            {
                try
                {
                    // Reproduire la logique de fusion utilisée dans l'endpoint availability
                    var existingAffectations = await _context.PrsAffectations
                        .Where(a => a.PrsId == Prs.Id)
                        .Select(a => new { a.Id, a.UtilisateurId, a.GroupeId })
                        .ToListAsync();

                    var deletedIds = new HashSet<int>();
                    if (!string.IsNullOrWhiteSpace(AffectationsToDelete))
                    {
                        var rawDel = AffectationsToDelete.Trim();
                        if (!(rawDel.Equals("[]", StringComparison.Ordinal) || rawDel.Equals("\"[]\"", StringComparison.Ordinal) || rawDel.Equals("null", StringComparison.OrdinalIgnoreCase)))
                        {
                            try
                            {
                                deletedIds = new HashSet<int>(JsonSerializer.Deserialize<List<int>>(rawDel,
                                    new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString }) ?? new List<int>());
                            }
                            catch (Exception exDel) { _logger.LogWarning(exDel, "[CLONE][POST] Parsing deletions ignoré"); }
                        }
                    }

                    var baseDtos = existingAffectations
                        .Where(a => !deletedIds.Contains(a.Id))
                        .Select(a => new AffectationDto
                        {
                            id = a.UtilisateurId ?? a.GroupeId ?? 0,
                            type = a.UtilisateurId.HasValue ? "Utilisateur" : "Groupe"
                        })
                        .Where(x => x.id > 0)
                        .ToList();

                    var additions = new List<AffectationDto>();
                    if (!string.IsNullOrWhiteSpace(AffectationsData) && AffectationsData.Trim() != "[]")
                    {
                        try
                        {
                            additions = JsonSerializer.Deserialize<List<AffectationDto>>(AffectationsData,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<AffectationDto>();
                        }
                        catch (Exception exAdd) { _logger.LogWarning(exAdd, "[CLONE][POST] Parsing additions ignoré"); }
                    }

                    var merged = new List<AffectationDto>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    void AddIfNew(AffectationDto dto)
                    {
                        if (dto == null || dto.id <= 0 || string.IsNullOrWhiteSpace(dto.type)) return;
                        var key = $"{dto.type}:{dto.id}";
                        if (seen.Add(key)) merged.Add(dto);
                    }
                    foreach (var b in baseDtos) AddIfNew(b);
                    foreach (var a in additions) AddIfNew(a);

                    var conflits = new List<object>();
                    foreach (var aff in merged)
                    {
                        if (aff.type == "Utilisateur")
                        {
                            conflits.AddRange(await VerifierConflitsUtilisateur(aff.id, Prs.DateDebut, Prs.DateFin));
                        }
                        else if (aff.type == "Groupe")
                        {
                            conflits.AddRange(await VerifierConflitsGroupe(aff.id, Prs.DateDebut, Prs.DateFin));
                        }
                    }

                    if (conflits.Any())
                    {
                        var messages = conflits.Select(c =>
                        {
                            dynamic conflit = c;
                            if (conflit.type == "direct")
                            {
                                return $"⚠️ {conflit.utilisateur} est déjà affecté(e) à la PRS #{conflit.prsId} '{conflit.prsTitre}' du {((DateTime)conflit.dateDebut):dd/MM/yyyy HH:mm} au {((DateTime)conflit.dateFin):dd/MM/yyyy HH:mm}";
                            }
                            else if (conflit.type == "groupe")
                            {
                                return $"⚠️ {conflit.utilisateur} fait partie du groupe '{conflit.groupe}' déjà affecté à la PRS #{conflit.prsId} '{conflit.prsTitre}' du {((DateTime)conflit.dateDebut):dd/MM/yyyy HH:mm} au {((DateTime)conflit.dateFin):dd/MM/yyyy HH:mm}";
                            }
                            return "Conflit détecté";
                        }).Distinct();

                        foreach (var message in messages)
                        {
                            ModelState.AddModelError(string.Empty, message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[CLONE][POST] Erreur vérification conflits d'affectation");
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
                prsFromDb.Statut = IsAdminOrValidateur ? "Validé" : "En attente";
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

        public async Task<IActionResult> OnPostCreateNewAsync()
        {
            // Normalisation minimale des champs utilisés par l’IHM
            if (string.IsNullOrWhiteSpace(AffectationsData)) AffectationsData = "[]";
            ModelState.Remove(nameof(AffectationsData));
            ModelState.Remove(nameof(AffectationsToDelete)); // pas utilisé pour la création

            // Mode "semaine" (utilisateurs non-admin) — identique à OnPostAsync
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

            // Validations principales (comme Create/Edit)
            if (Prs.DateDebut >= Prs.DateFin)
                ModelState.AddModelError(string.Empty, "La date de début doit être antérieure à la date de fin.");
            if (Prs.LigneId == 0)
                ModelState.AddModelError("Prs.LigneId", "La sélection d'une ligne est obligatoire.");

            // Vérification disponibilité (utilise les affectations saisies + affectations source - suppressions)
            if (!string.IsNullOrEmpty(AffectationsData) || !string.IsNullOrEmpty(AffectationsToDelete))
            {
                try
                {
                    var srcAffFull = await _context.PrsAffectations
                        .Where(a => a.PrsId == Prs.Id)
                        .Select(a => new { a.Id, a.UtilisateurId, a.GroupeId })
                        .ToListAsync();

                    var deletedIds = new HashSet<int>();
                    if (!string.IsNullOrWhiteSpace(AffectationsToDelete))
                    {
                        var rawDel = AffectationsToDelete.Trim();
                        if (!(rawDel.Equals("[]", StringComparison.Ordinal) || rawDel.Equals("\"[]\"", StringComparison.Ordinal) || rawDel.Equals("null", StringComparison.OrdinalIgnoreCase)))
                        {
                            try
                            {
                                deletedIds = new HashSet<int>(JsonSerializer.Deserialize<List<int>>(rawDel,
                                    new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString }) ?? new List<int>());
                            }
                            catch (Exception exDel) { _logger.LogWarning(exDel, "[CLONE][POST CreateNew] Parsing deletions ignoré"); }
                        }
                    }

                    var baseDtos = srcAffFull
                        .Where(a => !deletedIds.Contains(a.Id))
                        .Select(a => new AffectationDto
                        {
                            id = a.UtilisateurId ?? a.GroupeId ?? 0,
                            type = a.UtilisateurId.HasValue ? "Utilisateur" : "Groupe"
                        })
                        .Where(x => x.id > 0)
                        .ToList();

                    var additions = new List<AffectationDto>();
                    if (!string.IsNullOrWhiteSpace(AffectationsData) && AffectationsData.Trim() != "[]")
                    {
                        try
                        {
                            additions = JsonSerializer.Deserialize<List<AffectationDto>>(AffectationsData,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<AffectationDto>();
                        }
                        catch (Exception exAdd) { _logger.LogWarning(exAdd, "[CLONE][POST CreateNew] Parsing additions ignoré"); }
                    }

                    var merged = new List<AffectationDto>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    void AddIfNew(AffectationDto dto)
                    {
                        if (dto == null || dto.id <= 0 || string.IsNullOrWhiteSpace(dto.type)) return;
                        var key = $"{dto.type}:{dto.id}";
                        if (seen.Add(key)) merged.Add(dto);
                    }
                    foreach (var b in baseDtos) AddIfNew(b);
                    foreach (var a in additions) AddIfNew(a);

                    var conflits = new List<object>();
                    foreach (var aff in merged)
                    {
                        if (aff.type == "Utilisateur")
                        {
                            conflits.AddRange(await VerifierConflitsUtilisateur(aff.id, Prs.DateDebut, Prs.DateFin));
                        }
                        else if (aff.type == "Groupe")
                        {
                            conflits.AddRange(await VerifierConflitsGroupe(aff.id, Prs.DateDebut, Prs.DateFin));
                        }
                    }

                    if (conflits.Any())
                    {
                        var messages = conflits.Select(c =>
                        {
                            dynamic conflit = c;
                            if (conflit.type == "direct")
                            {
                                return $"⚠️ {conflit.utilisateur} est déjà affecté(e) à la PRS #{conflit.prsId} '{conflit.prsTitre}' du {((DateTime)conflit.dateDebut):dd/MM/yyyy HH:mm} au {((DateTime)conflit.dateFin):dd/MM/yyyy HH:mm}";
                            }
                            else if (conflit.type == "groupe")
                            {
                                return $"⚠️ {conflit.utilisateur} fait partie du groupe '{conflit.groupe}' déjà affecté à la PRS #{conflit.prsId} '{conflit.prsTitre}' du {((DateTime)conflit.dateDebut):dd/MM/yyyy HH:mm} au {((DateTime)conflit.dateFin):dd/MM/yyyy HH:mm}";
                            }
                            return "Conflit détecté";
                        }).Distinct();

                        foreach (var message in messages)
                        {
                            ModelState.AddModelError(string.Empty, message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[CLONE][POST CreateNew] Erreur vérification conflits");
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

            if (!ModelState.IsValid)
            {
                await ChargerDonneesAsync();
                await ChargerFichiersEtLiensAsync(Prs.Id);
                await ChargerAffectationsExistantesAsync(Prs.Id);
                return Page();
            }

            try
            {
                var equip = Prs.Equipement;
                // Construire une NOUVELLE PRS à partir du formulaire
                var newPrs = new Models.Prs
                {
                    Equipement = equip,

                    // AJOUT: si c'est une Finition, on stocke le parent; sinon on force à null
                    PrsParentId = (equip == "Finition") ? Prs.PrsParentId : null,

                    Titre = CleanEmojis(Prs.Titre),
                    ReferenceProduit = Prs.ReferenceProduit,
                    Quantite = Prs.Quantite,
                    BesoinOperateur = CleanEmojis(Prs.BesoinOperateur),
                    PresenceClient = CleanEmojis(Prs.PresenceClient),
                    DateDebut = Prs.DateDebut,
                    DateFin = Prs.DateFin,
                    Statut = IsAdminOrValidateur ? "Validé" : "En attente",
                    InfoDiverses = Prs.InfoDiverses,
                    FamilleId = Prs.FamilleId,
                    LigneId = Prs.LigneId,
                    CouleurPRS = string.IsNullOrWhiteSpace(Prs.CouleurPRS) ? null : Prs.CouleurPRS,
                    DateCreation = DateTime.Now,
                    DerniereModification = DateTime.Now,
                    CreatedByLogin = GetCurrentUserLogin()
                };

                _context.Prs.Add(newPrs);
                await _context.SaveChangesAsync(); // newPrs.Id est disponible

                // Réutiliser les méthodes existantes (ciblent Prs.Id)
                var originalId = Prs.Id;
                Prs.Id = newPrs.Id;
                try
                {
                    // Nouvelle logique: construire la liste FINALE désirée = (Affectations source - suppressions) U (ajouts IHM)
                    _logger.LogInformation("[CLONE][CreateNew] Préparation affectations | AffectationsData='{Data}' | AffectationsToDelete='{Del}'", AffectationsData ?? "null", AffectationsToDelete ?? "null");

                    var srcAffFull = await _context.PrsAffectations
                        .Where(a => a.PrsId == originalId)
                        .ToListAsync();
                    _logger.LogInformation("[CLONE][CreateNew] Source affectations count={Count}", srcAffFull.Count);

                    var deletedIds = new List<int>();
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(AffectationsToDelete))
                        {
                            var raw = AffectationsToDelete.Trim();
                            if (!(raw.Equals("[]", StringComparison.Ordinal) || raw.Equals("\"[]\"", StringComparison.Ordinal) || raw.Equals("null", StringComparison.OrdinalIgnoreCase)))
                            {
                                deletedIds = JsonSerializer.Deserialize<List<int>>(raw, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString }) ?? new List<int>();
                            }
                        }
                    }
                    catch (Exception exParse)
                    {
                        _logger.LogWarning(exParse, "[CLONE][CreateNew] Parsing AffectationsToDelete échoué, on ignore les suppressions explicites.");
                    }
                    _logger.LogInformation("[CLONE][CreateNew] DeletedIds count={Count}", deletedIds.Count);

                    var baseDtos = srcAffFull
                        .Where(a => !deletedIds.Contains(a.Id))
                        .Select(a => new AffectationDto
                        {
                            id = a.UtilisateurId ?? a.GroupeId ?? 0,
                            type = a.UtilisateurId.HasValue ? "Utilisateur" : "Groupe",
                            name = "",
                            info = ""
                        })
                        .Where(x => x.id > 0)
                        .ToList();

                    _logger.LogInformation("[CLONE][CreateNew] Base (après suppressions) count={Count}", baseDtos.Count);

                    var additions = new List<AffectationDto>();
                    if (!string.IsNullOrWhiteSpace(AffectationsData) && AffectationsData.Trim() != "[]")
                    {
                        try
                        {
                            additions = JsonSerializer.Deserialize<List<AffectationDto>>(AffectationsData, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<AffectationDto>();
                        }
                        catch (Exception exAdd)
                        {
                            _logger.LogWarning(exAdd, "[CLONE][CreateNew] Parsing AffectationsData échoué, aucun ajout pris en compte.");
                            additions = new List<AffectationDto>();
                        }
                    }
                    _logger.LogInformation("[CLONE][CreateNew] Additions (IHM) count={Count}", additions.Count);

                    var merged = new List<AffectationDto>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    void addIfNew(AffectationDto dto)
                    {
                        if (dto == null || dto.id <= 0 || string.IsNullOrWhiteSpace(dto.type)) return;
                        var key = $"{dto.type}:{dto.id}";
                        if (seen.Add(key)) merged.Add(new AffectationDto { id = dto.id, type = dto.type, name = dto.name, info = dto.info });
                    }

                    foreach (var b in baseDtos) addIfNew(b);
                    foreach (var a in additions) addIfNew(a);

                    _logger.LogInformation("[CLONE][CreateNew] Fusion finale: SourceKept={BaseCount} + Additions={AddCount} => Merged={MergedCount}", baseDtos.Count, additions.Count, merged.Count);

                    AffectationsData = JsonSerializer.Serialize(merged);
                    _logger.LogInformation("[CLONE][CreateNew] AffectationsData (merged)='{Data}'", AffectationsData);

                    await SynchroniserAffectationsPrsAsync();

                    if (string.IsNullOrWhiteSpace(PrsFolderLinks))
                    {
                        var srcLinks = await _context.LiensDossierPrs.Where(l => l.PrsId == originalId).ToListAsync();
                        if (srcLinks.Any())
                        {
                            var links = srcLinks.Select(l => new FolderLinkDto { Chemin = l.Chemin, Description = l.Description }).ToList();
                            PrsFolderLinks = JsonSerializer.Serialize(links);
                        }
                    }

                    await TraiterChecklistsEtAffectationsAsync();

                    var newHasChecklist = await _context.PrsChecklists.AnyAsync(c => c.PRSId == newPrs.Id);
                    if (!newHasChecklist)
                    {
                        var srcItems = await _context.PrsChecklists
                            .Where(c => c.PRSId == originalId)
                            .OrderBy(c => c.Priorite)
                            .ThenBy(c => c.DelaiDefautJours)
                            .ThenBy(c => c.Categorie)
                            .ThenBy(c => c.SousCategorie)
                            .ToListAsync();

                        if (srcItems.Any())
                        {
                            var srcIds = srcItems.Select(i => i.Id).ToList();
                            var affs = await _context.ChecklistAffectations
                                .Where(a => srcIds.Contains(a.ChecklistId))
                                .ToListAsync();

                            var elements = srcItems.Select(i =>
                            {
                                var assignedUsers = affs.Where(a => a.ChecklistId == i.Id && a.UtilisateurId.HasValue)
                                                        .Select(a => a.UtilisateurId!.Value).Distinct().ToList();
                                var assignedGroups = affs.Where(a => a.ChecklistId == i.Id && a.GroupeId.HasValue)
                                                         .Select(a => a.GroupeId!.Value).Distinct().ToList();

                                return new ChecklistElementDto
                                {
                                    categorie = i.Categorie,
                                    sousCategorie = i.SousCategorie,
                                    libelle = string.IsNullOrWhiteSpace(i.Libelle) ? i.Tache : i.Libelle,
                                    priorite = i.Priorite > 0 ? i.Priorite : 3,
                                    delaiDefautJours = i.DelaiDefautJours > 0 ? i.DelaiDefautJours : 1,
                                    obligatoire = i.Obligatoire,
                                    assignedUsers = assignedUsers,
                                    assignedGroups = assignedGroups
                                };
                            }).ToList();

                            var dto = new ChecklistFormDto
                            {
                                type = "copy",
                                sourceId = originalId,
                                elements = elements
                            };

                            ChecklistData = JsonSerializer.Serialize(dto);
                            await TraiterChecklistsEtAffectationsAsync();
                            Flash += " Checklist copiée automatiquement.";
                        }
                    }

                    await TraiterFichiersEtLiensAsync();

                    var hasNewFiles = await _context.PrsFichiers.AnyAsync(f => f.PrsId == newPrs.Id);
                    if (!hasNewFiles)
                    {
                        var srcFiles = await _context.PrsFichiers.Where(f => f.PrsId == originalId).ToListAsync();
                        if (srcFiles.Any())
                        {
                            foreach (var f in srcFiles)
                            {
                                _context.PrsFichiers.Add(new PrsFichier
                                {
                                    PrsId = newPrs.Id,
                                    NomOriginal = f.NomOriginal,
                                    CheminFichier = f.CheminFichier,
                                    TypeMime = f.TypeMime,
                                    Taille = f.Taille,
                                    DateUpload = DateTime.Now,
                                    UploadParLogin = CurrentUserLogin
                                });
                            }
                            var count = await _context.SaveChangesAsync();
                            if (count > 0)
                                Flash += $" {srcFiles.Count} fichier(s) copiés.";
                        }
                    }
                }
                finally
                {
                    Prs.Id = originalId;
                }

                // AJOUT : notifications clonage
                try
                {
                    await _notificationService.EnvoyerNotificationsPRS(newPrs.Id, "clone");
                }
                catch (Exception notifEx)
                {
                    _logger.LogError(notifEx, "[NOTIF] Erreur notification clonage PRS {Id}", newPrs.Id);
                }

                Flash = (Flash ?? "") + " PRS créée avec succès ✅";
                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EDIT][POST CreateNew] Erreur lors de la création d'une nouvelle PRS depuis l'écran d'édition.");
                ModelState.AddModelError(string.Empty, $"Une erreur est survenue : {ex.Message}");
                await ChargerDonneesAsync();
                await ChargerFichiersEtLiensAsync(Prs.Id);
                await ChargerAffectationsExistantesAsync(Prs.Id);
                return Page();
            }
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

        private async Task SynchroniserAffectationsPrsAsync()
        {
            _logger.LogInformation("[CLONE] === DÉBUT SynchroniserAffectationsPrsAsync ===");
            _logger.LogInformation("[CLONE] AffectationsData: '{AffectationsData}'", AffectationsData);

            try
            {
                // 1. Parser les affectations désirées depuis l'IHM
                var affectationsDesired = new List<AffectationDto>();
                if (!string.IsNullOrEmpty(AffectationsData) && AffectationsData != "[]")
                {
                    affectationsDesired = JsonSerializer.Deserialize<List<AffectationDto>>(AffectationsData, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<AffectationDto>();
                }

                _logger.LogInformation("[CLONE] Affectations désirées: {Count}", affectationsDesired.Count);
                foreach (var aff in affectationsDesired)
                {
                    _logger.LogInformation("[CLONE] Désiré: {Type} ID={Id}", aff.type, aff.id);
                }

                // 2. Charger les affectations existantes en base
                var affectationsExistantes = await _context.PrsAffectations
                    .Where(a => a.PrsId == Prs.Id)
                    .ToListAsync();

                _logger.LogInformation("[CLONE] Affectations existantes en base: {Count}", affectationsExistantes.Count);
                foreach (var aff in affectationsExistantes)
                {
                    _logger.LogInformation("[CLONE] Existant: Id={Id} User={User} Group={Group}", aff.Id, aff.UtilisateurId, aff.GroupeId);
                }

                // 3. Déterminer les suppressions
                var toDelete = affectationsExistantes.Where(existing =>
                    !affectationsDesired.Any(desired =>
                        (desired.type == "Utilisateur" && existing.UtilisateurId == desired.id) ||
                        (desired.type == "Groupe" && existing.GroupeId == desired.id)
                    )).ToList();

                _logger.LogInformation("[CLONE] Affectations à supprimer: {Count}", toDelete.Count);
                foreach (var aff in toDelete)
                {
                    _logger.LogInformation("[CLONE] À supprimer: Id={Id} User={User} Group={Group}", aff.Id, aff.UtilisateurId, aff.GroupeId);
                }

                // 4. Déterminer les ajouts
                var toAdd = affectationsDesired.Where(desired =>
                    !affectationsExistantes.Any(existing =>
                        (desired.type == "Utilisateur" && existing.UtilisateurId == desired.id) ||
                        (desired.type == "Groupe" && existing.GroupeId == desired.id)
                    )).ToList();

                _logger.LogInformation("[CLONE] Affectations à ajouter: {Count}", toAdd.Count);
                foreach (var aff in toAdd)
                {
                    _logger.LogInformation("[CLONE] À ajouter: {Type} ID={Id}", aff.type, aff.id);
                }

                // 5. Appliquer
                if (toDelete.Any())
                {
                    _context.PrsAffectations.RemoveRange(toDelete);
                    _logger.LogInformation("[CLONE] Suppression de {Count} affectations", toDelete.Count);
                }

                foreach (var affectation in toAdd)
                {
                    var prsAffectation = new PrsAffectation
                    {
                        PrsId = Prs.Id,
                        TypeAffectation = affectation.type,
                        AffectePar = CurrentUserLogin,
                        DateAffectation = DateTime.Now,
                        UtilisateurId = affectation.type == "Utilisateur" ? affectation.id : (int?)null,
                        GroupeId = affectation.type == "Groupe" ? affectation.id : (int?)null
                    };
                    _context.PrsAffectations.Add(prsAffectation);
                    _logger.LogInformation("[CLONE] Ajout affectation: {Type} ID={Id}", affectation.type, affectation.id);
                }

                var changesCount = await _context.SaveChangesAsync();
                _logger.LogInformation("[CLONE] Modifications sauvegardées: {Changes} lignes affectées", changesCount);

                var totalOperations = toDelete.Count + toAdd.Count;
                if (totalOperations > 0)
                {
                    Flash += $" Affectations PRS mises à jour ({toDelete.Count} supprimées, {toAdd.Count} ajoutées).";
                }

                _logger.LogInformation("[CLONE] === FIN SynchroniserAffectationsPrsAsync ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CLONE] Erreur lors de la synchronisation des affectations PRS");
                ErrorMessage += " Erreur lors de la mise à jour des affectations PRS.";
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
                                    _logger.LogWarning("[EDIT] Type=copy mais Aucune tâche fourni par l'IHM.");
                                    ErrorMessage += " Aucune tâche de checklist à enregistrer.";
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