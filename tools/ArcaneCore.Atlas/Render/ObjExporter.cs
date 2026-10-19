using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Render;

/// <summary>
/// Writes Wavefront OBJ (<c>v</c>, <c>f</c>, <c>l</c>, <c>p</c>, <c>o</c>, <c>g</c>, <c>usemtl</c>, <c>mtllib</c>) plus the
/// MTL it names. Vertices are WoW world coordinates (x north, y west, z up, yards) written with the shortest decimal
/// text that parses back to the same double (<c>R</c>, invariant culture); a chunk's float positions are added to its
/// double origin exactly as <see cref="GeometryChunk.Vertex"/> does, so a reader recovers the same world vertices.
/// <para>
/// Layout: one object (<c>o</c>) per layer, tile and part; inside it one group (<c>g</c>) per colour key (primitives whose
/// attribute has no <c>colorKey</c> go to <c>default</c>), each with a material named after the layer and the key.
/// Indices are absolute and 1-based as OBJ requires, running on across objects. The material colours come from the view's
/// layer styles (the attribute colour when the layer colours by attribute).
/// </para>
/// </summary>
internal static class ObjExporter
{
    /// <summary>The group a primitive without a <c>colorKey</c> belongs to.</summary>
    public const string DefaultGroup = "default";

    /// <summary>Writes the OBJ and MTL, moving both into place only when the whole export succeeded.</summary>
    public static ExportSummary Write(ExportRequest request, IEnumerable<ExportTile> tiles, string objPath, string mtlPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(objPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mtlPath);
        var summary = new ExportSummary();
        var materials = new SortedDictionary<string, Material>(StringComparer.Ordinal);
        string objTemp = OutputPaths.PrepareTemp(objPath);
        string mtlTemp = OutputPaths.PrepareTemp(mtlPath);
        bool done = false;
        try
        {
            using (FileStream stream = OutputPaths.OpenTemp(objTemp))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" })
            {
                WriteHeader(writer, request, Path.GetFileName(mtlPath));
                long vertexBase = 0;
                var notes = new List<string>();
                foreach (ExportTile tile in tiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    summary.Add(tile);
                    if (tile.Build.Status.State is not (TileState.Ok or TileState.Empty))
                    {
                        notes.Add($"# {tile.Layer.Id} tile {tile.Tile}: {tile.Build.Status.State}{(tile.Build.Status.Detail is { } d ? $" ({OneLine(d)})" : string.Empty)}");
                    }

                    foreach (GeometryChunk chunk in tile.Build.Chunks)
                    {
                        WriteChunk(writer, request, tile.Layer, chunk, vertexBase, materials, summary);
                        vertexBase += chunk.VertexCount;
                    }
                }

                foreach (string note in notes)
                {
                    writer.WriteLine(note);
                }
            }

            using (FileStream stream = OutputPaths.OpenTemp(mtlTemp))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 12) { NewLine = "\n" })
            {
                WriteMaterials(writer, materials);
            }

            if (summary.Primitives == 0)
            {
                return summary;
            }

            File.Move(mtlTemp, mtlPath, overwrite: true);
            File.Move(objTemp, objPath, overwrite: true);
            done = true;
            summary.Written = true;
        }
        finally
        {
            if (!done)
            {
                OutputPaths.DeleteQuietly(objTemp);
                OutputPaths.DeleteQuietly(mtlTemp);
            }
        }

        return summary;
    }

    /// <summary>OBJ object and group names: letters, digits, underscore and hyphen only.</summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            text.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return text.ToString();
    }

    /// <summary>The object name of a chunk: layer id, map, tile (or <c>global</c>) and, past the first, the part.</summary>
    public static string ObjectName(GeometryChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        string tile = chunk.Tile.IsGlobal
            ? string.Create(CultureInfo.InvariantCulture, $"{chunk.Tile.MapId}_global")
            : string.Create(CultureInfo.InvariantCulture, $"{chunk.Tile.MapId}_{chunk.Tile.X}_{chunk.Tile.Y}");
        string name = $"{Sanitize(chunk.LayerId)}_{tile}";
        return chunk.Part > 0 ? string.Create(CultureInfo.InvariantCulture, $"{name}_p{chunk.Part}") : name;
    }

    /// <summary>The colour key of an attribute id in a chunk, or <see cref="DefaultGroup"/>.</summary>
    public static string ColorKeyOf(GeometryChunk chunk, uint attribute)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return chunk.Attributes.TryGetValue(attribute, out JsonObject? table)
            && table["colorKey"] is JsonValue value && value.TryGetValue(out string? key) && !string.IsNullOrEmpty(key)
            ? key
            : DefaultGroup;
    }

    private static void WriteHeader(StreamWriter writer, ExportRequest request, string mtlName)
    {
        writer.WriteLine("# arcane-atlas export.obj");
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"# map {request.MapId}, centre x {request.X:R} y {request.Y:R}, radius {request.Radius} tiles"));
        writer.WriteLine("# WoW world coordinates in yards: x north, y west, z up. Viewers that assume y-up show this on its side.");
        writer.WriteLine($"mtllib {mtlName}");
    }

    private static void WriteChunk(
        StreamWriter writer,
        ExportRequest request,
        LayerInfo layer,
        GeometryChunk chunk,
        long vertexBase,
        SortedDictionary<string, Material> materials,
        ExportSummary summary)
    {
        writer.Write("o ");
        writer.WriteLine(ObjectName(chunk));
        for (int i = 0; i < chunk.VertexCount; i++)
        {
            WorldPoint p = chunk.Vertex((uint)i);
            writer.Write("v ");
            WriteNumber(writer, p.X);
            writer.Write(' ');
            WriteNumber(writer, p.Y);
            writer.Write(' ');
            WriteNumber(writer, p.Z);
            writer.WriteLine();
        }

        var keys = new Dictionary<uint, string>();
        string KeyOf(uint attribute)
        {
            if (!keys.TryGetValue(attribute, out string? key))
            {
                key = ColorKeyOf(chunk, attribute);
                keys[attribute] = key;
            }

            return key;
        }

        SortedDictionary<string, Group> groups = new(StringComparer.Ordinal);
        Group GroupOf(string key)
        {
            if (!groups.TryGetValue(key, out Group? group))
            {
                group = new Group();
                groups[key] = group;
            }

            return group;
        }

        for (int t = 0; t < chunk.TriangleCount; t++)
        {
            GroupOf(KeyOf(chunk.TriangleAttributes[t])).Triangles.Add(t);
        }

        for (int l = 0; l < chunk.LineCount; l++)
        {
            GroupOf(KeyOf(chunk.LineAttributes[l])).Lines.Add(l);
        }

        for (int p = 0; p < chunk.PointCount; p++)
        {
            GroupOf(KeyOf(chunk.PointAttributes[p])).Points.Add(p);
        }

        foreach ((string key, Group group) in groups)
        {
            string material = $"{Sanitize(layer.Id)}_{Sanitize(key)}";
            if (!materials.ContainsKey(material))
            {
                materials[material] = MaterialOf(request.View, layer, key);
            }

            writer.Write("g ");
            writer.WriteLine(Sanitize(key));
            writer.Write("usemtl ");
            writer.WriteLine(material);
            foreach (int t in group.Triangles)
            {
                writer.Write("f ");
                writer.Write(Index(vertexBase, chunk.Triangles[3 * t]));
                writer.Write(' ');
                writer.Write(Index(vertexBase, chunk.Triangles[(3 * t) + 1]));
                writer.Write(' ');
                writer.WriteLine(Index(vertexBase, chunk.Triangles[(3 * t) + 2]));
            }

            foreach (int l in group.Lines)
            {
                writer.Write("l ");
                writer.Write(Index(vertexBase, chunk.Lines[2 * l]));
                writer.Write(' ');
                writer.WriteLine(Index(vertexBase, chunk.Lines[(2 * l) + 1]));
            }

            foreach (int p in group.Points)
            {
                writer.Write("p ");
                writer.WriteLine(Index(vertexBase, chunk.Points[p]));
            }
        }

        summary.Objects++;
        summary.Vertices += chunk.VertexCount;
        summary.Triangles += chunk.TriangleCount;
        summary.Lines += chunk.LineCount;
        summary.Points += chunk.PointCount;
    }

    private static string Index(long vertexBase, uint local) => (vertexBase + local + 1).ToString(CultureInfo.InvariantCulture);

    private static Material MaterialOf(ViewState view, LayerInfo layer, string key)
    {
        LayerStyle style = view.Layers.TryGetValue(layer.Id, out LayerStyle? s) ? s : LayerStyle.For(layer);
        string text = style.ColorByAttribute && layer.AttributeColors is { } colors && colors.TryGetValue(key, out string? keyed) ? keyed : style.Color;
        Rgb colour = Rgb.TryParse(text, out Rgb parsed) ? parsed : Rgb.Background;
        return new Material(colour, style.Opacity);
    }

    private static void WriteMaterials(StreamWriter writer, SortedDictionary<string, Material> materials)
    {
        writer.WriteLine("# arcane-atlas export.obj materials: layer colours of the view at export time");
        foreach ((string name, Material material) in materials)
        {
            writer.WriteLine($"newmtl {name}");
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"# {material.Colour}"));
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Kd {material.Colour.R / 255.0:F6} {material.Colour.G / 255.0:F6} {material.Colour.B / 255.0:F6}"));
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"d {material.Opacity:F6}"));
        }
    }

    private static void WriteNumber(TextWriter writer, double value)
    {
        Span<char> buffer = stackalloc char[40];
        if (!value.TryFormat(buffer, out int written, "R", CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException($"Could not format {value}.");
        }

        writer.Write(buffer[..written]);
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");

    private sealed record Material(Rgb Colour, double Opacity);

    private sealed class Group
    {
        public List<int> Triangles { get; } = [];

        public List<int> Lines { get; } = [];

        public List<int> Points { get; } = [];
    }
}
