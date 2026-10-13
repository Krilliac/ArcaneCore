using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureAi.SmartAi.SmartRows;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

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
