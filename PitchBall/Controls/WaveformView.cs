using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PitchBall.Models;
using PitchBall.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace PitchBall.Controls;

/// <summary>
/// 波形与音高曲线自绘控件。
/// 实时模式:最近 12 秒滚动波形 + 音高曲线;
/// 文件模式:完整波形 + 音高曲线,滚轮缩放、拖动平移、双击适配。
/// 注:波形/音高曲线用 DrawLine 逐段绘制——本机 WPF 渲染环境对
/// StreamGeometry/PolyLineSegment/多段 LineSegment 的 DrawGeometry
/// 输出不稳定(部分或全部不渲染),DrawLine 实测始终正常。
/// </summary>
public class WaveformView : FrameworkElement
{
    private const double LiveWindowSeconds = 12.0;
    private const double LeftMargin = 46;
    private const double RightMargin = 10;
    private const double TopMargin = 10;
    private const double BottomMargin = 26;

    private readonly DispatcherTimer _timer;
    private readonly List<(DateTime Time, float Level, double Freq)> _live = [];

    private AnalysisResult? _analysis;
    private double _viewStart;
    private double _pps = 120;
    private bool _liveMode = true;

    private bool _dragging;
    private Point _dragStart;
    private double _dragStartView;
    private Point? _hoverPoint;

    public WaveformView()
    {
        ClipToBounds = true;
        Focusable = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => InvalidateVisual();
    }

    // ---------------- 数据入口 ----------------

    public void EnterLive()
    {
        _liveMode = true;
        _analysis = null;
        _live.Clear();
        _timer.Start();
        InvalidateVisual();
    }

    public void EnterFile(AnalysisResult analysis)
    {
        _liveMode = false;
        _analysis = analysis;
        _timer.Stop();
        FitAll();
        InvalidateVisual();
    }

    public void PushLiveSample(double freq, double level)
    {
        if (!_liveMode) return;
        // 防御:异常浮点值会破坏绘制几何,统一钳制
        if (!(freq > 0) || double.IsNaN(freq) || double.IsInfinity(freq)) freq = 0;
        if (double.IsNaN(level) || double.IsInfinity(level)) level = 0;
        if (level < 0) level = 0;
        if (level > 1) level = 1;
        var now = DateTime.Now;
        _live.Add((now, (float)level, freq));
        while (_live.Count > 0 && (now - _live[0].Time).TotalSeconds > LiveWindowSeconds)
        {
            _live.RemoveAt(0);
        }
    }

    public void FitAll()
    {
        if (_analysis == null) return;
        double plotW = Math.Max(50, ActualWidth - LeftMargin - RightMargin);
        _pps = plotW / Math.Max(1.0, _analysis.Duration);
        _viewStart = 0;
    }

    // ---------------- 交互 ----------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_liveMode || _analysis == null)
        {
            base.OnMouseWheel(e);
            return;
        }
        double plotW = Math.Max(50, ActualWidth - LeftMargin - RightMargin);
        double minPps = plotW / Math.Max(1.0, _analysis.Duration);
        double zoom = Math.Pow(1.25, e.Delta / 120.0);
        double newPps = Math.Clamp(_pps * zoom, minPps, 6000);

        // 以鼠标位置为中心缩放
        double plotX = e.GetPosition(this).X - LeftMargin;
        double tAtCursor = _viewStart + plotX / _pps;
        _viewStart = tAtCursor - plotX / newPps;
        _pps = newPps;
        ClampView();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_liveMode || _analysis == null) return;
        if (e.ClickCount == 2)
        {
            FitAll();
            InvalidateVisual();
            return;
        }
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragStartView = _viewStart;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            double dx = e.GetPosition(this).X - _dragStart.X;
            _viewStart = _dragStartView - dx / _pps;
            ClampView();
            InvalidateVisual();
            return;
        }
        // 悬停:文件模式下显示所指位置的音高
        _hoverPoint = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverPoint = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void ClampView()
    {
        if (_analysis == null) return;
        double plotW = Math.Max(50, ActualWidth - LeftMargin - RightMargin);
        double maxStart = Math.Max(0, _analysis.Duration - plotW / _pps);
        _viewStart = Math.Clamp(_viewStart, 0, maxStart);
    }

    // ---------------- 绘制 ----------------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var border = (Brush)Application.Current.Resources["BorderBrush"];
        var grid = (Brush)Application.Current.Resources["GridLineBrush"];
        var wave = (Brush)Application.Current.Resources["WaveBrush"];
        var pitch = (Brush)Application.Current.Resources["PitchBrush"];
        var textSec = (Brush)Application.Current.Resources["TextSecondaryBrush"];

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 波形区透明背景(让窗口背景透出),仅保留描边
        dc.DrawRoundedRectangle(null, new Pen(border, 1), new Rect(0.5, 0.5, w - 1, h - 1), 12, 12);

        var plot = new Rect(LeftMargin, TopMargin, w - LeftMargin - RightMargin, h - TopMargin - BottomMargin);
        if (plot.Width <= 0 || plot.Height <= 0) return;

        double midiMin, midiMax;
        if (_liveMode)
        {
            midiMin = 36; // C2
            midiMax = 96; // C7
        }
        else
        {
            int aMin = Math.Min(120, Math.Max(24, _analysis?.MinMidi ?? 40));
            int aMax = Math.Min(120, Math.Max(30, _analysis?.MaxMidi ?? 80));
            int pad = Math.Max(2, (aMax - aMin) / 8);
            midiMin = Math.Max(24, aMin - pad);
            midiMax = Math.Min(108, aMax + pad);
            if (midiMax - midiMin < 12) { midiMin = Math.Max(24, midiMin - 6); midiMax = Math.Min(108, midiMin + 24); }
        }

        DrawGrid(dc, plot, grid, textSec, dpi, midiMin, midiMax);

        // 裁剪绘制区
        dc.PushClip(new RectangleGeometry(plot));

        if (_liveMode)
        {
            DrawLive(dc, plot, wave, pitch, midiMin, midiMax);
        }
        else
        {
            DrawFile(dc, plot, wave, pitch, midiMin, midiMax);
        }

        dc.Pop();

        // 文件模式:信息与时间刻度
        if (!_liveMode && _analysis != null)
        {
            DrawFileOverlay(dc, plot, textSec, dpi);
            DrawHoverPitch(dc, plot, textSec, dpi);
        }
        else if (_live.Count == 0)
        {
            var hint = MakeText("正在聆听…实时波形将显示在这里", textSec, dpi, 14);
            dc.DrawText(hint, new Point(plot.X + (plot.Width - hint.Width) / 2,
                plot.Y + (plot.Height - hint.Height) / 2));
        }
    }

    /// <summary>悬停:在光标处画竖线,并在附近显示该时刻的音高信息。</summary>
    private void DrawHoverPitch(DrawingContext dc, Rect plot, Brush textSec, double dpi)
    {
        if (_analysis == null || _hoverPoint is not { } hp) return;
        if (hp.X < plot.X || hp.X > plot.Right || hp.Y < plot.Top || hp.Y > plot.Bottom) return;

        double t = _viewStart + (hp.X - plot.X) / _pps;
        if (t < 0 || t > _analysis.Duration) return;

        // 竖线
        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        dc.DrawLine(new Pen(accent, 1), new Point(hp.X, plot.Top), new Point(hp.X, plot.Bottom));

        // 所指帧的音高(取最近一帧;0 = 无声)
        int i = Math.Clamp((int)Math.Round(t * _analysis.PitchRate), 0, _analysis.PitchFreqs.Length - 1);
        double freq = _analysis.PitchFreqs[i];
        string note = freq > 0 ? NoteNames.GetNoteName(NoteNames.FrequencyToMidiNote(freq)) : "无声";
        string freqText = freq > 0 ? $" {NoteNames.FormatFrequency(freq)} Hz" : "";
        string line1 = $"{FormatTime(t)}  {note}{freqText}";
        var text = MakeText(line1, textSec, dpi, 12);

        // 提示框(跟随光标,出界时翻到另一侧)
        const double padX = 8, padY = 5;
        double boxW = text.Width + padX * 2;
        double boxH = text.Height + padY * 2;
        double bx = hp.X + 12;
        if (bx + boxW > ActualWidth - 4) bx = hp.X - 12 - boxW;
        double by = hp.Y - boxH / 2;
        by = Math.Clamp(by, plot.Top + 2, plot.Bottom - boxH - 2);

        var bg = (Brush)Application.Current.Resources["CardBgBrush"];
        var border = (Brush)Application.Current.Resources["BorderBrush"];
        var box = new Rect(bx, by, boxW, boxH);
        dc.DrawRoundedRectangle(bg, new Pen(border, 1), box, 6, 6);
        dc.DrawText(text, new Point(bx + padX, by + padY));
    }

    private void DrawGrid(DrawingContext dc, Rect plot, Brush grid, Brush textSec, double dpi,
        double midiMin, double midiMax)
    {
        var finePen = new Pen(grid, 0.6);
        var strongPen = new Pen(new SolidColorBrush(Color.FromArgb(110, 150, 150, 150)), 1);
        // 半音细线 + 八度(带 C)粗线
        for (int m = (int)Math.Ceiling(midiMin); m <= (int)midiMax; m++)
        {
            double y = MidiToY(m, plot, midiMin, midiMax);
            bool isC = m % 12 == 0;
            dc.DrawLine(isC ? strongPen : finePen, new Point(plot.X, y), new Point(plot.Right, y));
            if (isC)
            {
                var label = MakeText(NoteNames.GetNoteName(m), textSec, dpi, 10.5);
                dc.DrawText(label, new Point(plot.X - label.Width - 8, y - label.Height / 2));
            }
        }
    }

    private void DrawLive(DrawingContext dc, Rect plot, Brush wave, Brush pitch,
        double midiMin, double midiMax)
    {
        if (_live.Count < 2) return;

        double end = _live[^1].Time.TimeOfDay.TotalSeconds;
        double start = end - LiveWindowSeconds;

        // 波形带(下半区)。按窗口内峰值电平自适应归一化,
        // 低音量的回环信号(-25dBFS 以下)也能画出饱满可见的波形。
        double bandTop = plot.Y + plot.Height * 0.55;
        double bandH = plot.Height * 0.45;
        float peak = 0.02f;
        foreach (var (_, level, _) in _live) if (level > peak) peak = level;
        double gain = Math.Min(30.0, 0.95 / peak);

        // 波形:DrawLine 逐段(见类注释)
        var wpen = new Pen(wave, 1.4);
        Point? wPrev = null;
        foreach (var (time, level, _) in _live)
        {
            double t = time.TimeOfDay.TotalSeconds;
            double x = plot.X + (t - start) / LiveWindowSeconds * plot.Width;
            double y = bandTop + bandH / 2 - level * gain * bandH / 2;
            var pt = new Point(x, y);
            if (wPrev.HasValue) dc.DrawLine(wpen, wPrev.Value, pt);
            wPrev = pt;
        }

        // 音高曲线:DrawLine 逐段,静音/异常值处断开
        var ppen = new Pen(pitch, 2);
        Point? pPrev = null;
        foreach (var (time, _, freq) in _live)
        {
            if (!(freq > 0)) { pPrev = null; continue; }
            double t = time.TimeOfDay.TotalSeconds;
            double x = plot.X + (t - start) / LiveWindowSeconds * plot.Width;
            double midi = NoteNames.FrequencyToMidi(freq, 440);
            double y = MidiToY(midi, plot, midiMin, midiMax);
            var pt = new Point(x, y);
            if (pPrev.HasValue) dc.DrawLine(ppen, pPrev.Value, pt);
            pPrev = pt;
        }
    }

    private void DrawFile(DrawingContext dc, Rect plot, Brush wave, Brush pitch,
        double midiMin, double midiMax)
    {
        if (_analysis == null) return;

        double viewDur = plot.Width / _pps;

        // 波形(按屏幕密度抽稀后 DrawLine 逐段)
        var peaks = _analysis.WavePeaks;
        if (peaks.Length > 1)
        {
            int step = Math.Max(1, (int)Math.Ceiling(viewDur * Audio.FileAnalyzer.PeaksPerSecond / plot.Width));
            var pts = new List<Point>();
            for (int i = 0; i < peaks.Length; i += step)
            {
                double t = i / (double)Audio.FileAnalyzer.PeaksPerSecond;
                double x = plot.X + (t - _viewStart) * _pps;
                if (x < plot.X - 2 || x > plot.Right + 2) continue;
                double y = plot.Y + plot.Height / 2 - peaks[i] * plot.Height / 2;
                pts.Add(new Point(x, y));
            }
            var wpen = new Pen(wave, 1.2);
            for (int i = 1; i < pts.Count; i++) dc.DrawLine(wpen, pts[i - 1], pts[i]);
        }

        // 音高曲线(抽稀 + DrawLine,静音处断开)
        var freqs = _analysis.PitchFreqs;
        if (freqs.Length > 1)
        {
            int step = Math.Max(1, (int)Math.Ceiling(viewDur * _analysis.PitchRate / plot.Width));
            var ppen = new Pen(pitch, 1.8);
            Point? prev = null;
            for (int i = 0; i < freqs.Length; i += step)
            {
                if (!(freqs[i] > 0)) { prev = null; continue; }
                double t = i / _analysis.PitchRate;
                double x = plot.X + (t - _viewStart) * _pps;
                if (x < plot.X - 2 || x > plot.Right + 2) continue;
                double midi = NoteNames.FrequencyToMidi(freqs[i], 440);
                double y = MidiToY(midi, plot, midiMin, midiMax);
                var pt = new Point(x, y);
                if (prev.HasValue) dc.DrawLine(ppen, prev.Value, pt);
                prev = pt;
            }
        }
    }

    private void DrawFileOverlay(DrawingContext dc, Rect plot, Brush textSec, double dpi)
    {
        if (_analysis == null) return;

        // 音域与有声率
        string range = _analysis.MinMidi <= _analysis.MaxMidi
            ? $"音域 {NoteNames.GetNoteName(_analysis.MinMidi)} - {NoteNames.GetNoteName(_analysis.MaxMidi)}"
            : "未检测到音高";
        string info = $"{range} · 有声率 {_analysis.VoicedRatio:P0} · 双击适配全览";
        var infoText = MakeText(info, textSec, dpi, 11.5);
        dc.DrawText(infoText, new Point(plot.Right - infoText.Width, plot.Y));

        // 时间刻度
        var pen = new Pen(textSec, 1);
        double duration = _analysis.Duration;
        double minTick = 60 / _pps; // 最少 60px 一格
        double tick = 1;
        foreach (var step in new[] { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 })
        {
            if (step >= minTick) { tick = step; break; }
        }
        double rulerY = plot.Bottom + 4;
        for (double t = 0; t <= duration + 0.001; t += tick)
        {
            double x = plot.X + (t - _viewStart) * _pps;
            if (x < plot.X - 1 || x > plot.Right + 1) continue;
            dc.DrawLine(pen, new Point(x, rulerY), new Point(x, rulerY + 5));
            var label = MakeText(FormatTime(t), textSec, dpi, 10);
            dc.DrawText(label, new Point(x - label.Width / 2, rulerY + 7));
        }
    }

    private static string FormatTime(double seconds)
    {
        int total = (int)Math.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }

    private static double MidiToY(double midi, Rect plot, double midiMin, double midiMax)
    {
        double ratio = (midi - midiMin) / (midiMax - midiMin);
        return plot.Bottom - Math.Clamp(ratio, 0, 1) * plot.Height;
    }

    private static FormattedText MakeText(string text, Brush brush, double dpi, double size)
    {
        return new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Microsoft YaHei UI"),
            size, brush, dpi);
    }
}
