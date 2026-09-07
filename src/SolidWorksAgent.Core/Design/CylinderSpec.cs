namespace SolidWorksAgent.Core.Design;

public sealed record CylinderSpec
{
    public required double DiameterMm { get; init; }
    public required double LengthMm { get; init; }

    public void Validate()
    {
        if (!double.IsFinite(DiameterMm) || DiameterMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(DiameterMm), "Diameter must be a positive finite value in millimetres.");
        if (!double.IsFinite(LengthMm) || LengthMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(LengthMm), "Length must be a positive finite value in millimetres.");
    }
}

public sealed record ShaftStage(double DiameterMm, double LengthMm)
{
    public void Validate()
    {
        if (!double.IsFinite(DiameterMm) || DiameterMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(DiameterMm));
        if (!double.IsFinite(LengthMm) || LengthMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(LengthMm));
    }
}

public sealed record SteppedShaftSpec
{
    public required IReadOnlyList<ShaftStage> Stages { get; init; }

    public void Validate()
    {
        if (Stages is null || Stages.Count == 0) throw new ArgumentException("At least one shaft stage is required.");
        foreach (var stage in Stages) stage.Validate();
    }
}

public sealed record TubeSpec
{
    public required double OuterDiameterMm { get; init; }
    public required double InnerDiameterMm { get; init; }
    public required double LengthMm { get; init; }

    public void Validate()
    {
        if (!double.IsFinite(OuterDiameterMm) || OuterDiameterMm <= 0) throw new ArgumentOutOfRangeException(nameof(OuterDiameterMm));
        if (!double.IsFinite(InnerDiameterMm) || InnerDiameterMm <= 0) throw new ArgumentOutOfRangeException(nameof(InnerDiameterMm));
        if (InnerDiameterMm >= OuterDiameterMm) throw new ArgumentException("Inner diameter must be smaller than outer diameter.");
        if (!double.IsFinite(LengthMm) || LengthMm <= 0) throw new ArgumentOutOfRangeException(nameof(LengthMm));
    }
}
