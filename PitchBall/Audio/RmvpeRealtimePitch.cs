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
    private readonly BufferedWaveProvider? _buffered;
    private readonly WdlResamplingSampleProvider? _resampler;
    private readonly float[]? _ring;                         // 16kHz 环形缓冲
    private readonly float[] _readBuf = new float[16384];
    private readonly object _lock = new();
    private int _ringCount;                                  // 已写入的样本数(≤ 容量)
    private int _ringPos;                                    // 下一个写入位置
    private int _sinceInfer;                                 // 距上次推理新增的样本数
    private volatile float _lastFreq;
    private volatile bool _busy;

    public RmvpeRealtimePitch(int deviceRate)
    {
        if (!RmvpePitchEngine.Available) return;
        _engine = new RmvpePitchEngine();
        int ringLen = (int)(Rate * (WindowSeconds + IntervalSeconds + 1.0));
        _ring = new float[ringLen];

        _buffered = new BufferedWaveProvider(new WaveFormat(deviceRate, 32, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(4),
            DiscardOnBufferOverflow = true,
            ReadFully = true,               // 数据不足时补零,避免 Read 返回 0 阻塞
        };
        _resampler = new WdlResamplingSampleProvider(_buffered.ToSampleProvider(), Rate);
    }

    /// <summary>推入一帧设备采样率的单声道样本,返回当前最新的基频(0=未检出)。</summary>
    public double Push(ReadOnlySpan<float> frame, double sampleRate)
    {
        if (_engine == null || _ring == null || _buffered == null || _resampler == null) return 0;
        if (Math.Abs(_buffered.WaveFormat.SampleRate - sampleRate) > 1) return _lastFreq;

        // float → byte 直塞缓冲(不阻塞)
        var bytes = new byte[frame.Length * 4];
        for (int i = 0; i < frame.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), frame[i]);
        lock (_lock)
        {
            _buffered.AddSamples(bytes, 0, bytes.Length);
            // 把可用的 16kHz 数据全部读进环形缓冲
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
                if (n < _readBuf.Length) break;
            }
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
        Task.Run(() =>
        {
            try
            {
                var (f, c) = _engine.Infer(win);
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
            catch
            {
                // 推理异常不影响采集
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
            try { _buffered?.ClearBuffer(); } catch { }
        }
    }

    public void Dispose() => _engine?.Dispose();
}
