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

    [Fact]
    public void Discarded_held_commands_never_run_and_the_table_stays_held_for_the_next_reload()
    {
        var authority = new GroupAuthority(new InMemoryRealmPresenceDirectory());
        authority.Restore(
            [new GroupRecord(1, 1, (byte)LootMethod.GroupLoot, 1, Group.DefaultLootThreshold, false, new ulong[8], [new GroupMemberRecord(1, 0, false), new GroupMemberRecord(2, 0, false)])],
            new Lookup());
        Group group = authority.GetGroup(ObjectGuid.Player(1))!;
        Assert.Equal(LootMethod.GroupLoot, group.LootMethod);

        authority.HoldForReload();
        authority.Execute("worker-a", new GroupCommand(GroupCommandKind.ConvertToRaid, ObjectGuid.Player(1).Value, "Alice", 0, false));
        authority.Execute("worker-a", Loot(LootMethod.FreeForAll));
        authority.DiscardHeldCommands();
        authority.Execute("worker-a", Loot(LootMethod.RoundRobin));

        // Still held: nothing has run.
        Assert.False(group.IsRaid);
        Assert.Equal(LootMethod.GroupLoot, group.LootMethod);

        Assert.Equal(0, authority.ReleaseHeldCommands());
        Assert.False(group.IsRaid);
        Assert.Equal(LootMethod.RoundRobin, group.LootMethod);
    }
}
