using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PitchBall.Audio.Interop;

namespace PitchBall.Audio;

public enum SourceKind
{
    None,
    Mic,
    System,
    App,
}

/// <summary>一个可选择的音源。</summary>
public class AudioSourceSpec
{
    public SourceKind Kind { get; set; } = SourceKind.None;

    /// <summary>麦克风:设备编号;系统:输出设备 ID;应用:进程 ID。</summary>
    public string? Id { get; set; }

    public string Name { get; set; } = "";
    /// <summary>应用的会话显示名(如网易云音乐)。</summary>
    public string? SessionDisplayName { get; set; }
    /// <summary>应用是否正在播放(会话 Active)。</summary>
    public bool Active { get; set; }

    public string DisplayLabel
        => Kind == SourceKind.App
            ? (string.IsNullOrWhiteSpace(SessionDisplayName) || SessionDisplayName == Name
                ? Name
                : $"{SessionDisplayName} ({Name})")
            : Name;
}

/// <summary>枚举麦克风、输出设备与正在播放的应用会话。</summary>
public class SourceManager : IDisposable
{
    private IAudioSessionManager2? _sessionManager;
    private SessionNotification? _notification;

    /// <summary>有新的音频会话出现(新应用开始播放)。</summary>
    public event Action? SourcesChanged;

    private void NotifySourcesChanged() => UiDispatcher.Post(() => SourcesChanged?.Invoke());

    public List<AudioSourceSpec> GetMicSources()
    {
        var list = new List<AudioSourceSpec>();
        try
        {
            int count = WaveInEvent.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                string name = $"麦克风 {i + 1}";
                try { name = WaveInEvent.GetCapabilities(i).ProductName; } catch { }
                list.Add(new AudioSourceSpec { Kind = SourceKind.Mic, Id = i.ToString(), Name = name });
            }
        }
        catch { }
        return list;
    }

    public List<AudioSourceSpec> GetSystemSources()
    {
        var list = new List<AudioSourceSpec>();
        try
        {
            using var mm = new MMDeviceEnumerator();
            foreach (var device in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                string label = device.FriendlyName;
                if (device.ID == mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID)
                {
                    label += "(默认)";
                }
                list.Add(new AudioSourceSpec { Kind = SourceKind.System, Id = device.ID, Name = label });
            }
        }
        catch { }
        return list;
    }

    public List<AudioSourceSpec> GetAppSources()
    {
        var list = new List<AudioSourceSpec>();
        try
        {
            EnsureSessionManager();
            if (_sessionManager == null) return list;
            _sessionManager.GetSessionEnumerator(out var sessionEnum);
            sessionEnum.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    sessionEnum.GetSession(i, out var session);
                    if (session == null) continue;
                    session.GetState(out int state);
                    if (state == 2) continue; // Expired

                    uint pid = 0;
                    string displayName = "";
                    if (session is IAudioSessionControl2 s2)
                    {
                        int pidHr = s2.GetProcessId(out pid);
                        // AUDCLNT_S_NO_SINGLE_PROCESS:跨进程会话(部分 UWP 应用),拿不到单个进程
                        if (pidHr != 0) pid = 0;
                    }
                    try { session.GetDisplayName(out displayName); } catch { }
                    if (pid == 0) continue;

                    string processName = "";
                    try { processName = Path.GetFileNameWithoutExtension(Process.GetProcessById((int)pid).ProcessName); }
                    catch { }

                    list.Add(new AudioSourceSpec
                    {
                        Kind = SourceKind.App,
                        Id = pid.ToString(),
                        Name = string.IsNullOrWhiteSpace(processName) ? $"进程 {pid}" : processName,
                        SessionDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                        Active = state == 1,
                    });
                }
                catch
                {
                    // 单个会话枚举失败不影响其他会话
                }
            }
        }
        catch { }
        return list;
    }

    private void EnsureSessionManager()
    {
        if (_sessionManager != null) return;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(
                CoreAudioConstants.EDataFlowERender, CoreAudioConstants.ERoleMultimedia, out var device);
            if (hr < 0) return;
            var iid = AudioIIDs.IAudioSessionManager2;
            hr = device.Activate(ref iid, CoreAudioConstants.ClsCtxAll, IntPtr.Zero, out object? mgrObj);
            if (hr < 0) return;
            _sessionManager = (IAudioSessionManager2)mgrObj!;

            // 注册会话通知:新应用开始播放时刷新来源列表
            _notification = new SessionNotification(NotifySourcesChanged);
            _sessionManager.RegisterSessionNotification(_notification);
        }
        catch
        {
            _sessionManager = null;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_sessionManager != null && _notification != null)
            {
                _sessionManager.UnregisterSessionNotification(_notification);
            }
        }
        catch { }
        _notification = null;
        _sessionManager = null;
    }

    private sealed class SessionNotification : IAudioSessionNotification
    {
        private readonly Action _onCreated;
        public SessionNotification(Action onCreated) => _onCreated = onCreated;
        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            _onCreated();
            return 0;
        }
    }
}
