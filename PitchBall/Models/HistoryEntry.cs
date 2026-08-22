namespace PitchBall.Models;

/// <summary>历史分析记录。</summary>
public class HistoryEntry
{
    public string Id { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public double Duration { get; set; }
    public DateTime AnalyzedAt { get; set; }
    /// <summary>分析结果缓存文件名(位于 analyses 目录)。</summary>
    public string CacheFile { get; set; } = "";
}

/// <summary>离线分析结果(JSON 缓存)。</summary>
public class AnalysisResult
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public double Duration { get; set; }
    /// <summary>波形峰值(RMS),每秒 50 个点。</summary>
    public float[] WavePeaks { get; set; } = [];
    /// <summary>音高序列(Hz,0 表示无声),频率为 PitchRate。</summary>
    public double[] PitchFreqs { get; set; } = [];
    /// <summary>音高检测帧率(帧/秒)。</summary>
    public double PitchRate { get; set; } = 21.5;
    /// <summary>所用算法标识(YIN / pYIN),旧缓存为空。</summary>
    public string Algorithm { get; set; } = "";
    /// <summary>每个 MIDI 音高的命中次数。</summary>
    public Dictionary<int, int> NoteCounts { get; set; } = new();
    public int MinMidi { get; set; } = int.MaxValue;
    public int MaxMidi { get; set; } = int.MinValue;
    /// <summary>有声帧占比(0-1)。</summary>
    public double VoicedRatio { get; set; }
}
