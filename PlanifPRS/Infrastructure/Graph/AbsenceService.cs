using Microsoft.Graph;
using Microsoft.Graph.Models;
using PlanifPRS.Infrastructure.Absences;

namespace PlanifPRS.Infrastructure.Graph;

public interface IAbsenceService
{
    Task<UserAbsenceAggregate?> GetUserAbsenceAsync(string email, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

public class AbsenceService : IAbsenceService
{
    private readonly IGraphClientProvider _provider;
    private readonly MailboxSettingsCache _mailboxCache;
    private readonly ILogger<AbsenceService> _logger;

    public AbsenceService(IGraphClientProvider provider,
                          MailboxSettingsCache mailboxCache,
                          ILogger<AbsenceService> logger)
    {
        _provider = provider;
        _mailboxCache = mailboxCache;
        _logger = logger;
    }

    public async Task<UserAbsenceAggregate?> GetUserAbsenceAsync(string email, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var client = _provider.GetClient();
        var aggregate = new UserAbsenceAggregate { Email = email };

        // Présence
        try
        {
            var presence = await client.Users[email].Presence.GetAsync(cancellationToken: ct);
            if (presence != null)
            {
                aggregate.Presence = new PresenceInfo
                {
                    Email = email,
                    Activity = presence.Activity,
                    Availability = presence.Availability,
                    IsOutOfOffice = string.Equals(presence.Activity, "Away", StringComparison.OrdinalIgnoreCase)
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Présence non récupérée pour {Email}", email);
        }

        // Mailbox OOF avec cache
        var cached = _mailboxCache.TryGet(email);
        if (cached != null)
        {
            aggregate.Presence ??= new PresenceInfo { Email = email };
            aggregate.Presence.IsOutOfOffice = cached.IsOutOfOffice;
            aggregate.Presence.OoOMessage = cached.Message;
        }
        else
        {
            try
            {
                var mailbox = await client.Users[email].MailboxSettings.GetAsync(cancellationToken: ct);
                if (mailbox?.AutomaticRepliesSetting != null)
                {
                    aggregate.Presence ??= new PresenceInfo { Email = email };
                    var auto = mailbox.AutomaticRepliesSetting;
                    bool isEnabled = auto.Status == AutomaticRepliesStatus.Scheduled
                                     || auto.Status == AutomaticRepliesStatus.AlwaysEnabled;

                    aggregate.Presence.IsOutOfOffice = isEnabled;
                    if (!string.IsNullOrEmpty(auto.InternalReplyMessage))
                        aggregate.Presence.OoOMessage = CleanOoO(auto.InternalReplyMessage);

                    DateTimeOffset? scheduledEnd = null;
                    if (auto.Status == AutomaticRepliesStatus.Scheduled &&
                        DateTimeOffset.TryParse(auto.ScheduledEndDateTime?.DateTime, out var end))
                        scheduledEnd = end;

                    _mailboxCache.Store(email, isEnabled, scheduledEnd, aggregate.Presence.OoOMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MailboxSettings indisponible pour {Email}", email);
            }
        }

        // PLUS DE CalendarView ICI (getSchedule s'occupe des events)
        aggregate.GeneratedAtUtc = DateTimeOffset.UtcNow;
        return aggregate;
    }

    private static string? CleanOoO(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        try
        {
            var text = System.Text.RegularExpressions.Regex
                .Replace(html, "<.*?>", " ")
                .Replace("&nbsp;", " ")
                .Replace("&quot;", "\"")
                .Replace("&amp;", "&");
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        }
        catch { return html; }
    }
}