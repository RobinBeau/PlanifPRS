using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PlanifPRS.Data;
using PlanifPRS.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlanifPRS.Pages
{
    public class UsersModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;
        private readonly ILogger<UsersModel> _logger;

        public bool IsAdmin { get; set; }
        public List<Utilisateur> ListeUtilisateurs { get; set; }

        public UsersModel(PlanifPrsDbContext context, ILogger<UsersModel> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var login = User.Identity?.Name?.Split('\\').LastOrDefault();
            var user = await _context.Utilisateurs
                .FirstOrDefaultAsync(u => u.LoginWindows == login && !u.DateDeleted.HasValue);

            if (user == null || !user.IsAdmin)
            {
                _logger.LogWarning($"[USERS] Accès refusé pour {login}");
                return RedirectToPage("/AccessDenied");
            }

            IsAdmin = true;

            ListeUtilisateurs = await _context.Utilisateurs
                .Where(u => !u.DateDeleted.HasValue)
                .OrderBy(u => u.Nom)
                .ThenBy(u => u.Prenom)
                .ToListAsync();

            // Défaut droits si null
            foreach (var util in ListeUtilisateurs)
            {
                if (string.IsNullOrEmpty(util.Droits))
                    util.Droits = "Visualiseur";
            }

            _logger.LogInformation($"[USERS] {ListeUtilisateurs.Count} utilisateurs chargés");

            return Page();
        }

        /// <summary>
        /// Met à jour les droits d'un utilisateur
        /// </summary>
        public async Task<IActionResult> OnPostUpdateDroitAsync(int id, string nouveauDroit)
        {
            var droitsValides = new[] { "admin", "cdp", "validateur", "process", "maintenance", "qualite", "visualiseur" };
            if (!droitsValides.Contains(nouveauDroit?.ToLower()))
            {
                TempData["ErrorMessage"] = "❌ Droit invalide sélectionné.";
                return RedirectToPage();
            }

            var user = await _context.Utilisateurs.FirstOrDefaultAsync(u => u.Id == id);
            if (user == null)
            {
                TempData["ErrorMessage"] = "❌ Utilisateur introuvable.";
                return RedirectToPage();
            }

            var ancienDroit = user.Droits ?? "Visualiseur";
            user.Droits = nouveauDroit;
            await _context.SaveChangesAsync();

            _logger.LogInformation($"[USERS] Droits mis à jour : {user.LoginWindows} {ancienDroit} → {nouveauDroit}");
            TempData["SuccessMessage"] = $"✅ Droits de {user.Prenom} {user.Nom} mis à jour : {ancienDroit} → {nouveauDroit}";

            return RedirectToPage();
        }

        /// <summary>
        /// Active/désactive le statut de chef de service
        /// </summary>
        public async Task<IActionResult> OnPostToggleChefServiceAsync(int id)
        {
            var user = await _context.Utilisateurs.FirstOrDefaultAsync(u => u.Id == id);
            if (user == null)
            {
                TempData["ErrorMessage"] = "❌ Utilisateur introuvable.";
                return RedirectToPage();
            }

            // Toggle la valeur
            user.EstChefService = !(user.EstChefService ?? false);
            await _context.SaveChangesAsync();

            var statut = user.EstChefService == true ? "activé" : "désactivé";
            var serviceName = user.ServiceClean;

            _logger.LogInformation($"[USERS] Chef de service {statut} : {user.LoginWindows} ({serviceName})");

            TempData["SuccessMessage"] = user.EstChefService == true
                ? $"✅ {user.Prenom} {user.Nom} est maintenant <strong>Chef de Service</strong> pour <strong>{serviceName}</strong>"
                : $"✅ {user.Prenom} {user.Nom} n'est plus Chef de Service";

            return RedirectToPage();
        }
    }
}