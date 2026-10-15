using System.Text.Json;
using System.Text.RegularExpressions;

namespace SolidWorksAgent.Core.Automation;

public static class PlateRecipe
{
    public static ApiPlan Create(double width, double height, double thickness, double radius, double[][] holes, string thread = "M3x0.5")
    {
        if(new[]{width,height,thickness}.Any(v=>!double.IsFinite(v)||v<=0) || !double.IsFinite(radius) || radius<0 || radius*2>=Math.Min(width,height))
            throw new ArgumentException("寸法・角Rが不正です。角Rは短辺の半分未満にしてください。");
        var match=Regex.Match(thread,@"^M(\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)$");
        if(!match.Success)throw new ArgumentException("ねじは M3x0.5 の形式で指定してください。");
        double diameter=double.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
        if(holes.Length>60 || diameter<=0)throw new ArgumentException("穴数またはねじ径が不正です。");
        for(int i=0;i<holes.Length;i++)
        {
            var h=holes[i];
            if(h is null || h.Length!=2 || h.Any(v=>!double.IsFinite(v)) || h[0]<=diameter/2 || h[0]>=width-diameter/2 || h[1]<=diameter/2 || h[1]>=height-diameter/2)
                throw new ArgumentException("穴中心は板の左下基準の [x,y] mm で、ねじ径を含め板内に指定してください。");
            var dx=h[0]-Math.Clamp(h[0],radius,width-radius);var dy=h[1]-Math.Clamp(h[1],radius,height-radius);
            if(dx*dx+dy*dy>0 && Math.Sqrt(dx*dx+dy*dy)+diameter/2>radius)throw new ArgumentException("穴が角Rにかかっています。");
            if(holes.Take(i).Any(p=>Math.Pow(p[0]-h[0],2)+Math.Pow(p[1]-h[1],2)<=diameter*diameter))throw new ArgumentException("穴が重なっています。");
        }
        var steps=new List<ApiStep>(); int seq=0;
        object M(double v)=>new Dictionary<string,double>{{"mm",v}};
        void Add(string target,string type,string member,object?[] args,string? id=null,string kind="call",string expect="success")
            =>steps.Add(new(id??"s"+(++seq),target,type,member,kind,args.Select(v=>JsonSerializer.SerializeToElement(v)).ToArray(),expect));
        object Ref(string id)=>new Dictionary<string,string>{{"ref",id}};
        object[] Transform(double x,double y,double z,bool vector=false)
        {
            string id="p"+(++seq); string type=vector?"IMathVector":"IMathPoint";
            object[] coordinates=vector?[x,y,z]:[M(x),M(y),M(z)];
            Add("math","IMathUtility",vector?"CreateVector":"CreatePoint",[coordinates],id);
            Add(id,type,"MultiplyTransform",[Ref("toWorld")],id+"world");
            Add(id+"world",type,"ArrayData",[],id+"xyz","get");
            return [Ref(id+"xyz[0]"),Ref(id+"xyz[1]"),Ref(id+"xyz[2]")];
        }
        Add("plane","IFeature","Select2",[false,0]);
        Add("sketch","ISketchManager","InsertSketch",[true]);
        Add("sketch","ISketchManager","ActiveSketch",[],"profile","get");
        Add("profile","ISketch","ModelToSketchTransform",[],"toSketch","get");
        Add("toSketch","IMathTransform","Inverse",[],"toWorld");
        Add("sketch","ISketchManager","CreateCornerRectangle",[M(0),M(0),M(0),M(width),M(height),M(0)]);
        Add("sketch","ISketchManager","InsertSketch",[true]);
        Add("features","IFeatureManager","FeatureExtrusion2",[true,false,false,0,0,M(thickness),M(0),false,false,false,false,0d,0d,false,false,false,false,true,true,true,0,M(0),false],"base");
        Add("model","IModelDoc2","ViewZoomtofit2",[]);
        if(radius>0)
        {
            Add("model","IModelDoc2","ClearSelection2",[true]);
            foreach(var xy in new[]{new[]{0d,0d},new[]{width,0d},new[]{width,height},new[]{0d,height}})
            {
                var p=Transform(xy[0]+(xy[0]==0?-10:10),xy[1]+(xy[1]==0?-10:10),thickness/2);
                var d=Transform(xy[0]==0?1:-1,xy[1]==0?1:-1,0,true);
                Add("extension","IModelDocExtension","SelectByRay",[..p,..d,M(.02),1,true,1,0]);
            }
            Add("features","IFeatureManager","FeatureFillet3",[3,M(radius),M(0),0d,0,0,0,null,null,null,null,null,null,null],"corners");
        }
        if(holes.Length>0)
        {
            var direction=Transform(0,0,-1,true);
            for(int index=0;index<holes.Length;index++)
            {
                Add("model","IModelDoc2","ClearSelection2",[true]);
                var h=holes[index]; var p=Transform(h[0],h[1],thickness+10);
                Add("extension","IModelDocExtension","SelectByRay",[..p,..direction,M(.01),2,false,0,0]);
                // One native feature per location avoids SOLIDWORKS collapsing repeated selections on the same face.
                string feature="tap"+index, data="holeData"+index;
                Add("features","IFeatureManager","HoleWizard5",[4,8,147,thread,1,-1d,-1d,-1d,M(thickness),-1d,-1d,-1d,-1d,-1d,1d,1d,-1d,-1d,-1d,-1d,"",false,false,true,false,false,false],feature);
                Add(feature,"IFeature","GetDefinition",[],data);
                Add(data,"IWizardHoleFeatureData2","FastenerSize",[],"threadSize"+index,"get");
                Add(data,"IWizardHoleFeatureData2","ThreadDiameter",[],"threadDiameter"+index,"get");
                steps[^1]=steps[^1] with { Equal=JsonSerializer.SerializeToElement(M(diameter)) };
                Add(data,"IWizardHoleFeatureData2","GetSketchPointCount",[],"holeCount"+index);
                steps[^1]=steps[^1] with { Equal=JsonSerializer.SerializeToElement(1) };
            }
        }
        return new($"{width:g} × {height:g} × {thickness:g} mm、四隅R{radius:g}、{thread}貫通タップ穴{holes.Length}個のプレート", "new-part", steps.ToArray(),[width,height,thickness]);
    }
}
