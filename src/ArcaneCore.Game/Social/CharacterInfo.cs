using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Social;

/// <summary>The identity of any character on the realm, online or not.</summary>
/// <remarks>
/// <see cref="Level"/> and <see cref="ZoneId"/> are the last stored values of an offline character (vmangos
/// PlayerCacheData uiLevel / uiZoneId, read by Guild::AddMember, Guild.cpp:230-256).
/// </remarks>
public sealed record CharacterInfo(uint Id, int AccountId, string Name, Race Race, Class Class, byte Level = 1, uint ZoneId = 0)
{
    public ObjectGuid Guid => ObjectGuid.Player(Id);

    /// <summary>vmangos Player::TeamForRace.</summary>
    public Team Team => TeamRules.TeamOf(Race);
}
