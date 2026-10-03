using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>vmangos GetReactionTo/GetFactionReactionTo for creatures and players, fail-closed.</summary>
public sealed class ReputationReactionTests
{
    private readonly Player _player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
    private readonly PlayerReputation _rep = Human();

    [Fact]
    public void Npc_ReputationFaction_UsesRank()
    {
        Assert.Equal(ReputationRank.Neutral, Npc(StormwindNpc));
        _rep.Apply(Get(Stormwind), 9000, incremental: false);
        Assert.Equal(ReputationRank.Honored, Npc(StormwindNpc));
        _rep.Apply(Get(Stormwind), -6000, incremental: false);
        Assert.Equal(ReputationRank.Hostile, Npc(StormwindNpc));
        _rep.Apply(Get(Stormwind), -42000, incremental: false);
        Assert.Equal(ReputationRank.Hated, Npc(StormwindNpc));
    }

    [Fact]
    public void AtWar_CapsTheNpcAtNeutral_AndMakesThePlayerHostile()
    {
        _rep.Apply(Get(BootyBay), 3000, incremental: false);
        Assert.Equal(ReputationRank.Friendly, Npc(BootyBayNpc));
        Assert.Equal(ReputationRank.Friendly, Pc(BootyBayNpc));
        Assert.True(_rep.SetAtWarByClient(0, true));
        Assert.Equal(ReputationRank.Neutral, Npc(BootyBayNpc));
        Assert.Equal(ReputationRank.Hostile, Pc(BootyBayNpc));
    }

    [Fact]
    public void ContestedGuards_AttackOnlyContestedPlayers()
    {
        Assert.Equal(ReputationRank.Neutral, Npc(ContestedGuard));
        Assert.Equal(ReputationRank.Friendly, Pc(StormwindContestedGuard));
        _player.Flags |= PlayerFlags.ContestedPvp;
        Assert.Equal(ReputationRank.Hostile, Npc(ContestedGuard));
        Assert.Equal(ReputationRank.Hostile, Npc(StormwindContestedGuard));
        Assert.Equal(ReputationRank.Hostile, Pc(StormwindContestedGuard));
    }

    [Fact]
    public void GameMasters_AreNeutralEitherWay()
    {
        _player.Flags |= PlayerFlags.Gm | PlayerFlags.ContestedPvp;
        Assert.Equal(ReputationRank.Neutral, Npc(HostileNpc));
        Assert.Equal(ReputationRank.Neutral, Npc(ContestedGuard));
        Assert.Equal(ReputationRank.Neutral, Pc(HostileNpc));
    }

    [Fact]
    public void NonReputationFactions_UseTemplateRelations()
    {
        Assert.Equal(ReputationRank.Hostile, Npc(HostileNpc));
        Assert.Equal(ReputationRank.Hostile, Npc(DefiasNpc));
        Assert.Equal(ReputationRank.Neutral, Npc(NeutralNpc));
        Assert.Equal(ReputationRank.Friendly, Npc(FriendlyNpc));
        Assert.Equal(ReputationRank.Hostile, Pc(HostileNpc)); // player template hostile to monsters
        Assert.Equal(ReputationRank.Friendly, Pc(FriendlyNpc));
    }

    [Fact]
    public void UnknownFactionsAndMissingState_FailClosed()
    {
        Assert.False(ReputationReactions.TryNpcReactionTo(UnknownNpc, PlayerTemplate, _player, Factions, _rep, out _));
        Assert.False(ReputationReactions.TryPlayerReactionTo(UnknownNpc, PlayerTemplate, _player, Factions, _rep, out _));
        Assert.False(ReputationReactions.TryNpcReactionTo(StormwindNpc, PlayerTemplate, _player, Factions, null, out _));
        Assert.False(ReputationReactions.TryPlayerReactionTo(StormwindNpc, PlayerTemplate, _player, Factions, null, out _));
        // Without Faction.dbc every reputation-bearing template is unresolvable.
        Assert.False(ReputationReactions.TryNpcReactionTo(StormwindNpc, PlayerTemplate, _player, Kernel.Reputation.FactionCatalog.Empty, _rep, out _));
        Assert.True(ReputationReactions.TryNpcReactionTo(HostileNpc, PlayerTemplate, _player, Kernel.Reputation.FactionCatalog.Empty, null, out _));
    }

    private ReputationRank Npc(FactionTemplateRecord npc)
    {
        Assert.True(ReputationReactions.TryNpcReactionTo(npc, PlayerTemplate, _player, Factions, _rep, out ReputationRank reaction));
        return reaction;
    }

    private ReputationRank Pc(FactionTemplateRecord npc)
    {
        Assert.True(ReputationReactions.TryPlayerReactionTo(npc, PlayerTemplate, _player, Factions, _rep, out ReputationRank reaction));
        return reaction;
    }
}
