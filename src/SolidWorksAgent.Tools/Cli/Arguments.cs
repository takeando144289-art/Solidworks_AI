using System.Globalization;

namespace SolidWorksAgent.Tools.Cli;

internal sealed class Arguments(string[] values)
{
    public bool Has(string option) => values.Contains(option, StringComparer.OrdinalIgnoreCase);

    public string? Value(string option)
    {
        var index = Array.FindIndex(values, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
    }

    public double RequiredDouble(string option)
    {
        var raw = Value(option) ?? throw new ArgumentException($"Missing required option: {option}");
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            throw new ArgumentException($"Invalid numeric value for {option}: {raw}");
        return result;
    }

    public IReadOnlyList<double> RequiredDoubleList(string option)
    {
        var raw = Value(option) ?? throw new ArgumentException($"Missing required option: {option}");
        var result = new List<double>();
        foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException($"Invalid numeric list for {option}: {raw}");
            result.Add(number);
        }
        if (result.Count == 0) throw new ArgumentException($"Empty numeric list for {option}.");
        return result;
    }
}
