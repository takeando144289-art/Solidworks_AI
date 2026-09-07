namespace SolidWorksAgent.Core.Models;

public enum CadDocumentType
{
    None = 0,
    Part = 1,
    Assembly = 2,
    Drawing = 3,
    Unknown = -1
}

public sealed record DocumentInfo(
    bool Connected,
    string? Title,
    string? Path,
    CadDocumentType Type);

public sealed record BoundingBox(
    double WidthMm,
    double HeightMm,
    double DepthMm)
{
    public double DiameterMm => Math.Max(WidthMm, HeightMm);
}

public sealed record PartCreationResult(
    string Path,
    string FeatureName,
    BoundingBox BoundingBox,
    bool RebuildSucceeded);

public sealed record Point3(double Xmm, double Ymm, double Zmm);

public sealed record FeatureInfo(string Name, string TypeName);

public sealed record CylindricalFaceInfo(
    string Id,
    double DiameterMm,
    double LengthMm,
    Point3 Origin,
    Point3 Axis);

public sealed record MassProperties(
    double MassKg,
    double VolumeMm3,
    double SurfaceAreaMm2,
    Point3 CenterOfMass);

public sealed record PartInspectionResult(
    string Title,
    string? Path,
    string? Material,
    BoundingBox BoundingBox,
    IReadOnlyList<FeatureInfo> Features,
    IReadOnlyList<CylindricalFaceInfo> Cylinders,
    MassProperties? MassProperties);

public sealed record ComponentInfo(string Name, string Path, bool Fixed);

public sealed record MateInfo(string Name, string Type, string ComponentA, string ComponentB);

public sealed record AssemblyCreationResult(
    string Path,
    IReadOnlyList<ComponentInfo> Components,
    IReadOnlyList<MateInfo> Mates,
    bool RebuildSucceeded);

public sealed record InterferenceInfo(
    IReadOnlyList<string> Components,
    double VolumeMm3,
    bool PossibleInterference,
    bool Fastener);

public sealed record AssemblyAnalysisResult(
    string Title,
    string? Path,
    BoundingBox BoundingBox,
    MassProperties? MassProperties,
    IReadOnlyList<ComponentInfo> Components,
    IReadOnlyList<InterferenceInfo> Interferences)
{
    public int InterferenceCount => Interferences.Count;
    public double TotalInterferenceVolumeMm3 => Interferences.Sum(item => item.VolumeMm3);
}

public sealed record DrawingCreationResult(
    string DrawingPath,
    string PdfPath,
    IReadOnlyList<string> Views,
    int ImportedDimensionCount,
    bool LengthDimensionCreated,
    bool DiameterDimensionCreated,
    bool ToleranceApplied,
    bool RebuildSucceeded);
