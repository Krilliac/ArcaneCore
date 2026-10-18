using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Render;

/// <summary>
/// Writes the geometry as one JSON document for tools that are not OBJ viewers. Per chunk: the layer, tile and part, the
/// double-precision <c>origin</c>, WORLD-space <c>positions</c> as a flat <c>[x, y, z, ...]</c> array (each the chunk origin
/// plus the float position, in double, exactly as <see cref="GeometryChunk.Vertex"/>), the triangle, line and point index
/// arrays with their attribute id arrays, the attribute table and the chunk's statistics. The status of every tile asked
/// for follows the chunks, so a tile that was missing or rejected is visible in the file. Numbers are written as the
/// shortest text that parses back to the same double.
/// </summary>
internal static class JsonExporter
{
    /// <summary>Format identifier written into the document.</summary>
    public const string Format = "arcane-atlas-export";

    /// <summary>Document version.</summary>
    public const int Version = 1;

    /// <summary>Writes the document to a temporary file and moves it to <paramref name="path"/> on success.</summary>
    public static ExportSummary Write(ExportRequest request, IEnumerable<ExportTile> tiles, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tiles);
        var summary = new ExportSummary();
        summary.Written = OutputPaths.WriteAtomically(
            path,
            stream => WriteDocument(stream, request, tiles, summary, cancellationToken),
            () => summary.Primitives > 0);
        return summary;
    }

    private static void WriteDocument(Stream stream, ExportRequest request, IEnumerable<ExportTile> tiles, ExportSummary summary, CancellationToken cancellationToken)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteString("format", Format);
        writer.WriteNumber("version", Version);
        writer.WriteString("coordinates", "WoW world coordinates in yards: x north, y west, z up");
        writer.WriteNumber("map", request.MapId);
        writer.WriteStartArray("center");
        writer.WriteNumberValue(request.X);
        writer.WriteNumberValue(request.Y);
        writer.WriteEndArray();
        writer.WriteNumber("radius", request.Radius);

        writer.WriteStartArray("chunks");
        var statuses = new List<TileStatus>();
        foreach (ExportTile tile in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            summary.Add(tile);
            statuses.Add(tile.Build.Status);
            foreach (GeometryChunk chunk in tile.Build.Chunks)
            {
                WriteChunk(writer, chunk);
                summary.Objects++;
                summary.Vertices += chunk.VertexCount;
                summary.Triangles += chunk.TriangleCount;
                summary.Lines += chunk.LineCount;
                summary.Points += chunk.PointCount;
                writer.Flush();
            }
        }

        writer.WriteEndArray();
        writer.WriteStartArray("tiles");
        foreach (TileStatus status in statuses)
        {
            status.ToJson().WriteTo(writer);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteChunk(Utf8JsonWriter writer, GeometryChunk chunk)
    {
        writer.WriteStartObject();
        writer.WriteString("layer", chunk.LayerId);
        writer.WriteStartArray("tile");
        writer.WriteNumberValue(chunk.Tile.MapId);
        writer.WriteNumberValue(chunk.Tile.X);
        writer.WriteNumberValue(chunk.Tile.Y);
        writer.WriteEndArray();
        writer.WriteNumber("part", chunk.Part);
        writer.WriteStartArray("origin");
        writer.WriteNumberValue(chunk.Origin.X);
        writer.WriteNumberValue(chunk.Origin.Y);
        writer.WriteNumberValue(chunk.Origin.Z);
        writer.WriteEndArray();
        writer.WriteNumber("vertexCount", chunk.VertexCount);

        writer.WriteStartArray("positions");
        for (int i = 0; i < chunk.VertexCount; i++)
        {
            WorldPoint p = chunk.Vertex((uint)i);
            writer.WriteNumberValue(p.X);
            writer.WriteNumberValue(p.Y);
            writer.WriteNumberValue(p.Z);
        }

        writer.WriteEndArray();
        WriteIndices(writer, "triangles", chunk.Triangles);
        WriteIndices(writer, "triangleAttributes", chunk.TriangleAttributes);
        WriteIndices(writer, "lines", chunk.Lines);
        WriteIndices(writer, "lineAttributes", chunk.LineAttributes);
        WriteIndices(writer, "points", chunk.Points);
        WriteIndices(writer, "pointAttributes", chunk.PointAttributes);

        writer.WriteStartObject("attributes");
        foreach ((uint id, JsonObject table) in chunk.Attributes.OrderBy(a => a.Key))
        {
            writer.WritePropertyName(id.ToString(CultureInfo.InvariantCulture));
            table.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.WritePropertyName("stats");
        chunk.Stats.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static void WriteIndices(Utf8JsonWriter writer, string name, uint[] values)
    {
        writer.WriteStartArray(name);
        foreach (uint value in values)
        {
            writer.WriteNumberValue(value);
        }

        writer.WriteEndArray();
    }
}
