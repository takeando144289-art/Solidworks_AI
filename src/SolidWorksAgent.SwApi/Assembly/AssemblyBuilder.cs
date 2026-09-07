using SolidWorksAgent.Core.Logging;
using SolidWorksAgent.Core.Models;
using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi.Assembly;

public sealed class AssemblyBuilder(SwConnection connection, IAgentLog? log = null)
{
    private readonly IAgentLog _log = log ?? new NullAgentLog();

    public AssemblyCreationResult CreateBearingTestAssembly(
        string housingPath,
        string bearingPath,
        string shaftPath,
        string outputPath,
        string assemblyTemplate,
        bool overwrite = false)
    {
        foreach (var path in new[] { housingPath, bearingPath, shaftPath, assemblyTemplate })
            if (!File.Exists(path)) throw new FileNotFoundException("Assembly input was not found.", path);
        outputPath = Path.GetFullPath(outputPath);
        if (!outputPath.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output path must use .SLDASM.");
        if (File.Exists(outputPath) && !overwrite) throw new IOException($"Refusing to overwrite existing CAD file: {outputPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        object? model = null;
        object? housing = null;
        object? bearing = null;
        object? shaft = null;
        var mateObjects = new List<object>();
        try
        {
            _log.Write("DESIGN", $"Create bearing test assembly: housing={housingPath}, bearing={bearingPath}, shaft={shaftPath}");
            model = connection.CreateDocument(assemblyTemplate, 0, 0d, 0d)
                ?? throw new InvalidOperationException("SolidWorks did not create an assembly document.");
            _log.Write("CAD", "Created new assembly document.");

            housing = InsertComponent(model, housingPath);
            _log.Write("CAD", "Inserted Housing component.");
            bearing = InsertComponent(model, bearingPath);
            _log.Write("CAD", "Inserted Bearing component.");
            shaft = InsertComponent(model, shaftPath);
            _log.Write("CAD", "Inserted Shaft component.");
            FixComponent(model, housing);
            _log.Write("CAD", "Fixed Housing component.");

            mateObjects.Add(AddConcentricMate(model, housing, 22, bearing, 22, "Housing", "Bearing"));
            _log.Write("CAD", "Added Housing/Bearing concentric mate.");
            mateObjects.Add(AddConcentricMate(model, shaft, 12, bearing, 12, "Shaft", "Bearing"));
            _log.Write("CAD", "Added Shaft/Bearing concentric mate.");

            var rebuilt = Convert.ToBoolean(ComDispatch.Call(model, "EditRebuild3"));
            if (!rebuilt) throw new InvalidOperationException("SolidWorks reported an assembly rebuild failure.");
            SaveAndVerify(model, outputPath);

            var components = new[]
            {
                ToComponentInfo(housing, housingPath),
                ToComponentInfo(bearing, bearingPath),
                ToComponentInfo(shaft, shaftPath)
            };
            var mates = new[]
            {
                new MateInfo(GetName(mateObjects[0]), "Concentric", "Housing", "Bearing"),
                new MateInfo(GetName(mateObjects[1]), "Concentric", "Shaft", "Bearing")
            };
            _log.Write("CHECK", $"Assembly rebuilt and saved with {components.Length} components and {mates.Length} concentric mates: {outputPath}");
            return new AssemblyCreationResult(outputPath, components, mates, rebuilt);
        }
        finally
        {
            foreach (var mate in mateObjects) ComDispatch.Release(mate);
            ComDispatch.Release(shaft);
            ComDispatch.Release(bearing);
            ComDispatch.Release(housing);
            ComDispatch.Release(model);
        }
    }

    private static object InsertComponent(object assembly, string path)
    {
        path = Path.GetFullPath(path);
        object? component;
        try
        {
            component = ComDispatch.Call(assembly, "AddComponent5", path, 0, string.Empty, false, string.Empty, 0d, 0d, 0d);
        }
        catch
        {
            // AddComponent5 in SOLIDWORKS Connected exposes a different IDispatch
            // signature. The parts are already loaded, so AddComponent4 is the
            // compatible official API for this operation.
            component = ComDispatch.Call(assembly, "AddComponent4", path, string.Empty, 0d, 0d, 0d);
        }
        return component ?? throw new InvalidOperationException($"SolidWorks did not insert component: {path}");
    }

    private static void FixComponent(object assembly, object component)
    {
        if (Convert.ToBoolean(ComDispatch.Call(component, "IsFixed"))) return;
        ComDispatch.Call(assembly, "ClearSelection2", true);
        if (!Convert.ToBoolean(ComDispatch.Call(component, "Select4", false, null, false)))
            throw new InvalidOperationException("Housing component could not be selected for fixing.");
        ComDispatch.Call(assembly, "FixComponent");
        ComDispatch.Call(assembly, "ClearSelection2", true);
    }

    private object AddConcentricMate(
        object assembly,
        object componentA,
        double diameterA,
        object componentB,
        double diameterB,
        string nameA,
        string nameB)
    {
        object? partFaceA = null;
        object? partFaceB = null;
        object? assemblyFaceA = null;
        object? assemblyFaceB = null;
        object? selectionManager = null;
        object? selectDataA = null;
        object? selectDataB = null;
        try
        {
            partFaceA = FindCylindricalFace(componentA, diameterA);
            partFaceB = FindCylindricalFace(componentB, diameterB);
            _log.Write("CAD", $"Resolved cylindrical part faces for {nameA}/{nameB}.");
            assemblyFaceA = ComDispatch.Call(componentA, "GetCorrespondingEntity", partFaceA)
                ?? throw new InvalidOperationException($"Could not map the {nameA} face into assembly context.");
            assemblyFaceB = ComDispatch.Call(componentB, "GetCorrespondingEntity", partFaceB)
                ?? throw new InvalidOperationException($"Could not map the {nameB} face into assembly context.");
            _log.Write("CAD", $"Mapped cylindrical faces into assembly context for {nameA}/{nameB}.");

            ComDispatch.Call(assembly, "ClearSelection2", true);
            selectionManager = ComDispatch.Get(assembly, "SelectionManager")
                ?? throw new InvalidOperationException("Assembly SelectionManager is unavailable.");
            selectDataA = ComDispatch.Call(selectionManager, "CreateSelectData")
                ?? throw new InvalidOperationException("Could not create selection data.");
            selectDataB = ComDispatch.Call(selectionManager, "CreateSelectData")
                ?? throw new InvalidOperationException("Could not create selection data.");
            if (!Convert.ToBoolean(ComDispatch.Call(assemblyFaceA, "Select4", false, selectDataA)) ||
                !Convert.ToBoolean(ComDispatch.Call(assemblyFaceB, "Select4", true, selectDataB)))
                throw new InvalidOperationException($"Could not select cylindrical faces for {nameA}/{nameB} mate.");
            _log.Write("CAD", $"Selected cylindrical faces for {nameA}/{nameB}.");

            object?[] arguments = [1, 0, false, 0d, 0d, 0d, 1d, 1d, 0d, 0d, 0d, false, 0];
            var mate = ComDispatch.CallWithByRef(assembly, "AddMate3", arguments, 12);
            var error = Convert.ToInt32(arguments[^1]);
            // swAddMateError_NoError is 1; zero means unknown error.
            if (mate is null || error != 1) throw new InvalidOperationException($"Concentric mate {nameA}/{nameB} failed (error={error}).");
            ComDispatch.Call(assembly, "ClearSelection2", true);
            return mate;
        }
        finally
        {
            ComDispatch.Release(selectDataB);
            ComDispatch.Release(selectDataA);
            ComDispatch.Release(selectionManager);
            ComDispatch.Release(assemblyFaceB);
            ComDispatch.Release(assemblyFaceA);
            ComDispatch.Release(partFaceB);
            ComDispatch.Release(partFaceA);
        }
    }

    private static object FindCylindricalFace(object component, double diameterMm)
    {
        object? part = null;
        try
        {
            part = ComDispatch.Call(component, "GetModelDoc2")
                ?? throw new InvalidOperationException("Component model document is unavailable.");
            var bodies = ComDispatch.Call(part, "GetBodies2", 0, true) as Array;
            if (bodies is null) throw new InvalidOperationException("Component has no solid body.");
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
                            if (surface is null || !Convert.ToBoolean(ComDispatch.Call(surface, "IsCylinder"))) continue;
                            var values = ComDispatch.Get(surface, "CylinderParams") as Array;
                            if (values is not null && values.Length >= 7 &&
                                Math.Abs(Convert.ToDouble(values.GetValue(6)) * 2000d - diameterMm) <= 0.02)
                            {
                                keep = true;
                                return face;
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
            throw new InvalidOperationException($"No cylindrical face with diameter {diameterMm:g} mm was found.");
        }
        finally { ComDispatch.Release(part); }
    }

    private void SaveAndVerify(object model, string outputPath)
    {
        var saved = Convert.ToBoolean(ComDispatch.Call(model, "SaveAs3", outputPath, 0, 1));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(outputPath) && DateTime.UtcNow < deadline) Thread.Sleep(100);
        if (!File.Exists(outputPath)) throw new IOException($"Assembly did not appear on disk (API returned {saved}): {outputPath}");
        var activePath = ComDispatch.Call(model, "GetPathName") as string;
        if (!string.Equals(Path.GetFullPath(activePath ?? string.Empty), outputPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Assembly active path does not match the save path.");
        if (!saved) _log.Write("CAD", "Assembly SaveAs3 returned false, but file and active path verification succeeded.");
    }

    private static ComponentInfo ToComponentInfo(object component, string path)
    {
        var name = ComDispatch.Get(component, "Name2") as string ?? Path.GetFileNameWithoutExtension(path);
        var fixedState = Convert.ToBoolean(ComDispatch.Call(component, "IsFixed"));
        return new ComponentInfo(name, Path.GetFullPath(path), fixedState);
    }

    private static string GetName(object value) => ComDispatch.Get(value, "Name") as string ?? "Concentric";
}
