using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// vmangos Spell::EffectDispel priorityDispel (SpellEffects.cpp:2492-2499, :2519-2523): on a non-friendly target a charm aura whose charmed unit's
/// original faction is friendly to the caster is the first pick, without an RNG draw.
/// </summary>
public sealed class DispelCharmPriorityTests
{
    private const uint DispelMagic = 931001;
    private const uint Charm = 931002;
    private const uint MagicBuff = 931003;

    private static SpellTestKit NewKit(out Player caster, out Player target, bool charmInfo, bool friendlyOrigin)
    {
        var kit = new SpellTestKit(
            SpellTestKit.Spell(DispelMagic, SpellTestKit.Effect(SpellEffectName.Dispel, 1, SpellImplicitTarget.Unit, misc: 1)) with
            {
                RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
            SpellTestKit.Spell(Charm, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.Unit, AuraType.ModCharm)) with
            {
                Dispel = 1, Duration = new SpellDuration(30000, 0, 30000), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
            SpellTestKit.Spell(MagicBuff, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            {
                Dispel = 1, Duration = new SpellDuration(30000, 0, 30000), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0,
            });
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        (caster, _) = kit.AddPlayer(1);
        (target, _) = kit.AddPlayer(2, 2, race: Race.Orc);
        relations.Hostile.Add(target.Guid);

        // The charmed unit is hostile to the caster by its current template; its original (race) template is friendly to the caster's.
        const uint casterTemplate = 9_001; // not the race template of either player, so the two records differ
        caster.FactionTemplate = casterTemplate;
        uint originalTemplate = target.RaceFactionTemplate;
        var catalog = new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(casterTemplate, casterTemplate, 0, OwnMask: 1, FriendlyMask: 0, HostileMask: 0),
            new FactionTemplateRecord(originalTemplate, originalTemplate, 0, OwnMask: 2, FriendlyMask: friendlyOrigin ? 1u : 0u, HostileMask: 0),
        ]);
        caster.Map!.Combat.Hooks = new FactionCombatHooks(catalog);

        kit.System.CastSpell(target, MagicBuff, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(caster, Charm, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        if (charmInfo)
        {
            CharmService.InitCharmInfo(target);
        }

        return kit;
    }

    [Fact]
    public void CharmOnAnEnemy_WhoseOriginalFactionIsFriendly_IsTheFirstPick()
    {
        using SpellTestKit kit = NewKit(out Player caster, out Player target, charmInfo: true, friendlyOrigin: true);

        List<(SpellAuraHolder Holder, int Stacks)> candidates = kit.System.DispelCandidates(caster, target, null, 1, out int priority);

        Assert.Equal(2, candidates.Count);
        Assert.True(priority >= 0, "no priority charm candidate");
        Assert.Equal(Charm, candidates[priority].Holder.Spell.Id);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WithoutCharmInfoOrAFriendlyOrigin_NoCandidateHasPriority(bool charmInfo, bool friendlyOrigin)
    {
        using SpellTestKit kit = NewKit(out Player caster, out Player target, charmInfo, friendlyOrigin);

        kit.System.DispelCandidates(caster, target, null, 1, out int priority);

        Assert.Equal(-1, priority);
    }

    [Fact]
    public void SinglePickDispelMagic_OnTheCharmedEnemy_RemovesTheCharmNotTheBuff()
    {
        using SpellTestKit kit = NewKit(out Player caster, out Player target, charmInfo: true, friendlyOrigin: true);
        kit.Spellbook.Teach(caster, DispelMagic);

        kit.System.HandleCastRequest(caster, DispelMagic, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(kit.System.SpellBatchWindowMs);

        Assert.False(kit.System.HasAura(target, Charm));
        Assert.True(kit.System.HasAura(target, MagicBuff));
    }
}
