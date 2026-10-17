using System.Globalization;

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>The shape of a decoder-only transformer as its GGUF metadata gives it.</summary>
internal sealed record ChatModelConfig(
    string Architecture,
    int Dim,
    int Layers,
    int Heads,
    int KvHeads,
    int HeadDim,
    int FfnDim,
    int Vocabulary,
    int ContextLength,
    float RopeBase,
    int RopeDims,
    float RmsEpsilon,
    bool RopeNeox)
{
    public int QueryDim => Heads * HeadDim;

    public int KvDim => KvHeads * HeadDim;
}

/// <summary>A weight matrix of <see cref="Rows"/> rows of <see cref="Columns"/> weights (GGUF dims [Columns, Rows]); y = W x dots each row with x.</summary>
internal sealed record WeightMatrix(GgufTensor Tensor, int Rows, int Columns)
{
    public GgmlType Type => Tensor.Type;
}

/// <summary>One transformer block's weights (biases only for qwen2).</summary>
internal sealed record ChatModelLayer(
    float[] AttentionNorm, WeightMatrix Query, WeightMatrix Key, WeightMatrix Value, WeightMatrix Output,
    float[]? QueryBias, float[]? KeyBias, float[]? ValueBias,
    float[] FfnNorm, WeightMatrix Gate, WeightMatrix Up, WeightMatrix Down);

/// <summary>
/// A llama or qwen2 decoder-only transformer read from a GGUF file (llama.cpp's tensor names: <c>token_embd</c>, <c>blk.N.attn_*</c>,
/// <c>blk.N.ffn_*</c>, <c>output_norm</c>, <c>output</c>, tied to the embedding when absent). The weights stay in the file's
/// mapping; the norms and biases (a few kilobytes) are copied out as floats. Loading checks every shape and type the forward pass
/// relies on and throws <see cref="InvalidDataException"/> with the reason for anything it cannot run (another architecture, a
/// quantization outside <see cref="GgmlQuant.SupportedList"/>, a missing tensor, RoPE scaling).
/// </summary>
internal sealed class ChatModel : IDisposable
{
    public static readonly string[] Architectures = ["llama", "qwen2"];

    private ChatModel(GgufFile file, ChatModelConfig config, WeightMatrix embedding, ChatModelLayer[] layers, float[] outputNorm, WeightMatrix output, BpeTokenizer tokenizer)
    {
        File = file;
        Config = config;
        Embedding = embedding;
        Layers = layers;
        OutputNorm = outputNorm;
        Output = output;
        Tokenizer = tokenizer;
        Parameters = file.Tensors.Sum(t => t.Elements);
        Name = file.String("general.name") is { Length: > 0 } name ? Clip(name, 80) : Path.GetFileNameWithoutExtension(file.Path);
        Quantization = QuantizationOf(file);
    }

    public GgufFile File { get; }

    public ChatModelConfig Config { get; }

    public WeightMatrix Embedding { get; }

    public IReadOnlyList<ChatModelLayer> Layers { get; }

    public float[] OutputNorm { get; }

    public WeightMatrix Output { get; }

    public BpeTokenizer Tokenizer { get; }

    public long Parameters { get; }

    public string Name { get; }

    public string Quantization { get; }

    /// <summary>"SmolLM2 360M Instruct llama 362M Q8_0".</summary>
    public string Description => $"{Name} {Config.Architecture} {FormatParameters(Parameters)} {Quantization}";

    /// <summary>Open and check a model file. Throws <see cref="InvalidDataException"/> (or an I/O error) with the reason when it cannot be run.</summary>
    public static ChatModel Load(string path)
    {
        GgufFile file = GgufFile.Open(path);
        try
        {
            return FromFile(file);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Read the model from an open file (which the model then owns).</summary>
    internal static ChatModel FromFile(GgufFile file)
    {
        string arch = file.String("general.architecture") ?? "(missing)";
        if (!Architectures.Contains(arch))
            throw new InvalidDataException($"architecture \"{arch}\" is not supported (only {string.Join(" and ", Architectures)})");

        int Required(string key)
        {
            long value = file.Integer($"{arch}.{key}") ?? throw new InvalidDataException($"{arch}.{key} is missing");
            if (value is < 1 or > 1 << 20) throw new InvalidDataException($"{arch}.{key} is {value}");
            return (int)value;
        }

        int dim = Required("embedding_length");
        int layers = Required("block_count");
        int heads = Required("attention.head_count");
        int kvHeads = (int)(file.Integer($"{arch}.attention.head_count_kv") ?? heads);
        int ffn = Required("feed_forward_length");
        int context = Required("context_length");
        if (kvHeads < 1 || heads % kvHeads != 0) throw new InvalidDataException($"{heads} attention heads cannot share {kvHeads} key/value heads");
        if (layers > 256) throw new InvalidDataException($"{layers} layers is more than this engine runs");
        int headDim = (int)(file.Integer($"{arch}.attention.key_length") ?? (dim / heads));
        long valueDim = file.Integer($"{arch}.attention.value_length") ?? headDim;
        if (headDim < 2 || headDim % 2 != 0 || valueDim != headDim)
            throw new InvalidDataException($"the head size {headDim} (values {valueDim}) is not supported");
        int ropeDims = (int)(file.Integer($"{arch}.rope.dimension_count") ?? headDim);
        if (ropeDims < 2 || ropeDims > headDim || ropeDims % 2 != 0) throw new InvalidDataException($"{arch}.rope.dimension_count {ropeDims} does not fit the head size {headDim}");
        float ropeBase = (float)(file.Real($"{arch}.rope.freq_base") ?? 10000.0);
        if (!float.IsFinite(ropeBase) || ropeBase <= 1) throw new InvalidDataException($"{arch}.rope.freq_base {ropeBase} is not usable");
        string? scaling = file.String($"{arch}.rope.scaling.type");
        if (scaling is not (null or "none")) throw new InvalidDataException($"RoPE scaling \"{scaling}\" is not supported");
        if (file.Tensor("rope_freqs.weight") is not null) throw new InvalidDataException("per-dimension RoPE frequencies (rope_freqs, Llama 3.1+) are not supported");
        float eps = (float)(file.Real($"{arch}.attention.layer_norm_rms_epsilon") ?? 1e-5);
        if (!float.IsFinite(eps) || eps <= 0 || eps > 0.1f) throw new InvalidDataException($"{arch}.attention.layer_norm_rms_epsilon {eps} is not usable");

        WeightMatrix embedding = Matrix(file, "token_embd.weight", dim, null);
        int vocab = embedding.Rows;
        BpeTokenizer tokenizer = BpeTokenizer.FromGguf(file);
        if (tokenizer.VocabularySize > vocab)
            throw new InvalidDataException($"the tokenizer has {tokenizer.VocabularySize} tokens but the embedding only {vocab} rows");
        if (tokenizer.ImStart is null || tokenizer.ImEnd is null)
            throw new InvalidDataException("the vocabulary has no <|im_start|>/<|im_end|> tokens: only ChatML chat models are supported");

        var config = new ChatModelConfig(arch, dim, layers, heads, kvHeads, headDim, ffn, vocab, context, ropeBase, ropeDims, eps, RopeNeox: arch == "qwen2");
        var blocks = new ChatModelLayer[layers];
        for (int l = 0; l < layers; l++)
        {
            string p = $"blk.{l.ToString(CultureInfo.InvariantCulture)}.";
            bool biases = arch == "qwen2";
            blocks[l] = new ChatModelLayer(
                Vector(file, p + "attn_norm.weight", dim, required: true)!,
                Matrix(file, p + "attn_q.weight", dim, config.QueryDim),
                Matrix(file, p + "attn_k.weight", dim, config.KvDim),
                Matrix(file, p + "attn_v.weight", dim, config.KvDim),
                Matrix(file, p + "attn_output.weight", config.QueryDim, dim),
                Vector(file, p + "attn_q.bias", config.QueryDim, required: biases),
                Vector(file, p + "attn_k.bias", config.KvDim, required: biases),
                Vector(file, p + "attn_v.bias", config.KvDim, required: biases),
                Vector(file, p + "ffn_norm.weight", dim, required: true)!,
                Matrix(file, p + "ffn_gate.weight", dim, ffn),
                Matrix(file, p + "ffn_up.weight", dim, ffn),
                Matrix(file, p + "ffn_down.weight", ffn, dim));
        }

        float[] outputNorm = Vector(file, "output_norm.weight", dim, required: true)!;
        WeightMatrix output = file.Tensor("output.weight") is not null ? Matrix(file, "output.weight", dim, vocab) : embedding;
        return new ChatModel(file, config, embedding, blocks, outputNorm, output, tokenizer);
    }

    private static WeightMatrix Matrix(GgufFile file, string name, int columns, int? rows)
    {
        GgufTensor tensor = file.Tensor(name) ?? throw new InvalidDataException($"the tensor {name} is missing");
        if (tensor.Dims.Length != 2 || tensor.Dims[0] != columns || (rows is { } r && tensor.Dims[1] != r))
            throw new InvalidDataException($"the tensor {name} is {tensor.Shape}, expected [{columns},{(rows is { } e ? e.ToString(CultureInfo.InvariantCulture) : "n")}]");
        if (!GgmlQuant.IsSupported(tensor.Type))
            throw new InvalidDataException($"the tensor {name} is {tensor.Type}: this engine reads {GgmlQuant.SupportedList} (pick a Q8_0 or Q4_K_M file)");
        if (tensor.RowBytes < 0) throw new InvalidDataException($"the tensor {name} ({tensor.Type}) has rows of {columns}, not whole blocks");
        return new WeightMatrix(tensor, (int)tensor.Dims[1], columns);
    }

    private static float[]? Vector(GgufFile file, string name, int length, bool required)
    {
        GgufTensor? tensor = file.Tensor(name);
        if (tensor is null)
        {
            if (required) throw new InvalidDataException($"the tensor {name} is missing");
            return null;
        }

        if (tensor.Elements != length || tensor.Dims[0] != length) throw new InvalidDataException($"the tensor {name} is {tensor.Shape}, expected [{length}]");
        if (!GgmlQuant.IsSupported(tensor.Type) || GgmlQuant.RowBytes(tensor.Type, length) < 0)
            throw new InvalidDataException($"the tensor {name} is {tensor.Type}, not readable");
        var values = new float[length];
        GgmlQuant.DequantizeRowScalar(tensor.Type, file.Row(tensor, 0), values);
        foreach (float v in values)
            if (!float.IsFinite(v)) throw new InvalidDataException($"the tensor {name} holds a non-finite value");
        return values;
    }

    /// <summary>The file's quantization: its <c>general.file_type</c> name, else the type holding most of the bytes.</summary>
    private static string QuantizationOf(GgufFile file)
    {
        string? named = file.Integer("general.file_type") switch
        {
            0 => "F32",
            1 => "F16",
            2 => "Q4_0",
            3 => "Q4_1",
            7 => "Q8_0",
            8 => "Q5_0",
            9 => "Q5_1",
            10 => "Q2_K",
            11 => "Q3_K_S",
            12 => "Q3_K_M",
            13 => "Q3_K_L",
            14 => "Q4_K_S",
            15 => "Q4_K_M",
            16 => "Q5_K_S",
            17 => "Q5_K_M",
            18 => "Q6_K",
            32 => "BF16",
            _ => null,
        };
        if (named is not null) return named;
        return file.Tensors.Where(t => t.Dims.Length == 2).GroupBy(t => t.Type).OrderByDescending(g => g.Sum(t => Math.Max(0, t.ByteSize)))
            .Select(g => g.Key.ToString()).FirstOrDefault() ?? "unknown";
    }

    internal static string FormatParameters(long count) => count >= 1_000_000_000
        ? string.Create(CultureInfo.InvariantCulture, $"{count / 1e9:0.##}B")
        : string.Create(CultureInfo.InvariantCulture, $"{count / 1e6:0}M");

    private static string Clip(string text, int max)
    {
        string clean = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= max ? clean : clean[..max];
    }

    public void Dispose() => File.Dispose();
}
