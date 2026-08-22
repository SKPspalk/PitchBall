using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PitchBall.Audio;
using PitchBall.Models;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Separator = System.Windows.Controls.Separator;

namespace PitchBall.Views;

public partial class PitchBallWindow : Window
{
    private const int WmNcHitTest = 0x0084;

    private bool _pressed;
    private bool _dragged;
    private Point _pressPoint;
    private readonly DispatcherTimer _sizeSaveTimer;
    private static readonly Dictionary<int, Brush> PitchClassBrushes = new();

    public PitchBallWindow()
    {
        InitializeComponent();
        _sizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _sizeSaveTimer.Tick += (_, _) =>
        {
            _sizeSaveTimer.Stop();
            SavePositionAndSize();
        };
        SizeChanged += (_, _) =>
        {
            UpdateFonts();
            _sizeSaveTimer.Stop();
            _sizeSaveTimer.Start();
        };
    }

    // ---------------- 边缘拖拽调整大小(WM_NCHITTEST) ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest)
        {
            int x = unchecked((short)(long)lParam);
            int y = unchecked((short)((long)lParam >> 16));
            var pt = PointFromScreen(new Point(x, y));

            const int margin = 10;
            bool left = pt.X <= margin;
            bool right = pt.X >= Width - margin;
            bool top = pt.Y <= margin;
            bool bottom = pt.Y >= Height - margin;

            int hit = 1; // HTCLIENT
            if (top && left) hit = 13;        // HTTOPLEFT
            else if (top && right) hit = 14;  // HTTOPRIGHT
            else if (bottom && left) hit = 16;// HTBOTTOMLEFT
            else if (bottom && right) hit = 17;// HTBOTTOMRIGHT
            else if (top) hit = 12;           // HTTOP
            else if (bottom) hit = 15;        // HTBOTTOM
            else if (left) hit = 10;          // HTLEFT
            else if (right) hit = 11;         // HTRIGHT

            if (hit != 1)
            {
                handled = true;
                return (IntPtr)hit;
            }
        }
        return IntPtr.Zero;
    }

    // ---------------- 拖动移动 / 左键点击切换主界面 ----------------

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _dragged = false;
        _pressPoint = e.GetPosition(this);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || _dragged) return;
        var pt = e.GetPosition(this);
        if (Math.Abs(pt.X - _pressPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pt.Y - _pressPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _dragged = true;
            try { DragMove(); } catch { }
        }
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressed && !_dragged)
        {
            App.Instance.ToggleMainWindow();
        }
        _pressed = false;
        _dragged = false;
        SavePositionAndSize();
    }

    // ---------------- 右键菜单 ----------------

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ContextMenu = BuildContextMenu();
    }

    internal ContextMenu BuildContextMenu()
    {
        var app = App.Instance;
        var menu = new ContextMenu();
        var current = app.Engine.CurrentSource;

        var sourceRoot = new MenuItem { Header = "音频来源" };
        var micRoot = new MenuItem { Header = "麦克风" };
        foreach (var mic in app.Sources.GetMicSources())
        {
            micRoot.Items.Add(MakeSourceItem(mic, current));
        }
        if (micRoot.Items.Count == 0)
        {
            micRoot.Items.Add(new MenuItem { Header = "(未找到麦克风)", IsEnabled = false });
        }

        var sysRoot = new MenuItem { Header = "系统声音" };
        foreach (var sys in app.Sources.GetSystemSources())
        {
            sysRoot.Items.Add(MakeSourceItem(sys, current));
        }
        if (sysRoot.Items.Count == 0)
        {
            sysRoot.Items.Add(new MenuItem { Header = "(未找到输出设备)", IsEnabled = false });
        }

        var appRoot = new MenuItem { Header = "正在播放的应用" };
        var apps = app.Sources.GetAppSources();
        apps = [.. apps.OrderByDescending(a => a.Active)];
        foreach (var s in apps)
        {
            appRoot.Items.Add(MakeSourceItem(s, current));
        }
        if (apps.Count == 0)
        {
            appRoot.Items.Add(new MenuItem { Header = "(暂无正在播放的应用)", IsEnabled = false });
        }
        appRoot.Items.Add(new Separator());
        var rescan = new MenuItem { Header = "重新扫描来源" };
        rescan.Click += (_, _) => ContextMenu = BuildContextMenu();
        appRoot.Items.Add(rescan);

        sourceRoot.Items.Add(micRoot);
        sourceRoot.Items.Add(sysRoot);
        sourceRoot.Items.Add(appRoot);
        var stop = new MenuItem { Header = "停止采集" };
        stop.Click += (_, _) => app.SelectSource(null);
        sourceRoot.Items.Add(new Separator());
        sourceRoot.Items.Add(stop);
        menu.Items.Add(sourceRoot);

        menu.Items.Add(new Separator());

        var displayRoot = new MenuItem { Header = "显示内容" };
        foreach (var (mode, label) in new[]
                 {
                     ("NoteOnly", "仅音名"),
                     ("NoteFreq", "音名 + 频率"),
                     ("NoteFreqCents", "音名 + 频率 + 音分"),
                 })
        {
            var item = new MenuItem { Header = label, IsCheckable = true };
            item.IsChecked = app.Settings.Current.BallDisplayMode == mode;
            var m = mode;
            item.Click += (_, _) =>
            {
                app.Settings.Current.BallDisplayMode = m;
                app.Settings.Save();
                ApplySettings();
            };
            displayRoot.Items.Add(item);
        }
        menu.Items.Add(displayRoot);

        var topmost = new MenuItem { Header = "窗口置顶", IsCheckable = true, IsChecked = Topmost };
        topmost.Click += (_, _) =>
        {
            app.Settings.Current.BallTopmost = !Topmost;
            app.Settings.Save();
            ApplySettings();
        };
        menu.Items.Add(topmost);

        var highEmphasis = new MenuItem { Header = "高音强调模式", IsCheckable = true };
        highEmphasis.IsChecked = app.Settings.Current.HighEmphasis;
        highEmphasis.Click += (_, _) =>
        {
            app.Settings.Current.HighEmphasis = !app.Settings.Current.HighEmphasis;
            app.Settings.Save();
        };
        menu.Items.Add(highEmphasis);

        menu.Items.Add(new Separator());
        var openMain = new MenuItem { Header = "打开主界面" };
        openMain.Click += (_, _) => app.ShowMainWindow();
        menu.Items.Add(openMain);
        var openSettings = new MenuItem { Header = "设置" };
        openSettings.Click += (_, _) => app.OpenSettings();
        menu.Items.Add(openSettings);
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => app.ExitApp();
        menu.Items.Add(exit);

        return menu;
    }

    private MenuItem MakeSourceItem(AudioSourceSpec spec, AudioSourceSpec? current)
    {
        string label = spec.Kind switch
        {
            SourceKind.Mic => "🎤 " + spec.DisplayLabel,
            SourceKind.System => "🔊 " + spec.DisplayLabel,
            SourceKind.App => spec.DisplayLabel + (spec.Active ? "(播放中)" : "(未播放)"),
            _ => spec.DisplayLabel,
        };
        var item = new MenuItem { Header = label, IsCheckable = true };
        item.IsChecked = current != null && current.Kind == spec.Kind && current.Id == spec.Id;
        item.Click += (_, _) => App.Instance.SelectSource(spec);
        return item;
    }

    // ---------------- 拖入文件 ----------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasAudioFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        string path = files[0];
        if (!IsSupportedFile(path)) return;
        await App.Instance.AnalyzeFileAsync(path);
    }

    private static bool HasAudioFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return false;
        return IsSupportedFile(files[0]);
    }

    private static bool IsSupportedFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".wav" or ".mp3" or ".m4a" or ".aac" or ".wma" or ".aiff" or ".aif"
            or ".flac" or ".ogg" or ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv"
            or ".webm" or ".m4v" or ".mpg" or ".mpeg";
    }

    // ---------------- 音高显示 ----------------

    /// <summary>高音强调模式:G4 以上高音保持显示约 700ms,便于看清。</summary>
    private static readonly TimeSpan HighHoldTime = TimeSpan.FromMilliseconds(700);
    private double _lastHighFreq;
    private double _lastHighLevel;
    private int _lastHighMidi;
    private DateTime _lastHighAt = DateTime.MinValue;

    public void UpdatePitch(PitchSample sample)
    {
        var app = App.Instance;
        double a4 = app.Settings.Current.A4Frequency;

        double freq = 0;
        double level = 0;
        int midi = 0;
        if (sample.HasPitch)
        {
            freq = sample.Frequency;
            level = sample.Level;
            midi = NoteNames.FrequencyToMidiNote(freq, a4);
        }

        // 高音强调模式:实时高音直接显示并记录;此后约 700ms 内即使音高回落,
        // 也继续显示刚才的高音,方便看清;超时或来了新的高音立即恢复实时。
        if (app.Settings.Current.HighEmphasis)
        {
            if (sample.HasPitch && midi >= NoteNames.HighNoteMidi)
            {
                _lastHighFreq = freq;
                _lastHighLevel = level;
                _lastHighMidi = midi;
                _lastHighAt = DateTime.Now;
            }
            else if (_lastHighFreq > 0 && DateTime.Now - _lastHighAt <= HighHoldTime)
            {
                freq = _lastHighFreq;
                level = _lastHighLevel;
                midi = _lastHighMidi;
            }
            else
            {
                _lastHighFreq = 0;
            }
        }

        if (freq <= 0)
        {
            NoteText.Text = "—";
            FreqText.Text = "";
            CentsText.Text = "";
            Ring.Stroke = (Brush)Application.Current.Resources["IdleRingBrush"];
            Glow.Opacity = 0.35;
            return;
        }

        NoteText.Text = NoteNames.GetNoteName(midi);
        FreqText.Text = NoteNames.FormatFrequency(freq) + " Hz";
        CentsText.Text = NoteNames.FormatCents(NoteNames.CentsOffset(freq, a4));

        Ring.Stroke = GetBallBrush(midi, sample.Register);
        Glow.Opacity = 0.35 + level * 0.5;
    }

    /// <summary>按颜色模式取小球外圈颜色:音级 / 声区 / 音高分组(3/4/5 组)。</summary>
    private static Brush GetBallBrush(int midi, VocalRegister register)
    {
        string mode = App.Instance.Settings.Current.BallColorMode;
        if (mode == "Register")
        {
            return GetRegisterBrush(register);
        }
        if (mode.StartsWith("Groups") && int.TryParse(mode.AsSpan(6), out int n) && n >= 3 && n <= 5)
        {
            // 分组区间:C2(36)~C7(96),低→高均分 N 组
            double band = (midi - 36) / 60.0 * n;
            int idx = Math.Clamp((int)band, 0, n - 1);
            return GetGroupBrush(idx, n);
        }
        return GetPitchClassBrush(midi);
    }

    private static readonly Dictionary<int, Brush> RegisterBrushes = new();
    private static readonly Dictionary<(int Idx, int N), Brush> GroupBrushes = new();

    private static Brush GetRegisterBrush(VocalRegister register)
    {
        int idx = register switch { VocalRegister.Falsetto => 2, VocalRegister.Mixed => 1, _ => 0 };
        if (RegisterBrushes.TryGetValue(idx, out var brush)) return brush;
        var colors = App.Instance.Settings.Current.RegisterColors;
        var fallback = new[] { "#FF8C42", "#34C77B", "#B44CFF" };
        var c = idx < colors.Length ? PitchColors.FromHex(colors[idx], Colors.White)
            : PitchColors.FromHex(fallback[idx], Colors.White);
        brush = new SolidColorBrush(c);
        brush.Freeze();
        RegisterBrushes[idx] = brush;
        return brush;
    }

    private static Brush GetGroupBrush(int idx, int n)
    {
        if (GroupBrushes.TryGetValue((idx, n), out var brush)) return brush;
        var colors = App.Instance.Settings.Current.GroupColors;
        var fallback = new[] { "#4F9DF3", "#2EC4B6", "#8BD450", "#FFC53D", "#F2555A" };
        var c = idx < colors.Length ? PitchColors.FromHex(colors[idx], Colors.White)
            : PitchColors.FromHex(fallback[idx], Colors.White);
        brush = new SolidColorBrush(c);
        brush.Freeze();
        GroupBrushes[(idx, n)] = brush;
        return brush;
    }

    private static Brush GetPitchClassBrush(int midi)
    {
        int pitchClass = ((midi % 12) + 12) % 12;
        if (PitchClassBrushes.TryGetValue(pitchClass, out var brush)) return brush;
        var colors = App.Instance.Settings.Current.PitchClassColors;
        Color c = pitchClass < colors.Length
            ? PitchColors.FromHex(colors[pitchClass], Colors.White)
            : PitchColors.FromHex(PitchColors.CreateDefaults()[pitchClass], Colors.White);
        brush = new SolidColorBrush(c);
        brush.Freeze();
        PitchClassBrushes[pitchClass] = brush;
        return brush;
    }

    // ---------------- 设置与状态 ----------------

    public void ApplySettings()
    {
        var settings = App.Instance.Settings.Current;
        double size = Math.Clamp(settings.BallSize, 88, 420);
        Width = size;
        Height = size;
        Topmost = settings.BallTopmost;

        string mode = settings.BallDisplayMode;
        FreqText.Visibility = mode != "NoteOnly" ? Visibility.Visible : Visibility.Collapsed;
        CentsText.Visibility = mode == "NoteFreqCents" ? Visibility.Visible : Visibility.Collapsed;
        PitchClassBrushes.Clear(); // 颜色自定义后重建画刷
        RegisterBrushes.Clear();
        GroupBrushes.Clear();
        UpdateFonts();
    }

    private void UpdateFonts()
    {
        double size = Math.Max(88, Math.Min(Width, Height));
        NoteText.FontSize = size * 0.26;
        FreqText.FontSize = size * 0.095;
        CentsText.FontSize = size * 0.085;
    }

    public void SavePositionAndSize()
    {
        var settings = App.Instance.Settings.Current;
        settings.BallSize = Math.Min(Width, Height);
        if (WindowState == WindowState.Normal)
        {
            settings.BallX = Left;
            settings.BallY = Top;
        }
        App.Instance.Settings.Save();
    }

    /// <summary>新手引导演示时切换虚线高亮圈。</summary>
    public void SetGuideHighlight(bool on)
        => GuideRing.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
}
