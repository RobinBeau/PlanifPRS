using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlanifPRS.Models;
using PlanifPRS.Data;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net; // Pour WebUtility.HtmlDecode
using System.Globalization; // Pour les semaines

namespace PlanifPRS.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class EventsController : ControllerBase
    {
        private readonly PlanifPrsDbContext _context;

        public EventsController(PlanifPrsDbContext context)
        {
            _context = context;
        }

        // MÉTHODE UTILITAIRE POUR DÉCODER LES ENTITÉS HTML
        private static string DecodeHtmlEntities(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return WebUtility.HtmlDecode(input);
        }

        private string GetEventTitle(Prs prs)
        {
            string statutIcon = prs.Statut switch
            {
                "Validé" => "✅",
                "En attente" => "",
                "En retard" => "🕒",
                _ => ""
            };
            return $"{statutIcon} {prs.Titre} - {prs.ReferenceProduit} ({prs.DateDebut:HH:mm}-{prs.DateFin:HH:mm})";
        }

        // MÉTHODE POUR LE MAIL HEBDOMADAIRE
        [HttpGet("week")]
        public async Task<IActionResult> GetWeeklyPrs(string week)
        {
            try
            {
                if (string.IsNullOrEmpty(week))
                {
                    return BadRequest("Le paramètre 'week' est requis au format YYYY-WXX");
                }

                // Parser la semaine (format YYYY-WXX)
                if (!TryParseWeek(week, out DateTime weekStart, out DateTime weekEnd))
                {
                    return BadRequest("Format de semaine invalide. Utilisez YYYY-WXX (ex: 2025-W01)");
                }

                var sql = @"
                    SELECT 
                        p.Id,
                        p.Titre,
                        p.ReferenceProduit,
                        p.DateDebut,
                        p.DateFin,
                        p.Statut,
                        p.Equipement,
                        p.InfoDiverses,
                        p.BesoinOperateur,
                        p.PresenceClient,
                        p.LigneId,
                        p.FamilleId,
                        p.CouleurPRS,
                        l.Nom as LigneNom,
                        l.idSecteur,
                        s.nom as SecteurNom,
                        f.Libelle as FamilleLibelle
                    FROM [PlanifPRS].[dbo].[Prs] p
                    LEFT JOIN [PlanifPRS].[dbo].[Lignes] l ON p.LigneId = l.Id
                    LEFT JOIN [PlanifPRS].[dbo].[Secteur] s ON l.idSecteur = s.id
                    LEFT JOIN [PlanifPRS].[dbo].[PRS_Famille] f ON p.FamilleId = f.Id
                    WHERE p.DateDebut >= @weekStart AND p.DateDebut < @weekEnd
                    ORDER BY p.DateDebut, l.Nom";

                var connection = _context.Database.GetDbConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = sql;

                var startParam = command.CreateParameter();
                startParam.ParameterName = "@weekStart";
                startParam.Value = weekStart;
                command.Parameters.Add(startParam);

                var endParam = command.CreateParameter();
                endParam.ParameterName = "@weekEnd";
                endParam.Value = weekEnd;
                command.Parameters.Add(endParam);

                using var reader = await command.ExecuteReaderAsync();
                var prsData = new List<object>();

                while (await reader.ReadAsync())
                {
                    try
                    {
                        var titre = DecodeHtmlEntities(reader["Titre"]?.ToString()) ?? "Sans titre";
                        var reference = DecodeHtmlEntities(reader["ReferenceProduit"]?.ToString()) ?? "";
                        var dateDebut = reader["DateDebut"] != DBNull.Value ? (DateTime)reader["DateDebut"] : DateTime.Now;
                        var dateFin = reader["DateFin"] != DBNull.Value ? (DateTime)reader["DateFin"] : DateTime.Now;
                        var equipement = DecodeHtmlEntities(reader["Equipement"]?.ToString()) ?? "";
                        var ligneNom = DecodeHtmlEntities(reader["LigneNom"]?.ToString()) ?? "N/A";
                        var secteurNom = DecodeHtmlEntities(reader["SecteurNom"]?.ToString()) ?? "Non défini";
                        var infoDiverses = DecodeHtmlEntities(reader["InfoDiverses"]?.ToString()) ?? "";
                        var besoinOperateur = DecodeHtmlEntities(reader["BesoinOperateur"]?.ToString()) ?? "";
                        var presenceClient = DecodeHtmlEntities(reader["PresenceClient"]?.ToString()) ?? "Client absent";
                        var couleurPRS = reader["CouleurPRS"]?.ToString(); // Récupération de la couleur PRS

                        // CONSTRUIRE LA DESCRIPTION POUR LE MAIL
                        var description = titre;
                        if (!string.IsNullOrEmpty(reference))
                            description += $" {reference}";

                        // CONSTRUIRE LES COMMENTAIRES
                        var commentaires = new List<string>();

                        if (!string.IsNullOrEmpty(infoDiverses))
                            commentaires.Add(infoDiverses);

                        if (!string.IsNullOrEmpty(besoinOperateur) && besoinOperateur != "Aucun")
                            commentaires.Add($"({besoinOperateur})");

                        if (presenceClient == "Client présent")
                            commentaires.Add("(Présence client requise)");

                        if (dateDebut.TimeOfDay != TimeSpan.Zero)
                            commentaires.Add($"Démarrage à {dateDebut:HH}h");

                        var prs = new
                        {
                            id = reader["Id"],
                            dateDebut = dateDebut.ToString("yyyy-MM-ddTHH:mm:ss"),
                            dateFin = dateFin.ToString("yyyy-MM-ddTHH:mm:ss"),
                            description = description,
                            ligne = ligneNom,
                            secteur = secteurNom,
                            commentaires = string.Join(" - ", commentaires),
                            presenceClient = presenceClient,
                            equipement = equipement,
                            besoinOperateur = besoinOperateur,
                            couleurPRS = couleurPRS // Inclure la couleur PRS dans la réponse
                        };

                        prsData.Add(prs);
                    }
                    catch (Exception ex)
                    {
                        // Continue avec l'événement suivant
                        continue;
                    }
                }

                return Ok(prsData);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    error = ex.Message,
                    details = "Erreur lors du chargement des PRS hebdomadaires"
                });
            }
        }

        // MÉTHODE UTILITAIRE POUR PARSER LA SEMAINE
        private bool TryParseWeek(string weekString, out DateTime weekStart, out DateTime weekEnd)
        {
            weekStart = DateTime.MinValue;
            weekEnd = DateTime.MinValue;

            try
            {
                // Format attendu: YYYY-WXX (ex: 2025-W01)
                if (!weekString.StartsWith("20") || !weekString.Contains("-W"))
                    return false;

                var parts = weekString.Split('-');
                if (parts.Length != 2 || !parts[1].StartsWith("W"))
                    return false;

                if (!int.TryParse(parts[0], out int year))
                    return false;

                if (!int.TryParse(parts[1].Substring(1), out int weekNumber))
                    return false;

                // ✅ UTILISER LA MÊME LOGIQUE ISO QUE LE FRONTEND
                // Calculer le premier jour (lundi) de la semaine ISO
                var jan4 = new DateTime(year, 1, 4);
                var daysFromMonday = ((int)jan4.DayOfWeek + 6) % 7; // Lundi = 0
                var firstMondayOfYear = jan4.AddDays(-daysFromMonday);

                // Calculer le début de la semaine demandée
                weekStart = firstMondayOfYear.AddDays((weekNumber - 1) * 7);
                weekEnd = weekStart.AddDays(7);

                return true;
            }
            catch
            {
                return false;
            }
        }

        // Test avec le bon nom de table
        [HttpGet("test-famille")]
        public async Task<IActionResult> GetTestFamille()
        {
            try
            {
                var sql = "SELECT Id, Libelle, CouleurHex FROM [PlanifPRS].[dbo].[PRS_Famille]";

                var connection = _context.Database.GetDbConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = sql;
                using var reader = await command.ExecuteReaderAsync();

                var results = new List<object>();
                while (await reader.ReadAsync())
                {
                    results.Add(new
                    {
                        Id = reader["Id"],
                        Libelle = DecodeHtmlEntities(reader["Libelle"]?.ToString()), // DÉCODAGE
                        CouleurHex = reader["CouleurHex"]
                    });
                }

                return Ok(new
                {
                    message = "Test table PRS_Famille (avec underscore)",
                    results = results
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Version principale AVEC les couleurs des familles, InfoDiverses, et CouleurPRS
        [HttpGet]
        public async Task<IActionResult> GetPrs(DateTime? start, DateTime? end)
        {
            try
            {
                var sql = @"
                    SELECT 
                        p.Id,
                        p.Titre,
                        p.ReferenceProduit,
                        p.DateDebut,
                        p.DateFin,
                        p.Statut,
                        p.Equipement,
                        p.InfoDiverses,
                        p.LigneId,
                        p.FamilleId,
                        p.CouleurPRS,
                        p.AncienneDateDebut,
                        p.AncienneDateFin,
                        l.Nom as LigneNom,
                        l.idSecteur,
                        s.nom as SecteurNom,
                        f.Libelle as FamilleLibelle,
                        f.CouleurHex as FamilleCouleur
                    FROM [PlanifPRS].[dbo].[Prs] p
                    LEFT JOIN [PlanifPRS].[dbo].[Lignes] l ON p.LigneId = l.Id
                    LEFT JOIN [PlanifPRS].[dbo].[Secteur] s ON l.idSecteur = s.id
                    LEFT JOIN [PlanifPRS].[dbo].[PRS_Famille] f ON p.FamilleId = f.Id";

                if (start.HasValue && end.HasValue)
                {
                    sql += " WHERE p.DateDebut < @end AND p.DateFin > @start";
                }

                sql += " ORDER BY p.DateDebut";

                var connection = _context.Database.GetDbConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = sql;

                if (start.HasValue && end.HasValue)
                {
                    var startParam = command.CreateParameter();
                    startParam.ParameterName = "@start";
                    startParam.Value = start.Value;
                    command.Parameters.Add(startParam);

                    var endParam = command.CreateParameter();
                    endParam.ParameterName = "@end";
                    endParam.Value = end.Value;
                    command.Parameters.Add(endParam);
                }

                using var reader = await command.ExecuteReaderAsync();
                var events = new List<object>();

                while (await reader.ReadAsync())
                {
                    try
                    {
                        // DÉCODAGE HTML DE TOUS LES CHAMPS TEXTE
                        var titre = DecodeHtmlEntities(reader["Titre"]?.ToString()) ?? "Sans titre";
                        var reference = DecodeHtmlEntities(reader["ReferenceProduit"]?.ToString()) ?? "Sans ref";
                        var statut = DecodeHtmlEntities(reader["Statut"]?.ToString()) ?? "";
                        var dateDebut = reader["DateDebut"] != DBNull.Value ? (DateTime)reader["DateDebut"] : DateTime.Now;
                        var dateFin = reader["DateFin"] != DBNull.Value ? (DateTime)reader["DateFin"] : DateTime.Now;
                        var equipement = DecodeHtmlEntities(reader["Equipement"]?.ToString()) ?? "";
                        var ligneNom = DecodeHtmlEntities(reader["LigneNom"]?.ToString()) ?? "N/A";
                        var secteurNom = DecodeHtmlEntities(reader["SecteurNom"]?.ToString()) ?? "Non défini";
                        var familleLibelle = DecodeHtmlEntities(reader["FamilleLibelle"]?.ToString()) ?? "Non défini";
                        var familleCouleur = reader["FamilleCouleur"]?.ToString() ?? "#009dff";
                        // Récupération de la couleur PRS personnalisée
                        var couleurPRS = reader["CouleurPRS"]?.ToString();

                        // DÉCODAGE HTML pour InfoDiverses
                        var infoDiverses = DecodeHtmlEntities(reader["InfoDiverses"]?.ToString()) ?? "";

                        string statutIcon = statut switch
                        {
                            "Validé" => "✅",
                            "En attente" => "",
                            "En retard" => "🕒",
                            _ => ""
                        };

                        // DÉTECTION DU TYPE D'ÉVÉNEMENT AMÉLIORÉE
                        string eventType = "Autre";

                        // Vérifier d'abord par l'équipement (priorité aux événements)
                        if (!string.IsNullOrEmpty(equipement))
                        {
                            if (equipement == "Audit")
                                eventType = "Audit";
                            else if (equipement == "Intervention")
                                eventType = "Intervention";
                            else if (equipement == "Visite Client")
                                eventType = "Visite Client";
                            else if (equipement.StartsWith("CMS") || equipement.Contains("CMS"))
                                eventType = "PRS CMS";
                            else if (equipement.StartsWith("Finition") || equipement.Contains("Finition"))
                                eventType = "PRS Finition";
                        }

                        // Si pas trouvé dans l'équipement, vérifier dans le titre
                        if (eventType == "Autre" && !string.IsNullOrEmpty(titre))
                        {
                            if (titre.Contains("Audit"))
                                eventType = "Audit";
                            else if (titre.Contains("Intervention") || titre.Contains("Maintenance"))
                                eventType = "Intervention";
                            else if (titre.Contains("Visite") || titre.Contains("Client"))
                                eventType = "Visite Client";
                        }

                        // VÉRIFIER AUSSI PAR LA FAMILLE EN DERNIER RECOURS
                        if (eventType == "Autre" && !string.IsNullOrEmpty(familleLibelle))
                        {
                            if (familleLibelle.Contains("Audit"))
                                eventType = "Audit";
                            else if (familleLibelle.Contains("Intervention"))
                                eventType = "Intervention";
                            else if (familleLibelle.Contains("Visite"))
                                eventType = "Visite Client";
                            else if (familleLibelle.Contains("Absence"))
                                eventType = "Absence";
                        }

                        string ComputeTextColor(string hexColor)
                        {
                            if (string.IsNullOrEmpty(hexColor) || !hexColor.StartsWith("#") || hexColor.Length != 7)
                                return "#ffffff";

                            try
                            {
                                var r = Convert.ToInt32(hexColor.Substring(1, 2), 16);
                                var g = Convert.ToInt32(hexColor.Substring(3, 2), 16);
                                var b = Convert.ToInt32(hexColor.Substring(5, 2), 16);

                                var luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                                return luminance > 186 ? "#000000" : "#ffffff";
                            }
                            catch
                            {
                                return "#ffffff";
                            }
                        }

                        var eventObj = new
                        {
                            id = reader["Id"],
                            title = $"{statutIcon} {titre} - {reference} ({dateDebut:HH:mm}-{dateFin:HH:mm})",
                            start = dateDebut.ToString("s"),
                            end = dateFin.ToString("s"),
                            color = familleCouleur,
                            textColor = ComputeTextColor(familleCouleur),
                            extendedProps = new
                            {
                                type = eventType, // UTILISER LE TYPE DÉTECTÉ
                                statut = statut,
                                equipement = equipement,
                                familleLibelle = familleLibelle,
                                familyColor = familleCouleur,
                                ligne = ligneNom,
                                secteur = secteurNom,
                                infoDiverses = infoDiverses,
                                couleurPRS = couleurPRS, // Inclure la couleur PRS dans les propriétés étendues
                                ancienneDateDebut = reader["AncienneDateDebut"] != DBNull.Value ? ((DateTime)reader["AncienneDateDebut"]).ToString("s") : null,
                                ancienneDateFin = reader["AncienneDateFin"] != DBNull.Value ? ((DateTime)reader["AncienneDateFin"]).ToString("s") : null

                            }
                        };

                        events.Add(eventObj);
                    }
                    catch (Exception ex)
                    {
                        // Continue avec l'événement suivant
                        continue;
                    }
                }

                return Ok(events);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    error = ex.Message,
                    details = "Erreur lors du chargement des événements",
                    stackTrace = ex.StackTrace
                });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CreatePrs([FromBody] Prs newPrs)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                newPrs.DateCreation = DateTime.Now;
                newPrs.DerniereModification = DateTime.Now;

                _context.Prs.Add(newPrs);
                await _context.SaveChangesAsync();

                return Ok(new { success = true, id = newPrs.Id });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("week-users")]
        public async Task<IActionResult> GetWeekUsers(string week)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(week))
                    return BadRequest("Paramètre 'week' requis");

                if (!TryParseWeek(week, out DateTime weekStart, out DateTime weekEnd))
                    return BadRequest("Format de semaine invalide. Utilisez YYYY-WXX (ex: 2025-W01)");

                // SCHÉMA UTILISÉ :
                // Prs(Id, DateDebut, ...)
                // PrsAffectations(Id, PrsId, UtilisateurId, GroupeId, TypeAffectation)  (TypeAffectation = 'Utilisateur' ou 'Groupe')
                // GroupeUtilisateurs(GroupeId, UtilisateurId)
                // Utilisateurs(Id, Nom, Prenom, Mail, Service, DateDeleted)
                //
                // OPTIONNEL (si existe) : Groupes(Id, Nom, DateDeleted) -> tu peux décommenter la jointure et sélection associée.

                var sql = @"
WITH WeekPrs AS (
    SELECT p.Id
    FROM [PlanifPRS].[dbo].[Prs] p
    WHERE p.DateDebut >= @weekStart AND p.DateDebut < @weekEnd
),

-- Affectations directes
DirectUsers AS (
    SELECT DISTINCT
        u.Id                    AS UserId,
        u.Nom,
        u.Prenom,
        u.Mail,
        u.Service,
        CAST(NULL AS INT)       AS GroupeId,
        CAST(NULL AS NVARCHAR(150)) AS GroupeNom,
        1                       AS FlagDirect,
        0                       AS FlagGroup
    FROM WeekPrs w
    INNER JOIN [PlanifPRS].[dbo].[PrsAffectations] pa ON pa.PrsId = w.Id
    INNER JOIN [PlanifPRS].[dbo].[Utilisateurs] u ON u.Id = pa.UtilisateurId
    WHERE pa.UtilisateurId IS NOT NULL
      AND (pa.TypeAffectation = 'Utilisateur' OR pa.TypeAffectation IS NULL)
      AND u.Mail IS NOT NULL
      AND u.Mail <> ''
      AND u.DateDeleted IS NULL
),

-- Utilisateurs issus des groupes affectés
GroupUsers AS (
    SELECT DISTINCT
        ug.Id                   AS UserId,
        ug.Nom,
        ug.Prenom,
        ug.Mail,
        ug.Service,
        pa.GroupeId             AS GroupeId,
        /* Si table Groupes disponible :
           g.Nom AS GroupeNom,
           sinon : */ 
        CAST(NULL AS NVARCHAR(150)) AS GroupeNom,
        0                       AS FlagDirect,
        1                       AS FlagGroup
    FROM WeekPrs w
    INNER JOIN [PlanifPRS].[dbo].[PrsAffectations] pa ON pa.PrsId = w.Id
    INNER JOIN [PlanifPRS].[dbo].[GroupeUtilisateurs] gu ON gu.GroupeId = pa.GroupeId
    INNER JOIN [PlanifPRS].[dbo].[Utilisateurs] ug ON ug.Id = gu.UtilisateurId
    /* LEFT JOIN [PlanifPRS].[dbo].[Groupes] g ON g.Id = pa.GroupeId AND g.DateDeleted IS NULL */
    WHERE pa.GroupeId IS NOT NULL
      AND pa.TypeAffectation = 'Groupe'
      AND ug.Mail IS NOT NULL
      AND ug.Mail <> ''
      AND ug.DateDeleted IS NULL
),

Unioned AS (
    SELECT * FROM DirectUsers
    UNION ALL
    SELECT * FROM GroupUsers
)

SELECT 
    UserId,
    Nom,
    Prenom,
    Mail,
    Service,
    GroupeId,
    GroupeNom,
    FlagDirect,
    FlagGroup
FROM Unioned
ORDER BY Service, Nom, Prenom, GroupeId;
";

                using var connection = _context.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = sql;

                var pStart = command.CreateParameter();
                pStart.ParameterName = "@weekStart";
                pStart.Value = weekStart;
                command.Parameters.Add(pStart);

                var pEnd = command.CreateParameter();
                pEnd.ParameterName = "@weekEnd";
                pEnd.Value = weekEnd;
                command.Parameters.Add(pEnd);

                var rows = new List<RowAffectation>();

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        rows.Add(new RowAffectation
                        {
                            UserId = Convert.ToInt32(reader["UserId"]),
                            Nom = DecodeHtmlEntities(reader["Nom"]?.ToString()),
                            Prenom = DecodeHtmlEntities(reader["Prenom"]?.ToString()),
                            Email = reader["Mail"]?.ToString(),
                            Service = DecodeHtmlEntities(reader["Service"]?.ToString()) ?? "Non défini",
                            GroupeId = reader["GroupeId"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["GroupeId"]),
                            GroupeNom = reader["GroupeNom"] == DBNull.Value ? null : DecodeHtmlEntities(reader["GroupeNom"]?.ToString()),
                            FlagDirect = Convert.ToInt32(reader["FlagDirect"]) == 1,
                            FlagGroup = Convert.ToInt32(reader["FlagGroup"]) == 1
                        });
                    }
                }

                // Agrégation par utilisateur
                var usersMap = new Dictionary<int, AggregUser2>();

                foreach (var r in rows)
                {
                    if (!usersMap.TryGetValue(r.UserId, out var agg))
                    {
                        agg = new AggregUser2
                        {
                            UserId = r.UserId,
                            Nom = r.Nom,
                            Prenom = r.Prenom,
                            Email = r.Email,
                            Service = r.Service,
                            Groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            Direct = false,
                            ViaGroup = false
                        };
                        usersMap[r.UserId] = agg;
                    }

                    if (r.FlagDirect) agg.Direct = true;
                    if (r.FlagGroup) agg.ViaGroup = true;

                    if (r.GroupeId.HasValue)
                    {
                        string gLabel = r.GroupeNom;
                        if (string.IsNullOrWhiteSpace(gLabel))
                            gLabel = $"Groupe #{r.GroupeId}";
                        agg.Groups.Add(gLabel);
                    }
                }

                var finalUsers = usersMap.Values
                    .Select(u => new
                    {
                        Id = u.UserId,
                        u.Nom,
                        u.Prenom,
                        Email = u.Email,
                        u.Service,
                        Groups = u.Groups.OrderBy(g => g).ToList(),
                        Source = (u.Direct, u.ViaGroup) switch
                        {
                            (true, true) => "Direct+Groupe",
                            (true, false) => "Direct",
                            (false, true) => "Groupe",
                            _ => "Inconnu"
                        }
                    })
                    .OrderBy(u => u.Service)
                    .ThenBy(u => u.Nom)
                    .ThenBy(u => u.Prenom)
                    .ToList();

                var usersByService = finalUsers
                    .GroupBy(u => u.Service)
                    .Select(g => new {
                        Service = g.Key,
                        Utilisateurs = g.ToList()
                    })
                    .OrderBy(g => g.Service)
                    .ToList();

                var allEmails = finalUsers
                    .Select(u => u.Email)
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(e => e)
                    .ToList();

                return Ok(new
                {
                    TotalUsers = finalUsers.Count,
                    AllEmails = allEmails,
                    EmailsFormatted = string.Join("; ", finalUsers.Select(u => $"{u.Prenom} {u.Nom} <{u.Email}>")),
                    UsersByService = usersByService,
                    WeekStart = weekStart.ToString("yyyy-MM-dd"),
                    WeekEnd = weekEnd.ToString("yyyy-MM-dd")
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    error = "Erreur lors de la récupération des utilisateurs",
                    details = ex.Message
                });
            }
        }

        private sealed class RowAffectation
        {
            public int UserId { get; set; }
            public string Nom { get; set; }
            public string Prenom { get; set; }
            public string Email { get; set; }
            public string Service { get; set; }
            public int? GroupeId { get; set; }
            public string GroupeNom { get; set; }
            public bool FlagDirect { get; set; }
            public bool FlagGroup { get; set; }
        }

        private sealed class AggregUser2
        {
            public int UserId { get; set; }
            public string Nom { get; set; }
            public string Prenom { get; set; }
            public string Email { get; set; }
            public string Service { get; set; }
            public HashSet<string> Groups { get; set; }
            public bool Direct { get; set; }
            public bool ViaGroup { get; set; }
        }

      

        [HttpGet("secteurs")]
        public async Task<IActionResult> GetSecteurs()
        {
            try
            {
                var secteurs = await _context.Secteurs
                    .Where(s => s.Activation == true && !string.IsNullOrEmpty(s.Nom))
                    .OrderBy(s => s.Nom)
                    .Select(s => new { id = s.Id, nom = DecodeHtmlEntities(s.Nom) }) // DÉCODAGE
                    .ToListAsync();

                return Ok(secteurs);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Erreur lors de la récupération des secteurs", error = ex.Message });
            }
        }


    }
}