using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PitchBall.Models;

namespace PitchBall.Services;

/// <summary>历史记录与离线分析结果缓存。</summary>
public class HistoryService
{
    private readonly string _historyPath;
    private readonly string _analysesDir;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public List<HistoryEntry> Entries { get; private set; } = [];

    public event Action? HistoryChanged;

    public HistoryService()
    {
        _historyPath = Path.Combine(SettingsService.DataDir, "history.json");
        _analysesDir = Path.Combine(SettingsService.DataDir, "analyses");
    }

    public void Load()
    {
        Directory.CreateDirectory(_analysesDir);
        try
        {
            if (File.Exists(_historyPath))
            {
                Entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_historyPath)) ?? [];
            }
        }
        catch
        {
            Entries = [];
        }
    }

    private void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.DataDir);
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(Entries, _json));
        }
        catch
        {
            // 忽略保存失败
        }
        HistoryChanged?.Invoke();
    }

    public string GetCachePath(string filePath, string algorithm, string profile)
    {
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(filePath + "|" + algorithm + "|" + profile + "|v4")));
        return Path.Combine(_analysesDir, hash + ".json");
    }

    public AnalysisResult? LoadCached(string filePath, string algorithm, string profile)
    {
        string cache = GetCachePath(filePath, algorithm, profile);
        if (!File.Exists(cache)) return null;
        try
        {
            return JsonSerializer.Deserialize<AnalysisResult>(File.ReadAllText(cache));
        }
        catch
        {
            return null;
        }
    }

    public HistoryEntry AddOrUpdate(string filePath, AnalysisResult result, string algorithm, string profile)
    {
        string cache = GetCachePath(filePath, algorithm, profile);
        try
        {
            File.WriteAllText(cache, JsonSerializer.Serialize(result, _json));
        }
        catch
        {
            // 缓存写入失败时仍可加入历史,但再次加载需重算
        }

        var existing = Entries.FirstOrDefault(e =>
            string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.FileName = result.FileName;
            existing.Duration = result.Duration;
            existing.AnalyzedAt = DateTime.Now;
            existing.CacheFile = cache;
        }
        else
        {
            existing = new HistoryEntry
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                FilePath = filePath,
                FileName = result.FileName,
                Duration = result.Duration,
                AnalyzedAt = DateTime.Now,
                CacheFile = cache,
            };
            Entries.Insert(0, existing);
        }
        SaveIndex();
        return existing;
    }

    public void Remove(string id)
    {
        var entry = Entries.FirstOrDefault(e => e.Id == id);
        if (entry == null) return;
        try { if (File.Exists(entry.CacheFile)) File.Delete(entry.CacheFile); } catch { }
        Entries.Remove(entry);
        SaveIndex();
    }

    public void Clear()
    {
        Entries.Clear();
        try
        {
            if (Directory.Exists(_analysesDir))
            {
                foreach (var f in Directory.GetFiles(_analysesDir))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
        SaveIndex();
    }
}
