namespace PitchBall.Models;

/// <summary>音名与频率换算工具。MIDI 69 = A4。</summary>
public static class NoteNames
{
    private static readonly string[] Names =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>高音阈值:G4(midi 67,默认 A4=440 时约 392Hz),用于高音强调模式与平滑策略。</summary>
    public const int HighNoteMidi = 67;

    public static string GetNoteName(int midi)
    {
        int m = ((midi % 12) + 12) % 12;
        int octave = midi / 12 - 1;
        return Names[m] + octave;
    }

    /// <summary>音级名(不带八度):C, C#, …, B。</summary>
    public static string GetPitchClassName(int pitchClass)
        => Names[((pitchClass % 12) + 12) % 12];

    /// <summary>频率 → 浮点 MIDI 编号(小数部分为音分)。</summary>
    public static double FrequencyToMidi(double freq, double a4 = 440.0)
        => 69.0 + 12.0 * Math.Log2(freq / a4);

    public static int FrequencyToMidiNote(double freq, double a4 = 440.0)
        => (int)Math.Round(FrequencyToMidi(freq, a4));

    public static double MidiToFrequency(int midi, double a4 = 440.0)
        => a4 * Math.Pow(2.0, (midi - 69) / 12.0);

    /// <summary>音分偏差,-50 ~ +50。</summary>
    public static double CentsOffset(double freq, double a4 = 440.0)
    {
        double m = FrequencyToMidi(freq, a4);
        return (m - Math.Round(m)) * 100.0;
    }

    public static string FormatCents(double cents)
        => (cents >= 0 ? "+" : "") + cents.ToString("F0") + "¢";

    public static string FormatFrequency(double freq)
        => freq < 100 ? freq.ToString("F2") : freq.ToString("F1");
}
