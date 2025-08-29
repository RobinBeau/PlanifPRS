using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Services;

namespace PlanifPRS.Pages
{
    public class ExportModel : PageModel
    {
        private readonly PlanifPrsDbContext _context;
        private readonly ExportCalendarService _exportCalendarService;

        public ExportModel(PlanifPrsDbContext context, ExportCalendarService exportCalendarService)
        {
            _context = context;
            _exportCalendarService = exportCalendarService;
        }

        // Année sélectionnée
        [BindProperty]
        public int Year { get; set; }

        // Chaîne saisie ou reconstruite des semaines (ex: "26,27-29,31")
        [BindProperty]
        public string WeeksInput { get; set; } = string.Empty;

        // Login qui sera vérifié côté service pour les droits
        [BindProperty]
        public string? RequesterLogin { get; set; }

        // Semaine courante (affichage)
        public int CurrentWeek { get; private set; }

        // Liste des semaines sélectionnées (pour afficher les boutons actifs)
        public List<int> SelectedWeeks { get; private set; } = new();

        // Indique si l’utilisateur courant a les droits (admin ou validateur)
        public bool IsAdminOrValidateur => HasRequiredRole();

        // Login simplifié (sans domaine) de l’utilisateur courant
        public string CurrentUserLogin => GetCurrentUserLogin();

        public void OnGet()
        {
            // Valeurs par défaut : année courante / semaine actuelle
            var today = DateTime.Today;
            Year = today.Year;
            CurrentWeek = ISOWeek.GetWeekOfYear(today);

            // Pré-remplir le login requérant si non fourni
            if (string.IsNullOrWhiteSpace(RequesterLogin))
                RequesterLogin = CurrentUserLogin;

            // Si WeeksInput vide → sélectionner la semaine courante
            if (string.IsNullOrWhiteSpace(WeeksInput))
            {
                SelectedWeeks = new List<int> { CurrentWeek };
                WeeksInput = CurrentWeek.ToString("00");
            }
            else
            {
                SelectedWeeks = ExpandWeeks(WeeksInput);
            }
        }

        // Handler de téléchargement (nommé Download pour correspondre au form asp-page-handler="Download")
        public async Task<IActionResult> OnPostDownloadAsync()
        {
            // Re-valider / normaliser données
            if (Year < 2000 || Year > 2100)
            {
                ModelState.AddModelError(nameof(Year), "Année invalide.");
                return Page();
            }

            var weeks = ExpandWeeks(WeeksInput);
            if (weeks.Count == 0)
            {
                ModelState.AddModelError(nameof(WeeksInput), "Aucune semaine valide.");
                return Page();
            }

            // Auto fallback du login
            if (string.IsNullOrWhiteSpace(RequesterLogin))
                RequesterLogin = CurrentUserLogin;

            try
            {
                var bytes = await _exportCalendarService.ExportSemainesAsync(Year, weeks, RequesterLogin);
                var fileName = $"CalendrierPRS_{Year}_{string.Join("-", CompactWeeks(weeks))}.xlsx";
                return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
            }
            catch (UnauthorizedAccessException uaex)
            {
                ModelState.AddModelError(string.Empty, uaex.Message);
                return Page();
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, $"Erreur export : {ex.Message}");
                return Page();
            }
        }

        // ========== UTILITAIRES WEEKS ==========

        private List<int> ExpandWeeks(string input)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(input))
                return result;

            foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim();
                if (p.Contains('-'))
                {
                    var bounds = p.Split('-', StringSplitOptions.RemoveEmptyEntries);
                    if (bounds.Length == 2 &&
                        int.TryParse(bounds[0], out int a) &&
                        int.TryParse(bounds[1], out int b))
                    {
                        int start = Math.Min(a, b);
                        int end = Math.Max(a, b);
                        for (int w = start; w <= end; w++)
                        {
                            if (w >= 1 && w <= 53 && !result.Contains(w))
                                result.Add(w);
                        }
                    }
                }
                else
                {
                    if (int.TryParse(p, out int w) && w >= 1 && w <= 53 && !result.Contains(w))
                        result.Add(w);
                }
            }

            result.Sort();
            return result;
        }

        private string CompactWeeks(List<int> weeks)
        {
            if (weeks == null || weeks.Count == 0) return "";
            weeks = weeks.OrderBy(w => w).ToList();

            var pieces = new List<string>();
            int start = weeks[0];
            int prev = weeks[0];

            for (int i = 1; i < weeks.Count; i++)
            {
                if (weeks[i] == prev + 1)
                {
                    prev = weeks[i];
                    continue;
                }

                if (start == prev)
                    pieces.Add(start.ToString("00"));
                else
                    pieces.Add($"{start:00}-{prev:00}");

                start = prev = weeks[i];
            }

            if (start == prev)
                pieces.Add(start.ToString("00"));
            else
                pieces.Add($"{start:00}-{prev:00}");

            return string.Join(",", pieces);
        }

        // ========== UTILITAIRES DROITS / LOGIN ==========

        private string GetCurrentUserLogin()
        {
            var raw = User.Identity?.Name ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return "";
            // Domain\login → prendre la partie après '\'
            var parts = raw.Split('\\');
            var login = parts.LastOrDefault() ?? raw;
            return login.Trim();
        }

        private bool HasRequiredRole()
        {
            var login = GetCurrentUserLogin();
            if (string.IsNullOrEmpty(login)) return false;

            var user = _context.Utilisateurs
                .AsNoTracking()
                .FirstOrDefault(u => u.LoginWindows == login && u.DateDeleted == null);

            if (user == null) return false;
            var droits = (user.Droits ?? "").Trim().ToLowerInvariant();

            // Harmonisation : "validateur" (pas "valideur")
            return droits == "admin" || droits == "validateur";
        }
    }
}