using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The DUMMY and SCRIPT_EFFECT spells and DUMMY auras that only cmangos classic or mangoszero handle (Spell::EffectDummy and Aura::HandleAuraDummy
/// cases, and the ScriptDevAI spell scripts), ported to <c>Spells/Generic/OtherCores</c>. Each marker aura stands for the spell the script casts.
/// </summary>
public sealed class OtherCoresScriptTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo Dummy(uint id) => Instant(Spell(id, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster)));

    private static SpellInfo ScriptEffect(uint id) => Instant(Spell(id, Effect(SpellEffectName.ScriptEffect, 0, SpellImplicitTarget.UnitCaster)));

    private static SpellInfo Marker(uint id) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000),
        });

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    private static void CastSelf(SpellTestKit kit, Player player, uint spellId)
        => Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true));

    [Fact]
    public void IceLock_TheTargetFreezesItself()
    {
        using SpellTestKit kit = Kit(ScriptEffect(5249), Marker(22856));
        (Player player, _) = kit.AddPlayer(1);

        CastSelf(kit, player, 5249);

        Assert.True(kit.System.HasAura(player, 22856));
    }

    [Fact]
    public void DragonOrb_PossessesAPlayerOnlyWithoutMindExhaustion()
    {
        using SpellTestKit kit = Kit(Dummy(19869), Marker(19832), Marker(23958));
        (Player free, _) = kit.AddPlayer(1);
        (Player tired, _) = kit.AddPlayer(2);
        Assert.True(kit.System.AddAura(tired, 23958));

        CastSelf(kit, free, 19869);
        CastSelf(kit, tired, 19869);

        Assert.True(kit.System.HasAura(free, 19832));
        Assert.False(kit.System.HasAura(tired, 19832));
    }

    [Fact]
    public void SummonDementedDruidSpirit_CastsTheSpiritOnAPlayerTarget()
    {
        using SpellTestKit kit = Kit(Dummy(24796), Marker(24795));
        (Player player, _) = kit.AddPlayer(1);

        CastSelf(kit, player, 24796);

        Assert.True(kit.System.HasAura(player, 24795));
    }

    [Theory]
    [InlineData(25145u, 25172u, 0u)]
    [InlineData(25149u, 25168u, 0u)]
    [InlineData(25150u, 25169u, 25170u)]
    public void SilithusAncientSpells_TheTargetCastsItsFollowUps(uint spell, uint first, uint second)
    {
        using SpellTestKit kit = Kit(Dummy(spell), Marker(first), Marker(second == 0 ? 90001u : second));
        (Player player, _) = kit.AddPlayer(1);

        CastSelf(kit, player, spell);

        Assert.True(kit.System.HasAura(player, first));
        Assert.Equal(second != 0, kit.System.HasAura(player, second == 0 ? 90001u : second));
    }

    [Fact]
    public void TimeStop_StripsTheTargetsAurasThenCastsTheVisual()
    {
        using SpellTestKit kit = Kit(Dummy(25158), Marker(25171), Marker(90010));
        (Player player, _) = kit.AddPlayer(1);
        Assert.True(kit.System.AddAura(player, 90010));

        CastSelf(kit, player, 25158);

        Assert.False(kit.System.HasAura(player, 90010));
        Assert.True(kit.System.HasAura(player, 25171));
        Assert.True((player.UnitFlags & UnitFlags.ImmuneToNpc) != 0);
    }

    [Theory]
    [InlineData(22276u, 22277u, 22281u)]
    [InlineData(22282u, 22283u, 22288u)]
    public void RandomShieldSpells_LeaveExactlyOneOfTheirFiveAuras(uint spell, uint low, uint high)
    {
        uint[] set = spell == 22276 ? [22277, 22278, 22279, 22280, 22281] : [22283, 22285, 22286, 22287, 22288];
        using SpellTestKit kit = Kit([Dummy(spell), .. set.Select(Marker)]);
        (Player player, _) = kit.AddPlayer(1);
        Assert.True(kit.System.AddAura(player, low));
        Assert.True(kit.System.AddAura(player, high));

        CastSelf(kit, player, spell);

        Assert.Single(set, id => kit.System.HasAura(player, id));
    }

    [Theory]
    [InlineData(true, 26082u, 26081u)]
    [InlineData(false, 26081u, 26082u)]
    public void StingerChargePrimer_PicksTheChargeByTheCatalyst(bool catalyst, uint expected, uint other)
    {
        using SpellTestKit kit = Kit(Dummy(26080), Marker(26078), Marker(26081), Marker(26082));
        (Player player, _) = kit.AddPlayer(1);
        if (catalyst)
        {
            Assert.True(kit.System.AddAura(player, 26078));
        }

        CastSelf(kit, player, 26080);

        Assert.True(kit.System.HasAura(player, expected));
        Assert.False(kit.System.HasAura(player, other));
    }

    [Fact]
    public void InitializeTank_GivesTheControlAndPassiveAuras()
    {
        using SpellTestKit kit = Kit(ScriptEffect(27749), Marker(24936), Marker(27747));
        (Player player, _) = kit.AddPlayer(1);

        CastSelf(kit, player, 27749);

        Assert.True(kit.System.HasAura(player, 24936));
        Assert.True(kit.System.HasAura(player, 27747));
    }

    [Theory]
    [InlineData(28415u, 28421u)]
    [InlineData(28416u, 28422u)]
    [InlineData(28417u, 28423u)]
    [InlineData(28455u, 28454u)]
    public void KelThuzadAddTriggers_CastTheirSummonOnTheTrigger(uint trigger, uint summon)
    {
        using SpellTestKit kit = Kit(ScriptEffect(trigger), Marker(summon));
        (Player player, _) = kit.AddPlayer(1);

        CastSelf(kit, player, trigger);

        Assert.True(kit.System.HasAura(player, summon));
    }

    [Theory]
    [InlineData(28628u)]
    [InlineData(28629u)]
    public void ClearWebWrap_FreesAWrappedPlayer(uint spell)
    {
        using SpellTestKit kit = Kit(ScriptEffect(spell), Marker(28627), Marker(28622), Marker(29108));
        (Player player, _) = kit.AddPlayer(1);
        Assert.True(kit.System.AddAura(player, 28627));
        Assert.True(kit.System.AddAura(player, 28622));

        CastSelf(kit, player, spell);

        // 28628 clears a player's wrap (cmangos boss_maexxna.cpp ClearWebWrap); 28629 acts on a creature (the Web Wrap NPC) only.
        Assert.Equal(spell != 28628, kit.System.HasAura(player, 28627));
        Assert.Equal(spell != 28628, kit.System.HasAura(player, 28622));
        Assert.False(kit.System.HasAura(player, 29108));
    }

    [Fact]
    public void ZombieChowSearchHeal_HealsFivePercent()
    {
        using SpellTestKit kit = Kit(Dummy(28238));
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 4000;
        player.Health = 1000;

        CastSelf(kit, player, 28238);

        Assert.Equal(1200u, player.Health);
    }

    [Theory]
    [InlineData(19707u, 500f)]
    [InlineData(26569u, 0f)]
    public void HateSpells_CutTheCastersThreatOnTheTarget(uint spell, float expected)
    {
        using SpellTestKit kit = Kit(Instant(Spell(spell, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy))));
        (Player player, _) = kit.AddPlayer(1);
        Creature wolf = MakeCreature(kit);
        wolf.Combat.Threat.AddThreat(player, 1000f);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(wolf, spell, SpellCastTargets.ForUnit(player.Guid), triggered: true));

        Assert.Equal(expected, wolf.Combat.Threat.Entries.Single(e => ReferenceEquals(e.Target, player)).Threat, 0.5f);
    }

    [Fact]
    public void LordaeronsBlessing_GivesItsEffectAuraWhileItLasts()
    {
        using SpellTestKit kit = Kit(Marker(30238), Marker(31906));
        (Player player, _) = kit.AddPlayer(1);

        CastSelf(kit, player, 30238);
        Assert.True(kit.System.HasAura(player, 31906));

        kit.System.RemoveAuras(player, 30238);
        Assert.False(kit.System.HasAura(player, 31906));
    }

    [Fact]
    public void FrostwhispersLifeblood_FillsTheCastersHealthWhenItEnds()
    {
        using SpellTestKit kit = Kit(Marker(17189));
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 5000;
        player.Health = 1000;
        CastSelf(kit, player, 17189);
        Assert.Equal(1000u, player.Health);

        kit.System.RemoveAuras(player, 17189);

        Assert.Equal(5000u, player.Health);
    }

    [Fact]
    public void HeiganTeleportTrigger_TheTargetCastsTeleportPlayersOnItself()
    {
        using SpellTestKit kit = Kit(ScriptEffect(29499), Marker(29273));
        (Player player, _) = kit.AddPlayer(1);

        CastSelf(kit, player, 29499);

        Assert.True(kit.System.HasAura(player, 29273));
    }

    [Fact]
    public void TriggerTeslas_StripsAurasThenCastsShockOverload()
    {
        using SpellTestKit kit = Kit(Dummy(28359), Marker(28159), Marker(90020));
        (Player player, _) = kit.AddPlayer(1);
        Assert.True(kit.System.AddAura(player, 90020));

        CastSelf(kit, player, 28359);

        Assert.False(kit.System.HasAura(player, 90020));
        Assert.True(kit.System.HasAura(player, 28159));
    }

    [Fact]
    public void GraveyardTeleport_HasAnEffectHandlerAndIgnoresNonBattlegrounds()
    {
        using SpellTestKit kit = Kit(Instant(Spell(24237, Effect(SpellEffectName.TeleportGraveyard, 0, SpellImplicitTarget.UnitCaster))));
        (Player player, _) = kit.AddPlayer(1);

        Assert.True(kit.System.HasEffectHandler(SpellEffectName.TeleportGraveyard));
        CastSelf(kit, player, 24237);

        Assert.True(player.IsAlive);
    }

    [Fact]
    public void Discovery_RegistersEveryOtherCoresScript()
    {
        var ids = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly).SpellIds.ToHashSet();
        foreach (uint id in new uint[]
        {
            5249, 17678, 18269, 19484, 19707, 19869, 22276, 22282, 22659, 22664, 23450, 23484, 24019, 24342, 24796, 24886, 25145, 25149, 25150,
            25158, 25792, 26080, 26264, 26569, 26585, 27749, 28000, 28238, 28359, 28415, 28416, 28417, 28455, 28628, 28629, 29336, 29499, 16510,
        })
        {
            Assert.Contains(id, ids);
        }
    }

    /// <summary>A wolf spawned into the kit's map (threat needs both units in the same map).</summary>
    private static Creature MakeCreature(SpellTestKit kit)
    {
        CreatureTemplate template = CreatureTestKit.Template(CreatureTestKit.WolfEntry, t =>
        {
            t.MinLevelHealth = 100;
            t.MaxLevelHealth = 100;
        });
        CreatureSpawn spawn = CreatureTestKit.Spawn(77, CreatureTestKit.WolfEntry, 3, 0);
        var system = new CreatureMapSystem(kit.World.GetMap(0), CreatureTestKit.Content([template], [spawn]), null, random: new Random(1));
        kit.World.GetMap(0).AddUpdater(system);
        kit.World.RunTick(50);
        Creature creature = Assert.Single(system.Creatures);
        return creature;
    }
}
