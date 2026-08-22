namespace PitchBall.Models;

/// <summary>声区分类:Chest 真声(谐波丰富)/ Mixed 混声 / Falsetto 假声(近似纯音)。</summary>
public enum VocalRegister { Chest, Mixed, Falsetto }

/// <summary>实时音高采样结果。Frequency = 0 表示静音或未检测到音高。</summary>
public readonly struct PitchSample
{
    public PitchSample(double frequency, double level, DateTime time, VocalRegister register = VocalRegister.Chest)
    {
        Frequency = frequency;
        Level = level;
        Time = time;
        Register = register;
    }

    public double Frequency { get; }
    public double Level { get; }
    public DateTime Time { get; }
    public VocalRegister Register { get; }

    /// <summary>是否有音高(非静音且频率有效)。</summary>
    public bool HasPitch => Frequency > 0;
}
