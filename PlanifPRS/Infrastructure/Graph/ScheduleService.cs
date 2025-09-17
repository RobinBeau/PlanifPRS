using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.Calendar.GetSchedule;

namespace PlanifPRS.Infrastructure.Graph;

/// <summary>
/// Service batch pour récupérer les créneaux OOF via /users/{anchor}/calendar/getSchedule (SDK Microsoft.Graph v5.x).
/// </summary>
public class ScheduleService
{
    private readonly GraphServiceClient _client;
    private readonly ILogger<ScheduleService> _logger;

    public ScheduleService(IGraphClientProvider provider, ILogger<ScheduleService> logger)
    {
        _client = provider.GetClient();
        _logger = logger;
    }

    public async Task<Dictionary<string, List<(DateTimeOffset Start, DateTimeOffset End)>>> GetOutOfOfficeAsync(
        string anchorUser,
        IEnumerable<string> emails,
        DateTimeOffset from,
        DateTimeOffset to,
        int chunkSize,
        CancellationToken ct)
    {
        var dict = new Dictionary<string, List<(DateTimeOffset, DateTimeOffset)>>(StringComparer.OrdinalIgnoreCase);
        var unique = emails.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        string startIso = from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss");
        string endIso = to.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss");

        for (int i = 0; i < unique.Count; i += chunkSize)
        {
            var slice = unique.Skip(i).Take(chunkSize).ToList();

            var body = new GetSchedulePostRequestBody
            {
                Schedules = slice,
                StartTime = new DateTimeTimeZone
                {
                    DateTime = startIso,
                    TimeZone = "UTC"
                },
                EndTime = new DateTimeTimeZone
                {
                    DateTime = endIso,
                    TimeZone = "UTC"
                },
                AvailabilityViewInterval = 60
            };

            try
            {
                var response = await _client
                    .Users[anchorUser]
                    .Calendar
                    .GetSchedule
                    .PostAsync(body, cancellationToken: ct);

                if (response?.Value != null)
                {
                    foreach (var schedule in response.Value)
                    {
                        if (schedule?.ScheduleId == null)
                            continue;

                        var oofList = new List<(DateTimeOffset, DateTimeOffset)>();

                        if (schedule.ScheduleItems != null)
                        {
                            foreach (var item in schedule.ScheduleItems)
                            {
                                if (item?.Status != FreeBusyStatus.Oof)
                                    continue;
                                if (item.Start?.DateTime == null || item.End?.DateTime == null)
                                    continue;

                                if (DateTimeOffset.TryParse(item.Start.DateTime, out var s) &&
                                    DateTimeOffset.TryParse(item.End.DateTime, out var e))
                                {
                                    oofList.Add((s, e));
                                }
                            }
                        }

                        dict[schedule.ScheduleId] = oofList;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "getSchedule chunk erreur ({Start}-{End})", i, i + slice.Count - 1);
            }
        }

        foreach (var mail in unique)
            dict.TryAdd(mail, new List<(DateTimeOffset, DateTimeOffset)>());

        return dict;
    }
}