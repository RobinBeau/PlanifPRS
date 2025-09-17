using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;

namespace PlanifPRS.Infrastructure.Absences;

public class SkipUsersStore
{
    private readonly string _file;
    private readonly Dictionary<string, SkipUserInfo> _dict = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public SkipUsersStore(string file)
    {
        _file = file;
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private void Load()
    {
        if (_loaded) return;
        if (File.Exists(_file))
        {
            try
            {
                var arr = JsonSerializer.Deserialize<List<SkipUserInfo>>(File.ReadAllText(_file));
                if (arr != null)
                    foreach (var u in arr) _dict[u.Email] = u;
            }
            catch { }
        }
        _loaded = true;
    }

    public bool IsPermanentlySkipped(string email)
    {
        Load();
        return _dict.TryGetValue(email, out var info) && info.Skip;
    }

    public void RegisterRunResult(string email, bool useful, int threshold)
    {
        Load();
        if (!_dict.TryGetValue(email, out var info))
        {
            info = new SkipUserInfo { Email = email };
            _dict[email] = info;
        }

        if (useful)
        {
            info.EmptyRuns = 0;
            info.Skip = false;
        }
        else
        {
            info.EmptyRuns++;
            if (info.EmptyRuns >= threshold)
                info.Skip = true;
        }
    }

    public void Save()
    {
        Load();
        var list = new List<SkipUserInfo>(_dict.Values);
        File.WriteAllText(_file, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public class SkipUserInfo
{
    public string Email { get; set; } = "";
    public int EmptyRuns { get; set; }
    public bool Skip { get; set; }
}