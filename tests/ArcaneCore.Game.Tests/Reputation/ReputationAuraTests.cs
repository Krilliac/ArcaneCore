using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Reputation;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Reputation;

public sealed class ReputationAuraTests
{
    private const uint Generic=995156, Faction=995190, Other=995157, Penalty=995158;
    private static SpellInfo Aura(uint id, AuraType type, int amount, int faction=0)
        => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura:type, misc:faction)) with
        { Duration=new SpellDuration(-1,0,-1) };
    private static SpellTestKit Kit() => new(Aura(Generic,AuraType.ModReputationGain,10),
        Aura(Faction,AuraType.ModFactionReputationGain,20,(int)Stormwind),
        Aura(Other,AuraType.ModReputationGain,5),Aura(Penalty,AuraType.ModReputationGain,-200));
    private static ReputationService Service(SpellTestKit kit,Player player)
    {
        var service=new ReputationService(Factions,roll:()=>0.999);
        service.Track(player,service.Create(player,CharacterReputationData.Empty));
        service.BuildInitializeFactions(player);
        ReputationAuras.Bind(kit.System,service);
        return service;
    }
    private static void Cast(SpellTestKit kit,Player player,uint id)
        => Assert.Equal(SpellCastResult.CastOk,kit.System.CastSpell(player,id,SpellCastTargets.ForSelf(),triggered:true));

    [Fact]
    public void GeneralGainAppliesToPositiveRewardsAndRemovalRestoresBaseWhileLossesRemainUnmodified()
    {
        using var kit=Kit();var (player,_)=kit.AddPlayer(1);var service=Service(kit,player);
        Cast(kit,player,Generic);
        Assert.Equal(110,service.Gain(ReputationSource.Quest,player,100,Stormwind,player.Level));
        Assert.Equal(110,service.Gain(ReputationSource.Kill,player,100,Stormwind,player.Level));
        Assert.Equal(-100,service.Gain(ReputationSource.Quest,player,-100,Stormwind,player.Level));
        kit.System.RemoveAuras(player,Generic);
        Assert.Equal(100,service.Gain(ReputationSource.Quest,player,100,Stormwind,player.Level));
        Cast(kit,player,Penalty);
        Assert.Equal(0,service.Gain(ReputationSource.Quest,player,100,Stormwind,player.Level));
        Assert.Equal(-100,service.Gain(ReputationSource.Quest,player,-100,Stormwind,player.Level));
    }

    [Fact]
    public void FactionModifierMatchesOnlyKillFactionAndDoesNotAffectQuestRewards()
    {
        using var kit=Kit();var (player,_)=kit.AddPlayer(1);var service=Service(kit,player);
        Cast(kit,player,Generic);Cast(kit,player,Faction);
        Assert.Equal(130,service.Gain(ReputationSource.Kill,player,100,Stormwind,player.Level));
        Assert.Equal(110,service.Gain(ReputationSource.Kill,player,100,BootyBay,player.Level));
        Assert.Equal(110,service.Gain(ReputationSource.Quest,player,100,Stormwind,player.Level));
        kit.System.RemoveAuras(player,Faction);
        Assert.Equal(110,service.Gain(ReputationSource.Kill,player,100,Stormwind,player.Level));
    }

    [Fact]
    public void SurvivingAndRestoredAurasAreReadFromLiveStateWithoutCachedBonuses()
    {
        using var kit=Kit();var (player,_)=kit.AddPlayer(1);var service=Service(kit,player);
        Cast(kit,player,Generic);Cast(kit,player,Other);
        Assert.Equal(115,service.Gain(ReputationSource.Quest,player,100,Stormwind,player.Level));
        kit.System.RemoveAuras(player,Generic);
        Assert.Equal(105,service.Gain(ReputationSource.Quest,player,100,Stormwind,player.Level));
        var saved=kit.System.CaptureState(player,1_800_000_000_000);
        kit.System.RemoveAuras(player,Other);
        Assert.Single(kit.System.RestoreAuras(player,saved.Auras,1_800_000_000_000));
        Assert.Equal(105,service.Gain(ReputationSource.Quest,player,100,Stormwind,player.Level));
    }

    [Fact]
    public void QuestStageFreezesAuraBonusAndPublicationDoesNotRecalculateAfterRemoval()
    {
        using var kit=Kit();var (player,_)=kit.AddPlayer(1);var service=Service(kit,player);
        Cast(kit,player,Generic);
        Assert.True(service.TryStage(player,(int)player.Level,[new(Stormwind,250)],out var stage));
        Assert.Equal(0,service.GetReputation(player,Stormwind));
        Assert.Equal(275,Assert.Single(stage.Gains).Gain);
        kit.System.RemoveAuras(player,Generic);
        Assert.True(service.Publish(player,stage));
        Assert.Equal(275,service.GetReputation(player,Stormwind));
    }
}
