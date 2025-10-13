using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using System.Linq;
using System.Threading.Tasks;

namespace VotreNamespace.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GroupesController : ControllerBase
    {
        private readonly PlanifPrsDbContext _context; // Remplace par ton DbContext

        public GroupesController(PlanifPrsDbContext context)
        {
            _context = context;
        }

        // Méthode pour récupérer les membres d'un groupe
        [HttpGet("{id}/members")]
        public async Task<IActionResult> GetGroupMembers(int id)
        {
            var members = await _context.GroupeUtilisateurs
                .Where(gu => gu.GroupeId == id)
                .Join(
                    _context.Utilisateurs,
                    gu => gu.UtilisateurId,
                    u => u.Id,
                    (gu, u) => new
                    {
                        id = u.Id,
                        nom = u.Nom,
                        prenom = u.Prenom,
                        email = u.Mail,
                        service = u.Service
                    }
                )
                .ToListAsync();

            return Ok(members);
        }
    }
}