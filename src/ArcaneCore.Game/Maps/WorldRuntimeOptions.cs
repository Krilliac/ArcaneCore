namespace ArcaneCore.Game.Maps;

/// <summary>Tuning for the world simulation (bound from the "World" configuration section).</summary>
public sealed class WorldRuntimeOptions
{
    /// <summary>World tick length in milliseconds (vmangos WORLD_SLEEP_CONST = 50).</summary>
    public int TickIntervalMs { get; set; } = 50;

    /// <summary>
    /// Update packets larger than this many bytes are zlib-compressed into
    /// SMSG_COMPRESSED_UPDATE_OBJECT (vmangos Compression.Update.Size default 128). 0 disables.
    /// </summary>
    public int UpdateCompressionThreshold { get; set; } = 128;

    /// <summary>Periodic save of online characters, in milliseconds (vmangos PlayerSave.Interval default 900000). 0 disables.</summary>
    public int AutosaveIntervalMs { get; set; } = 15 * 60 * 1000;
}
