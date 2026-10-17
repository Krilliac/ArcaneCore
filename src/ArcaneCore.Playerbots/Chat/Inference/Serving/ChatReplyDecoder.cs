using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ArcaneCore.World.Playerbots.Chat.Inference.Sampling;

namespace ArcaneCore.World.Playerbots.Chat.Inference.Serving;

/// <summary>Why a reply's token loop ended.</summary>
internal enum ChatStopReason : byte
{
    /// <summary>Still writing.</summary>
    None,

    /// <summary>An end-of-turn / end-of-text / control token.</summary>
    StopToken,

    /// <summary>A configured stop string.</summary>
    StopSequence,

    /// <summary>A line break after the first visible character (the reply is one chat line).</summary>
    Newline,

    /// <summary>Every candidate was masked (the constraint left nothing).</summary>
    NoCandidate,
}

/// <summary>
/// One reply's token selection on a generation slot: the ported <see cref="SamplerChain"/> (logit bias, penalties, top-k, typical-p,
/// top-p, min-p, temperature, then a seeded draw or greedy) behind the <see cref="ChatLineConstraint"/> (no markup, no role tokens,
/// no think tags, no second line), the token-level stops (end of turn, end of text, control tokens) and the text-level
/// <see cref="StopSequenceMatcher"/>. The chain and constraint are built once per slot and reset per reply; a reply's state can be
/// rebuilt from its prompt and the tokens written so far (<see cref="Begin"/>), so a sequence preempted and resumed on another slot
/// carries on with the same penalties and constraint state. After warm-up nothing allocates per token.
/// </summary>
internal sealed class ChatReplyDecoder
{
    private readonly BpeTokenizer _tokenizer;
    private readonly int _vocabulary;
    private readonly ChatLineConstraint _constraint;
    private readonly int[] _stopTokens;
    private readonly List<byte> _piece = new(16);
    private readonly List<byte> _text = new(256);
    private SamplerChain? _chain;
    private ChatGenerationSettings? _built;
    private StopSequenceMatcher? _stops;
    private IReadOnlyList<string>? _stopsFor;
    private bool _content;
    private bool _oneLine = true;

    public ChatReplyDecoder(ChatModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _tokenizer = model.Tokenizer;
        _vocabulary = model.Config.Vocabulary;
        var stops = new List<int>(3);
        foreach (int? id in new[] { _tokenizer.Eos, _tokenizer.ImEnd, _tokenizer.EndOfText })
            if (id is { } value && (uint)value < (uint)_vocabulary && !stops.Contains(value)) stops.Add(value);
        _stopTokens = [.. stops];
        _constraint = new ChatLineConstraint(ChatTokenTexts.For(_tokenizer), _stopTokens);
    }

    /// <summary>The chain in use (after <see cref="Begin"/>).</summary>
    internal SamplerChain? Chain => _chain;

    /// <summary>The reply so far (stop strings removed, a held-back possible stop prefix not yet included).</summary>
    public int TextBytes => _text.Count;

    /// <summary>
    /// Start (or resume) a reply: build the chain when <paramref name="settings"/> changed, reset it, seed it, feed the prompt's
    /// last <see cref="ChatGenerationSettings.PenaltyWindow"/> tokens into the penalty history and replay
    /// <paramref name="generated"/> tokens already written (penalties, constraint, text).
    /// </summary>
    public void Begin(ChatGenerationSettings settings, ReadOnlySpan<int> prompt, ReadOnlySpan<int> generated)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ChatGenerationSettings key = settings with { BudgetMs = 0, MaxNewTokens = 0, Seed = null, StopSequences = null };
        if (_chain is null || _built != key)
        {
            _chain = SamplerChain.Build(Config(settings, _stopTokens), settings.StopAtNewline ? _constraint : null, _vocabulary);
            _built = key;
        }

        _oneLine = settings.StopAtNewline;

        _chain.Reset();
        _chain.Reseed(settings.Seed is { } seed ? (ulong)(uint)seed : SamplingRng.EntropySeed());
        int window = Math.Min(prompt.Length, Math.Max(0, settings.PenaltyWindow));
        _chain.AcceptPrompt(prompt[^window..]);
        if (!ReferenceEquals(_stopsFor, settings.StopSequences))
        {
            _stops = settings.StopSequences is { Count: > 0 } list ? new StopSequenceMatcher(list) : null;
            _stopsFor = settings.StopSequences;
        }

        _stops?.Reset();
        _text.Clear();
        _content = false;
        foreach (int token in generated) Commit(token);
    }

    /// <summary>Choose the next token from <paramref name="logits"/>; <see cref="ChatStopReason.NoCandidate"/> when none is allowed.</summary>
    public int Choose(ReadOnlySpan<float> logits, out ChatStopReason stop)
    {
        SampleResult result = _chain!.Sample(logits);
        if (!result.Ok)
        {
            stop = ChatStopReason.NoCandidate;
            return SamplingTokens.Invalid;
        }

        int token = result.Token;
        stop = _chain.IsStopToken(token) || _tokenizer.IsControl(token) ? ChatStopReason.StopToken : ChatStopReason.None;
        return token;
    }

    /// <summary>
    /// Commit a chosen (non-stop) token: penalties and constraint advance, its bytes join the reply. A line break before any visible
    /// character is dropped (leading blank lines are trimmed); one after it ends the line (<see cref="ChatStopReason.Newline"/>, the
    /// text before the break is kept). A completed stop string ends the reply (<see cref="ChatStopReason.StopSequence"/>).
    /// </summary>
    public ChatStopReason Commit(int token)
    {
        _chain!.Accept(token);
        _piece.Clear();
        _tokenizer.AppendBytes(token, _piece);
        ReadOnlySpan<byte> piece = CollectionsMarshal.AsSpan(_piece);
        ChatStopReason stop = ChatStopReason.None;
        int cut = _oneLine ? piece.IndexOfAny((byte)'\n', (byte)'\r') : -1;
        if (cut >= 0)
        {
            if (_content || HasVisible(piece[..cut]))
            {
                piece = piece[..cut];
                stop = ChatStopReason.Newline;
            }
            else
            {
                piece = piece[(cut + 1)..].TrimStart((byte)'\n').TrimStart((byte)'\r');
            }
        }

        if (!_content && HasVisible(piece)) _content = true;
        if (_stops is { } stops)
        {
            if (stops.Feed(piece).Stopped)
            {
                Append(stops.Emitted);
                return ChatStopReason.StopSequence;
            }

            Append(stops.Emitted);
            if (stop != ChatStopReason.None) Append(stops.Flush());
        }
        else
        {
            Append(piece);
        }

        return stop;
    }

    /// <summary>The reply text (any held-back possible stop prefix released).</summary>
    public string Text()
    {
        if (_stops is { Stopped: false } stops) Append(stops.Flush());
        return Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_text));
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes) _text.Add(b);
    }

    private static bool HasVisible(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')) return true;
        return false;
    }

    /// <summary>The ported chain's config for the in-process model's settings.</summary>
    internal static SamplerConfig Config(ChatGenerationSettings settings, IReadOnlyList<int> stopTokens)
    {
        bool greedy = settings.Temperature <= 0f || settings.TopK == 1;
        return new SamplerConfig
        {
            Greedy = greedy,
            Temperature = Math.Max(0f, settings.Temperature),
            TopK = Math.Max(0, settings.TopK),
            TopP = Math.Clamp(settings.TopP, 0f, 1f),
            MinP = Math.Clamp(settings.MinP, 0f, 1f),
            TypicalP = 1f,
            RepeatPenalty = settings.RepetitionPenalty > 0f ? settings.RepetitionPenalty : 1f,
            PenaltyLastN = Math.Max(0, settings.PenaltyWindow),
            StopTokens = [.. stopTokens],
        };
    }
}

/// <summary>The decoded text of every vocabulary entry (control and user-defined tokens by their written form), built once per tokenizer for <see cref="ChatLineConstraint"/>.</summary>
internal static class ChatTokenTexts
{
    private static readonly ConditionalWeakTable<BpeTokenizer, string?[]> Cache = [];

    public static IReadOnlyList<string?> For(BpeTokenizer tokenizer)
        => Cache.GetValue(tokenizer, static t =>
        {
            var texts = new string?[t.VocabularySize];
            var bytes = new List<byte>(32);
            for (int id = 0; id < texts.Length; id++)
            {
                if (t.IsControl(id) || t.TokenType(id) == BpeTokenizer.TypeUserDefined)
                {
                    texts[id] = t.TokenText(id);
                    continue;
                }

                bytes.Clear();
                t.AppendBytes(id, bytes);
                texts[id] = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(bytes));
            }

            return texts;
        });
}
