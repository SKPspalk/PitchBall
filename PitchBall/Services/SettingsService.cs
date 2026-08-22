using System.IO;
using System.Text.Json;
using PitchBall.Models;

namespace PitchBall.Services;

/// <summary>设置读写,存于 %APPDATA%\PitchBall\settings.json。</summary>
public class SettingsService
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PitchBall");

    private static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");

    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public AppSettings Current { get; private set; } = new();

    public event Action? SettingsChanged;

    public void Load()
    {
        Directory.CreateDirectory(DataDir);
        try
        {
            if (File.Exists(SettingsPath))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, _json));
            SettingsChanged?.Invoke();
        }
        catch
        {
            // 保存失败不致命(例如只读环境)
        }
    }
}
