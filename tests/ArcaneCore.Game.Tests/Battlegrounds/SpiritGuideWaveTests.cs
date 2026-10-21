using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// The battleground spirit guide's resurrection wave end to end in the spell system (bot QA #887, "waited 90 s within 0.0 yd of spirit
/// guide 13117 without a resurrection wave"). The data is the 1.12.1 client's (client-dbc-5875 Spell.dbc): Spirit Heal Channel (22011)
/// is a self channel (AttributesEx 0x40) of 30 s (DurationIndex 9) whose only effect is SPELL_AURA_PERIODIC_TRIGGER_SPELL on the caster
/// with a 30000 ms amplitude triggering Spirit Heal (22012), so the one tick lands as the channel ends; Spirit Heal is SPELL_EFFECT_SPIRIT_HEAL
/// on TARGET_ENUM_UNITS_FRIEND_AOE_AT_DEST_LOC around the caster (radius index 9, 20 yd), at most 10 targets (MaxTargets), and takes the dead
/// only (AttributesEx2 0x1 ALLOW_DEAD_TARGET, Targets 0x400 TARGET_FLAG_UNIT_DEAD: vmangos IsDeathOnlySpell).
/// vmangos npc_spirit_guideAI::UpdateAI (battleground.cpp:58-71) casts the channel again whenever the guide is not channelling, which
/// the match runtime does on every update (MatchRuntime.UpdateSpiritGuides). A ghost carrying Waiting to Resurrect (2584) in range comes
/// back within one channel.
/// </summary>
public sealed class SpiritGuideWaveTests : IDisposable
{
    private const uint SpiritHealChannel = BattlegroundConstants.SpellSpiritHealChannel;
    private const uint SpiritHeal = 22012;

    private readonly SpellTestKit _spells = new(
        Spell(SpiritHealChannel, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.PeriodicTriggerSpell, amplitude: 30_000, trigger: SpiritHeal)) with
        {
            AttributesEx = SpellAttributesEx.IsSelfChanneled,
            Duration = new SpellDuration(30_000, 0, 30_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            SpellVisual = 1,
        },
        Spell(SpiritHeal, Effect(SpellEffectName.SpiritHeal, 0, SpellImplicitTarget.LocationCasterDest,
            targetB: SpellImplicitTarget.EnumUnitsFriendAoeAtDestLoc) with { Radius = 20f }) with
        {
            AttributesEx2 = SpellAttributesEx2.AllowDeadTarget,
            Targets = (uint)SpellCastTargetFlags.UnitDead,
            MaxAffectedTargets = 10,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(SpiritHealEffect.WaitingToResurrect, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Attributes = SpellAttributes.AllowCastWhileDead,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    public void Dispose() => _spells.Dispose();

    /// <summary>One world update as a match runs it: the guide channels again when it is not channelling, then the spells update.</summary>
    private void Update(Player guide, uint diffMs)
    {
        if (!_spells.System.IsChanneling(guide))
            _spells.System.CastSpell(guide, SpiritHealChannel, SpellCastTargets.ForSelf(), triggered: false);
        _spells.Now += diffMs;
        _spells.System.Update(diffMs);
    }

    [Theory]
    [InlineData(50u)]
    [InlineData(100u)]
    [InlineData(37u)]
    public void AWaitingGhostBesideTheGuide_IsResurrectedByTheNextWave(uint stepMs)
    {
        (Player guide, _) = _spells.AddPlayer(9);
        (Player ghost, _) = _spells.AddPlayer(1, 3, 0);
        Map map = _spells.World.GetMap(0);
        map.Combat.KillPlayer(ghost);
        _spells.World.RunTick(100);
        Assert.True(map.Combat.RepopPlayer(ghost));

        // The guide has channelled for a while before this death: the ghost queues at an arbitrary point of the cycle.
        for (int i = 0; i < 12_345 / stepMs; i++)
            Update(guide, stepMs);
        Assert.Equal(SpellCastResult.CastOk, _spells.System.CastSpell(ghost, SpiritHealEffect.WaitingToResurrect, SpellCastTargets.ForSelf(), triggered: true));

        // Within one full channel (plus an update) the wave comes.
        uint waited = 0;
        while (!ghost.IsAlive && waited <= 30_000 + (2 * stepMs))
        {
            Update(guide, stepMs);
            waited += stepMs;
        }

        Assert.True(ghost.IsAlive, $"no resurrection wave in {waited} ms of the guide channelling");
    }

    [Fact]
    public void TheGuide_KeepsWaving_EveryThirtySeconds()
    {
        (Player guide, _) = _spells.AddPlayer(9);
        (Player ghost, _) = _spells.AddPlayer(1, 3, 0);
        Map map = _spells.World.GetMap(0);
        int waves = 0;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            map.Combat.KillPlayer(ghost);
            _spells.World.RunTick(100);
            Assert.True(map.Combat.RepopPlayer(ghost));
            Assert.Equal(SpellCastResult.CastOk, _spells.System.CastSpell(ghost, SpiritHealEffect.WaitingToResurrect, SpellCastTargets.ForSelf(), triggered: true));
            for (uint waited = 0; !ghost.IsAlive && waited <= 30_100; waited += 50)
                Update(guide, 50);
            waves += ghost.IsAlive ? 1 : 0;
        }

        Assert.Equal(3, waves);
    }

    /// <summary>
    /// The wave's ten targets are the dead: a crowd of living friends at the graveyard does not take the places (Spirit Heal is death-only,
    /// vmangos CanTargetAliveState), so the one ghost among twelve living players comes back.
    /// </summary>
    [Fact]
    public void LivingFriendsAroundTheGuide_DoNotCrowdTheGhostOutOfTheWave()
    {
        (Player guide, _) = _spells.AddPlayer(9);
        for (uint i = 0; i < 12; i++)
            _spells.AddPlayer(20 + i, 2 + (i % 4), i % 3);
        (Player ghost, _) = _spells.AddPlayer(1, 3, 0);
        Map map = _spells.World.GetMap(0);
        map.Combat.KillPlayer(ghost);
        _spells.World.RunTick(100);
        Assert.True(map.Combat.RepopPlayer(ghost));
        Assert.Equal(SpellCastResult.CastOk, _spells.System.CastSpell(ghost, SpiritHealEffect.WaitingToResurrect, SpellCastTargets.ForSelf(), triggered: true));

        for (uint waited = 0; !ghost.IsAlive && waited <= 30_100; waited += 50)
            Update(guide, 50);

        Assert.True(ghost.IsAlive);
    }

    [Fact]
    public void SpiritGuideCreature_UsesNullCreatureAI_DoesNotAggroHostileEnemy_AndRecastsChannelWhenAuraRemoved()
    {
        var template = new CreatureTemplate
        {
            Entry = 13117,
            Name = "Warsong Spirit Guide",
            MinLevel = 60,
            MaxLevel = 60,
            MinLevelHealth = 10000,
            MaxLevelHealth = 10000,
            Faction = 67,
            NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.SpiritGuide),
            Civilian = false,
        };
        var guide = new Creature(7001, template, null, CreatureContent.Empty, new Random(1));
        CreatureAI ai = new CreatureAiFactory().Create(guide, CreatureContent.Empty, out _);
        Assert.IsType<NullCreatureAI>(ai);
        guide.AI = ai;

        Map map = _spells.World.GetMap(0);
        map.AddObject(guide);
        (Player enemy, _) = _spells.AddPlayer(55, 2, 0);
        Assert.False(ai.AggroesOnSight);
        ai.OnUpdate(100);
        Assert.False(guide.Combat.IsInCombat);

        Assert.Equal(SpellCastResult.CastOk, _spells.System.CastSpell(guide, SpiritHealChannel, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(_spells.System.HasAura(guide, SpiritHealChannel));

        _spells.System.RemoveAuras(guide, SpiritHealChannel, AuraRemoveMode.Default);
        Assert.True(_spells.System.HasAura(guide, SpiritHealChannel));
    }
}
