using System.Text.Json;

namespace PlanifPRS.Infrastructure.Absences;

public class FileAbsenceSyncStateStore : IAbsenceSyncStateStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileAbsenceSyncStateStore(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "Data", "Absences");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "sync-state.json");
    }

    public async Task<AbsenceSyncState> GetAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath))
                return new AbsenceSyncState();
            var json = await File.ReadAllTextAsync(_filePath, ct);
            return JsonSerializer.Deserialize<AbsenceSyncState>(json, JsonOpts) ?? new AbsenceSyncState();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateAsync(AbsenceSyncState state, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOpts);
            await File.WriteAllTextAsync(_filePath, json, ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}