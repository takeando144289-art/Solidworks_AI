using SolidWorksAgent.Core.Design;
using SolidWorksAgent.Core.Models;

namespace SolidWorksAgent.Core.Validation;

public static class GeometryValidator
{
    public static void ValidateCylinder(CylinderSpec expected, BoundingBox actual, double toleranceMm = 0.02)
    {
        expected.Validate();
        if (toleranceMm < 0) throw new ArgumentOutOfRangeException(nameof(toleranceMm));

        var dimensions = new[] { actual.WidthMm, actual.HeightMm, actual.DepthMm }.Order().ToArray();
        var expectedDimensions = new[] { expected.DiameterMm, expected.DiameterMm, expected.LengthMm }.Order().ToArray();
        for (var i = 0; i < dimensions.Length; i++)
        {
            if (Math.Abs(dimensions[i] - expectedDimensions[i]) > toleranceMm)
                throw new InvalidOperationException(
                    $"Generated geometry does not match the request. Expected {expected.DiameterMm:g} x {expected.LengthMm:g} mm, " +
                    $"measured {actual.WidthMm:F3} x {actual.HeightMm:F3} x {actual.DepthMm:F3} mm.");
        }
    }
}

