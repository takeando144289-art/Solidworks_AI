using System.Runtime.InteropServices;
using SolidWorksAgent.SwApi.Interop;
namespace SolidWorksAgent.SwApi.Drawing;

public sealed partial class SheetMetalDrawingBuilder
{
    static double[] DimensionValues(object d)
    {
        var values=new List<double>();
        foreach(var sn in Items(ComDispatch.Call(d,"GetSheetNames"))) {
            ComDispatch.Call(d,"ActivateSheet",sn.ToString());
            foreach(var v in Views(d))foreach(var dd in Items(ComDispatch.Call(v,"GetDisplayDimensions")))
                values.Add(Convert.ToDouble(ComDispatch.Get(ComDispatch.Call(dd,"GetDimension2",0)!,"SystemValue")));
        }
        return values.Order().ToArray();
    }
    static void ConsolidateSheets(object d)
    {
        var before=DimensionValues(d);
        foreach(var name in new[]{"LEFT","BOTTOM"}) {
            Console.Error.WriteLine("Move view to single sheet: "+name);
            ComDispatch.Call(d,"ActivateSheet","REVERSE");
            var source=Views(d).Single(v=>Name(v)==name);SelectView(d,source);ComDispatch.Call(d,"EditCut");
            ComDispatch.Call(d,"ActivateSheet","FORMED");ComDispatch.Call(d,"ClearSelection2",true);ComDispatch.Call(d,"ActivateView","");
            var existing=Views(d).Select(Name).ToHashSet();ComDispatch.Call(d,"Paste");Rebuild(d);
            var pasted=Views(d).Where(v=>!existing.Contains(Name(v))).ToArray();
            if(pasted.Length!=1)throw new Exception("Expected one pasted view: "+name);
            ComDispatch.Call(pasted[0],"SetName2",name);ComDispatch.Call(pasted[0],"RemoveAlignment");
            ComDispatch.Set(pasted[0],"Position",name=="LEFT"?new[]{.375,.300}:new[]{.520,.265});
        }
        ComDispatch.Call(d,"ActivateSheet","REVERSE");
        if(Views(d).Any(v=>Items(ComDispatch.Call(v,"GetDisplayDimensions")).Length>0))throw new Exception("Dimensions remain on the second sheet.");
        ComDispatch.Call(d,"ActivateSheet","FORMED");ComDispatch.Call(d,"ClearSelection2",true);
        var ext=ComDispatch.Get(d,"Extension")!;
        if(!Convert.ToBoolean(ComDispatch.Call(ext,"SelectByID2","REVERSE","SHEET",0d,0d,0d,false,0,new DispatchWrapper(null),0)))throw new Exception("Cannot select redundant sheet.");
        if(!Convert.ToBoolean(ComDispatch.Call(ext,"DeleteSelection2",1)))throw new Exception("Cannot remove redundant sheet.");
        ComDispatch.Call(d,"ActivateSheet","FORMED");
        foreach(var n in Items(ComDispatch.Call(ComDispatch.Call(d,"GetFirstView")!,"GetNotes"))) {
            string t=Convert.ToString(ComDispatch.Call(n,"GetText"))??"";
            if(t.Contains("SHEET 1/2"))ComDispatch.Call(n,"SetText",t.Replace("SHEET 1/2","SHEET 1/1"));
            if(t.StartsWith("ISOMETRIC VIEW")) {
                ComDispatch.Call(n,"SetText","ISOMETRIC (1:2)");
                ComDispatch.Call(ComDispatch.Call(n,"GetAnnotation")!,"SetPosition2",.485,.395,0d);
            }
            if(t.StartsWith("AUXILIARY A"))ComDispatch.Call(n,"SetText","A - NORMAL TO INCLINED TIP");
        }
        var iso=Views(d).Single(v=>Name(v)=="ISOMETRIC");ComDispatch.Set(iso,"Position",new[]{.520,.355});
        Note(d,"LEFT VIEW",355,237,3.5);Note(d,"BOTTOM VIEW",485,305,3.5);
        Rebuild(d);
        var after=DimensionValues(d);
        if(before.Length!=after.Length||before.Where((value,i)=>Math.Abs(value-after[i])>1e-10).Any())throw new Exception("Dimension values changed during consolidation.");
        if(Items(ComDispatch.Call(d,"GetSheetNames")).Length!=1)throw new Exception("Consolidation did not result in one sheet.");
        Console.Error.WriteLine("Single sheet: "+after.Length+" dimensions preserved.");
    }
}
