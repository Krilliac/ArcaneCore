namespace ArcaneCore.World.Playerbots.Chat.Inference.Sampling;

/// <summary>
/// ArcaneCore-specific constraint for bot chat replies: one plain WoW chat line. It bans every token that would
/// <list type="bullet">
/// <item>start a role or markup sequence: any token containing <c>|</c> (which also covers <c>&lt;|im_start|&gt;</c>, <c>&lt;|</c> and
/// <c>|&gt;</c>; the 1.12 client treats <c>|</c> as an escape and the server rejects it in chat), any token containing <c>&lt;think</c>
/// or <c>&lt;/think</c>, and, right after a token that ended in <c>&lt;</c> or <c>&lt;/</c>, a token that continues it into
/// <c>think</c>;</item>
/// <item>start a second line: a token containing a line break once the line has visible content (leading blank tokens are left to the
/// caller to trim);</item>
/// <item>appear in an optional extra ban set (for later grounding).</item>
/// </list>
/// Exempt ids (end-of-turn / EOS) are never banned, so the model can always finish: <c>&lt;|im_end|&gt;</c> contains <c>|</c> and would
/// otherwise be unreachable.
/// </summary>
/// <remarks>
/// Token texts are the decoded strings of each vocabulary entry (the bytes the token writes, as UTF-8), not the raw byte-level BPE
/// pieces. Flags are computed once at construction; while the candidates are still in vocabulary order the bans are written by index
/// (O(banned)), otherwise one flag lookup per candidate. No per-step allocation.
/// </remarks>
internal sealed class ChatLineConstraint : IConstraint
{
    [Flags]
    private enum TokenFlags : byte
    {
        None = 0,
        Markup = 1,
        LineBreak = 2,
        ThinkContinuation = 4,
        EndsWithOpenAngle = 8,
        Visible = 16,
        Extra = 32,
        Exempt = 64,
    }

    private readonly TokenFlags[] _flags;
    private readonly int[] _alwaysBanned;
    private readonly int[] _lineBreaks;
    private readonly int[] _thinkContinuations;
    private bool _hasContent;
    private bool _afterOpenAngle;

    /// <param name="tokenTexts">Decoded text of every vocabulary entry, index = token id. Null entries are treated as plain text.</param>
    /// <param name="exemptTokens">Ids never banned (end-of-turn, EOS).</param>
    /// <param name="bannedTokens">Optional extra ids to ban outright.</param>
    public ChatLineConstraint(IReadOnlyList<string?> tokenTexts, IEnumerable<int>? exemptTokens = null, IEnumerable<int>? bannedTokens = null)
    {
        ArgumentNullException.ThrowIfNull(tokenTexts);
        _flags = new TokenFlags[tokenTexts.Count];
        for (int id = 0; id < _flags.Length; id++)
        {
            _flags[id] = Classify(tokenTexts[id]);
        }

        if (bannedTokens is not null)
        {
            foreach (int id in bannedTokens)
            {
                if ((uint)id < (uint)_flags.Length)
                {
                    _flags[id] |= TokenFlags.Extra;
                }
            }
        }

        if (exemptTokens is not null)
        {
            foreach (int id in exemptTokens)
            {
                if ((uint)id < (uint)_flags.Length)
                {
                    _flags[id] |= TokenFlags.Exempt;
                }
            }
        }

        var always = new List<int>();
        var lines = new List<int>();
        var think = new List<int>();
        for (int id = 0; id < _flags.Length; id++)
        {
            TokenFlags f = _flags[id];
            if ((f & TokenFlags.Exempt) != 0)
            {
                continue;
            }

            if ((f & (TokenFlags.Markup | TokenFlags.Extra)) != 0)
            {
                always.Add(id);
                continue;
            }

            if ((f & TokenFlags.LineBreak) != 0)
            {
                lines.Add(id);
            }

            if ((f & TokenFlags.ThinkContinuation) != 0)
            {
                think.Add(id);
            }
        }

        _alwaysBanned = [.. always];
        _lineBreaks = [.. lines];
        _thinkContinuations = [.. think];
    }

    public string Name => "chat_line";

    public bool IsComplete => false;

    /// <summary>Number of ids banned on every step (markup and the extra set).</summary>
    public int AlwaysBannedCount => _alwaysBanned.Length;

    public void Apply(Candidates candidates)
    {
        if (candidates.IdOrdered)
        {
            Span<TokenData> span = candidates.Span;
            Ban(span, _alwaysBanned);
            if (_hasContent)
            {
                Ban(span, _lineBreaks);
            }

            if (_afterOpenAngle)
            {
                Ban(span, _thinkContinuations);
            }
        }
        else
        {
            foreach (ref TokenData t in candidates.Span)
            {
                if (IsBanned(t.Id))
                {
                    t.Logit = float.NegativeInfinity;
                }
            }
        }

        candidates.Sorted = false;
    }

    /// <summary>True when <paramref name="token"/> would be masked in the current state.</summary>
    public bool IsBanned(int token)
    {
        if ((uint)token >= (uint)_flags.Length)
        {
            return false;
        }

        TokenFlags f = _flags[token];
        if ((f & TokenFlags.Exempt) != 0)
        {
            return false;
        }

        return (f & (TokenFlags.Markup | TokenFlags.Extra)) != 0
            || (_hasContent && (f & TokenFlags.LineBreak) != 0)
            || (_afterOpenAngle && (f & TokenFlags.ThinkContinuation) != 0);
    }

    public void Accept(int token)
    {
        if ((uint)token >= (uint)_flags.Length)
        {
            _afterOpenAngle = false;
            return;
        }

        TokenFlags f = _flags[token];
        _hasContent |= (f & TokenFlags.Visible) != 0;
        _afterOpenAngle = (f & TokenFlags.EndsWithOpenAngle) != 0;
    }

    public void Reset()
    {
        _hasContent = false;
        _afterOpenAngle = false;
    }

    private static void Ban(Span<TokenData> span, int[] ids)
    {
        foreach (int id in ids)
        {
            if ((uint)id < (uint)span.Length)
            {
                span[id].Logit = float.NegativeInfinity;
            }
        }
    }

    private static TokenFlags Classify(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return TokenFlags.None;
        }

        TokenFlags f = TokenFlags.None;
        if (text.Contains('|') || text.Contains("<think", StringComparison.OrdinalIgnoreCase)
            || text.Contains("</think", StringComparison.OrdinalIgnoreCase))
        {
            f |= TokenFlags.Markup;
        }

        if (text.Contains('\n') || text.Contains('\r'))
        {
            f |= TokenFlags.LineBreak;
        }

        if (text.StartsWith("think", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/think", StringComparison.OrdinalIgnoreCase))
        {
            f |= TokenFlags.ThinkContinuation;
        }

        if (text.EndsWith('<') || text.EndsWith("</", StringComparison.Ordinal))
        {
            f |= TokenFlags.EndsWithOpenAngle;
        }

        foreach (char ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                f |= TokenFlags.Visible;
                break;
            }
        }

        return f;
    }
}
