using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.Game.Social;

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
    /// Which channel names are built-in channels (ChatChannels.dbc). The transcribed 1.12.1 rows unless the world feature
    /// replaced it with the client's own file at startup (World:Chat:ChatChannelsDbcPath). Read when a channel is created.
    /// </summary>
    public ArcaneCore.Game.Channels.ChatChannelCatalog ChannelCatalog { get; set; } = ArcaneCore.Game.Channels.ChatChannelCatalog.Builtin;

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
