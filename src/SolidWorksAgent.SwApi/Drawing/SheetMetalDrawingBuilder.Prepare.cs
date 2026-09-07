using System.Text.Json;
using System.Runtime.InteropServices;
using SolidWorksAgent.SwApi.Interop;
namespace SolidWorksAgent.SwApi.Drawing;

public sealed partial class SheetMetalDrawingBuilder
{
    static void Rebuild(object d) {if(!Convert.ToBoolean(ComDispatch.Call(d,"EditRebuild3"))) throw new Exception("Rebuild failed.");}
    static string Output(string p) {p=Path.GetFullPath(p); if(File.Exists(p)) throw new IOException($"Output already exists: {p}"); Directory.CreateDirectory(Path.GetDirectoryName(p)!);return p;}
    static void SaveNew(object d,string path) {ComDispatch.Call(d,"ClearSelection2",true);ComDispatch.Call(d,"SaveAs3",path,0,1); if(!File.Exists(path)||new FileInfo(path).Length==0)throw new IOException("Save failed: "+path);}
    static void Note(object d,string text,double x,double y,double height=3.5)
    {
        ComDispatch.Call(d,"ClearSelection2",true); ComDispatch.Call(d,"ActivateView","");
        var n=ComDispatch.Call(d,"InsertNote",text)!;var a=ComDispatch.Call(n,"GetAnnotation")!;
        var fmt=ComDispatch.Call(a,"GetTextFormat",0)!; ComDispatch.Set(fmt,"TypeFaceName","Arial");ComDispatch.Set(fmt,"CharHeight",height/1000);
        ComDispatch.Call(a,"SetTextFormat",0,false,fmt);ComDispatch.Call(a,"SetPosition2",x/1000,y/1000,0d);
        ComDispatch.Release(fmt);ComDispatch.Release(a);ComDispatch.Release(n);
    }
    static List<object> Views(object d) {var list=new List<object>();var v=ComDispatch.Call(ComDispatch.Call(d,"GetFirstView")!,"GetNextView");while(v!=null){list.Add(v);v=ComDispatch.Call(v,"GetNextView");}return list;}
    static void SelectView(object d,object v)
    {
        ComDispatch.Call(d,"ClearSelection2",true);
        if(!Convert.ToBoolean(ComDispatch.Call(ComDispatch.Get(d,"Extension")!,"SelectByID2",Name(v),"DRAWINGVIEW",0d,0d,0d,false,0,new DispatchWrapper(null),0)))throw new Exception("Cannot select view.");
    }
    static void Layout(object v,string name,double x,double y,double scale=1)
    {
        ComDispatch.Call(v,"SetName2",name);ComDispatch.Call(v,"RemoveAlignment");ComDispatch.Set(v,"UseSheetScale",false);ComDispatch.Set(v,"ScaleDecimal",scale);ComDispatch.Set(v,"Position",new[]{x/1000,y/1000});
        ComDispatch.Call(v,"SetDisplayMode3",false,1,false,false);
    }
    static void Frame(object d,string title,int page)
    {
        ComDispatch.Call(d,"ActivateView","");
        var sm=ComDispatch.Get(d,"SketchManager")!;
        ComDispatch.Call(sm,"CreateLine",.010,.010,0d,.584,.010,0d);
        ComDispatch.Call(sm,"CreateLine",.584,.010,0d,.584,.410,0d);
        ComDispatch.Call(sm,"CreateLine",.584,.410,0d,.010,.410,0d);
        ComDispatch.Call(sm,"CreateLine",.010,.410,0d,.010,.010,0d);
        ComDispatch.Call(sm,"CreateLine",.010,.042,0d,.584,.042,0d);
        Note(d,"PM20015-0A  CLAVICLE  |  "+title,20,400,5);
        Note(d,$"MATERIAL: 5052-H36   |   UNIT: mm   |   THIRD ANGLE   |   SCALE 1:1   |   A2   |   SHEET {page}/2",20,32);
        Note(d,"DIMENSION PRESENTATION: JIS B 0001:2019   |   TOLERANCES OMITTED AS REQUESTED",20,24);
        Note(d,"SOURCE: PM20015-0A_CLAVICLE_copy   |   SURFACE TREATMENT: NOT SPECIFIED",20,16);
    }
    public object Prepare(string source,string modelCopy,string drawingPath,string template,string probePath)
    {
        source=Path.GetFullPath(source);modelCopy=source;drawingPath=Output(drawingPath);probePath=Path.GetFullPath(probePath);
        var model=Open(modelCopy,1);
        var config=Name(ComDispatch.Get(ComDispatch.Get(model,"ConfigurationManager")!,"ActiveConfiguration")!);
        var modelViewNames=Items(ComDispatch.Call(model,"GetModelViewNames")).Select(x=>x.ToString()).ToArray();
        var d=connection.CreateDocument(template,0,0d,0d)!;
        var ext=ComDispatch.Get(d,"Extension")!;
        if(!Convert.ToBoolean(ComDispatch.Call(ext,"SetUserPreferenceInteger",13,0,4)))throw new Exception("Cannot set JIS dimension standard.");
        ComDispatch.Call(ext,"SetUserPreferenceInteger",263,0,5); // MMGS
        var sh=ComDispatch.Call(d,"GetCurrentSheet")!;ComDispatch.Call(sh,"SetName","FORMED");ComDispatch.Call(sh,"SetProperties2",9,13,1d,1d,false,.594,.420,false);
        if(!Convert.ToBoolean(ComDispatch.Call(d,"Create3rdAngleViews2",modelCopy)))throw new Exception("Cannot create formed views.");
        var vv=Views(d); Layout(vv[0],"FRONT",160,135); Layout(vv[1],"TOP",160,300);Layout(vv[2],"RIGHT",375,135);
        var isoName=modelViewNames.FirstOrDefault(x=>x!=null && (x.Contains("等角")||x.Contains("Isometric")));
        if(isoName!=null) {var iso=ComDispatch.Call(d,"CreateDrawViewFromModelView3",modelCopy,isoName,.440,.300,0d);if(iso!=null) Layout(iso,"ISOMETRIC",440,300,.5);}
        Frame(d,"FORMED GEOMETRY",1); Note(d,"ISOMETRIC VIEW: REFERENCE ONLY (SCALE 1:2)",365,210,3);
        var all=new List<object>();Rebuild(d);all.Add(DumpSheet(d));
        if(!Convert.ToBoolean(ComDispatch.Call(d,"NewSheet3","REVERSE",9,13,1d,1d,false,"",.594,.420,""))) throw new Exception("Cannot create reverse sheet.");
        var sh2=ComDispatch.Call(d,"GetCurrentSheet")!;ComDispatch.Call(sh2,"SetSize",9,.594,.420);
        var reverse=new[]{("背面","Back","BACK",160d,300d),("底面","Bottom","BOTTOM",160d,135d),("左側面","Left","LEFT",375d,300d)};
        foreach(var (jp,en,name,x,y) in reverse)
        {
            var vn=modelViewNames.FirstOrDefault(n=>n!=null&&(n.Contains(jp)||n.Contains(en))) ?? throw new Exception("Missing model view: "+en);
            var v=ComDispatch.Call(d,"CreateDrawViewFromModelView3",modelCopy,vn,x/1000,y/1000,0d) ?? throw new Exception("Cannot create "+name);
            Layout(v,name,x,y);
        }
        Frame(d,"OPPOSITE FACES - FORMED PART",2);Rebuild(d);all.Add(DumpSheet(d));
        ComDispatch.Call(d,"ActivateSheet","FORMED");Rebuild(d);SaveNew(d,drawingPath);
        File.WriteAllText(probePath,JsonSerializer.Serialize(new{Model=modelCopy,Drawing=drawingPath,ModelViews=modelViewNames,Sheets=all},new JsonSerializerOptions{WriteIndented=true}));
        ComDispatch.Call(d,"ViewZoomtofit2");
        return new{Model=modelCopy,Drawing=drawingPath,Probe=probePath,Sheets=2};
    }
    static object[] Edges(object v)
    {
        return Items(ComDispatch.Call(v,"GetVisibleComponents")).SelectMany(c=>Items(ComDispatch.Call(v,"GetVisibleEntities2",c,1))).ToArray();
    }
    static double[] Project(double[] p,double[] t) => new[]{(p[0]*t[0]+p[1]*t[3]+p[2]*t[6])*t[12]+t[9],(p[0]*t[1]+p[1]*t[4]+p[2]*t[7])*t[12]+t[10],(p[0]*t[2]+p[1]*t[5]+p[2]*t[8])*t[12]+t[11]}.Select(x=>x*1000).ToArray();
    object DumpSheet(object d,string? only=null)
    {
        string sheet=(string)ComDispatch.Call(ComDispatch.Call(d,"GetCurrentSheet")!,"GetName")!;
        var vs=new List<object>();
        foreach(var v in Views(d).Where(v=>only==null||Name(v)==only))
        {
            Console.Error.WriteLine("Read drawing edges: "+sheet+"/"+Name(v));
            var t=Num(ComDispatch.Get(ComDispatch.Get(v,"ModelToViewTransform")!,"ArrayData"));
            var es=new List<object>();int index=0;
            if(Name(v)!="ISOMETRIC") foreach(var e in Edges(v))
            {
                var c=ComDispatch.Call(e,"GetCurve")!;var cp=Num(ComDispatch.Call(e,"GetCurveParams2"));
                string kind=Convert.ToBoolean(ComDispatch.Call(c,"IsCircle"))?"circle":Convert.ToBoolean(ComDispatch.Call(c,"IsLine"))?"line":"other";
                var circle=kind=="circle"?Num(ComDispatch.Get(c,"CircleParams")):[];
                es.Add(new{Index=index++,Kind=kind,Start=Project(cp.Take(3).ToArray(),t),End=Project(cp.Skip(3).Take(3).ToArray(),t),Center=circle.Length>0?Project(circle.Take(3).ToArray(),t):null,Diameter=circle.Length>0?circle[6]*2000:0, Sweep=cp.Length>7?Math.Abs(cp[7]-cp[6]):0,ModelStart=cp.Take(3).Select(x=>x*1000).ToArray(),ModelEnd=cp.Skip(3).Take(3).Select(x=>x*1000).ToArray()});
            }
            vs.Add(new{Name=Name(v),Outline=Num(ComDispatch.Call(v,"GetOutline")).Select(x=>x*1000).ToArray(),Transform=t,Config=ComDispatch.Get(v,"ReferencedConfiguration"),Edges=es,Notes=Items(ComDispatch.Call(v,"GetNotes")).Select(n=>Try(()=>ComDispatch.Call(n,"GetText"))).ToArray()});
        }
        return new{Sheet=sheet,Views=vs};
    }
}


