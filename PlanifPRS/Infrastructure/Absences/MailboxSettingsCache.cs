using System;
using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;

namespace PlanifPRS.Infrastructure.Absences;

public class MailboxSettingsCache
{
    private readonly string _dir;
    private readonly int _defaultHours;
    private readonly int _enabledHours;
    private readonly ConcurrentDictionary<string, MailboxCacheEntry> _mem = new(StringComparer.OrdinalIgnoreCase);

    public MailboxSettingsCache(string directory, int defaultHours, int enabledHours)
    {
        _dir = directory;
        _defaultHours = defaultHours;
        _enabledHours = enabledHours;
        Directory.CreateDirectory(_dir);
    }

    public MailboxCacheEntry? TryGet(string email)
    {
        if (_mem.TryGetValue(email, out var e) && e.NextRefreshUtc > DateTimeOffset.UtcNow)
            return e;

        var path = Path.Combine(_dir, Sanitize(email) + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<MailboxCacheEntry>(File.ReadAllText(path));
            if (entry == null) return null;
            _mem[email] = entry;
            return entry.NextRefreshUtc > DateTimeOffset.UtcNow ? entry : null;
        }
        catch { return null; }
    }

    public void Store(string email, bool isOof, DateTimeOffset? scheduledEnd, string? message)
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset next;
        if (isOof)
        {
            if (scheduledEnd.HasValue && scheduledEnd > now)
                next = scheduledEnd.Value.AddMinutes(5);
            else
                next = now.AddHours(_enabledHours);
        }
        else
            next = now.AddHours(_defaultHours);

        var entry = new MailboxCacheEntry
        {
            Email = email,
            IsOutOfOffice = isOof,
            Message = message,
            LastUpdateUtc = now,
            NextRefreshUtc = next
        };
        _mem[email] = entry;

        var path = Path.Combine(_dir, Sanitize(email) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Sanitize(string email) =>
        email.Replace("@", "_at_").Replace("/", "_").Replace("\\", "_").Replace(":", "_");
}

public class MailboxCacheEntry
{
    public string Email { get; set; } = "";
    public bool IsOutOfOffice { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset NextRefreshUtc { get; set; }
    public DateTimeOffset LastUpdateUtc { get; set; }
}