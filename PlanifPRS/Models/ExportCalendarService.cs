using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Data;
using PlanifPRS.Models;

namespace PlanifPRS.Services
{
    /// <summary>
    /// Export "Calendrier PRS" – version horizontale (style planning).
    /// - 1 feuille par semaine : Sxx
    /// - 6 jours (Lundi→Samedi) disposés horizontalement
    /// - Par jour : 5 colonnes (Jour | Date | Chef | Quantités | Ligne)
    /// - Chaque PRS possède son propre bloc :
    ///       Ligne 1 (jaune) : Jour | Date | Chef | Quantités | Ligne (spécifiques à cette PRS)
    ///       Ligne 2 : Cellule fusionnée avec le titre (fond vert si Validé)
    /// - PRS avec statut "Supprimé" ignorées
    /// - Fallback sans RichText (version de ClosedXML ne l’expose pas)
    /// </summary>
    public class ExportCalendarService
    {
        private readonly PlanifPrsDbContext _context;

        private const int DAY_COUNT = 6;                  // Lundi..Samedi
        private const int DAY_COLUMNS = 5;                // Jour | Date | Chef | Quantités | Ligne
        private const int DAY_SEPARATOR_COLUMNS = 1;      // 1 colonne vide entre jours (sauf après le dernier)

        // Couleurs
        private static readonly XLColor ColorPrsValidated = XLColor.FromHtml("#92D050");
        private static readonly XLColor ColorDayHeader = XLColor.FromHtml("#FFF200");
        private static readonly XLColor ColorSeparatorBackground = XLColor.White;

        // Palette segments quantités (non utilisée en fallback sans RichText)
        private static readonly XLColor[] QuantitySegmentColors = new[]
        {
            XLColor.Black,
            XLColor.Magenta,
            XLColor.FromHtml("#1F4E78"),
            XLColor.Green
        };

        public ExportCalendarService(PlanifPrsDbContext context)
        {
            _context = context;
        }

        public async Task<byte[]> ExportSemainesAsync(int year, IEnumerable<int> semainesIso, string? requesterLogin)
        {
            var weeks = semainesIso.Distinct().OrderBy(w => w).ToList();
            if (!weeks.Any())
                throw new ArgumentException("Aucune semaine fournie.", nameof(semainesIso));

            if (!string.IsNullOrWhiteSpace(requesterLogin) && !await IsAdminOrValidateur(requesterLogin))
                throw new UnauthorizedAccessException("Droits insuffisants pour exporter le calendrier.");

            DateTime yearStart = new(year, 1, 1);
            DateTime yearEnd = new(year, 12, 31, 23, 59, 59);

            // Liste des équipements à exclure (filtrage EN MÉMOIRE pour éviter le bug SQL dû à Contains)
            var excludedEquipements = new[] { "Visite Client", "Intervention", "Audit" };
            var excludedEquipementsSet = new HashSet<string>(excludedEquipements, StringComparer.OrdinalIgnoreCase);

            // PRS filtrées (date + statut) — PAS de filtrage Equipement ici pour éviter le pattern SQL problématique
            var prsRaw = await _context.Prs
                .Include(p => p.Ligne)
                .AsNoTracking()
                .Where(p => p.DateDebut >= yearStart
                            && p.DateDebut <= yearEnd
                            && (p.Statut == null || p.Statut.ToUpper() != "SUPPRIMÉ"))
                .ToListAsync();

            // Filtrage en mémoire des équipements exclus
            prsRaw = prsRaw
                .Where(p => string.IsNullOrWhiteSpace(p.Equipement) || !excludedEquipementsSet.Contains(p.Equipement!))
                .ToList();

            var logins = prsRaw.Where(p => !string.IsNullOrWhiteSpace(p.CreatedByLogin))
                               .Select(p => p.CreatedByLogin!)
                               .Distinct()
                               .ToList();

            var allActive = await _context.Utilisateurs
                .AsNoTracking()
                .Where(u => !u.DateDeleted.HasValue)
                .ToListAsync();

            var loginSet = new HashSet<string>(
                logins.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var users = allActive.Where(u => loginSet.Contains(u.LoginWindows)).ToList();

            var prenomsByLogin = users
                .GroupBy(u => u.LoginWindows)
                .ToDictionary(g => g.Key, g => g.First().Prenom ?? "");

            // Groupement par semaine / jour
            var prsByWeekDay = prsRaw
                .Select(p => new
                {
                    Prs = p,
                    Week = ISOWeek.GetWeekOfYear(p.DateDebut),
                    Day = p.DateDebut.Date
                })
                .Where(x => weeks.Contains(x.Week))
                .GroupBy(x => x.Week)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(z => z.Day)
                          .ToDictionary(dg => dg.Key, dg => dg.Select(v => v.Prs)
                                                              .OrderBy(v => v.DateDebut)
                                                              .ThenBy(v => v.Id)
                                                              .ToList())
                );

            using var wb = new XLWorkbook();

            foreach (var w in weeks)
            {
                var ws = wb.Worksheets.Add($"S{w:00}");
                BuildWeekSheetHorizontalMatrix(ws, year, w,
                    prsByWeekDay.GetValueOrDefault(w, new Dictionary<DateTime, List<Prs>>()),
                    prenomsByLogin);
            }

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        // ===================== LAYOUT HORIZONTAL (par blocs par PRS) =====================
        private void BuildWeekSheetHorizontalMatrix(IXLWorksheet ws,
                                                    int year,
                                                    int isoWeek,
                                                    Dictionary<DateTime, List<Prs>> prsByDay,
                                                    Dictionary<string, string> prenomsByLogin)
        {
            int totalCols = TotalColumns();

            // Titre semaine
            ws.Range(1, 1, 1, totalCols).Merge().Value = $"Calendrier PRS - Semaine {isoWeek:00}";
            var titleCell = ws.Cell(1, 1);
            titleCell.Style.Font.Bold = true;
            titleCell.Style.Font.FontSize = 16;
            titleCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            titleCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Row(1).Height = 22;

            // Prépare listes PRS par jour
            var dayInfos = new List<(DayOfWeek dow, DateTime date, List<DayPrsBlock> blocks)>();
            foreach (var dow in new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday
            })
            {
                var (d, ok) = GetDateForIsoWeekAndDay(year, isoWeek, dow);
                if (!ok) continue;
                prsByDay.TryGetValue(d, out var listForDay);
                var blocks = BuildDayPrsBlocks(d, listForDay ?? new List<Prs>(), prenomsByLogin);
                dayInfos.Add((dow, d, blocks));
            }

            // Curseur de ligne indépendant pour chaque jour (ligne 3 = première ligne disponible)
            var dayRow = new int[dayInfos.Count];
            for (int i = 0; i < dayRow.Length; i++) dayRow[i] = 3;

            for (int dayIndex = 0; dayIndex < dayInfos.Count; dayIndex++)
            {
                var (dow, date, blocks) = dayInfos[dayIndex];
                int startCol = StartColumnForDay(dayIndex);

                // Largeurs colonnes fixées
                SetDayColumnWidths(ws, startCol);

                if (blocks.Count == 0)
                {
                    int rHeader = dayRow[dayIndex];
                    WriteDayHeaderForBlock(ws, rHeader, startCol, dow, date, chef: "", quantities: "", ligne: "");
                    int rPrs = rHeader + 1;
                    ws.Range(rPrs, startCol, rPrs, startCol + DAY_COLUMNS - 1).Merge();
                    ws.Cell(rPrs, startCol).Value = "Aucune PRS";
                    ws.Cell(rPrs, startCol).Style.Font.Italic = true;
                    ws.Cell(rPrs, startCol).Style.Font.FontColor = XLColor.Gray;
                    ws.Cell(rPrs, startCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(rPrs, startCol).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    dayRow[dayIndex] = rPrs + 1; // prochaine position
                }
                else
                {
                    foreach (var block in blocks)
                    {
                        int rHeader = dayRow[dayIndex];
                        WriteDayHeaderForBlock(ws,
                                               rHeader,
                                               startCol,
                                               dow,
                                               date,
                                               chef: block.PrimaryChefProjetPrenom ?? "",
                                               quantities: block.QuantitiesDisplay ?? "",
                                               ligne: block.LigneDisplay ?? "");

                        int rPrs = rHeader + 1;
                        ws.Range(rPrs, startCol, rPrs, startCol + DAY_COLUMNS - 1).Merge();
                        var cell = ws.Cell(rPrs, startCol);
                        ApplyTitleRichText(cell, block.TitleDisplay);
                        if (block.IsValidated)
                            cell.Style.Fill.BackgroundColor = ColorPrsValidated;
                        // Centrage du nom de la PRS
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                        cell.Style.Alignment.WrapText = true;
                        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

                        dayRow[dayIndex] = rPrs + 1; // Avance au prochain bloc
                    }
                }

                // Colonne séparateur
                if (dayIndex < dayInfos.Count - 1)
                {
                    int sepCol = startCol + DAY_COLUMNS;
                    ws.Column(sepCol).Width = 2.0;
                }
            }

            // Styliser séparateurs sur la hauteur max
            int maxUsedRow = dayRow.Length == 0 ? 3 : dayRow.Max() - 1;
            for (int dayIndex = 0; dayIndex < dayInfos.Count - 1; dayIndex++)
            {
                int sepCol = StartColumnForDay(dayIndex) + DAY_COLUMNS;
                var sepRange = ws.Range(3, sepCol, Math.Max(3, maxUsedRow), sepCol);
                sepRange.Style.Fill.BackgroundColor = ColorSeparatorBackground;
            }

            // Geler la ligne 2 (titre seulement)
            ws.SheetView.FreezeRows(2);
        }

        private void WriteDayHeaderForBlock(IXLWorksheet ws,
                                            int row,
                                            int startCol,
                                            DayOfWeek dow,
                                            DateTime date,
                                            string chef,
                                            string quantities,
                                            string ligne)
        {
            ws.Cell(row, startCol + 0).Value = GetFrenchDayName(dow);
            ws.Cell(row, startCol + 1).Value = date;
            ws.Cell(row, startCol + 1).Style.DateFormat.Format = "dd/MM/yyyy";
            ws.Cell(row, startCol + 2).Value = chef;
            if (!string.IsNullOrWhiteSpace(quantities))
                WriteQuantitiesRich(ws.Cell(row, startCol + 3), quantities);
            else
                ws.Cell(row, startCol + 3).Value = "";
            ws.Cell(row, startCol + 4).Value = ligne;

            var range = ws.Range(row, startCol, row, startCol + DAY_COLUMNS - 1);
            range.Style.Fill.BackgroundColor = ColorDayHeader;
            range.Style.Font.Bold = true;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        }

        private void ApplyTitleRichText(IXLCell cell, string title)
        {
            cell.Value = title;
            if (title.IndexOf("PV", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.Magenta;
            }
        }

        private void WriteQuantitiesRich(IXLCell cell, string chain)
        {
            cell.Value = chain;
            cell.Style.Alignment.WrapText = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        }

        private int StartColumnForDay(int dayIndex)
        {
            return dayIndex * (DAY_COLUMNS + DAY_SEPARATOR_COLUMNS) + 1;
        }

        private int TotalColumns()
        {
            return DAY_COUNT * DAY_COLUMNS + (DAY_COUNT - 1) * DAY_SEPARATOR_COLUMNS;
        }

        private void SetDayColumnWidths(IXLWorksheet ws, int startCol)
        {
            ws.Column(startCol + 0).Width = 10;
            ws.Column(startCol + 1).Width = 11;
            ws.Column(startCol + 2).Width = 13;
            ws.Column(startCol + 3).Width = 30;
            ws.Column(startCol + 4).Width = 14;
        }

        private List<DayPrsBlock> BuildDayPrsBlocks(DateTime dayDate, List<Prs> prsList, Dictionary<string, string> prenomsByLogin)
        {
            var result = new List<DayPrsBlock>();
            foreach (var prs in prsList)
            {
                var prenom = (prs.CreatedByLogin != null && prenomsByLogin.TryGetValue(prs.CreatedByLogin, out var pr))
                    ? pr
                    : "";

                var block = new DayPrsBlock
                {
                    Date = dayDate,
                    IsValidated = string.Equals(prs.Statut, "Validé", StringComparison.OrdinalIgnoreCase),
                    PrimaryChefProjetPrenom = prenom,
                    QuantitiesDisplay = BuildQuantitiesChain(prs),
                    LigneDisplay = prs.Ligne?.Nom ?? "",
                    ReferenceDisplay = BuildReference(prs),
                };
                block.TitleDisplay = block.ReferenceDisplay ?? $"PRS {prs.Id}";
                result.Add(block);
            }
            return result;
        }

        private string BuildQuantitiesChain(Prs prs)
        {
            var type = prs.GetType();
            string[] preferenceOrder =
            {
                "Quantite","Qte","QtePlan","QuantitePlan","QteInitiale","QuantiteInitiale",
                "QtePrevue","QuantitePrevue","QteRevisee","QuantiteRevisee",
                "QteVersion","QuantiteVersion","QteActuelle","QteActuelle",
                "QteFinale","QteFinale","QteRealisee","QteRealisee","QteProd","QuantiteProduite"
            };

            var numericTypes = new HashSet<Type>
            {
                typeof(int), typeof(int?),
                typeof(long), typeof(long?),
                typeof(double), typeof(double?),
                typeof(decimal), typeof(decimal?),
                typeof(float), typeof(float?)
            };

            var props = type.GetProperties()
                .Where(p =>
                    numericTypes.Contains(p.PropertyType) &&
                    (p.Name.IndexOf("Qte", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     p.Name.IndexOf("Quant", StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            if (props.Count == 0)
                return "";

            props = props
                .OrderBy(p =>
                {
                    int idx = Array.FindIndex(preferenceOrder,
                        pref => string.Equals(pref, p.Name, StringComparison.OrdinalIgnoreCase));
                    return idx == -1 ? int.MaxValue : idx;
                })
                .ThenBy(p => p.Name)
                .ToList();

            var values = new List<string>();
            foreach (var p in props)
            {
                object raw = p.GetValue(prs, null);
                if (raw == null) continue;
                double? num;
                try { num = Convert.ToDouble(raw, CultureInfo.InvariantCulture); }
                catch { num = null; }
                if (num == null) continue;

                if (values.Count > 0)
                {
                    if (double.TryParse(values[^1].Split(' ')[0],
                            NumberStyles.Any, CultureInfo.InvariantCulture, out double lastVal))
                    {
                        if (Math.Abs(lastVal - num.Value) < 0.0001)
                            continue;
                    }
                }

                string formatted = Math.Abs(num.Value - Math.Round(num.Value)) < 0.0001
                    ? ((int)Math.Round(num.Value)).ToString(CultureInfo.InvariantCulture) + " pcs"
                    : num.Value.ToString("0.##", CultureInfo.InvariantCulture) + " pcs";

                values.Add(formatted);
            }

            if (values.Count <= 1)
                return values.Count == 0 ? "" : values[0];
            return string.Join(" => ", values);
        }

        private string BuildReference(Prs prs)
        {
            return !string.IsNullOrWhiteSpace(prs.Titre) ? prs.Titre : $"PRS {prs.Id}";
        }

        private (DateTime date, bool ok) GetDateForIsoWeekAndDay(int year, int isoWeek, DayOfWeek dow)
        {
            try
            {
                var date = ISOWeek.ToDateTime(year, isoWeek, dow);
                return (date, true);
            }
            catch
            {
                return (DateTime.MinValue, false);
            }
        }

        private static string GetFrenchDayName(DayOfWeek dow)
        {
            switch (dow)
            {
                case DayOfWeek.Monday: return "Lundi";
                case DayOfWeek.Tuesday: return "Mardi";
                case DayOfWeek.Wednesday: return "Mercredi";
                case DayOfWeek.Thursday: return "Jeudi";
                case DayOfWeek.Friday: return "Vendredi";
                case DayOfWeek.Saturday: return "Samedi";
                case DayOfWeek.Sunday: return "Dimanche";
                default: return dow.ToString();
            }
        }

        private async Task<bool> IsAdminOrValidateur(string login)
        {
            if (string.IsNullOrWhiteSpace(login)) return false;

            var normalized = NormalizeLogin(login);
            var lowerNorm = normalized.ToLowerInvariant();
            var lowerOriginal = login.Trim().ToLowerInvariant();

            var user = await _context.Utilisateurs
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.LoginWindows.ToLower() == lowerNorm && !u.DateDeleted.HasValue);

            if (user == null)
            {
                user = await _context.Utilisateurs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.LoginWindows.ToLower() == lowerOriginal && !u.DateDeleted.HasValue);
            }

            if (user == null) return false;

            var droitsRaw = (user.Droits ?? "").Trim();
            if (string.IsNullOrEmpty(droitsRaw)) return false;

            var droitsLower = droitsRaw.ToLowerInvariant();
            var tokens = droitsLower
                .Split(new[] { ';', ',', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim());

            foreach (var t in tokens)
            {
                if (t == "admin" || t == "validateur") return true;
                if (t.StartsWith("admin")) return true;
                if (t.StartsWith("validat")) return true;
            }

            if (droitsLower.Contains("admin") || droitsLower.Contains("validat"))
                return true;

            return false;
        }

        private string NormalizeLogin(string login)
        {
            login = login.Trim();
            if (login.Contains("\\"))
            {
                var parts = login.Split('\\');
                login = parts[^1];
            }
            if (login.Contains("@"))
            {
                login = login.Split('@')[0];
            }
            return login.ToLowerInvariant();
        }

        private class DayPrsBlock
        {
            public DateTime Date { get; set; }
            public bool IsValidated { get; set; }
            public string? PrimaryChefProjetPrenom { get; set; }
            public string? QuantitiesDisplay { get; set; }
            public string? LigneDisplay { get; set; }
            public string? ReferenceDisplay { get; set; }
            public string TitleDisplay { get; set; } = "";
        }
    }
}