// Ported from Krilliac/Sonder-Inference src/sampling/include/sonder/sampling/constraint.hpp and src/sampling/src/constraint.cpp @1c4c152 (Apache-2.0)

namespace ArcaneCore.World.Playerbots.Chat.Inference.Sampling;

/// <summary>
/// Grammar / structured-output hook. A constraint masks candidates that would violate some output structure (grammar, chat-line rules,
/// a tool-call format). The chain calls <see cref="Apply"/> before every other stage, so disallowed tokens never receive probability
/// mass, and <see cref="Accept"/> after a token is committed so the constraint can advance. Prompt tokens are not fed to it
/// (<see cref="SamplerChain.AcceptPrompt"/>).
/// </summary>
internal interface IConstraint
{
    string Name { get; }

    /// <summary>Set the logit of every candidate that is not allowed next to -inf.</summary>
    void Apply(Candidates candidates);

    /// <summary>Advance state after <paramref name="token"/> has been committed to the sequence.</summary>
    void Accept(int token);

    /// <summary>Return to the initial state (new generation).</summary>
    void Reset();

    /// <summary>True when the structure is complete and generation may stop.</summary>
    bool IsComplete => false;
}

/// <summary>Reference constraint: only tokens in a fixed allow-list may be sampled. Stateless.</summary>
internal sealed class TokenAllowlistConstraint : IConstraint
{
    private readonly HashSet<int> _allowed;

    public TokenAllowlistConstraint(IEnumerable<int> allowed) => _allowed = [.. allowed];

    public string Name => "allowlist";

    public bool IsComplete => false;

    public void Apply(Candidates candidates)
    {
        foreach (ref TokenData t in candidates.Span)
        {
            if (!_allowed.Contains(t.Id))
            {
                t.Logit = float.NegativeInfinity;
            }
        }

        candidates.Sorted = false;
    }

    public void Accept(int token)
    {
    }

    public void Reset()
    {
    }
}
