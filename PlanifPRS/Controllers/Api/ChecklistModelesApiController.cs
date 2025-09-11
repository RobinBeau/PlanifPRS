using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Services;

namespace PlanifPRS.Controllers.Api
{
    [ApiController]
    [Route("api/checklist-modeles")]
    public class ChecklistModelesApiController : ControllerBase
    {
        private readonly ChecklistService _checklistService;
        private readonly PlanifPrsDbContext _context;

        public ChecklistModelesApiController(ChecklistService checklistService, PlanifPrsDbContext context)
        {
            _checklistService = checklistService;
            _context = context;
        }

        [HttpGet("{id}/elements")]
        public async Task<IActionResult> GetModeleElements(int id)
        {
            var modele = await _checklistService.GetChecklistModeleByIdAsync(id);
            if (modele == null)
                return NotFound();

            return Ok(new
            {
                id = modele.Id,
                nom = modele.Nom,
                description = modele.Description,
                familleEquipement = modele.FamilleEquipement,
                elements = modele.Elements.Select(e => new {
                    id = e.Id,
                    categorie = e.Categorie,
                    sousCategorie = e.SousCategorie,
                    libelle = e.Libelle,
                    obligatoire = e.Obligatoire,
                    priorite = e.Priorite,
                    delaiDefautJours = e.DelaiDefautJours
                })
            });
        }

        [HttpPost("elements/batch")]
        public async Task<IActionResult> GetElementsBatch([FromBody] BatchElementsRequest request)
        {
            var elements = await _context.ChecklistElementModeles
                .Where(e => request.ElementIds.Contains(e.Id))
                .Select(e => new {
                    id = e.Id,
                    categorie = e.Categorie,
                    sousCategorie = e.SousCategorie,
                    libelle = e.Libelle,
                    obligatoire = e.Obligatoire,
                    priorite = e.Priorite,
                    delaiDefautJours = e.DelaiDefautJours
                })
                .ToListAsync();

            return Ok(elements);
        }

        public class BatchElementsRequest
        {
            public List<int> ElementIds { get; set; } = new();
        }
    }
}