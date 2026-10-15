using SolidWorksAgent.Core.Automation;
using System.Text.Json;
using SolidWorksAgent.Core.Design;
using SolidWorksAgent.Core.Drawing;
using SolidWorksAgent.Core.Models;
using SolidWorksAgent.Core.Validation;

var tests = new (string Name, Action Test)[]
{
    ("API plans reject unapproved application access", () => MustThrow<ArgumentException>(() => ApiPolicy.Validate("ISldWorks","RunMacro2"))),
    ("API plans cannot bypass host save", () => MustThrow<ArgumentException>(() => ApiPolicy.Validate("IModelDoc2","SaveAs3"))),
    ("API plans support feature operations", () => { ApiPolicy.Validate("IFeatureManager","FeatureFillet3"); ApiPolicy.Validate("ISketchManager","InsertSketch"); }),
    ("Plate recipe round trips through strict plan validation", () => { var p=PlateRecipe.Create(150,20,3,5,[[10,10],[20,10]],"M3x0.5"); ApiPlan.Parse(JsonSerializer.Serialize(p,ApiPlan.Json)); }),
    ("Plate rejects holes outside material", () => MustThrow<ArgumentException>(()=>PlateRecipe.Create(150,20,3,5,[[1,1]]))),
    ("Plate rejects overlapping holes", () => MustThrow<ArgumentException>(()=>PlateRecipe.Create(150,20,3,5,[[10,10],[11,10]]))),
    ("API rejects forward object references", () => MustThrow<ArgumentException>(()=>ApiPlan.Parse("""{"summary":"test","document":"new-part","steps":[{"id":"a","target":"missing","interface":"IModelDoc2","member":"ClearSelection2","kind":"call","args":[true]}]}"""))),
    ("Drawing layout supports arbitrary stage sizes", () => CheckDrawingLayout([new(30,20),new(30,30),new(20,30),new(40,35)])),
    ("Drawing layout scales large models onto one sheet", () => CheckDrawingLayout([new(25000,3000),new(25000,5000),new(3000,5000),new(28000,6000)])),
    ("Drawing layout places all seven views", () => CheckDrawingLayout(Enumerable.Repeat(new DrawingLayout.Size(200,150),7).ToArray())),
    ("Drawing layout rejects impossible explicit scale", () => MustThrow<ArgumentException>(() => DrawingLayout.Fit([new(1000,1000)],"A4",1))),
    ("Drawing layout rejects non-finite size", () => MustThrow<ArgumentException>(() => DrawingLayout.Fit([new(double.NaN,20)]))),
    ("Cleanup preserves a dirty final output",()=>CheckCleanup(false,true,true,false,false,false,true,true)),
    ("Cleanup discards an owned unsaved intermediate",()=>CheckCleanup(true,false,true,false,false,false,true,true)),
    ("Cleanup discards owned intermediates on failure",()=>CheckCleanup(true,false,true,false,false,false,true,false)),
    ("Cleanup preserves preexisting dirty user documents",()=>CheckCleanup(false,false,false,true,false,true,true,true)),
    ("Cleanup preserves concurrently opened unowned documents",()=>CheckCleanup(false,false,false,false,false,false,true,true)),
    ("Cleanup retires only clean previous generated outputs",()=>CheckCleanup(true,false,false,true,true,false,false,true)),
    ("Cleanup protects a previous output edited during generation",()=>CheckCleanup(false,false,false,true,true,false,true,true)),
    ("Failure preserves the previous successful output",()=>CheckCleanup(false,false,false,true,true,false,false,false)),
    ("CylinderSpec accepts positive dimensions", () => new CylinderSpec { DiameterMm = 30, LengthMm = 20 }.Validate()),
    ("CylinderSpec rejects zero diameter", () => MustThrow<ArgumentOutOfRangeException>(() => new CylinderSpec { DiameterMm = 0, LengthMm = 20 }.Validate())),
    ("Geometry validation ignores extrusion axis", () => GeometryValidator.ValidateCylinder(
        new CylinderSpec { DiameterMm = 30, LengthMm = 20 }, new BoundingBox(20, 30, 30))),
    ("Geometry validation rejects wrong size", () => MustThrow<InvalidOperationException>(() => GeometryValidator.ValidateCylinder(
        new CylinderSpec { DiameterMm = 30, LengthMm = 20 }, new BoundingBox(20, 29, 30)))),
    ("Stepped shaft validates stages", () => new SteppedShaftSpec { Stages = [new(12, 8), new(10, 20), new(8, 5)] }.Validate()),
    ("Stepped shaft requires a stage", () => MustThrow<ArgumentException>(() => new SteppedShaftSpec { Stages = [] }.Validate())),
    ("Tube validates diameters", () => new TubeSpec { OuterDiameterMm = 22, InnerDiameterMm = 12, LengthMm = 7 }.Validate()),
    ("Tube rejects inverted diameters", () => MustThrow<ArgumentException>(() => new TubeSpec { OuterDiameterMm = 12, InnerDiameterMm = 22, LengthMm = 7 }.Validate())),
    ("Assembly interference totals are calculated", () =>
    {
        var analysis = new AssemblyAnalysisResult(
            "Test", null, new BoundingBox(30, 30, 33), null, [],
            [new InterferenceInfo(["A", "B"], 1.25, false, false), new InterferenceInfo(["B", "C"], 2.75, false, false)]);
        if (analysis.InterferenceCount != 2 || Math.Abs(analysis.TotalInterferenceVolumeMm3 - 4) > 1e-9)
            throw new Exception("Interference aggregation is incorrect.");
    })
};

var failures = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static void MustThrow<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static void CheckCleanup(bool expected,bool final,bool owned,bool existed,bool previous,bool dirtyBefore,bool dirtyNow,bool succeeded)
{
    if(DocumentCleanupPolicy.ShouldClose(final,owned,existed,previous,dirtyBefore,dirtyNow,succeeded)!=expected)throw new Exception("Incorrect document cleanup decision.");
}

static void CheckDrawingLayout(DrawingLayout.Size[] sizes)
{
    var plan=DrawingLayout.Fit(sizes);
    if(plan.Cells.Length!=sizes.Length || plan.Scale<=0) throw new Exception("Invalid layout");
    for(int i=0;i<sizes.Length;i++)
    {
        var c=plan.Cells[i];var s=sizes[i];
        if(s.WidthMm*plan.Scale>c.WidthMm-44+.001 || s.HeightMm*plan.Scale>c.HeightMm-36+.001) throw new Exception("View does not fit its cell");
        if(c.Xmm-c.WidthMm/2<0 || c.Ymm-c.HeightMm/2<0 || c.Xmm+c.WidthMm/2>plan.WidthMm || c.Ymm+c.HeightMm/2>plan.HeightMm) throw new Exception("View outside sheet");
    }
}
