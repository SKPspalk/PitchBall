using System.Runtime.InteropServices;
using PitchBall.Audio.Interop;

namespace PitchBall.Audio;

/// <summary>
/// 按进程(应用)回环捕捉:Windows 10 2004+ 的 ActivateAudioInterfaceAsync +
/// 虚拟设备 "VAD\Process_Loopback" + AUDIOCLIENT_ACTIVATION_PARAMS。
/// 输出为单声道 float 采样,采样率为设备混音格式采样率。
/// </summary>
public sealed class ProcessLoopbackWaveIn : IDisposable
{
    private const string VirtualDevicePath = "VAD\\Process_Loopback";
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const ushort VtBlob = 65;

    private readonly uint _processId;
    private IAudioClient? _client;
    private IAudioCaptureClient? _captureClient;
    private IntPtr _mixFormatPtr;
    private IntPtr _eventHandle;
    private Thread? _captureThread;
    private volatile bool _running;
    private string? _lastError;

    public event Action<float[], int>? DataAvailable;
    public event Action<string>? CaptureFailed;

    public string? LastError => _lastError;

    public ProcessLoopbackWaveIn(uint processId)
    {
        _processId = processId;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _eventHandle = NativeMethods.CreateEvent(IntPtr.Zero, false, false, null);
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "ProcessLoopback" };
        _captureThread.Start();
    }

    public void Stop()
    {
        _running = false;
        NativeMethods.SetEvent(_eventHandle);
        _captureThread?.Join(1500);
        try { _client?.Stop(); } catch { }
        ReleaseCom();
    }

    private void CaptureLoop()
    {
        try
        {
            if (!ActivateAndInit()) throw new InvalidOperationException(_lastError ?? "激活失败");

            while (_running)
            {
                uint res = NativeMethods.WaitForSingleObject(_eventHandle, 200);
                if (res == WaitFailed)
                {
                    _lastError = "等待音频事件失败";
                    break;
                }
                DrainPackets();
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }

        if (_lastError != null && !_running) return; // 主动停止不算失败
        if (_lastError != null)
        {
            ReleaseCom();
            CaptureFailed?.Invoke(_lastError);
        }
    }

    private bool ActivateAndInit()
    {
        var done = new ManualResetEventSlim(false);
        var handler = new ActivationHandler(done);
        IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        try
        {
            var pars = new AudioClientActivationParams
            {
                ActivationType = CoreAudioConstants.AudioClientActivationTypeProcessLoopback,
                TargetProcessId = _processId,
                ProcessLoopbackMode = CoreAudioConstants.ProcessLoopbackModeIncludeTargetProcessTree,
            };
            Marshal.StructureToPtr(pars, paramsPtr, false);

            var pv = new PropVariant
            {
                vt = VtBlob,
                blob = new PropVariantBlob
                {
                    cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                    pBlobData = paramsPtr,
                },
            };

            var iid = AudioIIDs.IAudioClient;
            int hr = NativeMethods.ActivateAudioInterfaceAsync(
                VirtualDevicePath, ref iid, ref pv, handler, out _);
            if (hr < 0)
            {
                _lastError = $"ActivateAudioInterfaceAsync 失败 (0x{hr:X8})";
                return false;
            }
            if (!done.Wait(TimeSpan.FromSeconds(8)))
            {
                _lastError = "音频激活超时";
                return false;
            }
            if (handler.ActivateHResult < 0 || handler.Client == null)
            {
                _lastError = $"获取 IAudioClient 失败 (0x{handler.ActivateHResult:X8})";
                return false;
            }
            _client = handler.Client;
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }

        // 使用设备混音格式初始化。Process_Loopback 虚拟设备在部分系统上
        // GetMixFormat 返回 E_NOTIMPL,此时回退到标准混音格式。
        int initHr = _client.GetMixFormat(out _mixFormatPtr);
        (int SampleRate, int Channels, int Bits, bool IsFloat) fmt;
        if (initHr < 0 || _mixFormatPtr == IntPtr.Zero)
        {
            if (_mixFormatPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_mixFormatPtr);
                _mixFormatPtr = IntPtr.Zero;
            }
            _mixFormatPtr = WaveFormatExParser.BuildDefaultFloat(48000, 2);
            fmt = (48000, 2, 32, true);
        }
        else
        {
            fmt = WaveFormatExParser.Parse(_mixFormatPtr);
        }

        var sessionGuid = Guid.Empty;
        initHr = _client.Initialize(
            CoreAudioConstants.AudioClntShareModeShared,
            CoreAudioConstants.AudioClntStreamFlagsLoopback |
            CoreAudioConstants.AudioClntStreamFlagsEventCallback |
            CoreAudioConstants.AudioClntStreamFlagsAutoConvertPcm,
            0, 0, _mixFormatPtr, ref sessionGuid);
        if (initHr < 0)
        {
            _lastError = $"IAudioClient.Initialize 失败 (0x{initHr:X8})";
            return false;
        }

        var capIid = AudioIIDs.IAudioCaptureClient;
        initHr = _client.GetService(ref capIid, out IntPtr capPtr);
        if (initHr < 0)
        {
            _lastError = $"GetService(IAudioCaptureClient) 失败 (0x{initHr:X8})";
            return false;
        }
        _captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capPtr);

        initHr = _client.SetEventHandle(_eventHandle);
        if (initHr < 0)
        {
            _lastError = $"SetEventHandle 失败 (0x{initHr:X8})";
            return false;
        }

        initHr = _client.Start();
        if (initHr < 0)
        {
            _lastError = $"IAudioClient.Start 失败 (0x{initHr:X8})";
            return false;
        }

        _sampleRate = fmt.SampleRate;
        _channels = fmt.Channels;
        _bits = fmt.Bits;
        _isFloat = fmt.IsFloat;
        return true;
    }

    private int _sampleRate;
    private int _channels;
    private int _bits;
    private bool _isFloat;

    private void DrainPackets()
    {
        if (_captureClient == null) return;
        while (_running)
        {
            int hr = _captureClient.GetNextPacketSize(out uint frames);
            if (hr < 0 || frames == 0) break;

            hr = _captureClient.GetBuffer(out IntPtr data, out uint framesToRead, out _, out _, out _);
            if (hr < 0) break;

            if (framesToRead > 0)
            {
                var mono = MonoConverter.FromIntPtr(data, framesToRead, _channels, _bits, _isFloat);
                DataAvailable?.Invoke(mono, _sampleRate);
            }
            _captureClient.ReleaseBuffer(framesToRead);
        }
    }

    private void ReleaseCom()
    {
        _captureClient = null;
        _client = null;
        if (_mixFormatPtr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_mixFormatPtr);
            _mixFormatPtr = IntPtr.Zero;
        }
        if (_eventHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_eventHandle);
            _eventHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Stop();
        ReleaseCom();
    }

    /// <summary>激活完成回调,必须在 MTA 线程上创建并实现 IAgileObject。</summary>
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly ManualResetEventSlim _done;

        public ActivationHandler(ManualResetEventSlim done) => _done = done;

        public IAudioClient? Client { get; private set; }
        public int ActivateHResult { get; private set; } = int.MinValue;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                ActivateHResult = activateOperation.GetActivateResult(out int hr, out IntPtr ptr);
                if (hr >= 0 && ActivateHResult >= 0 && ptr != IntPtr.Zero)
                {
                    Client = (IAudioClient)Marshal.GetObjectForIUnknown(ptr);
                }
            }
            catch
            {
                ActivateHResult = -1;
            }
            finally
            {
                _done.Set();
            }
            return 0;
        }
    }
}
