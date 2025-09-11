using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace PlanifPRS.Infrastructure.Graph;

public interface IAbsenceService
{
    Task<UserAbsenceAggregate?> GetUserAbsenceAsync(string email, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

public class AbsenceService : IAbsenceService
{
    private readonly IGraphClientProvider _provider;
    private readonly ILogger<AbsenceService> _logger;

    public AbsenceService(IGraphClientProvider provider, ILogger<AbsenceService> logger)
    {
        _provider = provider;
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

        // Mailbox OOF
        try
        {
            var mailbox = await client.Users[email].MailboxSettings.GetAsync(cancellationToken: ct);
            if (mailbox?.AutomaticRepliesSetting != null)
            {
                aggregate.Presence ??= new PresenceInfo { Email = email };
                var auto = mailbox.AutomaticRepliesSetting;
                bool scheduled = auto.Status == AutomaticRepliesStatus.Scheduled;
                aggregate.Presence.IsOutOfOffice = scheduled;
                aggregate.Presence.OoOMessage = auto.InternalReplyMessage;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MailboxSettings indisponible pour {Email}", email);
        }

        // Événements OOF (calendarView)
        try
        {
            var startUtc = from.ToUniversalTime().ToString("o");
            var endUtc = to.ToUniversalTime().ToString("o");

            var eventsPage = await client.Users[email].CalendarView.GetAsync(config =>
            {
                config.QueryParameters.StartDateTime = startUtc;
                config.QueryParameters.EndDateTime = endUtc;
                config.QueryParameters.Select = new[] { "subject", "start", "end", "showAs" };
                config.QueryParameters.Top = 50;
            }, cancellationToken: ct);

            if (eventsPage?.Value != null)
            {
                foreach (var ev in eventsPage.Value)
                {
                    if (ev.Start?.DateTime == null || ev.End?.DateTime == null) continue;

                    DateTimeOffset start;
                    DateTimeOffset end;
                    try
                    {
                        start = DateTimeOffset.Parse(ev.Start.DateTime);
                        end = DateTimeOffset.Parse(ev.End.DateTime);
                    }
                    catch
                    {
                        continue;
                    }

                    bool isOof = ev.ShowAs == FreeBusyStatus.Oof;

                    if (isOof)
                    {
                        aggregate.Events.Add(new AbsenceEvent
                        {
                            Subject = ev.Subject ?? "(Sans objet)",
                            Start = start,
                            End = end,
                            IsOutOfOffice = true
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erreur événements pour {Email}", email);
        }

        aggregate.GeneratedAtUtc = DateTimeOffset.UtcNow;
        return aggregate;
    }
}