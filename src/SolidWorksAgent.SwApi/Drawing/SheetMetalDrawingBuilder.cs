using System.Text.Json;
using SolidWorksAgent.SwApi.Interop;
namespace SolidWorksAgent.SwApi.Drawing;

public sealed partial class SheetMetalDrawingBuilder(SwConnection connection)
{
    static object[] Items(object? v) => v is Array a ? a.Cast<object?>().Where(x=>x!=null).Cast<object>().ToArray() : [];
    static double[] Num(object? v) => Items(v).Select(Convert.ToDouble).ToArray();
    static object? Try(Func<object?> f) { try { return f(); } catch(Exception e) { return "Unavailable: " + e.GetBaseException().Message; } }
    object Open(string path, int type) { object?[] a=[Path.GetFullPath(path),type,1,"",0,0]; return connection.OpenDocument(a) ?? throw new Exception($"Cannot open {path}: {a[4]}"); }
    static string Name(object v) => (string)ComDispatch.Get(v,"Name")!;
    public object Inspect(string path)
    {
        var m=Open(path,1);
        var fs=new List<object>();
        var visited=new HashSet<string>();
        void Feature(object f)
        {
            string name=Name(f), type=(string)ComDispatch.Call(f,"GetTypeName2")!;
            if(!visited.Add(name+"|"+type)) return;
            Console.Error.WriteLine($"Inspect {name} ({type})");
            var dims=new List<object>();
            var dimNames=new HashSet<string>();
            var dd=ComDispatch.Call(f,"GetFirstDisplayDimension");
            while(dd!=null) {
                var d=ComDispatch.Call(dd,"GetDimension2",0)!;
                if(!dimNames.Add((string)ComDispatch.Get(d,"FullName")!)) break;
                dims.Add(new {Name=ComDispatch.Get(d,"FullName"), Value=ComDispatch.Get(d,"SystemValue"), Type=Try(()=>ComDispatch.Get(d,"Type"))});
                dd=ComDispatch.Call(f,"GetNextDisplayDimension",dd);
            }
            object? definition=null;
            if(type is "SheetMetal" or "SM3dBend" or "HoleWzd") {
                var def=ComDispatch.Call(f,"GetDefinition")!;
                var props=type=="SheetMetal" ? new[]{"Thickness","BendRadius","KFactor"} : type=="SM3dBend" ? new[]{"BendAngle","BendRadius","BendPosition","UseDefaultBendRadius"} : new[]{"Type","HoleDiameter","HoleDepth","ThreadDiameter","ThreadDepth","ThreadClass","FastenerSize","Standard","EndCondition"};
                definition=props.ToDictionary(p=>p,p=>Try(()=>ComDispatch.Get(def,p)));
            }
            fs.Add(new {Name=name,Type=type,Suppressed=Try(()=>ComDispatch.Call(f,"IsSuppressed")),Dimensions=dims,Definition=definition});
            var sub=ComDispatch.Call(f,"GetFirstSubFeature");
            var siblings=new HashSet<string>();
            while(sub!=null && siblings.Add(Name(sub))) {Feature(sub);sub=ComDispatch.Call(sub,"GetNextSubFeature");}
        }
        var f=ComDispatch.Call(m,"FirstFeature"); while(f!=null) {Feature(f); f=ComDispatch.Call(f,"GetNextFeature");}
        var cfg=ComDispatch.Get(ComDispatch.Get(m,"ConfigurationManager")!,"ActiveConfiguration")!;
        object?[] mat=[Name(cfg),""];
        var material=ComDispatch.CallWithByRef(m,"GetMaterialPropertyName2",mat,1);
        return new {Path=path,Material=material,MaterialDatabase=mat[1],Config=Name(cfg),Configurations=ComDispatch.Call(m,"GetConfigurationNames"), Bodies=Items(ComDispatch.Call(m,"GetBodies2",0,false)).Select(b=>new {Name=Name(b), SheetMetal=Try(()=>ComDispatch.Call(b,"IsSheetMetal")),Box=Num(ComDispatch.Call(b,"GetBodyBox")).Select(x=>x*1000).ToArray()}).ToArray(),Features=fs};
    }
}

// Drawing construction and dimension plans are kept in the COM boundary assembly.
