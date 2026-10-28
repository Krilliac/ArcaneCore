using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.ClassSpells.Totems;

/// <summary>
/// <see cref="ISpellGroupResolver.AppendGroupMembers"/> through <see cref="OwnerAwareGroupResolver"/> (a totem appends its owner's group),
/// <see cref="NoGroupResolver"/> and the default interface method of a resolver that implements only <c>GetGroupMembers</c>: always the
/// same GUIDs in the same order as <c>GetGroupMembers</c>, appended after what the list held.
/// ENABLE by renaming this file to .cs once the fix adds the method.
/// </summary>
public sealed class AppendGroupMembersTests
{
    [Fact]
    public void OwnerAware_AppendGroupMembers_MatchesGetGroupMembers_ForTotemOwnerPartyMateAndStranger()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 100, 200);
        (Player mate, _) = kit.AddPlayer(2, 5000, 5000);
        (Player stranger, _) = kit.AddPlayer(3, 9000, 9000);
        kit.Groups.Parties.Add([shaman.Guid, mate.Guid]);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(shaman, TotemTestKit.EarthTotemSummon));
        Creature totem = Assert.IsType<Creature>(kit.Totems.GetTotem(shaman, TotemSlot.Earth));
        ISpellGroupResolver resolver = kit.Spells.Groups;
        Assert.IsType<OwnerAwareGroupResolver>(resolver);

        foreach (Unit unit in new Unit[] { totem, shaman, mate, stranger })
        {
            foreach (bool raid in new[] { false, true })
            {
                var appended = new List<ObjectGuid>();
                resolver.AppendGroupMembers(unit, raid, appended);
                Assert.Equal(resolver.GetGroupMembers(unit, raid), appended);
            }
        }

        // The totem answers for its owner's party (the totem itself is not in it).
        var forTotem = new List<ObjectGuid>();
        resolver.AppendGroupMembers(totem, false, forTotem);
        Assert.Contains(shaman.Guid, forTotem);
        Assert.Contains(mate.Guid, forTotem);
        Assert.DoesNotContain(totem.Guid, forTotem);
        Assert.DoesNotContain(stranger.Guid, forTotem);
    }

    [Fact]
    public void OwnerAware_AppendGroupMembers_KeepsExistingEntries()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 100, 200);
        (Player mate, _) = kit.AddPlayer(2, 5000, 5000);
        kit.Groups.Parties.Add([shaman.Guid, mate.Guid]);
        ISpellGroupResolver resolver = kit.Spells.Groups;
        ObjectGuid existing = ObjectGuid.Player(99);
        var into = new List<ObjectGuid> { existing };

        resolver.AppendGroupMembers(shaman, false, into);

        Assert.Equal([existing, .. resolver.GetGroupMembers(shaman, false)], into);
    }

    [Fact]
    public void NoGroupResolver_AppendGroupMembers_AppendsTheUnitAlone_AndGuardsNull()
    {
        using var kit = new TotemTestKit();
        (Player player, _) = kit.AddPlayer(1);
        var into = new List<ObjectGuid> { ObjectGuid.Player(99) };

        ((ISpellGroupResolver)NoGroupResolver.Instance).AppendGroupMembers(player, raid: true, into);

        Assert.Equal([ObjectGuid.Player(99), player.Guid], into);
        Assert.Throws<ArgumentNullException>(() => ((ISpellGroupResolver)NoGroupResolver.Instance).AppendGroupMembers(null!, false, []));
    }

    [Fact]
    public void DefaultAppendGroupMembers_FallsBackToGetGroupMembers_ForAResolverThatOnlyImplementsIt()
    {
        using var kit = new TotemTestKit();
        (Player a, _) = kit.AddPlayer(1);
        (Player b, _) = kit.AddPlayer(2, 5000, 5000);
        ISpellGroupResolver resolver = new MembersOnly([a.Guid, b.Guid]);
        var into = new List<ObjectGuid> { ObjectGuid.Player(99) };

        resolver.AppendGroupMembers(a, raid: false, into);

        Assert.Equal([ObjectGuid.Player(99), a.Guid, b.Guid], into);
        Assert.Equal(1, ((MembersOnly)resolver).Calls);
    }

    [Fact]
    public void OwnerAware_AppendGroupMembers_AllocatesNothing_OverAnAllocationFreeInner()
    {
        using var kit = new TotemTestKit();
        (Player shaman, _) = kit.AddPlayer(1, 100, 200);
        (Player mate, _) = kit.AddPlayer(2, 5000, 5000);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(shaman, TotemTestKit.EarthTotemSummon));
        Creature totem = Assert.IsType<Creature>(kit.Totems.GetTotem(shaman, TotemSlot.Earth));
        ISpellGroupResolver resolver = new OwnerAwareGroupResolver(new ArrayParty(shaman.Guid, mate.Guid));
        var into = new List<ObjectGuid>(16);

        void TotemWindow() { for (int i = 0; i < 1000; i++) { into.Clear(); resolver.AppendGroupMembers(totem, false, into); } }
        void PlayerWindow() { for (int i = 0; i < 1000; i++) { into.Clear(); resolver.AppendGroupMembers(shaman, true, into); } }

        AllocationProbe.AssertEveryCleanWindowAtMost(TotemWindow, 0, "totem AppendGroupMembers");
        AllocationProbe.AssertEveryCleanWindowAtMost(PlayerWindow, 0, "owner AppendGroupMembers through OwnerAware");
    }

    private sealed class MembersOnly(ObjectGuid[] members) : ISpellGroupResolver
    {
        public int Calls { get; private set; }

        public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid)
        {
            Calls++;
            return members;
        }
    }

    /// <summary>A party whose append is allocation-free, so a probe measures only the wrapper.</summary>
    private sealed class ArrayParty(ObjectGuid first, ObjectGuid second) : ISpellGroupResolver
    {
        private readonly ObjectGuid[] _members = [first, second];

        public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid) => _members;

        public void AppendGroupMembers(Unit unit, bool raid, List<ObjectGuid> into)
        {
            into.Add(_members[0]);
            into.Add(_members[1]);
        }
    }
}
