using ArcaneCore.Game;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>The hold a <see cref="GroupAuthority"/> keeps while its table is not the one loaded for the current holding.</summary>
public sealed class GroupAuthorityTests
{
    private sealed class Lookup : ICharacterLookup
    {
        private readonly Dictionary<uint, CharacterInfo> _byId = new()
        {
            [1] = new CharacterInfo(1, 101, "Alice", Race.Human, Class.Warrior),
            [2] = new CharacterInfo(2, 102, "Bob", Race.Human, Class.Warrior),
        };

        public CharacterInfo? Find(uint characterId) => _byId.GetValueOrDefault(characterId);

        public CharacterInfo? FindByName(string name) => _byId.Values.FirstOrDefault(c => c.Name == name);
    }

    private static GroupCommand Loot(LootMethod method)
        => new(GroupCommandKind.SetLootMethod, ObjectGuid.Player(1).Value, "Alice", 0, false, Value: (uint)method, Value2: Group.DefaultLootThreshold);

    private static GroupAuthority AuthorityWithGroup(out Group group)
    {
        var authority = new GroupAuthority(new InMemoryRealmPresenceDirectory());
        authority.Restore(
            [new GroupRecord(1, 1, (byte)LootMethod.GroupLoot, 1, Group.DefaultLootThreshold, false, new ulong[8], [new GroupMemberRecord(1, 0, false), new GroupMemberRecord(2, 0, false)])],
            new Lookup());
        group = authority.GetGroup(ObjectGuid.Player(1))!;
        return authority;
    }

    private static GroupCommand ConvertToRaid() => new(GroupCommandKind.ConvertToRaid, ObjectGuid.Player(1).Value, "Alice", 0, false);

    private static GroupCommand Relay() => new(GroupCommandKind.Relay, ObjectGuid.Player(1).Value, "Alice", 0, false);

    [Fact]
    public void Held_commands_run_in_order_on_release_and_none_runs_before()
    {
        GroupAuthority authority = AuthorityWithGroup(out Group group);

        authority.HoldForReload();
        authority.HoldForReload(); // idempotent: the commands already held are kept
        authority.Execute("worker-a", Loot(LootMethod.FreeForAll));
        authority.HoldForReload();
        authority.Execute("worker-a", Loot(LootMethod.RoundRobin));
        Assert.Equal(LootMethod.GroupLoot, group.LootMethod);

        Assert.Equal(0, authority.ReleaseHeldCommands());
        Assert.Equal(LootMethod.RoundRobin, group.LootMethod);
    }

    [Fact]
    public void Relay_commands_are_dropped_while_held_and_cannot_push_a_real_command_out_of_the_cap()
    {
        GroupAuthority authority = AuthorityWithGroup(out Group group);
        authority.HoldForReload();
        authority.Execute("worker-a", ConvertToRaid()); // the oldest held command, the one a flood of stats would evict
        const int Floods = GroupAuthority.MaxCommandsHeldForReload + 100;
        for (int i = 0; i < Floods; i++)
        {
            authority.Execute("worker-a", Relay());
        }

        Assert.Equal(Floods, authority.RelaysDroppedWhileHeld);
        Assert.Equal(0, authority.ReleaseHeldCommands());
        Assert.True(group.IsRaid);
    }

    [Fact]
    public void The_hold_keeps_at_most_the_cap_and_drops_the_oldest()
    {
        GroupAuthority full = AuthorityWithGroup(out Group fullGroup);
        full.HoldForReload();
        full.Execute("worker-a", ConvertToRaid());
        for (int i = 0; i < GroupAuthority.MaxCommandsHeldForReload; i++)
        {
            full.Execute("worker-a", Loot(LootMethod.RoundRobin));
        }

        Assert.Equal(0, full.ReleaseHeldCommands());
        Assert.False(fullGroup.IsRaid); // the cap was exceeded by one: the oldest went

        GroupAuthority exact = AuthorityWithGroup(out Group exactGroup);
        exact.HoldForReload();
        exact.Execute("worker-a", ConvertToRaid());
        for (int i = 0; i < GroupAuthority.MaxCommandsHeldForReload - 1; i++)
        {
            exact.Execute("worker-a", Loot(LootMethod.RoundRobin));
        }

        Assert.Equal(0, exact.ReleaseHeldCommands());
        Assert.True(exactGroup.IsRaid); // exactly the cap: all kept
    }

    [Fact]
    public void Dropping_the_held_commands_runs_none_of_them_and_applies_later_ones()
    {
        GroupAuthority authority = AuthorityWithGroup(out Group group);
        authority.HoldForReload();
        authority.Execute("worker-a", ConvertToRaid());
        authority.DropHeldCommands();
        authority.Execute("worker-a", Loot(LootMethod.RoundRobin));

        Assert.False(group.IsRaid);
        Assert.Equal(LootMethod.RoundRobin, group.LootMethod);
    }
}
