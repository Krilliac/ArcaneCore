using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

/// <summary>
/// Reads numbers out of command results and chunk attributes through <see cref="JsonScalars"/>, which works whatever
/// CLR type backs a node built in code (a <c>float</c> node does not convert to <c>double</c> through
/// <c>GetValue</c>).
/// </summary>
internal static class JsonNumbers
{
    /// <summary>The node as a finite double; fails the test when it is not a number.</summary>
    public static double AsDouble(this JsonNode? node)
    {
        Assert.True(JsonScalars.TryGetDouble(node, out double value), $"Not a finite JSON number: {node?.ToJsonString() ?? "null"}");
        return value;
    }

    /// <summary>The node as an <see cref="int"/>; fails the test when it is not an integral number in range.</summary>
    public static int AsInt(this JsonNode? node)
    {
        Assert.True(JsonScalars.TryGetInt64(node, out long value), $"Not an integral JSON number: {node?.ToJsonString() ?? "null"}");
        return checked((int)value);
    }

    /// <summary>The node as a <see cref="uint"/>; fails the test when it is not an integral number in range.</summary>
    public static uint AsUInt(this JsonNode? node)
    {
        Assert.True(JsonScalars.TryGetInt64(node, out long value), $"Not an integral JSON number: {node?.ToJsonString() ?? "null"}");
        return checked((uint)value);
    }
}
