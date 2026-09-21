using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PitchBall.Audio;

/// <summary>
/// RMVPE 的实时封装:把采集到的 (设备采样率的) 单声道 float 帧重采样到 16kHz,
/// 累积到环形缓冲;每累计够 <see cref="IntervalSeconds"/> 就用最近
/// <see cref="WindowSeconds"/> 的音频跑一次模型,更新结果。
///
/// 推理在后台线程执行,Push 永不阻塞采集回调(回调里跑 50ms 的推理会导致丢采样)。
/// 模型前端需要至少 32 帧 mel(320ms),窗口取 1s 留足上下文。
/// </summary>
public sealed class RmvpeRealtimePitch : IDisposable
{
    private const int Rate = RmvpePitchEngine.SampleRate;   // 16000
    private const double WindowSeconds = 1.0;               // 每次推理用的窗长
    private const double IntervalSeconds = 0.25;            // 推理间隔
    private const double CalibMargin = 0.03;                // 置信度阈值(与离线一致)

    private readonly RmvpePitchEngine? _engine;
    private SincResampler? _resampler;
    private readonly int _deviceRate;
    private readonly float[]? _ring;                         // 16kHz 环形缓冲
    private readonly float[] _readBuf = new float[16384];
    private readonly object _lock = new();
    private int _ringCount;                                  // 已写入的样本数(≤ 容量)
    private int _ringPos;                                    // 下一个写入位置
    private int _sinceInfer;                                 // 距上次推理新增的样本数
    private volatile float _lastFreq;
    private volatile bool _busy;

    /// <summary>诊断:已完成的推理次数。</summary>
    public int InferCount;
    /// <summary>诊断:最近一次推理的异常信息(为空表示正常)。</summary>
    public string? LastError;
    /// <summary>诊断:窗内最大置信度与对应帧位置(距窗尾的帧数)。</summary>
    public double LastMaxConf;
    public int LastBestIdxFromEnd;
    public double LastBestFreq;
    /// <summary>诊断:环内与窗内样本的最大绝对值(0=空/静音)。</summary>
    public double RingMaxAbs;
    public double WinMaxAbs;
    /// <summary>诊断:当前窗的过零率(次/秒,按 16kHz 计)与样本数。</summary>
    public double WinZcr;
    public int WinLen;
    /// <summary>诊断:环形缓冲当前样本数。</summary>
    public int RingCount { get { lock (_lock) return _ringCount; } }

    public RmvpeRealtimePitch(int deviceRate)
    {
        if (!RmvpePitchEngine.Available) return;
        _engine = new RmvpePitchEngine();
        int ringLen = (int)(Rate * (WindowSeconds + IntervalSeconds + 1.0));
        _ring = new float[ringLen];

        _deviceRate = deviceRate;
        _resampler = new SincResampler(deviceRate, Rate);
    }

    /// <summary>推入一帧设备采样率的单声道样本,返回当前最新的基频(0=未检出)。</summary>
    public double Push(ReadOnlySpan<float> frame, double sampleRate)
    {
        if (_engine == null || _ring == null || _resampler == null) return 0;

        lock (_lock)
        {
            _resampler.Push(frame);
            // 把当前可用的 16kHz 输出全部读进环形缓冲(Read 返回 0 即输入不足,安全)
            int n;
            while ((n = _resampler.Read(_readBuf, 0, _readBuf.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    _ring[_ringPos] = _readBuf[i];
                    _ringPos = (_ringPos + 1) % _ring.Length;
                    if (_ringCount < _ring.Length) _ringCount++;
                }
                _sinceInfer += n;
            }
            double mx = 0;
            for (int i = 0; i < _ringCount; i++) { double a = Math.Abs(_ring[i]); if (a > mx) mx = a; }
            RingMaxAbs = mx;
            bool due = _sinceInfer >= (int)(IntervalSeconds * Rate);
            if (due) _sinceInfer = 0;
            if (!due || _busy || _ringCount < (int)(WindowSeconds * Rate)) return _lastFreq;
            _busy = true;
        }

        // 后台跑模型(拷贝出窗口,避免与写入竞争)
        var win = new float[(int)(WindowSeconds * Rate)];
        lock (_lock)
        {
            for (int i = 0; i < win.Length; i++)
            {
                int idx = (_ringPos - win.Length + i + _ring.Length) % _ring.Length;
                win[i] = _ring[idx];
            }
        }
        double wmx = 0; int zc = 0;
        for (int i = 1; i < win.Length; i++)
        {
            double a = Math.Abs(win[i]); if (a > wmx) wmx = a;
            if ((win[i - 1] < 0) != (win[i] < 0)) zc++;
        }
        WinZcr = zc / (win.Length / (double)Rate);
        WinLen = win.Length;
        WinMaxAbs = wmx;
        Task.Run(() =>
        {
            try
            {
                var (f, c) = _engine.Infer(win);
                InferCount++;
                if (f.Length > 0)
                {
                    int bi = 0;
                    for (int k = 1; k < c.Length; k++) if (c[k] > c[bi]) bi = k;
                    LastMaxConf = c[bi];
                    LastBestIdxFromEnd = f.Length - 1 - bi;
                    LastBestFreq = f[bi];
                }
                if (f.Length >= 8)
                {
                    // 末尾几帧的上下文不完整,取回退 5 帧处的结果
                    int idx = f.Length - 5;
                    double v = c[idx] >= CalibMargin ? f[idx] : 0.0;
                    _lastFreq = (float)v;
                }
                else
                {
                    _lastFreq = 0;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;   // 诊断:不吞异常
            }
            finally
            {
                lock (_lock) { _busy = false; }
            }
        });
        return _lastFreq;
    }

    /// <summary>重置(切换音源或停止采集时调用)。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _ringCount = 0;
            _ringPos = 0;
            _sinceInfer = 0;
            _lastFreq = 0;
            // 重建重采样器 = 清空其内部相位与缓冲
            if (_deviceRate > 0) _resampler = new SincResampler(_deviceRate, Rate);
        }
    }

    public void Dispose() => _engine?.Dispose();
}
