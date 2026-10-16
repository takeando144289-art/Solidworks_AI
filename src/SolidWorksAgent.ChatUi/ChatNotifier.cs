using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SolidWorksAgent.ChatUi;

public enum ChatNotification { Completed, ReplyRequired, Failed }
public interface IChatNotifier : IDisposable
{
    void Notify(ChatNotification notification, string provider);
}

/// <summary>Non-modal Windows notifications; never interrupt CAD with a message box.</summary>
public sealed class ChatNotifier(int processId, Action focusChat) : IChatNotifier
{
    private NotifyIcon? _icon;
    private bool _disposed;
    public void Notify(ChatNotification notification, string provider)
    {
        if (_disposed) return;
        if (_icon is null)
        {
            _icon = new NotifyIcon { Icon = SystemIcons.Information, Text = "SolidWorks AI Chat", Visible = true };
            _icon.BalloonTipClicked += (_, _) => ActivateChat();
            _icon.DoubleClick += (_, _) => ActivateChat();
        }
        var (title, message) = notification switch
        {
            ChatNotification.ReplyRequired => ("返答が必要です", $"{provider}から確認があります。SOLIDWORKSのチャットで返信してください。"),
            ChatNotification.Failed => ("AIとの処理が中断しました", "SOLIDWORKSのチャットでエラーの内容を確認してください。"),
            _ => ("AIの応答が完了しました", $"{provider}の応答をSOLIDWORKSのチャットで確認できます。")
        };
        // Do not expose CAD names or message contents on the desktop/lock screen.
        _icon.ShowBalloonTip(10000, "SolidWorks AI Chat — " + title, message,
            notification == ChatNotification.Failed ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }
    private void ActivateChat()
    {
        if (_disposed) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            var window = process.MainWindowHandle;
            if (window != 0)
            {
                if (IsIconic(window)) ShowWindowAsync(window, 9); // SW_RESTORE
                SetForegroundWindow(window);
            }
            focusChat();
        }
        catch (Exception ex) { Trace.WriteLine("Chat notification activation: " + ex.Message); }
    }
    public void Dispose()
    {
        _disposed = true;
        if (_icon is not null) { _icon.Visible = false; _icon.Dispose(); _icon = null; }
    }
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
}
