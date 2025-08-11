using Microsoft.AspNetCore.Mvc;
using PlanifPRS.Services;
using PlanifPRS.Models;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;

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
                    FamilleAffichage = m.FamilleAffichage
                });

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Erreur lors de la récupération des modèles: {ex.Message}" });
            }
        }

        [HttpGet("modeles/{id}")]
        public async Task<IActionResult> GetChecklistModele(int id)
        {
            try
            {
                var modele = await _checklistService.GetChecklistModeleByIdAsync(id);
                if (modele == null)
                    return NotFound(new { message = "Modèle de checklist non trouvé" });

                var result = new
                {
                    modele.Id,
                    modele.Nom,
                    modele.Description,
                    modele.FamilleEquipement,
                    modele.DateCreation,
                    modele.CreatedByLogin,
                    modele.Actif,
                    Elements = modele.Elements.OrderBy(e => e.Priorite).ThenBy(e => e.DelaiDefautJours).Select(e => new
                    {
                        e.Id,
                        e.Categorie,
                        e.SousCategorie,
                        e.Libelle,
                        e.Priorite,
                        e.DelaiDefautJours,
                        e.Obligatoire,
                        CategorieComplete = e.CategorieComplete,
                        PrioriteLibelle = e.PrioriteLibelle,
                        CouleurPriorite = e.CouleurPriorite
                    }).ToList()
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Erreur lors de la récupération du modèle: {ex.Message}" });
            }
        }

        [HttpGet("prs/{prsId}")]
        public async Task<IActionResult> GetPrsChecklist(int prsId)
        {
            try
            {
                var checklist = await _checklistService.GetPrsChecklistAsync(prsId);
                var result = new
                {
                    prsId = prsId,
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
                        // AJOUT: renvoyer les IDs d'affectations pour que le front puisse les afficher
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
                        // AJOUT: renvoyer les IDs d'affectations
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
        public async Task<IActionResult> CreateCustomChecklist(int prsId, [FromBody] List<PrsChecklistCreateDto> elements)
        {
            try
            {
                var userLogin = GetCurrentUserLogin();

                var checklistElements = elements.Select(e => new PrsChecklist
                {
                    Categorie = e.Categorie,
                    SousCategorie = e.SousCategorie,
                    Libelle = e.Libelle,
                    Tache = e.Libelle, // Compatibilité
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
                        // Par cohérence on renvoie aussi les affectations
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
                    .Where(p => p.Checklist.Any()) // Utiliser "Checklist" au lieu de "PrsChecklists"
                    .Select(p => new
                    {
                        id = p.Id,
                        titre = p.Titre,
                        equipement = p.Equipement ?? "N/A",
                        dateCreation = p.DateCreation, // Garder DateTime pour le tri
                        nombreElements = p.Checklist.Count(),
                        pourcentageCompletion = p.Checklist.Any()
                            ? (int)Math.Round((double)p.Checklist.Count(pc => pc.EstCoche) / p.Checklist.Count() * 100)
                            : 0
                    })
                    .OrderByDescending(p => p.dateCreation) // Trier par DateTime directement
                    .ToListAsync();

                // Formater les dates côté client après récupération
                var result = prsWithChecklist.Select(p => new
                {
                    p.id,
                    p.titre,
                    p.equipement,
                    dateCreation = p.dateCreation.ToString("dd/MM/yyyy"), // Formater ici
                    p.nombreElements,
                    p.pourcentageCompletion
                }).ToList();

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
                    .Where(u => u.DateDeleted == null) // Utilisateurs non supprimés
                    .Select(u => new { u.Id, u.Nom, u.Prenom })
                    .OrderBy(u => u.Nom)
                    .ThenBy(u => u.Prenom)
                    .ToListAsync();

                var groupes = await _context.GroupesUtilisateurs
                    .Where(g => g.Actif)
                    .Select(g => new { g.Id, g.NomGroupe })
                    .OrderBy(g => g.NomGroupe)
                    .ToListAsync();

                return Ok(new
                {
                    utilisateurs = utilisateurs,
                    groupes = groupes
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        private string GetCurrentUserLogin()
        {
            return User?.FindFirst(ClaimTypes.Name)?.Value ?? "Unknown";
        }
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
}