using System.Windows;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace PitchBall.Services;

public enum ThemeMode
{
    Light,
    Dark,
    System,
}

/// <summary>主题管理:浅色/深色/跟随系统,基于资源字典 DynamicResource。</summary>
public class ThemeService
{
    private ThemeMode _mode = ThemeMode.System;

    public ThemeMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            ApplyCurrent();
        }
    }

    /// <summary>当前生效的是否浅色主题。</summary>
    public bool IsLightEffective { get; private set; } = true;

    public event Action? ThemeChanged;

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (int)(key?.GetValue("AppsUseLightTheme") ?? 1) != 0;
        }
        catch
        {
            return true;
        }
    }

    public void ApplyCurrent()
    {
        bool light = _mode switch
        {
            ThemeMode.Light => true,
            ThemeMode.Dark => false,
            _ => SystemUsesLightTheme(),
        };
        IsLightEffective = light;

        var resources = Application.Current.Resources;
        var uri = new Uri(light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        // 替换合并字典中的主题字典(保留顺序)
        var old = resources.MergedDictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString is "Themes/Light.xaml" or "Themes/Dark.xaml");
        if (old != null) resources.MergedDictionaries.Remove(old);
        resources.MergedDictionaries.Insert(0, dict);

        ThemeChanged?.Invoke();
    }

    public void StartSystemWatcher()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_mode == ThemeMode.System && e.Category == UserPreferenceCategory.General)
            {
                ApplyCurrent();
            }
        };
    }
}
