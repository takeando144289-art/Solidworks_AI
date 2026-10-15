using System.Runtime.InteropServices;
using System.Diagnostics;
using SolidWorksAgent.Core.Logging;
using SolidWorksAgent.Core.Models;
using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi;

public sealed partial class SwConnection : IDisposable
{
    private readonly IAgentLog _log;
    private object? _application;

    public SwConnection(IAgentLog? log = null) => _log = log ?? new NullAgentLog();

    public bool IsConnected => _application is not null;
    internal object Application => _application ?? throw new InvalidOperationException("Not connected to SolidWorks.");

    public void ConnectToRunningSolidWorks(int? processId = null)
    {
        if (IsConnected) return;
        if (processId is int target)
        {
            if (target <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
            _application = ComDispatch.GetRunningApplication(target);
            if (Convert.ToInt32(ComDispatch.Call(_application, "GetProcessID")) != target)
            {
                ComDispatch.Release(_application); _application = null;
                throw new InvalidOperationException("The connected SolidWorks process does not match the requested PID.");
            }
            _log.Write("CAD", $"Connected to requested SolidWorks PID {target}.");
            return;
        }
        try
        {
            _application = ComDispatch.GetRunningApplication();
            _log.Write("CAD", "Connected to running SolidWorks instance.");
        }
        catch (COMException ex)
        {
            // Multiple SOLIDWORKS Connected processes can coexist while one is
            // waiting for authentication. Select an initialized window by the
            // PID-specific moniker that SolidWorks publishes in the ROT.
            var initialized = Process.GetProcessesByName("SLDWORKS")
                .Where(process => process.MainWindowHandle != IntPtr.Zero)
                .OrderByDescending(process => process.StartTime)
                .ToArray();
            foreach (var process in initialized)
            {
                try
                {
                    _application = ComDispatch.GetRunningApplication(process.Id);
                    _log.Write("CAD", $"Connected to initialized SolidWorks PID {process.Id} through its ROT moniker.");
                    return;
                }
                catch (COMException) { }
            }
            if (Process.GetProcessesByName("SLDWORKS").Length > 0)
            {
                var monikers = ComDispatch.GetRunningSolidWorksMonikers();
                var detail = monikers.Count == 0 ? "No SolidWorks ROT monikers are visible." : $"Visible monikers: {string.Join(", ", monikers)}";
                throw new InvalidOperationException($"A SolidWorks process exists, but no initialized window is publishing an authenticated COM application. {detail}", ex);
            }
            throw new InvalidOperationException("No authenticated SolidWorks instance is available. Start SolidWorks manually, complete authentication, and try again.", ex);
        }
    }

    public void StartSolidWorks(bool visible = true)
    {
        if (IsConnected) return;
        _application = ComDispatch.CreateApplication();
        ComDispatch.Set(_application, "Visible", visible);
        _log.Write("CAD", $"Started SolidWorks (visible={visible}).");
    }

    public DocumentInfo GetActiveDocument()
    {
        var document = ComDispatch.Get(Application, "ActiveDoc");
        if (document is null) return new DocumentInfo(true, null, null, CadDocumentType.None);
        try
        {
            var title = ComDispatch.Call(document, "GetTitle") as string;
            var path = ComDispatch.Call(document, "GetPathName") as string;
            var rawType = Convert.ToInt32(ComDispatch.Call(document, "GetType"));
            return new DocumentInfo(true, title, string.IsNullOrWhiteSpace(path) ? null : path, ToDocumentType(rawType));
        }
        finally { ComDispatch.Release(document); }
    }

    public void CloseDocument(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        string token=title;int matches=0;
        if(ComDispatch.Call(Application,"GetDocuments") is Array docs)foreach(var doc in docs.Cast<object>()) {
            try {
                string current=Convert.ToString(ComDispatch.Call(doc,"GetTitle"))!;
                string path=Convert.ToString(ComDispatch.Call(doc,"GetPathName"))!;
                string baseTitle=current;
                if(Convert.ToInt32(ComDispatch.Call(doc,"GetType"))==3) {
                    var sheet=ComDispatch.Call(doc,"GetCurrentSheet");
                    string suffix=" - "+ComDispatch.Call(sheet!,"GetName");
                    if(baseTitle.EndsWith(suffix,StringComparison.Ordinal))baseTitle=baseTitle[..^suffix.Length];
                }
                if(string.Equals(title,current,StringComparison.OrdinalIgnoreCase)||string.Equals(title,baseTitle,StringComparison.OrdinalIgnoreCase)||string.Equals(title,path,StringComparison.OrdinalIgnoreCase)){token=current;matches++;}
            }
            finally {Marshal.ReleaseComObject(doc);}
        }
        if(matches!=1)throw new InvalidOperationException("Expected exactly one matching open document: "+title);
        ComDispatch.Call(Application,"CloseDoc",token);
        if(ListOpenDocuments().Any(d=>d.Title==token&&d.Visible))throw new InvalidOperationException("SolidWorks did not close document: "+token);
        _log.Write("CAD", $"Closed document: {title}");
    }

    public sealed record OpenDocumentInfo(string Title,string Path,int Type,bool Modified,bool Visible);
    public OpenDocumentInfo[] ListOpenDocuments()
    {
        var docs=ComDispatch.Call(Application,"GetDocuments") as Array;
        if(docs==null)return [];
        var result=new List<OpenDocumentInfo>();
        foreach(var document in docs.Cast<object>()) {
            try {result.Add(new(Convert.ToString(ComDispatch.Call(document,"GetTitle"))!,Convert.ToString(ComDispatch.Call(document,"GetPathName"))!,Convert.ToInt32(ComDispatch.Call(document,"GetType")),Convert.ToBoolean(ComDispatch.Call(document,"GetSaveFlag")),Convert.ToBoolean(ComDispatch.Get(document,"Visible"))));}
            finally {Marshal.ReleaseComObject(document);}
        }
        return result.ToArray();
    }

    public void SetIsometricView()
    {
        var document = ComDispatch.Get(Application, "ActiveDoc")
            ?? throw new InvalidOperationException("There is no active SolidWorks document.");
        try
        {
            ComDispatch.Call(document, "ShowNamedView2", "*Isometric", 7);
            ComDispatch.Call(document, "ViewZoomtofit2");
            _log.Write("CAD", "Set isometric view and zoomed to fit.");
        }
        finally { ComDispatch.Release(document); }
    }

    public string SaveActiveViewBitmap(string path, int width = 1600, int height = 1000)
    {
        path = Path.GetFullPath(path);
        if (!path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Bitmap path must use .bmp.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var document = ComDispatch.Get(Application, "ActiveDoc")
            ?? throw new InvalidOperationException("There is no active SolidWorks document.");
        try
        {
            if (!Convert.ToBoolean(ComDispatch.Call(document, "SaveBMP", path, width, height)) || !File.Exists(path))
                throw new IOException($"SolidWorks did not save the active view bitmap: {path}");
            return path;
        }
        finally { ComDispatch.Release(document); }
    }

    private static CadDocumentType ToDocumentType(int type) => type switch
    {
        1 => CadDocumentType.Part,
        2 => CadDocumentType.Assembly,
        3 => CadDocumentType.Drawing,
        0 => CadDocumentType.None,
        _ => CadDocumentType.Unknown
    };

    public void Dispose()
    {
        ComDispatch.Release(_application);
        _application = null;
        GC.SuppressFinalize(this);
    }
}
