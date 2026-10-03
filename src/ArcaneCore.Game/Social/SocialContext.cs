using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Social;

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
    public Team Team => Race is Race.Human or Race.Dwarf or Race.NightElf or Race.Gnome ? Team.Alliance : Team.Horde;
}

/// <summary>Finds characters that may be offline (the world daemon's character directory).</summary>
public interface ICharacterLookup
{
    CharacterInfo? Find(uint characterId);

    /// <summary>Exact (already normalized) name.</summary>
    CharacterInfo? FindByName(string name);
}

/// <summary>Accepts social writes made on the world thread for ordered, asynchronous persistence.</summary>
public interface ISocialPersistence
{
    void SetSocial(int characterId, int otherId, SocialFlags flags);

    void SaveGuild(GuildData guild);

    void DeleteGuild(int guildId);

    /// <summary>
    /// A deleted character's friend/ignore rows (owned and pointing at it) and guild membership,
    /// after every earlier write (docs/integration/character-delete.md). Default: nothing to purge.
    /// </summary>
    void PurgeCharacter(int characterId)
    {
    }

    /// <summary>Completes once every write queued before the call was attempted. Default: nothing is queued.</summary>
    Task FlushAsync() => Task.CompletedTask;
}

/// <summary>
/// Realm rules for cross-faction social interaction. Names and defaults follow the vmangos
/// mangosd.conf AllowTwoSide.* options (all off by default); bound from <see cref="SectionName"/>
/// and reloadable with <c>.reload config</c> (docs/areas/hot-reload.md).
/// </summary>
public sealed class SocialOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "World:Social";

    /// <summary>AllowTwoSide.AddFriend.</summary>
    public bool AllowTwoSideAddFriend { get; set; }

    /// <summary>AllowTwoSide.Interaction.Group.</summary>
    public bool AllowTwoSideGroup { get; set; }

    /// <summary>AllowTwoSide.Interaction.Guild.</summary>
    public bool AllowTwoSideGuild { get; set; }

    /// <summary>AllowTwoSide.Interaction.Channel.</summary>
    public bool AllowTwoSideChannel { get; set; }
}

/// <summary>
/// The shared state of the social systems (friends, groups, guilds, channels): the world, the
/// character lookup, persistence and realm options. World thread only.
/// </summary>
public sealed class SocialContext
{
    public SocialContext(WorldRuntime world, ICharacterLookup characters, ISocialPersistence persistence, SocialOptions? options = null)
    {
        World = world;
        Characters = characters;
        Persistence = persistence;
        Options = options ?? new SocialOptions();
        Friends = new FriendsService(this);
        Groups = new Groups.GroupManager(this);
        Guilds = new Guilds.GuildManager(this);
        Petitions = new Guilds.PetitionManager(this);
        Channels = new Channels.ChannelManager(this);
    }

    public WorldRuntime World { get; }

    public ICharacterLookup Characters { get; }

    public ISocialPersistence Persistence { get; }

    public SocialOptions Options { get; }

    public FriendsService Friends { get; }

    public Groups.GroupManager Groups { get; }

    public Guilds.GuildManager Guilds { get; }

    /// <summary>Guild charters (petitions); stored through <see cref="Guilds.IPetitionPersistence"/> when <see cref="Persistence"/> implements it.</summary>
    public Guilds.PetitionManager Petitions { get; }

    public Channels.ChannelManager Channels { get; }

    /// <summary>
    /// Whether <paramref name="viewer"/> may see <paramref name="other"/> as online in social
    /// lists (vmangos SocialMgr::GetFriendInfo / BroadcastToFriendListers): staff see everyone;
    /// players see their own faction (or both with AllowTwoSide.WhoList) and no staff above
    /// GmLevelInWhoList.
    /// </summary>
    public bool CanSeeOnline(Player viewer, Player other)
        => viewer.Security > AccountSecurity.Player
        || ((other.Team == viewer.Team || World.Options.AllowTwoSideWhoList) && other.Security <= World.Options.GmLevelInWhoList);

    /// <summary>Whether <paramref name="ignorer"/> has <paramref name="target"/> on its ignore list.</summary>
    public bool IsIgnoring(Player ignorer, ObjectGuid target) => Friends.HasIgnore(ignorer, target);
}
