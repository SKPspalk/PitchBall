using System.Runtime.InteropServices;

namespace PitchBall.Audio.Interop;

// ---- 常量 ----
internal static class CoreAudioConstants
{
    public const int ClsCtxAll = 23; // CLSCTX_ALL
    public const int EDataFlowERender = 0;
    public const int ERoleMultimedia = 1;
    public const int DeviceStateActive = 0x1;

    public const int AudioClntShareModeShared = 0;
    public const int AudioClntStreamFlagsLoopback = 0x00020000;
    public const int AudioClntStreamFlagsEventCallback = 0x00040000;
    public const int AudioClntStreamFlagsAutoConvertPcm = unchecked((int)0x80000000);

    public const int AudioClntBufferFlagsDataDiscontinuity = 0x1;
    public const int AudioClntBufferFlagsSilent = 0x2;

    public const int AudioClientActivationTypeProcessLoopback = 1;
    public const int ProcessLoopbackModeIncludeTargetProcessTree = 0;
}

// ---- GUIDs ----
internal static class AudioIIDs
{
    public static readonly Guid IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static readonly Guid IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
}

// ---- 激活参数结构 ----
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;         // AUDIOCLIENT_ACTIVATION_TYPE
    public uint TargetProcessId;       // union: ProcessLoopbackParams.TargetProcessId
    public int ProcessLoopbackMode;    // union: ProcessLoopbackParams.ProcessLoopbackMode
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    public uint cbSize;
    public IntPtr pBlobData;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort vt;         // VT_BLOB = 65
    [FieldOffset(8)] public PropVariantBlob blob;
}

// ---- WAVEFORMATEX ----
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;          // 0xFFFE = WAVEFORMATEXTENSIBLE
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;
}

internal static class WaveFormatExParser
{
    public const ushort FormatTagExtensible = 0xFFFE;
    public static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    /// <summary>解析 GetMixFormat 返回的 WAVEFORMATEX 内存块。</summary>
    public static (int SampleRate, int Channels, int Bits, bool IsFloat) Parse(IntPtr ptr)
    {
        var baseFmt = Marshal.PtrToStructure<WaveFormatEx>(ptr);
        bool isFloat = baseFmt.wFormatTag == 3; // WAVE_FORMAT_IEEE_FLOAT
        if (baseFmt.wFormatTag == FormatTagExtensible)
        {
            // WAVEFORMATEXTENSIBLE: 22 字节扩展,SubFormat GUID 位于偏移 24
            IntPtr guidPtr = IntPtr.Add(ptr, 24);
            var sub = (Guid)Marshal.PtrToStructure(guidPtr, typeof(Guid))!;
            isFloat = sub == SubTypeIeeeFloat;
        }
        return ((int)baseFmt.nSamplesPerSec, (int)baseFmt.nChannels, (int)baseFmt.wBitsPerSample, isFloat);
    }

    /// <summary>
    /// 构造标准混音格式(共享模式回环常用)。Process_Loopback 虚拟设备
    /// 在部分系统上 GetMixFormat 返回 E_NOTIMPL,此时用该格式初始化。
    /// </summary>
    public static IntPtr BuildDefaultFloat(int sampleRate = 48000, int channels = 2)
    {
        var fmt = new WaveFormatExtensible
        {
            wFormatTag = FormatTagExtensible,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            nAvgBytesPerSec = (uint)(sampleRate * channels * 4),
            nBlockAlign = (ushort)(channels * 4),
            wBitsPerSample = 32,
            cbSize = 22,
            wValidBitsPerSample = 32,
            dwChannelMask = channels == 1 ? 0x4u : 0x3u,
            SubFormat = SubTypeIeeeFloat,
        };
        IntPtr ptr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatExtensible>());
        Marshal.StructureToPtr(fmt, ptr, false);
        return ptr;
    }
}

// ---- 设备枚举与音频会话 ----
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    [PreserveSig] int OpenPropertyStore(int access, out IntPtr props);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // IAudioSessionManager
    [PreserveSig] int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags, out IntPtr sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags, out IntPtr audioVolume);
    // IAudioSessionManager2
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification sessionNotification);
    [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification sessionNotification);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int sessionCount);
    [PreserveSig] int GetSession(int sessionCount, [MarshalAs(UnmanagedType.Interface)] out IAudioSessionControl session);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // 注意:不能写成 ": IAudioSessionControl"。实测 ComImport 接口继承时,
    // .NET 会把派生方法放到槽位 3 起(而非基类方法之后),导致调用落到错误方法上。
    // 必须把基类 9 个方法原样重写,保证 vtable 顺序正确。

    // ---- IAudioSessionControl ----
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);

    // ---- IAudioSessionControl2 ----
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionIdentifier);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceIdentifier);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference(int optOut);
}

[ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionNotification
{
    [PreserveSig] int OnSessionCreated([MarshalAs(UnmanagedType.Interface)] IAudioSessionControl newSession);
}

// ---- IAudioClient / IAudioCaptureClient ----
[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration,
        long hnsPeriodicity, IntPtr pFormat, ref Guid audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long hnsLatency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid riid, out IntPtr service);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags,
        out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}

// ---- 异步激活 ----
[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(
        [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceAsyncOperation activateOperation);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, out IntPtr activatedInterface);
}

/// <summary>IAgileObject 标记接口(无方法),使回调可跨线程直达。</summary>
[ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

internal static class NativeMethods
{
    /// <summary>Windows 10 2004+ 按进程回环激活。</summary>
    [DllImport("Mmdevapi.dll", PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariant activationParams,
        [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEvent(IntPtr securityAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    public static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern int GetLastError();
}
