using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi;

public sealed partial class SwConnection
{
    public object CheckChatConnection()
    {
        var addin = ComDispatch.Call(Application, "GetAddInObject", "{E2CF72C9-B695-48DF-968F-EAB0E85B9A24}")
            ?? throw new InvalidOperationException("The conversation add-in is not loaded.");
        try { return new { Started = Convert.ToBoolean(ComDispatch.Call(addin, "CheckConnection")) }; }
        finally { ComDispatch.Release(addin); }
    }
    public object LoadChatAddin(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !Path.GetFileName(path).Equals("SolidWorksAgent.ChatAddin.V3.comhost.dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Specify the built SolidWorksAgent.ChatAddin.V3.comhost.dll.");
        int status = Convert.ToInt32(ComDispatch.Call(Application, "LoadAddIn", path));
        if (status is not (0 or 2)) throw new InvalidOperationException($"SolidWorks LoadAddIn returned {status}. Check registration and the addin.log file.");
        var addin = ComDispatch.Call(Application, "GetAddInObject", "{E2CF72C9-B695-48DF-968F-EAB0E85B9A24}")
            ?? throw new InvalidOperationException("The add-in did not initialize. Check addin.log.");
        try
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>((string)ComDispatch.Call(addin, "GetStatus")!);
            if (!state.GetProperty("Connected").GetBoolean()) throw new InvalidOperationException("The chat pane is not connected.");
            return new { Loaded = true, AlreadyLoaded = status == 2, Path = path, Status = status, Pane = state };
        }
        finally { ComDispatch.Release(addin); }
    }

    public object UnloadChatAddin(string path)
    {
        path = Path.GetFullPath(path);
        if (!new[] { "SolidWorksAgent.ChatAddin.V3.comhost.dll", "SolidWorksAgent.ChatAddin.V2.comhost.dll", "SolidWorksAgent.ChatAddin.comhost.dll", "SolidWorksAgent.Addin.comhost.dll" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Specify the built SolidWorksAgent.ChatAddin.V3.comhost.dll.");
        return new { Unloaded = ComDispatch.Call(Application, "UnloadAddIn", path), Path = path };
    }
}
