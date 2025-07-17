using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Models;

namespace PlanifPRS.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChecklistAssignationController : ControllerBase
    {
        private readonly PlanifPrsDbContext _context;

        public ChecklistAssignationController(PlanifPrsDbContext context)
        {
            _context = context;
        }

        [HttpPost("assign-users/{checklistId}")]
        public async Task<IActionResult> AssignUsers(int checklistId, [FromBody] List<int> userIds)
        {
            try
            {
                var currentUser = User.Identity?.Name ?? "RobinBeau";

                // Supprimer les affectations utilisateur existantes pour cette checklist
                var existingUserAssignations = await _context.ChecklistAffectations
                    .Where(ca => ca.ChecklistId == checklistId && ca.TypeAffectation == "Utilisateur")
                    .ToListAsync();
                _context.ChecklistAffectations.RemoveRange(existingUserAssignations);

                // Ajouter les nouvelles affectations utilisateur
                foreach (var userId in userIds)
                {
                    _context.ChecklistAffectations.Add(new ChecklistAffectation
                    {
                        ChecklistId = checklistId,
                        UtilisateurId = userId,
                        GroupeId = null,
                        TypeAffectation = "Utilisateur",
                        DateAffectation = DateTime.Now,
                        AffectePar = currentUser
                    });
                }

                await _context.SaveChangesAsync();
                return Ok();
            }
            catch (Exception ex)
            {
                return BadRequest($"Erreur lors de l'assignation: {ex.Message}");
            }
        }

        [HttpPost("assign-groups/{checklistId}")]
        public async Task<IActionResult> AssignGroups(int checklistId, [FromBody] List<int> groupIds)
        {
            try
            {
                var currentUser = User.Identity?.Name ?? "RobinBeau";

                // Supprimer les affectations groupe existantes pour cette checklist
                var existingGroupAssignations = await _context.ChecklistAffectations
                    .Where(ca => ca.ChecklistId == checklistId && ca.TypeAffectation == "Groupe")
                    .ToListAsync();
                _context.ChecklistAffectations.RemoveRange(existingGroupAssignations);

                // Ajouter les nouvelles affectations groupe
                foreach (var groupId in groupIds)
                {
                    _context.ChecklistAffectations.Add(new ChecklistAffectation
                    {
                        ChecklistId = checklistId,
                        UtilisateurId = null,
                        GroupeId = groupId,
                        TypeAffectation = "Groupe",
                        DateAffectation = DateTime.Now,
                        AffectePar = currentUser
                    });
                }

                await _context.SaveChangesAsync();
                return Ok();
            }
            catch (Exception ex)
            {
                return BadRequest($"Erreur lors de l'assignation: {ex.Message}");
            }
        }

        [HttpGet("users")]
        public async Task<IActionResult> GetUsers()
        {
            var users = await _context.Utilisateurs
                .Where(u => u.DateDeleted == null)
                .Select(u => new { u.Id, u.Nom, u.Prenom, u.LoginWindows })
                .ToListAsync();
            return Ok(users);
        }

        [HttpGet("groups")]
        public async Task<IActionResult> GetGroups()
        {
            var groups = await _context.GroupesUtilisateurs
                .Where(g => g.Actif)
                .Select(g => new { g.Id, g.NomGroupe, g.Description })
                .ToListAsync();
            return Ok(groups);
        }

        // Nouvelle méthode pour récupérer les affectations existantes
        [HttpGet("assignments/{checklistId}")]
        public async Task<IActionResult> GetAssignments(int checklistId)
        {
            var assignments = await _context.ChecklistAffectations
                .Include(ca => ca.Utilisateur)
                .Include(ca => ca.Groupe)
                .Where(ca => ca.ChecklistId == checklistId)
                .Select(ca => new {
                    ca.Id,
                    ca.TypeAffectation,
                    ca.DateAffectation,
                    ca.AffectePar,
                    Utilisateur = ca.Utilisateur != null ? new { ca.Utilisateur.Id, ca.Utilisateur.Nom, ca.Utilisateur.Prenom } : null,
                    Groupe = ca.Groupe != null ? new { ca.Groupe.Id, ca.Groupe.NomGroupe } : null
                })
                .ToListAsync();
            return Ok(assignments);
        }
    }
}