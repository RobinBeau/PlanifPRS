using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace PlanifPRS.Controllers.Api
{
    [ApiController]
    [Route("api/ai-suggestions")]
    public class AiSuggestionsController : ControllerBase
    {
        private readonly PlanifPrsDbContext _context;

        // Mémoire des préférences historiques pour la requête en cours
        private HistoricalPreferences _historicalPrefs;

        public AiSuggestionsController(PlanifPrsDbContext context)
        {
            _context = context;
        }

        [HttpPost("suggest-slot")]
        public async Task<IActionResult> SuggestSlot([FromBody] SlotSuggestionRequest request)
        {
            try
            {
                var suggestions = await GenerateSlotSuggestions(request);

                return Ok(new
                {
                    success = true,
                    suggestions = suggestions.Select(s => new
                    {
                        dateDebut = s.DateDebut.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"),
                        dateFin = s.DateFin.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss"),
                        score = s.Score,
                        raison = s.Raison
                    }),
                    metadata = new
                    {
                        timestamp = DateTime.UtcNow,
                        robia_version = "1.1",
                        duration_requested = request.DurationHours,
                        equipement = request.Equipement,
                        working_hours_french = "9h00-17h00 (UTC+2)",
                        french_holidays_excluded = true,
                        day_scoring = "Lun/Mar/Mer=25pts, Jeu=10pts, Ven=5pts",
                        ai_preference_learning = new
                        {
                            enabled = _historicalPrefs?.TotalCount > 0,
                            scope = _historicalPrefs?.Scope ?? "none",
                            window_days = _historicalPrefs?.WindowDays ?? 0,
                            samples = _historicalPrefs?.TotalCount ?? 0
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = ex.Message,
                    timestamp = DateTime.UtcNow
                });
            }
        }

        // ===== AJOUT : GESTION DES JOURS FÉRIÉS FRANÇAIS =====
        private static readonly Dictionary<int, List<DateTime>> FrenchHolidays = new Dictionary<int, List<DateTime>>
        {
            [2025] = new List<DateTime>
            {
                new DateTime(2025, 1, 1),   // Jour de l'An
                new DateTime(2025, 4, 21),  // Lundi de Pâques
                new DateTime(2025, 5, 1),   // Fête du Travail
                new DateTime(2025, 5, 8),   // Victoire 1945
                new DateTime(2025, 5, 29),  // Ascension
                new DateTime(2025, 6, 9),   // Lundi de Pentecôte
                new DateTime(2025, 7, 14),  // Fête Nationale
                new DateTime(2025, 8, 15),  // Assomption
                new DateTime(2025, 11, 1),  // Toussaint
                new DateTime(2025, 11, 11), // Armistice 1918
                new DateTime(2025, 12, 25)  // Noël
            },
            [2026] = new List<DateTime>
            {
                new DateTime(2026, 1, 1),   // Jour de l'An
                new DateTime(2026, 4, 6),   // Lundi de Pâques
                new DateTime(2026, 5, 1),   // Fête du Travail
                new DateTime(2026, 5, 8),   // Victoire 1945
                new DateTime(2026, 5, 14),  // Ascension
                new DateTime(2026, 5, 25),  // Lundi de Pentecôte
                new DateTime(2026, 7, 14),  // Fête Nationale
                new DateTime(2026, 8, 15),  // Assomption
                new DateTime(2026, 11, 1),  // Toussaint
                new DateTime(2026, 11, 11), // Armistice 1918
                new DateTime(2026, 12, 25)  // Noël
            }
        };

        /// <summary>
        /// Vérifie si une date est un jour férié français
        /// </summary>
        private bool IsFreenchHoliday(DateTime date)
        {
            var year = date.Year;
            if (FrenchHolidays.ContainsKey(year))
            {
                return FrenchHolidays[year].Any(h => h.Date == date.Date);
            }

            // Si l'année n'est pas dans notre dictionnaire, calculer les fêtes mobiles
            return IsCalculatedHoliday(date);
        }

        /// <summary>
        /// Calcule les jours fériés mobiles pour une année donnée
        /// </summary>
        private bool IsCalculatedHoliday(DateTime date)
        {
            var year = date.Year;
            var easter = CalculateEaster(year);

            var holidays = new List<DateTime>
            {
                new DateTime(year, 1, 1),   // Jour de l'An
                easter.AddDays(1),          // Lundi de Pâques
                new DateTime(year, 5, 1),   // Fête du Travail
                new DateTime(year, 5, 8),   // Victoire 1945
                easter.AddDays(39),         // Ascension
                easter.AddDays(50),         // Lundi de Pentecôte
                new DateTime(year, 7, 14),  // Fête Nationale
                new DateTime(year, 8, 15),  // Assomption
                new DateTime(year, 11, 1),  // Toussaint
                new DateTime(year, 11, 11), // Armistice 1918
                new DateTime(year, 12, 25)  // Noël
            };

            return holidays.Any(h => h.Date == date.Date);
        }

        /// <summary>
        /// Calcule la date de Pâques pour une année donnée (algorithme de Gauss)
        /// </summary>
        private DateTime CalculateEaster(int year)
        {
            int a = year % 19;
            int b = year / 100;
            int c = year % 100;
            int d = b / 4;
            int e = b % 4;
            int f = (b + 8) / 25;
            int g = (b - f + 1) / 3;
            int h = (19 * a + b - d - g + 15) % 30;
            int i = c / 4;
            int k = c % 4;
            int l = (32 + 2 * e + 2 * i - h - k) % 7;
            int m = (a + 11 * h + 22 * l) / 451;
            int n = (h + l - 7 * m + 114) / 31;
            int p = (h + l - 7 * m + 114) % 31;

            return new DateTime(year, n, p + 1);
        }

        /// <summary>
        /// Vérifie si un jour est ouvrable (ni weekend, ni férié)
        /// </summary>
        private bool IsWorkingDay(DateTime date)
        {
            return date.DayOfWeek != DayOfWeek.Saturday &&
                   date.DayOfWeek != DayOfWeek.Sunday &&
                   !IsFreenchHoliday(date);
        }

        /// <summary>
        /// Obtient le nom du jour férié français
        /// </summary>
        private string GetHolidayName(DateTime date)
        {
            if (!IsFreenchHoliday(date)) return null;

            var year = date.Year;
            var easter = CalculateEaster(year);

            var holidayNames = new Dictionary<DateTime, string>
            {
                { new DateTime(year, 1, 1), "Jour de l'An" },
                { easter.AddDays(1), "Lundi de Pâques" },
                { new DateTime(year, 5, 1), "Fête du Travail" },
                { new DateTime(year, 5, 8), "Fête de la Victoire" },
                { easter.AddDays(39), "Ascension" },
                { easter.AddDays(50), "Lundi de Pentecôte" },
                { new DateTime(year, 7, 14), "Fête Nationale" },
                { new DateTime(year, 8, 15), "Assomption" },
                { new DateTime(year, 11, 1), "Toussaint" },
                { new DateTime(year, 11, 11), "Armistice" },
                { new DateTime(year, 12, 25), "Noël" }
            };

            return holidayNames.FirstOrDefault(h => h.Key.Date == date.Date).Value ?? "Jour férié";
        }

        // ===== MODIFICATION DES MÉTHODES EXISTANTES + APPRENTISSAGE PRÉFÉRENCES =====

        private async Task<List<SlotSuggestion>> GenerateSlotSuggestions(SlotSuggestionRequest request)
        {
            var suggestions = new List<SlotSuggestion>();

            // Période d'analyse dynamique - ne pas proposer dans le passé
            // - Début = maintenant (UTC)
            // - Fin = +28 jours à 15h UTC (17h FR)
            var nowUtc = DateTime.UtcNow;
            var startAnalysis = nowUtc;
            var endAnalysis = nowUtc.Date.AddDays(28).AddHours(15); // 15h UTC = 17h FR

            // Charger les infos de secteur + préférences historiques
            var secteurInfo = await GetSecteurInfoSecure(request.LigneId);
            _historicalPrefs = await GetHistoricalPreferencesSecure(request.LigneId, secteurInfo?.SecteurId);

            if (secteurInfo == null)
            {
                var basic = await GenerateBasicSlotSuggestions(request, startAnalysis, endAnalysis);
                // Filtre de sécurité: exclure toute proposition passée
                return basic.Where(s => s.DateDebut >= DateTime.UtcNow)
                            .OrderByDescending(s => s.Score)
                            .ThenBy(s => s.DateDebut)
                            .Take(5)
                            .ToList();
            }

            var existingPrs = await GetExistingPrsSecure(startAnalysis, endAnalysis);

            if (request.DurationHours > 8)
            {
                suggestions = await GenerateMultiDaySlotSuggestions(startAnalysis, endAnalysis, existingPrs, request, secteurInfo);
            }
            else
            {
                suggestions = await GenerateDailySlotSuggestions(startAnalysis, endAnalysis, existingPrs, request, secteurInfo);
            }

            var topSuggestions = suggestions
                .Where(s => s.Score > 20)
                .Where(s => s.DateDebut >= DateTime.UtcNow) // Filtre de sécurité: pas de dates passées
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.DateDebut)
                .Take(5)
                .ToList();

            return topSuggestions;
        }

        private async Task<List<SlotSuggestion>> GenerateDailySlotSuggestions(
            DateTime startAnalysis,
            DateTime endAnalysis,
            List<PrsInfo> existingPrs,
            SlotSuggestionRequest request,
            SecteurInfo secteurInfo)
        {
            var suggestions = new List<SlotSuggestion>();
            var todayUtc = DateTime.UtcNow.Date;

            for (var day = startAnalysis.Date; day <= endAnalysis.Date; day = day.AddDays(1))
            {
                // Ne considérer que les jours présents/futurs et ouvrables
                if (day < todayUtc) continue;
                if (!IsWorkingDay(day)) continue;

                // Créneaux par balayage standard
                var daySlots = FindBestSlotsForDay(day, existingPrs, request, secteurInfo);
                suggestions.AddRange(daySlots);

                // Créneaux "IA" favoris du passé (surpondérés)
                var preferredSlots = GeneratePreferredSlotsForDay(day, existingPrs, request, secteurInfo);
                suggestions.AddRange(preferredSlots);
            }

            return suggestions;
        }

        private async Task<List<SlotSuggestion>> GenerateMultiDaySlotSuggestions(
            DateTime startAnalysis,
            DateTime endAnalysis,
            List<PrsInfo> existingPrs,
            SlotSuggestionRequest request,
            SecteurInfo secteurInfo)
        {
            var suggestions = new List<SlotSuggestion>();
            var workingDaysNeeded = Math.Ceiling((double)request.DurationHours / 8);
            var todayUtc = DateTime.UtcNow.Date;

            for (var testDay = startAnalysis.Date; testDay <= endAnalysis.Date.AddDays(-(int)workingDaysNeeded); testDay = testDay.AddDays(1))
            {
                // Vérifier que le jour de début est dans le présent/futur et ouvrable
                if (testDay < todayUtc) continue;
                if (!IsWorkingDay(testDay)) continue;

                var multiDaySlot = AnalyzeMultiDaySlot(testDay, existingPrs, request, secteurInfo);
                if (multiDaySlot != null)
                {
                    suggestions.Add(multiDaySlot);
                }
            }

            return suggestions;
        }

        private SlotSuggestion AnalyzeMultiDaySlot(DateTime startDay, List<PrsInfo> existingPrs, SlotSuggestionRequest request, SecteurInfo secteurInfo)
        {
            var slotStart = startDay.Date.AddHours(7); // 7h UTC = 9h FR
            var nowUtc = DateTime.UtcNow;

            // Ne pas proposer un démarrage aujourd'hui si l'heure de début (7h UTC) est déjà passée
            if (startDay.Date == nowUtc.Date && nowUtc > slotStart)
            {
                return null;
            }

            DateTime slotEnd;
            var remainingHours = request.DurationHours;
            var currentDay = startDay.Date;

            while (remainingHours > 0)
            {
                // Vérifier que le jour est ouvrable
                if (IsWorkingDay(currentDay))
                {
                    if (remainingHours >= 8)
                    {
                        remainingHours -= 8;
                        if (remainingHours == 0)
                        {
                            slotEnd = currentDay.AddHours(15); // 15h UTC = 17h FR
                            break;
                        }
                    }
                    else
                    {
                        slotEnd = currentDay.AddHours(7 + remainingHours);
                        break;
                    }
                }
                currentDay = currentDay.AddDays(1);
            }
            slotEnd = currentDay.AddHours(15);

            // Vérifier conflits + que tous les jours de la période sont ouvrables
            for (var checkDay = startDay.Date; checkDay <= slotEnd.Date; checkDay = checkDay.AddDays(1))
            {
                if (!IsWorkingDay(checkDay))
                {
                    return null;
                }

                var dayConflicts = existingPrs.Any(p =>
                    p.LigneId == request.LigneId &&
                    p.DateDebut.Date == checkDay);

                if (dayConflicts)
                {
                    return null;
                }
            }

            bool hasSectorConflict = CheckSectorConflictForPeriod(slotStart, slotEnd, existingPrs, secteurInfo);
            var score = CalculateMultiDayScore(slotStart, slotEnd, request, secteurInfo, hasSectorConflict, existingPrs);
            var reason = GenerateMultiDayReason(slotStart, slotEnd, request, secteurInfo, hasSectorConflict);

            return new SlotSuggestion
            {
                DateDebut = slotStart,
                DateFin = slotEnd,
                Score = score,
                Raison = reason
            };
        }

        private bool CheckSectorConflictForPeriod(DateTime slotStart, DateTime slotEnd, List<PrsInfo> existingPrs, SecteurInfo secteurInfo)
        {
            if (!secteurInfo.SecteurId.HasValue)
                return false;

            var currentWeekStart = GetStartOfWeek(slotStart);
            var endWeekStart = GetStartOfWeek(slotEnd);

            while (currentWeekStart <= endWeekStart)
            {
                var weekEnd = currentWeekStart.AddDays(6);

                var weekSectorConflict = existingPrs.Any(p =>
                    p.SecteurId == secteurInfo.SecteurId.Value &&
                    p.DateDebut.Date >= currentWeekStart &&
                    p.DateDebut.Date <= weekEnd &&
                    !(p.DateDebut >= slotStart && p.DateDebut < slotEnd));

                if (weekSectorConflict)
                    return true;

                currentWeekStart = currentWeekStart.AddDays(7);
            }

            return false;
        }

        private List<SlotSuggestion> FindBestSlotsForDay(DateTime day, List<PrsInfo> allExistingPrs, SlotSuggestionRequest request, SecteurInfo secteurInfo)
        {
            var suggestions = new List<SlotSuggestion>();
            var duration = TimeSpan.FromHours(request.DurationHours);

            var workStart = day.Date.AddHours(7);  // 7h UTC = 9h FR
            var workEnd = day.Date.AddHours(15);   // 15h UTC = 17h FR
            var nowUtc = DateTime.UtcNow;

            var dayPrsOnSameLine = allExistingPrs
                .Where(p => p.LigneId == request.LigneId && p.DateDebut.Date == day.Date)
                .ToList();

            bool hasSectorConflictThisWeek = false;
            List<PrsInfo> weekSectorPrs = new List<PrsInfo>();

            if (secteurInfo.SecteurId.HasValue)
            {
                var weekStart = GetStartOfWeek(day);
                var weekEnd = weekStart.AddDays(6);

                weekSectorPrs = allExistingPrs
                    .Where(p => p.SecteurId == secteurInfo.SecteurId.Value &&
                               p.DateDebut.Date >= weekStart &&
                               p.DateDebut.Date <= weekEnd)
                    .ToList();

                hasSectorConflictThisWeek = weekSectorPrs.Any();
            }

            if (request.DurationHours >= 4)
            {
                var longSlots = new[]
                {
                    new { Start = workStart, End = workStart.AddHours(request.DurationHours), Name = "Matinée complète", Bonus = 25 },
                    new { Start = workStart.AddHours(1), End = workStart.AddHours(1 + request.DurationHours), Name = "Matinée décalée", Bonus = 15 },
                    new { Start = workEnd.AddHours(-request.DurationHours), End = workEnd, Name = "Après-midi complet", Bonus = 10 }
                };

                foreach (var slot in longSlots.Where(s => s.End <= workEnd))
                {
                    // Ne pas proposer un créneau partiellement ou totalement dans le passé
                    if (day.Date == nowUtc.Date && slot.Start < nowUtc)
                        continue;

                    bool hasDirectConflict = dayPrsOnSameLine.Any(p =>
                        (slot.Start < p.DateFin && slot.End > p.DateDebut));

                    if (!hasDirectConflict)
                    {
                        var score = CalculateSlotScore(slot.Start, slot.End, dayPrsOnSameLine.Count, allExistingPrs, request, secteurInfo, hasSectorConflictThisWeek, day);
                        var reason = GenerateReason(slot.Start, slot.End, dayPrsOnSameLine.Count, weekSectorPrs, request, secteurInfo, hasSectorConflictThisWeek, day);

                        suggestions.Add(new SlotSuggestion
                        {
                            DateDebut = slot.Start,
                            DateFin = slot.End,
                            Score = score + slot.Bonus,
                            Raison = $"{slot.Name}: {reason}"
                        });
                    }
                }
            }
            else
            {
                for (var start = workStart; start.Add(duration) <= workEnd; start = start.AddHours(1))
                {
                    var end = start.Add(duration);

                    // Ne pas proposer un créneau partiellement ou totalement dans le passé
                    if (day.Date == nowUtc.Date && start < nowUtc)
                        continue;

                    bool hasDirectConflict = dayPrsOnSameLine.Any(p =>
                        (start < p.DateFin && end > p.DateDebut));

                    if (!hasDirectConflict)
                    {
                        var score = CalculateSlotScore(start, end, dayPrsOnSameLine.Count, allExistingPrs, request, secteurInfo, hasSectorConflictThisWeek, day);
                        var reason = GenerateReason(start, end, dayPrsOnSameLine.Count, weekSectorPrs, request, secteurInfo, hasSectorConflictThisWeek, day);

                        suggestions.Add(new SlotSuggestion
                        {
                            DateDebut = start,
                            DateFin = end,
                            Score = score,
                            Raison = reason
                        });
                    }
                }
            }

            return suggestions;
        }

        // Créneaux alignés sur les heures de début historiquement privilégiées (IA)
        private List<SlotSuggestion> GeneratePreferredSlotsForDay(DateTime day, List<PrsInfo> allExistingPrs, SlotSuggestionRequest request, SecteurInfo secteurInfo)
        {
            var results = new List<SlotSuggestion>();
            if (_historicalPrefs == null || _historicalPrefs.TotalCount == 0) return results;

            var duration = TimeSpan.FromHours(request.DurationHours);
            var workStart = day.Date.AddHours(7);
            var workEnd = day.Date.AddHours(15);
            var nowUtc = DateTime.UtcNow;

            // Top 3 heures (UTC) pour ce jour (si connues), sinon top global
            var candidateHours = _historicalPrefs
                .GetTopHoursForDay(day.DayOfWeek, 3)
                .DefaultIfEmpty()
                .Where(h => h >= 7 && h <= 14) // doit pouvoir accueillir la durée
                .Distinct()
                .ToList();

            // Fallback: si rien de spécifique au jour, utiliser top heures globales
            if (candidateHours.Count == 1 && candidateHours[0] == default(int))
            {
                candidateHours = _historicalPrefs.GetTopHoursGlobal(3)
                    .Where(h => h >= 7 && h <= 14)
                    .Distinct()
                    .ToList();
            }

            if (candidateHours.Count == 0) return results;

            // Exclusions de conflits
            var dayPrsOnSameLine = allExistingPrs
                .Where(p => p.LigneId == request.LigneId && p.DateDebut.Date == day.Date)
                .ToList();

            foreach (var hour in candidateHours)
            {
                var start = day.Date.AddHours(hour);
                var end = start.Add(duration);

                // hors horaires ouvrés ou passé
                if (start < workStart || end > workEnd) continue;
                if (day.Date == nowUtc.Date && start < nowUtc) continue;
                if (!IsWorkingDay(day)) continue;

                bool hasDirectConflict = dayPrsOnSameLine.Any(p => start < p.DateFin && end > p.DateDebut);
                if (hasDirectConflict) continue;

                bool hasSectorConflictThisWeek = false;
                List<PrsInfo> weekSectorPrs = new List<PrsInfo>();
                if (secteurInfo.SecteurId.HasValue)
                {
                    var weekStart = GetStartOfWeek(day);
                    var weekEnd = weekStart.AddDays(6);

                    weekSectorPrs = allExistingPrs
                        .Where(p => p.SecteurId == secteurInfo.SecteurId.Value &&
                                   p.DateDebut.Date >= weekStart &&
                                   p.DateDebut.Date <= weekEnd)
                        .ToList();

                    hasSectorConflictThisWeek = weekSectorPrs.Any();
                }

                var score = CalculateSlotScore(start, end, dayPrsOnSameLine.Count, allExistingPrs, request, secteurInfo, hasSectorConflictThisWeek, day);
                var reason = GenerateReason(start, end, dayPrsOnSameLine.Count, weekSectorPrs, request, secteurInfo, hasSectorConflictThisWeek, day);

                // Surligner le fait "préféré"
                var frHour = (start.Hour + 2) % 24;
                var pct = _historicalPrefs.GetDayHourSharePercent(day.DayOfWeek, start.Hour);
                if (pct > 0)
                {
                    reason = $"🤖 IA: créneau historiquement privilégié ({pct:F0}% des PRS le {day.ToString("dddd", new CultureInfo("fr-FR"))} à {frHour:00}h), {reason}";
                }

                results.Add(new SlotSuggestion
                {
                    DateDebut = start,
                    DateFin = end,
                    Score = score + 10, // léger bonus pour remonter ces propositions IA
                    Raison = reason
                });
            }

            return results;
        }

        private int CalculateSlotScore(DateTime start, DateTime end, int dayPrsCount, List<PrsInfo> allPrs, SlotSuggestionRequest request, SecteurInfo secteurInfo, bool hasSectorConflictThisWeek, DateTime day)
        {
            int score = 100;

            if (hasSectorConflictThisWeek)
                score -= 70;

            // Bonus horaire (UTC+2 = heure française)
            if (start.Hour >= 7 && start.Hour < 9)        // 9h-11h FR
                score += 35;
            else if (start.Hour >= 9 && start.Hour < 11)  // 11h-13h FR
                score += 25;
            else if (start.Hour >= 11 && start.Hour < 13) // 13h-15h FR
                score += 15;

            if (start.Hour >= 13) // 15h+ FR
                score -= 25;

            // Bonus équipement
            if (!string.IsNullOrEmpty(request.Equipement))
            {
                if (request.Equipement == "CMS" && start.Hour >= 7 && start.Hour < 9)
                    score += 30;
                if (request.Equipement == "Finition" && start.Hour >= 11 && start.Hour < 13)
                    score += 25;
            }

            // Bonus charge journée
            var dayAllPrs = allPrs.Count(p => p.DateDebut.Date == start.Date);
            if (dayAllPrs == 0)
                score += 40;
            else if (dayAllPrs <= 2)
                score += 25;
            else if (dayAllPrs >= 5)
                score -= 20;

            // Système de scoring par jour
            switch (start.DayOfWeek)
            {
                case DayOfWeek.Monday:
                case DayOfWeek.Tuesday:
                case DayOfWeek.Wednesday:
                    score += 25;
                    if (start.DayOfWeek == DayOfWeek.Monday && IsFreenchHoliday(start.AddDays(-1)))
                        score += 10;
                    break;

                case DayOfWeek.Thursday:
                    score += 10;
                    if (IsFreenchHoliday(start.AddDays(1)))
                        score -= 10;
                    break;

                case DayOfWeek.Friday:
                    score += 5;
                    if (IsFreenchHoliday(start.AddDays(3)))
                        score -= 15;
                    break;
            }

            if (!hasSectorConflictThisWeek && secteurInfo.SecteurId.HasValue)
                score += 45;

            if (dayPrsCount == 0)
                score += 30;

            if (request.DurationHours == 1 && start.Hour >= 7 && start.Hour < 9)
                score += 15;
            else if (request.DurationHours == 8 && start.Hour == 7)
                score += 20;

            // ===== AJOUT: Bonus de préférence historique (IA) =====
            score += GetPreferenceBoost(start);

            return Math.Max(0, score);
        }

        private int CalculateMultiDayScore(DateTime start, DateTime end, SlotSuggestionRequest request, SecteurInfo secteurInfo, bool hasSectorConflict, List<PrsInfo> allPrs)
        {
            int score = 150;

            if (hasSectorConflict)
                score -= 80;

            // Scoring par jour pour multi-day
            switch (start.DayOfWeek)
            {
                case DayOfWeek.Monday:
                    score += 35;
                    if (IsFreenchHoliday(start.AddDays(-1)))
                        score += 10;
                    break;
                case DayOfWeek.Tuesday:
                    score += 30;
                    break;
                case DayOfWeek.Wednesday:
                    score += 25;
                    break;
                case DayOfWeek.Thursday:
                    score += 5;
                    break;
                case DayOfWeek.Friday:
                    score -= 10;
                    break;
            }

            if (!hasSectorConflict && secteurInfo.SecteurId.HasValue)
                score += 50;

            if (request.DurationHours == 16)
                score += 20;
            else if (request.DurationHours == 24)
                score += 15;
            else if (request.DurationHours == 40)
                score += 25;

            var periodPrsCount = allPrs.Count(p => p.DateDebut.Date >= start.Date && p.DateDebut.Date <= end.Date);
            if (periodPrsCount <= 3)
                score += 30;
            else if (periodPrsCount >= 10)
                score -= 25;

            if (end.DayOfWeek == DayOfWeek.Friday && end.Hour > 13)
                score -= 15;

            // ===== AJOUT: Bonus de préférence historique (IA) pour l'heure de début =====
            score += GetPreferenceBoost(start);

            return Math.Max(0, score);
        }

        private string GenerateReason(DateTime start, DateTime end, int dayPrsCount, List<PrsInfo> weekSectorPrs, SlotSuggestionRequest request, SecteurInfo secteurInfo, bool hasSectorConflictThisWeek, DateTime day)
        {
            var reasons = new List<string>();
            var frenchHour = start.Hour + 2; // UTC+2

            // Vérifier les jours fériés dans les raisons
            var holidayName = GetHolidayName(start.Date);
            if (!string.IsNullOrEmpty(holidayName))
            {
                reasons.Add($"⚠️ Attention: {holidayName}");
            }

            // Vérifier les jours adjacents
            if (IsFreenchHoliday(start.AddDays(-1)))
                reasons.Add("🎉 Reprise après férié - Excellent");
            if (IsFreenchHoliday(start.AddDays(1)))
                reasons.Add("⚠️ Veille de férié - Risqué");

            if (secteurInfo.SecteurId.HasValue)
            {
                if (!hasSectorConflictThisWeek)
                    reasons.Add($"✅ Secteur {secteurInfo.SecteurNom} libre cette semaine");
                else
                {
                    var conflictDay = weekSectorPrs.FirstOrDefault()?.DateDebut.AddHours(2).ToString("dddd dd/MM", new CultureInfo("fr-FR"));
                    reasons.Add($"⚠️ Conflit secteur {secteurInfo.SecteurNom} le {conflictDay}");
                }
            }

            if (frenchHour >= 9 && frenchHour < 11)
                reasons.Add("🌅 Créneau matinal premium (9h-11h)");
            else if (frenchHour >= 11 && frenchHour < 13)
                reasons.Add("🌄 Bon créneau matinal (11h-13h)");
            else if (frenchHour >= 13 && frenchHour < 15)
                reasons.Add("🌤️ Créneau après-midi correct");

            if (dayPrsCount == 0)
                reasons.Add("📅 Journée totalement libre sur cette ligne");
            else if (dayPrsCount <= 2)
                reasons.Add($"📊 Journée peu chargée ({dayPrsCount} PRS)");
            else if (dayPrsCount >= 4)
                reasons.Add($"⚠️ Journée chargée ({dayPrsCount} PRS)");

            if (!string.IsNullOrEmpty(request.Equipement))
            {
                if (request.Equipement == "CMS" && frenchHour >= 9 && frenchHour < 11)
                    reasons.Add("🔧 Parfait pour CMS matinal (9h-11h)");
                else if (request.Equipement == "Finition" && frenchHour >= 13 && frenchHour < 15)
                    reasons.Add("✨ Idéal pour finition après-midi (13h-15h)");
            }

            // Messages pour les jours
            switch (start.DayOfWeek)
            {
                case DayOfWeek.Monday:
                    reasons.Add("🚀 Lundi - Début de semaine optimal");
                    break;
                case DayOfWeek.Tuesday:
                    reasons.Add("📈 Mardi - Journée excellente pour la production");
                    break;
                case DayOfWeek.Wednesday:
                    reasons.Add("📊 Mercredi - Milieu de semaine stable");
                    break;
                case DayOfWeek.Thursday:
                    reasons.Add("📉 Jeudi - Fin de semaine approche, moins favorable");
                    break;
                case DayOfWeek.Friday:
                    reasons.Add("⚠️ Vendredi - Attention fin de semaine peu productive");
                    break;
            }

            // ===== AJOUT: Explication IA si boost significatif =====
            var share = _historicalPrefs?.GetDayHourSharePercent(start.DayOfWeek, start.Hour) ?? 0;
            if (share >= 10) // n'ajouter l'explication que si >=10% des historiques
            {
                reasons.Add($"🤖 IA: créneau historiquement privilégié ({share:F0}% des PRS le {start.ToString("dddd", new CultureInfo("fr-FR"))} à {frenchHour:00}h, {_historicalPrefs?.Scope})");
            }

            return reasons.Count > 0 ? string.Join(", ", reasons) : "Créneau disponible";
        }

        private string GenerateMultiDayReason(DateTime start, DateTime end, SlotSuggestionRequest request, SecteurInfo secteurInfo, bool hasSectorConflict)
        {
            var reasons = new List<string>();
            var duration = (end - start).TotalHours;
            var workingDays = Math.Ceiling(duration / 8);

            reasons.Add($"📅 Période de {duration}h sur {workingDays} jours ouvrés (9h-17h FR)");

            // Vérifier fériés dans la période
            var holidaysInPeriod = new List<string>();
            for (var checkDay = start.Date; checkDay <= end.Date; checkDay = checkDay.AddDays(1))
            {
                var holidayName = GetHolidayName(checkDay);
                if (!string.IsNullOrEmpty(holidayName))
                {
                    holidaysInPeriod.Add($"{holidayName} ({checkDay:dd/MM})");
                }
            }

            if (holidaysInPeriod.Any())
            {
                reasons.Add($"🎉 Évite les fériés: {string.Join(", ", holidaysInPeriod)}");
            }

            if (!hasSectorConflict && secteurInfo.SecteurId.HasValue)
                reasons.Add($"✅ Secteur {secteurInfo.SecteurNom} entièrement libre");
            else if (hasSectorConflict)
                reasons.Add($"⚠️ Conflit détecté secteur {secteurInfo.SecteurNom}");

            // Messages multi-jour
            switch (start.DayOfWeek)
            {
                case DayOfWeek.Monday:
                    reasons.Add("🚀 Début lundi 9h - Semaine optimale et productive");
                    break;
                case DayOfWeek.Tuesday:
                    reasons.Add("📈 Début mardi 9h - Très favorable pour production");
                    break;
                case DayOfWeek.Wednesday:
                    reasons.Add("📊 Début mercredi 9h - Milieu de semaine acceptable");
                    break;
                case DayOfWeek.Thursday:
                    reasons.Add("📉 Début jeudi 9h - Fin de semaine moins favorable");
                    break;
                case DayOfWeek.Friday:
                    reasons.Add("⚠️ Début vendredi 9h - Peu recommandé");
                    break;
            }

            if (request.DurationHours == 16)
                reasons.Add("⏰ Période de 2 jours consécutifs");
            else if (request.DurationHours == 24)
                reasons.Add("📊 Période de 3 jours étalés");
            else if (request.DurationHours == 40)
                reasons.Add("📅 Semaine complète de production");

            // ===== AJOUT: Explication IA sur l'heure de démarrage =====
            var frenchHour = start.Hour + 2;
            var share = _historicalPrefs?.GetDayHourSharePercent(start.DayOfWeek, start.Hour) ?? 0;
            if (share >= 10)
            {
                reasons.Add($"🤖 IA: démarrage souvent privilégié ({share:F0}% des PRS à {frenchHour:00}h le {start.ToString("dddd", new CultureInfo("fr-FR"))}, {_historicalPrefs?.Scope})");
            }

            return string.Join(", ", reasons);
        }

        private async Task<SecteurInfo> GetSecteurInfoSecure(int ligneId)
        {
            try
            {
                var connection = _context.Database.GetDbConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT l.Id as LigneId, l.Nom as LigneNom, 
                           s.Id as SecteurId, s.Nom as SecteurNom
                    FROM [PlanifPRS].[dbo].[Lignes] l
                    LEFT JOIN [PlanifPRS].[dbo].[Secteur] s ON l.idSecteur = s.id
                    WHERE l.Id = @ligneId";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "@ligneId";
                parameter.Value = ligneId;
                command.Parameters.Add(parameter);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var secteurIdObj = reader["SecteurId"];
                    var secteurNomObj = reader["SecteurNom"];

                    int? secteurId = null;
                    if (secteurIdObj != null && secteurIdObj != DBNull.Value)
                    {
                        if (int.TryParse(secteurIdObj.ToString(), out int parsedId))
                        {
                            secteurId = parsedId;
                        }
                    }

                    return new SecteurInfo
                    {
                        LigneId = ligneId,
                        LigneNom = reader["LigneNom"]?.ToString() ?? "Ligne inconnue",
                        SecteurId = secteurId,
                        SecteurNom = secteurNomObj?.ToString() ?? "Secteur inconnu"
                    };
                }
            }
            catch (Exception)
            {
                // Erreur ignorée
            }

            return null;
        }

        private async Task<List<PrsInfo>> GetExistingPrsSecure(DateTime startDate, DateTime endDate)
        {
            var prsList = new List<PrsInfo>();

            try
            {
                var connection = _context.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT p.Id, p.DateDebut, p.DateFin, p.LigneId,
                           l.Nom as LigneNom, l.idSecteur,
                           s.id as SecteurId, s.nom as SecteurNom
                    FROM [PlanifPRS].[dbo].[PRS] p
                    LEFT JOIN [PlanifPRS].[dbo].[Lignes] l ON p.LigneId = l.Id
                    LEFT JOIN [PlanifPRS].[dbo].[Secteur] s ON l.idSecteur = s.id
                    WHERE p.DateDebut >= @startDate AND p.DateDebut <= @endDate
                    ORDER BY p.DateDebut";

                var startParam = command.CreateParameter();
                startParam.ParameterName = "@startDate";
                startParam.Value = startDate;
                command.Parameters.Add(startParam);

                var endParam = command.CreateParameter();
                endParam.ParameterName = "@endDate";
                endParam.Value = endDate;
                command.Parameters.Add(endParam);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var prsInfo = new PrsInfo();

                    if (reader["Id"] != null && reader["Id"] != DBNull.Value && int.TryParse(reader["Id"].ToString(), out int prsId))
                        prsInfo.Id = prsId;

                    if (reader["DateDebut"] != null && reader["DateDebut"] != DBNull.Value && DateTime.TryParse(reader["DateDebut"].ToString(), out DateTime dateDebut))
                        prsInfo.DateDebut = dateDebut;

                    if (reader["DateFin"] != null && reader["DateFin"] != DBNull.Value && DateTime.TryParse(reader["DateFin"].ToString(), out DateTime dateFin))
                        prsInfo.DateFin = dateFin;

                    if (reader["LigneId"] != null && reader["LigneId"] != DBNull.Value && int.TryParse(reader["LigneId"].ToString(), out int ligneId))
                        prsInfo.LigneId = ligneId;

                    if (reader["SecteurId"] != null && reader["SecteurId"] != DBNull.Value && int.TryParse(reader["SecteurId"].ToString(), out int secteurId))
                        prsInfo.SecteurId = secteurId;

                    prsInfo.SecteurNom = reader["SecteurNom"]?.ToString();
                    prsList.Add(prsInfo);
                }
            }
            catch (Exception)
            {
                // Erreur ignorée
            }

            return prsList;
        }

        private async Task<List<SlotSuggestion>> GenerateBasicSlotSuggestions(SlotSuggestionRequest request, DateTime startAnalysis, DateTime endAnalysis)
        {
            var suggestions = new List<SlotSuggestion>();
            var nowUtc = DateTime.UtcNow;

            try
            {
                var sameLigneQuery = await _context.Prs
                    .Where(p => p.LigneId == request.LigneId &&
                               p.DateDebut >= startAnalysis &&
                               p.DateDebut <= endAnalysis)
                    .Select(p => new { p.DateDebut, p.DateFin })
                    .ToListAsync();

                for (var day = startAnalysis.Date; day <= endAnalysis.Date; day = day.AddDays(1))
                {
                    // Ne considérer que les jours présents/futurs et ouvrables
                    if (day < nowUtc.Date) continue;
                    if (!IsWorkingDay(day)) continue;

                    var workStart = day.Date.AddHours(7);  // 7h UTC = 9h FR
                    var workEnd = day.Date.AddHours(15);   // 15h UTC = 17h FR
                    var duration = TimeSpan.FromHours(request.DurationHours);
                    var dayPrs = sameLigneQuery.Where(p => p.DateDebut.Date == day.Date).ToList();

                    for (var start = workStart; start.Add(duration) <= workEnd; start = start.AddHours(1))
                    {
                        var end = start.Add(duration);

                        // Ne pas proposer un créneau dans le passé
                        if (day.Date == nowUtc.Date && start < nowUtc)
                            continue;

                        bool hasConflict = dayPrs.Any(p => (start < p.DateFin && end > p.DateDebut));

                        if (!hasConflict)
                        {
                            var score = CalculateBasicScore(start, dayPrs.Count, request);
                            var reason = GenerateBasicReason(start, dayPrs.Count, request);

                            // Bonus IA de préférence aussi pour le mode basic
                            score += GetPreferenceBoost(start);
                            var share = _historicalPrefs?.GetDayHourSharePercent(start.DayOfWeek, start.Hour) ?? 0;
                            if (share >= 10)
                            {
                                var frHour = (start.Hour + 2) % 24;
                                reason = $"🤖 IA: créneau historiquement privilégié ({share:F0}% à {frHour:00}h), {reason}";
                            }

                            suggestions.Add(new SlotSuggestion
                            {
                                DateDebut = start,
                                DateFin = end,
                                Score = score,
                                Raison = reason
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Erreur ignorée
            }

            return suggestions
                .Where(s => s.DateDebut >= DateTime.UtcNow) // Filtre de sécurité: pas de dates passées
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.DateDebut)
                .Take(3)
                .ToList();
        }

        private int CalculateBasicScore(DateTime start, int dayPrsCount, SlotSuggestionRequest request)
        {
            int score = 80;
            var frenchHour = start.Hour + 2; // UTC+2

            if (frenchHour >= 9 && frenchHour < 13) score += 30; // 9h-13h FR
            if (frenchHour >= 15) score -= 20; // 15h+ FR
            if (request.Equipement == "CMS" && frenchHour >= 9 && frenchHour < 11) score += 25; // 9h-11h FR
            if (request.Equipement == "Finition" && frenchHour >= 13 && frenchHour < 15) score += 20; // 13h-15h FR
            score += Math.Max(0, 3 - dayPrsCount) * 10;

            // SYSTÈME DE SCORING PAR JOUR POUR BASIC
            switch (start.DayOfWeek)
            {
                case DayOfWeek.Monday:
                case DayOfWeek.Tuesday:
                case DayOfWeek.Wednesday:
                    score += 25;
                    break;
                case DayOfWeek.Thursday:
                    score += 10;
                    break;
                case DayOfWeek.Friday:
                    score += 5;
                    break;
            }

            // Bonus/malus fériés
            if (IsFreenchHoliday(start.AddDays(-1))) score += 25; // Après férié
            if (IsFreenchHoliday(start.AddDays(1))) score -= 15;  // Veille férié

            return Math.Max(0, score);
        }

        private string GenerateBasicReason(DateTime start, int dayPrsCount, SlotSuggestionRequest request)
        {
            var reasons = new List<string>();
            var frenchHour = start.Hour + 2; // UTC+2

            // Vérifier fériés
            if (IsFreenchHoliday(start.AddDays(-1))) reasons.Add("🎉 Reprise après férié");
            if (IsFreenchHoliday(start.AddDays(1))) reasons.Add("⚠️ Veille de férié");

            if (frenchHour >= 9 && frenchHour < 13) reasons.Add("🌅 Matinal optimal (9h-13h)");
            if (dayPrsCount == 0) reasons.Add("📅 Journée libre");
            if (request.Equipement == "CMS" && frenchHour >= 9 && frenchHour < 11) reasons.Add("🔧 CMS matinal (9h-11h)");
            if (request.Equipement == "Finition" && frenchHour >= 13 && frenchHour < 15) reasons.Add("✨ Finition après-midi (13h-15h)");

            // Messages par jour pour basic
            switch (start.DayOfWeek)
            {
                case DayOfWeek.Monday:
                    reasons.Add("🚀 Lundi optimal");
                    break;
                case DayOfWeek.Tuesday:
                    reasons.Add("📈 Mardi excellent");
                    break;
                case DayOfWeek.Wednesday:
                    reasons.Add("📊 Mercredi stable");
                    break;
                case DayOfWeek.Thursday:
                    reasons.Add("📉 Jeudi correct");
                    break;
                case DayOfWeek.Friday:
                    reasons.Add("⚠️ Vendredi moins favorable");
                    break;
            }

            return reasons.Count > 0 ? string.Join(", ", reasons) : "Disponible";
        }

        private DateTime GetStartOfWeek(DateTime date)
        {
            var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-1 * diff).Date;
        }

        // ====== APPRENTISSAGE DES PRÉFÉRENCES HISTORIQUES (IA) ======

        private async Task<HistoricalPreferences> GetHistoricalPreferencesSecure(int ligneId, int? secteurId)
        {
            var prefs = new HistoricalPreferences { Scope = "none", WindowDays = 180 };
            var windowStart = DateTime.UtcNow.Date.AddDays(-prefs.WindowDays);
            var windowEnd = DateTime.UtcNow;

            try
            {
                // 1) Essayer par ligne
                var rows = await FetchPrsRows(windowStart, windowEnd, ligneId, null);
                prefs = BuildPreferences(rows, "ligne", prefs.WindowDays);
                if (prefs.TotalCount >= 20)
                    return prefs;

                // 2) Essayer par secteur
                if (secteurId.HasValue)
                {
                    rows = await FetchPrsRows(windowStart, windowEnd, null, secteurId.Value);
                    prefs = BuildPreferences(rows, "secteur", prefs.WindowDays);
                    if (prefs.TotalCount >= 20)
                        return prefs;
                }

                // 3) Fallback global
                rows = await FetchPrsRows(windowStart, windowEnd, null, null);
                prefs = BuildPreferences(rows, "global", prefs.WindowDays);
                return prefs;
            }
            catch
            {
                return prefs;
            }
        }

        private async Task<List<PrsRow>> FetchPrsRows(DateTime startDate, DateTime endDate, int? ligneId, int? secteurId)
        {
            var rows = new List<PrsRow>();

            var connection = _context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync();

            using var command = connection.CreateCommand();
            // Base query
            var sql = @"
                SELECT p.DateDebut, p.DateFin, p.LigneId, l.idSecteur AS SecteurId
                FROM [PlanifPRS].[dbo].[PRS] p
                LEFT JOIN [PlanifPRS].[dbo].[Lignes] l ON p.LigneId = l.Id
                WHERE p.DateDebut >= @startDate AND p.DateDebut <= @endDate";

            if (ligneId.HasValue)
            {
                sql += " AND p.LigneId = @ligneId";
            }
            else if (secteurId.HasValue)
            {
                sql += " AND l.idSecteur = @secteurId";
            }

            command.CommandText = sql;

            var startParam = command.CreateParameter();
            startParam.ParameterName = "@startDate";
            startParam.Value = startDate;
            command.Parameters.Add(startParam);

            var endParam = command.CreateParameter();
            endParam.ParameterName = "@endDate";
            endParam.Value = endDate;
            command.Parameters.Add(endParam);

            if (ligneId.HasValue)
            {
                var pLigne = command.CreateParameter();
                pLigne.ParameterName = "@ligneId";
                pLigne.Value = ligneId.Value;
                command.Parameters.Add(pLigne);
            }
            else if (secteurId.HasValue)
            {
                var pSect = command.CreateParameter();
                pSect.ParameterName = "@secteurId";
                pSect.Value = secteurId.Value;
                command.Parameters.Add(pSect);
            }

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new PrsRow
                {
                    DateDebut = reader["DateDebut"] != DBNull.Value ? (DateTime)reader["DateDebut"] : DateTime.MinValue,
                    DateFin = reader["DateFin"] != DBNull.Value ? (DateTime)reader["DateFin"] : DateTime.MinValue,
                    LigneId = reader["LigneId"] != DBNull.Value ? Convert.ToInt32(reader["LigneId"]) : 0,
                    SecteurId = reader["SecteurId"] != DBNull.Value ? Convert.ToInt32(reader["SecteurId"]) : (int?)null
                };
                rows.Add(row);
            }

            return rows;
        }

        private HistoricalPreferences BuildPreferences(List<PrsRow> rows, string scope, int windowDays)
        {
            var prefs = new HistoricalPreferences { Scope = scope, WindowDays = windowDays };

            foreach (var r in rows)
            {
                // On se base sur l'heure de démarrage UTC
                var h = r.DateDebut.Hour;
                var dow = r.DateDebut.DayOfWeek;

                // On ne retient que les démarrages plausibles dans notre plage de travail (7h-15h UTC)
                if (h < 7 || h > 15) continue;

                // Comptages
                if (!prefs.HourCountsUtc.ContainsKey(h)) prefs.HourCountsUtc[h] = 0;
                prefs.HourCountsUtc[h]++;

                if (!prefs.DayCounts.ContainsKey(dow)) prefs.DayCounts[dow] = 0;
                prefs.DayCounts[dow]++;

                var key = (dow, h);
                if (!prefs.DayHourCounts.ContainsKey(key)) prefs.DayHourCounts[key] = 0;
                prefs.DayHourCounts[key]++;

                prefs.TotalCount++;
            }

            return prefs;
        }

        private int GetPreferenceBoost(DateTime start)
        {
            if (_historicalPrefs == null || _historicalPrefs.TotalCount == 0) return 0;

            var dow = start.DayOfWeek;
            var hour = start.Hour;

            // Poids principal: distribution par (jour, heure)
            var dayHourCount = _historicalPrefs.DayHourCounts.TryGetValue((dow, hour), out var dh) ? dh : 0;
            var dayTotal = _historicalPrefs.DayCounts.TryGetValue(dow, out var dt) ? dt : 0;
            double primaryRatio = dayTotal > 0 ? (double)dayHourCount / dayTotal : 0.0;

            // Poids secondaire: popularité globale de l'heure
            var hourCount = _historicalPrefs.HourCountsUtc.TryGetValue(hour, out var hc) ? hc : 0;
            double hourRatio = _historicalPrefs.TotalCount > 0 ? (double)hourCount / _historicalPrefs.TotalCount : 0.0;

            // Convertir en bonus de score (borné)
            var bonus = (int)Math.Round(primaryRatio * 25.0 + hourRatio * 10.0); // max ~35
            if (bonus > 35) bonus = 35;
            if (bonus < 0) bonus = 0;
            return bonus;
        }

        // ===== Types internes =====

        private class PrsRow
        {
            public DateTime DateDebut { get; set; }
            public DateTime DateFin { get; set; }
            public int LigneId { get; set; }
            public int? SecteurId { get; set; }
        }

        private class HistoricalPreferences
        {
            public Dictionary<int, int> HourCountsUtc { get; set; } = new Dictionary<int, int>();
            public Dictionary<DayOfWeek, int> DayCounts { get; set; } = new Dictionary<DayOfWeek, int>();
            public Dictionary<(DayOfWeek, int), int> DayHourCounts { get; set; } = new Dictionary<(DayOfWeek, int), int>();
            public int TotalCount { get; set; }
            public string Scope { get; set; } = "none";
            public int WindowDays { get; set; } = 180;

            public IEnumerable<int> GetTopHoursForDay(DayOfWeek dow, int topN)
            {
                var q = DayHourCounts
                    .Where(kv => kv.Key.Item1 == dow)
                    .OrderByDescending(kv => kv.Value)
                    .Take(topN)
                    .Select(kv => kv.Key.Item2);
                return q;
            }

            public List<int> GetTopHoursGlobal(int topN)
            {
                return HourCountsUtc
                    .OrderByDescending(kv => kv.Value)
                    .Take(topN)
                    .Select(kv => kv.Key)
                    .ToList();
            }

            public double GetDayHourSharePercent(DayOfWeek dow, int hour)
            {
                var dayTotal = DayCounts.TryGetValue(dow, out var dt) ? dt : 0;
                if (dayTotal == 0) return 0;
                var dayHour = DayHourCounts.TryGetValue((dow, hour), out var dh) ? dh : 0;
                return (double)dayHour / dayTotal * 100.0;
            }
        }
    }

    public class SecteurInfo
    {
        public int LigneId { get; set; }
        public string LigneNom { get; set; }
        public int? SecteurId { get; set; }
        public string SecteurNom { get; set; }
    }

    public class PrsInfo
    {
        public int Id { get; set; }
        public DateTime DateDebut { get; set; }
        public DateTime DateFin { get; set; }
        public int LigneId { get; set; }
        public int? SecteurId { get; set; }
        public string SecteurNom { get; set; }
    }

    public class SlotSuggestionRequest
    {
        public int LigneId { get; set; }
        public string Equipement { get; set; }
        public int DurationHours { get; set; } = 1;
    }

    public class SlotSuggestion
    {
        public DateTime DateDebut { get; set; }
        public DateTime DateFin { get; set; }
        public int Score { get; set; }
        public string Raison { get; set; }
    }
}