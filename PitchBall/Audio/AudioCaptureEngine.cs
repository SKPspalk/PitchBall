using NAudio.CoreAudioApi;
using NAudio.Wave;
using PitchBall.Models;

namespace PitchBall.Audio;

/// <summary>
/// 统一实时采集引擎。三种音源(麦克风 / 整机回环 / 按应用回环)
/// 均转换为单声道 float 流,以 2048 帧 + 1024 跳采样率做 YIN 检测。
/// </summary>
public sealed class AudioCaptureEngine : IDisposable
{
    public const int FrameSize = 4096;
    public const int HopSize = 2048;

    private readonly PitchDetector _yin = new(FrameSize);
    private readonly PyinRealtimePitch _pyin = new();
    private readonly List<float> _pending = [];
    private readonly float[] _frame = new float[FrameSize];
    private readonly double[] _smoothBuffer = new double[16];
    private readonly double[] _recentScratch = new double[16];
    private int _smoothCount;
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(280); // 短丢帧保持时长
    private double _lastOutFreq;
    private DateTime _lastOutAt = DateTime.MinValue;
    private double _anchorFreq;   // 慢速参考音高(锚点)
    private int _rejectCount;     // 连续异常帧计数

    private IWaveIn? _waveIn;                 // 麦克风 / 整机回环
    private ProcessLoopbackWaveIn? _process;  // 按应用回环
    private WaveFormat? _waveFormat;          // 当前源格式(声道/位深/浮点)
    private double _sampleRate = 44100;

    /// <summary>音高更新(UI 线程)。</summary>
    public event Action<PitchSample>? PitchUpdated;
    /// <summary>状态变化(UI 线程),如当前音源名。</summary>
    public event Action<string>? StatusChanged;
    /// <summary>采集失败(UI 线程)。</summary>
    public event Action<string>? CaptureFailed;

    public AudioSourceSpec? CurrentSource { get; private set; }
    public bool IsCapturing { get; private set; }

    /// <summary>A4 基准频率,来自设置。</summary>
    public double A4Frequency { get; set; } = 440;

    /// <summary>平滑窗口(帧数)。</summary>
    public int Smoothing { get; set; } = 3;

    /// <summary>静音门限(RMS)。</summary>
    public double SilenceThreshold { get; set; } = 0.004;

    /// <summary>音高算法:"Pyin"(pYIN,默认)/ "Yin"(原手写 YIN)/ "Rmvpe"(神经人声模型)。</summary>
    public string Algorithm { get; set; } = "Pyin";

    private RmvpeRealtimePitch? _rmvpe;

    private void PostToUi(Action action) => UiDispatcher.Post(action);

    public void StartSource(AudioSourceSpec spec)
    {
        Stop();

        CurrentSource = spec;
        _pending.Clear();
        _smoothCount = 0;
        _pyin.Reset();

        try
        {
            switch (spec.Kind)
            {
                case SourceKind.Mic:
                    StartMic(spec);
                    break;
                case SourceKind.System:
                    StartSystem(spec);
                    break;
                case SourceKind.App:
                    StartApp(spec);
                    break;
                default:
                    StatusChanged?.Invoke("已停止采集");
                    return;
            }
            IsCapturing = true;
            PostToUi(() => StatusChanged?.Invoke($"正在收听:{spec.DisplayLabel}"));
        }
        catch (Exception ex)
        {
            IsCapturing = false;
            PostToUi(() => CaptureFailed?.Invoke($"无法启动音源「{spec.DisplayLabel}」:{ex.Message}"));
        }
    }

    private void StartMic(AudioSourceSpec spec)
    {
        int deviceNumber = int.TryParse(spec.Id, out int n) ? n : 0;
        var waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(44100, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 4,
        };
        waveIn.DataAvailable += OnByteData;
        waveIn.RecordingStopped += OnRecordingStopped;
        _waveFormat = waveIn.WaveFormat;
        _sampleRate = waveIn.WaveFormat.SampleRate;
        _waveIn = waveIn;
        waveIn.StartRecording();
    }

    private void StartSystem(AudioSourceSpec spec)
    {
        WasapiLoopbackCapture capture;
        if (!string.IsNullOrEmpty(spec.Id))
        {
            using var mm = new MMDeviceEnumerator();
            var device = mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.ID == spec.Id);
            capture = device != null ? new WasapiLoopbackCapture(device) : new WasapiLoopbackCapture();
        }
        else
        {
            capture = new WasapiLoopbackCapture();
        }
        capture.DataAvailable += OnByteData;
        capture.RecordingStopped += OnRecordingStopped;
        _waveFormat = capture.WaveFormat;
        _sampleRate = capture.WaveFormat.SampleRate;
        _waveIn = capture;
        capture.StartRecording();
    }

    private void StartApp(AudioSourceSpec spec)
    {
        if (!uint.TryParse(spec.Id, out uint pid))
        {
            throw new InvalidOperationException("无效的进程 ID");
        }
        try
        {
            using var check = System.Diagnostics.Process.GetProcessById((int)pid);
        }
        catch
        {
            throw new InvalidOperationException("该应用已退出,请重新选择音源");
        }
        var process = new ProcessLoopbackWaveIn(pid);
        process.DataAvailable += (mono, rate) =>
        {
            if (_process != process) return;
            _sampleRate = rate;
            OnMonoData(mono);
        };
        process.CaptureFailed += error => PostToUi(() =>
        {
            if (_process != process) return;
            IsCapturing = false;
            CaptureFailed?.Invoke($"按应用捕捉失败,已无法获取「{spec.DisplayLabel}」的声音:{error}");
        });
        _process = process;
        process.Start();
    }

    private void OnByteData(object? sender, WaveInEventArgs e)
    {
        if (_waveFormat == null) return;
        bool isFloat = _waveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
        var mono = MonoConverter.FromBytes(e.Buffer, _waveFormat.Channels, _waveFormat.BitsPerSample, isFloat);
        OnMonoData(mono);
    }

    private void OnMonoData(float[] mono)
    {
        if (mono.Length == 0) return;
        _pending.AddRange(mono);
        while (_pending.Count >= FrameSize)
        {
            _pending.CopyTo(0, _frame, 0, FrameSize);
            _pending.RemoveRange(0, HopSize);
            ProcessFrame();
        }
    }

    private void ProcessFrame()
    {
        // RMS 响度
        double sum = 0;
        for (int i = 0; i < FrameSize; i++)
        {
            sum += _frame[i] * _frame[i];
        }
        double rms = Math.Sqrt(sum / FrameSize);

        double freq = 0;
        if (Algorithm == "Pyin")
        {
            // pYIN:每 2048 样本跳按 256 样本子帧推进 8 步(≈172 fps,官方 pYIN 帧率,
            // 细时间采样保证音高线连续),静音判定交给 HMM 无声状态;取最后一步(最新)输出
            int subCount = HopSize / PyinRealtimePitch.SubHop;
            for (int k = 0; k < subCount; k++)
            {
                int off = (k + 1) * PyinRealtimePitch.SubHop;
                freq = _pyin.Push(_frame.AsSpan(off, PyinPitchDetector.BlockSize), _sampleRate);
            }
        }
        else if (Algorithm == "Rmvpe")
        {
            // RMVPE:后台线程按 0.25s 间隔对最近 1s 音频推理,这里取最新结果(不阻塞采集)
            _rmvpe ??= new RmvpeRealtimePitch((int)Math.Round(_sampleRate));
            freq = _rmvpe.Push(_frame, _sampleRate);
        }
        else if (rms >= SilenceThreshold)
        {
            freq = _yin.GetPitch(_frame, _sampleRate);
        }

        // 中值平滑:取最近 window 帧的原始检测值(0 = 无音高)的中位数;
        // 若窗口内 G4 以上高音帧占多数,改取高音帧的中位数,短促高音不被低音吞掉
        _smoothBuffer[_smoothCount % _smoothBuffer.Length] = freq;
        _smoothCount++;
        int window = Math.Clamp(Smoothing * 2 + 1, 1, _smoothBuffer.Length);
        int n = Math.Min(window, _smoothCount);
        double highLimit = NoteNames.MidiToFrequency(NoteNames.HighNoteMidi, A4Frequency);
        for (int i = 0; i < n; i++)
        {
            _recentScratch[i] = _smoothBuffer[(_smoothCount - 1 - i) % _smoothBuffer.Length];
        }
        double smoothed = MedianSmooth(_recentScratch, n, highLimit);

        // 连续性增强:①短暂丢帧保持上一音高(避免曲线断断续续);
        // ②锚点式异常抑制:与慢速参考音高偏差超过约 ±7 半音的跳变/滑落
        // 视为噪声保持锚点(连拒约 1.2s 才接受新音高线),抹平直升直降与触底
        var now = DateTime.Now;
        if (smoothed > 0)
        {
            if (_anchorFreq <= 0) _anchorFreq = smoothed;
            double ratio = smoothed / _anchorFreq;
            if (ratio > 1.5 || ratio < 0.667)
            {
                _rejectCount++;
                if (_rejectCount > 26) // ≈1.2s @ 21.5fps
                {
                    _anchorFreq = smoothed;
                    _rejectCount = 0;
                }
                else
                {
                    smoothed = _anchorFreq;
                }
            }
            else
            {
                _anchorFreq += 0.2 * (smoothed - _anchorFreq);
                _rejectCount = 0;
            }
            _lastOutFreq = smoothed;
            _lastOutAt = now;
        }
        else if (_lastOutFreq > 0 && (now - _lastOutAt) <= HoldTime)
        {
            smoothed = _lastOutFreq;
        }
        else
        {
            _lastOutFreq = 0;
        }

        double level = Math.Min(1.0, rms * 8.0);
        var sample = new PitchSample(smoothed, level, now,
            Algorithm == "Pyin" ? _pyin.LastRegister : VocalRegister.Chest);
        // 注:RMVPE 目前只输出音高与置信度,声区沿用真声标签(后续可按频谱量补)
        PostToUi(() => PitchUpdated?.Invoke(sample));
    }

    /// <summary>窗口中值平滑:若高音帧(≥highLimit)占多数,取高音帧的中位数,
    /// 否则取全部值的中位数(0 表示静音)。</summary>
    internal static double MedianSmooth(double[] values, int count, double highLimit)
    {
        int highCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (values[i] >= highLimit) highCount++;
        }
        bool highDominant = highCount * 2 >= count;
        var arr = new double[highDominant ? highCount : count];
        if (highDominant)
        {
            int m = 0;
            for (int i = 0; i < count; i++)
            {
                if (values[i] >= highLimit) arr[m++] = values[i];
            }
        }
        else
        {
            Array.Copy(values, arr, count);
        }
        Array.Sort(arr);
        return arr[arr.Length / 2];
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            PostToUi(() => CaptureFailed?.Invoke($"采集意外停止:{e.Exception.Message}"));
        }
    }

    public void Stop()
    {
        IsCapturing = false;
        try { _rmvpe?.Dispose(); } catch { }
        _rmvpe = null;
        try { _waveIn?.StopRecording(); } catch { }
        try { _waveIn?.Dispose(); } catch { }
        _waveIn = null;
        _waveFormat = null;

        try { _process?.Dispose(); } catch { }
        _process = null;

        CurrentSource = null;
        PostToUi(() => StatusChanged?.Invoke("已停止采集"));
    }

    public void Dispose() => Stop();
}
