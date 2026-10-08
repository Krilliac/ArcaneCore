using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Protocol;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Battlegrounds;

/// <summary>
/// The world side of the <see cref="BattlegroundManager"/> (<see cref="IBattlegroundManagerHost"/>): the queue packets, the instance ids, the
/// entry points, the port into a match, and the ports every match shares (spells, honor, ranks, reputation). World thread.
/// </summary>
internal sealed class BattlegroundWorldHost(BattlegroundFeature feature) : IBattlegroundManagerHost,
    IBattlegroundSpellPort, IBattlegroundHonorSink, IHonorRankSource, IBattlegroundReputationSink
{
    /// <summary>Instance ids when no instance manager exists (tests without dungeons); far above any dungeon save id.</summary>
    private uint _fallbackInstanceId = 1_000_000;

    private WorldRuntime World => feature.World;

    private Player? Online(ObjectGuid guid) => World.FindOnlinePlayer(guid);

    public BattlegroundPorts BasePorts() => new()
    {
        Spells = this,
        Honor = this,
        Ranks = this,
        Reputation = this,
    };

    // ------------------------------------------------------------------ IBattlegroundManagerHost

    public bool IsOnline(ObjectGuid player) => Online(player) is not null;

    public IBattlegroundHost CreateMatchHost(BattlegroundType type, uint instanceId) => feature.CreateMatch(type, instanceId);

    public uint AllocateInstanceId()
        => feature.Services.GetService<InstanceFeature>() is { } instances
            ? instances.Instances.AllocateInstanceId()
            : _fallbackInstanceId++;

    public void SendStatus(ObjectGuid player, uint queueSlot, BattlegroundStatusSubject subject, BattlegroundStatus status, uint time1, uint time2)
        => Online(player)?.Session.Send(WorldOpcode.SmsgBattlefieldStatus,
            BattlegroundPackets.BuildBattlefieldStatus(queueSlot, subject.MapId, subject.Bracket, subject.ClientInstanceId, status, time1, time2));

    public void SendGroupJoined(ObjectGuid player, uint result)
        => Online(player)?.Session.Send(WorldOpcode.SmsgGroupJoinedBattleground, BattlegroundPackets.BuildGroupJoined(result));

    /// <summary>vmangos <c>SendBattleGroundJoinError</c> (BattleGroundHandler.cpp:620-656): a system message of the error's string.</summary>
    public void SendJoinError(ObjectGuid player, BattlegroundJoinError error)
    {
        uint text = error switch
        {
            BattlegroundJoinError.OfflineMember => 727,
            BattlegroundJoinError.GroupTooMany => 720,
            BattlegroundJoinError.MixedFaction => 728,
            BattlegroundJoinError.MixedLevels => 729,
            BattlegroundJoinError.GroupMemberAlreadyInQueue => 730,
            BattlegroundJoinError.GroupDeserter => 731,
            BattlegroundJoinError.AllQueuesUsed => 732,
            _ => 0,
        };
        if (text != 0 && BattlegroundStrings.Mangos(text) is { } message)
        {
            Online(player)?.Session.Send(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(message));
        }
    }

    public void GroupQueueLimitNotice(ObjectGuid player, uint limit)
        => Online(player)?.Session.Send(WorldOpcode.SmsgMessagechat,
            ChatPackets.BuildSystemMessage($"Group queue limit is set to {limit}. You have been queued solo."));

    /// <summary>vmangos <c>SetBattleGroundEntryPoint</c>: the leader's place (the member's own when solo), or the player's at a portal.</summary>
    public void StoreEntryPoint(ObjectGuid member, ObjectGuid leader, bool queuedAtPortal)
    {
        if ((queuedAtPortal ? Online(member) : Online(leader) ?? Online(member)) is { } from)
        {
            feature.StoreEntryPoint(member, from);
        }
    }

    /// <summary>vmangos HandleBattleFieldPortOpcode (BattleGroundHandler.cpp:453-464): a dead player is resurrected at full health before the port.</summary>
    public void PrepareToPortIn(ObjectGuid player)
    {
        if (Online(player) is { Map: { } map } p && !p.IsAlive)
        {
            map.Combat.ResurrectPlayer(p, 1.0f, applySickness: false);
            map.Combat.SpawnCorpseBones(p);
        }
    }

    /// <summary>vmangos <c>SendToBattleGround</c> (BattleGroundMgr.cpp:1452-1473): AFK goes, then the teleport to the team's start location.</summary>
    public void SendToBattleground(ObjectGuid player, Battleground battleground, Team team)
    {
        if (Online(player) is not { } p)
        {
            return;
        }

        if (p.IsAfk)
        {
            p.ToggleAfk();
        }

        BattlegroundStartLocation start = team == Team.Alliance ? battleground.Template.AllianceStart : battleground.Template.HordeStart;
        if (feature.Services.GetService<TeleportFeature>() is { } teleport
            && !teleport.Teleports.TeleportTo(p, battleground.MapId, start.X, start.Y, start.Z, start.Orientation))
        {
            feature.Logger.LogWarning("could not teleport {Player} into {Battleground} instance {Instance}", p.Name, battleground.Name, battleground.InstanceId);
        }
    }

    public void BattlegroundCreated(Battleground battleground) => feature.OnBattlegroundCreated(battleground);

    public void BattlegroundDeleted(Battleground battleground) => feature.OnBattlegroundDeleted(battleground);

    // ------------------------------------------------------------------ IBattlegroundSpellPort

    private SpellSystem? Spells => feature.Services.GetService<SpellFeature>()?.System;

    /// <summary>
    /// vmangos <c>CastSpell(player, spell, true)</c>. The dropped-flag spells (23334, 23336) summon the dropped flag (vmangos
    /// <c>Spell::EffectSummonObjectWild</c>, SpellEffects.cpp:3596-3666); the spell system has no SUMMON_OBJECT_WILD effect, so the match
    /// places the object itself.
    /// </summary>
    public void CastOnSelf(ObjectGuid player, uint spellId)
    {
        if (Online(player) is not { } p)
        {
            return;
        }

        if (spellId is WarsongGulch.SpellWarsongFlagDropped or WarsongGulch.SpellSilverwingFlagDropped)
        {
            feature.MatchOf(player)?.SummonDroppedFlag(p, spellId);
            return;
        }

        Spells?.CastSpell(p, spellId, SpellCastTargets.ForSelf(), triggered: true);
    }

    public void RemoveAura(ObjectGuid player, uint spellId)
    {
        if (Online(player) is { } p)
        {
            Spells?.RemoveAuras(p, spellId);
        }
    }

    public bool HasAura(ObjectGuid player, uint spellId) => Online(player) is { } p && Spells?.HasAura(p, spellId) == true;

    public void RemoveSpiritOfRedemption(ObjectGuid player) => RemoveAura(player, BattlegroundConstants.SpellSpiritOfRedemption);

    // ------------------------------------------------------------------ honor, rank, reputation

    /// <summary>vmangos <c>GetHonorMgr().Add(honor, BONUS)</c> (BattleGround.cpp:1272-1276); nothing while honor is disabled.</summary>
    public bool TryAddBonusHonor(ObjectGuid player, uint honor)
        => Online(player) is { } p && feature.Services.GetService<HonorFeature>()?.ActiveService is { } service
            && service.Add(p, honor, HonorKind.Bonus, null);

    public uint? RankOf(ObjectGuid player)
        => Online(player) is { } p && feature.Services.GetService<HonorFeature>()?.ActiveService?.For(p) is { } state ? state.Rank.Rank : null;

    /// <summary>vmangos <c>RewardReputationToTeam</c> (BattleGround.cpp:586-613): the spell-source gain rules, then the change.</summary>
    public void Reward(ObjectGuid player, uint factionId, int baseAmount)
    {
        if (Online(player) is not { } p || feature.Services.GetService<ReputationFeature>()?.Service is not { } reputation)
        {
            return;
        }

        int gain = reputation.Gain(ReputationSource.Spell, p, baseAmount, factionId, 0);
        reputation.ModifyReputation(p, factionId, gain);
    }
}
