using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlanifPRS.Data;
using PlanifPRS.Models;
using System.Collections.Generic;
using System.Linq;

namespace PlanifPRS.Pages
{
    public class UsersModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;

        public bool IsAdmin { get; set; }
        public List<Utilisateur> ListeUtilisateurs { get; set; }

        public UsersModel(PlanifPrsDbContext context)
        {
            _context = context;
        }

        public IActionResult OnGet()
        {
            var login = User.Identity?.Name?.Split('\\').LastOrDefault();
            var user = _context.Utilisateurs.FirstOrDefault(u => u.LoginWindows == login);

            if (user == null || user.Droits?.ToLower() != "admin")
            {
                // Redirection vers la page AccessDenied au lieu de retourner Unauthorized()
                return RedirectToPage("/AccessDenied");
            }

            IsAdmin = true;

            ListeUtilisateurs = _context.Utilisateurs.OrderBy(u => u.Nom).ThenBy(u => u.Prenom).ToList();

            // Défaut droits si null
            foreach (var util in ListeUtilisateurs)
            {
                if (string.IsNullOrEmpty(util.Droits))
                    util.Droits = "Visualiseur";
            }

            return Page();
        }

        public IActionResult OnPostUpdateDroit(int id, string nouveauDroit)
        {
            // Validation du droit
            var droitsValides = new[] { "admin", "cdp", "validateur", "process", "maintenance", "qualite", "visualiseur" };
            if (!droitsValides.Contains(nouveauDroit?.ToLower()))
            {
                TempData["ErrorMessage"] = "Droit invalide sélectionné.";
                return RedirectToPage();
            }

            var user = _context.Utilisateurs.FirstOrDefault(u => u.Id == id);
            if (user == null)
            {
                TempData["ErrorMessage"] = "Utilisateur introuvable.";
                return RedirectToPage();
            }

            var ancienDroit = user.Droits ?? "Visualiseur";
            user.Droits = nouveauDroit;
            _context.SaveChanges();

            TempData["SuccessMessage"] = $"Droits de {user.Prenom} {user.Nom} mis à jour : {ancienDroit} → {nouveauDroit}";

            return RedirectToPage();
        }
    }
}