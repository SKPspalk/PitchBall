using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Ellipse = System.Windows.Shapes.Ellipse;
using PitchBall.Models;
using PitchBall.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Separator = System.Windows.Controls.Separator;

namespace PitchBall.Views;

public partial class MainWindow : Window
{
    private AnalysisResult? _currentAnalysis;
    private bool _suppressSettingsEvents;

    public MainWindow()
    {
        // 构造期间控件初值事件不写回设置,初始化完成后用设置值同步控件
        _suppressSettingsEvents = true;
        try
        {
            InitializeComponent();
        }
        finally
        {
            _suppressSettingsEvents = false;
        }
        SyncSettingsControls();
        RefreshHistoryList();
        Waveform.EnterLive();
        ApplySidebarState();
    }

    // ---------------- 标题栏 ----------------

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        try { DragMove(); } catch { }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnSimpleModeClick(object sender, RoutedEventArgs e) => App.Instance.EnterSimpleMode();

    private void OnSettingsClick(object sender, RoutedEventArgs e) =>
        SetSettingsOpen(SettingsPanel.Visibility != Visibility.Visible);

    private void OnSettingsOverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == SettingsPanel) SetSettingsOpen(false);
    }

    private void OnSettingsCardClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    public void SetSettingsOpen(bool open)
    {
        SettingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) SyncSettingsControls();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => App.Instance.ExitApp();

    protected override void OnClosing(CancelEventArgs e)
    {
        // 点关闭时最小化到托盘,由托盘/小球菜单真正退出
        if (!App.Instance.IsExiting)
        {
            e.Cancel = true;
            App.Instance.EnterSimpleMode();
        }
        base.OnClosing(e);
    }

    // ---------------- 实时音高 ----------------

    public void UpdatePitch(PitchSample sample)
    {
        var app = App.Instance;
        double a4 = app.Settings.Current.A4Frequency;

        if (!sample.HasPitch)
        {
            StatusNoteText.Text = "—";
            StatusDetailText.Text = "等待音频…";
        }
        else
        {
            int midi = NoteNames.FrequencyToMidiNote(sample.Frequency, a4);
            StatusNoteText.Text = NoteNames.GetNoteName(midi);
            string register = sample.Register switch
            {
                VocalRegister.Falsetto => "假声",
                VocalRegister.Mixed => "混声",
                _ => "真声",
            };
            StatusDetailText.Text =
                $"{NoteNames.FormatFrequency(sample.Frequency)} Hz · {NoteNames.FormatCents(NoteNames.CentsOffset(sample.Frequency, a4))} · {register}";
        }

        LevelFill.Width = Math.Max(0, sample.Level) * 150;
        Waveform.PushLiveSample(sample.Frequency, sample.Level);
    }

    public void UpdateStatus(string text) => SourceLabel.Text = text;

    // ---------------- 历史列表 ----------------

    public void RefreshHistoryList()
    {
        var app = App.Instance;
        HistoryList.Items.Clear();
        foreach (var entry in app.History.Entries)
        {
            var item = new ListBoxItem
            {
                Tag = entry,
                Content = BuildHistoryItemContent(entry),
            };
            HistoryList.Items.Add(item);
        }
        HistoryEmptyHint.Visibility = app.History.Entries.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static UIElement BuildHistoryItemContent(HistoryEntry entry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        var name = new TextBlock
        {
            Text = entry.FileName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(name, 0);
        Grid.SetColumnSpan(name, 2);
        grid.Children.Add(name);

        var meta = new TextBlock
        {
            Text = $"{FormatDuration(entry.Duration)} · {entry.AnalyzedAt:MM-dd HH:mm}",
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
        };
        Grid.SetRow(meta, 1);
        grid.Children.Add(meta);

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "查看分析" };
        open.Click += (_, _) => App.Instance.MainWindow?.LoadHistoryEntry(entry);
        var reanalyze = new MenuItem { Header = "重新分析" };
        reanalyze.Click += async (_, _) => await App.Instance.AnalyzeFileAsync(entry.FilePath, true);
        var reveal = new MenuItem { Header = "打开所在文件夹" };
        reveal.Click += (_, _) =>
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{entry.FilePath}\"");
            }
            catch { }
        };
        var delete = new MenuItem { Header = "删除记录" };
        delete.Click += (_, _) =>
        {
            App.Instance.History.Remove(entry.Id);
            App.Instance.MainWindow?.RefreshHistoryList();
        };
        menu.Items.Add(open);
        menu.Items.Add(reanalyze);
        menu.Items.Add(reveal);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        grid.ContextMenu = menu;
        return grid;
    }

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is ListBoxItem { Tag: HistoryEntry entry })
        {
            LoadHistoryEntry(entry);
        }
    }

    public void LoadHistoryEntry(HistoryEntry entry)
    {
        var app = App.Instance;
        var result = app.History.LoadCached(entry.FilePath, app.Settings.Current.PitchAlgorithm, app.Settings.Current.VocalProfile);
        if (result == null)
        {
            _ = app.AnalyzeFileAsync(entry.FilePath, true);
            return;
        }
        ShowAnalysis(result);
        HistoryList.SelectedItem = null;
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        var app = App.Instance;
        if (app.History.Entries.Count == 0) return;
        var result = MessageBox.Show(this, "确定清空所有历史分析记录吗?", "清空历史",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            app.History.Clear();
            RefreshHistoryList();
        }
    }

    // ---------------- 文件分析展示 ----------------

    public void ShowAnalysis(AnalysisResult analysis)
    {
        _currentAnalysis = analysis;
        Waveform.EnterFile(analysis);
        FileBanner.Visibility = Visibility.Visible;
        string algo = string.IsNullOrEmpty(analysis.Algorithm) ? "" : $" · {analysis.Algorithm}";
        FileBannerText.Text =
            $"📄 {analysis.FileName} · {FormatDuration(analysis.Duration)}{algo} · 双击波形适配全览";
        BackToLiveBtn.Visibility = Visibility.Visible;
    }

    /// <summary>显示离线分析进度。</summary>
    public void ShowAnalyzing(string fileName, double progress)
    {
        _currentAnalysis = null;
        Waveform.EnterLive();
        FileBanner.Visibility = Visibility.Visible;
        FileBannerText.Text = $"⏳ 正在分析 {fileName}… {progress:P0}";
        BackToLiveBtn.Visibility = Visibility.Collapsed;
    }

    public void HideAnalyzingBanner()
    {
        if (_currentAnalysis == null)
        {
            FileBanner.Visibility = Visibility.Collapsed;
            BackToLiveBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void OnBackToLiveClick(object sender, RoutedEventArgs e)
    {
        _currentAnalysis = null;
        Waveform.EnterLive();
        FileBanner.Visibility = Visibility.Collapsed;
        BackToLiveBtn.Visibility = Visibility.Collapsed;
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
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".wav" or ".mp3" or ".m4a" or ".aac" or ".wma" or ".aiff" or ".aif"
            or ".flac" or ".ogg" or ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv"
            or ".webm" or ".m4v" or ".mpg" or ".mpeg")) return;
        await App.Instance.AnalyzeFileAsync(path);
    }

    private static bool HasAudioFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return false;
        string ext = Path.GetExtension(files[0]).ToLowerInvariant();
        return ext is ".wav" or ".mp3" or ".m4a" or ".aac" or ".wma" or ".aiff" or ".aif"
            or ".flac" or ".ogg" or ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv"
            or ".webm" or ".m4v" or ".mpg" or ".mpeg";
    }

    // ---------------- 设置面板 ----------------

    public void SyncSettingsControls()
    {
        _suppressSettingsEvents = true;
        try
        {
            var s = App.Instance.Settings.Current;

            ThemeLightRadio.IsChecked = s.Theme == "Light";
            ThemeDarkRadio.IsChecked = s.Theme == "Dark";
            ThemeSystemRadio.IsChecked = s.Theme == "System";

            BlurSlider.Value = s.BackgroundBlur;
            BlurValueText.Text = $"虚化程度:{s.BackgroundBlur:F0}";
            AutoAccentCheck.IsChecked = s.AutoAccent;
            RefreshAccentSwatch();

            BallModeNote.IsChecked = s.BallDisplayMode == "NoteOnly";
            BallModeFreq.IsChecked = s.BallDisplayMode == "NoteFreq";
            BallModeCents.IsChecked = s.BallDisplayMode == "NoteFreqCents";

            BallSizeSlider.Value = s.BallSize;
            BallSizeText.Text = $"小球大小:{s.BallSize:F0}";
            BallTopmostCheck.IsChecked = s.BallTopmost;

            A4Slider.Value = s.A4Frequency;
            A4ValueText.Text = $"A4 基准频率:{s.A4Frequency:F0} Hz";
            SmoothSlider.Value = s.Smoothing;
            SmoothValueText.Text = $"音高平滑度:{s.Smoothing}";

            PitchAlgoCombo.SelectedIndex = s.PitchAlgorithm switch
            {
                "Yin" => 1,
                "Rmvpe" => 2,
                _ => 0,
            };
            VocalProfileCombo.SelectedIndex = s.VocalProfile switch
            {
                "Clean" => 1,
                "Live" => 2,
                _ => 0,
            };
            BallColorModeCombo.SelectedIndex = s.BallColorMode switch
            {
                "Register" => 1,
                "VoiceRange" or "Groups3" or "Groups4" or "Groups5" => 2,
                _ => 0,
            };
            AskOnAnalyzeCheck.IsChecked = s.AskOnAnalyze;

            BuildPitchColorGrid();
        }
        finally
        {
            _suppressSettingsEvents = false;
        }
    }

    // ---------------- 左侧历史栏收起/展开 ----------------

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        var s = App.Instance.Settings.Current;
        s.SidebarCollapsed = !s.SidebarCollapsed;
        App.Instance.Settings.Save();
        ApplySidebarState();
    }

    private void ApplySidebarState()
    {
        bool collapsed = App.Instance.Settings.Current.SidebarCollapsed;
        SidebarPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarToggleBtn.Content = collapsed ? "❯" : "❮";
    }

    // ---------------- 新手引导 ----------------

    private static readonly (string Icon, string Title, string[] Lines)[] OnboardingPages =
    [
        ("🎤", "欢迎使用音高球 PitchBall",
         ["这是一款实时人声音高测量工具", "悬浮小球随时显示你当前唱的音名、频率与音分", "主界面可以看实时波形与音高曲线,也能离线分析音频文件"]),
        ("🔵", "悬浮小球怎么用",
         ["拖动小球:移动位置,边缘拖拽可缩放大小", "单击小球:显示 / 隐藏主界面", "右键小球:选择音频来源(麦克风 / 系统声音 / 正在播放的应用)", "把音频文件直接拖到小球上,即可离线分析"]),
        ("📊", "主界面功能",
         ["波形图:实时模式滚动显示;文件模式下鼠标悬停可查看所指位置的音高", "拖入音频文件进入文件模式:滚轮缩放、拖动平移、双击适配全览", "左侧历史栏默认收起,点击窗口左缘的按钮展开"]),
        ("⚙️", "设置与声区识别",
         ["右上角 ⚙ 打开设置面板", "人声场景:通用 / 纯净CD版 / 演唱会版(按你的素材选择)", "小球颜色模式:按音级 / 按声区(真声·混声·假声)/ 按音高分组", "检测时主界面会实时标注 真声 / 混声 / 假声"]),
    ];
    private int _onboardingPage;

    /// <summary>显示新手引导(首次启动由 App 调用;设置里可随时重新打开)。</summary>
    public void ShowOnboarding()
    {
        if (OnboardingPanel.Visibility == Visibility.Visible) return;
        _onboardingPage = 0;
        RenderOnboardingPage();
        OnboardingPanel.Visibility = Visibility.Visible;
    }

    private void RenderOnboardingPage()
    {
        var (icon, title, lines) = OnboardingPages[_onboardingPage];
        OnboardingTitle.Text = $"{icon}  {title}";

        OnboardingBody.Children.Clear();
        foreach (var line in lines)
        {
            OnboardingBody.Children.Add(new TextBlock
            {
                Text = "•  " + line,
                FontSize = 13.5,
                LineHeight = 26,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            });
        }

        OnboardingDots.Children.Clear();
        for (int i = 0; i < OnboardingPages.Length; i++)
        {
            OnboardingDots.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = i == _onboardingPage
                    ? (Brush)Application.Current.Resources["AccentBrush"]
                    : (Brush)Application.Current.Resources["TextSecondaryBrush"],
            });
        }

        OnboardingPrevBtn.Visibility = _onboardingPage > 0 ? Visibility.Visible : Visibility.Collapsed;
        OnboardingNextBtn.Content = _onboardingPage == OnboardingPages.Length - 1 ? "开始使用" : "下一步";

        // 联动真实界面演示
        var app = App.Instance;
        app.Ball?.SetGuideHighlight(false);
        GuideWaveHighlight.Visibility = Visibility.Collapsed;
        if (_onboardingPage == 1 && app.Ball != null)
        {
            // 小球页:把小球真实显示在主窗口右侧,并加虚线高亮圈
            app.Ball.ApplySettings();
            app.Ball.Left = Left + ActualWidth + 20;
            app.Ball.Top = Math.Max(SystemParameters.VirtualScreenTop, Top + 60);
            app.Ball.Show();
            app.Ball.SetGuideHighlight(true);
            OnboardingCard.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        }
        else if (_onboardingPage == 2)
        {
            // 主界面页:真实展开左侧历史栏,高亮波形图区
            SidebarPanel.Visibility = Visibility.Visible;
            SidebarToggleBtn.Content = "❮";
            GuideWaveHighlight.Visibility = Visibility.Visible;
            OnboardingCard.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        }
        else if (_onboardingPage == 3)
        {
            // 设置页:真实打开设置面板,引导卡片移到左侧让设置可见
            SetSettingsOpen(true);
            OnboardingCard.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        }
        else
        {
            OnboardingCard.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        }
    }

    private void OnOnboardingNextClick(object sender, RoutedEventArgs e)
    {
        if (_onboardingPage >= OnboardingPages.Length - 1)
        {
            CloseOnboarding();
        }
        else
        {
            _onboardingPage++;
            RenderOnboardingPage();
        }
    }

    private void OnOnboardingPrevClick(object sender, RoutedEventArgs e)
    {
        if (_onboardingPage > 0)
        {
            _onboardingPage--;
            RenderOnboardingPage();
        }
    }

    private void OnOnboardingSkipClick(object sender, RoutedEventArgs e) => CloseOnboarding();

    private void OnShowOnboardingClick(object sender, RoutedEventArgs e)
    {
        SetSettingsOpen(false);
        ShowOnboarding();
    }

    private void OnOnboardingCardClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnOnboardingMaskClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void CloseOnboarding()
    {
        OnboardingPanel.Visibility = Visibility.Collapsed;
        // 恢复演示状态
        App.Instance.Ball?.SetGuideHighlight(false);
        GuideWaveHighlight.Visibility = Visibility.Collapsed;
        ApplySidebarState();
        var s = App.Instance.Settings.Current;
        if (!s.OnboardingShown)
        {
            s.OnboardingShown = true; // 记忆:关闭后不再自动弹出
            App.Instance.Settings.Save();
        }
    }

    private void OnAskOnAnalyzeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        var s = App.Instance.Settings.Current;
        s.AskOnAnalyze = AskOnAnalyzeCheck.IsChecked == true;
        App.Instance.Settings.Save();
    }

    // ---------------- 小球颜色编辑(音级 / 声区 / 分组) ----------------

    private static readonly string[] DefaultRegisterColors = ["#FF8C42", "#34C77B", "#B44CFF"];
    private static readonly string[] DefaultGroupColors = ["#4F9DF3", "#2EC4B6", "#8BD450", "#FFC53D", "#FF8C42", "#F2555A"];
    // 声部划分(男/女低中高)及标注音域
    private static readonly string[] VoicePartLabels = ["男低音", "男中音", "男高音", "女低音", "女中音", "女高音"];
    private static readonly string[] VoicePartRanges = ["<C3", "C3–C4", "C4–C5", "C5–F5", "F5–A5", ">A5"];

    private void OnBallColorModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        if (BallColorModeCombo.SelectedIndex < 0) return;
        var s = App.Instance.Settings.Current;
        s.BallColorMode = BallColorModeCombo.SelectedIndex switch
        {
            1 => "Register",
            2 => "VoiceRange",
            _ => "PitchClass",
        };
        App.Instance.Settings.Save();
        BuildPitchColorGrid();
        App.Instance.Ball?.ApplySettings();
    }

    private void BuildPitchColorGrid()
    {
        var s = App.Instance.Settings.Current;
        // 防御:旧设置文件可能缺项,先补齐各数组长度
        if (s.RegisterColors.Length < 3) s.RegisterColors = [.. DefaultRegisterColors];
        if (s.GroupColors.Length < 6) s.GroupColors = [.. DefaultGroupColors];
        if (s.PitchClassColors.Length < 12) s.PitchClassColors = PitchColors.CreateDefaults();

        PitchColorGrid.Children.Clear();
        string mode = s.BallColorMode;
        if (mode == "Register")
        {
            PitchColorGrid.Columns = 6;
            string[] labels = ["真声", "混声", "假声"];
            for (int i = 0; i < 3; i++)
            {
                AddSwatch(s.RegisterColors, i, labels[i], DefaultRegisterColors[i]);
            }
        }
        else if (mode == "VoiceRange" || mode.StartsWith("Groups"))
        {
            // 按声部:男低/男中/男高 + 女低/女中/女高,色块下方标注划分音高
            PitchColorGrid.Columns = 3;
            for (int i = 0; i < 6; i++)
            {
                AddSwatch(s.GroupColors, i, $"{VoicePartLabels[i]} {VoicePartRanges[i]}",
                    DefaultGroupColors[i]);
            }
        }
        else
        {
            PitchColorGrid.Columns = 6;
            var defaults = PitchColors.CreateDefaults();
            for (int pc = 0; pc < 12; pc++)
            {
                AddSwatch(s.PitchClassColors, pc, NoteNames.GetPitchClassName(pc), defaults[pc]);
            }
        }
    }

    private void AddSwatch(string[] colors, int idx, string label, string fallbackHex)
    {
        var fallback = PitchColors.FromHex(fallbackHex, Colors.White);
        var color = idx < colors.Length ? PitchColors.FromHex(colors[idx], fallback) : fallback;
        var swatch = new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(1.5),
            Background = new SolidColorBrush(color),
            BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = $"{label}(点击修改颜色)",
        };
        swatch.MouseLeftButtonDown += (_, _) => EditColor(colors, idx, swatch);
        var cell = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        cell.Children.Add(swatch);
        cell.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9.5,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
        });
        PitchColorGrid.Children.Add(cell);
    }

    private void EditColor(string[] colors, int idx, Border swatch)
    {
        var current = PitchColors.FromHex(colors[idx], Colors.White);
        var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            colors[idx] = PitchColors.ToHex(
                System.Windows.Media.Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
            App.Instance.Settings.Save();
            swatch.Background = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
            App.Instance.Ball?.ApplySettings();
        }
    }

    private void OnResetPitchColorsClick(object sender, RoutedEventArgs e)
    {
        var s = App.Instance.Settings.Current;
        if (s.BallColorMode == "Register")
        {
            s.RegisterColors = [.. DefaultRegisterColors];
        }
        else if (s.BallColorMode == "VoiceRange" || s.BallColorMode.StartsWith("Groups"))
        {
            s.GroupColors = [.. DefaultGroupColors];
        }
        else
        {
            s.PitchClassColors = PitchColors.CreateDefaults();
        }
        App.Instance.Settings.Save();
        BuildPitchColorGrid();
        App.Instance.Ball?.ApplySettings();
    }

    private void OnThemeRadioChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        if (!ReferenceEquals(sender, ThemeLightRadio) && !ReferenceEquals(sender, ThemeDarkRadio)
            && !ReferenceEquals(sender, ThemeSystemRadio)) return;
        var s = App.Instance.Settings.Current;
        if (ThemeLightRadio.IsChecked == true) s.Theme = "Light";
        else if (ThemeDarkRadio.IsChecked == true) s.Theme = "Dark";
        else if (ThemeSystemRadio.IsChecked == true) s.Theme = "System";
        App.Instance.Settings.Save();
        App.Instance.Theme.Mode = s.Theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    private void OnPickBackgroundClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.webp|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            var s = App.Instance.Settings.Current;
        s.BackgroundImagePath = dlg.FileName;
        App.Instance.Settings.Save();
            ApplyBackground();
        }
    }

    private void OnClearBackgroundClick(object sender, RoutedEventArgs e)
    {
        var s = App.Instance.Settings.Current;
        s.BackgroundImagePath = null;
        App.Instance.Settings.Save();
        ApplyBackground();
        BgPathText.Text = "未设置";
    }

    public void ApplyBackground()
    {
        var s = App.Instance.Settings.Current;
        try
        {
            if (string.IsNullOrEmpty(s.BackgroundImagePath) || !File.Exists(s.BackgroundImagePath))
            {
                BgImage.Source = null;
                BgPathText.Text = "未设置";
                BgMask.Opacity = 0.80;
                if (s.AutoAccent) App.Instance.ApplyAccent(); // 无背景图时回到手动强调色
                RefreshAccentSwatch();
                return;
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(s.BackgroundImagePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            BgImage.Source = bmp;
            BgPathText.Text = Path.GetFileName(s.BackgroundImagePath);
            // 功能区已改为透明,遮罩调浅让背景图清晰可见(保留少量压暗保证文字可读)
            BgMask.Opacity = 0.35;
            // 强调色跟随背景:从背景图主色调派生强调色
            if (s.AutoAccent) ApplyAutoAccent(bmp);
            else App.Instance.ApplyAccent();
            RefreshAccentSwatch();
        }
        catch
        {
            BgImage.Source = null;
            BgPathText.Text = "图片加载失败";
        }
        BgBlur.Radius = Math.Clamp(s.BackgroundBlur, 0, 60);
    }

    private void OnBlurChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSettingsEvents) return;
        var s = App.Instance.Settings.Current;
        s.BackgroundBlur = e.NewValue;
        BlurValueText.Text = $"虚化程度:{e.NewValue:F0}";
        BgBlur.Radius = e.NewValue;
        App.Instance.Settings.Save();
    }

    private void OnAccentSwatchClick(object sender, MouseButtonEventArgs e)
    {
        var s = App.Instance.Settings.Current;
        var current = PitchColors.FromHex(s.AccentColor, System.Windows.Media.Color.FromRgb(0x6C, 0x93, 0xFF));
        var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            s.AccentColor = PitchColors.ToHex(
                System.Windows.Media.Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
            App.Instance.Settings.Save();
            AccentSwatch.Background = new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
            App.Instance.ApplyAccent();
        }
    }

    private void OnResetAccentClick(object sender, RoutedEventArgs e)
    {
        var s = App.Instance.Settings.Current;
        s.AccentColor = "#6C93FF";
        App.Instance.Settings.Save();
        RefreshAccentSwatch();
        App.Instance.ApplyAccent();
    }

    private void OnAutoAccentChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        var s = App.Instance.Settings.Current;
        s.AutoAccent = AutoAccentCheck.IsChecked == true;
        App.Instance.Settings.Save();
        if (s.AutoAccent && BgImage.Source is BitmapSource bmp)
        {
            ApplyAutoAccent(bmp);
        }
        else
        {
            App.Instance.ApplyAccent();
        }
        RefreshAccentSwatch();
    }

    /// <summary>刷新设置面板的强调色色块:跟随模式下显示自动派生值,否则显示手动颜色。</summary>
    private void RefreshAccentSwatch()
    {
        var s = App.Instance.Settings.Current;
        string eff = s.AutoAccent && !string.IsNullOrEmpty(s.AccentColorAuto)
            ? s.AccentColorAuto : s.AccentColor;
        AccentSwatch.Background = new SolidColorBrush(
            PitchColors.FromHex(eff, System.Windows.Media.Color.FromRgb(0x6C, 0x93, 0xFF)));
    }

    /// <summary>从背景图采样主色调,派生明亮鲜明的强调色并应用。</summary>
    private void ApplyAutoAccent(BitmapSource bmp)
    {
        var s = App.Instance.Settings.Current;
        var derived = DeriveAccentFromImage(bmp);
        s.AccentColorAuto = PitchColors.ToHex(derived);
        App.Instance.Settings.Save();
        App.Instance.ApplyAccent(PitchColors.ToHex(derived));
    }

    private static System.Windows.Media.Color DeriveAccentFromImage(BitmapSource bmp)
    {
        // 缩小到 96x96 采样,按饱和度加权平均得到主色调
        const int target = 96;
        double sx = bmp.PixelWidth / (double)target;
        double sy = bmp.PixelHeight / (double)target;
        var scaled = new TransformedBitmap(bmp, new ScaleTransform(1.0 / sx, 1.0 / sy));
        var fmt = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        int stride = fmt.PixelWidth * 4;
        var pixels = new byte[stride * fmt.PixelHeight];
        fmt.CopyPixels(pixels, stride, 0);

        double r = 0, g = 0, b = 0, wsum = 0;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            double br = pixels[i + 2], bg = pixels[i + 1], bb = pixels[i];
            double mx = Math.Max(br, Math.Max(bg, bb));
            double mn = Math.Min(br, Math.Min(bg, bb));
            double sat = mx > 1e-6 ? (mx - mn) / mx : 0;
            double weight = 0.12 + sat; // 有彩色的像素权重大,避免平均成灰色
            r += br * weight;
            g += bg * weight;
            b += bb * weight;
            wsum += weight;
        }
        if (wsum <= 0)
        {
            return System.Windows.Media.Color.FromRgb(0x6C, 0x93, 0xFF);
        }
        r /= wsum; g /= wsum; b /= wsum;

        // RGB → HSV,把饱和度与明度提到舒适的区间,保持色相
        double mx2 = Math.Max(r, Math.Max(g, b));
        double mn2 = Math.Min(r, Math.Min(g, b));
        double delta = mx2 - mn2;
        double hue = 0;
        if (delta > 1e-6)
        {
            if (mx2 == r) hue = 60 * (((g - b) / delta) % 6);
            else if (mx2 == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }
        if (hue < 0) hue += 360;
        double sat2 = Math.Clamp(mx2 > 1e-6 ? delta / mx2 : 0.65, 0.45, 0.75);
        double val2 = Math.Clamp(mx2 / 255.0, 0.55, 0.85);

        double c2 = val2 * sat2;
        double x2 = c2 * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        double m2 = val2 - c2;
        (double rr, double gg, double bb2) = ((int)(hue / 60)) switch
        {
            0 => (c2, x2, 0.0),
            1 => (x2, c2, 0.0),
            2 => (0.0, c2, x2),
            3 => (0.0, x2, c2),
            4 => (x2, 0.0, c2),
            _ => (c2, 0.0, x2),
        };
        return System.Windows.Media.Color.FromRgb(
            (byte)((rr + m2) * 255), (byte)((gg + m2) * 255), (byte)((bb2 + m2) * 255));
    }

    private void OnBallModeChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        var s = App.Instance.Settings.Current;
        if (BallModeNote.IsChecked == true) s.BallDisplayMode = "NoteOnly";
        else if (BallModeFreq.IsChecked == true) s.BallDisplayMode = "NoteFreq";
        else if (BallModeCents.IsChecked == true) s.BallDisplayMode = "NoteFreqCents";
        App.Instance.Settings.Save();
        App.Instance.Ball?.ApplySettings();
    }

    private void OnBallSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSettingsEvents) return;
        var s = App.Instance.Settings.Current;
        s.BallSize = e.NewValue;
        BallSizeText.Text = $"小球大小:{e.NewValue:F0}";
        App.Instance.Settings.Save();
        App.Instance.Ball?.ApplySettings();
    }

    private void OnBallTopmostChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        var s = App.Instance.Settings.Current;
        s.BallTopmost = BallTopmostCheck.IsChecked == true;
        App.Instance.Settings.Save();
        App.Instance.Ball?.ApplySettings();
    }

    private void OnA4Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSettingsEvents) return;
        var s = App.Instance.Settings.Current;
        s.A4Frequency = e.NewValue;
        A4ValueText.Text = $"A4 基准频率:{e.NewValue:F0} Hz";
        App.Instance.Settings.Save();
        App.Instance.Engine.A4Frequency = e.NewValue;
    }

    private void OnSmoothChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSettingsEvents) return;
        var s = App.Instance.Settings.Current;
        s.Smoothing = (int)e.NewValue;
        SmoothValueText.Text = $"音高平滑度:{s.Smoothing}";
        App.Instance.Settings.Save();
        App.Instance.Engine.Smoothing = s.Smoothing;
    }

    private void OnPitchAlgoChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        if (PitchAlgoCombo.SelectedIndex < 0) return;
        var app = App.Instance;
        var s = app.Settings.Current;
        s.PitchAlgorithm = PitchAlgoCombo.SelectedIndex switch
        {
            1 => "Yin",
            2 => "Rmvpe",
            _ => "Pyin",
        };
        app.Settings.Save();
        app.Engine.Algorithm = s.PitchAlgorithm;
        // 当前展示的文件分析用旧算法算的,切换后自动按新算法重新分析
        if (_currentAnalysis?.FilePath is { } path)
        {
            _ = app.AnalyzeFileAsync(path, true);
        }
    }

    private void OnVocalProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        if (VocalProfileCombo.SelectedIndex < 0) return;
        var app = App.Instance;
        var s = app.Settings.Current;
        s.VocalProfile = VocalProfileCombo.SelectedIndex switch
        {
            1 => "Clean",
            2 => "Live",
            _ => "Balanced",
        };
        app.Settings.Save();
        PitchBall.Audio.PyinPitchDetector.VocalProfile = App.ParseVocalProfile(s.VocalProfile);
        // 场景影响候选权重,切换后重新分析当前文件
        if (_currentAnalysis?.FilePath is { } path)
        {
            _ = app.AnalyzeFileAsync(path, true);
        }
    }

    // ---------------- 窗口状态 ----------------

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Normal)
        {
            var s = App.Instance.Settings.Current;
            s.MainW = Math.Max(Width, 880);
            s.MainH = Math.Max(Height, 560);
        }
    }
}
