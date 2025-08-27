using System.Net.Mail;
using System.Net;
using Microsoft.Extensions.Configuration;
using PlanifPRS.Models;
using Microsoft.EntityFrameworkCore;
using System.Text;
using PlanifPRS.Data;
using System.Text.RegularExpressions;
using System.Net.Mime; // pour MediaTypeNames

namespace PlanifPRS.Services
{
    public class NotificationService
    {
        private readonly PlanifPrsDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<NotificationService> _logger;

        // Toggle principal
        private bool EmailEnabled => _configuration.GetValue<bool?>("Email:Enable") ?? false;
        private bool TestMode => _configuration.GetValue<bool?>("Email:TestMode") ?? false;
        private string ForceRecipient => _configuration.GetValue<string>("Email:ForceRecipient") ?? string.Empty;
        private bool SimpleMode => string.Equals(_configuration.GetValue<string>("Email:Mode"), "Simple", StringComparison.OrdinalIgnoreCase);

        public NotificationService(PlanifPrsDbContext context, IConfiguration configuration, ILogger<NotificationService> logger)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Envoi des notifications PRS.
        /// action : "create" | "clone" | "edit"
        /// anciennesAffectations : liste d’Id utilisateurs affectés AVANT modification (uniquement pour edit)
        /// actorLogin : (non utilisé pour le filtrage d'envoi désormais) login Windows de l'utilisateur déclencheur
        /// </summary>
        public async Task EnvoyerNotificationsPRS(int prsId, string action, List<int>? anciennesAffectations = null, string? actorLogin = null)
        {
            if (!EmailEnabled)
            {
                _logger.LogInformation("[NotificationService] Envoi désactivé (Email:Enable = false). PRS {PrsId}, Action {Action}", prsId, action);
                return;
            }

            try
            {
                var prs = await _context.Prs
                    .Include(p => p.Ligne)
                    .Include(p => p.Affectations).ThenInclude(a => a.Utilisateur)
                    .Include(p => p.Affectations).ThenInclude(a => a.Groupe).ThenInclude(g => g.Membres).ThenInclude(m => m.Utilisateur)
                    .Include(p => p.Checklist).ThenInclude(c => c.Affectations).ThenInclude(ca => ca.Utilisateur)
                    .FirstOrDefaultAsync(p => p.Id == prsId);

                if (prs == null)
                {
                    _logger.LogWarning("[NotificationService] PRS {PrsId} introuvable", prsId);
                    return;
                }

                // Récupérer l'email et le nom de l'organisateur (créateur de la PRS)
                var (organizerUser, organizerEmail, organizerDisplayName) = await GetOrganizerInfos(prs);

                // Filtrage basé UNIQUEMENT sur les droits du créateur (organizerUser)
                if (organizerUser == null)
                {
                    _logger.LogInformation("[NotificationService] Pas de créateur identifié pour PRS {PrsId} → aucun envoi.", prs.Id);
                    return;
                }
                if (!await IsAdminOrValidateur(organizerUser.LoginWindows))
                {
                    _logger.LogInformation("[NotificationService] Créateur '{Creator}' sans droits admin/validateur → aucun envoi (PRS {PrsId}, Action {Action})",
                        organizerUser.LoginWindows, prsId, action);
                    return;
                }

                var utilisateursAffectesPRS = await GetUtilisateursAffectesPRS(prsId);
                var utilisateursChecklistOnly = await GetUtilisateursChecklistOnly(prsId);

                // Toujours envoyer une invitation au créateur (organisateur) à la création/clonage
                // même s'il n'est pas affecté pour qu'il voie la PRS dans son calendrier.
                if (action != "edit" && organizerUser != null && !string.IsNullOrWhiteSpace(organizerEmail))
                {
                    bool dejaDansListe = utilisateursAffectesPRS.Any(u => u.Id == organizerUser.Id);
                    if (!dejaDansListe)
                    {
                        string motifInit = action == "clone" ? "Clonage PRS" : "Création PRS";
                        await EnvoyerInvitationOutlook(prs, organizerUser, motifInit, organizerEmail, organizerDisplayName);
                    }
                }

                if (action == "edit")
                {
                    var nouveauxUtilisateurs = new List<Utilisateur>();
                    if (anciennesAffectations != null)
                    {
                        var setAnciens = anciennesAffectations.ToHashSet();
                        nouveauxUtilisateurs = utilisateursAffectesPRS
                            .Where(u => !setAnciens.Contains(u.Id))
                            .ToList();
                    }

                    var datesModifiees = prs.AncienneDateDebut != prs.DateDebut || prs.AncienneDateFin != prs.DateFin;

                    // Nouveaux utilisateurs affectés
                    if (nouveauxUtilisateurs.Any())
                    {
                        foreach (var utilisateur in nouveauxUtilisateurs)
                            await EnvoyerInvitationOutlook(prs, utilisateur, motif: "Nouvelle affectation", organizerEmail, organizerDisplayName);
                    }

                    // Mises à jour de dates → renvoyer à tous les affectés + checklist-only + créateur si pas affecté
                    if (datesModifiees)
                    {
                        foreach (var utilisateur in utilisateursAffectesPRS)
                            await EnvoyerInvitationOutlook(prs, utilisateur, motif: "Dates modifiées", organizerEmail, organizerDisplayName);

                        foreach (var utilisateur in utilisateursChecklistOnly)
                            await EnvoyerEmailChecklist(prs, utilisateur, motif: "Dates modifiées");

                        // Créateur non affecté : recevoir aussi la MAJ de dates
                        if (organizerUser != null &&
                            !string.IsNullOrWhiteSpace(organizerEmail) &&
                            !utilisateursAffectesPRS.Any(u => u.Id == organizerUser.Id))
                        {
                            await EnvoyerInvitationOutlook(prs, organizerUser, motif: "Dates modifiées", organizerEmail, organizerDisplayName);
                        }
                    }

                    if (!nouveauxUtilisateurs.Any() && !datesModifiees)
                        _logger.LogInformation("[NotificationService] Edit PRS {PrsId} sans nouveaux utilisateurs ni changement de dates → aucun envoi.", prsId);
                }
                else // create ou clone
                {
                    string motif = action == "clone" ? "Clonage PRS" : "Création PRS";

                    // Invitations pour tous les affectés (le créateur a déjà pu être invité juste avant si non affecté)
                    foreach (var utilisateur in utilisateursAffectesPRS)
                        await EnvoyerInvitationOutlook(prs, utilisateur, motif, organizerEmail, organizerDisplayName);

                    // Checklist-only
                    foreach (var utilisateur in utilisateursChecklistOnly)
                        await EnvoyerEmailChecklist(prs, utilisateur, motif: "Checklist (non affecté à la PRS)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur globale NotificationService PRS {PrsId}", prsId);
            }
        }

        // Vérifie les droits admin/validateur
        private async Task<bool> IsAdminOrValidateur(string login)
        {
            try
            {
                var user = await _context.Utilisateurs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.LoginWindows == login && !u.DateDeleted.HasValue);

                if (user == null) return false;
                var droit = (user.Droits ?? "").Trim().ToLowerInvariant();
                return droit == "admin" || droit == "validateur";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NotificationService] Erreur vérification droits pour {Login}", login);
                return false;
            }
        }

        private async Task<List<Utilisateur>> GetUtilisateursAffectesPRS(int prsId)
        {
            var utilisateurs = new List<Utilisateur>();

            var affectationsDirectes = await _context.PrsAffectations
                .Include(a => a.Utilisateur)
                .Where(a => a.PrsId == prsId && a.TypeAffectation == "Utilisateur" && a.UtilisateurId.HasValue)
                .ToListAsync();
            utilisateurs.AddRange(affectationsDirectes.Select(a => a.Utilisateur!).Where(u => u != null));

            var affectationsGroupes = await _context.PrsAffectations
                .Include(a => a.Groupe).ThenInclude(g => g.Membres).ThenInclude(m => m.Utilisateur)
                .Where(a => a.PrsId == prsId && a.TypeAffectation == "Groupe" && a.GroupeId.HasValue)
                .ToListAsync();
            foreach (var aff in affectationsGroupes)
                if (aff.Groupe?.Membres != null)
                    utilisateurs.AddRange(aff.Groupe.Membres.Select(m => m.Utilisateur!).Where(u => u != null));

            return utilisateurs
                .Where(u => u != null && !string.IsNullOrWhiteSpace(u.Mail))
                .GroupBy(u => u.Id)
                .Select(g => g.First())
                .ToList();
        }

        private async Task<List<Utilisateur>> GetUtilisateursChecklistOnly(int prsId)
        {
            var idsAffectesPRS = (await GetUtilisateursAffectesPRS(prsId)).Select(u => u.Id).ToHashSet();
            var utilisateursChecklist = new List<Utilisateur>();

            var affectationsChecklistDirectes = await _context.ChecklistAffectations
                .Include(ca => ca.Utilisateur)
                .Include(ca => ca.Checklist)
                .Where(ca => ca.Checklist.PRSId == prsId && ca.TypeAffectation == "Utilisateur" && ca.UtilisateurId.HasValue)
                .ToListAsync();
            utilisateursChecklist.AddRange(affectationsChecklistDirectes.Select(ca => ca.Utilisateur!).Where(u => u != null));

            var affectationsChecklistGroupes = await _context.ChecklistAffectations
                .Include(ca => ca.Groupe).ThenInclude(g => g.Membres).ThenInclude(m => m.Utilisateur)
                .Include(ca => ca.Checklist)
                .Where(ca => ca.Checklist.PRSId == prsId && ca.TypeAffectation == "Groupe" && ca.GroupeId.HasValue)
                .ToListAsync();
            foreach (var aff in affectationsChecklistGroupes)
                if (aff.Groupe?.Membres != null)
                    utilisateursChecklist.AddRange(aff.Groupe.Membres.Select(m => m.Utilisateur!).Where(u => u != null));

            return utilisateursChecklist
                .Where(u => u != null && !idsAffectesPRS.Contains(u.Id) && !string.IsNullOrWhiteSpace(u.Mail))
                .GroupBy(u => u.Id)
                .Select(g => g.First())
                .ToList();
        }

        private async Task EnvoyerInvitationOutlook(Prs prs, Utilisateur utilisateur, string motif, string organizerEmail, string organizerDisplayName)
        {
            if (string.IsNullOrEmpty(utilisateur.Mail)) return;

            if (SimpleMode)
            {
                var checklistInfoHtml = await GetChecklistInfoPourUtilisateur(prs.Id, utilisateur.Id);
                var body = BuildSimpleBody(prs, checklistInfoHtml, motif, isChecklist: false);
                await SendSimple(utilisateur.Mail, $"[PRS] {prs.Titre} ({motif})", body);
                return;
            }

            var checklistInfoHtmlFull = await GetChecklistInfoPourUtilisateur(prs.Id, utilisateur.Id);

            var mail = new MailMessage
            {
                From = new MailAddress(GetFromAddress(), GetFromDisplayName()),
                Subject = $"[PRS] {prs.Titre} ({motif})",
                IsBodyHtml = true,
                Body = $@"
<html>
<body style='font-family:Segoe UI,Arial,sans-serif;font-size:13px'>
  <h3 style='margin:0 0 10px'>PRS : {EscapeHtml(prs.Titre)}</h3>
  <p><strong>Équipement :</strong> {EscapeHtml(prs.Equipement)}</p>
  <p><strong>Période :</strong> {prs.DateDebut:dd/MM/yyyy HH:mm} → {prs.DateFin:dd/MM/yyyy HH:mm}</p>
  <p><strong>Ligne :</strong> {EscapeHtml(prs.Ligne?.Nom)}</p>
  <hr />
  {checklistInfoHtmlFull}
  <hr />
  <p><strong>Informations diverses :</strong><br/>{(string.IsNullOrWhiteSpace(prs.InfoDiverses) ? "<em>Aucune</em>" : EscapeHtml(prs.InfoDiverses))}</p>
  <p style='color:#888'>Mail automatique - Ne pas répondre.</p>
</body>
</html>"
            };

            // Ajoute Reply-To vers le créateur si différent de l'adresse générique
            if (!string.IsNullOrWhiteSpace(organizerEmail) && !string.Equals(organizerEmail, GetFromAddress(), StringComparison.OrdinalIgnoreCase))
            {
                mail.ReplyToList.Clear();
                mail.ReplyToList.Add(new MailAddress(organizerEmail, organizerDisplayName));
                mail.Headers.Add("X-Organizer-Login", prs.CreatedByLogin ?? "");
            }

            // En-têtes utiles Outlook
            mail.Headers.Add("Content-class", "urn:content-classes:calendarmessage");

            AjouterDestinataire(mail, utilisateur.Mail);

            // Convertir le Body HTML en AlternateViews
            ConvertBodyToAlternateViews(mail);

            // ICS comme AlternateView
            var icsContent = CreerFichierICS(prs, utilisateur, checklistInfoHtmlFull, organizerEmail, organizerDisplayName, motif);
            var calendarView = AlternateView.CreateAlternateViewFromString(icsContent, Encoding.UTF8, "text/calendar");
            calendarView.ContentType.Parameters.Add("method", "REQUEST");
            calendarView.ContentType.Parameters.Add("name", "invitation.ics");
            mail.AlternateViews.Add(calendarView);

            // (Optionnel) en pièce jointe aussi
            var attachment = Attachment.CreateAttachmentFromString(icsContent, "invitation.ics", Encoding.UTF8, "text/calendar");
            attachment.ContentDisposition.Inline = true;
            mail.Attachments.Add(attachment);

            await EnvoyerOuSimuler(mail, "INVITATION", utilisateur.Mail);
        }

        private async Task EnvoyerEmailChecklist(Prs prs, Utilisateur utilisateur, string motif)
        {
            if (string.IsNullOrEmpty(utilisateur.Mail)) return;

            if (SimpleMode)
            {
                var checklistDetails = await GetChecklistDetailsPourUtilisateur(prs.Id, utilisateur.Id);
                var body = BuildSimpleBody(prs, checklistDetails, motif, isChecklist: true);
                await SendSimple(utilisateur.Mail, $"[PRS Checklist] {prs.Titre} ({motif})", body);
                return;
            }

            var checklistDetailsFull = await GetChecklistDetailsPourUtilisateur(prs.Id, utilisateur.Id);

            var mail = new MailMessage
            {
                From = new MailAddress(GetFromAddress(), GetFromDisplayName()),
                Subject = $"[PRS Checklist] {prs.Titre} ({motif})",
                IsBodyHtml = true,
                Body = $@"
<html>
<body style='font-family:Segoe UI,Arial,sans-serif;font-size:13px'>
  <h3 style='margin:0 0 10px'>Checklist - PRS : {EscapeHtml(prs.Titre)}</h3>
  <p><strong>Période :</strong> {prs.DateDebut:dd/MM/yyyy HH:mm} → {prs.DateFin:dd/MM/yyyy HH:mm}</p>
  <p><strong>Ligne :</strong> {EscapeHtml(prs.Ligne?.Nom)}</p>
  <hr />
  <h4>Vos éléments :</h4>
  {checklistDetailsFull}
  <hr />
  <p style='color:#888'>Mail automatique - Ne pas répondre.</p>
</body>
</html>"
            };
            AjouterDestinataire(mail, utilisateur.Mail);

            ConvertBodyToAlternateViews(mail);

            await EnvoyerOuSimuler(mail, "CHECKLIST", utilisateur.Mail);
        }

        private void ConvertBodyToAlternateViews(MailMessage mail)
        {
            if (string.IsNullOrWhiteSpace(mail.Body)) return;

            var html = mail.Body;
            var plain = Regex.Replace(html, "<style.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            plain = Regex.Replace(plain, "<script.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            plain = Regex.Replace(plain, "<[^>]+>", " ");
            plain = System.Net.WebUtility.HtmlDecode(plain);
            plain = Regex.Replace(plain, @"\s+", " ").Trim();

            var textView = AlternateView.CreateAlternateViewFromString(plain, Encoding.UTF8, MediaTypeNames.Text.Plain);
            var htmlView = AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html);

            mail.AlternateViews.Add(textView);
            mail.AlternateViews.Add(htmlView);

            mail.Body = string.Empty;
            mail.IsBodyHtml = false;
        }

        private string BuildSimpleBody(Prs prs, string checklistSectionHtml, string motif, bool isChecklist)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"PRS : {prs.Titre} ({motif})");
            sb.AppendLine($"Équipement : {prs.Equipement}");
            sb.AppendLine($"Période : {prs.DateDebut:dd/MM/yyyy HH:mm} -> {prs.DateFin:dd/MM/yyyy HH:mm}");
            sb.AppendLine($"Ligne : {prs.Ligne?.Nom}");
            sb.AppendLine();
            sb.AppendLine(isChecklist ? "Checklist :" : "Checklist assignée :");
            var textChecklist = Regex.Replace(checklistSectionHtml ?? "", "<.*?>", " ");
            textChecklist = Regex.Replace(textChecklist, @"\s+", " ").Trim();
            sb.AppendLine(textChecklist);
            if (!string.IsNullOrWhiteSpace(prs.InfoDiverses))
            {
                sb.AppendLine();
                sb.AppendLine("Informations diverses :");
                sb.AppendLine(prs.InfoDiverses);
            }
            sb.AppendLine();
            sb.AppendLine("(Mail automatique)");
            return sb.ToString();
        }

        private async Task SendSimple(string to, string subject, string body)
        {
            if (TestMode)
            {
                _logger.LogInformation("[NotificationService][TEST MODE][SIMPLE] {Subject} -> {To}", subject, to);
                return;
            }

            try
            {
                var host = _configuration["Email:SmtpHost"] ?? "localhost";
                var portStr = _configuration["Email:SmtpPort"];
                int port = 25;
                int.TryParse(portStr, out port);

                var from = GetFromAddress();
                var finalRecipient = !string.IsNullOrWhiteSpace(ForceRecipient) ? ForceRecipient : to;

                using (var client = new SmtpClient(host, port))
                {
                    client.EnableSsl = _configuration.GetValue<bool?>("Email:EnableSsl") ?? false;

                    var authenticate = _configuration.GetValue<bool?>("Email:Authenticate") ?? false;
                    var user = _configuration["Email:Username"];
                    var pass = _configuration["Email:Password"];

                    if (authenticate && !string.IsNullOrWhiteSpace(user))
                        client.Credentials = new NetworkCredential(user, pass ?? "");
                    else
                        client.UseDefaultCredentials = true;

                    client.Send(from, finalRecipient, subject, body);
                    _logger.LogInformation("[NotificationService][SIMPLE] Mail envoyé à {Dest} (original:{Orig})", finalRecipient, to);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[NotificationService][SIMPLE] Échec envoi mail vers {To}", to);
            }
        }

        private async Task EnvoyerOuSimuler(MailMessage mail, string type, string originalRecipient)
        {
            if (TestMode)
            {
                _logger.LogInformation("[NotificationService][TEST MODE] {Type} vers {Dest} (NON ENVOYÉ)", type, originalRecipient);
                mail.Dispose();
                return;
            }

            try
            {
                using var client = CreerClientSmtp();
                await client.SendMailAsync(mail);
                _logger.LogInformation("[NotificationService] {Type} envoyé à {Dest}", type, originalRecipient);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[NotificationService] Échec envoi {Type} à {Dest}", type, originalRecipient);
            }
            finally
            {
                mail.Dispose();
            }
        }

        private SmtpClient CreerClientSmtp()
        {
            var host = _configuration["Email:SmtpHost"];
            var portStr = _configuration["Email:SmtpPort"];
            if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("Email:SmtpHost non configuré");

            int port = 25;
            int.TryParse(portStr, out port);

            bool enableSsl = _configuration.GetValue<bool?>("Email:EnableSsl") ?? false;
            bool authenticate = _configuration.GetValue<bool?>("Email:Authenticate") ?? true;

            var user = _configuration["Email:Username"];
            var pass = _configuration["Email:Password"];

            var client = new SmtpClient(host, port)
            {
                EnableSsl = enableSsl
            };

            if (authenticate)
            {
                if (!string.IsNullOrWhiteSpace(user))
                {
                    client.Credentials = new NetworkCredential(user, pass ?? "");
                    _logger.LogDebug("[NotificationService] Auth SMTP utilisée (user={User}, ssl={Ssl}, port={Port})", user, enableSsl, port);
                }
                else
                {
                    _logger.LogWarning("[NotificationService] Authenticate=true mais Username vide → fallback anonym.");
                    client.UseDefaultCredentials = true;
                }
            }
            else
            {
                client.UseDefaultCredentials = true;
                _logger.LogDebug("[NotificationService] Envoi sans authentification (anonyme / relay IP). SSL={Ssl}, Port={Port}", enableSsl, port);
            }

            return client;
        }

        private string GetFromAddress()
        {
            var username = _configuration["Email:Username"];
            if (string.IsNullOrWhiteSpace(username))
                return "no-reply@local.test";
            return username;
        }

        private string GetFromDisplayName()
        {
            var display = _configuration["Email:FromDisplayName"];
            return string.IsNullOrWhiteSpace(display) ? "PlanifPRS" : display;
        }

        private void AjouterDestinataire(MailMessage mail, string target)
        {
            if (!string.IsNullOrWhiteSpace(ForceRecipient))
            {
                mail.To.Add(ForceRecipient);
                mail.Subject = "[REDIRIGE] " + mail.Subject;
                mail.Body = $"<p style='color:#d9534f'><strong>ATTENTION :</strong> Mail redirigé au lieu de {target}</p>" + mail.Body;
            }
            else
            {
                mail.To.Add(target);
            }
        }

        private string CreerFichierICS(Prs prs, Utilisateur utilisateur, string checklistInfoHtml, string organizerEmail, string organizerDisplayName, string motif)
        {
            string StripHtml(string html)
            {
                if (string.IsNullOrEmpty(html)) return string.Empty;
                var noTags = Regex.Replace(html, "<.*?>", " ");
                return WebUtility.HtmlDecode(noTags);
            }

            var checklistText = StripHtml(checklistInfoHtml).Trim();
            checklistText = Regex.Replace(checklistText, @"\s+", " ");

            // UID STABLE par PRS
            var uid = $"PRS-{prs.Id}@planifprs";
            var dateCreation = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            var dateDebut = prs.DateDebut.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
            var dateFin = prs.DateFin.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");

            int sequence = motif == "Dates modifiées" ? 1 : 0;

            string Sanitize(string s) =>
                (s ?? "")
                .Replace("\\", "\\\\")
                .Replace("\r\n", "\\n")
                .Replace("\n", "\\n")
                .Replace(",", "\\,")
                .Replace(";", "\\;");

            var description = $"PRS: {prs.Titre}\\nEquipement: {prs.Equipement}\\nChecklist: {checklistText}\\n";
            description = Sanitize(description);

            var location = Sanitize(prs.Ligne?.Nom ?? "Non spécifiée");
            var subject = Sanitize("PRS - " + prs.Titre);

            if (string.IsNullOrWhiteSpace(organizerEmail))
            {
                organizerEmail = GetFromAddress();
                organizerDisplayName = GetFromDisplayName();
            }

            var organizerLine = string.IsNullOrWhiteSpace(organizerDisplayName)
                ? $"ORGANIZER:MAILTO:{organizerEmail}"
                : $"ORGANIZER;CN={Sanitize(organizerDisplayName)}:MAILTO:{organizerEmail}";

            var attendeeCn = Sanitize($"{utilisateur.Prenom} {utilisateur.Nom}".Trim());
            var attendeeLine = string.IsNullOrWhiteSpace(attendeeCn)
                ? $"ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:MAILTO:{utilisateur.Mail}"
                : $"ATTENDEE;CN={attendeeCn};ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:MAILTO:{utilisateur.Mail}";

            var sb = new StringBuilder();
            sb.AppendLine("BEGIN:VCALENDAR");
            sb.AppendLine("PRODID:-//PlanifPRS//FR");
            sb.AppendLine("VERSION:2.0");
            sb.AppendLine("METHOD:REQUEST");
            sb.AppendLine("BEGIN:VEVENT");
            sb.AppendLine($"UID:{uid}");
            sb.AppendLine($"DTSTAMP:{dateCreation}");
            sb.AppendLine($"DTSTART:{dateDebut}");
            sb.AppendLine($"DTEND:{dateFin}");
            sb.AppendLine($"SEQUENCE:{sequence}");
            sb.AppendLine($"SUMMARY:{subject}");
            sb.AppendLine($"DESCRIPTION:{description}");
            sb.AppendLine($"LOCATION:{location}");
            sb.AppendLine(organizerLine);
            sb.AppendLine(attendeeLine);
            if (!string.IsNullOrWhiteSpace(organizerEmail) &&
                !string.Equals(organizerEmail, utilisateur.Mail, StringComparison.OrdinalIgnoreCase))
            {
                var orgAttendee = string.IsNullOrWhiteSpace(organizerDisplayName)
                    ? $"ATTENDEE;ROLE=CHAIR:MAILTO:{organizerEmail}"
                    : $"ATTENDEE;CN={Sanitize(organizerDisplayName)};ROLE=CHAIR:MAILTO:{organizerEmail}";
                sb.AppendLine(orgAttendee);
            }
            sb.AppendLine("CLASS:PUBLIC");
            sb.AppendLine("STATUS:CONFIRMED");
            sb.AppendLine("TRANSP:OPAQUE");
            sb.AppendLine("PRIORITY:5");
            sb.AppendLine("BEGIN:VALARM");
            sb.AppendLine("TRIGGER:-PT15M");
            sb.AppendLine("ACTION:DISPLAY");
            sb.AppendLine($"DESCRIPTION:Rappel PRS - {subject}");
            sb.AppendLine("END:VALARM");
            sb.AppendLine("END:VEVENT");
            sb.AppendLine("END:VCALENDAR");

            return sb.ToString();
        }

        private async Task<(Utilisateur? user, string email, string displayName)> GetOrganizerInfos(Prs prs)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(prs.CreatedByLogin))
                {
                    var user = await _context.Utilisateurs
                        .AsNoTracking()
                        .FirstOrDefaultAsync(u => u.LoginWindows == prs.CreatedByLogin && !u.DateDeleted.HasValue);

                    if (user != null && !string.IsNullOrWhiteSpace(user.Mail))
                    {
                        var dn = $"{user.Prenom} {user.Nom}".Trim();
                        return (user, user.Mail.Trim(), string.IsNullOrWhiteSpace(dn) ? user.Mail.Trim() : dn);
                    }
                    return (user, "", "");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NotificationService] Impossible de récupérer l'organisateur, fallback.");
            }

            return (null, GetFromAddress(), GetFromDisplayName());
        }

        private async Task<string> GetChecklistInfoPourUtilisateur(int prsId, int utilisateurId)
        {
            var elements = await _context.PrsChecklists
                .Where(c => c.PRSId == prsId)
                .Where(c => c.Affectations.Any(a =>
                    (a.TypeAffectation == "Utilisateur" && a.UtilisateurId == utilisateurId) ||
                    (a.TypeAffectation == "Groupe" && a.Groupe.Membres.Any(m => m.UtilisateurId == utilisateurId))
                ))
                .OrderBy(c => c.DateEcheance)
                .ToListAsync();

            if (!elements.Any()) return "<p><em>Aucun élément de checklist assigné.</em></p>";

            var sb = new StringBuilder();
            sb.Append("<h4>Checklist assignée :</h4><ul style='padding-left:16px'>");
            foreach (var e in elements)
            {
                var echeance = e.DateEcheance?.ToString("dd/MM") ?? "N/A";
                sb.Append($"<li>{EscapeHtml(e.Libelle)} (échéance: {echeance})</li>");
            }
            sb.Append("</ul>");
            return sb.ToString();
        }

        private async Task<string> GetChecklistDetailsPourUtilisateur(int prsId, int utilisateurId)
        {
            var elements = await _context.PrsChecklists
                .Where(c => c.PRSId == prsId)
                .Where(c => c.Affectations.Any(a =>
                    (a.TypeAffectation == "Utilisateur" && a.UtilisateurId == utilisateurId) ||
                    (a.TypeAffectation == "Groupe" && a.Groupe.Membres.Any(m => m.UtilisateurId == utilisateurId))
                ))
                .OrderBy(c => c.DateEcheance)
                .ToListAsync();

            if (!elements.Any()) return "<p><em>Aucun élément de checklist assigné.</em></p>";

            var sb = new StringBuilder();
            sb.Append("<table style='border-collapse:collapse;width:100%;font-size:12px' border='1'>");
            sb.Append("<tr style='background:#f2f2f2'><th align='left'>Tâche</th><th>Prio</th><th>Échéance</th><th>Obl.</th></tr>");
            foreach (var e in elements)
            {
                var echeance = e.DateEcheance?.ToString("dd/MM/yyyy HH:mm") ?? "-";
                sb.Append("<tr>");
                sb.Append($"<td>{EscapeHtml(e.Libelle)}</td>");
                sb.Append($"<td style='text-align:center'>{e.Priorite}</td>");
                sb.Append($"<td style='white-space:nowrap'>{echeance}</td>");
                sb.Append($"<td style='text-align:center'>{(e.Obligatoire ? "Oui" : "Non")}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            return sb.ToString();
        }

        private static string EscapeHtml(string? input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return System.Net.WebUtility.HtmlEncode(input);
        }
    }
}