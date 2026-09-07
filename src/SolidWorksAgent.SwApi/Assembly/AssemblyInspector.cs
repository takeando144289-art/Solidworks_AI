using SolidWorksAgent.Core.Models;
using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi.Assembly;

public sealed class AssemblyInspector(SwConnection connection)
{
    public AssemblyAnalysisResult AnalyzeActiveAssembly()
    {
        var model = ComDispatch.Get(connection.Application, "ActiveDoc")
            ?? throw new InvalidOperationException("There is no active SolidWorks document.");
        try
        {
            if (Convert.ToInt32(ComDispatch.Call(model, "GetType")) != 2)
                throw new InvalidOperationException("The active document is not an Assembly.");
            ComDispatch.Call(model, "EditRebuild3");
            ComDispatch.Call(model, "UpdateBox");
            var title = ComDispatch.Call(model, "GetTitle") as string ?? "(untitled)";
            var path = ComDispatch.Call(model, "GetPathName") as string;
            return new AssemblyAnalysisResult(
                title,
                string.IsNullOrWhiteSpace(path) ? null : path,
                GetBoundingBox(model),
                GetMassProperties(model),
                GetComponents(model),
                GetInterferences(model));
        }
        finally { ComDispatch.Release(model); }
    }

    private static BoundingBox GetBoundingBox(object assembly)
    {
        var values = ComDispatch.Call(assembly, "GetBox", 0) as Array
            ?? throw new InvalidOperationException("SolidWorks did not return the assembly bounding box.");
        return new BoundingBox(
            Math.Abs(Convert.ToDouble(values.GetValue(3)) - Convert.ToDouble(values.GetValue(0))) * 1000d,
            Math.Abs(Convert.ToDouble(values.GetValue(4)) - Convert.ToDouble(values.GetValue(1))) * 1000d,
            Math.Abs(Convert.ToDouble(values.GetValue(5)) - Convert.ToDouble(values.GetValue(2))) * 1000d);
    }

    private static IReadOnlyList<ComponentInfo> GetComponents(object assembly)
    {
        var values = ComDispatch.Call(assembly, "GetComponents", false) as Array;
        if (values is null) return [];
        var result = new List<ComponentInfo>();
        foreach (var component in values)
        {
            if (component is null) continue;
            try
            {
                var name = ComDispatch.Get(component, "Name2") as string ?? "(unnamed)";
                var path = ComDispatch.Call(component, "GetPathName") as string ?? string.Empty;
                var fixedState = Convert.ToBoolean(ComDispatch.Call(component, "IsFixed"));
                result.Add(new ComponentInfo(name, path, fixedState));
            }
            finally { ComDispatch.Release(component); }
        }
        return result;
    }

    private static IReadOnlyList<InterferenceInfo> GetInterferences(object assembly)
    {
        object? manager = null;
        try
        {
            manager = ComDispatch.Get(assembly, "InterferenceDetectionManager")
                ?? throw new InvalidOperationException("InterferenceDetectionManager is unavailable.");
            var values = ComDispatch.Call(manager, "GetInterferences") as Array;
            if (values is null) return [];
            var result = new List<InterferenceInfo>();
            foreach (var interference in values)
            {
                if (interference is null) continue;
                try
                {
                    var components = ComDispatch.Get(interference, "Components") as Array;
                    var componentNames = new List<string>();
                    if (components is not null)
                    {
                        foreach (var component in components)
                        {
                            if (component is null) continue;
                            try { componentNames.Add(ComDispatch.Get(component, "Name2") as string ?? "(unnamed)"); }
                            finally { ComDispatch.Release(component); }
                        }
                    }
                    result.Add(new InterferenceInfo(
                        componentNames,
                        Convert.ToDouble(ComDispatch.Get(interference, "Volume")) * 1_000_000_000d,
                        Convert.ToBoolean(ComDispatch.Get(interference, "IsPossibleInterference")),
                        Convert.ToBoolean(ComDispatch.Get(interference, "IsFastener"))));
                }
                finally { ComDispatch.Release(interference); }
            }
            return result;
        }
        finally { ComDispatch.Release(manager); }
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
        finally
        {
            ComDispatch.Release(massProperty);
            ComDispatch.Release(extension);
        }
    }
}
