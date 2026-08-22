using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PitchBall.Services;

/// <summary>系统托盘图标与菜单。</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notify;

    public TrayIconService(Action openMain, Action enterSimple, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => openMain());
        menu.Items.Add("简约模式(小球)", null, (_, _) => enterSimple());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _notify = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "音高球 PitchBall",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notify.DoubleClick += (_, _) => openMain();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(255, 79, 125, 243));
            g.FillEllipse(fill, 2, 2, 28, 28);
            using var pen = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 20, 8, 20, 21);
            g.DrawLine(pen, 8, 12, 20, 10);
            g.DrawEllipse(pen, 13.5f, 19, 8.5f, 6.5f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
    }
}
