using SolidWorksAgent.Core.Logging;
using SolidWorksAgent.SwApi.Interop;
using SolidWorksAgent.SwApi.Parts;

namespace SolidWorksAgent.SwApi.Drawing;

public sealed class DrawingBuilder(SwConnection connection, IAgentLog? log = null)
{
    private readonly IAgentLog _log = log ?? new NullAgentLog();
    private sealed record Ring(object Edge, double[] Center, double DiameterMm);
    public sealed record DimensionCheck(string Name, double ValueMm, int AttachedEntities, bool Dangling, bool Overridden);

    public object InspectDrawing(string path)
    {
        object?[] args = [Path.GetFullPath(path), 3, 1, "", 0, 0];
        var drawing = connection.OpenDocument(args)
            ?? throw new InvalidOperationException("Cannot open drawing for inspection.");
        Rebuild(drawing);
        var views = GetModelViews(drawing);
        views.Insert(0, ComDispatch.Call(drawing, "GetFirstView")!);
        return views.Select(v => new {
            View = Name(v),
            Notes = Items(ComDispatch.Call(v, "GetNotes")).Select(n => {
                var a = ComDispatch.Call(n, "GetAnnotation")!;
                return new { Text = ComDispatch.Call(n, "GetText"), Position = Numbers(ComDispatch.Call(a, "GetPosition")), Layer = ComDispatch.Get(a, "Layer"), Visible = ComDispatch.Get(a, "Visible") };
            }).ToArray(),
            Dimensions = Items(ComDispatch.Call(v, "GetDisplayDimensions")).Select(d => {
                var dim = ComDispatch.Call(d, "GetDimension2", 0)!;
                return Check(d, (string)ComDispatch.Get(dim, "FullName")!, Convert.ToDouble(ComDispatch.Get(dim, "SystemValue"))*1000);
            }).ToArray()
        }).ToArray();
    }

    public object CreateShaftDrawing(string modelPath, string drawingPath, string pdfPath,
        string drawingTemplate, double? lengthMm = null, double? diameterMm = null,
        double? toleranceMm = null, bool overwrite = false)
    {
        modelPath = Path.GetFullPath(modelPath);
        foreach (var path in new[] { modelPath, drawingTemplate })
            if (!File.Exists(path)) throw new FileNotFoundException("Drawing input not found.", path);
        drawingPath = ValidateOutput(drawingPath, ".SLDDRW", overwrite);
        pdfPath = ValidateOutput(pdfPath, ".PDF", overwrite);
        if (toleranceMm is not null && (!double.IsFinite(toleranceMm.Value) || toleranceMm <= 0))
            throw new ArgumentException("Tolerance must be finite and positive.");
        var inspection = new PartInspector(connection).InspectFile(modelPath);
        var model = ComDispatch.Get(connection.Application, "ActiveDoc")!;
        var bodies = Items(ComDispatch.Call(model, "GetBodies2", 0, true));
        if (bodies.Length != 1) throw new InvalidOperationException("A single solid shaft is required.");
        var rings = new List<Ring>();
        foreach (var face in Items(ComDispatch.Call(bodies[0], "GetFaces")))
        {
            var surface = ComDispatch.Call(face, "GetSurface")!;
            if (!Convert.ToBoolean(ComDispatch.Call(surface, "IsCylinder")) && !Convert.ToBoolean(ComDispatch.Call(surface, "IsPlane")))
                throw new InvalidOperationException("This shaft drawing planner supports plain cylindrical stages only; additional features require their own dimension plan.");
        }
        foreach (var edge in Items(ComDispatch.Call(bodies[0], "GetEdges")))
        {
            var curve = ComDispatch.Call(edge, "GetCurve")!;
            if (!Convert.ToBoolean(ComDispatch.Call(curve, "IsCircle"))) continue;
            var p = Numbers(ComDispatch.Get(curve, "CircleParams"));
            rings.Add(new Ring(edge, p.Take(3).ToArray(), p[6] * 2000));
        }
        if (rings.Count == 0) throw new InvalidOperationException("No cylindrical boundary edges found.");
        // The supported generated shafts are coaxial with a principal model axis.
        int axis = Enumerable.Range(0, 3).MaxBy(i => rings.Max(r => r.Center[i]) - rings.Min(r => r.Center[i]));
        var stations = rings.GroupBy(r => Math.Round(r.Center[axis], 8)).OrderBy(g => g.Key).ToArray();
        double measuredLength = (stations.Last().Key - stations.First().Key) * 1000;
        double measuredDiameter = rings.Max(r => r.DiameterMm);
        if (Enumerable.Range(0, 3).Where(i => i != axis)
            .Any(i => rings.Max(r => r.Center[i]) - rings.Min(r => r.Center[i]) > 1e-7))
            throw new InvalidOperationException("The stages must share a principal axis.");
        if (measuredLength > 40 || measuredDiameter > 20)
            throw new InvalidOperationException("Shaft exceeds the validated A4 3:1 layout envelope (length 40 mm, diameter 20 mm).");
        if (stations.Length != inspection.Cylinders.Count + 1 || inspection.Cylinders.Count != 3 || rings.Count != 6)
            throw new InvalidOperationException("Expected a plain three-stage solid shaft with six circular boundary edges.");
        var expectedVolume = inspection.Cylinders.Sum(c => Math.PI * c.DiameterMm * c.DiameterMm / 4 * c.LengthMm);
        if (inspection.MassProperties is null || Math.Abs(inspection.MassProperties.VolumeMm3 - expectedVolume) > 0.001)
            throw new InvalidOperationException("Shaft contains geometry not covered by the cylindrical dimension plan.");
        if (lengthMm is double l && (!double.IsFinite(l) || Math.Abs(l - measuredLength) > 0.001) ||
            diameterMm is double d && (!double.IsFinite(d) || Math.Abs(d - measuredDiameter) > 0.001))
            throw new ArgumentException("Requested size disagrees with model geometry; numeric overrides are prohibited.");

        var drawing = connection.CreateDocument(drawingTemplate, 0, 0d, 0d)
            ?? throw new InvalidOperationException("Cannot create drawing.");
        var sheet = ComDispatch.Call(drawing, "GetCurrentSheet")!;
        if (!Convert.ToBoolean(ComDispatch.Call(sheet, "SetSize", 6, 0.297d, 0.210d)))
            throw new InvalidOperationException("Cannot set A4 drawing sheet.");
        if (!Convert.ToBoolean(ComDispatch.Call(drawing, "Create3rdAngleViews2", modelPath)))
            throw new InvalidOperationException("Cannot create model views.");
        var views = GetModelViews(drawing);
        var main = views.MaxBy(v => { var b = Numbers(ComDispatch.Call(v, "GetOutline")); return b[2]-b[0]; })!;
        var extension = ComDispatch.Get(drawing, "Extension")!;
        foreach (var view in views.Where(v => !ReferenceEquals(v, main)))
        {
            ComDispatch.Call(drawing, "ClearSelection2", true);
            if (!Convert.ToBoolean(ComDispatch.Call(extension, "SelectByID2", Name(view), "DRAWINGVIEW", 0d, 0d, 0d, false, 0, new System.Runtime.InteropServices.DispatchWrapper(null), 0)))
                throw new InvalidOperationException("Cannot select surplus projection.");
            if (!Convert.ToBoolean(ComDispatch.Call(extension, "DeleteSelection2", 0)))
                throw new InvalidOperationException("Cannot remove surplus projection.");
        }
        ComDispatch.Call(drawing, "ClearSelection2", true);
        ComDispatch.Call(main, "RemoveAlignment");
        ComDispatch.Set(main, "UseSheetScale", false);
        ComDispatch.Set(main, "ScaleDecimal", 3d);
        ComDispatch.Set(main, "Position", new[] { 0.110d, 0.115d });
        Rebuild(drawing);
        var math = ComDispatch.Call(connection.Application, "GetMathUtility")!;
        double[] Project(Ring r)
        {
            var point = ComDispatch.Call(math, "CreatePoint", r.Center)!;
            var transformed = ComDispatch.Call(point, "MultiplyTransform", ComDispatch.Get(main, "ModelToViewTransform"))!;
            return Numbers(ComDispatch.Get(transformed, "ArrayData"));
        }
        if (Project(stations[0].First())[0] > Project(stations[^1].First())[0])
        {
            ComDispatch.Set(main, "Angle", Convert.ToDouble(ComDispatch.Get(main, "Angle")) + Math.PI);
            Rebuild(drawing);
        }
        ComDispatch.Call(drawing, "ClearSelection2", true);
        ComDispatch.Call(extension, "SelectByID2", Name(main), "DRAWINGVIEW", 0d, 0d, 0d, false, 0,
            new System.Runtime.InteropServices.DispatchWrapper(null), 0);
        var end = ComDispatch.Call(drawing, "CreateUnfoldedViewAt3", 0.235d, 0.115d, 0d, false)
            ?? throw new InvalidOperationException("Cannot create aligned end projection.");
        foreach (var face in Items(ComDispatch.Call(bodies[0], "GetFaces")))
        {
            var surface = ComDispatch.Call(face, "GetSurface")!;
            if (!Convert.ToBoolean(ComDispatch.Call(surface, "IsCylinder"))) continue;
            Select(drawing, main, face, false);
            if (ComDispatch.Call(drawing, "InsertCenterLine2") is null)
                throw new InvalidOperationException("Cannot create cylinder centerline.");
        }
        var checks = new List<DimensionCheck>();
        var dimensions = new List<(object Display, string Label, double Expected)>();
        var baseRing = stations[0].OrderByDescending(r => r.DiameterMm).First();
        for (int i = 1; i < stations.Length; i++)
        {
            var target = stations[i].OrderByDescending(r => r.DiameterMm).First();
            var a = Project(baseRing); var b = Project(target);
            Select(drawing, main, baseRing.Edge, false);
            Select(drawing, main, target.Edge, true);
            var display = ComDispatch.Call(drawing, "AddHorizontalDimension2", (a[0]+b[0])/2, 0.080d-i*0.012d, 0d)
                ?? throw new InvalidOperationException($"Cannot dimension axial station {i}.");
            dimensions.Add((display, i == stations.Length-1 ? "Overall length" : $"Shoulder {i} from left end", (stations[i].Key-stations[0].Key)*1000));
        }
        foreach (var group in rings.GroupBy(r => Math.Round(r.DiameterMm, 6)).OrderByDescending(g => g.Key))
        {
            var ring = group.OrderBy(r => r.Center[axis]).First();
            Select(drawing, main, ring.Edge, false);
            var a = Project(ring);
            var display = ComDispatch.Call(drawing, "AddDimension2", a[0] + 0.012d, 0.158d, 0d)
                ?? throw new InvalidOperationException($"Cannot dimension diameter {group.Key}.");
            dimensions.Add((display, $"Diameter {group.Key:g}", group.Key));
        }
        ComDispatch.Call(drawing, "ClearSelection2", true);
        ComDispatch.Call(drawing, "ActivateView", "");
        ComDispatch.Call(drawing, "ActivateSheet", ComDispatch.Call(sheet, "GetName"));
        AddNote(drawing, $"STEPPED SHAFT - {Path.GetFileNameWithoutExtension(modelPath)}", 0.020, 0.195);
        AddNote(drawing, "UNITS: mm   THIRD ANGLE   SCALE 3:1", 0.020, 0.183);
        AddNote(drawing, "AXIAL ORIGIN: LEFT END FACE\nSOLID SHAFT - NO SECTION REQUIRED", 0.020, 0.027);
        var toleranceNote = toleranceMm.HasValue ? $"DIMENSION TOLERANCE: +/-{toleranceMm.Value:g} mm" : "DIMENSION TOLERANCES: TBD";
        AddNote(drawing, $"MATERIAL: {inspection.Material ?? "NOT SPECIFIED"}   {toleranceNote}\nFITS / FINISH / HEAT TREATMENT: TBD\nCHAMFERS / SHOULDER RADII: TBD\nDEFINE ABOVE REQUIREMENTS BEFORE MANUFACTURE", 0.135, 0.027);
        Rebuild(drawing);
        foreach (var item in dimensions)
        {
            if (toleranceMm is double tol) ApplyTolerance(item.Display, tol);
            checks.Add(Check(item.Display, item.Label, item.Expected));
        }
        ComDispatch.Call(drawing, "GraphicsRedraw2");
        Save(drawing, drawingPath, true);
        var viewNames = new[] { Name(main), Name(end) };
        ComDispatch.Call(connection.Application, "CloseDoc", ComDispatch.Call(drawing, "GetTitle"));
        object?[] reopen = [drawingPath, 3, 1, "", 0, 0];
        drawing = connection.OpenDocument(reopen)
            ?? throw new InvalidOperationException("Cannot reopen saved drawing.");
        Rebuild(drawing);
        var persisted = GetModelViews(drawing).SelectMany(v => Items(ComDispatch.Call(v, "GetDisplayDimensions"))).ToArray();
        if (persisted.Length != checks.Count) throw new InvalidOperationException("Saved dimension count changed.");
        for (int i = 0; i < persisted.Length; i++) Check(persisted[i], checks[i].Name, checks[i].ValueMm);
        Save(drawing, pdfPath, false);
        ComDispatch.Call(drawing, "ViewZoomtofit2");
        _log.Write("CHECK", $"Saved associative shaft drawing: {drawingPath}; {checks.Count} checked dimensions.");
        return new { DrawingPath = drawingPath, PdfPath = pdfPath, ModelPath = modelPath,
            Views = viewNames, Scale = "3:1", SectionUsed = false,
            Dimensions = checks, RebuildSucceeded = true, ToleranceApplied = toleranceMm.HasValue,
            ManufacturingSpecificationsComplete = false };
    }

    private static void Select(object drawing, object view, object entity, bool append)
    {
        if (!append) ComDispatch.Call(drawing, "ClearSelection2", true);
        ComDispatch.Call(drawing, "ActivateView", Name(view));
        if (!Convert.ToBoolean(ComDispatch.Call(view, "SelectEntity", entity, append)))
            throw new InvalidOperationException("Model edge selection in drawing failed.");
    }
    private static DimensionCheck Check(object display, string label, double expected)
    {
        var dimension = ComDispatch.Call(display, "GetDimension2", 0)!;
        double value = Convert.ToDouble(ComDispatch.Get(dimension, "SystemValue"))*1000;
        var annotation = ComDispatch.Call(display, "GetAnnotation")!;
        bool dangling = Convert.ToBoolean(ComDispatch.Call(annotation, "IsDangling"));
        bool overridden = Convert.ToBoolean(ComDispatch.Call(display, "GetOverride"));
        int attached = Items(ComDispatch.Call(annotation, "GetAttachedEntities3")).Length;
        if (dangling || overridden || attached == 0 || Math.Abs(value-expected)>0.001)
            throw new InvalidOperationException($"Invalid dimension {label}: value={value}, expected={expected}, attached={attached}, dangling={dangling}, overridden={overridden}.");
        return new DimensionCheck(label, value, attached, dangling, overridden);
    }
    private static void ApplyTolerance(object display, double mm)
    {
        var dimension = ComDispatch.Call(display, "GetDimension2", 0)!;
        var tolerance = ComDispatch.Get(dimension, "Tolerance")!;
        ComDispatch.Set(tolerance, "Type", 4);
        if (!Convert.ToBoolean(ComDispatch.Call(tolerance, "SetValues", -mm/1000, mm/1000)))
            throw new InvalidOperationException("Tolerance could not be applied.");
    }
    private static void AddNote(object drawing, string text, double x, double y)
    {
        var note = ComDispatch.Call(drawing, "InsertNote", text) ?? throw new InvalidOperationException("Cannot create drawing note.");
        var annotation = ComDispatch.Call(note, "GetAnnotation")!;
        var format = ComDispatch.Call(annotation, "GetTextFormat", 0)!;
        ComDispatch.Set(format, "TypeFaceName", "Arial");
        ComDispatch.Set(format, "CharHeight", 0.003d);
        ComDispatch.Call(annotation, "SetTextFormat", 0, false, format);
        ComDispatch.Call(annotation, "SetPosition2", x, y, 0d);
        ComDispatch.Release(format);
        ComDispatch.Release(annotation);
        ComDispatch.Release(note);
    }
    private static void Rebuild(object doc)
    {
        if (!Convert.ToBoolean(ComDispatch.Call(doc, "EditRebuild3"))) throw new InvalidOperationException("Drawing rebuild failed.");
    }
    private static string Name(object view) => (string)ComDispatch.Get(view, "Name")!;
    private static object[] Items(object? value) => value is Array a ? a.Cast<object?>().Where(x=>x is not null).Cast<object>().ToArray() : [];
    private static double[] Numbers(object? value) => value is Array a ? a.Cast<object>().Select(Convert.ToDouble).ToArray() : throw new InvalidOperationException("Geometry array missing.");
    private static List<object> GetModelViews(object drawing)
    {
        var result = new List<object>();
        var view = ComDispatch.Call(ComDispatch.Call(drawing, "GetFirstView")!, "GetNextView");
        while (view is not null) { result.Add(view); view = ComDispatch.Call(view, "GetNextView"); }
        return result;
    }
    private static string ValidateOutput(string path, string extension, bool overwrite)
    {
        path = Path.GetFullPath(path);
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Expected {extension} output.");
        if (File.Exists(path) && !overwrite) throw new IOException($"Refusing to overwrite existing file: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
    private static void Save(object doc, string path, bool native)
    {
        var returned = ComDispatch.Call(doc, "SaveAs3", path, 0, 1);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException($"Save failed ({returned}): {path}");
        if (native && !string.Equals((string?)ComDispatch.Call(doc, "GetPathName"), path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Saved drawing path mismatch.");
    }
}



