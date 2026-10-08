namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Options of the game object behaviours (configuration section <c>GameObjects</c>). Every default is the
/// retail (vmangos) behaviour; a switch exists only where a deviation is deliberate.
/// </summary>
public sealed class GameObjectOptions
{
    public const string SectionName = "GameObjects";

    /// <summary>
    /// vmangos GameObjectData::GetRandomRespawnTime (GameObject.cpp:706-709): the respawn delay of a database
    /// spawn is a random value between spawntimesecsmin and spawntimesecsmax. False always uses the minimum.
    /// </summary>
    public bool RandomRespawn { get; set; } = true;

    /// <summary>
    /// vmangos Rate.Mining.Amount (CONFIG_FLOAT_RATE_MINING_AMOUNT, default 1; LootHandler.cpp:455-457): scales the minimum and maximum
    /// number of opens of a multi-use mineral vein (chest.minSuccessOpens / maxSuccessOpens). Default 1 (retail).
    /// </summary>
    public float MiningAmountRate { get; set; } = 1.0f;

    /// <summary>
    /// vmangos Rate.Mining.Next (CONFIG_FLOAT_RATE_MINING_NEXT, default 1; LootHandler.cpp:466-474): scales the chance that a mineral vein
    /// past its minimum opens stays for one more open. Default 1 (retail).
    /// </summary>
    public float MiningNextRate { get; set; } = 1.0f;

    /// <summary>
    /// A build-5875 TransportAnimation.dbc (vmangos loads it with the other DBCs): the animations of the elevators and trams (type 11), whose
    /// create block then carries the progress through their cycle. Empty (the default): their progress stays 0, as vmangos without the data.
    /// </summary>
    public string? TransportAnimationDbcPath { get; set; }
}
