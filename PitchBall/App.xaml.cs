using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PitchBall.Audio;
using PitchBall.Audio.Interop;
using PitchBall.Models;
using PitchBall.Services;
using PitchBall.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace PitchBall;

public partial class App : Application
{
    public static App Instance => (App)Current;

    public SettingsService Settings { get; } = new();
    public HistoryService History { get; } = new();
    public ThemeService Theme { get; } = new();
    public AudioCaptureEngine Engine { get; } = new();
    public SourceManager Sources { get; } = new();

    public new MainWindow? MainWindow { get; private set; }
    public PitchBallWindow? Ball { get; private set; }
    public bool IsExiting { get; private set; }

    private Mutex? _mutex;
    private TrayIconService? _tray;
    private bool _runUiTest;
    private readonly SemaphoreSlim _analyzeLock = new(1);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RegisterGlobalCrashHandlers();

        // 命令行自检模式(不启动界面)
        if (e.Args.Length > 0 && e.Args[0] == "--selftest")
        {
            RunSelfTest([.. e.Args.Skip(1)]);
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--listsources")
        {
            RunListSources();
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--capturetest" && e.Args.Length > 1)
        {
            double secs = 4.0;
            string? dump = null;
            for (int i = 2; i < e.Args.Length; i++)
            {
                if (double.TryParse(e.Args[i], out double s)) secs = s;
                else if (e.Args[i] == "--dump" && i + 1 < e.Args.Length) dump = e.Args[i + 1];
            }
            RunCaptureTest(e.Args[1], secs, dump);
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--listsessions")
        {
            RunListSessions();
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--nasessions")
        {
            RunNaudioSessions();
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--yinfile" && e.Args.Length > 1)
        {
            RunYinFile(e.Args[1],
                e.Args.Length > 2 && double.TryParse(e.Args[2], out double thr) ? thr : -1,
                e.Args.Length > 3 && double.TryParse(e.Args[3], out double s0) ? s0 : 0,
                e.Args.Length > 4 && double.TryParse(e.Args[4], out double s1) ? s1 : double.MaxValue,
                e.Args.Contains("--frames"),
                antiOvershoot: !e.Args.Contains("--noanti"),
                dumpCand: e.Args.Contains("--dumpcand"));
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--yinfile2" && e.Args.Length > 1)
        {
            RunAnalysisDiag(e.Args[1],
                e.Args.Length > 2 && double.TryParse(e.Args[2], out double a0) ? a0 : 0,
                e.Args.Length > 3 && double.TryParse(e.Args[3], out double a1) ? a1 : double.MaxValue);
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--pyinfile" && e.Args.Length > 1)
        {
            // --pyinfile <path> [startSec] [endSec] [contextSec] [yinTrust] [profile]
            if (e.Args.Length > 6)
            {
                PyinPitchDetector.VocalProfile = ParseVocalProfile(e.Args[6]);
            }
            RunPyinFile(e.Args[1],
                e.Args.Length > 2 && double.TryParse(e.Args[2], out double pf0) ? pf0 : 0,
                e.Args.Length > 3 && double.TryParse(e.Args[3], out double pf1) ? pf1 : 60,
                e.Args.Length > 4 && double.TryParse(e.Args[4], out double pfctx) ? pfctx : 1.0,
                e.Args.Length > 5 && double.TryParse(e.Args[5], out double pftr) ? pftr : 0.5);
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--rmvpefile" && e.Args.Length > 1)
        {
            // --rmvpefile <path> [startSec] [endSec] [thred]  (RMVPE 诊断)
            RunRmvpeFile(e.Args[1],
                e.Args.Length > 2 && double.TryParse(e.Args[2], out double rv0) ? rv0 : 0,
                e.Args.Length > 3 && double.TryParse(e.Args[3], out double rv1) ? rv1 : double.MaxValue,
                e.Args.Length > 4 && double.TryParse(e.Args[4], out double rvt) ? rvt : 0.03);
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--rmvpert" && e.Args.Length > 1)
        {
            // --rmvpert <path> [startSec] [endSec] [rate]  实时 RMVPE 链路诊断(不需要麦克风)
            RunRmvpeRt(e.Args[1],
                e.Args.Length > 2 && double.TryParse(e.Args[2], out double q0) ? q0 : 0,
                e.Args.Length > 3 && double.TryParse(e.Args[3], out double q1) ? q1 : double.MaxValue,
                e.Args.Length > 4 && int.TryParse(e.Args[4], out int qr) ? qr : 44100);
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--pyinsine")
        {
            RunPyinSine(e.Args.Length > 1 && double.TryParse(e.Args[1], out double sf) ? sf : 440,
                e.Args.Length > 2 && double.TryParse(e.Args[2], out double sd) ? sd : 2);
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--pyinrt" && e.Args.Length > 1)
        {
            // 模拟实时引擎的 pYIN 路径:44100Hz、4096 帧/2048 跳、8×256 子帧、流式回溯
            if (e.Args.Length > 4)
            {
                PyinPitchDetector.VocalProfile = ParseVocalProfile(e.Args[4]);
            }
            RunPyinRt(e.Args[1],
                e.Args.Length > 2 && double.TryParse(e.Args[2], out double rt0) ? rt0 : 0,
                e.Args.Length > 3 && double.TryParse(e.Args[3], out double rt1) ? rt1 : double.MaxValue);
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--pyinhighs" && e.Args.Length > 1)
        {
            // 全文件 pYIN 分析并提取高音段落(诊断用);可选第 2 参指定人声场景
            if (e.Args.Length > 2)
            {
                PyinPitchDetector.VocalProfile = ParseVocalProfile(e.Args[2]);
            }
            RunPyinHighs(e.Args[1]);
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--pyinbench" && e.Args.Length > 1)
        {
            // 全文件 pYIN 分析性能基准(诊断用)
            AttachLogging();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int lastPct = -1;
            var progress = new Progress<double>(p =>
            {
                int pct = (int)(p * 100);
                if (pct != lastPct && pct % 10 == 0)
                {
                    lastPct = pct;
                    Console.WriteLine($"  进度 {pct,3}%  已用 {sw.Elapsed.TotalSeconds:F1}s");
                }
            });
            string benchAlgo = e.Args.Length > 2 ? e.Args[2] : "Pyin";
            var result = FileAnalyzer.AnalyzeAsync(e.Args[1], progress, CancellationToken.None, benchAlgo)
                .GetAwaiter().GetResult();
            sw.Stop();
            Console.WriteLine($"BENCH 耗时 {sw.Elapsed.TotalSeconds:F1}s 算法 {result.Algorithm} 帧数 {result.PitchFreqs.Length} 帧率 {result.PitchRate:F1} 有声比 {result.VoicedRatio:P1} 音域 {NoteNames.GetNoteName(result.MinMidi)}-{NoteNames.GetNoteName(result.MaxMidi)}");
            // --dump <csv>: 导出逐帧结果(诊断/对照测试用)
            int dumpIdx = Array.IndexOf(e.Args, "--dump");
            if (dumpIdx >= 0 && dumpIdx + 1 < e.Args.Length)
            {
                using var w = new StreamWriter(e.Args[dumpIdx + 1], false);
                w.WriteLine("time,freq");
                for (int i = 0; i < result.PitchFreqs.Length; i++)
                {
                    w.WriteLine($"{(i / result.PitchRate):F3},{result.PitchFreqs[i]:F2}");
                }
                Console.WriteLine($"  已导出逐帧结果 → {e.Args[dumpIdx + 1]}");
            }
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--slice" && e.Args.Length > 4)
        {
            // --slice <in> <startSec> <endSec> <out.wav> :切出 44100Hz 单声道 PCM16 片段(诊断用)
            AttachLogging();
            RunSlice(e.Args[1],
                double.TryParse(e.Args[2], out double sl0) ? sl0 : 0,
                double.TryParse(e.Args[3], out double sl1) ? sl1 : 10,
                e.Args[4]);
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--genmix" && e.Args.Length > 1)
        {
            GenerateMixTestFile(e.Args[1], e.Args.Length > 2 ? e.Args[2] : "mix");
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--spectrum" && e.Args.Length > 1)
        {
            RunSpectrum(e.Args[1],
                e.Args.Length > 2 && double.TryParse(e.Args[2], out double sp0) ? sp0 : 0,
                e.Args.Length > 3 && double.TryParse(e.Args[3], out double sp1) ? sp1 : 10,
                hopFrames: e.Args.Contains("--hopframes"),
                morePeaks: e.Args.Contains("--peaks8"));
            Shutdown();
            return;
        }
        if (e.Args.Contains("--dumpmenu") || e.Args.Contains("--testhold"))
        {
            _runUiTest = true;
        }

        try
        {
            StartGui();
        }
        catch (Exception ex)
        {
            // 启动期崩溃(双击后"没反应/闪退"的典型原因):记日志并给用户可见提示
            LogCrash(ex, "启动");
            ShowCrashMessage("启动", ex);
            Shutdown();
        }
    }

    private void StartGui()
    {
        // 单实例
        _mutex = new Mutex(true, @"Local\PitchBall_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        Settings.Load();
        History.Load();

        Theme.Mode = Settings.Current.Theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        Theme.StartSystemWatcher();
        Theme.ThemeChanged += () => ApplyAccent(); // 换主题后重新套用强调色
        ApplyAccent();

        Engine.A4Frequency = Settings.Current.A4Frequency;
        Engine.Smoothing = Settings.Current.Smoothing;
        Engine.Algorithm = Settings.Current.PitchAlgorithm;
        PyinPitchDetector.VocalProfile = ParseVocalProfile(Settings.Current.VocalProfile);
        Engine.PitchUpdated += OnPitchUpdated;
        Engine.StatusChanged += OnEngineStatus;
        Engine.CaptureFailed += OnCaptureFailed;

        MainWindow = new MainWindow();
        Ball = new PitchBallWindow();
        Ball.ApplySettings();
        RestoreBallPosition();

        MainWindow.Width = Math.Max(880, Settings.Current.MainW);
        MainWindow.Height = Math.Max(560, Settings.Current.MainH);
        MainWindow.ApplyBackground();

        RestoreLastSource();

        _tray = new TrayIconService(ShowMainWindow, EnterSimpleMode, ExitApp);

        if (Settings.Current.LastViewMode == "Simple")
        {
            EnterSimpleMode();
        }
        else
        {
            ShowMainWindow();
        }

        if (_runUiTest)
        {
            Dispatcher.BeginInvoke(RunUiTests);
        }
    }

    // ---------------- 崩溃日志 ----------------

    private static readonly string CrashLogPath = Path.Combine(SettingsService.DataDir, "crash.log");
    private bool _crashDialogShown;

    private void RegisterGlobalCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception
                     ?? new Exception(args.ExceptionObject?.ToString() ?? "未知异常(无异常对象)");
            LogCrash(ex, "后台线程");
        };
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception, "界面线程");
            if (!_crashDialogShown)
            {
                _crashDialogShown = true;
                ShowCrashMessage("界面线程", args.Exception);
            }
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception, "后台任务");
            args.SetObserved();
        };
    }

    /// <summary>把未处理异常写入 %APPDATA%\PitchBall\crash.log,附带版本与系统信息。</summary>
    private static void LogCrash(Exception? ex, string context)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.DataDir);
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"版本: {typeof(App).Assembly.GetName().Version}");
            sb.AppendLine($"系统: {Environment.OSVersion.VersionString} ({RuntimeInformation.OSDescription})");
            sb.AppendLine($"框架: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"位置: {context}");
            for (var e = ex; e != null; e = e.InnerException)
            {
                sb.AppendLine("----------------------------------------");
                sb.AppendLine($"异常: {e.GetType().FullName}");
                sb.AppendLine($"消息: {e.Message}");
                sb.AppendLine($"堆栈:{Environment.NewLine}{e.StackTrace}");
            }
            File.AppendAllText(CrashLogPath, sb.ToString());
        }
        catch
        {
            // 日志写失败不致命
        }
    }

    /// <summary>崩溃时的用户可见提示,指明日志文件位置。</summary>
    private void ShowCrashMessage(string context, Exception ex)
    {
        string text = "程序遇到了一个错误,已把详细信息记录到日志文件:\n" +
                      CrashLogPath +
                      $"\n\n错误({context}): {ex.Message}" +
                      "\n\n请把该日志文件发给开发者,即可快速定位问题。";
        try
        {
            MessageBox.Show(text, "音高球", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 连消息框都弹不出来的极端情况
        }
    }

    // ---------------- UI 自测(CLI) ----------------

    private void RunUiTests()
    {
        AttachLogging();
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--dumpmenu")) DumpMenu();
        if (args.Contains("--testhold")) TestHold();
        Console.WriteLine("=== UI 测试完成 ===");
        Shutdown();
    }

    private void DumpMenu()
    {
        var menu = Ball!.BuildContextMenu();
        Console.WriteLine("=== 右键菜单结构 ===");
        WalkMenu(menu.Items, "");
    }

    private static void WalkMenu(System.Windows.Controls.ItemCollection items, string indent)
    {
        foreach (var obj in items)
        {
            switch (obj)
            {
                case System.Windows.Controls.MenuItem mi:
                    Console.WriteLine($"{indent}{mi.Header}{(mi.IsCheckable ? (mi.IsChecked ? " [勾选]" : " [未勾选]") : "")}");
                    if (mi.HasItems) WalkMenu(mi.Items, indent + "  ");
                    break;
                case System.Windows.Controls.Separator:
                    Console.WriteLine($"{indent}---");
                    break;
            }
        }
    }

    private void TestHold()
    {
        var ball = Ball!;
        Console.WriteLine("=== 高音强调保持测试 ===");
        Console.WriteLine($"(设置 HighEmphasis 当前 = {Settings.Current.HighEmphasis})");
        Settings.Current.HighEmphasis = true; // 内存态开启,不落盘

        void Step(string tag, bool hasPitch, double freq, double level)
        {
            ball.UpdatePitch(new PitchSample(hasPitch ? freq : 0, level, DateTime.Now));
            Console.WriteLine($"{tag}: 音名={ball.NoteText.Text} 频率={ball.FreqText.Text} 音分={ball.CentsText.Text}");
        }

        Step("t=0    高音 G5 ", true, 783.99, 0.8);
        Thread.Sleep(200);
        Step("t=200  静音    ", false, 0, 0);
        Thread.Sleep(200);
        Step("t=400  低音 C4 ", true, 261.63, 0.5);
        Thread.Sleep(400);
        Step("t=800  低音 C4 ", true, 261.63, 0.5);
        Step("t=800  静音    ", false, 0, 0);

        Settings.Current.HighEmphasis = false;
        Thread.Sleep(50);
        Step("t=850  关模式静音 ", false, 0, 0);
        Step("t=850  关模式高音", true, 783.99, 0.8);
        Thread.Sleep(800);
        Step("t=1650 关模式静音 ", false, 0, 0);

        Console.WriteLine("=== 高音友好平滑测试 ===");
        double[] win1 = { 261, 261, 261, 784, 784, 784, 784 };
        double r1 = AudioCaptureEngine.MedianSmooth(win1, 7, 392);
        Console.WriteLine($"窗口[3低音+4高音] → {r1:F1} (期待 784)");
        double[] win2 = { 261, 261, 261, 261, 784, 784, 784 };
        double r2 = AudioCaptureEngine.MedianSmooth(win2, 7, 392);
        Console.WriteLine($"窗口[4低音+3高音] → {r2:F1} (期待 261)");
        double[] win3 = { 0, 0, 0, 784, 784, 784, 784 };
        double r3 = AudioCaptureEngine.MedianSmooth(win3, 7, 392);
        Console.WriteLine($"窗口[3静音+4高音] → {r3:F1} (期待 784)");
    }

    // ---------------- 音高事件路由 ----------------

    private void OnPitchUpdated(PitchSample sample)
    {
        Ball?.UpdatePitch(sample);
        MainWindow?.UpdatePitch(sample);
    }

    private void OnEngineStatus(string text) => MainWindow?.UpdateStatus(text);

    private void OnCaptureFailed(string message)
    {
        MainWindow?.UpdateStatus("采集失败");
        if (MainWindow is { IsVisible: true })
        {
            MessageBox.Show(MainWindow, message, "音高球", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------- 音源 ----------------

    public void SelectSource(AudioSourceSpec? spec)
    {
        if (spec == null)
        {
            Engine.Stop();
            return;
        }
        Settings.Current.LastSourceJson = System.Text.Json.JsonSerializer.Serialize(spec);
        Settings.Save();
        Engine.StartSource(spec);
    }

    private void RestoreLastSource()
    {
        try
        {
            if (string.IsNullOrEmpty(Settings.Current.LastSourceJson)) return;
            var spec = System.Text.Json.JsonSerializer.Deserialize<AudioSourceSpec>(Settings.Current.LastSourceJson);
            if (spec == null || spec.Kind == SourceKind.None) return;
            Engine.StartSource(spec);
        }
        catch
        {
            // 上次音源已不可用,保持停止状态
        }
    }

    // ---------------- 窗口模式 ----------------

    /// <summary>设置字符串 → 人声场景先验。</summary>
    public static PyinPitchDetector.VocalProfileType ParseVocalProfile(string profile)
        => profile switch
        {
            "Clean" => PyinPitchDetector.VocalProfileType.Clean,
            "Live" => PyinPitchDetector.VocalProfileType.Live,
            _ => PyinPitchDetector.VocalProfileType.Balanced,
        };

    /// <summary>把设置的强调色应用到全局资源(强调色 / 柔和强调色 / 小球光晕)。</summary>
    public void ApplyAccent(string? hexOverride = null)
    {
        string hex = hexOverride ?? Settings.Current.AccentColor;
        var c = PitchColors.FromHex(hex,
            System.Windows.Media.Color.FromRgb(0x6C, 0x93, 0xFF));
        var resources = Application.Current.Resources;
        var accent = new SolidColorBrush(c);
        accent.Freeze();
        resources["AccentBrush"] = accent;
        var soft = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, c.R, c.G, c.B));
        soft.Freeze();
        resources["AccentSoftBrush"] = soft;
        var glow = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, c.R, c.G, c.B));
        glow.Freeze();
        resources["BallGlowBrush"] = glow;
    }

    public void ShowMainWindow()
    {
        MainWindow?.Show();
        if (MainWindow != null)
        {
            MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
            if (!Settings.Current.OnboardingShown)
            {
                MainWindow.ShowOnboarding(); // 首次启动弹出新手引导(关闭后记忆)
            }
        }
        Settings.Current.LastViewMode = "Main";
        Settings.Save();
    }

    public void EnterSimpleMode()
    {
        MainWindow?.Hide();
        if (Ball != null)
        {
            Ball.ApplySettings();
            Ball.Show();
        }
        Settings.Current.LastViewMode = "Simple";
        Settings.Save();
    }

    /// <summary>小球左键点击:切换主界面显隐。</summary>
    public void ToggleMainWindow()
    {
        if (MainWindow?.IsVisible == true)
        {
            MainWindow.Hide();
        }
        else
        {
            ShowMainWindow();
        }
    }

    public void OpenSettings()
    {
        ShowMainWindow();
        MainWindow?.SetSettingsOpen(true);
    }

    private void RestoreBallPosition()
    {
        var s = Settings.Current;
        if (Ball == null || s.BallX < 0 || s.BallY < 0) return;
        double minX = SystemParameters.VirtualScreenLeft;
        double maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        double minY = SystemParameters.VirtualScreenTop;
        double maxY = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        Ball.Left = Math.Clamp(s.BallX, minX, maxX - Ball.Width);
        Ball.Top = Math.Clamp(s.BallY, minY, maxY - Ball.Height);
    }

    // ---------------- 文件分析 ----------------

    public async Task AnalyzeFileAsync(string path, bool force = false)
    {
        await _analyzeLock.WaitAsync();
        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show(MainWindow, $"文件不存在:{path}", "音高球",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!force)
            {
                var cached = History.LoadCached(path, Settings.Current.PitchAlgorithm, Settings.Current.VocalProfile);
                if (cached != null)
                {
                    MainWindow?.ShowAnalysis(cached);
                    return;
                }

                // 拖入新文件:询问检测算法与人声场景(勾选记住后不再询问)
                if (Settings.Current.AskOnAnalyze)
                {
                    var dlg = new AnalyzeOptionsDialog(Settings.Current.PitchAlgorithm, Settings.Current.VocalProfile)
                    {
                        Owner = MainWindow is { IsVisible: true } ? MainWindow : null,
                    };
                    if (dlg.ShowDialog() != true || !dlg.Confirmed) return;
                    Settings.Current.PitchAlgorithm = dlg.Algorithm;
                    Settings.Current.VocalProfile = dlg.Profile;
                    if (dlg.Remember) Settings.Current.AskOnAnalyze = false;
                    Settings.Save();
                    Engine.Algorithm = dlg.Algorithm;
                    PyinPitchDetector.VocalProfile = ParseVocalProfile(dlg.Profile);
                    MainWindow?.SyncSettingsControls();
                }
            }

            var progress = new Progress<double>(p =>
                MainWindow?.ShowAnalyzing(Path.GetFileName(path), p));
            var result = await FileAnalyzer.AnalyzeAsync(path, progress,
                algorithm: Settings.Current.PitchAlgorithm);
            History.AddOrUpdate(path, result, Settings.Current.PitchAlgorithm, Settings.Current.VocalProfile);
            MainWindow?.RefreshHistoryList();
            MainWindow?.ShowAnalysis(result);

            if (MainWindow?.IsVisible != true)
            {
                ShowMainWindow();
            }
        }
        catch (Exception ex)
        {
            MainWindow?.HideAnalyzingBanner();
            MessageBox.Show(MainWindow, $"分析失败:{ex.Message}", "音高球",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _analyzeLock.Release();
        }
    }

    // ---------------- 退出 ----------------

    public void ExitApp()
    {
        IsExiting = true;
        try
        {
            Ball?.SavePositionAndSize();
            Settings.Save();
            Engine.Dispose();
            Sources.Dispose();
            _tray?.Dispose();
            _mutex?.ReleaseMutex();
        }
        catch { }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!IsExiting)
        {
            try
            {
                Ball?.SavePositionAndSize();
                Settings.Save();
                Engine.Dispose();
                Sources.Dispose();
                _tray?.Dispose();
            }
            catch { }
        }
        base.OnExit(e);
    }

    // ---------------- 命令行自检 ----------------

    private void RunSelfTest(string[] args)
    {
        AttachLogging();
        try
        {
            RunSelfTestInner(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine("自检异常: " + ex);
        }
        Console.Out.Flush();
    }

    /// <summary>CLI 诊断输出同时写入日志文件,便于 GUI 进程无控制台时读取。</summary>
    private void AttachLogging()
    {
        AttachConsole(-1);
        string logPath = Path.Combine(Path.GetTempPath(), "pitchball_selftest.log");
        var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        Console.SetOut(log);
        Console.SetError(log);
    }

    private void RunSelfTestInner(string[] args)
    {
        Console.WriteLine("=== PitchBall 自检 ===");
        Console.WriteLine();

        // 1. YIN 合成正弦测试
        Console.WriteLine("[1] YIN 音高检测(合成正弦波, 44.1kHz):");
        var yin = new PitchDetector(2048);
        var buffer = new float[2048];
        foreach (double freq in new[] { 110.0, 220.0, 440.0, 880.0 })
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (float)(0.8 * Math.Sin(2 * Math.PI * freq * i / 44100.0));
            }
            double detected = yin.GetPitch(buffer, 44100);
            Console.WriteLine($"  正弦 {freq,6:F0} Hz -> 检测 {detected,8:F2} Hz (误差 {Math.Abs(detected - freq) / freq:P1})");
        }

        // 2. 静音帧
        Array.Clear(buffer);
        double silent = yin.GetPitch(buffer, 44100);
        Console.WriteLine($"  静音帧       -> {silent:F1} Hz {(silent == 0 ? "(正确:0)" : "(异常)")}");
        Console.WriteLine();

        // 3. 文件分析
        if (args.Length > 0 && File.Exists(args[0]))
        {
            string path = args[0];
            Console.WriteLine($"[2] 离线分析: {path}");
            try
            {
                var result = FileAnalyzer.AnalyzeAsync(path).GetAwaiter().GetResult();
                Console.WriteLine($"  时长: {result.Duration:F1}s  波形点: {result.WavePeaks.Length}  音高帧: {result.PitchFreqs.Length}");
                Console.WriteLine($"  音域: {NoteNames.GetNoteName(result.MinMidi)} - {NoteNames.GetNoteName(result.MaxMidi)}  有声率: {result.VoicedRatio:P0}");
                var top = result.NoteCounts.OrderByDescending(kv => kv.Value).Take(12);
                Console.WriteLine("  最高频音高:");
                foreach (var kv in top)
                {
                    Console.WriteLine($"    {NoteNames.GetNoteName(kv.Key),-5} {kv.Value,5} 次 ({NoteNames.FormatFrequency(NoteNames.MidiToFrequency(kv.Key))} Hz)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  分析失败: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("[2] 未提供音频文件,跳过离线分析(用法: PitchBall.exe --selftest <音频文件>)");
        }
        Console.WriteLine();
        Console.WriteLine("=== 自检完成 ===");
    }

    private void RunListSources()
    {
        AttachLogging();
        Console.WriteLine("=== 音源列表 ===");
        Console.WriteLine("[麦克风]");
        foreach (var m in Sources.GetMicSources()) Console.WriteLine($"  {m.Id}: {m.Name}");
        Console.WriteLine("[输出设备]");
        foreach (var s in Sources.GetSystemSources()) Console.WriteLine($"  {s.Name}");
        Console.WriteLine("[正在播放的应用]");
        foreach (var a in Sources.GetAppSources())
            Console.WriteLine($"  PID {a.Id}: {a.DisplayLabel} {(a.Active ? "(播放中)" : "")}");
        Console.WriteLine("=== 完成 ===");
    }

    /// <summary>对指定进程做 N 秒回环采集,输出电平与音高统计(诊断用)。
    /// dumpPath 非空时把捕捉到的音频存成 48kHz 单声道 PCM16 WAV。</summary>
    private void RunCaptureTest(string pidText, double seconds, string? dumpPath = null)
    {
        AttachLogging();
        if (!uint.TryParse(pidText, out uint pid))
        {
            Console.WriteLine($"无效 PID: {pidText}");
            return;
        }
        Console.WriteLine($"=== 按应用捕捉测试 PID {pid} ({seconds:F0}s) ===");

        var pending = new List<float>();
        double sampleRate = 48000;
        long packets = 0, totalSamples = 0, voicedFrames = 0, frames = 0, gatedFrames = 0;
        double sumSq = 0, maxRms = 0;
        var freqs = new List<double>();
        var yin = new PitchDetector(AudioCaptureEngine.FrameSize);
        var frame = new float[AudioCaptureEngine.FrameSize];
        // CMNDF 谷值直方图:<0.05 / 0.05-0.1 / 0.1-0.2 / 0.2-0.3 / 0.3-0.5 / >=0.5
        var cmndfBuckets = new long[6];
        long voicedRuns = 0;
        bool prevVoiced = false;
        var perSec = new List<(int Gated, int Voiced, List<double> Fs)>();
        var dumpSamples = dumpPath != null ? new List<float>() : null;
        string? error = null;

        using var capture = new ProcessLoopbackWaveIn(pid);
        capture.DataAvailable += (mono, rate) =>
        {
            sampleRate = rate;
            packets++;
            totalSamples += mono.Length;
            foreach (var s in mono) sumSq += s * s;
            dumpSamples?.AddRange(mono);

            pending.AddRange(mono);
            while (pending.Count >= AudioCaptureEngine.FrameSize)
            {
                pending.CopyTo(0, frame, 0, AudioCaptureEngine.FrameSize);
                pending.RemoveRange(0, AudioCaptureEngine.HopSize);
                frames++;
                double rms = 0;
                for (int i = 0; i < AudioCaptureEngine.FrameSize; i++) rms += frame[i] * frame[i];
                rms = Math.Sqrt(rms / AudioCaptureEngine.FrameSize);
                if (rms > maxRms) maxRms = rms;
                if (rms < 0.004) { prevVoiced = false; continue; }
                gatedFrames++;
                double f = yin.GetPitch(frame, sampleRate);
                double mc = yin.LastMinCmndf;
                int bi = mc < 0.05 ? 0 : mc < 0.1 ? 1 : mc < 0.2 ? 2 : mc < 0.3 ? 3 : mc < 0.5 ? 4 : 5;
                cmndfBuckets[bi]++;
                if (f > 0)
                {
                    voicedFrames++;
                    freqs.Add(f);
                    if (!prevVoiced) voicedRuns++;
                    prevVoiced = true;
                }
                else prevVoiced = false;

                int secIdx = (int)((frames - 1) * AudioCaptureEngine.HopSize / 48000.0);
                while (perSec.Count <= secIdx) perSec.Add((0, 0, []));
                var ps = perSec[secIdx];
                ps.Gated++;
                if (f > 0) { ps.Voiced++; ps.Fs.Add(f); }
                perSec[secIdx] = ps;
            }
        };
        capture.CaptureFailed += msg => error = msg;

        capture.Start();
        Thread.Sleep(TimeSpan.FromSeconds(Math.Max(1, seconds)));
        capture.Stop();

        if (dumpPath != null && dumpSamples != null && dumpSamples.Count > 0)
        {
            WritePcm16Wav(dumpPath, dumpSamples, (int)sampleRate);
            Console.WriteLine($"  已保存音频: {dumpPath} ({dumpSamples.Count} 采样 @ {sampleRate}Hz)");
        }

        double rms = totalSamples > 0 ? Math.Sqrt(sumSq / totalSamples) : 0;
        Console.WriteLine($"  结果: {packets} 包, {totalSamples} 采样 @ {sampleRate}Hz");
        Console.WriteLine($"  电平: RMS {rms:F4} ({(rms > 0 ? 20 * Math.Log10(rms) : -999):F1} dBFS), 峰值 RMS {maxRms:F4}");
        Console.WriteLine($"  YIN: 阈值 {yin.Threshold:F2}, 共 {frames} 帧, 门限内 {gatedFrames} 帧");
        Console.WriteLine($"  CMNDF 谷值直方图: <0.05:{cmndfBuckets[0]}  0.05-0.1:{cmndfBuckets[1]}  0.1-0.2:{cmndfBuckets[2]}  0.2-0.3:{cmndfBuckets[3]}  0.3-0.5:{cmndfBuckets[4]}  >=0.5:{cmndfBuckets[5]}");
        if (error != null) Console.WriteLine($"  错误: {error}");
        else if (packets == 0) Console.WriteLine("  诊断: 未收到任何音频包(目标进程无声音输出或激活失败)");
        else if (rms < 0.0005) Console.WriteLine("  诊断: 有数据但接近静音");
        else if (voicedFrames == 0) Console.WriteLine("  诊断: 有声音但 YIN 未检测到音高(可能是噪声/非乐音)");

        if (freqs.Count > 0)
        {
            freqs.Sort();
            double median = freqs[freqs.Count / 2];
            int midi = NoteNames.FrequencyToMidiNote(median);
            double pct = gatedFrames > 0 ? 100.0 * voicedFrames / gatedFrames : 0;
            int minMidi = NoteNames.FrequencyToMidiNote(freqs[0]);
            int maxMidi = NoteNames.FrequencyToMidiNote(freqs[^1]);
            Console.WriteLine($"  音高: 门限内 {gatedFrames} 帧中 {voicedFrames} 帧有声 ({pct:F0}%), 连续段 {voicedRuns} 段, 中位频率 {median:F1} Hz → {NoteNames.GetNoteName(midi)}");
            Console.WriteLine($"  检测音符范围: {NoteNames.GetNoteName(minMidi)} ~ {NoteNames.GetNoteName(maxMidi)}");
        }
        Console.WriteLine("  逐秒(门限内帧有声数 + 中位音名):");
        for (int i = 0; i < perSec.Count; i++)
        {
            var (g, v, list) = perSec[i];
            string note = "—";
            if (list.Count > 0)
            {
                list.Sort();
                note = NoteNames.GetNoteName(NoteNames.FrequencyToMidiNote(list[list.Count / 2]));
            }
            Console.WriteLine($"    {i,2}s: {v}/{g} 有声  {note}");
        }
        Console.WriteLine("=== 完成 ===");
    }

    /// <summary>切出文件区间为 44100Hz 单声道 PCM16 WAV(与外部 pYIN 参考实现对照用)。</summary>
    private static void RunSlice(string path, double startSec, double endSec, string outPath)
    {
        using var reader = FileAnalyzer.OpenReader(path);
        var stream = new WdlResamplingSampleProvider(reader.ToSampleProvider().ToMono(), 44100)
            .Skip(TimeSpan.FromSeconds(Math.Max(0, startSec)));
        double wantSec = Math.Max(1.0, endSec - startSec) + 0.2;
        var samples = new List<float>((int)(44100 * wantSec));
        var buf = new float[16384];
        while (samples.Count < 44100 * wantSec)
        {
            int n = stream.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            samples.AddRange(buf.AsSpan(0, n).ToArray());
        }
        WritePcm16Wav(outPath, samples, 44100);
        Console.WriteLine($"已切出 {samples.Count / 44100.0:F2}s → {outPath}");
    }

    /// <summary>写 16 位 PCM 单声道 WAV(诊断转储用)。</summary>
    private static void WritePcm16Wav(string path, List<float> samples, int sampleRate)
    {
        int n = samples.Count;
        var pcm = new short[n];
        for (int i = 0; i < n; i++)
            pcm[i] = (short)Math.Round(Math.Clamp(samples[i], -1, 1) * 32767);
        var bytes = new byte[n * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write("RIFF".ToCharArray());
        bw.Write(36 + bytes.Length);
        bw.Write("WAVE".ToCharArray());
        bw.Write("fmt ".ToCharArray());
        bw.Write(16);
        bw.Write((short)1);      // PCM
        bw.Write((short)1);      // 单声道
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data".ToCharArray());
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    /// <summary>用与实时引擎相同的 YIN 管线分析 WAV 文件,输出 CMNDF 直方图与逐秒音高(诊断用)。
    /// threshold 小于等于 0 时使用默认阈值;startSec/endSec 限定分析区间(仅统计,不影响跳帧对齐)。</summary>
    private void RunYinFile(string path, double threshold, double startSec = 0, double endSec = double.MaxValue, bool dumpFrames = false, bool antiOvershoot = true, bool dumpCand = false)
    {
        AttachLogging();
        if (!File.Exists(path)) { Console.WriteLine($"文件不存在: {path}"); return; }
        Console.WriteLine($"=== YIN 文件分析: {Path.GetFileName(path)} ===");

        var seconds = new List<(long Gated, long Voiced, List<double> Freqs)>();
        var cmndfBuckets = new long[6];
        var octaveBuckets = new long[11]; // 检测帧按八度分布(0=C0-B0 … 10=C10-B10)
        long totalGated = 0, totalVoiced = 0, frameIndex = 0;

        var yin = new PitchDetector(AudioCaptureEngine.FrameSize);
        if (threshold > 0) yin.Threshold = threshold;
        yin.AntiOvershoot = antiOvershoot;
        var frame = new float[AudioCaptureEngine.FrameSize];
        var pending = new List<float>();
        var buf = new float[16384];

        using var reader = new AudioFileReader(path);
        var mono = new WdlResamplingSampleProvider(reader, 48000).ToMono(1.0f, 1.0f);
        while (true)
        {
            int read = mono.Read(buf, 0, buf.Length);
            if (read <= 0) break;
            pending.AddRange(buf.AsSpan(0, read));
            while (pending.Count >= AudioCaptureEngine.FrameSize)
            {
                pending.CopyTo(0, frame, 0, AudioCaptureEngine.FrameSize);
                pending.RemoveRange(0, AudioCaptureEngine.HopSize);
                double tFrame = frameIndex * AudioCaptureEngine.HopSize / 48000.0;
                bool inRange = tFrame >= startSec && tFrame < endSec;
                double rms = 0;
                for (int i = 0; i < AudioCaptureEngine.FrameSize; i++) rms += frame[i] * frame[i];
                rms = Math.Sqrt(rms / AudioCaptureEngine.FrameSize);
                if (inRange && rms >= 0.004)
                {
                    double f = yin.GetPitch(frame, 48000);
                    double mc = yin.LastMinCmndf;
                    if (dumpFrames)
                    {
                        Console.WriteLine($"F {tFrame,8:F3}s  {f,7:F1}Hz  cmndf={mc:F3}");
                    }
                    if (dumpCand)
                    {
                        var parts = new List<string>();
                        foreach (var (t, v) in yin.LastCandidates)
                            parts.Add($"{48000.0 / t:F0}Hz({v:F2})");
                        Console.WriteLine($"C {tFrame,8:F3}s  -> {string.Join(' ', parts)}");
                        var lf = yin.LastFlip;
                        if (lf.F > 0)
                            Console.WriteLine($"PF {tFrame,8:F3}s  f={lf.F,7:F1}  x1={lf.X1,6:F4}  p1={lf.P1,6:F4}  edge={lf.EdgeMax,6:F4}  bg={lf.BgMed,6:F4}  pk={lf.PeakOk}  rat={lf.RatioOk}  p3={lf.P3,6:F4}  p5={lf.P5,6:F4}  a3={lf.A3,6:F4}  a5={lf.A5,6:F4}  odd={lf.OddOk}  vwin={lf.VWin:F3}  vhalf={lf.VHalf:F3}  vok={lf.ValleyOk}  psw={lf.PsWin,6:F4}  psh={lf.PsHalf,6:F4}  warm={lf.Warm}  act={lf.FlipActive}  -> {(lf.FlipFinal ? "FLIP" : "keep")}");
                    }
                    int bi = mc < 0.05 ? 0 : mc < 0.1 ? 1 : mc < 0.2 ? 2 : mc < 0.3 ? 3 : mc < 0.5 ? 4 : 5;
                    cmndfBuckets[bi]++;
                    int sec = (int)(frameIndex * AudioCaptureEngine.HopSize / 48000.0);
                    while (seconds.Count <= sec) seconds.Add((0, 0, []));
                    var s = seconds[sec];
                    s.Gated++;
                    if (f > 0)
                    {
                        s.Voiced++; s.Freqs.Add(f);
                        int oct = Math.Clamp(NoteNames.FrequencyToMidiNote(f) / 12, 0, 10);
                        octaveBuckets[oct]++;
                    }
                    seconds[sec] = s;
                    totalGated++;
                    totalVoiced += f > 0 ? 1 : 0;
                }
                frameIndex++;
            }
        }

        Console.WriteLine($"  阈值 {yin.Threshold:F2}: 门限内 {totalGated} 帧, 有声 {totalVoiced} 帧 ({(totalGated > 0 ? 100.0 * totalVoiced / totalGated : 0):F0}%)");
        Console.WriteLine($"  CMNDF 谷值直方图: <0.05:{cmndfBuckets[0]}  0.05-0.1:{cmndfBuckets[1]}  0.1-0.2:{cmndfBuckets[2]}  0.2-0.3:{cmndfBuckets[3]}  0.3-0.5:{cmndfBuckets[4]}  >=0.5:{cmndfBuckets[5]}");
        Console.WriteLine("  检测频率八度分布(有声帧):");
        for (int oct = 0; oct < octaveBuckets.Length; oct++)
        {
            if (octaveBuckets[oct] == 0) continue;
            Console.WriteLine($"    {NoteNames.GetNoteName(oct * 12)} 八度: {octaveBuckets[oct]} 帧");
        }
        int firstNonZero = 0;
        while (firstNonZero < seconds.Count && seconds[firstNonZero].Gated == 0) firstNonZero++;
        const int blockSec = 60;
        Console.WriteLine($"  每 {blockSec}s 块摘要(块内有声帧众数音名,时间为文件绝对秒):");
        for (int b = firstNonZero / blockSec; b * blockSec < seconds.Count; b++)
        {
            long g = 0, v = 0;
            var names = new Dictionary<string, int>();
            for (int i = b * blockSec; i < Math.Min((b + 1) * blockSec, seconds.Count); i++)
            {
                var (gg, vv, list) = seconds[i];
                g += gg; v += vv;
                foreach (double f in list)
                {
                    string nm = NoteNames.GetNoteName(NoteNames.FrequencyToMidiNote(f));
                    names[nm] = names.GetValueOrDefault(nm) + 1;
                }
            }
            string mode = names.Count > 0 ? names.OrderByDescending(kv => kv.Value).First().Key : "—";
            Console.WriteLine($"    [{b * blockSec,5}s-{(b + 1) * blockSec,5}s] {v}/{g} 帧有声  {mode}");
        }
        if (seconds.Count - firstNonZero <= 1000)
        {
            Console.WriteLine("  逐秒音高(每秒门限内帧的中位频率):");
            for (int i = firstNonZero; i < seconds.Count; i++)
            {
                var (gated, voiced, list) = seconds[i];
                string note = "—";
                if (list.Count > 0)
                {
                    list.Sort();
                    double med = list[list.Count / 2];
                    note = $"{NoteNames.GetNoteName(NoteNames.FrequencyToMidiNote(med))} ({med:F1}Hz)";
                }
                Console.WriteLine($"    {i,3}s: {voiced}/{gated} 帧有声  {note}");
            }
        }
        Console.WriteLine("=== 完成 ===");
    }

    /// <summary>复现文件分析管线(FileAnalyzer:1024 帧 @22050Hz,跳距 1024)的逐帧翻转诊断,
    /// 与实时管线(4096 @48000)对比,定位文件视图里高音区八度误判的差异来源。</summary>
    private void RunAnalysisDiag(string path, double startSec = 0, double endSec = double.MaxValue)
    {
        AttachLogging();
        if (!File.Exists(path)) { Console.WriteLine($"文件不存在: {path}"); return; }
        Console.WriteLine($"=== 文件管线分析: {Path.GetFileName(path)} ===");

        var yin = new PitchDetector(FileAnalyzer.AnalysisFrame);
        var frame = new float[FileAnalyzer.AnalysisFrame];
        var pending = new List<float>();
        var buf = new float[16384];
        long frameIndex = 0;

        using var reader = new AudioFileReader(path);
        var stream = new WdlResamplingSampleProvider(reader, FileAnalyzer.AnalysisRate).ToMono(1.0f, 1.0f);
        while (true)
        {
            int read = stream.Read(buf, 0, buf.Length);
            if (read <= 0) break;
            pending.AddRange(buf.AsSpan(0, read));
            while (pending.Count >= FileAnalyzer.AnalysisFrame)
            {
                pending.CopyTo(0, frame, 0, FileAnalyzer.AnalysisFrame);
                pending.RemoveRange(0, FileAnalyzer.AnalysisHop);
                double tFrame = frameIndex * FileAnalyzer.AnalysisHop / (double)FileAnalyzer.AnalysisRate;
                bool inRange = tFrame >= startSec && tFrame < endSec;
                double rms = 0;
                for (int i = 0; i < FileAnalyzer.AnalysisFrame; i++) rms += frame[i] * frame[i];
                rms = Math.Sqrt(rms / FileAnalyzer.AnalysisFrame);
                if (inRange && rms >= 0.005)
                {
                    double f = yin.GetPitch(frame, FileAnalyzer.AnalysisRate);
                    var lf = yin.LastFlip;
                    if (lf.F > 0)
                        Console.WriteLine($"PF {tFrame,8:F3}s  f={lf.F,7:F1}  x1={lf.X1,6:F4}  p1={lf.P1,6:F4}  edge={lf.EdgeMax,6:F4}  bg={lf.BgMed,6:F4}  pk={lf.PeakOk}  rat={lf.RatioOk}  p3={lf.P3,6:F4}  p5={lf.P5,6:F4}  a3={lf.A3,6:F4}  a5={lf.A5,6:F4}  odd={lf.OddOk}  vwin={lf.VWin:F3}  vhalf={lf.VHalf:F3}  vok={lf.ValleyOk}  psw={lf.PsWin,6:F4}  psh={lf.PsHalf,6:F4}  warm={lf.Warm}  act={lf.FlipActive}  -> {(lf.FlipFinal ? "FLIP" : "keep")}  out={f:F1}");
                }
                frameIndex++;
            }
        }
        Console.WriteLine("=== 完成 ===");
    }

    /// <summary>实时 pYIN 离线模拟(诊断用):与 AudioCaptureEngine 相同的子帧推进方式。</summary>
    /// <summary>RMVPE 诊断:整段/区间跑 RMVPE 并逐帧打印(与 --pyinfile 同口径)。</summary>
    private void RunRmvpeFile(string path, double startSec, double endSec, double thred)
    {
        AttachLogging();
        if (!File.Exists(path)) { Console.WriteLine($"文件不存在: {path}"); return; }
        if (!RmvpePitchEngine.Available)
        {
            Console.WriteLine("RMVPE 模型未内嵌(构建时 Assets/rmvpe_int8.onnx 不存在),该算法不可用");
            return;
        }
        Console.WriteLine($"=== RMVPE 分析: {Path.GetFileName(path)} {startSec:F2}-{endSec:F2}s (阈值 {thred}) ===");

        var samples = new List<float>();
        using (var reader = FileAnalyzer.OpenReader(path))
        {
            var stream = new WdlResamplingSampleProvider(
                reader.ToSampleProvider().ToMono(), RmvpePitchEngine.SampleRate);
            var buf = new float[16384];
            int n;
            while ((n = stream.Read(buf, 0, buf.Length)) > 0) samples.AddRange(buf.AsSpan(0, n).ToArray());
        }
        var audio = samples.ToArray();
        Console.WriteLine($"  16kHz 采样 {audio.Length} 点 ({audio.Length / 16000.0:F1}s)");

        using var eng = new RmvpePitchEngine();
        const int chunkFrames = 60 * 100;    // 60s/块(每帧 10ms)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int shown = 0;
        for (int off = 0; off * 160 < audio.Length; off += chunkFrames)
        {
            int len = Math.Min(chunkFrames * 160, audio.Length - off * 160);
            var seg = new float[len];
            Array.Copy(audio, off * 160, seg, 0, len);
            var (fr, cf) = eng.Infer(seg);
            for (int i = 0; i < fr.Length; i++)
            {
                double t = off / 100.0 + i / 100.0;
                if (t < startSec - 0.005 || t > endSec + 0.005) continue;
                double f = cf[i] >= thred ? fr[i] : 0;
                string note = f > 0 ? NoteNames.GetNoteName(NoteNames.FrequencyToMidiNote(f)) : "—";
                Console.WriteLine($"RV {t,8:F3}s  f={f,7:F1}  {note,4}  conf={cf[i]:F3}");
                shown++;
            }
        }
        sw.Stop();
        Console.WriteLine($"  命中区间 {shown} 帧, 用时 {sw.Elapsed.TotalSeconds:F2}s, 实时率 {sw.Elapsed.TotalSeconds / (audio.Length / 16000.0):F3}");
        Console.WriteLine("=== 完成 ===");
    }

    /// <summary>
    /// 实时 RMVPE 链路诊断:把文件按设备采样率解码,再按采集帧长(4096)与真实时间节奏
    /// 喂给 RmvpeRealtimePitch,验证 重采样→环形缓冲→后台推理→取最新值 这条链路。
    /// 不依赖麦克风/系统声音,可与离线路径对照(同一时间点应给出相近音高)。
    /// </summary>
    private void RunRmvpeRt(string path, double startSec, double endSec, int rate)
    {
        AttachLogging();
        if (!File.Exists(path)) { Console.WriteLine($"文件不存在: {path}"); return; }
        if (!RmvpePitchEngine.Available)
        {
            Console.WriteLine("RMVPE 模型未内嵌,该算法不可用");
            return;
        }
        Console.WriteLine($"=== RMVPE 实时链路诊断: {Path.GetFileName(path)} {startSec:F2}-{endSec:F2}s @{rate}Hz ===");

        var samples = new List<float>();
        using (var reader = FileAnalyzer.OpenReader(path))
        {
            var stream = new WdlResamplingSampleProvider(reader.ToSampleProvider().ToMono(), rate);
            var buf = new float[16384];
            int n;
            while ((n = stream.Read(buf, 0, buf.Length)) > 0) samples.AddRange(buf.AsSpan(0, n).ToArray());
        }
        var audio = samples.ToArray();
        Console.WriteLine($"  设备采样 {audio.Length} 点 @{rate}Hz ({audio.Length / (double)rate:F1}s)");

        const int frameSize = AudioCaptureEngine.FrameSize;   // 4096,与采集一致
        using var rt = new RmvpeRealtimePitch(rate);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int frames = audio.Length / frameSize;
        int shown = 0;
        for (int i = 0; i < frames; i++)
        {
            double t = i * (double)frameSize / rate;
            double f = rt.Push(audio.AsSpan(i * frameSize, frameSize), rate);
            if (t + frameSize / (double)rate >= startSec && t <= endSec)
            {
                string note = f > 0 ? NoteNames.GetNoteName(NoteNames.FrequencyToMidiNote(f)) : "—";
                Console.WriteLine($"RT {t,8:F3}s  f={f,7:F1}  {note,4}");
                shown++;
            }
            // 按真实时间节奏推入(检验实时行为:后台推理与"先返回上次值"的语义)
            double target = (i + 1) * (double)frameSize / rate;
            int waitMs = (int)((target - sw.Elapsed.TotalSeconds) * 1000);
            if (waitMs > 0) System.Threading.Thread.Sleep(waitMs);
        }
        Console.WriteLine($"  命中区间 {shown} 帧, 用时 {sw.Elapsed.TotalSeconds:F1}s(应≈音频时长)");
        Console.WriteLine($"  诊断: 引擎可用={RmvpePitchEngine.Available} 推理次数={rt.InferCount} 环内样本={rt.RingCount} 错误={rt.LastError ?? "无"} 窗内最大置信={rt.LastMaxConf:F3} 最佳帧位置(距尾)={rt.LastBestIdxFromEnd} 该帧频率={rt.LastBestFreq:F1} 环内峰值={rt.RingMaxAbs:F4} 窗内峰值={rt.WinMaxAbs:F4} 窗长={rt.WinLen} 窗过零率={rt.WinZcr:F0}/s");
        Console.WriteLine("=== 完成 ===");
    }

    private void RunPyinRt(string path, double startSec, double endSec)
    {
        AttachLogging();
        const int frameSize = AudioCaptureEngine.FrameSize; // 4096
        const int hopSize = AudioCaptureEngine.HopSize;     // 2048
        const int rate = 44100;
        double s0 = Math.Max(0, startSec - 1.0);            // 1s 预热(窗口 + 滞后)

        using var reader = new AudioFileReader(path);
        var stream = new WdlResamplingSampleProvider(reader, rate).ToMono(1.0f, 1.0f)
            .Skip(TimeSpan.FromSeconds(s0));

        var rt = new PyinRealtimePitch();
        var det = new PyinPitchDetector();
        var pending = new List<float>();
        var frame = new float[frameSize];
        var buf = new float[8192];
        int hopIdx = 0;
        double lastT = s0;

        while (true)
        {
            int n = stream.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            pending.AddRange(buf.AsSpan(0, n).ToArray());
            while (pending.Count >= frameSize)
            {
                pending.CopyTo(0, frame, 0, frameSize);
                pending.RemoveRange(0, hopSize);
                double freq = 0;
                int subCount = hopSize / PyinRealtimePitch.SubHop;
                for (int k = 0; k < subCount; k++)
                {
                    int off = (k + 1) * PyinRealtimePitch.SubHop;
                    freq = rt.Push(frame.AsSpan(off, PyinPitchDetector.BlockSize), rate);
                }
                double tHop = s0 + hopIdx * hopSize / (double)rate;
                if (tHop >= startSec && tHop <= endSec)
                {
                    string note = freq > 0 ? NoteNames.GetNoteName(NoteNames.FrequencyToMidiNote(freq)) : "—";
                    var cands = det.Process(frame.AsSpan(frameSize - PyinPitchDetector.BlockSize, PyinPitchDetector.BlockSize), rate);
                    var top = cands.OrderByDescending(c => c.Prob).Take(3)
                        .Select(c => $"{440 * Math.Pow(2, (c.Midi - 69) / 12.0):F0}Hz({c.Prob:F3})");
                    Console.WriteLine($"RT {tHop,8:F3}s  f={freq,7:F1}  {note,4}  候选: {string.Join("  ", top)}");
                }
                hopIdx++;
                lastT = tHop;
            }
            if (lastT > endSec) break;
        }
        Console.WriteLine("=== 完成 ===");
    }

    /// <summary>全文件 pYIN 分析并提取高音段落(诊断用):≥B4 的连续段,按时长排序输出时间戳。</summary>
    private void RunPyinHighs(string path)
    {
        AttachLogging();
        if (!File.Exists(path)) { Console.WriteLine($"文件不存在: {path}"); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double[] freqs;
        string cacheKey = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..12];
        string cache = Path.Combine(Path.GetTempPath(), $"pyin_freqs_{cacheKey}.bin");
        if (File.Exists(cache) && new FileInfo(cache).Length > 0)
        {
            var bytes = File.ReadAllBytes(cache);
            freqs = new double[bytes.Length / 8];
            Buffer.BlockCopy(bytes, 0, freqs, 0, bytes.Length);
            Console.WriteLine($"使用缓存音高轨道({freqs.Length} 帧),跳过分析");
        }
        else
        {
            var result = FileAnalyzer.AnalyzeAsync(path, new Progress<double>(p =>
            {
                if ((int)(p * 20) != (int)((p - 0.001) * 20))
                    Console.WriteLine($"  分析进度 {p:P0}  已用 {sw.Elapsed.TotalSeconds:F0}s");
            }), CancellationToken.None, "Pyin").GetAwaiter().GetResult();
            sw.Stop();
            Console.WriteLine($"分析完成,耗时 {sw.Elapsed.TotalSeconds:F0}s,帧数 {result.PitchFreqs.Length},帧率 {result.PitchRate:F1}");
            freqs = result.PitchFreqs;
            var bytes = new byte[freqs.Length * 8];
            Buffer.BlockCopy(freqs, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(cache, bytes);
        }
        const double rate = 22050.0 / FileAnalyzer.PyinHop; // 与 AnalyzePyin 一致

        double highFreq = NoteNames.MidiToFrequency(70, 440); // A#4
        const int minGapFrames = 10;  // 间隙 ≤ 0.23s 视为同段
        const int minLenFrames = 10;  // 段长 ≥ 0.23s
        var segs = new List<(double Start, double End, List<double> Fs)>();
        double segStart = -1;
        int lastHigh = -1;
        var fs = new List<double>();
        void CloseSeg()
        {
            if (segStart >= 0 && fs.Count >= minLenFrames)
                segs.Add((segStart, (lastHigh + 1) / rate, fs));
            fs = [];
            segStart = -1;
        }
        for (int i = 0; i < freqs.Length; i++)
        {
            double f = freqs[i];
            if (f >= highFreq)
            {
                if (segStart < 0 || i - lastHigh > minGapFrames)
                {
                    CloseSeg();
                    segStart = i / rate;
                }
                lastHigh = i;
                fs.Add(f);
            }
        }
        CloseSeg();

        // 把间隔 ≤5s 的段落合并为"组"(同一乐句的起伏)
        var groups = new List<(double Start, double End, List<double> Fs)>();
        foreach (var s in segs.OrderBy(x => x.Start))
        {
            if (groups.Count > 0 && s.Start - groups[^1].End <= 5)
            {
                var g = groups[^1];
                g.Fs.AddRange(s.Fs);
                groups[^1] = (g.Start, s.End, g.Fs);
            }
            else
            {
                groups.Add(s);
            }
        }
        groups.Sort((a, b) => b.Fs.Count.CompareTo(a.Fs.Count));

        Console.WriteLine($"高音组(≥A#4,间隔≤5s合并)共 {groups.Count} 组:");
        for (int i = 0; i < groups.Count; i++)
        {
            var (s, e, list) = groups[i];
            var maxF = list.Max();
            var sorted = list.OrderBy(x => x).ToList();
            double medF = sorted[sorted.Count / 2];
            Console.WriteLine($"  {i + 1,2}. {FormatTs(s)} - {FormatTs(e)}  时长 {e - s:F1}s  峰值 {NoteNames.GetNoteName(NoteNames.FrequencyToMidiNote(maxF))}({maxF:F0}Hz)  中位 {NoteNames.GetNoteName(NoteNames.FrequencyToMidiNote(medF))}({medF:F0}Hz)");
        }
        Console.WriteLine("=== 完成 ===");
    }

    private static string FormatTs(double sec)
    {
        int m = (int)(sec / 60);
        int s = (int)(sec % 60);
        return $"{m}:{s:00}";
    }

    /// <summary>pYIN 区域分析(CLI 诊断):复现文件管线 pYIN 路径,输出逐帧
    /// 平滑频率与候选。startSec/endSec 限定打印区间(分析含 1 秒前导上下文)。</summary>
    private void RunPyinFile(string path, double startSec = 0, double endSec = 60, double contextSec = 1.0, double yinTrust = 0.5)
    {
        AttachLogging();
        if (!File.Exists(path)) { Console.WriteLine($"文件不存在: {path}"); return; }
        const int rate = FileAnalyzer.AnalysisRate;   // 22050
        const int hop = FileAnalyzer.PyinHop;         // 512 → 43 fps
        double s0 = Math.Max(0, startSec - contextSec); // 留上下文给 Viterbi
        Console.WriteLine($"=== pYIN 区域分析: {Path.GetFileName(path)} {startSec:F2}-{endSec:F2}s(含 {s0:F2}s 起上下文)===");

        var candFrames = new List<PyinCandidate[]>();
        var rmsList = new List<double>();
        var regList = new List<VocalRegister>();
        var pending = new List<float>();
        var buf = new float[16384];
        var frame = new float[PyinPitchDetector.BlockSize];
        var det = new PyinPitchDetector();
        int frameIndex = 0;
        double lastT = s0;

        using var reader = new AudioFileReader(path);
        var stream = new WdlResamplingSampleProvider(reader, rate).ToMono(1.0f, 1.0f)
            .Skip(TimeSpan.FromSeconds(s0));
        while (true)
        {
            int read = stream.Read(buf, 0, buf.Length);
            if (read <= 0) break;
            pending.AddRange(buf.AsSpan(0, read));
            while (pending.Count >= PyinPitchDetector.BlockSize)
            {
                pending.CopyTo(0, frame, 0, PyinPitchDetector.BlockSize);
                pending.RemoveRange(0, hop);
                double tFrame = s0 + frameIndex * hop / (double)rate;
                if (tFrame > endSec) break;
                candFrames.Add(det.Process(frame, rate));
                regList.Add(det.LastRegister);
                double rms = 0;
                for (int i = 0; i < frame.Length; i++) rms += frame[i] * frame[i];
                rmsList.Add(Math.Sqrt(rms / frame.Length));
                lastT = tFrame;
                frameIndex++;
            }
            if (lastT > endSec) break;
        }

        var hmm = new PyinMonoPitch(hop / (double)rate, yinTrust);
        var statePath = hmm.DecodeViterbi(candFrames.Count, i => hmm.CalculateObsProb(candFrames[i]));
        Console.WriteLine($"  帧数 {candFrames.Count}, 区间 {s0:F2}-{lastT:F2}s");
        for (int i = 0; i < candFrames.Count; i++)
        {
            double tFrame = s0 + i * hop / (double)rate;
            if (tFrame < startSec - 0.05 || tFrame > endSec + 0.05) continue;
            double f = hmm.MapStateToFreq(statePath[i], candFrames[i]);
            string note = f > 0 ? NoteNames.GetNoteName(NoteNames.FrequencyToMidiNote(f)) : "—";
            string reg = regList[i] switch
            {
                VocalRegister.Falsetto => "假声",
                VocalRegister.Mixed => "混声",
                _ => "真声",
            };
            var top = candFrames[i].OrderByDescending(c => c.Prob).Take(3)
                .Select(c => $"{440 * Math.Pow(2, (c.Midi - 69) / 12.0):F0}Hz({c.Prob:F3})");
            Console.WriteLine($"PY {tFrame,8:F3}s  f={f,7:F1}  {note,4}  {reg,2}  rms={rmsList[i]:F4}  候选: {string.Join("  ", top)}");
        }
        Console.WriteLine("=== 完成 ===");
    }

    /// <summary>生成单频正弦 WAV 后走一遍 pYIN 区域分析(移植正确性冒烟测试)。</summary>
    private void RunPyinSine(double freq, double seconds)
    {
        var samples = new List<float>((int)(44100 * seconds));
        for (int i = 0; i < 44100 * seconds; i++)
            samples.Add((float)(0.5 * Math.Sin(2 * Math.PI * freq * i / 44100)));
        string tmp = Path.Combine(Path.GetTempPath(), "pyin_sine_test.wav");
        WritePcm16Wav(tmp, samples, 44100);
        RunPyinFile(tmp, 0.5, Math.Max(0.6, seconds - 0.1));
    }

    /// <summary>对文件指定区间做 FFT 频谱分析,每 0.25s 窗口输出最强 5 个谱峰(诊断用:
    /// 用来判断某秒检测出的低音是真实能量还是次谐波误锁)。</summary>
    private void RunSpectrum(string path, double startSec, double endSec, bool hopFrames = false, bool morePeaks = false)
    {
        AttachLogging();
        if (!File.Exists(path)) { Console.WriteLine($"文件不存在: {path}"); return; }
        Console.WriteLine($"=== 频谱分析: {Path.GetFileName(path)} {startSec:F1}-{endSec:F1}s ===");

        const int sr = 48000;
        const int fftSize = 4096;
        int hop = hopFrames ? 2048 : 12000; // 85ms(与引擎帧对齐)或 0.25s
        long startSample = (long)(startSec * sr);
        long endSample = (long)(endSec * sr);

        using var reader = new AudioFileReader(path);
        var mono = new WdlResamplingSampleProvider(reader, sr).ToMono(1.0f, 1.0f);
        var seg = new List<float>();
        long pos = 0;
        var buf = new float[16384];
        while (true)
        {
            int read = mono.Read(buf, 0, buf.Length);
            if (read <= 0) break;
            for (int i = 0; i < read; i++)
            {
                if (pos >= startSample && pos < endSample) seg.Add(buf[i]);
                pos++;
                if (pos >= endSample) break;
            }
            if (pos >= endSample) break;
        }

        var re = new double[fftSize];
        var im = new double[fftSize];
        for (int off = 0; off + fftSize <= seg.Count; off += hop)
        {
            for (int i = 0; i < fftSize; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (fftSize - 1));
                re[i] = seg[off + i] * w;
                im[i] = 0;
            }
            Fft(re, im);
            // 60Hz~2kHz 内找幅度局部峰,取前 5
            var peaks = new List<(double hz, double amp)>();
            int lo = Math.Max(2, (int)(60.0 * fftSize / sr));
            int hi = Math.Min(fftSize / 2, (int)(2000.0 * fftSize / sr));
            for (int k = lo + 1; k < hi - 1; k++)
            {
                double a = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                double al = Math.Sqrt(re[k - 1] * re[k - 1] + im[k - 1] * im[k - 1]);
                double ar = Math.Sqrt(re[k + 1] * re[k + 1] + im[k + 1] * im[k + 1]);
                if (a >= al && a > ar) peaks.Add((k * sr / (double)fftSize, a));
            }
            peaks.Sort((a, b) => b.amp.CompareTo(a.amp));
            var top = peaks.Take(morePeaks ? 8 : 5)
                .Select(p => $"{p.hz,6:F0}Hz({20 * Math.Log10(Math.Max(p.amp, 1e-9)):F0}dB)");
            double t = startSec + off / (double)sr;
            Console.WriteLine($"  {t,7:F2}s: {string.Join("  ", top)}");
        }
        Console.WriteLine("=== 完成 ===");
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curR = 1, curI = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int u = i + k, v = u + len / 2;
                    double tr = re[v] * curR - im[v] * curI;
                    double ti = re[v] * curI + im[v] * curR;
                    re[v] = re[u] - tr; im[v] = im[u] - ti;
                    re[u] += tr; im[u] += ti;
                    double nR = curR * wr - curI * wi;
                    curI = curR * wi + curI * wr;
                    curR = nR;
                }
            }
        }
    }

    /// <summary>生成测试 WAV(48kHz 单声道 PCM16)。kind: mix=伴奏+人声 / vox=纯人声 / acc=纯伴奏。
    /// 旋律已知,可与逐秒检测结果直接比对,用于校准 YIN 阈值。</summary>
    private void GenerateMixTestFile(string path, string kind)
    {
        AttachLogging();
        bool withAcc = kind != "vox";
        bool withVox = kind != "acc";
        const int sr = 48000;
        // 人声旋律:每个音 2 秒
        double[] melody =
        [
            220.00, 261.63, 329.63, 392.00, 440.00, 523.25, 659.26, 783.99, 880.00,
            659.26, 523.25, 392.00, 329.63, 261.63, 220.00,
        ];
        double noteSec = 2.0;
        double totalSec = melody.Length * noteSec + 1.0; // 结尾 1 秒纯伴奏
        int n = (int)(totalSec * sr);
        var samples = new double[n];
        var rng = new Random(1234);

        double[][] chords =
        [
            [220.00, 261.63, 329.63],   // Am
            [174.61, 220.00, 261.63],   // F
            [130.81, 196.00, 261.63],   // C
            [196.00, 246.94, 293.66],   // G
        ];

        double voxPhase = 0; // 人声相位逐样本积分,颤音才是真正的频率调制而非 chirp
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sr;

            // --- 伴奏:和弦垫(带泛音)+ 低八度贝斯 + 鼓点噪声 ---
            double acc = 0;
            if (withAcc)
            {
                var chord = chords[(int)(t / 2.0) % 4];
                foreach (double f0 in chord)
                {
                    acc += Math.Sin(2 * Math.PI * f0 * t)
                         + 0.5 * Math.Sin(2 * Math.PI * f0 * 2 * t)
                         + 0.25 * Math.Sin(2 * Math.PI * f0 * 3 * t);
                }
                acc *= 0.08;
                acc += 0.18 * Math.Sin(2 * Math.PI * chord[0] / 2 * t);
                double beat = t % 0.5;
                if (beat < 0.08) acc += (rng.NextDouble() * 2 - 1) * (1 - beat / 0.08) * 0.35;
                if (beat > 0.25 && beat < 0.30) acc += (rng.NextDouble() * 2 - 1) * 0.10;
            }

            // --- 人声:已知旋律,带泛音、颤音与音符边界淡入淡出 ---
            int noteIdx = (int)(t / noteSec);
            double v = 0;
            if (withVox && noteIdx < melody.Length)
            {
                double fNow = melody[noteIdx] * (1 + 0.008 * Math.Sin(2 * Math.PI * 5.5 * t));
                voxPhase += 2 * Math.PI * fNow / sr;
                v = Math.Sin(voxPhase)
                  + 0.45 * Math.Sin(2 * voxPhase)
                  + 0.20 * Math.Sin(3 * voxPhase);
                double tt = t % noteSec;
                double env = Math.Min(1, Math.Min(tt / 0.15, (noteSec - tt) / 0.15));
                v *= 0.55 * env;
            }
            samples[i] = Math.Tanh(acc + v);
        }

        var pcm = new short[n];
        for (int i = 0; i < n; i++)
            pcm[i] = (short)Math.Round(Math.Clamp(samples[i], -1, 1) * 32767);
        var bytes = new byte[n * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

        using var bw = new BinaryWriter(File.Create(path));
        bw.Write("RIFF".ToCharArray());
        bw.Write(36 + bytes.Length);
        bw.Write("WAVE".ToCharArray());
        bw.Write("fmt ".ToCharArray());
        bw.Write(16);
        bw.Write((short)1);        // PCM
        bw.Write((short)1);        // 单声道
        bw.Write(sr);
        bw.Write(sr * 2);          // 字节率
        bw.Write((short)2);        // 块对齐
        bw.Write((short)16);       // 位深
        bw.Write("data".ToCharArray());
        bw.Write(bytes.Length);
        bw.Write(bytes);
        Console.WriteLine($"已生成 {path} [{kind}]: {totalSec:F1}s, {n} 采样, 旋律 {melody.Length} 音(每音 {noteSec:F0}s)");
        Console.WriteLine("=== 完成 ===");
    }

    /// <summary>用 NAudio 的会话管理枚举会话,与自写 Interop 结果对照。</summary>
    private void RunNaudioSessions()
    {
        AttachLogging();
        Console.WriteLine("=== NAudio 会话枚举 ===");
        try
        {
            using var mm = new MMDeviceEnumerator();
            var device = mm.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            Console.WriteLine($"默认设备: {device.FriendlyName}");
            var mgr = device.AudioSessionManager;
            var sessions = mgr.Sessions;
            Console.WriteLine($"会话数: {sessions.Count}");
            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var s = sessions[i];
                    string sid = "";
                    try { sid = s.GetSessionIdentifier; } catch { }
                    Console.WriteLine($"  [{i}] pid={s.GetProcessID} state={s.State} disp=\"{s.DisplayName}\" gp={s.GetGroupingParam()} sid={sid}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  会话异常: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NAudio 枚举异常: {ex}");
        }
        Console.WriteLine("=== 完成 ===");
    }

    /// <summary>详细枚举所有音频会话(诊断会话枚举是否正常)。</summary>
    private void RunListSessions()    {
        AttachLogging();
        Console.WriteLine("=== 音频会话详细枚举 ===");
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(
                CoreAudioConstants.EDataFlowERender, CoreAudioConstants.ERoleMultimedia, out var device);
            Console.WriteLine($"GetDefaultAudioEndpoint: 0x{hr:X8}");
            if (hr < 0) { Console.WriteLine("=== 完成 ==="); return; }

            var iid = AudioIIDs.IAudioSessionManager2;
            hr = device.Activate(ref iid, CoreAudioConstants.ClsCtxAll, IntPtr.Zero, out object? mgrObj);
            Console.WriteLine($"Activate(IAudioSessionManager2): 0x{hr:X8}");
            if (hr < 0 || mgrObj == null) { Console.WriteLine("=== 完成 ==="); return; }

            var mgr = (IAudioSessionManager2)mgrObj;
            hr = mgr.GetSessionEnumerator(out var sessionEnum);
            Console.WriteLine($"GetSessionEnumerator: 0x{hr:X8}");
            if (hr < 0) { Console.WriteLine("=== 完成 ==="); return; }

            hr = sessionEnum.GetCount(out int count);
            Console.WriteLine($"GetCount: 0x{hr:X8}, 会话数: {count}");
            for (int i = 0; i < count; i++)
            {
                try
                {
                    hr = sessionEnum.GetSession(i, out var session);
                    if (hr < 0)
                    {
                        Console.WriteLine($"  [{i}] GetSession 失败: 0x{hr:X8}");
                        continue;
                    }
                    if (session == null)
                    {
                        Console.WriteLine($"  [{i}] GetSession S_OK 但会话为空");
                        continue;
                    }
                    int stateHr = session.GetState(out int state);
                    uint pid = 0;
                    string disp = "";
                    string sid = "";
                    if (session is IAudioSessionControl2 s2)
                    {
                        try
                        {
                            int h = s2.GetProcessId(out pid);
                            Console.WriteLine($"   GetProcessId hr=0x{h:X8} pid={pid}");
                        }
                        catch (Exception ex2) { Console.WriteLine($"   GetProcessId 异常: {ex2}"); }
                        try
                        {
                            int h2 = s2.GetSessionIdentifier(out sid);
                            Console.WriteLine($"   GetSessionIdentifier hr=0x{h2:X8} sid=\"{sid}\"");
                        }
                        catch (Exception ex3) { Console.WriteLine($"   GetSessionIdentifier 异常: {ex3}"); }
                    }
                    else
                    {
                        Console.WriteLine("   (不是 IAudioSessionControl2)");
                    }
                    try
                    {
                        int h3 = session.GetDisplayName(out disp);
                        Console.WriteLine($"   GetDisplayName hr=0x{h3:X8} disp=\"{disp}\"");
                    }
                    catch (Exception ex4) { Console.WriteLine($"   GetDisplayName 异常: {ex4}"); }
                    try
                    {
                        int h4 = session.GetIconPath(out string iconPath);
                        Console.WriteLine($"   GetIconPath hr=0x{h4:X8} icon=\"{iconPath}\"");
                    }
                    catch (Exception ex5) { Console.WriteLine($"   GetIconPath 异常: {ex5}"); }
                    try
                    {
                        int h5 = session.GetGroupingParam(out Guid gp);
                        Console.WriteLine($"   GetGroupingParam hr=0x{h5:X8} gp={gp}");
                    }
                    catch (Exception ex6) { Console.WriteLine($"   GetGroupingParam 异常: {ex6}"); }
                    if (session is IAudioSessionControl2 s2b)
                    {
                        try
                        {
                            int h6 = s2b.GetSessionInstanceIdentifier(out string inst);
                            Console.WriteLine($"   GetSessionInstanceIdentifier hr=0x{h6:X8} inst=\"{inst}\"");
                        }
                        catch (Exception ex7) { Console.WriteLine($"   GetSessionInstanceIdentifier 异常: {ex7}"); }
                        // 注意:IsSystemSoundsSession 会触发访问冲突(包装器槽位错位),已移除
                    }
                    string shortSid = (sid ?? "").Length > 42 ? sid![..42] : (sid ?? "");
                    Console.WriteLine($"  [{i}] GetSession=0x{hr:X8} state=0x{stateHr:X8}/{state} pid={pid} disp=\"{disp}\" sid={shortSid}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{i}] 异常: {ex}");
                }
            }

            // 实验:直接以 IAudioSessionControl2 获取会话,绕过 is 转换的 QI 路径
            Console.WriteLine("--- 直接以 IAudioSessionControl2 获取 ---");
            var diagEnum = (IAudioSessionEnumerator2Diag)sessionEnum;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    int h = diagEnum.GetSession(i, out var s2);
                    if (h < 0 || s2 == null)
                    {
                        Console.WriteLine($"  [{i}] 直接获取失败 0x{h:X8}");
                        continue;
                    }
                    int hpid = s2.GetProcessId(out uint pid2);
                    int hsid = s2.GetSessionIdentifier(out string sid2);
                    int hstate = s2.GetState(out int state2);
                    Console.WriteLine($"  [{i}] GetProcessId=0x{hpid:X8} pid={pid2} sid=\"{sid2}\" state={state2}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{i}] 直接获取异常: {ex}");
                }
            }

            // 实验:对原始指针显式 QueryInterface 获取 IAudioSessionControl2
            Console.WriteLine("--- 显式 QI 实验 ---");
            var sc2Iid = new Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
            for (int i = 0; i < count; i++)
            {
                try
                {
                    int h = sessionEnum.GetSession(i, out var session);
                    if (h < 0 || session == null) continue;
                    IntPtr raw = Marshal.GetIUnknownForObject(session);
                    int qhr = Marshal.QueryInterface(raw, ref sc2Iid, out IntPtr sc2Ptr);
                    if (qhr < 0)
                    {
                        Console.WriteLine($"  [{i}] 显式QI失败 0x{qhr:X8}");
                        continue;
                    }
                    var sc2 = (IAudioSessionControl2)Marshal.GetObjectForIUnknown(sc2Ptr);
                    int hpid = sc2.GetProcessId(out uint pid2);
                    int hsid = sc2.GetSessionIdentifier(out string sid2);
                    Console.WriteLine($"  [{i}] 显式QI pid={pid2} (hr=0x{hpid:X8}) sid=\"{sid2}\" (hr=0x{hsid:X8})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{i}] 显式QI异常: {ex}");
                }
            }

            // 实验:手动走 vtable,绕过 RCW 槽位计算
            Console.WriteLine("--- 手动 vtable 实验 ---");
            for (int i = 0; i < count; i++)
            {
                try
                {
                    int h = sessionEnum.GetSession(i, out var session);
                    if (h < 0 || session == null) continue;
                    IntPtr ifacePtr = Marshal.GetComInterfaceForObject(session, typeof(IAudioSessionControl));
                    IntPtr unknownPtr = Marshal.GetIUnknownForObject(session);
                    Console.WriteLine($"  [{i}] iface=0x{ifacePtr.ToInt64():X} iunknown=0x{unknownPtr.ToInt64():X}");
                    IntPtr vtbl = Marshal.ReadIntPtr(ifacePtr);
                    for (int slot = 3; slot <= 16; slot++)
                    {
                        try
                        {
                            IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
                            Console.WriteLine($"      slot {slot}: 0x{fn.ToInt64():X}");
                        }
                        catch { Console.WriteLine($"      slot {slot}: 读取失败"); }
                    }
                    var fn4 = Marshal.GetDelegateForFunctionPointer<DiagGetStringDelegate>(Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size));
                    int hr4 = fn4(ifacePtr, out string disp4);
                    Console.WriteLine($"      手动 slot4 GetDisplayName: hr=0x{hr4:X8} disp=\"{disp4}\"");
                    var fn12 = Marshal.GetDelegateForFunctionPointer<DiagGetStringDelegate>(Marshal.ReadIntPtr(vtbl, 12 * IntPtr.Size));
                    int hr12 = fn12(ifacePtr, out string sid12);
                    Console.WriteLine($"      手动 slot12 GetSessionIdentifier: hr=0x{hr12:X8} sid=\"{sid12}\"");
                    var fn14 = Marshal.GetDelegateForFunctionPointer<DiagGetPidDelegate>(Marshal.ReadIntPtr(vtbl, 14 * IntPtr.Size));
                    int hr14 = fn14(ifacePtr, out uint pid14);
                    Console.WriteLine($"      手动 slot14 GetProcessId: hr=0x{hr14:X8} pid={pid14}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{i}] 手动vtable异常: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"枚举异常: {ex}");
        }
        Console.WriteLine("=== 完成 ===");
    }
}

// 诊断用:GetSession 直接返回 IAudioSessionControl2,绕过 is 转换的 QI 路径
[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator2Diag
{
    [PreserveSig] int GetCount(out int sessionCount);
    [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.Interface)] out IAudioSessionControl2 session);
}

// 诊断用:手动 vtable 调用委托
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int DiagGetPidDelegate(IntPtr self, out uint pid);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int DiagGetStringDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] out string value);
