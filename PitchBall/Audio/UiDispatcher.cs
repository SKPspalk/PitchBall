using System;
using System.Windows;
using Application = System.Windows.Application;

namespace PitchBall.Audio;

/// <summary>
/// 把回调投递到 WPF UI 线程。AudioCaptureEngine 与 SourceManager 是 App 的字段,
/// 在 App 构造函数期间创建,彼时 SynchronizationContext.Current 尚为 null,
/// 因此不能依赖同步上下文,统一走 Application.Current.Dispatcher。
/// </summary>
internal static class UiDispatcher
{
    public static void Post(Action action)
    {
        var app = Application.Current;
        if (app == null || app.Dispatcher == null || app.Dispatcher.HasShutdownStarted)
        {
            action();
            return;
        }
        var dispatcher = app.Dispatcher;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
