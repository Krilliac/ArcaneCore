using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>
/// The byte-level BPE tokenizer of a GGUF model whose <c>tokenizer.ggml.model</c> is <c>gpt2</c> (SmolLM2, Qwen2.5, Llama 3): the
/// vocabulary (<c>tokenizer.ggml.tokens</c>, <c>token_type</c>), the merges (<c>tokenizer.ggml.merges</c>, "left right" in rank order),
/// the pre-tokenizer of <c>tokenizer.ggml.pre</c> (llama.cpp llama-vocab.cpp regexes: <c>qwen2</c>, <c>llama-bpe</c>, <c>smollm</c>,
/// <c>gpt2</c>/<c>default</c>), and the special tokens. Text is split by the pre-tokenizer, each piece's UTF-8 bytes are mapped to
/// GPT-2's printable byte alphabet and merged by rank. Special tokens are never parsed out of text: the prompt builder inserts them by
/// id, so a player who types "&lt;|im_start|&gt;" gets plain text tokens.
/// </summary>
internal sealed class BpeTokenizer
{
    /// <summary>llama.cpp <c>llama_token_type</c>.</summary>
    internal const int TypeNormal = 1, TypeUnknown = 2, TypeControl = 3, TypeUserDefined = 4, TypeUnused = 5, TypeByte = 6;

    // llama-vocab.cpp, LLAMA_VOCAB_PRE_TYPE_* regex_exprs.
    internal const string Gpt2Pattern = @"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)";
    internal const string Qwen2Pattern = @"(?:'[sS]|'[tT]|'[rR][eE]|'[vV][eE]|'[mM]|'[lL][lL]|'[dD])|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+";
    internal const string Llama3Pattern = @"(?:'[sS]|'[tT]|'[rR][eE]|'[vV][eE]|'[mM]|'[lL][lL]|'[dD])|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+";

    private const int MaxCachedWords = 8192;

    private static readonly char[] ByteToChar = BuildByteToChar();
    private static readonly Dictionary<char, byte> CharToByte = ByteToChar.Select((c, b) => (c, (byte)b)).ToDictionary(p => p.c, p => p.Item2);

    private readonly string[] _tokens;
    private readonly int[] _types;
    private readonly Dictionary<string, int> _ids;
    private readonly Dictionary<string, int> _ranks;
    private readonly Regex[] _split;
    private readonly Dictionary<string, int[]> _cache = new(StringComparer.Ordinal);
    private readonly Lock _cacheGate = new();

    internal BpeTokenizer(string[] tokens, int[]? types, string[] merges, string pre, int? bos, int? eos, bool addBos)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(merges);
        if (tokens.Length == 0) throw new InvalidDataException("the vocabulary is empty");
        if (types is not null && types.Length != tokens.Length) throw new InvalidDataException($"tokenizer.ggml.token_type has {types.Length} entries for {tokens.Length} tokens");
        _tokens = tokens;
        _types = types ?? Enumerable.Repeat(TypeNormal, tokens.Length).ToArray();
        _ids = new Dictionary<string, int>(tokens.Length, StringComparer.Ordinal);
        for (int i = 0; i < tokens.Length; i++) _ids.TryAdd(tokens[i], i);
        _ranks = new Dictionary<string, int>(merges.Length, StringComparer.Ordinal);
        for (int i = 0; i < merges.Length; i++)
        {
            string merge = merges[i];
            int space = merge.IndexOf(' ', 1);
            if (space <= 0 || space == merge.Length - 1) throw new InvalidDataException($"merge {i} \"{merge}\" is not \"left right\"");
            _ranks.TryAdd(merge, i);
        }

        Pre = string.IsNullOrEmpty(pre) ? "default" : pre;
        (string[] Patterns, bool Exact) choice = Pre switch
        {
            "qwen2" or "deepseek-r1-qwen" => ([Qwen2Pattern], true),
            "llama-bpe" or "llama3" => ([Llama3Pattern], true),
            "smollm" or "starcoder" or "refact" or "codeshell" => ([@"\p{N}", Gpt2Pattern], true),
            "gpt2" or "default" or "gpt-2" => ([Gpt2Pattern], true),
            _ => ([Gpt2Pattern], false),
        };
        PreExact = choice.Exact;
        _split = [.. choice.Patterns.Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))];
        Bos = bos is { } b && b >= 0 && b < tokens.Length ? b : null;
        Eos = eos is { } e && e >= 0 && e < tokens.Length ? e : null;
        AddBos = addBos && Bos is not null;
        ImStart = Id("<|im_start|>");
        ImEnd = Id("<|im_end|>");
        EndOfText = Id("<|endoftext|>");
    }

    /// <summary>Read the tokenizer from a GGUF file's metadata. Throws <see cref="InvalidDataException"/> when it is not a gpt2 BPE tokenizer.</summary>
    internal static BpeTokenizer FromGguf(GgufFile file)
    {
        string model = file.String("tokenizer.ggml.model") ?? "(missing)";
        if (model != "gpt2")
            throw new InvalidDataException($"tokenizer.ggml.model is \"{model}\": only the gpt2 byte-level BPE tokenizer is supported (SmolLM2, Qwen2.5)");
        string[] tokens = file.Array<string>("tokenizer.ggml.tokens") ?? throw new InvalidDataException("tokenizer.ggml.tokens is missing");
        string[] merges = file.Array<string>("tokenizer.ggml.merges") ?? throw new InvalidDataException("tokenizer.ggml.merges is missing");
        int[]? types = file.IntArray("tokenizer.ggml.token_type");
        long? bos = file.Integer("tokenizer.ggml.bos_token_id");
        long? eos = file.Integer("tokenizer.ggml.eos_token_id");
        bool addBos = file.Boolean("tokenizer.ggml.add_bos_token") ?? false;
        return new BpeTokenizer(tokens, types, merges, file.String("tokenizer.ggml.pre") ?? "default", (int?)bos, (int?)eos, addBos);
    }

    public int VocabularySize => _tokens.Length;

    /// <summary>The pre-tokenizer name (<c>tokenizer.ggml.pre</c>).</summary>
    public string Pre { get; }

    /// <summary>False when <see cref="Pre"/> is unknown and the gpt2 split stands in for it (tokens may differ from the model's own).</summary>
    public bool PreExact { get; }

    public int? Bos { get; }

    public int? Eos { get; }

    public bool AddBos { get; }

    public int? ImStart { get; }

    public int? ImEnd { get; }

    public int? EndOfText { get; }

    public string TokenText(int id) => _tokens[id];

    public int TokenType(int id) => _types[id];

    /// <summary>A control or unused token (never part of a reply: generation ends on one).</summary>
    public bool IsControl(int id) => _types[id] is TypeControl or TypeUnused;

    public int? Id(string token) => _ids.TryGetValue(token, out int id) ? id : null;

    /// <summary>Encode plain text (no special tokens are recognised in it).</summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int>(text.Length / 3 + 4);
        EncodeInto(text, ids);
        return ids;
    }

    public void EncodeInto(string text, List<int> ids)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (string piece in PreTokenize(text)) ids.AddRange(EncodePiece(piece));
    }

    /// <summary>The pre-tokenizer's pieces: each regex in turn splits the previous pieces; text between matches stays a piece of its own.</summary>
    internal List<string> PreTokenize(string text)
    {
        var pieces = new List<string> { text };
        foreach (Regex regex in _split)
        {
            var next = new List<string>(pieces.Count * 2);
            foreach (string piece in pieces)
            {
                int at = 0;
                foreach (Match match in regex.Matches(piece))
                {
                    if (match.Length == 0) continue;
                    if (match.Index > at) next.Add(piece[at..match.Index]);
                    next.Add(match.Value);
                    at = match.Index + match.Length;
                }

                if (at < piece.Length) next.Add(piece[at..]);
            }

            pieces = next;
        }

        return pieces;
    }

    private int[] EncodePiece(string piece)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(piece, out int[]? cached)) return cached;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(piece);
        var symbols = new List<string>(bytes.Length);
        foreach (byte b in bytes) symbols.Add(ByteToChar[b].ToString());
        while (symbols.Count > 1)
        {
            int best = -1, bestRank = int.MaxValue;
            for (int i = 0; i < symbols.Count - 1; i++)
            {
                if (_ranks.TryGetValue(symbols[i] + " " + symbols[i + 1], out int rank) && rank < bestRank)
                {
                    bestRank = rank;
                    best = i;
                }
            }

            if (best < 0) break;
            string left = symbols[best], right = symbols[best + 1];
            var merged = new List<string>(symbols.Count);
            for (int i = 0; i < symbols.Count; i++)
            {
                if (i < symbols.Count - 1 && symbols[i] == left && symbols[i + 1] == right)
                {
                    merged.Add(left + right);
                    i++;
                }
                else
                {
                    merged.Add(symbols[i]);
                }
            }

            symbols = merged;
        }

        var ids = new List<int>(symbols.Count);
        foreach (string symbol in symbols)
        {
            if (_ids.TryGetValue(symbol, out int id))
            {
                ids.Add(id);
                continue;
            }

            // A symbol the vocabulary lacks (never for a complete byte-level vocabulary): its single bytes, then nothing.
            foreach (char c in symbol)
                if (_ids.TryGetValue(c.ToString(), out int single)) ids.Add(single);
        }

        int[] result = [.. ids];
        lock (_cacheGate)
        {
            if (_cache.Count >= MaxCachedWords) _cache.Clear();
            _cache[piece] = result;
        }

        return result;
    }

    /// <summary>Append token <paramref name="id"/>'s bytes to <paramref name="bytes"/> (control tokens add nothing).</summary>
    public void AppendBytes(int id, List<byte> bytes)
    {
        if ((uint)id >= (uint)_tokens.Length) return;
        int type = _types[id];
        if (type is TypeControl or TypeUnused) return;
        string text = _tokens[id];
        if (type == TypeByte && text.Length == 6 && text.StartsWith("<0x", StringComparison.Ordinal)
            && byte.TryParse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte raw))
        {
            bytes.Add(raw);
            return;
        }

        if (type == TypeUserDefined)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(text));
            return;
        }

        foreach (char c in text)
        {
            if (CharToByte.TryGetValue(c, out byte b)) bytes.Add(b);
            else bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
        }
    }

    /// <summary>Whether token <paramref name="id"/>'s text holds a line break (generation stops there).</summary>
    public bool HasNewline(int id)
    {
        if ((uint)id >= (uint)_tokens.Length || IsControl(id)) return false;
        string text = _tokens[id];
        return text.Contains(ByteToChar['\n']) || text.Contains(ByteToChar['\r']) || text == "<0x0A>" || text == "<0x0D>";
    }

    public string Decode(IEnumerable<int> ids)
    {
        var bytes = new List<byte>();
        foreach (int id in ids) AppendBytes(id, bytes);
        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>GPT-2's <c>bytes_to_unicode</c>: printable bytes map to themselves, the others to U+0100 upwards.</summary>
    private static char[] BuildByteToChar()
    {
        var map = new char[256];
        bool[] direct = new bool[256];
        for (int b = '!'; b <= '~'; b++) direct[b] = true;
        for (int b = 0xA1; b <= 0xAC; b++) direct[b] = true;
        for (int b = 0xAE; b <= 0xFF; b++) direct[b] = true;
        int next = 0;
        for (int b = 0; b < 256; b++) map[b] = direct[b] ? (char)b : (char)(256 + next++);
        return map;
    }

    /// <summary>The byte-level alphabet character of <paramref name="b"/> (tests build vocabularies with it).</summary>
    internal static char ByteChar(byte b) => ByteToChar[b];
}
