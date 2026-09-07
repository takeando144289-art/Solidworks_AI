using SolidWorksAgent.Core.Design;
using SolidWorksAgent.Core.Logging;
using SolidWorksAgent.Core.Models;
using SolidWorksAgent.Core.Validation;
using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi.Parts;

public sealed class PartBuilder(SwConnection connection, IAgentLog? log = null)
{
    private readonly IAgentLog _log = log ?? new NullAgentLog();

    public PartCreationResult CreateCylinder(CylinderSpec spec, string outputPath, string partTemplate, bool overwrite = false)
    {
        spec.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(partTemplate)) throw new FileNotFoundException("SolidWorks part template was not found.", partTemplate);

        outputPath = Path.GetFullPath(outputPath);
        if (!outputPath.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must use the .SLDPRT extension.", nameof(outputPath));
        if (File.Exists(outputPath) && !overwrite)
            throw new IOException($"Refusing to overwrite existing CAD file: {outputPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        object? model = null;
        object? sketchManager = null;
        object? featureManager = null;
        object? plane = null;
        object? feature = null;
        try
        {
            _log.Write("DESIGN", $"Create cylinder: diameter={spec.DiameterMm:g} mm, length={spec.LengthMm:g} mm");
            model = connection.CreateDocument(partTemplate, 0, 0d, 0d)
                ?? throw new InvalidOperationException("SolidWorks did not create a part document.");

            plane = FindFirstReferencePlane(model)
                ?? throw new InvalidOperationException("No reference plane was found in the new part.");
            if (!Convert.ToBoolean(ComDispatch.Call(plane, "Select2", false, 0)))
                throw new InvalidOperationException("The sketch plane could not be selected.");

            sketchManager = ComDispatch.Get(model, "SketchManager")
                ?? throw new InvalidOperationException("SketchManager is unavailable.");
            ComDispatch.Call(sketchManager, "InsertSketch", true);
            var circle = ComDispatch.Call(sketchManager, "CreateCircleByRadius", 0d, 0d, 0d, spec.DiameterMm / 2000d);
            if (circle is null) throw new InvalidOperationException("The circle sketch entity was not created.");
            ComDispatch.Release(circle);
            ComDispatch.Call(sketchManager, "InsertSketch", true);

            featureManager = ComDispatch.Get(model, "FeatureManager")
                ?? throw new InvalidOperationException("FeatureManager is unavailable.");
            feature = ComDispatch.Call(featureManager, "FeatureExtrusion2",
                true, false, false, 0, 0, spec.LengthMm / 1000d, 0d,
                false, false, false, false, 0d, 0d, false, false, false, false,
                true, true, true, 0, 0d, false)
                ?? throw new InvalidOperationException("The extrusion feature was not created.");

            var rebuildSucceeded = Convert.ToBoolean(ComDispatch.Call(model, "EditRebuild3"));
            if (!rebuildSucceeded) throw new InvalidOperationException("SolidWorks reported a rebuild failure.");

            var box = MeasurePartBox(model);
            GeometryValidator.ValidateCylinder(spec, box);
            var saved = Convert.ToBoolean(ComDispatch.Call(model, "SaveAs3", outputPath, 0, 1));
            if (!WaitForFile(outputPath, TimeSpan.FromSeconds(10)))
                throw new IOException($"The saved part did not appear on disk within 10 seconds (API returned {saved}): {outputPath}");
            var activePath = ComDispatch.Call(model, "GetPathName") as string;
            if (!string.Equals(Path.GetFullPath(activePath ?? string.Empty), outputPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"SolidWorks active document path does not match the requested save path: {activePath ?? "(empty)"}");
            if (!saved)
                _log.Write("CAD", "SaveAs3 returned false, but the file and active document path were both verified; accepting the observed result.");

            var featureName = ComDispatch.Get(feature, "Name") as string ?? "Boss-Extrude";
            _log.Write("CHECK", $"Cylinder measured {box.WidthMm:F3} x {box.HeightMm:F3} x {box.DepthMm:F3} mm; saved={outputPath}");
            return new PartCreationResult(outputPath, featureName, box, rebuildSucceeded);
        }
        catch (Exception ex)
        {
            _log.Write("ERROR", ex.Message);
            throw;
        }
        finally
        {
            ComDispatch.Release(feature);
            ComDispatch.Release(plane);
            ComDispatch.Release(featureManager);
            ComDispatch.Release(sketchManager);
            ComDispatch.Release(model);
        }
    }

    public PartCreationResult CreateSteppedShaft(SteppedShaftSpec spec, string outputPath, string partTemplate, bool overwrite = false)
    {
        spec.Validate();
        outputPath = ValidateOutput(outputPath, partTemplate, overwrite);
        object? model = null;
        object? sketchManager = null;
        object? featureManager = null;
        object? selectedPlaneOrFace = null;
        object? lastFeature = null;
        try
        {
            _log.Write("DESIGN", $"Create stepped shaft: {string.Join(", ", spec.Stages.Select(s => $"diameter={s.DiameterMm:g} x {s.LengthMm:g} mm"))}");
            model = connection.CreateDocument(partTemplate, 0, 0d, 0d)
                ?? throw new InvalidOperationException("SolidWorks did not create a part document.");
            sketchManager = ComDispatch.Get(model, "SketchManager")
                ?? throw new InvalidOperationException("SketchManager is unavailable.");
            featureManager = ComDispatch.Get(model, "FeatureManager")
                ?? throw new InvalidOperationException("FeatureManager is unavailable.");

            for (var index = 0; index < spec.Stages.Count; index++)
            {
                ComDispatch.Release(selectedPlaneOrFace);
                selectedPlaneOrFace = index == 0 ? FindFirstReferencePlane(model) : FindFarthestPlanarFace(model);
                if (selectedPlaneOrFace is null || !Convert.ToBoolean(ComDispatch.Call(selectedPlaneOrFace, "Select2", false, 0)))
                    throw new InvalidOperationException($"Could not select the sketch support for shaft stage {index + 1}.");

                var stage = spec.Stages[index];
                ComDispatch.Call(sketchManager, "InsertSketch", true);
                var circle = ComDispatch.Call(sketchManager, "CreateCircleByRadius", 0d, 0d, 0d, stage.DiameterMm / 2000d);
                if (circle is null) throw new InvalidOperationException($"The circle for shaft stage {index + 1} was not created.");
                ComDispatch.Release(circle);
                ComDispatch.Call(sketchManager, "InsertSketch", true);

                ComDispatch.Release(lastFeature);
                lastFeature = CreateExtrusion(featureManager, stage.LengthMm)
                    ?? throw new InvalidOperationException($"The extrusion for shaft stage {index + 1} was not created.");
                if (!Convert.ToBoolean(ComDispatch.Call(model, "EditRebuild3")))
                    throw new InvalidOperationException($"SolidWorks reported a rebuild failure at shaft stage {index + 1}.");
            }

            var box = MeasurePartBox(model);
            var expected = new[] { spec.Stages.Max(s => s.DiameterMm), spec.Stages.Max(s => s.DiameterMm), spec.Stages.Sum(s => s.LengthMm) }.Order().ToArray();
            var actual = new[] { box.WidthMm, box.HeightMm, box.DepthMm }.Order().ToArray();
            if (actual.Where((value, i) => Math.Abs(value - expected[i]) > 0.02).Any())
                throw new InvalidOperationException($"Generated shaft bounds are invalid: {box.WidthMm:F3} x {box.HeightMm:F3} x {box.DepthMm:F3} mm.");

            SaveAndVerify(model, outputPath);
            var featureName = lastFeature is null ? "Shaft" : ComDispatch.Get(lastFeature, "Name") as string ?? "Shaft";
            _log.Write("CHECK", $"Stepped shaft measured {box.WidthMm:F3} x {box.HeightMm:F3} x {box.DepthMm:F3} mm; saved={outputPath}");
            return new PartCreationResult(outputPath, featureName, box, true);
        }
        catch (Exception ex)
        {
            _log.Write("ERROR", ex.Message);
            throw;
        }
        finally
        {
            ComDispatch.Release(lastFeature);
            ComDispatch.Release(selectedPlaneOrFace);
            ComDispatch.Release(featureManager);
            ComDispatch.Release(sketchManager);
            ComDispatch.Release(model);
        }
    }

    public PartCreationResult CreateTube(TubeSpec spec, string outputPath, string partTemplate, bool overwrite = false)
    {
        spec.Validate();
        outputPath = ValidateOutput(outputPath, partTemplate, overwrite);
        object? model = null;
        object? sketchManager = null;
        object? featureManager = null;
        object? plane = null;
        object? feature = null;
        try
        {
            _log.Write("DESIGN", $"Create tube: outer={spec.OuterDiameterMm:g} mm, inner={spec.InnerDiameterMm:g} mm, length={spec.LengthMm:g} mm");
            model = connection.CreateDocument(partTemplate, 0, 0d, 0d)
                ?? throw new InvalidOperationException("SolidWorks did not create a part document.");
            plane = FindFirstReferencePlane(model) ?? throw new InvalidOperationException("No reference plane was found.");
            if (!Convert.ToBoolean(ComDispatch.Call(plane, "Select2", false, 0))) throw new InvalidOperationException("The sketch plane could not be selected.");
            sketchManager = ComDispatch.Get(model, "SketchManager") ?? throw new InvalidOperationException("SketchManager is unavailable.");
            featureManager = ComDispatch.Get(model, "FeatureManager") ?? throw new InvalidOperationException("FeatureManager is unavailable.");
            ComDispatch.Call(sketchManager, "InsertSketch", true);
            var outer = ComDispatch.Call(sketchManager, "CreateCircleByRadius", 0d, 0d, 0d, spec.OuterDiameterMm / 2000d);
            var inner = ComDispatch.Call(sketchManager, "CreateCircleByRadius", 0d, 0d, 0d, spec.InnerDiameterMm / 2000d);
            if (outer is null || inner is null) throw new InvalidOperationException("The annular sketch was not created.");
            ComDispatch.Release(outer);
            ComDispatch.Release(inner);
            ComDispatch.Call(sketchManager, "InsertSketch", true);
            feature = CreateExtrusion(featureManager, spec.LengthMm) ?? throw new InvalidOperationException("The tube extrusion was not created.");
            if (!Convert.ToBoolean(ComDispatch.Call(model, "EditRebuild3"))) throw new InvalidOperationException("SolidWorks reported a rebuild failure.");
            var box = MeasurePartBox(model);
            var expected = new[] { spec.OuterDiameterMm, spec.OuterDiameterMm, spec.LengthMm }.Order().ToArray();
            var actual = new[] { box.WidthMm, box.HeightMm, box.DepthMm }.Order().ToArray();
            if (actual.Where((value, i) => Math.Abs(value - expected[i]) > 0.02).Any())
                throw new InvalidOperationException($"Generated tube bounds are invalid: {box.WidthMm:F3} x {box.HeightMm:F3} x {box.DepthMm:F3} mm.");
            SaveAndVerify(model, outputPath);
            var featureName = ComDispatch.Get(feature, "Name") as string ?? "Tube";
            _log.Write("CHECK", $"Tube measured {box.WidthMm:F3} x {box.HeightMm:F3} x {box.DepthMm:F3} mm; saved={outputPath}");
            return new PartCreationResult(outputPath, featureName, box, true);
        }
        finally
        {
            ComDispatch.Release(feature);
            ComDispatch.Release(plane);
            ComDispatch.Release(featureManager);
            ComDispatch.Release(sketchManager);
            ComDispatch.Release(model);
        }
    }

    private static object? CreateExtrusion(object featureManager, double lengthMm) =>
        ComDispatch.Call(featureManager, "FeatureExtrusion2",
            true, false, false, 0, 0, lengthMm / 1000d, 0d,
            false, false, false, false, 0d, 0d, false, false, false, false,
            true, true, true, 0, 0d, false);

    private static object? FindFarthestPlanarFace(object model)
    {
        object? bestFace = null;
        var bestDistance = double.NegativeInfinity;
        var axis = GetFirstCylinderAxis(model) ?? new[] { 1d, 0d, 0d };
        var bodies = ComDispatch.Call(model, "GetBodies2", 0, true) as Array;
        if (bodies is null) return null;
        foreach (var body in bodies)
        {
            if (body is null) continue;
            try
            {
                var faces = ComDispatch.Call(body, "GetFaces") as Array;
                if (faces is null) continue;
                foreach (var face in faces)
                {
                    if (face is null) continue;
                    object? surface = null;
                    var keep = false;
                    try
                    {
                        surface = ComDispatch.Call(face, "GetSurface");
                        if (surface is null || !Convert.ToBoolean(ComDispatch.Call(surface, "IsPlane"))) continue;
                        var bounds = ComDispatch.Call(face, "GetBox") as Array;
                        if (bounds is null || bounds.Length < 6) continue;
                        // Use the actual face bounding-box centre. PlaneParams can
                        // report a parameterization origin unrelated to the face's
                        // trimmed location, which selected an earlier shoulder.
                        var distance = Math.Abs(Enumerable.Range(0, 3).Sum(i =>
                            (Convert.ToDouble(bounds.GetValue(i)) + Convert.ToDouble(bounds.GetValue(i + 3))) * 0.5d * axis[i]));
                        if (distance > bestDistance)
                        {
                            ComDispatch.Release(bestFace);
                            bestFace = face;
                            bestDistance = distance;
                            keep = true;
                        }
                    }
                    finally
                    {
                        ComDispatch.Release(surface);
                        if (!keep) ComDispatch.Release(face);
                    }
                }
            }
            finally { ComDispatch.Release(body); }
        }
        return bestFace;
    }

    private static double[]? GetFirstCylinderAxis(object model)
    {
        var bodies = ComDispatch.Call(model, "GetBodies2", 0, true) as Array;
        if (bodies is null) return null;
        foreach (var body in bodies)
        {
            if (body is null) continue;
            try
            {
                var faces = ComDispatch.Call(body, "GetFaces") as Array;
                if (faces is null) continue;
                foreach (var face in faces)
                {
                    if (face is null) continue;
                    object? surface = null;
                    try
                    {
                        surface = ComDispatch.Call(face, "GetSurface");
                        if (surface is null || !Convert.ToBoolean(ComDispatch.Call(surface, "IsCylinder"))) continue;
                        var values = ComDispatch.Get(surface, "CylinderParams") as Array;
                        if (values is not null && values.Length >= 6)
                            return [Convert.ToDouble(values.GetValue(3)), Convert.ToDouble(values.GetValue(4)), Convert.ToDouble(values.GetValue(5))];
                    }
                    finally
                    {
                        ComDispatch.Release(surface);
                        ComDispatch.Release(face);
                    }
                }
            }
            finally { ComDispatch.Release(body); }
        }
        return null;
    }

    private string ValidateOutput(string outputPath, string partTemplate, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(partTemplate)) throw new FileNotFoundException("SolidWorks part template was not found.", partTemplate);
        outputPath = Path.GetFullPath(outputPath);
        if (!outputPath.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output path must use .SLDPRT.");
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Refusing to overwrite existing CAD file: {outputPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        return outputPath;
    }

    private void SaveAndVerify(object model, string outputPath)
    {
        var saved = Convert.ToBoolean(ComDispatch.Call(model, "SaveAs3", outputPath, 0, 1));
        if (!WaitForFile(outputPath, TimeSpan.FromSeconds(10)))
            throw new IOException($"The saved part did not appear on disk within 10 seconds (API returned {saved}): {outputPath}");
        var activePath = ComDispatch.Call(model, "GetPathName") as string;
        if (!string.Equals(Path.GetFullPath(activePath ?? string.Empty), outputPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"SolidWorks active document path does not match the requested save path: {activePath ?? "(empty)"}");
        if (!saved) _log.Write("CAD", "SaveAs3 returned false, but file and active document path verification succeeded.");
    }

    private static object? FindFirstReferencePlane(object model)
    {
        var current = ComDispatch.Call(model, "FirstFeature");
        while (current is not null)
        {
            var typeName = ComDispatch.Call(current, "GetTypeName2") as string;
            if (string.Equals(typeName, "RefPlane", StringComparison.OrdinalIgnoreCase)) return current;
            var next = ComDispatch.Call(current, "GetNextFeature");
            ComDispatch.Release(current);
            current = next;
        }
        return null;
    }

    private static BoundingBox MeasurePartBox(object model)
    {
        var values = ComDispatch.Call(model, "GetPartBox", true) as Array
            ?? throw new InvalidOperationException("SolidWorks did not return a part bounding box.");
        if (values.Length < 6) throw new InvalidOperationException("Invalid part bounding box returned by SolidWorks.");
        var x = Math.Abs(Convert.ToDouble(values.GetValue(3)) - Convert.ToDouble(values.GetValue(0))) * 1000d;
        var y = Math.Abs(Convert.ToDouble(values.GetValue(4)) - Convert.ToDouble(values.GetValue(1))) * 1000d;
        var z = Math.Abs(Convert.ToDouble(values.GetValue(5)) - Convert.ToDouble(values.GetValue(2))) * 1000d;
        return new BoundingBox(x, y, z);
    }

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (File.Exists(path)) return true;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        return File.Exists(path);
    }
}
