using SolidWorksAgent.SwApi.Interop;
using System.Runtime.InteropServices;

namespace SolidWorksAgent.SwApi.Drawing;

public sealed partial class GeneralDrawingBuilder
{
    private sealed record Ring(object Edge, double[] Center, double Diameter);
    private void AddGeometryDimensions(ComScope scope, object drawing, object[] views, List<string> warnings)
    {
        var math = scope.Own(ComDispatch.Call(connection.Application, "GetMathUtility"));
        var diameters = new HashSet<double>();
        var measured = new HashSet<(string Kind, double Value)>();
        foreach(var v in ModelViews(scope,drawing)) foreach(var dd in Items(ComDispatch.Call(v,"GetDisplayDimensions")))
        {
            scope.Own(dd); var d=scope.Own(ComDispatch.Call(dd,"GetDimension2",0));
            if(Convert.ToInt32(ComDispatch.Call(d,"GetType"))!=0) continue;
            int type=Convert.ToInt32(ComDispatch.Get(dd,"Type2"));
            measured.Add((type==6 ? "diameter" : type==5 ? "radius" : "linear", Math.Round(Convert.ToDouble(ComDispatch.Get(d,"SystemValue"))*1000,6)));
        }
        foreach (var view in views)
        {
            var transform = scope.Own(ComDispatch.Get(view, "ModelToViewTransform"));
            double[] Project(double[] xyz)
            {
                var p = scope.Own(ComDispatch.Call(math, "CreatePoint", (object)xyz));
                return Numbers(ComDispatch.Get(scope.Own(ComDispatch.Call(p, "MultiplyTransform", transform)), "ArrayData"));
            }
            var components = Items(ComDispatch.Call(view, "GetVisibleComponents"));
            var edges = new List<object>(); var vertices = new List<object>();
            foreach (var component in components)
            {
                scope.Own(component);
                edges.AddRange(Items(ComDispatch.Call(view, "GetVisibleEntities2", component, 1)).Select(scope.Own));
                vertices.AddRange(Items(ComDispatch.Call(view, "GetVisibleEntities2", component, 2)).Select(scope.Own));
            }
            var b = Bounds(view); var viewScale = Convert.ToDouble(ComDispatch.Get(view, "ScaleDecimal"));
            bool Dimension(string method, object[] entities, double expected, double x, double y)
            {
                if (expected < 1e-5 || !double.IsFinite(expected)) return false;
                var key = (method=="AddDiameterDimension2" ? "diameter" : "linear", Math.Round(expected,6));
                if(measured.Contains(key)) return false;
                ComDispatch.Call(drawing, "ClearSelection2", true); ComDispatch.Call(drawing, "ActivateView", Name(view));
                for (int i = 0; i < entities.Length; i++) if (!Convert.ToBoolean(ComDispatch.Call(view, "SelectEntity", entities[i], i > 0))) return false;
                var display = ComDispatch.Call(drawing, method, x, y, 0d);
                if (display is null) return false;
                scope.Own(display); var dim = scope.Own(ComDispatch.Call(display, "GetDimension2", 0));
                var annotation = scope.Own(ComDispatch.Call(display, "GetAnnotation"));
                var actual = Convert.ToDouble(ComDispatch.Get(dim, "SystemValue"))*1000;
                if (Convert.ToInt32(ComDispatch.Call(dim, "GetType")) == 0 && Math.Abs(actual-expected) < .001 && !Convert.ToBoolean(ComDispatch.Call(annotation, "IsDangling"))) { measured.Add(key); return true; }
                // Reject a solver-selected dimension that does not measure the intended geometry.
                ComDispatch.Call(drawing, "ClearSelection2", true);
                var ext = scope.Own(ComDispatch.Get(drawing, "Extension"));
                if (!Convert.ToBoolean(ComDispatch.Call(ext, "SelectByID2", ComDispatch.Call(display, "GetNameForSelection"), "DIMENSION", 0d, 0d, 0d, false, 0, new DispatchWrapper(null), 0)) || !Convert.ToBoolean(ComDispatch.Call(ext, "DeleteSelection2", 0)))
                    throw new InvalidOperationException("自動寸法の誤った測定方向を取り消せませんでした。");
                return false;
            }
            var projected = vertices.Select(v => (Entity:v, Point:Project(Numbers(ComDispatch.Call(v,"GetPoint"))))).ToArray();
            for (int axis = 0; axis < 2 && projected.Length > 1; axis++)
            {
                var min = projected.MinBy(p => p.Point[axis]); var max = projected.MaxBy(p => p.Point[axis]);
                var expected = (max.Point[axis]-min.Point[axis])*1000/viewScale;
                if (expected > .001) Dimension(axis == 0 ? "AddHorizontalDimension2" : "AddVerticalDimension2", [min.Entity,max.Entity], expected,
                    axis == 0 ? (b[0]+b[2])/2 : b[0]-.009, axis == 0 ? b[1]-.009 : (b[1]+b[3])/2);
            }
            var rings = new List<Ring>();
            foreach (var edge in edges)
            {
                var curve = scope.Own(ComDispatch.Call(edge, "GetCurve"));
                if (!Convert.ToBoolean(ComDispatch.Call(curve, "IsCircle"))) continue;
                var p = Numbers(ComDispatch.Get(curve, "CircleParams"));
                rings.Add(new(edge, p.Take(3).ToArray(), p[6]*2000));
            }
            foreach (var ring in rings)
            {
                var key = Math.Round(ring.Diameter, 6);
                if (diameters.Contains(key)) continue;
                if (Dimension("AddDiameterDimension2", [ring.Edge], ring.Diameter, b[2]+.009, (b[1]+b[3])/2)) diameters.Add(key);
            }
            // Axial shoulders: group coaxial circular edges without assuming a stage count or a diameter.
            if (rings.Count > 1)
            {
                int axis = Enumerable.Range(0,3).MaxBy(i => rings.Max(r => r.Center[i])-rings.Min(r => r.Center[i]));
                bool coaxial = Enumerable.Range(0,3).Where(i=>i!=axis).All(i=>rings.Max(r=>r.Center[i])-rings.Min(r=>r.Center[i]) < 1e-7);
                if (coaxial)
                {
                    var stations = rings.GroupBy(r=>Math.Round(r.Center[axis],8)).OrderBy(g=>g.Key).Select(g=>g.OrderByDescending(r=>r.Diameter).First()).ToArray();
                    if (stations.Length > 1)
                    {
                        var a = Project(stations[0].Center); var z = Project(stations[^1].Center); int along = Math.Abs(z[0]-a[0]) > Math.Abs(z[1]-a[1]) ? 0 : 1;
                        if (Math.Abs(z[along]-a[along]) > 1e-8)
                        {
                            foreach (var target in stations.Skip(1))
                            {
                                var p = Project(target.Center); var expected = Math.Abs(target.Center[axis]-stations[0].Center[axis])*1000;
                                Dimension(along==0 ? "AddHorizontalDimension2" : "AddVerticalDimension2", [stations[0].Edge,target.Edge], expected,
                                    along==0 ? (a[0]+p[0])/2 : b[0]-.015, along==0 ? b[1]-.015 : (a[1]+p[1])/2);
                            }
                        }
                    }
                }
            }
        }
        ComDispatch.Call(drawing, "ClearSelection2", true);
    }

    private static void ArrangeDimensions(ComScope scope, object drawing, List<object> views)
    {
        var extension=scope.Own(ComDispatch.Get(drawing,"Extension"));
        foreach(var view in views)
        {
            ComDispatch.Call(drawing,"ClearSelection2",true);ComDispatch.Call(drawing,"ActivateView",Name(view));
            int count=0;
            var radial = new List<object>();
            foreach(var display in Items(ComDispatch.Call(view,"GetDisplayDimensions")))
            {
                scope.Own(display);var annotation=scope.Own(ComDispatch.Call(display,"GetAnnotation"));
                if (Convert.ToInt32(ComDispatch.Get(display,"Type2")) is 5 or 6) { radial.Add(annotation); continue; }
                if(Convert.ToBoolean(ComDispatch.Call(annotation,"Select3",true,new DispatchWrapper(null)))) count++;
            }
            if(count>0) ComDispatch.Call(extension,"AlignDimensions",0,.006);
            // AutoArrange can leave coincident diameter leaders; give their text distinct rows.
            var b = Bounds(view);
            for (int i = 0; i < radial.Count; i++)
                ComDispatch.Call(radial[i], "SetPosition2", b[2] + .012,
                    (b[1] + b[3]) / 2 + ((radial.Count - 1) / 2d - i) * .008, 0d);
        }
        ComDispatch.Call(drawing,"ClearSelection2",true);
    }
}
