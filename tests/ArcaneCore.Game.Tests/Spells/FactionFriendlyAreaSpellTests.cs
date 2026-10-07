using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Friendly area spells consume the faction-aware combat relation for known NPC templates.</summary>
public sealed class FactionFriendlyAreaSpellTests
{
    private const uint FriendlyAreaHeal = 900401;

    private static readonly FactionTemplateCatalog Catalog = new(
    [
        new FactionTemplateRecord(1, 1, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
        new FactionTemplateRecord(11, 11, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
        new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 8, HostileMask: 2),
    ]);

    [Fact]
    public void FriendlyAreaHeal_HealsKnownFriendlyNpc_AndExcludesHostileNpc()
    {
        using var kit = new SpellTestKit(
            Spell(FriendlyAreaHeal, Effect(SpellEffectName.Heal, 20, SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc)
                with { Radius = 8 }) with
            {
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (Player caster, _) = kit.AddPlayer(1);
        Map map = caster.Map!;
        map.Combat.Hooks = new FactionCombatHooks(Catalog);

        var friendly = new CombatTestUnit { FactionTemplate = 11, MaxHealth = 100, Health = 10 };
        friendly.Relocate(3, 0, caster.Z, 0, 0);
        map.AddObject(friendly);
        map.Combat.Track(friendly);

        var hostile = new CombatTestUnit { FactionTemplate = 14, MaxHealth = 100, Health = 10 };
        hostile.Relocate(4, 0, caster.Z, 0, 0);
        map.AddObject(hostile);
        map.Combat.Track(hostile);
        kit.World.RunTick(0);
        uint friendlyBefore = friendly.Health;
        uint hostileBefore = hostile.Health;

        Assert.Equal(SpellCastResult.CastOk,
            kit.System.CastSpell(caster, FriendlyAreaHeal, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(friendlyBefore + 20, friendly.Health);
        Assert.Equal(hostileBefore, hostile.Health);
    }
}
