using System.Diagnostics;
using System.Runtime.InteropServices;
using SolidWorks.Interop.swpublished;
using ISldWorks = SolidWorks.Interop.sldworks.ISldWorks;
using ITaskpaneView = SolidWorks.Interop.sldworks.ITaskpaneView;
using SolidWorksAgent.ChatUi;
using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi.Addin;

[ComVisible(true)]
[Guid("329431B7-EE93-4988-9079-7F0CE7464505")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IChatAddinAutomation
{
    [DispId(1)] bool ShowChat();
    [DispId(2)] string GetStatus();
    [DispId(3)] bool CheckConnection();
}

[ComVisible(true)]
[Guid("E2CF72C9-B695-48DF-968F-EAB0E85B9A28")]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IChatAddinAutomation))]
[ProgId("SolidWorksAgent.ChatAddin.V7")]
public sealed class ChatAddin : ISwAddin, IChatAddinAutomation
{
    private ITaskpaneView? _taskPane;
    private ChatPanel? _panel;

    public bool ConnectToSW(object ThisSW, int Cookie)
    {
        try
        {
            // SolidWorks owns this COM object. Do not FinalReleaseComObject on the borrowed application.
            var application = (ISldWorks)ThisSW;
            if (!application.SetAddinCallbackInfo2(0L, this, Cookie))
                throw new InvalidOperationException("Cannot register add-in callbacks.");
            _panel = new ChatPanel(Process.GetCurrentProcess().Id);
            _taskPane = CreateTaskPane(application)
                ?? throw new InvalidOperationException("Task pane creation failed.");
            if (!_taskPane.DisplayWindowFromHandlex64(_panel.Handle.ToInt64())) throw new InvalidOperationException("Cannot attach chat control to the task pane.");
            _panel.Show();
            ShowChat();
            Log("Connected");
            return true;
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            DisconnectFromSW();
            return false;
        }
    }

    private static ITaskpaneView? CreateTaskPane(ISldWorks application)
    {
        // Use the add-in location, not SOLIDWORKS's executable/working directory.
        var folder = Path.Combine(Path.GetDirectoryName(typeof(ChatAddin).Assembly.Location)!, "Assets");
        var icons = new[] { 20, 32, 40, 64, 96, 128 }
            .Select(size => Path.Combine(folder, $"taskpane-{size}.png")).ToArray();
        if (icons.All(File.Exists))
            return application.CreateTaskpaneView3(icons, "SolidWorks AI Chat");

        // A missing optional image must not prevent access to the chat.
        Log("Task pane icons missing; using the default icon.");
        return application.CreateTaskpaneView2("", "SolidWorks AI Chat");
    }

    public bool ShowChat() => _taskPane is not null && _taskPane.ShowView();
    public bool CheckConnection() => _panel?.BeginConnectionCheck() ?? false;

    public string GetStatus() => System.Text.Json.JsonSerializer.Serialize(new
    {
        Connected = _taskPane is not null && _panel is { IsDisposed: false, IsHandleCreated: true },
        ProcessId = Process.GetCurrentProcess().Id,
        Interface = "auto-review-v7",
        Chat = _panel?.Status,
        Width = _panel?.Width, Height = _panel?.Height
    });

    public bool DisconnectFromSW()
    {
        try
        {
            _panel?.Dispose(); _panel = null;
            _taskPane?.DeleteView();
            return true;
        }
        catch (Exception ex) { Log(ex.ToString()); return false; }
        finally { ComDispatch.Release(_taskPane); _taskPane = null; }
    }

    private static void Log(string text)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidWorksAgent");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "addin.log"), $"{DateTimeOffset.Now:O} {text}\n");
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
