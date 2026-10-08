using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Lfg;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// The meeting stone queue of the world daemon (a discovered <see cref="IWorldFeature"/>): one <see cref="LfgQueue"/> over the social feature's
/// groups, advanced by the world tick, told about logouts and about members joining, leaving, being kicked and parties disbanding (vmangos
/// LFGMgr / LFGQueue and the Group hooks). Without the social feature there is no queue: a join is ignored and the status poll answers NONE.
/// </summary>
public sealed class MeetingStoneFeature(IServiceProvider services, ILogger<MeetingStoneFeature> logger) : IWorldFeature
{
    private WorldRuntime? _world;

    /// <summary>The queue, once the social feature is attached (world thread).</summary>
    public LfgQueue? Queue { get; private set; }

    /// <summary>The groups the queue works with, or null without the social feature.</summary>
    public GroupManager? Groups
    {
        get
        {
            try
            {
                return services.GetService<SocialFeature>()?.Context.Groups;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        // The social feature attaches after this one (type-name order); its context exists by the time posted work runs.
        world.Post(Install);
    }

    private void Install()
    {
        WorldRuntime world = _world!;
        if (Groups is not { } groups)
        {
            logger.LogInformation("Meeting stones: no social feature, the queue is off");
            return;
        }

        var queue = new LfgQueue(new SocialLfgGroups(groups, world, services.GetService<CharacterDirectory>()));
        Queue = queue;
        world.Updated += queue.Update;
        world.PlayerLoggingOut += queue.OnLoggingOut;
        groups.MemberAdded += (group, _) => queue.OnMemberJoined(group);
        groups.MemberLeft += queue.OnMemberRemoved;
        groups.Disbanding += (group, _) => queue.OnDisbanding(group);
    }

    /// <summary>The social feature's <see cref="GroupManager"/> behind <see cref="ILfgGroups"/>.</summary>
    private sealed class SocialLfgGroups(GroupManager groups, WorldRuntime world, CharacterDirectory? directory) : ILfgGroups
    {
        public Group? GroupOf(ObjectGuid player) => groups.GetGroup(player);

        public Group? CreateGroup(Player leader, Player member) => groups.CreateLfgGroup(leader, member);

        public bool AddMember(Group group, Player member) => groups.AddLfgMember(group, member);

        public void Broadcast(Group group, WorldOpcode opcode, byte[] payload) => groups.Broadcast(group, opcode, payload);

        public Player? FindOnlinePlayer(ObjectGuid guid) => world.FindOnlinePlayer(guid);

        public Class ClassOf(ObjectGuid guid)
            => world.FindOnlinePlayer(guid) is { } player ? player.Class
                : directory?.Find((int)guid.Counter) is { } identity ? (Class)identity.Class : 0;
    }
}
