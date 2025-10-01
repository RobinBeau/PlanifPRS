using Microsoft.AspNetCore.Mvc;
using PlanifPRS.Services;
using PlanifPRS.Models;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace PlanifPRS.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ChecklistsController : ControllerBase
    {
        private readonly ChecklistService _checklistService;
        private readonly PlanifPrsDbContext _context;

        public ChecklistsController(ChecklistService checklistService, PlanifPrsDbContext context)
        {
            _checklistService = checklistService;
            _context = context;
        }

        // Liste des modèles (on peut laisser tel quel, j'ajoute juste un compteur des éléments avec GroupeId si utile)
        [HttpGet("modeles")]
        public async Task<IActionResult> GetChecklistModeles()
        {
            try
            {
                var modeles = await _checklistService.GetChecklistModelesAsync();

                var result = modeles.Select(m => new
                {
                    m.Id,
                    m.Nom,
                    m.Description,
                    m.FamilleEquipement,
                    m.DateCreation,
                    m.CreatedByLogin,
                    m.NombreElements,
                    m.NombreElementsObligatoires,
                    FamilleAffichage = m.FamilleAffichage,
                    AssignedGroupElements = m.Elements.Count(e => e.GroupeId.HasValue) // nouveau indicateur
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Erreur lors de la récupération des modèles: {ex.Message}" });
            }
        }

        // Détail d'un modèle avec éléments + groupeId/groupeNom
        // IMPORTANT : renvoyer "elements" (minuscule) pour coller au JS
        [HttpGet("modeles/{id}")]
        public async Task<IActionResult> GetChecklistModele(int id)
        {
            try
            {
                // On ne passe pas par le service pour garantir le Include sur Groupe
                var modele = await _context.ChecklistModeles
                    .AsNoTracking()
                    .Include(cm => cm.Elements)
                        .ThenInclude(e => e.Groupe)
                    .FirstOrDefaultAsync(cm => cm.Id == id);

                if (modele == null)
                    return NotFound(new { message = "Modèle de checklist non trouvé" });

                var result = new
                {
                    id = modele.Id,
                    nom = modele.Nom,
                    description = modele.Description,
                    familleEquipement = modele.FamilleEquipement,
                    dateCreation = modele.DateCreation,
                    createdByLogin = modele.CreatedByLogin,
                    actif = modele.Actif,
                    elements = modele.Elements
                        .OrderBy(e => e.Priorite)
                        .ThenBy(e => e.DelaiDefautJours)
                        .ThenBy(e => e.Categorie)
                        .Select(e => new
                        {
                            id = e.Id,
                            categorie = e.Categorie,
                            sousCategorie = e.SousCategorie,
                            libelle = e.Libelle,
                            priorite = e.Priorite,
                            delaiDefautJours = e.DelaiDefautJours,
                            obligatoire = e.Obligatoire,
                            groupeId = e.GroupeId,                             // <— AJOUT
                            groupeNom = e.Groupe != null ? e.Groupe.NomGroupe : null, // <— AJOUT
                            // Champs "affichage" si ton modèle (ou DTO) les calcule (je préserve ceux de ta version)
                            categorieComplete = (e as IChecklistElementModeleMeta)?.CategorieComplete ?? null,
                            prioriteLibelle = (e as IChecklistElementModeleMeta)?.PrioriteLibelle ?? PrioriteToLabel(e.Priorite),
                            couleurPriorite = (e as IChecklistElementModeleMeta)?.CouleurPriorite ?? PrioriteToColor(e.Priorite)
                        })
                        .ToList()
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Erreur lors de la récupération du modèle: {ex.Message}" });
            }
        }

        // Checklist d'une PRS
        [HttpGet("prs/{prsId}")]
        public async Task<IActionResult> GetPrsChecklist(int prsId)
        {
            try
            {
                var checklist = await _checklistService.GetPrsChecklistAsync(prsId);

                var result = new
                {
                    prsId,
                    checklist = checklist.Select(c => new
                    {
                        c.Id,
                        c.Categorie,
                        c.SousCategorie,
                        c.Libelle,
                        c.Tache,
                        c.Priorite,
                        c.DelaiDefautJours,
                        c.Obligatoire,
                        c.EstCoche,
                        c.Statut,
                        c.Commentaire,
                        c.DateValidation,
                        c.ValidePar,
                        LibelleAffichage = c.LibelleAffichage,
                        StatutAffichage = c.StatutAffichage,
                        CssClass = c.CssClass,
                        PrioriteLibelle = c.PrioriteLibelle,
                        assignedUsers = c.Affectations
                            .Where(a => a.TypeAffectation == "Utilisateur" && a.UtilisateurId.HasValue)
                            .Select(a => a.UtilisateurId!.Value)
                            .ToList(),
                        assignedGroups = c.Affectations
                            .Where(a => a.TypeAffectation == "Groupe" && a.GroupeId.HasValue)
                            .Select(a => a.GroupeId!.Value)
                            .ToList()
                    }).ToList()
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Erreur lors de la récupération de la checklist: {ex.Message}" });
            }
        }

        [HttpPost("prs/{prsId}/copy/{sourcePrsId}")]
        public async Task<IActionResult> CopyChecklistFromPrs(int prsId, int sourcePrsId)
        {
            try
            {
                var userLogin = GetCurrentUserLogin();
                var success = await _checklistService.CopyChecklistFromPrsAsync(prsId, sourcePrsId, userLogin);

                if (!success)
                    return BadRequest(new { message = "Erreur lors de la copie de la checklist" });

                var checklist = await _checklistService.GetPrsChecklistAsync(prsId);
                return Ok(new
                {
                    message = "Checklist copiée avec succès",
                    checklist = checklist.Select(c => new
                    {
                        c.Id,
                        c.Categorie,
                        c.SousCategorie,
                        c.Libelle,
                        c.Tache,
                        c.Priorite,
                        c.DelaiDefautJours,
                        c.Obligatoire,
                        c.EstCoche,
                        c.Statut,
                        c.Commentaire,
                        LibelleAffichage = c.LibelleAffichage,
                        StatutAffichage = c.StatutAffichage,
                        CssClass = c.CssClass,
                        assignedUsers = c.Affectations
                            .Where(a => a.TypeAffectation == "Utilisateur" && a.UtilisateurId.HasValue)
                            .Select(a => a.UtilisateurId!.Value)
                            .ToList(),
                        assignedGroups = c.Affectations
                            .Where(a => a.TypeAffectation == "Groupe" && a.GroupeId.HasValue)
                            .Select(a => a.GroupeId!.Value)
                            .ToList()
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Erreur lors de la copie: {ex.Message}" });
            }
        }

        [HttpPost("prs/{prsId}/custom")]
        public async Task<IActionResult> CreateCustomChecklist(int prsId, [FromBody] System.Collections.Generic.List<PrsChecklistCreateDto> elements)
        {
            try
            {
                var userLogin = GetCurrentUserLogin();

                var checklistElements = elements.Select(e => new PrsChecklist
                {
                    Categorie = e.Categorie,
                    SousCategorie = e.SousCategorie,
                    Libelle = e.Libelle,
                    Tache = e.Libelle,
                    Priorite = e.Priorite > 0 ? e.Priorite : 3,
                    DelaiDefautJours = e.DelaiDefautJours > 0 ? e.DelaiDefautJours : 1,
                    Obligatoire = e.Obligatoire
                }).ToList();

                var success = await _checklistService.CreateCustomChecklistAsync(prsId, checklistElements, userLogin);

                if (!success)
                    return BadRequest(new { message = "Erreur lors de la création de la checklist personnalisée" });

                var checklist = await _checklistService.GetPrsChecklistAsync(prsId);
                return Ok(new
                {
                    message = "Checklist personnalisée créée avec succès",
                    checklist = checklist.Select(c => new
                    {
                        c.Id,
                        c.Categorie,
                        c.SousCategorie,
                        c.Libelle,
                        c.Tache,
                        c.Priorite,
                        c.DelaiDefautJours,
                        c.Obligatoire,
                        c.EstCoche,
                        c.Statut,
                        c.Commentaire,
                        LibelleAffichage = c.LibelleAffichage,
                        StatutAffichage = c.StatutAffichage,
                        CssClass = c.CssClass,
                        assignedUsers = c.Affectations
                            .Where(a => a.TypeAffectation == "Utilisateur" && a.UtilisateurId.HasValue)
                            .Select(a => a.UtilisateurId!.Value)
                            .ToList(),
                        assignedGroups = c.Affectations
                            .Where(a => a.TypeAffectation == "Groupe" && a.GroupeId.HasValue)
                            .Select(a => a.GroupeId!.Value)
                            .ToList()
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Erreur lors de la création: {ex.Message}" });
            }
        }

        [HttpGet("prs-with-checklist")]
        public async Task<IActionResult> GetPrsWithChecklist()
        {
            try
            {
                var prsWithChecklist = await _context.Prs
                    .Where(p => p.Checklist.Any())
                    .Select(p => new
                    {
                        id = p.Id,
                        titre = p.Titre,
                        equipement = p.Equipement ?? "N/A",
                        dateCreation = p.DateCreation,
                        nombreElements = p.Checklist.Count(),
                        pourcentageCompletion = p.Checklist.Any()
                            ? (int)Math.Round((double)p.Checklist.Count(pc => pc.EstCoche) / p.Checklist.Count() * 100)
                            : 0
                    })
                    .OrderByDescending(p => p.dateCreation)
                    .ToListAsync();

                var result = prsWithChecklist.Select(p => new
                {
                    p.id,
                    p.titre,
                    p.equipement,
                    dateCreation = p.dateCreation.ToString("dd/MM/yyyy"),
                    p.nombreElements,
                    p.pourcentageCompletion
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Erreur lors de la récupération des PRS: {ex.Message}" });
            }
        }

        [HttpGet("utilisateurs-groupes")]
        public async Task<IActionResult> GetUtilisateursEtGroupes()
        {
            try
            {
                var utilisateurs = await _context.Utilisateurs
                    .Where(u => u.DateDeleted == null)
                    .Select(u => new { u.Id, u.Nom, u.Prenom })
                    .OrderBy(u => u.Nom)
                    .ThenBy(u => u.Prenom)
                    .ToListAsync();

                var groupes = await _context.GroupesUtilisateurs
                    .Where(g => g.Actif)
                    .Select(g => new { g.Id, g.NomGroupe })
                    .OrderBy(g => g.NomGroupe)
                    .ToListAsync();

                return Ok(new { utilisateurs, groupes });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        private string GetCurrentUserLogin()
            => User?.FindFirst(ClaimTypes.Name)?.Value ?? "Unknown";

        // Helpers fallback pour Priorite (si pas déjà calculé dans meta)
        private static string PrioriteToLabel(int p) => p switch
        {
            1 => "Critique",
            2 => "Important",
            3 => "Moyen",
            4 => "Faible",
            5 => "Optionnel",
            _ => "Moyen"
        };
        private static string PrioriteToColor(int p) => p switch
        {
            1 => "#dc3545",
            2 => "#fd7e14",
            3 => "#0d6efd",
            4 => "#198754",
            5 => "#6c757d",
            _ => "#0d6efd"
        };
    }

    public class PrsChecklistCreateDto
    {
        public string Categorie { get; set; }
        public string SousCategorie { get; set; }
        public string Libelle { get; set; }
        public int Priorite { get; set; } = 3;
        public int DelaiDefautJours { get; set; } = 1;
        public bool Obligatoire { get; set; }
    }

    // Interface facultative si tu avais des propriétés calculées (sinon ignore)
    public interface IChecklistElementModeleMeta
    {
        string CategorieComplete { get; }
        string PrioriteLibelle { get; }
        string CouleurPriorite { get; }
    }
}