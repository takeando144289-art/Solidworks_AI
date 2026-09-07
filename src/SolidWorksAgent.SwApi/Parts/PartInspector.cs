using SolidWorksAgent.Core.Models;
using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi.Parts;

public sealed class PartInspector(SwConnection connection)
{
    public PartInspectionResult InspectFile(string path)
    {
        object?[] args = [Path.GetFullPath(path), 1, 1, "", 0, 0];
        var model = connection.OpenDocument(args)
            ?? throw new InvalidOperationException($"Cannot open part: {path} (error {args[4]}).");
        object?[] activate = [ComDispatch.Call(model, "GetTitle"), false, 0, 0];
        ComDispatch.CallWithByRef(connection.Application, "ActivateDoc3", activate, 3);
        return InspectActivePart();
    }

    public PartInspectionResult InspectActivePart()
    {
        var model = ComDispatch.Get(connection.Application, "ActiveDoc")
            ?? throw new InvalidOperationException("There is no active SolidWorks document.");
        try
        {
            var documentType = Convert.ToInt32(ComDispatch.Call(model, "GetType"));
            if (documentType != 1) throw new InvalidOperationException("The active document is not a Part.");

            var title = ComDispatch.Call(model, "GetTitle") as string ?? "(untitled)";
            var path = ComDispatch.Call(model, "GetPathName") as string;
            var box = GetBoundingBox(model);
            return new PartInspectionResult(
                title,
                string.IsNullOrWhiteSpace(path) ? null : path,
                GetMaterial(model),
                box,
                GetFeatures(model),
                GetCylinders(model),
                GetMassProperties(model));
        }
        finally { ComDispatch.Release(model); }
    }

    private static IReadOnlyList<FeatureInfo> GetFeatures(object model)
    {
        var result = new List<FeatureInfo>();
        var current = ComDispatch.Call(model, "FirstFeature");
        while (current is not null)
        {
            var next = default(object);
            try
            {
                var name = ComDispatch.Get(current, "Name") as string ?? "(unnamed)";
                var type = ComDispatch.Call(current, "GetTypeName2") as string ?? "Unknown";
                result.Add(new FeatureInfo(name, type));
                next = ComDispatch.Call(current, "GetNextFeature");
            }
            finally { ComDispatch.Release(current); }
            current = next;
        }
        return result;
    }

    private static IReadOnlyList<CylindricalFaceInfo> GetCylinders(object model)
    {
        var result = new List<CylindricalFaceInfo>();
        var bodies = ComDispatch.Call(model, "GetBodies2", 0, true) as Array;
        if (bodies is null) return result;
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
                        var parameters = ComDispatch.Get(surface, "CylinderParams") as Array;
                        if (parameters is null || parameters.Length < 7) continue;
                        var uv = ComDispatch.Call(face, "GetUVBounds") as Array;
                        var lengthMm = uv is not null && uv.Length >= 4
                            ? Math.Abs(Convert.ToDouble(uv.GetValue(3)) - Convert.ToDouble(uv.GetValue(2))) * 1000d
                            : 0d;
                        result.Add(new CylindricalFaceInfo(
                            GetPersistentId(model, face),
                            Convert.ToDouble(parameters.GetValue(6)) * 2000d,
                            lengthMm,
                            new Point3(
                                Convert.ToDouble(parameters.GetValue(0)) * 1000d,
                                Convert.ToDouble(parameters.GetValue(1)) * 1000d,
                                Convert.ToDouble(parameters.GetValue(2)) * 1000d),
                            new Point3(
                                Convert.ToDouble(parameters.GetValue(3)),
                                Convert.ToDouble(parameters.GetValue(4)),
                                Convert.ToDouble(parameters.GetValue(5)))));
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
        return result;
    }

    private static string GetPersistentId(object model, object face)
    {
        object? extension = null;
        try
        {
            extension = ComDispatch.Get(model, "Extension");
            var bytes = extension is null ? null : ComDispatch.Call(extension, "GetPersistReference3", face) as byte[];
            return bytes is null ? "unavailable" : Convert.ToBase64String(bytes);
        }
        catch { return "unavailable"; }
        finally { ComDispatch.Release(extension); }
    }

    private static MassProperties? GetMassProperties(object model)
    {
        object? extension = null;
        object? massProperty = null;
        try
        {
            extension = ComDispatch.Get(model, "Extension");
            massProperty = extension is null ? null : ComDispatch.Call(extension, "CreateMassProperty");
            if (massProperty is null) return null;
            var center = ComDispatch.Get(massProperty, "CenterOfMass") as Array;
            if (center is null || center.Length < 3) return null;
            return new MassProperties(
                Convert.ToDouble(ComDispatch.Get(massProperty, "Mass")),
                Convert.ToDouble(ComDispatch.Get(massProperty, "Volume")) * 1_000_000_000d,
                Convert.ToDouble(ComDispatch.Get(massProperty, "SurfaceArea")) * 1_000_000d,
                new Point3(
                    Convert.ToDouble(center.GetValue(0)) * 1000d,
                    Convert.ToDouble(center.GetValue(1)) * 1000d,
                    Convert.ToDouble(center.GetValue(2)) * 1000d));
        }
        catch { return null; }
        finally
        {
            ComDispatch.Release(massProperty);
            ComDispatch.Release(extension);
        }
    }

    private static string? GetMaterial(object model)
    {
        object? configurationManager = null;
        object? configuration = null;
        try
        {
            configurationManager = ComDispatch.Get(model, "ConfigurationManager");
            configuration = configurationManager is null ? null : ComDispatch.Get(configurationManager, "ActiveConfiguration");
            var configurationName = configuration is null ? null : ComDispatch.Get(configuration, "Name") as string;
            if (string.IsNullOrWhiteSpace(configurationName)) return null;
            object?[] arguments = [configurationName, string.Empty];
            var material = ComDispatch.Call(model, "GetMaterialPropertyName2", arguments) as string;
            return string.IsNullOrWhiteSpace(material) ? null : material;
        }
        catch { return null; }
        finally
        {
            ComDispatch.Release(configuration);
            ComDispatch.Release(configurationManager);
        }
    }

    private static BoundingBox GetBoundingBox(object model)
    {
        var values = ComDispatch.Call(model, "GetPartBox", true) as Array
            ?? throw new InvalidOperationException("SolidWorks did not return a part bounding box.");
        return new BoundingBox(
            Math.Abs(Convert.ToDouble(values.GetValue(3)) - Convert.ToDouble(values.GetValue(0))) * 1000d,
            Math.Abs(Convert.ToDouble(values.GetValue(4)) - Convert.ToDouble(values.GetValue(1))) * 1000d,
            Math.Abs(Convert.ToDouble(values.GetValue(5)) - Convert.ToDouble(values.GetValue(2))) * 1000d);
    }
}
