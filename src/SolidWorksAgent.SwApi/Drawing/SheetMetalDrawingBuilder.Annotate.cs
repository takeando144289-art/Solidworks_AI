using System.Text.Json;
using SolidWorksAgent.SwApi.Interop;
namespace SolidWorksAgent.SwApi.Drawing;

public sealed partial class SheetMetalDrawingBuilder
{
    public object AddAuxiliary(string input,string output,string pdf,string probe)
    {
        output=Output(output);pdf=Output(pdf);File.Copy(Path.GetFullPath(input),output,false);var d=Open(output,3);ComDispatch.Call(d,"ActivateSheet","FORMED");
        var v=Views(d).Single(v=>Name(v)=="FRONT");var edge=Edges(v)[87];
        ComDispatch.Call(d,"ClearSelection2",true);ComDispatch.Call(d,"ActivateView",Name(v));ComDispatch.Call(v,"SelectEntity",edge,false);
        var aux=ComDispatch.Call(d,"CreateAuxiliaryViewAt2",.500,.130,0d,true,"A",true,false) ?? throw new Exception("Auxiliary view failed.");
        Layout(aux,"AUX_A",500,130);ComDispatch.Call(aux,"SetDisplayMode3",false,2,false,false);
        Rebuild(d);File.WriteAllText(probe,JsonSerializer.Serialize(DumpSheet(d,"AUX_A"),new JsonSerializerOptions{WriteIndented=true}));
        SaveNew(d,output);SaveNew(d,pdf);return new{Drawing=output,Pdf=pdf,Probe=probe};
    }
    public object Annotate(string input,string planPath,string output,string pdf)
    {
        output=Output(output);pdf=Output(pdf);
        using var planDoc=JsonDocument.Parse(File.ReadAllText(planPath));var plan=planDoc.RootElement;
        File.Copy(Path.GetFullPath(input),output,false);
        var d=Open(output,3);var report=new List<object>();
        if(plan.TryGetProperty("singleSheet",out var single)&&single.GetBoolean())ConsolidateSheets(d);
        ComDispatch.Call(ComDispatch.Get(d,"Extension")!,"SetUserPreferenceInteger",263,0,5);
        var extension=ComDispatch.Get(d,"Extension")!;
        ComDispatch.Call(extension,"SetUserPreferenceInteger",13,0,0);
        ComDispatch.Call(extension,"SetUserPreferenceInteger",13,0,4);
        foreach(var (ft,op) in new[]{(0,104),(1,200),(1,205),(1,206),(4,303),(16,303),(33,303),(34,303)}) {
            var fmt=ComDispatch.Call(extension,"GetUserPreferenceTextFormat",ft,op);if(fmt==null)continue;
            ComDispatch.Set(fmt,"TypeFaceName","Arial");ComDispatch.Set(fmt,"CharHeight",.0035d);
            ComDispatch.Call(extension,"SetUserPreferenceTextFormat",ft,op,fmt);
        }
        foreach(var sheetPlan in plan.GetProperty("sheets").EnumerateArray())
        {
            string sheet=sheetPlan.GetProperty("sheet").GetString()!;ComDispatch.Call(d,"ActivateSheet",sheet);
            var views=Views(d).ToDictionary(Name);
            if(views.TryGetValue("ISOMETRIC",out var iso))ComDispatch.Call(iso,"SetDisplayMode3",false,2,false,false);
            if(views.TryGetValue("AUX_A",out var auxView)) {
                if(sheetPlan.TryGetProperty("projectionArrow",out var pa)&&pa.GetBoolean()) {
                    var front=views["FRONT"];var edge=Edges(front)[87];
                    ComDispatch.Call(d,"ClearSelection2",true);ComDispatch.Call(d,"ActivateView","FRONT");ComDispatch.Call(front,"SelectEntity",edge,false);
                    var direction=ComDispatch.Call(d,"InsertNote","A")!;var da=ComDispatch.Call(direction,"GetAnnotation")!;
                    ComDispatch.Call(da,"SetLeader3",1,0,false,false,false,false);
                    // The arrow points opposite the outward normal of the inclined tip.
                    ComDispatch.Call(da,"SetPosition2",.240259,.158975,0d);
                    ComDispatch.Call(da,"SetLeaderAttachmentPointAtIndex",0,.214883,.147141,0d);
                    var df=ComDispatch.Call(da,"GetTextFormat",0)!;ComDispatch.Set(df,"TypeFaceName","Arial");ComDispatch.Set(df,"CharHeight",.005d);ComDispatch.Call(da,"SetTextFormat",0,false,df);ComDispatch.Set(da,"Color",0);
                }
                foreach(var note in Items(ComDispatch.Call(auxView,"GetNotes"))) {
                    var text=Convert.ToString(ComDispatch.Call(note,"GetText"));if(text!="A"&&!text!.Contains("図面"))continue;
                    ComDispatch.Call(note,"SetText","A");var an=ComDispatch.Call(note,"GetAnnotation")!;
                    ComDispatch.Call(an,"SetPosition2",.500,.062,0d);
                    var nf=ComDispatch.Call(an,"GetTextFormat",0)!;ComDispatch.Set(nf,"TypeFaceName","Arial");ComDispatch.Call(an,"SetTextFormat",0,false,nf);
                }
            }
            if(sheetPlan.TryGetProperty("moves",out var moves))foreach(var move in moves.EnumerateArray()) {
                var v=views[move.GetProperty("view").GetString()!];ComDispatch.Set(v,"Position",new[]{move.GetProperty("x").GetDouble()/1000,move.GetProperty("y").GetDouble()/1000});
            }
            Rebuild(d);
            if(sheetPlan.TryGetProperty("notes",out var notes))foreach(var n in notes.EnumerateArray())Note(d,n.GetProperty("text").GetString()!,n.GetProperty("x").GetDouble(),n.GetProperty("y").GetDouble(),n.TryGetProperty("height",out var ht)?ht.GetDouble():3.5);
            foreach(var vp in sheetPlan.GetProperty("views").EnumerateArray())
            {
                var v=views[vp.GetProperty("view").GetString()!];var edges=Edges(v);
                if(vp.TryGetProperty("removeDimensions",out var remove)&&remove.GetBoolean()) {
                    foreach(var dd in Items(ComDispatch.Call(v,"GetDisplayDimensions"))) {
                        ComDispatch.Call(d,"ClearSelection2",true);ComDispatch.Call(ComDispatch.Call(dd,"GetAnnotation")!,"Select2",false,0);ComDispatch.Call(d,"EditDelete");
                    }
                }
                if(vp.TryGetProperty("reposition",out var positions))foreach(var pos in positions.EnumerateArray()) {
                    string id=pos.GetProperty("id").GetString()!;
                    var dd=Items(ComDispatch.Call(v,"GetDisplayDimensions")).Single(z=>Convert.ToString(ComDispatch.Get(ComDispatch.Call(z,"GetDimension2",0)!,"FullName"))!.StartsWith(id+"@",StringComparison.Ordinal));
                    ComDispatch.Call(ComDispatch.Call(dd,"GetAnnotation")!,"SetPosition2",pos.GetProperty("x").GetDouble()/1000,pos.GetProperty("y").GetDouble()/1000,0d);
                }
                foreach(var spec in vp.GetProperty("dimensions").EnumerateArray())
                {
                    string label=spec.GetProperty("label").GetString()!,kind=spec.GetProperty("kind").GetString()!;
                    Console.Error.WriteLine("Dimension: "+sheet+"/"+Name(v)+"/"+label);
                    ComDispatch.Call(d,"ClearSelection2",true);ComDispatch.Call(d,"ActivateView",Name(v));
                    bool append=false;foreach(var r in spec.GetProperty("refs").EnumerateArray())
                    {
                        var e=edges[r.GetProperty("edge").GetInt32()];object entity=e;
                        if(r.TryGetProperty("vertex",out var vertex))entity=ComDispatch.Call(e,vertex.GetString()=="start"?"GetStartVertex":"GetEndVertex") ?? throw new Exception("Requested edge has no vertex.");
                        if(!Convert.ToBoolean(ComDispatch.Call(v,"SelectEntity",entity,append)))throw new Exception("Cannot select entity for "+label);
                        append=true;
                    }
                    string method=kind switch {"H"=>"AddHorizontalDimension2","V"=>"AddVerticalDimension2","HOLE"=>"AddHoleCallout2",_=>"AddDimension2"};
                    var display=ComDispatch.Call(d,method,spec.GetProperty("x").GetDouble()/1000,spec.GetProperty("y").GetDouble()/1000,0d) ?? throw new Exception("Cannot create "+label);
                    if(kind=="HOLE") {
                        var vars=Items(ComDispatch.Call(display,"GetHoleCalloutVariables"));
                        var tv=vars.Single(z=>Convert.ToString(ComDispatch.Get(z,"VariableName"))=="hw-threaddesc");
                        var thread=Convert.ToString(ComDispatch.Get(tv,"String"))!;
                        var oldAnnotation=ComDispatch.Call(display,"GetAnnotation")!;
                        var attachedEdges=Items(ComDispatch.Call(oldAnnotation,"GetAttachedEntities3"));
                        ComDispatch.Call(d,"ClearSelection2",true);ComDispatch.Call(oldAnnotation,"Select2",false,0);ComDispatch.Call(d,"EditDelete");
                        ComDispatch.Call(d,"ClearSelection2",true);
                        ComDispatch.Call(v,"SelectEntity",edges[spec.GetProperty("refs")[0].GetProperty("edge").GetInt32()],false);
                        var note=ComDispatch.Call(d,"InsertNote","2-"+thread)!;
                        var na=ComDispatch.Call(note,"GetAnnotation")!;
                        ComDispatch.Call(na,"SetAttachedEntities",attachedEdges);
                        ComDispatch.Call(na,"SetLeader3",1,0,true,false,false,false);
                        ComDispatch.Call(na,"SetPosition2",spec.GetProperty("x").GetDouble()/1000,spec.GetProperty("y").GetDouble()/1000,0d);
                        var nf=ComDispatch.Call(na,"GetTextFormat",0)!;ComDispatch.Set(nf,"TypeFaceName","Arial");ComDispatch.Set(nf,"CharHeight",.0035d);ComDispatch.Call(na,"SetTextFormat",0,false,nf);ComDispatch.Set(na,"Color",0);
                        int ac=Items(ComDispatch.Call(na,"GetAttachedEntities3")).Length;
                        bool dang=Convert.ToBoolean(ComDispatch.Call(na,"IsDangling"));
                        if(ac==0||dang)throw new Exception("Unattached thread note: "+label);
                        report.Add(new{Sheet=sheet,View=Name(v),Label=label,Kind="ThreadNote",Thread=thread,Attached=ac,Dangling=dang,Text="2-"+thread,SizeAutomaticallyUpdates=false});
                        continue;
                    }
                    if(spec.TryGetProperty("prefix",out var prefix))ComDispatch.Call(display,"SetText",1,prefix.GetString());
                    if(spec.TryGetProperty("suffix",out var suffix))ComDispatch.Call(display,"SetText",2,suffix.GetString());
                    var dim=ComDispatch.Call(display,"GetDimension2",0)!;var tolerance=ComDispatch.Get(dim,"Tolerance")!;if(kind!="HOLE")ComDispatch.Set(tolerance,"Type",0);
                    var a=ComDispatch.Call(display,"GetAnnotation")!;
                    ComDispatch.Set(a,"Color",0);
                    int formats=Convert.ToInt32(ComDispatch.Call(a,"GetTextFormatCount"));
                    for(int fi=0;fi<Math.Max(1,formats);fi++) {
                        var fmt=ComDispatch.Call(a,"GetTextFormat",fi)!;ComDispatch.Set(fmt,"TypeFaceName","Arial");ComDispatch.Set(fmt,"CharHeight",.0035d);ComDispatch.Call(a,"SetTextFormat",fi,false,fmt);
                    }
                    double value=Convert.ToDouble(ComDispatch.Get(dim,"SystemValue"))*(kind=="A"?180/Math.PI:1000);
                    var attached=Items(ComDispatch.Call(a,"GetAttachedEntities3"));bool dangling=Convert.ToBoolean(ComDispatch.Call(a,"IsDangling")),over=Convert.ToBoolean(ComDispatch.Call(display,"GetOverride"));
                    if(attached.Length==0||dangling||over)throw new Exception("Unassociated dimension: "+label);
                    if(spec.TryGetProperty("expected",out var expected)&&Math.Abs(value-expected.GetDouble())>.005)throw new Exception($"Dimension {label} reads {value}, expected {expected}.");
                    int precision=spec.TryGetProperty("precision",out var pr)?pr.GetInt32():Math.Abs(value-Math.Round(value))<1e-6?0:Math.Abs(value*10-Math.Round(value*10))<1e-6?1:2;
                    if(kind!="HOLE")ComDispatch.Call(display,"SetPrecision3",precision,0,precision,0);
                    report.Add(new{Sheet=sheet,View=Name(v),Label=label,Value=value,Attached=attached.Length,Dangling=dangling,Override=over,FullName=ComDispatch.Get(dim,"FullName"),Text=ComDispatch.Call(display,"GetText",0)});
                }
            }
            ComDispatch.Call(d,"ClearSelection2",true);ComDispatch.Call(d,"ActivateView","");Rebuild(d);
        }
        var before=ValidateSaved(d);
        ComDispatch.Call(d,"ActivateSheet","FORMED");ComDispatch.Call(d,"GraphicsRedraw2");SaveNew(d,output);
        ComDispatch.Call(connection.Application,"CloseDoc",ComDispatch.Call(d,"GetTitle"));
        d=Open(output,3);Rebuild(d);var after=ValidateSaved(d);
        if(JsonSerializer.Serialize(before)!=JsonSerializer.Serialize(after))throw new Exception("Saved drawing associations or values changed after reopen.");
        ComDispatch.Call(d,"ActivateSheet","FORMED");
        var pdfPrefs=new Dictionary<int,bool>();
        try {
            foreach(var (id,value) in new[]{(323,false),(324,false),(325,true)}) {
                pdfPrefs[id]=Convert.ToBoolean(ComDispatch.Call(connection.Application,"GetUserPreferenceToggle",id));
                ComDispatch.Call(connection.Application,"SetUserPreferenceToggle",id,value);
            }
            SaveNew(d,pdf);
        } finally {foreach(var p in pdfPrefs)ComDispatch.Call(connection.Application,"SetUserPreferenceToggle",p.Key,p.Value);}
        ComDispatch.Call(d,"ViewZoomtofit2");
        return new{Drawing=output,Pdf=pdf,Dimensions=report,Count=report.Count,ReopenedAndVerified=true,Verification=after};
    }
    static object ValidateSaved(object d)
    {
        var dims=new List<object>();var notes=new List<object>();var configs=new List<object>();
        foreach(var sn in Items(ComDispatch.Call(d,"GetSheetNames"))) {
            string sheet=sn.ToString()!;ComDispatch.Call(d,"ActivateSheet",sheet);Rebuild(d);
            foreach(var v in Views(d)) {
                configs.Add(new{Sheet=sheet,View=Name(v),Configuration=ComDispatch.Get(v,"ReferencedConfiguration")});
                foreach(var dd in Items(ComDispatch.Call(v,"GetDisplayDimensions"))) {
                    var dim=ComDispatch.Call(dd,"GetDimension2",0)!;var a=ComDispatch.Call(dd,"GetAnnotation")!;
                    int attached=Items(ComDispatch.Call(a,"GetAttachedEntities3")).Length;
                    int tol=Convert.ToInt32(ComDispatch.Get(ComDispatch.Get(dim,"Tolerance")!,"Type"));
                    if(attached==0||tol!=0||Convert.ToBoolean(ComDispatch.Call(a,"IsDangling"))||Convert.ToBoolean(ComDispatch.Call(dd,"GetOverride")))throw new Exception("Invalid saved dimension.");
                    dims.Add(new{Sheet=sheet,View=Name(v),Name=ComDispatch.Get(dim,"FullName"),ValueSI=ComDispatch.Get(dim,"SystemValue"),Attached=attached,Tolerance=tol});
                }
                foreach(var n in Items(ComDispatch.Call(v,"GetNotes"))) {
                    var a=ComDispatch.Call(n,"GetAnnotation")!;int attached=Items(ComDispatch.Call(a,"GetAttachedEntities3")).Length;
                    if(attached>0) {
                        if(Convert.ToBoolean(ComDispatch.Call(a,"IsDangling")))throw new Exception("Dangling saved note.");
                        notes.Add(new{Sheet=sheet,View=Name(v),Text=ComDispatch.Call(n,"GetText"),Attached=attached});
                    }
                }
            }
        }
        int standard=Convert.ToInt32(ComDispatch.Call(ComDispatch.Get(d,"Extension")!,"GetUserPreferenceInteger",13,0));
        if(standard!=4)throw new Exception("Drawing standard is not JIS.");
        return new{Standard=standard,Dimensions=dims,AttachedNotes=notes,Views=configs};
    }
}
