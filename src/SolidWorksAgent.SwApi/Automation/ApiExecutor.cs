using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using SolidWorksAgent.Core.Automation;
using SolidWorksAgent.Core.Configuration;
using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi.Automation;

public sealed class ApiExecutor(SwConnection connection)
{
    public object Execute(ApiPlan plan, AgentOptions options, string output, bool overwrite)
    {
        var catalog = new ApiCatalog();
        var methods = plan.Steps.Select(catalog.Method).ToArray(); // Check every signature before any CAD mutation.
        using var objects = new Objects();
        object? source = null;
        int type = plan.Document switch { "new-part" => 1, "new-assembly" => 2, "new-drawing" => 3, _ => 0 };
        if (type == 0)
        {
            source = objects.Own(ComDispatch.Get(connection.Application,"ActiveDoc"));
            type = Convert.ToInt32(ComDispatch.Call(source,"GetType"));
            if (type != 1) throw new ArgumentException("既存文書の汎用API編集は現在、部品のコピーを対象にしています。新規アセンブリ・図面は作成できます。");
        }
        string extension = type == 1 ? ".SLDPRT" : type == 2 ? ".SLDASM" : ".SLDDRW";
        output = Path.GetFullPath(output);
        if (!output.EndsWith(extension,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("保存先の拡張子は " + extension + " です。");
        if (File.Exists(output) && !overwrite) throw new IOException("既存CADの上書きには --overwrite が必要です。");
        if (connection.ListOpenDocuments().Any(d => string.Equals(d.Path,output,StringComparison.OrdinalIgnoreCase)))
            throw new IOException("開いている文書と異なる保存先を指定してください。");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        object model; string? sourcePath = null; bool? sourceDirty = null;
        if (source is not null)
        {
            sourcePath = Convert.ToString(ComDispatch.Call(source,"GetPathName")); sourceDirty = Convert.ToBoolean(ComDispatch.Call(source,"GetSaveFlag"));
            // The copy is a persistent recovery artifact; an unsuccessful trial is closed by DocumentSession.
            var copy = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_work_" + Guid.NewGuid().ToString("N")[..8] + extension);
            if (Convert.ToInt32(ComDispatch.Call(source,"SaveAs3",copy,0,3)) != 0 || !File.Exists(copy)) throw new IOException("元部品の作業コピーを作成できません。");
            model = objects.Own(connection.OpenDocument([copy,type,1,"",0,0]));
        }
        else
        {
            var template = type == 1 ? options.PartTemplate : type == 2 ? options.AssemblyTemplate : options.DrawingTemplate;
            if (template is null || !File.Exists(template)) throw new FileNotFoundException("対応するSOLIDWORKSテンプレートがありません。");
            model = objects.Own(connection.CreateDocument(template,0,0d,0d));
        }
        var values = new Dictionary<string,object?> { ["model"]=model,
            ["sketch"]=objects.Own(ComDispatch.Get(model,"SketchManager")), ["features"]=objects.Own(ComDispatch.Get(model,"FeatureManager")),
            ["extension"]=objects.Own(ComDispatch.Get(model,"Extension")), ["math"]=objects.Own(ComDispatch.Call(connection.Application,"GetMathUtility")) };
        values["plane"] = FirstPlane(model,objects);
        var results = new List<object>();
        object? Reference(string name)
        {
            var id = name.Split('[')[0]; var value = values[id];
            foreach(Match index in Regex.Matches(name,@"\[(\d+)\]"))
                value = value is Array a ? a.GetValue(int.Parse(index.Groups[1].Value,CultureInfo.InvariantCulture)) : throw new ArgumentException("配列ではありません："+name);
            return value;
        }
        object? ConvertValue(JsonElement v, Type? expected = null)
        {
            object? value = v.ValueKind switch
            {
                JsonValueKind.Null => null, JsonValueKind.True => true, JsonValueKind.False => false,
                JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.TryGetInt32(out var n) ? n : v.GetDouble(),
                JsonValueKind.Array => v.EnumerateArray().Select(x=>ConvertValue(x)).ToArray(),
                JsonValueKind.Object when v.TryGetProperty("ref",out var r) => Reference(r.GetString()!),
                JsonValueKind.Object when v.TryGetProperty("mm",out var mm) => mm.GetDouble()/1000,
                JsonValueKind.Object when v.TryGetProperty("deg",out var deg) => deg.GetDouble()*Math.PI/180,
                JsonValueKind.Object when v.TryGetProperty("enum",out var e) => catalog.EnumValue(e.GetString()!),
                _ => throw new ArgumentException("API引数が不正です。")
            };
            if (expected?.IsByRef == true) expected=expected.GetElementType();
            if (expected is not null && expected != typeof(object) && value is not null && !expected.IsInstanceOfType(value))
                value=Convert.ChangeType(value,expected,CultureInfo.InvariantCulture);
            if (value is object[] array && array.Length>0 && array.All(x=>x is double or int)) value=array.Select(Convert.ToDouble).ToArray();
            return value;
        }
        for(int i=0;i<plan.Steps.Length;i++)
        {
            var step=plan.Steps[i]; var method=methods[i];
            try
            {
                var target=Reference(step.Target) ?? throw new InvalidOperationException("対象オブジェクトがnullです。");
                if (!catalog.Type(step.Interface).IsInstanceOfType(target)) throw new ArgumentException("対象のインターフェースが異なります。");
                var parameters=method.GetParameters(); var args=step.Args.Select((v,j)=>ConvertValue(v,parameters[j].ParameterType)).ToArray();
                var result=method.Invoke(target,args);
                Track(result); foreach(var a in args) Track(a);
                values.Add(step.Id,result); values.Add(step.Id+"_out",args);
                if (step.Expect=="true" && result is not true || step.Expect=="not-null" && result is null
                    || step.Expect=="success" && (result is false || method.ReturnType != typeof(void) && result is null))
                    throw new InvalidOperationException("SOLIDWORKSが失敗またはnullを返しました。");
                if (step.Equal is JsonElement expected)
                {
                    var e=ConvertValue(expected);
                    bool same=result is IConvertible && e is IConvertible && result is not string && e is not string
                        ? Math.Abs(Convert.ToDouble(result)-Convert.ToDouble(e)) < 1e-8 : Equals(result,e);
                    if(!same) throw new InvalidOperationException($"期待値に一致しません。actual={result}, expected={e}");
                }
                results.Add(new {step.Id,step.Member,Result=Describe(result)});
                if (step.Kind == "call" && step.Interface == "IFeatureManager" && result is not null && catalog.Type("IFeature").IsInstanceOfType(result))
                {
                    Rebuild(model);
                    object?[] errorArgs=[false]; int error=Convert.ToInt32(ComDispatch.CallWithByRef(result,"GetErrorCode2",errorArgs,0));
                    if(error!=0 && !Convert.ToBoolean(errorArgs[0])) throw new InvalidOperationException("フィーチャーエラー："+error);
                    if(type==1 && ComDispatch.Call(model,"GetBodies2",-1,false) is Array bodies && bodies.Length>0)
                    { foreach(var body in bodies) objects.Own(body!); Validate(model,type,objects); }
                }
            }
            catch(Exception ex) { throw new InvalidOperationException($"APIステップ {i+1} ({step.Id}: {step.Interface}.{step.Member}) に失敗しました。元文書は保存していません。{ex.GetBaseException().Message}",ex); }
        }
        Rebuild(model);
        var geometry=Validate(model,type,objects);
        if(plan.SizeMm is not null)
        {
            if(type!=1)throw new ArgumentException("寸法検証は部品に指定してください。");
            var box=((Array)ComDispatch.Call(model,"GetPartBox",true)!).Cast<object>().Select(Convert.ToDouble).ToArray();
            var actual=Enumerable.Range(0,3).Select(i=>(box[i+3]-box[i])*1000).Order().ToArray();
            if(actual.Zip(plan.SizeMm.Order()).Any(pair=>Math.Abs(pair.First-pair.Second)>.05)) throw new InvalidOperationException("作成した部品の寸法が指定値と一致しません。");
        }
        if (source is not null && (Convert.ToString(ComDispatch.Call(source,"GetPathName")) != sourcePath || Convert.ToBoolean(ComDispatch.Call(source,"GetSaveFlag")) != sourceDirty))
            throw new InvalidOperationException("元文書の保存状態が変わったため停止しました。");
        if (File.Exists(output)&&!overwrite) throw new IOException("保存先が作成済みです。別名を指定してください。");
        int save=Convert.ToInt32(ComDispatch.Call(model,"SaveAs3",output,0,1));
        if(save!=0 || !File.Exists(output) || !string.Equals(Convert.ToString(ComDispatch.Call(model,"GetPathName")),output,StringComparison.OrdinalIgnoreCase))
            throw new IOException($"API操作結果の保存に失敗しました：{save}");
        ComDispatch.Call(model,"ViewZoomtofit2");
        return new {Path=output,plan.Summary,Steps=results,Geometry=geometry,RebuildSucceeded=true,SourceDocumentPreserved=true};

        void Track(object? v)
        {
            if(v is Array a) { foreach(var item in a) Track(item); return; }
            if(v is null || !Marshal.IsComObject(v)) return;
            objects.Own(v);
            if(catalog.Type("IModelDoc2").IsInstanceOfType(v) && SwConnection.DocumentIdentity(v)!=SwConnection.DocumentIdentity(model))
                throw new InvalidOperationException("作業対象外の文書へのAPIアクセスは許可されていません。");
        }
    }
    private static object? Describe(object? v) => v is null ? null : Marshal.IsComObject(v) ? "SOLIDWORKS object" : v is Array a ? new { Count=a.Length, Values=a.Cast<object?>().Take(64).Select(Describe).ToArray() } : v;
    private static object? FirstPlane(object model,Objects objects)
    {
        var f=ComDispatch.Call(model,"FirstFeature");
        while(f is not null) { objects.Own(f); if(Convert.ToString(ComDispatch.Call(f,"GetTypeName2"))=="RefPlane")return f; f=ComDispatch.Call(f,"GetNextFeature"); }
        return null;
    }
    internal static void Rebuild(object model)
    {
        if(!Convert.ToBoolean(ComDispatch.Call(model,"EditRebuild3"))) throw new InvalidOperationException("再構築に失敗しました。");
    }
    private static object Validate(object model,int type,Objects objects)
    {
        if(type==1)
        {
            var bodies=ComDispatch.Call(model,"GetBodies2",-1,false) as Array;
            if(bodies is null || bodies.Length==0) throw new InvalidOperationException("結果にソリッドまたはサーフェスボディがありません。");
            foreach(var b in bodies) { objects.Own(b!); var status=Convert.ToInt32(ComDispatch.Call(b!,"Check")); if(status!=1) throw new InvalidOperationException("ボディの幾何検証に失敗しました。status="+status+", bounds="+JsonSerializer.Serialize(ComDispatch.Call(model,"GetPartBox",true))); }
            var box=(Array)ComDispatch.Call(model,"GetPartBox",true)!;
            var values=box.Cast<object>().Select(Convert.ToDouble).ToArray();
            if(values.Length!=6||values.Any(v=>!double.IsFinite(v))) throw new InvalidOperationException("結果の外形が不正です。");
            return new {BodyCount=bodies.Length,BoundsMm=values.Select(v=>v*1000).ToArray()};
        }
        if(type==2) return new {ComponentCount=Convert.ToInt32(ComDispatch.Call(model,"GetComponentCount",false))};
        return new {SheetCount=((Array)ComDispatch.Call(model,"GetSheetNames")!).Length};
    }
    private sealed class Objects:IDisposable
    {
        private readonly List<object> objects=[];
        public object Own(object? o) { if(o is null)throw new InvalidOperationException("SOLIDWORKSオブジェクトがありません。"); if(Marshal.IsComObject(o))objects.Add(o); return o; }
        public void Dispose() { foreach(var o in objects.AsEnumerable().Reverse())try {Marshal.ReleaseComObject(o);}catch(InvalidComObjectException){} }
    }
}
