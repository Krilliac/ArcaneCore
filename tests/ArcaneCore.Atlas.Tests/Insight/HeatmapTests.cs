using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Insight;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Insight;

/// <summary>Cell counts are exact, the ramp is fixed, and a capped answer says so and keeps every counted point.</summary>
public sealed class HeatmapTests
{
    [Fact]
    public void Bin_CountsEveryPointInItsFloorCell_IncludingNegativeCoordinatesAndCellEdges()
    {
        IReadOnlyList<HeatCell> cells = Heatmap.Bin(
            [(0, 0, 10), (0.5, 0.5, 20), (99.9, 0, 30), (100, 0, 40), (-0.1, -0.1, 50), (-100, -100.5, 60)],
            100);

        Dictionary<(int, int), int> counts = cells.ToDictionary(c => (c.Cx, c.Cy), c => c.Count);
        Assert.Equal(4, counts.Count);
        Assert.Equal(3, counts[(0, 0)]);
        Assert.Equal(1, counts[(1, 0)]);
        Assert.Equal(1, counts[(-1, -1)]);
        Assert.Equal(1, counts[(-1, -2)]);
        Assert.Equal(6, cells.Sum(c => c.Count));
        Assert.Equal(20.0, cells.Single(c => c.Cx == 0 && c.Cy == 0).Z);
    }

    [Fact]
    public void Bin_OfNothing_IsEmpty_AndABadCellSizeIsRefused()
    {
        Assert.Empty(Heatmap.Bin([], 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Heatmap.Bin([(0, 0, 0)], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Heatmap.Bin([(0, 0, 0)], -5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Heatmap.Bin([(0, 0, 0)], double.NaN));
    }

    [Fact]
    public void TheRamp_IsFixed_AndSplitsTheRangeIntoFiveEqualSteps()
    {
        Assert.Equal("#2C7BB6", Heatmap.Ramp(1, 100));
        Assert.Equal("#2C7BB6", Heatmap.Ramp(19, 100));
        Assert.Equal("#ABD9E9", Heatmap.Ramp(20, 100));
        Assert.Equal("#FFFFBF", Heatmap.Ramp(50, 100));
        Assert.Equal("#FDAE61", Heatmap.Ramp(60, 100));
        Assert.Equal("#FDAE61", Heatmap.Ramp(79, 100));
        Assert.Equal("#D7191C", Heatmap.Ramp(80, 100));
        Assert.Equal("#D7191C", Heatmap.Ramp(100, 100));
        Assert.Equal("#D7191C", Heatmap.Ramp(1, 1));
    }

    [Fact]
    public void Render_WritesOneBoxPerCellAtItsExactBounds_WithItsCountAsTheLabel()
    {
        IReadOnlyList<HeatCell> cells = [new HeatCell(1, 0, 7, 12.5), new HeatCell(-1, -2, 1, -3.0)];

        JsonObject result = Heatmap.Render(cells, 100);

        Assert.False(result["truncated"]!.GetValue<bool>());
        Assert.Equal(8, result["total"]!.GetValue<int>());
        Assert.Equal(2, result["cells"]!.GetValue<int>());
        JsonArray boxes = result["overlay"]!["boxes"]!.AsArray();
        Assert.Equal(2, boxes.Count);
        JsonObject hot = boxes.Single(b => b!["label"]!.GetValue<string>() == "7")!.AsObject();
        Assert.Equal([100.0, 0.0, 12.5], hot["min"]!.AsArray().Select(n => n!.GetValue<double>()));
        Assert.Equal([200.0, 100.0, 12.5], hot["max"]!.AsArray().Select(n => n!.GetValue<double>()));
        Assert.Equal("#D7191C", hot["color"]!.GetValue<string>());
        JsonObject cold = boxes.Single(b => b!["label"]!.GetValue<string>() == "1")!.AsObject();
        Assert.Equal([-100.0, -200.0, -3.0], cold["min"]!.AsArray().Select(n => n!.GetValue<double>()));
        Assert.Equal("#2C7BB6", cold["color"]!.GetValue<string>());
        Assert.Equal(0, result["other"]!["cells"]!.GetValue<int>());
    }

    [Fact]
    public void Render_PastTheBoxCap_KeepsTheBusiestCells_AndMergesTheRestIntoOneOtherBucketWithoutLosingAnyCount()
    {
        var cells = new List<HeatCell>();
        for (int i = 0; i < 2000; i++)
        {
            cells.Add(new HeatCell(i, 0, (i % 7) + 1, 0));
        }

        JsonObject result = Heatmap.Render(cells, 50);

        JsonArray boxes = result["overlay"]!["boxes"]!.AsArray();
        Assert.True(result["truncated"]!.GetValue<bool>());
        Assert.Equal(cells.Sum(c => c.Count), result["total"]!.GetValue<int>());
        Assert.Equal(2000, result["cells"]!.GetValue<int>());
        Assert.Equal(Heatmap.MaxBoxes, boxes.Count);
        int inBoxes = boxes.Sum(b => int.Parse(b!["label"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        JsonObject other = result["other"]!.AsObject();
        Assert.Equal(2000 - Heatmap.MaxBoxes, other["cells"]!.GetValue<int>());
        Assert.Equal(cells.Sum(c => c.Count), inBoxes + other["count"]!.GetValue<int>());
        int lowestKept = boxes.Min(b => int.Parse(b!["label"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        int highestMerged = cells.OrderBy(c => c.Count).Take(2000 - Heatmap.MaxBoxes).Max(c => c.Count);
        Assert.True(lowestKept >= highestMerged, $"a merged cell ({highestMerged}) is busier than a kept one ({lowestKept})");
    }

    [Fact]
    public void Render_OfNoCells_IsAnEmptyHonestAnswer()
    {
        JsonObject result = Heatmap.Render([], 100);

        Assert.Equal(0, result["total"]!.GetValue<int>());
        Assert.Equal(0, result["cells"]!.GetValue<int>());
        Assert.Empty(result["overlay"]!["boxes"]!.AsArray());
        Assert.False(result["truncated"]!.GetValue<bool>());
    }
}
