using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// <c>SpellSystem.UpdateAreaAuras</c> catches per source: a source that throws while it updates is recorded as a spell script fault
/// (phase "UpdateAuras", the source's spell) and the unit's next area source still ticks in the same pass.
/// </summary>
public sealed class AreaAuraFaultIsolationTests
{
    private const uint First = 947001, Second = 947002;

    private static SpellInfo Permanent(uint id) => Spell(id,
        Effect(SpellEffectName.ApplyAreaAuraParty, 5, aura: AuraType.ModResistance, misc: 1) with { Radius = 30 }) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    /// <summary>Party lookup that throws once when armed (the first source updated), and otherwise answers with a fixed party.</summary>
    private sealed class ThrowOnceGroups(List<ObjectGuid> party) : ISpellGroupResolver
    {
        public bool ThrowNext { get; set; }

        public int Calls { get; private set; }

        public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid)
        {
            Calls++;
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new InvalidOperationException("area source fault");
            }

            return party;
        }
    }

    [Fact]
    public void ThrowingAreaSource_DoesNotStopTheNextSourcesTick()
    {
        using var kit = new SpellTestKit(Permanent(First), Permanent(Second));
        (Player caster, _) = kit.AddPlayer(1);
        (Player member, _) = kit.AddPlayer(2, 2, 0);
        var groups = new ThrowOnceGroups([caster.Guid, member.Guid]);
        kit.System.Groups = groups;

        kit.System.CastSpell(caster, First, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(caster, Second, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(2, kit.System.GetAuras(caster).Count(a => a.IsAreaSource));

        groups.ThrowNext = true;
        int callsBefore = groups.Calls;
        kit.Advance(100, step: 100); // exactly one update pass: the first source throws, the second must still run

        Assert.True(groups.Calls >= callsBefore + 2, "the second source was reached after the first one threw");
        SpellAuraHolder child = Assert.Single(kit.System.GetAuras(member));
        Assert.Equal(Second, child.Spell.Id);
    }
}
