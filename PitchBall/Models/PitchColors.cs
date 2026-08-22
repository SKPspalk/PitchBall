using System.Globalization;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace PitchBall.Models;

/// <summary>小球音高分组颜色工具:12 音级(C=0)默认色环与十六进制转换。</summary>
public static class PitchColors
{
    /// <summary>生成 12 音级的默认颜色(色相每音级 30°)。</summary>
    public static string[] CreateDefaults()
    {
        var list = new string[12];
        for (int pc = 0; pc < 12; pc++)
        {
            list[pc] = ToHex(HsvToRgb(pc * 30.0, 0.65, 1.0));
        }
        return list;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>解析 "#RRGGBB";失败返回 fallback。</summary>
    public static Color FromHex(string hex, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex) || hex.Length < 7) return fallback;
            byte r = byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber);
            byte g = byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber);
            byte b = byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber);
            return Color.FromRgb(r, g, b);
        }
        catch
        {
            return fallback;
        }
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = ((int)(h / 60)) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
