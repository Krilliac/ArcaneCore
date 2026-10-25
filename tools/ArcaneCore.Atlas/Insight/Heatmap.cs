using System.Globalization;
using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Insight;

/// <summary>One non-empty heatmap cell: its grid index, how many points fell in it and the mean height of those points.</summary>
internal readonly record struct HeatCell(int Cx, int Cy, int Count, double Z);

/// <summary>Spatial binning of points into square cells and the fixed colour ramp the cells are drawn with.</summary>
internal static class Heatmap
{
    /// <summary>
    /// Most boxes one answer carries. Each box is 11 JSON nodes (the object, two three-number arrays, a label and a colour), and the host
    /// replaces a command event over 20,000 nodes with a placeholder, so 1,300 boxes keeps the answer near 14,300 nodes.
    /// </summary>
    public const int MaxBoxes = 1300;

    // ColorBrewer RdYlBu, blue (few) to red (many); five equal steps of count / max.
    private static readonly string[] RampColors = ["#2C7BB6", "#ABD9E9", "#FFFFBF", "#FDAE61", "#D7191C"];

    /// <summary>Counts <paramref name="points"/> per cell of side <paramref name="cellSize"/> (floor of coordinate / size); non-finite points are skipped. Ordered by row, then column.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cellSize"/> is not a positive finite number.</exception>
    public static IReadOnlyList<HeatCell> Bin(IEnumerable<(double X, double Y, double Z)> points, double cellSize)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (!double.IsFinite(cellSize) || cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "the cell size must be a positive number.");
        }

        var cells = new Dictionary<(int, int), (int Count, double ZSum)>();
        foreach ((double x, double y, double z) in points)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            {
                continue;
            }

            double cx = Math.Floor(x / cellSize);
            double cy = Math.Floor(y / cellSize);
            if (Math.Abs(cx) > int.MaxValue / 2 || Math.Abs(cy) > int.MaxValue / 2)
            {
                continue;
            }

            (int, int) key = ((int)cx, (int)cy);
            (int count, double sum) = cells.GetValueOrDefault(key);
            cells[key] = (count + 1, sum + z);
        }

        return [.. cells
            .OrderBy(c => c.Key.Item2)
            .ThenBy(c => c.Key.Item1)
            .Select(c => new HeatCell(c.Key.Item1, c.Key.Item2, c.Value.Count, c.Value.ZSum / c.Value.Count))];
    }

    /// <summary>The colour of a cell with <paramref name="count"/> points when the busiest cell has <paramref name="max"/>.</summary>
    public static string Ramp(int count, int max)
    {
        if (max <= 0)
        {
            return RampColors[0];
        }

        long step = Math.Clamp((long)count * RampColors.Length / max, 0, RampColors.Length - 1);
        return RampColors[step];
    }

    /// <summary>
    /// The answer for <paramref name="cells"/>: <c>cells</c>, <c>total</c> (points), <c>truncated</c>, <c>other</c> (the cells merged because
    /// of <paramref name="maxBoxes"/>: their number and point count) and <c>overlay.boxes</c> for the busiest cells. A cell's label is its count.
    /// </summary>
    public static JsonObject Render(IReadOnlyList<HeatCell> cells, double cellSize, int maxBoxes = MaxBoxes)
    {
        ArgumentNullException.ThrowIfNull(cells);
        int total = cells.Sum(c => c.Count);
        int max = cells.Count == 0 ? 0 : cells.Max(c => c.Count);
        IReadOnlyList<HeatCell> kept = cells.Count <= maxBoxes
            ? cells
            : [.. cells.OrderByDescending(c => c.Count).ThenBy(c => c.Cy).ThenBy(c => c.Cx).Take(maxBoxes)];
        HashSet<HeatCell>? keptSet = cells.Count <= maxBoxes ? null : [.. kept];
        int mergedCells = 0;
        int mergedCount = 0;
        if (keptSet is not null)
        {
            foreach (HeatCell cell in cells)
            {
                if (!keptSet.Contains(cell))
                {
                    mergedCells++;
                    mergedCount += cell.Count;
                }
            }
        }

        var boxes = new JsonArray();
        foreach (HeatCell cell in kept.OrderBy(c => c.Cy).ThenBy(c => c.Cx))
        {
            double x0 = cell.Cx * cellSize;
            double y0 = cell.Cy * cellSize;
            boxes.Add(new JsonObject
            {
                ["min"] = new JsonArray(x0, y0, cell.Z),
                ["max"] = new JsonArray(x0 + cellSize, y0 + cellSize, cell.Z),
                ["label"] = cell.Count.ToString(CultureInfo.InvariantCulture),
                ["color"] = Ramp(cell.Count, max),
            });
        }

        return new JsonObject
        {
            ["cellSize"] = cellSize,
            ["cells"] = cells.Count,
            ["total"] = total,
            ["truncated"] = mergedCells > 0,
            ["other"] = new JsonObject { ["cells"] = mergedCells, ["count"] = mergedCount },
            ["overlay"] = new JsonObject { ["boxes"] = boxes },
        };
    }
}
