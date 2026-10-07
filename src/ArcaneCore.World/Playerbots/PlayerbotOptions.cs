using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Playerbots;

/// <summary>Configuration policy for server-managed autonomous players.</summary>
public sealed class PlayerbotOptions
{
    public const string SectionName = "World:Playerbots";

    /// <summary>Run server-managed playerbots (off by default).</summary>
    public bool Enabled { get; set; }

    /// <summary>Log the saved managed bots back in when the world starts.</summary>
    public bool RestoreOnStartup { get; set; }

    /// <summary>The most managed bots online at once (0..64).</summary>
    public int MaxBots { get; set; } = 8;

    /// <summary>Milliseconds between two decisions of a bot (50..60000).</summary>
    public int ThinkIntervalMs { get; set; } = 500;

    /// <summary>The most managed client actions all bots may send in one world tick (1..64).</summary>
    public int MaxActionsPerTick { get; set; } = 4;

    /// <summary>The most points of one bot route (1..4096).</summary>
    public int MaxPathPoints { get; set; } = 128;

    /// <summary>The longest bot route in yards (above 0, at most 100000).</summary>
    public float MaxRouteYards { get; set; } = 2000f;

    /// <summary>The bots' movement speed in yards per second, never above the player's run speed (above 0, at most 100).</summary>
    public float MoveSpeed { get; set; } = 7f;

    /// <summary>The maps bots may act and travel on (non-empty, no duplicates; a configured list replaces the default continents 0 and 1).</summary>
    public uint[] AllowedMaps { get; set; } = [0, 1];

    /// <summary>Let a local language model choose among the bot's candidate goals (off by default; the rules choose otherwise).</summary>
    public bool AllowLocalLlm { get; set; }

    /// <summary>The local model name sent to the loopback inference endpoint (at most 128 characters).</summary>
    public string LocalLlmModel { get; set; } = "R4C3R/qwen3-0.6b-heretic:q4_k_m";

    /// <summary>The local model's context size in tokens (512..2048).</summary>
    public int LocalLlmContextSize { get; set; } = 1024;

    /// <summary>Milliseconds a local model choice may take before the rules decide (100..10000).</summary>
    public int LocalLlmTimeoutMs { get; set; } = 5000;

    public static PlayerbotOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new PlayerbotOptions();
        ApplyConfiguration(options, configuration);
        return options;
    }

    internal static void ApplyConfiguration(PlayerbotOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);
        // ConfigurationBinder appends array elements to a non-empty initialized array.
        // Explicit maps replace the defaults; missing maps retain the default continents.
        if (section.GetChildren().Any(child => string.Equals(child.Key, nameof(AllowedMaps), StringComparison.OrdinalIgnoreCase))) options.AllowedMaps = [];
        section.Bind(options);
    }

    public void Validate()
    {
        if (MaxBots is < 0 or > 64) throw new InvalidOperationException($"{SectionName}: MaxBots must be 0..64.");
        if (ThinkIntervalMs is < 50 or > 60_000) throw new InvalidOperationException($"{SectionName}: ThinkIntervalMs must be 50..60000.");
        if (MaxActionsPerTick is < 1 or > 64) throw new InvalidOperationException($"{SectionName}: MaxActionsPerTick must be 1..64.");
        if (MaxPathPoints is < 1 or > 4096) throw new InvalidOperationException($"{SectionName}: MaxPathPoints must be 1..4096.");
        if (!float.IsFinite(MaxRouteYards) || MaxRouteYards <= 0 || MaxRouteYards > 100_000)
            throw new InvalidOperationException($"{SectionName}: MaxRouteYards must be finite and > 0..100000.");
        if (!float.IsFinite(MoveSpeed) || MoveSpeed <= 0 || MoveSpeed > 100)
            throw new InvalidOperationException($"{SectionName}: MoveSpeed must be finite and > 0..100.");
        if (AllowedMaps is null || AllowedMaps.Length == 0 || AllowedMaps.Distinct().Count() != AllowedMaps.Length)
            throw new InvalidOperationException($"{SectionName}: AllowedMaps must be non-empty and contain no duplicates.");
        if (string.IsNullOrWhiteSpace(LocalLlmModel) || LocalLlmModel.Length > 128 || LocalLlmModel.Any(char.IsControl)
            || LocalLlmContextSize is < 512 or > 2048 || LocalLlmTimeoutMs is < 100 or > 10_000)
            throw new InvalidOperationException($"{SectionName}: invalid bounded local model settings.");
    }
}
