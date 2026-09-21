namespace PitchBall.Audio;

/// <summary>
/// 流式分数重采样(设备采样率 → 目标采样率),用于实时路径。
///
/// 为什么不用 NAudio 的 WdlResamplingSampleProvider:它是"拉"式流处理器,配
/// BufferedWaveProvider 在 push 语义下有 ReadFully 补零与内部延迟的坑,实测把
/// 音频喂进去后模型置信度崩到 0.002(见 --rmvpert 诊断)。这里改成自写、状态明确、
/// 可单元验证的实现:每个输出样本对邻域 16 个输入样本做 Hann 窗 sinc 加权,
/// sinc 截止取**输出**奈奎斯特(即 0.5·target/device),因此自带抗混叠。
/// </summary>
public sealed class SincResampler
{
    private const int Taps = 16;

    private readonly double _step;      // 每个输出样本前进的输入样本数
    private readonly double _cutoff;    // 归一化截止(输入采样率单位)
    private readonly float[] _buf;      // 输入环形缓冲
    private int _count;                 // 已缓冲样本数(≤ 容量)
    private int _write;                 // 环形写位置
    private double _phase;              // 下一个输出样本的输入时间位置(相对缓冲起点)

    public SincResampler(int deviceRate, int targetRate, double bufferSeconds = 3.0)
    {
        _step = (double)deviceRate / targetRate;
        _cutoff = 0.5 * Math.Min(1.0, (double)targetRate / deviceRate);  // 抗混叠
        _buf = new float[(int)(deviceRate * bufferSeconds) + 1];
    }

    /// <summary>写入设备采样率的样本。</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (float s in samples)
        {
            _buf[_write] = s;
            _write = (_write + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
        }
    }

    /// <summary>按目标采样率取出当前可用的输出样本(0 = 输入不足,先继续 Push)。</summary>
    public int Read(float[] dest, int offset, int count)
    {
        int produced = 0;
        while (produced < count)
        {
            int need = (int)Math.Ceiling(_phase) + Taps / 2;
            if (_phase < 0 || need >= _count) break;    // 输入还没到/不够(等下一批)
            dest[offset + produced++] = (float)SampleAt(_phase);
            _phase += _step;
        }
        // 消费已用完的输入:整体前移,保持 _phase 相对新起点
        int consume = (int)Math.Floor(_phase) - Taps / 2;
        if (consume > 0)
        {
            int keep = _count - consume;
            if (keep < 0) keep = 0;
            for (int i = 0; i < keep; i++) _buf[i] = _buf[(consume + i) % _buf.Length];
            _count = keep;
            _write = keep % _buf.Length;
            _phase -= consume;
        }
        return produced;
    }

    /// <summary>当前缓冲里还有多少输入样本(未消费)。</summary>
    public int BufferedInput => _count;

    private double SampleAt(double pos)
    {
        int center = (int)Math.Floor(pos);
        double sum = 0, wsum = 0;
        for (int k = -Taps / 2 + 1; k <= Taps / 2; k++)
        {
            int idx = center + k;
            if (idx < 0 || idx >= _count) continue;
            double x = pos - idx;                         // 归一化到"输入样本"单位
            double w = Window(x);
            sum += w * _buf[idx];
            wsum += w;
        }
        return wsum > 1e-9 ? sum / wsum : 0;
    }

    /// <summary>Hann 窗 sinc(截止由 _cutoff 控制,自带抗混叠)。</summary>
    private double Window(double x)
    {
        double t = x * 2 * _cutoff;                       // 按截止频率缩放
        if (Math.Abs(x) >= Taps / 2.0) return 0;
        double sinc = Math.Abs(t) < 1e-9 ? 1.0 : Math.Sin(Math.PI * t) / (Math.PI * t);
        double win = 0.5 + 0.5 * Math.Cos(Math.PI * x / (Taps / 2.0));   // Hann
        return sinc * win;
    }
}
