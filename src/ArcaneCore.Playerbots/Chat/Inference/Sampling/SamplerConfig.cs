// Ported from Krilliac/Sonder-Inference src/sampling/include/sonder/sampling/config.hpp and src/sampling/src/config.cpp @1c4c152 (Apache-2.0)

using System.Globalization;
using System.Text;

namespace ArcaneCore.World.Playerbots.Chat.Inference.Sampling;

/// <summary>
/// Transforming / truncating stages whose relative order is configurable. Logit bias and the constraint always run first; the final
/// selector (greedy or seeded distribution) always runs last.
/// </summary>
internal enum StageKind : byte
{
    Penalties,
    TopK,
    TypicalP,
    TopP,
    MinP,
    Temperature,
}

/// <summary>Stage names (llama.cpp <c>--samplers</c> spellings) and the default order.</summary>
internal static class StageKinds
{
    /// <summary>The stage's canonical name, or "unknown" for a value outside the enum.</summary>
    public static string ToName(StageKind kind) => kind switch
    {
        StageKind.Penalties => "penalties",
        StageKind.TopK => "top_k",
        StageKind.TypicalP => "typ_p",
        StageKind.TopP => "top_p",
        StageKind.MinP => "min_p",
        StageKind.Temperature => "temperature",
        _ => "unknown",
    };

    /// <summary>Parse "penalties", "top_k", "typ_p"/"typical_p"/"typical", "top_p", "min_p", "temperature"/"temp".</summary>
    public static StageKind? Parse(string? name) => name switch
    {
        "penalties" => StageKind.Penalties,
        "top_k" => StageKind.TopK,
        "typ_p" or "typical_p" or "typical" => StageKind.TypicalP,
        "top_p" => StageKind.TopP,
        "min_p" => StageKind.MinP,
        "temperature" or "temp" => StageKind.Temperature,
        _ => null,
    };

    /// <summary>llama.cpp's default order restricted to the stages implemented here: penalties, top_k, typ_p, top_p, min_p, temperature.</summary>
    public static List<StageKind> DefaultOrder() =>
        [StageKind.Penalties, StageKind.TopK, StageKind.TypicalP, StageKind.TopP, StageKind.MinP, StageKind.Temperature];
}

/// <summary>Adds <see cref="Bias"/> to one token's logit. -inf bans the token.</summary>
internal readonly record struct LogitBias(int Token, float Bias);

/// <summary>
/// Full sampling policy. Defaults mirror llama.cpp's common defaults, so a default config samples like <c>llama-cli</c> with no flags.
/// </summary>
internal sealed class SamplerConfig
{
    /// <summary>Always pick the highest-logit token after bias/constraint/penalties; truncation and temperature stages are not built.</summary>
    public bool Greedy { get; set; }

    /// <summary>RNG seed. Null draws an entropy seed when the chain is built.</summary>
    public ulong? Seed { get; set; }

    /// <summary>0 keeps only the argmax of the remaining set. Must be finite and &gt;= 0.</summary>
    public float Temperature { get; set; } = 0.8f;

    /// <summary>0 = disabled. Values above the vocabulary keep everything.</summary>
    public int TopK { get; set; } = 40;

    /// <summary>1 = disabled. Range [0, 1].</summary>
    public float TopP { get; set; } = 0.95f;

    /// <summary>0 = disabled. Range [0, 1].</summary>
    public float MinP { get; set; } = 0.05f;

    /// <summary>1 = disabled. Range [0, 1].</summary>
    public float TypicalP { get; set; } = 1.0f;

    /// <summary>Minimum candidates kept by top_p/min_p/typical_p (0 is treated as 1).</summary>
    public int MinKeep { get; set; }

    /// <summary>Window of accepted tokens used for penalties. 0 = disabled, -1 = unbounded.</summary>
    public int PenaltyLastN { get; set; } = 64;

    /// <summary>1 = disabled. Must be finite and &gt; 0.</summary>
    public float RepeatPenalty { get; set; } = 1.0f;

    /// <summary>0 = disabled. Negative values encourage repetition.</summary>
    public float FrequencyPenalty { get; set; }

    /// <summary>0 = disabled.</summary>
    public float PresencePenalty { get; set; }

    public List<LogitBias> LogitBiases { get; set; } = [];

    /// <summary>Text-level stops; see <see cref="StopSequenceMatcher"/>.</summary>
    public List<string> StopSequences { get; set; } = [];

    /// <summary>Token-level stops (EOS / EOT ids).</summary>
    public List<int> StopTokens { get; set; } = [];

    public List<StageKind> StageOrder { get; set; } = StageKinds.DefaultOrder();

    /// <summary>
    /// Validate every field. When <paramref name="vocabSize"/> is known, token ids (logit bias, stop tokens) are also range-checked.
    /// Reports every problem, not just the first.
    /// </summary>
    public ValidationResult Validate(int? vocabSize = null)
    {
        var r = new ValidationResult();

        if (vocabSize is <= 0)
        {
            r.Add("vocab_size", "must be > 0");
        }

        if (!float.IsFinite(Temperature) || Temperature < 0f)
        {
            r.Add("temperature", "must be a finite value >= 0 (0 = greedy)" + Got(Temperature));
        }

        if (TopK < 0)
        {
            r.Add("top_k", "must be >= 0 (0 = disabled)" + Got(TopK));
        }

        CheckUnitInterval(r, "top_p", TopP);
        CheckUnitInterval(r, "min_p", MinP);
        CheckUnitInterval(r, "typical_p", TypicalP);
        if (MinKeep < 0)
        {
            r.Add("min_keep", "must be >= 0" + Got(MinKeep));
        }

        if (PenaltyLastN < -1)
        {
            r.Add("penalty_last_n", "must be >= -1 (-1 = unbounded, 0 = disabled)" + Got(PenaltyLastN));
        }

        if (!float.IsFinite(RepeatPenalty) || RepeatPenalty <= 0f)
        {
            r.Add("repeat_penalty", "must be a finite value > 0 (1 = disabled)" + Got(RepeatPenalty));
        }

        if (!float.IsFinite(FrequencyPenalty))
        {
            r.Add("frequency_penalty", "must be finite" + Got(FrequencyPenalty));
        }

        if (!float.IsFinite(PresencePenalty))
        {
            r.Add("presence_penalty", "must be finite" + Got(PresencePenalty));
        }

        for (int i = 0; i < LogitBiases.Count; i++)
        {
            LogitBias b = LogitBiases[i];
            string field = $"logit_bias[{i}]";
            CheckToken(r, field, b.Token, vocabSize);
            if (float.IsNaN(b.Bias) || float.IsPositiveInfinity(b.Bias))
            {
                r.Add(field, "bias must be finite or -inf (to ban a token)" + Got(b.Bias));
            }
        }

        for (int i = 0; i < StopSequences.Count; i++)
        {
            if (string.IsNullOrEmpty(StopSequences[i]))
            {
                r.Add($"stop_sequences[{i}]", "must not be empty");
            }
        }

        for (int i = 0; i < StopTokens.Count; i++)
        {
            CheckToken(r, $"stop_tokens[{i}]", StopTokens[i], vocabSize);
        }

        Span<bool> seen = stackalloc bool[256];
        for (int i = 0; i < StageOrder.Count; i++)
        {
            StageKind kind = StageOrder[i];
            string name = StageKinds.ToName(kind);
            if (name == "unknown")
            {
                r.Add($"stage_order[{i}]", "unknown stage kind");
            }
            else if (seen[(byte)kind])
            {
                r.Add($"stage_order[{i}]", $"duplicate stage '{name}'");
            }
            else
            {
                seen[(byte)kind] = true;
            }
        }

        return r;
    }

    private static string Got(float value) => " (got " + value.ToString(CultureInfo.InvariantCulture) + ")";

    private static string Got(int value) => " (got " + value.ToString(CultureInfo.InvariantCulture) + ")";

    private static void CheckUnitInterval(ValidationResult r, string field, float v)
    {
        if (!float.IsFinite(v) || v < 0f || v > 1f)
        {
            r.Add(field, "must be a finite value in [0, 1]" + Got(v));
        }
    }

    private static void CheckToken(ValidationResult r, string field, int id, int? vocabSize)
    {
        if (id < 0)
        {
            r.Add(field, "token id must be >= 0" + Got(id));
        }
        else if (vocabSize is { } vocab && id >= vocab)
        {
            r.Add(field, "token id must be < vocab size " + vocab.ToString(CultureInfo.InvariantCulture) + Got(id));
        }
    }
}

/// <summary>One failing config field.</summary>
internal readonly record struct ConfigError(string Field, string Message);

/// <summary>Every problem <see cref="SamplerConfig.Validate"/> found.</summary>
internal sealed class ValidationResult
{
    private readonly List<ConfigError> _errors = [];

    public IReadOnlyList<ConfigError> Errors => _errors;

    public bool Ok => _errors.Count == 0;

    public bool HasError(string field)
    {
        foreach (ConfigError e in _errors)
        {
            if (e.Field == field)
            {
                return true;
            }
        }

        return false;
    }

    internal void Add(string field, string message) => _errors.Add(new ConfigError(field, message));

    /// <summary>"sampling config invalid: top_p: must be a finite value in [0, 1] (got 1.5); ..." or "sampling config valid".</summary>
    public override string ToString()
    {
        if (_errors.Count == 0)
        {
            return "sampling config valid";
        }

        var sb = new StringBuilder("sampling config invalid: ");
        for (int i = 0; i < _errors.Count; i++)
        {
            if (i != 0)
            {
                sb.Append("; ");
            }

            sb.Append(_errors[i].Field).Append(": ").Append(_errors[i].Message);
        }

        return sb.ToString();
    }
}
