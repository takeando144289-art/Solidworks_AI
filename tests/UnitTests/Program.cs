using SolidWorksAgent.Core.Design;
using SolidWorksAgent.Core.Models;
using SolidWorksAgent.Core.Validation;

var tests = new (string Name, Action Test)[]
{
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
