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
}
