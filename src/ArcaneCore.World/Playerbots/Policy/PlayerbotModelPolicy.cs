using System.Numerics.Tensors;
using System.Text.Json;

namespace ArcaneCore.World.Playerbots.Policy;

/// <summary>
/// A small built-in goal model: optional per-feature standardisation, then one to three dense layers (the last one linear, the others
/// ReLU), evaluated on the CPU with <see cref="TensorPrimitives"/> (SIMD dot products). One layer is a linear model; two or three are a tiny
/// MLP. A bot costs at most a few thousand multiply-adds, so there is no GPU path: moving the batch would cost more than scoring it.
/// <para>
/// Weights file (JSON): <c>{"format":"arcanecore.playerbot-policy","version":1,"features":[...],"goals":[...],"mean":[...]?,"scale":[...]?,
/// "layers":[{"inputs":I,"outputs":O,"activation":"relu"|"linear","weights":[O*I, row-major by output],"bias":[O]}]}</c>. The feature and
/// goal names must equal <see cref="PlayerbotPolicySchema"/>'s, in order. An offline trainer writes it from the policy telemetry (docs/areas/playbots.md, Goal policy).
/// </para>
/// </summary>
public sealed class PlayerbotModelPolicy : IPlayerbotPolicy
{
    public const string Format = "arcanecore.playerbot-policy";
    public const int Version = 1;
    public const int MaxLayers = 3;
    public const int MaxWidth = 256;
    public const long MaxFileBytes = 4L * 1024 * 1024;

    private readonly float[] _mean;
    private readonly float[] _scale;
    private readonly Layer[] _layers;
    private readonly int _widest;

    private sealed record Layer(int Inputs, int Outputs, bool Relu, float[] Weights, float[] Bias);

    private PlayerbotModelPolicy(string name, float[] mean, float[] scale, Layer[] layers)
    {
        Name = name;
        _mean = mean;
        _scale = scale;
        _layers = layers;
        _widest = layers.Max(layer => Math.Max(layer.Inputs, layer.Outputs));
    }

    public string Name { get; }

    /// <summary>Multiply-adds per bot (inspection; the cost estimate in the docs).</summary>
    public int MultiplyAddsPerBot => _layers.Sum(layer => layer.Inputs * layer.Outputs);

    /// <summary>Load a weights file; throws <see cref="InvalidDataException"/> (or an I/O error) with the reason when it is not a usable model.</summary>
    public static PlayerbotModelPolicy Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("the policy model file does not exist", path);
        if (file.Length > MaxFileBytes) throw new InvalidDataException($"the policy model file is {file.Length} bytes, over the {MaxFileBytes} byte limit");
        using FileStream stream = file.OpenRead();
        return Parse(stream, "model:" + file.Name);
    }

    /// <summary>Parse a weights document (tests, and <see cref="Load"/>).</summary>
    public static PlayerbotModelPolicy Parse(Stream json, string name = "model")
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException error) { throw new InvalidDataException("the policy model is not valid JSON: " + error.Message, error); }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("the policy model must be a JSON object");
            if (!root.TryGetProperty("format", out JsonElement format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
                throw new InvalidDataException($"the policy model's format must be \"{Format}\"");
            if (!root.TryGetProperty("version", out JsonElement version) || !version.TryGetInt32(out int v) || v != Version)
                throw new InvalidDataException($"the policy model's version must be {Version}");
            RequireNames(root, "features", PlayerbotPolicySchema.FeatureNames);
            RequireNames(root, "goals", PlayerbotPolicySchema.GoalNames);

            int features = PlayerbotPolicySchema.FeatureCount;
            float[] mean = OptionalVector(root, "mean", features, 0f);
            float[] scale = OptionalVector(root, "scale", features, 1f);

            if (!root.TryGetProperty("layers", out JsonElement layersElement) || layersElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("the policy model needs a \"layers\" array");
            int count = layersElement.GetArrayLength();
            if (count is < 1 or > MaxLayers) throw new InvalidDataException($"the policy model must have 1..{MaxLayers} layers, not {count}");
            var layers = new Layer[count];
            int inputs = features;
            for (int i = 0; i < count; i++)
            {
                JsonElement layer = layersElement[i];
                bool last = i == count - 1;
                int layerInputs = RequireInt(layer, "inputs", i);
                int outputs = RequireInt(layer, "outputs", i);
                if (layerInputs != inputs) throw new InvalidDataException($"layer {i} takes {layerInputs} inputs; the layer before gives {inputs}");
                if (outputs is < 1 or > MaxWidth) throw new InvalidDataException($"layer {i} must have 1..{MaxWidth} outputs, not {outputs}");
                if (last && outputs != PlayerbotPolicySchema.GoalCount)
                    throw new InvalidDataException($"the last layer must give {PlayerbotPolicySchema.GoalCount} outputs (one per goal), not {outputs}");
                string activation = layer.TryGetProperty("activation", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "";
                bool relu = activation switch
                {
                    "relu" when !last => true,
                    "linear" => false,
                    _ => throw new InvalidDataException($"layer {i}'s activation must be \"linear\"{(last ? "" : " or \"relu\"")}, not \"{activation}\""),
                };
                float[] weights = RequireVector(layer, "weights", outputs * layerInputs, $"layer {i} weights");
                float[] bias = RequireVector(layer, "bias", outputs, $"layer {i} bias");
                layers[i] = new Layer(layerInputs, outputs, relu, weights, bias);
                inputs = outputs;
            }
            return new PlayerbotModelPolicy(name, mean, scale, layers);
        }
    }

    public void Score(ReadOnlySpan<float> features, int count, Span<float> scores)
    {
        int f = PlayerbotPolicySchema.FeatureCount, g = PlayerbotPolicySchema.GoalCount;
        Span<float> a = stackalloc float[_widest];
        Span<float> b = stackalloc float[_widest];
        for (int row = 0; row < count; row++)
        {
            Span<float> input = a[..f];
            TensorPrimitives.Subtract(features.Slice(row * f, f), _mean, input);
            TensorPrimitives.Multiply(input, _scale, input);
            Span<float> current = a, next = b;
            for (int l = 0; l < _layers.Length; l++)
            {
                Layer layer = _layers[l];
                ReadOnlySpan<float> x = current[..layer.Inputs];
                Span<float> y = l == _layers.Length - 1 ? scores.Slice(row * g, g) : next[..layer.Outputs];
                ReadOnlySpan<float> w = layer.Weights;
                for (int o = 0; o < layer.Outputs; o++) y[o] = TensorPrimitives.Dot(w.Slice(o * layer.Inputs, layer.Inputs), x) + layer.Bias[o];
                if (layer.Relu) TensorPrimitives.Max(y, 0f, y);
                Span<float> swap = current;
                current = next;
                next = swap;
            }
        }
    }

    private static void RequireNames(JsonElement root, string property, IReadOnlyList<string> expected)
    {
        if (!root.TryGetProperty(property, out JsonElement names) || names.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"the policy model needs a \"{property}\" array");
        string[] got = names.EnumerateArray().Select(n => n.ValueKind == JsonValueKind.String ? n.GetString()! : "?").ToArray();
        if (!got.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException($"the policy model's {property} [{string.Join(",", got)}] are not this server's [{string.Join(",", expected)}]");
    }

    private static int RequireInt(JsonElement layer, string property, int index)
        => layer.ValueKind == JsonValueKind.Object && layer.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int n) && n > 0
            ? n
            : throw new InvalidDataException($"layer {index} needs a positive integer \"{property}\"");

    private static float[] OptionalVector(JsonElement root, string property, int length, float fill)
    {
        if (!root.TryGetProperty(property, out JsonElement element)) return Enumerable.Repeat(fill, length).ToArray();
        return ReadVector(element, length, property);
    }

    private static float[] RequireVector(JsonElement owner, string property, int length, string what)
        => owner.TryGetProperty(property, out JsonElement element)
            ? ReadVector(element, length, what)
            : throw new InvalidDataException($"the policy model is missing {what}");

    private static float[] ReadVector(JsonElement element, int length, string what)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != length)
            throw new InvalidDataException($"{what} must be an array of {length} numbers");
        var values = new float[length];
        int i = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out float value) || !float.IsFinite(value))
                throw new InvalidDataException($"{what}[{i}] is not a finite number");
            values[i++] = value;
        }
        return values;
    }
}
