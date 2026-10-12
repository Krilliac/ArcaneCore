using System.Globalization;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// The respawn tuning of the world (<c>World:Respawn</c>). Today it holds the population-scaled respawn of creatures and gathering and
/// quest game objects (<see cref="Dynamic"/>, docs/areas/creature-movement-spawns.md, "Population-scaled respawn").
/// </summary>
public sealed class WorldRespawnOptions
{
    /// <summary>
    /// Population-scaled respawn delays (<c>World:Respawn:Dynamic</c>): the more players stand around a spawn when it dies, the sooner it
    /// comes back. Off by default, which is retail 1.12.1.
    /// </summary>
    public DynamicRespawnOptions Dynamic { get; set; } = new();
}

/// <summary>
/// <c>World:Respawn:Dynamic</c>. With <see cref="Enabled"/> a dying open-world spawn counts the players within <see cref="Radius"/> yards of
/// its home position once, at death, and its respawn delay becomes <c>base / (1 + ScalePerPlayer * max(0, players - PlayerThreshold))</c>,
/// never below <c>base * (1 - MaxReduction)</c> or <see cref="MinimumSeconds"/>. ArcaneCore's own design (the idea is TrinityCore's and
/// AzerothCore's "dynamic respawn rate", the formula and the radius count are not theirs): a kill-quest area with hundreds of players is
/// otherwise capped by the spawns' respawn time. Every key is live (<c>.reload config</c>) and validated; a death uses the values in force at
/// that moment, a respawn already scheduled keeps its time.
/// </summary>
public sealed class DynamicRespawnOptions
{
    /// <summary>Largest value of <see cref="PlayerThreshold"/>.</summary>
    public const int MaxPlayerThreshold = 10_000;

    /// <summary>Largest value of <see cref="ScalePerPlayer"/>.</summary>
    public const float MaxScalePerPlayer = 10f;

    /// <summary>Largest value of <see cref="MinimumSeconds"/> (one day).</summary>
    public const uint MaxMinimumSeconds = 86_400;

    /// <summary>Smallest value of <see cref="Radius"/> (yards).</summary>
    public const float MinRadius = 5f;

    /// <summary>Largest value of <see cref="Radius"/> (yards): a few grid cells, so the count stays a local grid query.</summary>
    public const float MaxRadius = 500f;

    /// <summary>
    /// <c>World:Respawn:Dynamic:Enabled</c>: scale respawn delays with the players around the spawn. False (the default) leaves every
    /// delay as the database says, which is retail. When true this replaces <c>Creatures:Respawn:DynamicRate</c> (TrinityCore's zone-rate
    /// scaling, off by default) rather than compounding with it.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// <c>World:Respawn:Dynamic:PlayerThreshold</c>: players within <see cref="Radius"/> that are free; only the players above this many
    /// shorten the delay (0..10000, default 5).
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 0 to 10000")]
    public int PlayerThreshold { get; set; } = 5;

    /// <summary>
    /// <c>World:Respawn:Dynamic:ScalePerPlayer</c>: each player above <see cref="PlayerThreshold"/> adds this to the divisor, so the delay is
    /// <c>base / (1 + ScalePerPlayer * max(0, players - PlayerThreshold))</c> (0..10, default 0.1: 15 players over the threshold
    /// divide the delay by 2.5). 0 never scales.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 10 ('.' is the decimal point); 0 never scales")]
    public float ScalePerPlayer { get; set; } = 0.1f;

    /// <summary>
    /// <c>World:Respawn:Dynamic:MinimumSeconds</c>: the delay is never scaled below this many seconds, and a delay already at or under it
    /// is left alone (1..86400, default 30). A corpse is removed when its respawn time is reached, so a value under the time players need
    /// to loot cuts loot short. The code has no separate corpse minimum, so this floor is it: the default 30 keeps a corpse lootable for at least 30 s.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number of seconds from 1 to 86400")]
    public uint MinimumSeconds { get; set; } = 30;

    /// <summary>
    /// <c>World:Respawn:Dynamic:MaxReduction</c>: the share of the base delay that scaling may take off, so the delay is never below
    /// <c>base * (1 - MaxReduction)</c> (0..1, default 0.9: at most a tenth of the base delay remains). 0 never scales.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 1 ('.' is the decimal point); 0 never scales")]
    public float MaxReduction { get; set; } = 0.9f;

    /// <summary>
    /// <c>World:Respawn:Dynamic:CountBots</c>: count playerbots as players (default true). False counts only human players, so bots
    /// neither shorten nor take part in the scaling.
    /// </summary>
    public bool CountBots { get; set; } = true;

    /// <summary>
    /// <c>World:Respawn:Dynamic:Radius</c>: players are counted within this many yards (2D) of the spawn's home position (5..500, default 100).
    /// A radius is local to the contested spot, where a zone count is not, and is answered by the map's grid cells at the cost of the few
    /// cells it touches.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number of yards from 5 to 500 ('.' is the decimal point)")]
    public float Radius { get; set; } = 100f;

    /// <summary>
    /// <c>World:Respawn:Dynamic:IncludeElitesAndRares</c>: also scale elite, rare and rare-elite creatures (default false: only normal
    /// creatures scale). World bosses never scale, whatever this says.
    /// </summary>
    public bool IncludeElitesAndRares { get; set; }

    /// <summary>
    /// <c>World:Respawn:Dynamic:IncludeGameObjects</c>: scale the respawn delay of gathering nodes (herbs, mineral veins) and quest objects
    /// too (default true). Other game objects never scale.
    /// </summary>
    public bool IncludeGameObjects { get; set; } = true;

    /// <summary>True when scaling is switched on and every value is usable; a value a bad start-up file left unusable turns scaling off instead of misbehaving.</summary>
    public bool Active => Enabled && IsUsable;

    /// <summary>True when every value is inside its documented range.</summary>
    public bool IsUsable
        => PlayerThreshold is >= 0 and <= MaxPlayerThreshold
            && float.IsFinite(ScalePerPlayer) && ScalePerPlayer is >= 0f and <= MaxScalePerPlayer
            && MinimumSeconds is >= 1 and <= MaxMinimumSeconds
            && float.IsFinite(MaxReduction) && MaxReduction is >= 0f and <= 1f
            && float.IsFinite(Radius) && Radius is >= MinRadius and <= MaxRadius;

    /// <summary>Whether creatures of this <c>creature_template.Rank</c> may scale: normal ones always, elites and rares with <see cref="IncludeElitesAndRares"/>, world bosses and unknown ranks never.</summary>
    public bool ScalesRank(uint rank) => rank switch
    {
        0 => true,
        1 or 2 or 4 => IncludeElitesAndRares,
        _ => false,
    };

    /// <summary>The factor the delay is multiplied by for <paramref name="players"/> counted players: 1 up to the threshold, then <c>1 / (1 + ScalePerPlayer * excess)</c>. Not bounded by the floors (see <see cref="ScaleDelay"/>).</summary>
    public double Multiplier(int players)
    {
        int excess = players - PlayerThreshold;
        return excess <= 0 ? 1.0 : 1.0 / (1.0 + (Quantize(ScalePerPlayer) * excess));
    }

    /// <summary>
    /// The respawn delay in seconds for <paramref name="players"/> counted players: <paramref name="baseSeconds"/> times
    /// <see cref="Multiplier"/>, rounded up to a whole second, never under <c>base * (1 - MaxReduction)</c> or <see cref="MinimumSeconds"/>,
    /// never over <paramref name="baseSeconds"/>. A delay at or under the minimum, and "never" (<see cref="uint.MaxValue"/>), are returned as they are.
    /// Pure; no allocation.
    /// </summary>
    public uint ScaleDelay(uint baseSeconds, int players)
    {
        if (!IsUsable || baseSeconds == uint.MaxValue || baseSeconds <= MinimumSeconds)
        {
            return baseSeconds;
        }

        double multiplier = Multiplier(players);
        if (multiplier >= 1.0)
        {
            return baseSeconds;
        }

        double floor = Math.Max(baseSeconds * (1.0 - Quantize(MaxReduction)), MinimumSeconds);
        double scaled = Math.Max(baseSeconds * multiplier, floor);
        double rounded = Math.Ceiling(scaled - 1e-6); // the float options are widened first, so a result of exactly N does not become N + 1
        return (uint)Math.Min(rounded, baseSeconds);
    }

    /// <summary>The problem with a <see cref="PlayerThreshold"/> value, or null.</summary>
    public static string? CheckPlayerThreshold(int value)
        => value is >= 0 and <= MaxPlayerThreshold ? null : string.Create(CultureInfo.InvariantCulture, $"must be 0..{MaxPlayerThreshold}");

    /// <summary>The problem with a <see cref="ScalePerPlayer"/> value, or null.</summary>
    public static string? CheckScalePerPlayer(float value)
        => float.IsFinite(value) && value is >= 0f and <= MaxScalePerPlayer ? null : string.Create(CultureInfo.InvariantCulture, $"must be 0..{MaxScalePerPlayer}");

    /// <summary>The problem with a <see cref="MinimumSeconds"/> value, or null.</summary>
    public static string? CheckMinimumSeconds(uint value)
        => value is >= 1 and <= MaxMinimumSeconds ? null : string.Create(CultureInfo.InvariantCulture, $"must be 1..{MaxMinimumSeconds}");

    /// <summary>The problem with a <see cref="MaxReduction"/> value, or null.</summary>
    public static string? CheckMaxReduction(float value)
        => float.IsFinite(value) && value is >= 0f and <= 1f ? null : "must be 0..1";

    /// <summary>The problem with a <see cref="Radius"/> value, or null.</summary>
    public static string? CheckRadius(float value)
        => float.IsFinite(value) && value is >= MinRadius and <= MaxRadius
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"must be {MinRadius}..{MaxRadius}");

    /// <summary>Every problem with the current values, as "Key must ..." lines (start-up warning).</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        void Add(string key, string? problem)
        {
            if (problem is not null)
            {
                problems.Add($"World:Respawn:Dynamic:{key} {problem}");
            }
        }

        Add(nameof(PlayerThreshold), CheckPlayerThreshold(PlayerThreshold));
        Add(nameof(ScalePerPlayer), CheckScalePerPlayer(ScalePerPlayer));
        Add(nameof(MinimumSeconds), CheckMinimumSeconds(MinimumSeconds));
        Add(nameof(MaxReduction), CheckMaxReduction(MaxReduction));
        Add(nameof(Radius), CheckRadius(Radius));
        return problems;
    }

    // 0.1f is 0.100000001490116...; rounding to 6 places makes 0.1f behave as the 0.1 the operator wrote.
    private static double Quantize(float value) => Math.Round((double)value, 6);
}
