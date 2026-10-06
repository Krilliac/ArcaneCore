using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Playerbots;

/// <summary>Configuration policy for server-managed autonomous players.</summary>
public sealed class PlayerbotOptions
{
    public const string SectionName = "World:Playerbots";

    public bool Enabled { get; set; }
    public bool RestoreOnStartup { get; set; }
    public int MaxBots { get; set; } = 8;
    public int ThinkIntervalMs { get; set; } = 500;
    public int MaxActionsPerTick { get; set; } = 4;
    public int MaxPathPoints { get; set; } = 128;
    public float MaxRouteYards { get; set; } = 2000f;
    public float MoveSpeed { get; set; } = 7f;
    public uint[] AllowedMaps { get; set; } = [0, 1];
    public bool AllowLocalLlm { get; set; }
    public string LocalLlmModel { get; set; } = "R4C3R/qwen3-0.6b-heretic:q4_k_m";
    public int LocalLlmContextSize { get; set; } = 1024;
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
        if (section.GetSection(nameof(AllowedMaps)).Exists()) options.AllowedMaps = [];
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
