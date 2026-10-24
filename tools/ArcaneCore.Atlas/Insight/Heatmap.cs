using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Insight;

/// <summary>One non-empty heatmap cell.</summary>
internal readonly record struct HeatCell(int Cx, int Cy, int Count, double Z);

/// <summary>Spatial binning and the fixed colour ramp. Stub.</summary>
internal static class Heatmap
{
    public const int MaxBoxes = 1300;

    public static IReadOnlyList<HeatCell> Bin(IEnumerable<(double X, double Y, double Z)> points, double cellSize) => throw new NotImplementedException();

    public static JsonObject Render(IReadOnlyList<HeatCell> cells, double cellSize, int maxBoxes = MaxBoxes) => throw new NotImplementedException();

    public static string Ramp(int count, int max) => throw new NotImplementedException();
}
