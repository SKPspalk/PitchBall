namespace PitchBall.Models;

/// <summary>应用设置(持久化到 %APPDATA%\PitchBall\settings.json)。</summary>
public class AppSettings
{
    // ---- 外观 ----
    /// <summary>主题:Light / Dark / System。</summary>
    public string Theme { get; set; } = "System";

    public string? BackgroundImagePath { get; set; }

    /// <summary>背景图片虚化程度(0-60)。</summary>
    public double BackgroundBlur { get; set; } = 20;

    /// <summary>界面强调色("#RRGGBB",按钮、高亮、小球光晕等)。</summary>
    public string AccentColor { get; set; } = "#6C93FF";

    /// <summary>强调色是否跟随背景图自动变化(开启时从背景主色调派生)。</summary>
    public bool AutoAccent { get; set; } = true;

    /// <summary>最近一次从背景图派生的强调色(供色块显示)。</summary>
    public string AccentColorAuto { get; set; } = "#6C93FF";

    // ---- 小球 ----
    /// <summary>小球显示内容:NoteOnly / NoteFreq / NoteFreqCents。</summary>
    public string BallDisplayMode { get; set; } = "NoteFreqCents";

    /// <summary>小球默认直径(像素)。</summary>
    public double BallSize { get; set; } = 130;

    public bool BallTopmost { get; set; } = true;

    /// <summary>高音强调模式:G4 以上音高保持显示约 700ms,便于看清。</summary>
    public bool HighEmphasis { get; set; } = false;

    /// <summary>小球 12 音级颜色("#RRGGBB",下标 = 音级 C=0)。</summary>
    public string[] PitchClassColors { get; set; } = PitchColors.CreateDefaults();

    /// <summary>小球颜色模式:PitchClass(音级)/ Register(声区)/ VoiceRange(声部,男/女低中高)。</summary>
    public string BallColorMode { get; set; } = "PitchClass";

    /// <summary>声区颜色:真声 / 混声 / 假声("#RRGGBB")。</summary>
    public string[] RegisterColors { get; set; } = ["#FF8C42", "#34C77B", "#B44CFF"];

    /// <summary>声部颜色(男低→男中→男高→女低→女中→女高,6 色,"#RRGGBB")。</summary>
    public string[] GroupColors { get; set; } = ["#4F9DF3", "#2EC4B6", "#8BD450", "#FFC53D", "#FF8C42", "#F2555A"];

    // ---- 音频 ----
    /// <summary>A4 基准频率,默认 440Hz。</summary>
    public double A4Frequency { get; set; } = 440;

    /// <summary>音高平滑窗口(1-7,帧数)。</summary>
    public int Smoothing { get; set; } = 3;

    /// <summary>音高检测算法:"Pyin"(pYIN,默认)/ "Yin"(原手写 YIN)。</summary>
    public string PitchAlgorithm { get; set; } = "Pyin";

    /// <summary>人声场景:"Balanced"(通用,默认)/ "Clean"(纯净CD版)/ "Live"(演唱会版)。</summary>
    public string VocalProfile { get; set; } = "Balanced";

    /// <summary>上次使用的音源描述(JSON)。</summary>
    public string? LastSourceJson { get; set; }

    // ---- 窗口状态 ----
    public string LastViewMode { get; set; } = "Main"; // Main / Simple
    public double BallX { get; set; } = -1;
    public double BallY { get; set; } = -1;
    public double MainW { get; set; } = 1080;
    public double MainH { get; set; } = 680;

    /// <summary>主界面左侧历史栏是否收起(默认收起)。</summary>
    public bool SidebarCollapsed { get; set; } = true;

    /// <summary>是否已完成新手引导(首次启动弹出一次,关闭后不再显示)。</summary>
    public bool OnboardingShown { get; set; } = false;

    /// <summary>拖入音频文件分析时是否询问算法与场景(可在对话框勾选记住后关闭)。</summary>
    public bool AskOnAnalyze { get; set; } = true;
}
