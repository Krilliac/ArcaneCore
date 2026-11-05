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

        Assert.Equal(1, full.CommandsEvictedWhileHeld);
        Assert.Equal(0, full.ReleaseHeldCommands());
        Assert.False(fullGroup.IsRaid); // the cap was exceeded by one: the oldest went

        GroupAuthority exact = AuthorityWithGroup(out Group exactGroup);
        exact.HoldForReload();
        exact.Execute("worker-a", ConvertToRaid());
        for (int i = 0; i < GroupAuthority.MaxCommandsHeldForReload - 1; i++)
        {
            exact.Execute("worker-a", Loot(LootMethod.RoundRobin));
        }

        Assert.Equal(0, exact.CommandsEvictedWhileHeld);
        Assert.Equal(0, exact.ReleaseHeldCommands());
        Assert.True(exactGroup.IsRaid); // exactly the cap: all kept
    }

    [Fact]
    public void A_release_in_batches_stays_held_until_the_last_and_a_later_command_waits_behind_the_backlog()
    {
        GroupAuthority authority = AuthorityWithGroup(out Group group);
        authority.HoldForReload();
        authority.Execute("worker-a", Loot(LootMethod.FreeForAll));
        authority.Execute("worker-a", Loot(LootMethod.RoundRobin));

        Assert.Equal(0, authority.ReleaseHeldCommands(1));
        Assert.Equal(LootMethod.FreeForAll, group.LootMethod);
        Assert.Equal(1, authority.HeldCommandCount);

        authority.Execute("worker-a", Loot(LootMethod.MasterLoot)); // still held: it must not overtake RoundRobin
        Assert.Equal(LootMethod.FreeForAll, group.LootMethod);
        Assert.Equal(2, authority.HeldCommandCount);

        Assert.Equal(0, authority.ReleaseHeldCommands(1));
        Assert.Equal(LootMethod.RoundRobin, group.LootMethod);
        Assert.Equal(0, authority.ReleaseHeldCommands(1));
        Assert.Equal(LootMethod.MasterLoot, group.LootMethod);
        Assert.Equal(0, authority.HeldCommandCount);

        authority.Execute("worker-a", Loot(LootMethod.GroupLoot)); // the hold ended with the last one
        Assert.Equal(LootMethod.GroupLoot, group.LootMethod);
    }

    [Fact]
    public void Closing_a_held_table_loses_what_was_held_and_applies_nothing_from_then_on()
    {
        GroupAuthority authority = AuthorityWithGroup(out Group group);
        authority.HoldForReload();
        authority.Execute("worker-a", ConvertToRaid());
        authority.Execute("worker-a", Loot(LootMethod.FreeForAll));

        Assert.Equal(2, authority.CloseHeldTable());
        Assert.Equal(0, authority.CloseHeldTable()); // idempotent: nothing is counted twice

        authority.Execute("worker-a", Loot(LootMethod.RoundRobin));
        Assert.False(authority.TryExecute("worker-a", Loot(LootMethod.RoundRobin)));
        authority.HoldForReload();
        Assert.Equal(0, authority.ReleaseHeldCommands());

        Assert.False(group.IsRaid);
        Assert.Equal(LootMethod.GroupLoot, group.LootMethod);
        Assert.Equal(1, authority.CommandsRefusedAfterClose);
    }

    [Fact]
    public void Closing_a_table_that_is_not_held_changes_nothing()
    {
        GroupAuthority authority = AuthorityWithGroup(out Group group);

        Assert.Equal(0, authority.CloseHeldTable());
        authority.Execute("worker-a", Loot(LootMethod.RoundRobin));

        Assert.Equal(LootMethod.RoundRobin, group.LootMethod);
    }
}
