using System.Runtime.InteropServices;
using System.Text.Json;
using SolidWorksAgent.Core.Validation;
using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi;

public sealed partial class SwConnection
{
    private DocumentSession? _session;
    internal object? CreateDocument(string template,int paper,double width,double height)
    {
        var d=ComDispatch.Call(Application,"NewDocument",template,paper,width,height);
        if(d!=null)_session?.Track(d);
        return d;
    }
    internal object? OpenDocument(object?[] args)
    {
        var d=ComDispatch.CallWithByRef(Application,"OpenDoc6",args,4,5);
        if(d!=null)_session?.Track(d);
        return d;
    }
    public DocumentSession BeginDocumentSession(string workspace)
    {
        if(_session!=null)throw new InvalidOperationException("A document session is already active.");
        return _session=new DocumentSession(this,workspace);
    }
    public object OpenFinalDocument(string path)
    {
        path=Path.GetFullPath(path);if(!File.Exists(path))throw new FileNotFoundException("Final drawing not found.",path);
        int type=Path.GetExtension(path).ToUpperInvariant() switch {".SLDDRW"=>3,".SLDASM"=>2,".SLDPRT"=>1,_=>throw new ArgumentException("A CAD file is required.")};
        object?[] args=[path,type,1,"",0,0];var d=OpenDocument(args)??throw new IOException("Cannot open final document.");
        object?[] activate=[ComDispatch.Call(d,"GetTitle"),false,1,0];ComDispatch.CallWithByRef(Application,"ActivateDoc3",activate,3);
        return new{Path=path};
    }
    public object VerifyDocumentCleanup(string finalPath,string template,string workspace)
    {
        var baseline=SnapshotDocuments();
        var control=CreateDocument(template,0,0d,0d)??throw new Exception("Cannot create lifecycle control document.");
        long controlId=DocumentIdentity(control);string controlTitle=Convert.ToString(ComDispatch.Call(control,"GetTitle"))!;
        object success;
        try {
            ComDispatch.Call(control,"SetSaveFlag");
            using(var scope=BeginDocumentSession(workspace)) {
                var trial=CreateDocument(template,0,0d,0d)??throw new Exception("Cannot create trial document.");
                long trialId=DocumentIdentity(trial);ComDispatch.Call(trial,"SetSaveFlag");
                OpenFinalDocument(finalPath);success=scope.Complete([finalPath]);
                var remaining=SnapshotDocuments();
                if(remaining.Any(x=>x.Identity==trialId))throw new Exception("Unsaved intermediate was not closed.");
                if(!remaining.Any(x=>x.Identity==controlId&&x.Info.Modified))throw new Exception("Preexisting unsaved document was not protected.");
            }
            long failedId;
            using(var scope=BeginDocumentSession(workspace)) {
                var failed=CreateDocument(template,0,0d,0d)??throw new Exception("Cannot create failed trial document.");
                failedId=DocumentIdentity(failed);ComDispatch.Call(failed,"SetSaveFlag");
                // Dispose without Complete is the same cleanup path used when command execution throws.
            }
            if(SnapshotDocuments().Any(x=>x.Identity==failedId))throw new Exception("Failed trial was not closed.");
        }
        finally {CloseDocument(controlTitle);}
        var after=SnapshotDocuments();
        if(baseline.Where(x=>x.Info.Modified).Any(x=>!after.Any(y=>y.Identity==x.Identity)))throw new Exception("An existing edited document was closed.");
        return new{SuccessCleanup=success,UnsavedIntermediateClosed=true,FailureCleanupVerified=true,PreexistingUnsavedDocumentPreserved=true,Remaining=ListOpenDocuments()};
    }
    internal sealed record DocumentState(long Identity,OpenDocumentInfo Info);
    internal static long DocumentIdentity(object d)
    {
        var ptr=Marshal.GetIUnknownForObject(d);try{return ptr.ToInt64();}finally{Marshal.Release(ptr);}
    }
    internal DocumentState[] SnapshotDocuments()
    {
        var docs=ComDispatch.Call(Application,"GetDocuments") as Array;if(docs==null)return [];
        var result=new List<DocumentState>();
        foreach(var d in docs.Cast<object>()) {
            try {result.Add(new(DocumentIdentity(d),new(Convert.ToString(ComDispatch.Call(d,"GetTitle"))!,Convert.ToString(ComDispatch.Call(d,"GetPathName"))!,Convert.ToInt32(ComDispatch.Call(d,"GetType")),Convert.ToBoolean(ComDispatch.Call(d,"GetSaveFlag")),Convert.ToBoolean(ComDispatch.Get(d,"Visible")))));}
            finally {Marshal.ReleaseComObject(d);}
        }
        return result.ToArray();
    }
    public sealed class DocumentSession : IDisposable
    {
        private readonly SwConnection connection;
        private readonly Dictionary<long,DocumentState> before;
        private readonly HashSet<long> owned=[];
        private readonly List<IntPtr> pins=[];
        private readonly string ledger;
        private readonly HashSet<string> previous;
        private HashSet<string> final=new(StringComparer.OrdinalIgnoreCase);
        private bool succeeded,finished;
        internal DocumentSession(SwConnection connection,string workspace)
        {
            this.connection=connection;
            if(ComDispatch.Call(connection.Application,"GetDocuments") is Array initialDocs)foreach(var doc in initialDocs.Cast<object>()) {
                pins.Add(Marshal.GetIUnknownForObject(doc));Marshal.ReleaseComObject(doc);
            }
            before=connection.SnapshotDocuments().ToDictionary(x=>x.Identity);
            ledger=Path.Combine(Path.GetFullPath(workspace),".swagent-last-output.json");
            previous=new(StringComparer.OrdinalIgnoreCase);
            if(File.Exists(ledger))try {foreach(var path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(ledger))??[])previous.Add(Path.GetFullPath(path));}catch(JsonException){ }
        }
        internal void Track(object d){var pin=Marshal.GetIUnknownForObject(d);pins.Add(pin);long id=pin.ToInt64();if(!before.ContainsKey(id))owned.Add(id);}
        public object Complete(string[] finalPaths,bool saveGeneratedChanges=false)
        {
            final=new(finalPaths.Select(Path.GetFullPath),StringComparer.OrdinalIgnoreCase);
            foreach(var path in final)if(!File.Exists(path)||new FileInfo(path).Length==0)throw new IOException("Final output was not saved: "+path);
            if(saveGeneratedChanges&&ComDispatch.Call(connection.Application,"GetDocuments") is Array docs)foreach(var doc in docs.Cast<object>()) {
                try {
                    var path=Convert.ToString(ComDispatch.Call(doc,"GetPathName"))!;
                    if(final.Contains(path)&&owned.Contains(DocumentIdentity(doc))&&Convert.ToBoolean(ComDispatch.Call(doc,"GetSaveFlag"))) {
                        object?[] save=[1,0,0];
                        if(!Convert.ToBoolean(ComDispatch.CallWithByRef(doc,"Save3",save,1,2))||Convert.ToInt32(save[1])!=0||Convert.ToBoolean(ComDispatch.Call(doc,"GetSaveFlag")))throw new IOException("Final generated document could not be saved: "+path);
                    }
                } finally {Marshal.ReleaseComObject(doc);}
            }
            succeeded=true;return Cleanup();
        }
        private object Cleanup()
        {
            var closed=new List<string>();var errors=new List<string>();
            // Drawings/assemblies first so closing inputs can release their unused references.
            foreach(var d in connection.SnapshotDocuments().OrderByDescending(x=>x.Info.Type)) {
                bool existed=before.TryGetValue(d.Identity,out var initial);
                bool isFinal=d.Info.Path.Length>0&&final.Contains(Path.GetFullPath(d.Info.Path));
                bool wasGenerated=d.Info.Path.Length>0&&previous.Contains(Path.GetFullPath(d.Info.Path));
                if(!DocumentCleanupPolicy.ShouldClose(isFinal,owned.Contains(d.Identity),existed,wasGenerated,initial?.Info.Modified??false,d.Info.Modified,succeeded&&final.Count>0))continue;
                try {connection.CloseDocument(d.Info.Title);closed.Add(d.Info.Title);}catch(Exception e){errors.Add(d.Info.Title+": "+e.GetBaseException().Message);}
            }
            var remaining=connection.SnapshotDocuments();
            foreach(var title in closed)if(remaining.Any(x=>x.Info.Title==title&&x.Info.Visible))errors.Add("Document is still visible: "+title);
            foreach(var path in final) {
                var d=remaining.FirstOrDefault(x=>string.Equals(x.Info.Path,path,StringComparison.OrdinalIgnoreCase));
                if(d!=null){object?[] activate=[d.Info.Title,false,1,0];ComDispatch.CallWithByRef(connection.Application,"ActivateDoc3",activate,3);}
            }
            if(succeeded&&final.Count>0) {Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);File.WriteAllText(ledger,JsonSerializer.Serialize(final));}
            finished=true;connection._session=null;foreach(var pin in pins)Marshal.Release(pin);pins.Clear();
            foreach(var error in errors)Console.Error.WriteLine("Document cleanup: "+error);
            return new{Closed=closed,Remaining=remaining.Select(x=>x.Info).ToArray(),Errors=errors};
        }
        public void Dispose()
        {
            if(finished)return;
            try{Cleanup();}catch(Exception e){Console.Error.WriteLine("Document cleanup failed: "+e.GetBaseException().Message);}
            finally{connection._session=null;foreach(var pin in pins)Marshal.Release(pin);pins.Clear();}
        }
    }
}
