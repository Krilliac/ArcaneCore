namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:WorldPvp</c>: the world PvP objectives bots take part in (<c>PlayerbotWorldPvpObjectives</c>,
/// docs/areas/playbots-social.md, World PvP: Silithyst). Every key is live: <c>.reload config</c> changes this object in place and a bot
/// reads it at its next decision. Not vmangos keys: bots taking objectives is an ArcaneCore feature.
/// </summary>
public sealed class PlayerbotWorldPvpOptions
{
    /// <summary>The Silithus Silithyst objective (<c>World:Playerbots:WorldPvp:Silithyst</c>).</summary>
    public PlayerbotSilithystOptions Silithyst { get; set; } = new();

    /// <summary>Throws when a key is out of range (the reload refuses such a value with the same ranges).</summary>
    public void Validate()
    {
        if (Silithyst is null) throw new InvalidOperationException($"{PlayerbotOptions.SectionName}:WorldPvp:Silithyst is missing.");
        Silithyst.Validate();
    }
}

/// <summary>
/// <c>World:Playerbots:WorldPvp:Silithyst</c>: bots in Silithus (zone 1377) at level 55 or more go for the silithyst geysers and mounds, carry
/// the dust to their own faction's camp trigger and turn it in, as a player does. A carrier never mounts or stealths (the server drops the dust
/// when it does). Every key is live.
/// </summary>
public sealed class PlayerbotSilithystOptions
{
    /// <summary>Let bots pick up and deliver silithyst (on by default). Off: bots ignore the geysers; a bot already carrying finishes its trip.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The chance (0..1; default 0.5) that an eligible bot takes part in a given two-minute window: each bot rolls once per window from its
    /// guid, so a crowd of bots in Silithus is not all on the geysers at once.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 1, '.' is the decimal point")]
    public float ParticipationChance { get; set; } = 0.5f;

    /// <summary>The chance (0..1; default 0.9) of the same roll while <c>World:Playerbots:Social:WarMode:Enabled</c> is on.</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 1, '.' is the decimal point")]
    public float WarModeParticipationChance { get; set; } = 0.9f;

    /// <summary>The most bots that go for one geyser or mound at the same time (1..8; default 1).</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 1 to 8")]
    public int MaxBotsPerGeyser { get; set; } = 1;

    /// <summary>Throws when a key is out of range (the reload refuses such a value with the same ranges).</summary>
    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":WorldPvp:Silithyst";
        if (CheckChance(ParticipationChance) is { } chance) throw new InvalidOperationException($"{section}: ParticipationChance {chance}.");
        if (CheckChance(WarModeParticipationChance) is { } war) throw new InvalidOperationException($"{section}: WarModeParticipationChance {war}.");
        if (CheckMaxBots(MaxBotsPerGeyser) is { } bots) throw new InvalidOperationException($"{section}: MaxBotsPerGeyser {bots}.");
    }

    internal static string? CheckChance(float value) => float.IsFinite(value) && value is >= 0f and <= 1f ? null : "must be 0..1";

    internal static string? CheckMaxBots(int value) => value is >= 1 and <= 8 ? null : "must be 1..8";
}
