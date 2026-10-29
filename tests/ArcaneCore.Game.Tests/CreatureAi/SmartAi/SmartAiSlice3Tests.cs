using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureAi.SmartAi.SmartRows;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>SmartAI slice 3: creature actions PLAY_EMOTE (5), SET_EMOTE_STATE (17), EVADE (24), REMOVEAURASFROMSPELL (28), FORCE_DESPAWN (41).</summary>
public sealed class SmartAiSlice3Tests
{
    private static void Pull(SmartRig rig) => rig.Map.Combat.DealDamage(rig.Player, rig.Wolf, 1, direct: false);

    [Fact]
    public void PlayEmote_AOneShotEmoteIsSent_AStateEmoteBecomesTheEmoteState()
    {
        using var rig = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.PlayEmote, a1: 25, link: 1),
            CreatureRow(WolfEntry, 1, SmartEvent.Link, SmartAction.PlayEmote, a1: 10),
        ]);
        Pull(rig);
        Assert.Contains(Packets(rig.Session, WorldOpcode.SmsgEmote), p => BitConverter.ToUInt32(p, 0) == 25);
        Assert.Equal(10u, rig.Wolf.GetUInt32(UpdateFields.UnitNpcEmotestate));
    }

    [Fact]
    public void SetEmoteState_WritesTheEmoteStateOfTheTarget()
    {
        using var rig = SmartRig.Start([CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.SetEmoteState, a1: 69)]);
        Pull(rig);
        Assert.Equal(69u, rig.Wolf.GetUInt32(UpdateFields.UnitNpcEmotestate));
    }

    [Fact]
    public void Evade_TheCreatureLeavesTheFight()
    {
        using var rig = SmartRig.Start([CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.Evade)]);
        Pull(rig);
        Assert.True(rig.Wolf.IsEvading);
        Assert.Empty(rig.Catalog.Rejected);
    }

    [Fact]
    public void Evade_AScriptedEvadeWithNoPlayerOnTheOtherSide_IsNoEvadeLoop()
    {
        // SMART_ACTION_EVADE passes CreatureAnomalies.ScriptedEvadeReason like EventAI's evade, so a script that ends a fight on purpose
        // (here four times inside the 60 s window) raises no creature/evade-loop; the wolf has no victim and no last victim.
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "creature/evade-loop"
            && n.Subject.StartsWith($"creature entry {WolfEntry} ", StringComparison.Ordinal));
        using var rig = SmartRig.Start([CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.Evade)]);

        for (int i = 0; i < 4; i++)
        {
            rig.Wolf.Relocate(25, 0, 83.5f, 0, 0);
            rig.Map.Combat.AttackStop(rig.Wolf);
            rig.Wolf.Combat.LastVictimGuid = default;
            rig.Script.ProcessEventsFor(SmartEvent.Aggro);
            Assert.True(rig.Wolf.IsEvading);
            rig.World.RunTick(6_000);
        }

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void RemoveAurasFromSpell_RemovesTheSpellsAurasFromTheTarget()
    {
        using var rig = SmartRig.Start([CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.RemoveAurasFromSpell, SmartTarget.ActionInvoker, a1: 1234)]);
        Pull(rig);
        Assert.Contains(rig.Spells.RemovedAuras, r => ReferenceEquals(r.Unit, rig.Player) && r.Spell == 1234);
    }

    [Fact]
    public void ForceDespawn_WaitsForTheDelay_ThenTheCreatureGoes()
    {
        using var rig = SmartRig.Start([CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.ForceDespawn, a1: 2000)]);
        var wolf = rig.Wolf;
        Pull(rig);
        Assert.True(wolf.IsAlive);
        rig.World.RunTick(1000);
        Assert.True(wolf.IsAlive);
        rig.World.RunTick(1500);
        Assert.True(!wolf.IsAlive || !rig.Creatures.Creatures.Contains(wolf));
    }

    [Fact]
    public void Catalog_GameObjectEvadeIsRefused_CreatureRowsOfTheNewActionsLoad()
    {
        var catalog = new SmartScriptCatalog(
        [
            Make(50000, 1, 0, 64, 24),
            Make((int)WolfEntry, 0, 0, 4, 5, a1: 25),
            Make((int)WolfEntry, 0, 1, 4, 17, a1: 10),
            Make((int)WolfEntry, 0, 2, 4, 24),
            Make((int)WolfEntry, 0, 3, 4, 28, a1: 5),
            Make((int)WolfEntry, 0, 4, 4, 41),
        ]);
        Assert.Contains("a game object cannot evade", Assert.Single(catalog.Rejected).Reason, StringComparison.Ordinal);
        Assert.Equal(5, catalog.For(WolfEntry, 0).Count);
    }
}

/// <summary>SmartAI slice 3b: SET_FACTION (2), MORPH_TO_ENTRY_OR_MODEL (3), AUTO_ATTACK (20), ALLOW_COMBAT_MOVEMENT (21), DIE (37).</summary>
public sealed class SmartAiSlice3bTests
{
    private static void Pull(SmartRig rig) => rig.Map.Combat.DealDamage(rig.Player, rig.Wolf, 1, direct: false);

    [Fact]
    public void SetFaction_ChangesTheFaction_AndZeroRestoresTheTemplates()
    {
        using var rig = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.SetFaction, a1: 35),
            CreatureRow(WolfEntry, 1, SmartEvent.Evade, SmartAction.SetFaction),
        ]);
        uint original = rig.Wolf.FactionTemplate;
        Pull(rig);
        Assert.Equal(35u, rig.Wolf.FactionTemplate);
        rig.Script.ProcessEventsFor(SmartEvent.Evade);
        Assert.Equal(original, rig.Wolf.FactionTemplate);
    }

    [Fact]
    public void Morph_AModelIdIsWorn_AndZeroGivesTheNativeModelBack()
    {
        using var rig = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.MorphToEntryOrModel, a2: 4321),
            CreatureRow(WolfEntry, 1, SmartEvent.Evade, SmartAction.MorphToEntryOrModel),
        ]);
        Pull(rig);
        Assert.Equal(4321u, rig.Wolf.DisplayId);
        rig.Script.ProcessEventsFor(SmartEvent.Evade);
        Assert.Equal(rig.Wolf.NativeDisplayId, rig.Wolf.DisplayId);
    }

    [Fact]
    public void AutoAttack_AndCombatMovement_SwitchTheCreaturesMeleeAndChase()
    {
        using var rig = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.AutoAttack, link: 1),
            CreatureRow(WolfEntry, 1, SmartEvent.Link, SmartAction.AllowCombatMovement),
        ]);
        Pull(rig);
        Assert.False(rig.WolfAi.MeleeEnabled);
        Assert.False(rig.WolfAi.CombatMovement);
    }

    [Fact]
    public void Die_KillsTheCreature()
    {
        using var rig = SmartRig.Start([CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.Die)]);
        Pull(rig);
        Assert.False(rig.Wolf.IsAlive);
    }

    [Fact]
    public void Catalog_GameObjectRowsOfTheOwnCreatureActionsAreRefused()
    {
        var catalog = new SmartScriptCatalog([Make(50000, 1, 0, 64, 20), Make(50000, 1, 1, 64, 21), Make(50000, 1, 2, 64, 37), Make((int)WolfEntry, 0, 0, 4, 2, a1: 35)]);
        Assert.Equal(3, catalog.Rejected.Count);
        Assert.All(catalog.Rejected, r => Assert.Contains("own creature", r.Reason, StringComparison.Ordinal));
        Assert.Single(catalog.For(WolfEntry, 0));
    }
}

/// <summary>SmartAI slice 3c: events KILL (5), SUMMONED_UNIT (17), RECEIVE_EMOTE (22); actions SOUND (4), CALL_FOR_HELP (39).</summary>
public sealed class SmartAiSlice3cTests
{
    private static void Pull(SmartRig rig, Creature wolf) => rig.Map.Combat.DealDamage(rig.Player, wolf, 1, direct: false);

    [Fact]
    public void Kill_RunsForAMatchingVictim_AndPlayerOnlySkipsCreatures()
    {
        using var rig = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.Kill, SmartAction.SetEventPhase, e3: 1, a1: 3),
        ], creatureSpawns: [Spawn(2, SmartRig.Speaker, 6, 0)]);
        Creature speaker = rig.Creatures.Creatures.Single(c => c.Entry == SmartRig.Speaker);
        rig.WolfAi.OnKilledUnit(speaker);
        Assert.Equal(0u, rig.Script.Phase);
        rig.WolfAi.OnKilledUnit(rig.Player);
        Assert.Equal(3u, rig.Script.Phase);
    }

    [Fact]
    public void SummonedUnit_FiltersByEntry()
    {
        using var rig = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.SummonedUnit, SmartAction.SetEventPhase, e1: SmartRig.Other, a1: 2),
        ], creatureSpawns: [Spawn(2, SmartRig.Speaker, 6, 0), Spawn(3, SmartRig.Other, 7, 0)]);
        rig.WolfAi.OnJustSummoned(rig.Creatures.Creatures.Single(c => c.Entry == SmartRig.Speaker));
        Assert.Equal(0u, rig.Script.Phase);
        rig.WolfAi.OnJustSummoned(rig.Creatures.Creatures.Single(c => c.Entry == SmartRig.Other));
        Assert.Equal(2u, rig.Script.Phase);
    }

    [Fact]
    public void ReceiveEmote_MatchesTheTextEmote_WithThePlayerAsInvoker()
    {
        using var rig = SmartRig.Start([CreatureRow(WolfEntry, 0, SmartEvent.ReceiveEmote, SmartAction.SetEventPhase, e1: 101, a1: 4)]);
        rig.WolfAi.OnReceiveEmote(rig.Player, 102);
        Assert.Equal(0u, rig.Script.Phase);
        rig.WolfAi.OnReceiveEmote(rig.Player, 101);
        Assert.Equal(4u, rig.Script.Phase);
        Assert.Same(rig.Player, rig.Script.LastInvoker);
    }

    [Fact]
    public void Sound_IsSentToThePlayersThatSeeTheCreature()
    {
        using var rig = SmartRig.Start([CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.Sound, a1: 6789)]);
        Pull(rig, rig.Wolf);
        Assert.Contains(Packets(rig.Session, WorldOpcode.SmsgPlaySound), p => BitConverter.ToUInt32(p, 0) == 6789);
    }

    [Fact]
    public void CallForHelp_BringsAFarSameFactionCreatureIntoTheFight()
    {
        using var rig = SmartRig.Start(
            [CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.CallForHelp, a1: 40)],
            creatureSpawns: [Spawn(2, WolfEntry, 30, 0)]);
        Creature first = rig.Creatures.Creatures.Single(c => c.Spawn?.Guid == 1);
        Creature second = rig.Creatures.Creatures.Single(c => c.Spawn?.Guid == 2);
        Pull(rig, first);
        Assert.True(second.Combat.IsInCombat);
    }
}
