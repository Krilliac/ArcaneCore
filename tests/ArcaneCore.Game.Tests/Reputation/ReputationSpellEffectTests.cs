using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>
/// The reputation spell effect (103), the gain auras (156 Diplomacy, 190) and forced reactions (139): vmangos
/// SpellEffects.cpp:5307-5323, Player.cpp:6258-6262, SpellAuras.cpp:2785-2805.
/// </summary>
public sealed class ReputationSpellEffectTests
{
    private const uint Reward = 940001;
    private const uint Penalty = 940002;
    private const uint Diplomacy = 940003;
    private const uint FactionBoost = 940004;
    private const uint FurbolgForm = 940005;

    private static Spells.SpellTestKit Kit()
    {
        SpellInfo Perm(uint id, SpellEffectInfo effect) => Spell(id, effect) with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 };
        return new Spells.SpellTestKit(
            Spell(Reward, Effect(SpellEffectName.Reputation, 20, misc: (int)Stormwind)),
            Spell(Penalty, Effect(SpellEffectName.Reputation, -20, misc: (int)Stormwind)),
            Perm(Diplomacy, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModReputationGain)),
            Perm(FactionBoost, Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModFactionReputationGain, misc: (int)BootyBay)),
            Perm(FurbolgForm, Effect(SpellEffectName.ApplyAura, (int)ReputationRank.Friendly, aura: AuraType.ForceReaction, misc: (int)Stormwind)));
    }

    private static (ReputationService Service, Player Player, FakeSession Session) Setup(Spells.SpellTestKit kit, Func<ReputationService, ReputationService>? configure = null)
    {
        (Player player, FakeSession session) = kit.AddPlayer(1);
        var service = new ReputationService(Factions, roll: () => 0.0) { SendForcedReactions = false };
        ReputationEnvironment.Register(kit.System, service);
        service.GainModifier = ReputationSpellHandlers.GainModifier(kit.System);
        service.Track(player, service.Create(player, CharacterReputationData.Empty));
        service.BuildInitializeFactions(player);
        session.Clear();
        return (service, player, session);
    }

    [Fact]
    public void ModuleHandlers_AreRegistered()
    {
        using var kit = Kit();
        Assert.Contains(typeof(ReputationSpellHandlers), kit.System.Modules);
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Reputation));
        foreach (AuraType type in new[] { AuraType.ForceReaction, AuraType.ModReputationGain, AuraType.ModFactionReputationGain })
        {
            Assert.True(kit.System.HasAuraHandler(type), type.ToString());
        }
    }

    [Fact]
    public void ReputationEffect_AddsItsBasePoints_ThroughTheSpellRate()
    {
        using var kit = Kit();
        (ReputationService service, Player player, _) = Setup(kit);
        kit.System.CastSpell(player, Reward, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(20, service.GetReputation(player, Stormwind));
        kit.System.CastSpell(player, Penalty, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, Penalty, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(-20, service.GetReputation(player, Stormwind));
    }

    [Fact]
    public void Diplomacy_AddsTenPercentToGains_ButNeverToLosses()
    {
        using var kit = Kit();
        (ReputationService service, Player player, _) = Setup(kit);
        kit.System.CastSpell(player, Diplomacy, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.CastSpell(player, Reward, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(22, service.GetReputation(player, Stormwind)); // 20 * 110%

        kit.System.CastSpell(player, Penalty, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(2, service.GetReputation(player, Stormwind));  // -20, unscaled (Player.cpp:6254)
    }

    [Fact]
    public void TheFactionGainAura_AppliesToKillsOfThatFactionOnly()
    {
        using var kit = Kit();
        (ReputationService service, Player player, _) = Setup(kit);
        player.Level = 20;
        kit.System.CastSpell(player, FactionBoost, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(125, service.Gain(ReputationSource.Kill, player, 100, BootyBay, 20));   // +25% on kills of Booty Bay
        Assert.Equal(100, service.Gain(ReputationSource.Kill, player, 100, Stormwind, 20));  // another faction
        Assert.Equal(100, service.Gain(ReputationSource.Spell, player, 100, BootyBay, 20));  // not on spells
        Assert.Equal(100, service.Gain(ReputationSource.Quest, player, 100, BootyBay, 20));  // not on quests
    }

    [Fact]
    public void ForceReaction_SetsAndReleasesTheRank_AndStopsAFightWhenFriendly()
    {
        using var kit = Kit();
        (ReputationService service, Player player, FakeSession session) = Setup(kit);
        var stopped = new List<uint>();
        service.StopAttackFaction = (_, faction) => stopped.Add(faction);
        service.SetReputation(player, Stormwind, ReputationMath.Bottom); // Hated
        Assert.Equal(ReputationRank.Hated, service.GetRank(player, Stormwind));
        session.Clear();

        kit.System.CastSpell(player, FurbolgForm, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(service.For(player)!.TryGetForcedRank(Stormwind, out ReputationRank forced));
        Assert.Equal(ReputationRank.Friendly, forced);
        Assert.Equal([Stormwind], stopped);
        Assert.True(service.TryGetNpcReaction(player, StormwindNpc, PlayerTemplate, out ReputationRank reaction));
        Assert.Equal(ReputationRank.Friendly, reaction);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgSetForcedReactions); // off until a real client confirms the width

        kit.System.RemoveAuras(player, FurbolgForm);
        Assert.False(service.For(player)!.TryGetForcedRank(Stormwind, out _));
        Assert.Equal(ReputationRank.Hated, service.GetRank(player, Stormwind)); // the real rank is back, and it is not Friendly: no second stop
        Assert.Single(stopped);
    }

    [Fact]
    public void ForcedReactionsPacket_FollowsVmangos_AndIsSentOnlyWhenEnabled()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        var service = new ReputationService(Factions, roll: () => 0.0) { SendForcedReactions = true };
        ReputationEnvironment.Register(kit.System, service);
        service.Track(player, service.Create(player, CharacterReputationData.Empty));
        session.Clear();

        kit.System.CastSpell(player, FurbolgForm, SpellCastTargets.ForSelf(), triggered: true);
        byte[] packet = Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgSetForcedReactions).Payload;
        Assert.Equal(ReputationPackets.SetForcedReactions(new Dictionary<uint, ReputationRank> { [Stormwind] = ReputationRank.Friendly }), packet);
        // u32 count, then u32 faction and u32 rank (Misc.cpp:522-530).
        Assert.Equal(new byte[] { 1, 0, 0, 0, 72, 0, 0, 0, 4, 0, 0, 0 }, packet);

        kit.System.RemoveAuras(player, FurbolgForm);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, session.Sent.Last(p => p.Opcode == WorldOpcode.SmsgSetForcedReactions).Payload);
    }

    [Fact]
    public void WithoutALiveService_TheHandlersDoNothing()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, Reward, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, FurbolgForm, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(kit.System.HasAura(player, FurbolgForm)); // the aura still applies; there is simply nothing to force
    }
}
