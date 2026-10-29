using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.ClassSpells.Totems;

/// <summary>
/// <see cref="ISpellGroupResolver.IsMember"/> through <see cref="OwnerAwareGroupResolver"/> (the live resolver when totems are on):
/// a totem answers for its owner's group, and the answer equals <c>GetGroupMembers(...).Contains</c>. Also the default interface
/// method, which a resolver that implements only <c>GetGroupMembers</c> inherits.
/// </summary>
public sealed class TotemGroupMembershipTests
{
    [Fact]
    public void Totem_IsMember_MatchesGetGroupMembersContains_ForOwnerPartyAndOutsiders()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 100, 200);
        (Player mate, _) = kit.AddPlayer(2, 5000, 5000);
        (Player stranger, _) = kit.AddPlayer(3, 9000, 9000);
        kit.Groups.Parties.Add([shaman.Guid, mate.Guid]);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(shaman, TotemTestKit.EarthTotemSummon));
        Creature totem = Assert.IsType<Creature>(kit.Totems.GetTotem(shaman, TotemSlot.Earth));
        Assert.True(TotemQuery.IsTotem(totem));

        ISpellGroupResolver resolver = kit.Spells.Groups;
        Assert.IsType<OwnerAwareGroupResolver>(resolver);
        ObjectGuid[] others = [shaman.Guid, mate.Guid, stranger.Guid, totem.Guid, ObjectGuid.Player(99)];
        foreach (Unit unit in new Unit[] { totem, shaman, stranger })
        {
            foreach (ObjectGuid other in others)
            {
                foreach (bool raid in new[] { false, true })
                {
                    Assert.Equal(resolver.GetGroupMembers(unit, raid).Contains(other), resolver.IsMember(unit, other, raid));
                }
            }
        }

        // The totem answers for its owner's party; the totem itself is not in it.
        Assert.True(resolver.IsMember(totem, shaman.Guid, raid: false));
        Assert.True(resolver.IsMember(totem, mate.Guid, raid: false));
        Assert.False(resolver.IsMember(totem, stranger.Guid, raid: false));
        Assert.False(resolver.IsMember(totem, totem.Guid, raid: false));
    }

    [Fact]
    public void Totem_IsMember_AllocatesNothing_OverAnAllocationFreeInner()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 100, 200);
        (Player mate, _) = kit.AddPlayer(2, 5000, 5000);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(shaman, TotemTestKit.EarthTotemSummon));
        Creature totem = Assert.IsType<Creature>(kit.Totems.GetTotem(shaman, TotemSlot.Earth));
        ISpellGroupResolver resolver = new OwnerAwareGroupResolver(new ArrayParty(shaman.Guid, mate.Guid));
        ObjectGuid inParty = mate.Guid;
        ObjectGuid outside = ObjectGuid.Player(99);
        bool sink = false;

        void TotemWindow() { for (int i = 0; i < 1000; i++) { sink ^= resolver.IsMember(totem, inParty, false) ^ resolver.IsMember(totem, outside, true); } }
        void PlayerWindow() { for (int i = 0; i < 1000; i++) { sink ^= resolver.IsMember(shaman, inParty, false) ^ resolver.IsMember(shaman, outside, true); } }

        Assert.True(resolver.IsMember(totem, inParty, false));
        AllocationProbe.AssertEveryCleanWindowAtMost(TotemWindow, 0, "totem IsMember");
        AllocationProbe.AssertEveryCleanWindowAtMost(PlayerWindow, 0, "owner IsMember through OwnerAware");
        GC.KeepAlive(sink);
    }

    [Fact]
    public void DefaultIsMember_EnumeratesGetGroupMembers_ForAResolverThatOnlyImplementsIt()
    {
        using var kit = new TotemTestKit();
        (Player a, _) = kit.AddPlayer(1);
        (Player b, _) = kit.AddPlayer(2, 5000, 5000);
        (Player c, _) = kit.AddPlayer(3, 9000, 9000);
        ISpellGroupResolver resolver = new MembersOnly([a.Guid, b.Guid]);

        Assert.True(resolver.IsMember(a, a.Guid, raid: false));
        Assert.True(resolver.IsMember(a, b.Guid, raid: true));
        Assert.False(resolver.IsMember(a, c.Guid, raid: false));
        Assert.False(resolver.IsMember(a, default, raid: false));
        Assert.Equal((3, 1), (((MembersOnly)resolver).Calls[false], ((MembersOnly)resolver).Calls[true]));
    }

    /// <summary>Implements only <c>GetGroupMembers</c>, so <c>IsMember</c> is the interface default.</summary>
    private sealed class MembersOnly(ObjectGuid[] members) : ISpellGroupResolver
    {
        public Dictionary<bool, int> Calls { get; } = new() { [false] = 0, [true] = 0 };

        public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid)
        {
            Calls[raid]++;
            return members;
        }
    }

    /// <summary>A party that answers <c>IsMember</c> without allocating, so a probe measures only the wrapper.</summary>
    private sealed class ArrayParty(ObjectGuid first, ObjectGuid second) : ISpellGroupResolver
    {
        private readonly ObjectGuid[] _members = [first, second];

        public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid) => _members;

        public bool IsMember(Unit unit, ObjectGuid other, bool raid) => other == _members[0] || other == _members[1];
    }
}
