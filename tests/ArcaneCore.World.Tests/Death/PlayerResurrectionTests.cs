using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Death;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

public sealed class PlayerResurrectionTests
{
    private const uint Resurrection = 991180;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientCastAndAccept_RelocatesAfterAck_RestoresLifeAndPersistsIt(bool released)
    {
        await using WorldTestHost host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player);
        await using WorldTestClient healer = await host.EnterWorldAsync("RESHEAL", "Reshealer");
        await using WorldTestClient target = await host.EnterWorldAsync("RESDEAD", "Resdead");
        ulong targetGuid = await ConfigureAsync(host, "Reshealer", "Resdead");
        SpellCastTargets castTargets = SpellCastTargets.ForUnit(new ObjectGuid(targetGuid));
        if (released)
        {
            castTargets = await host.OnWorldAsync(() =>
            {
                Player victim = host.World.FindOnlinePlayer("Resdead")!;
                Assert.True(victim.Map!.Combat.RepopPlayer(victim));
                victim.Relocate(victim.X + 1000, victim.Y, victim.Z, 0, host.World.NowMs);
                return new SpellCastTargets { Mask = SpellCastTargetFlags.CorpseAlly, Corpse = victim.Combat.Corpse!.Guid };
            });
        }
        await healer.CollectAsync();
        await target.CollectAsync();

        var payload = new PacketWriter();
        payload.WriteUInt32(Resurrection);
        castTargets.Write(payload);
        await healer.SendAsync(WorldOpcode.CmsgCastSpell, payload.ToArray());

        byte[] offer = await target.ReadUntilAsync(WorldOpcode.SmsgResurrectRequest);
        ulong casterGuid = await host.PlayerStateAsync("Reshealer", p => p.Guid.Value);
        AssertOffer(offer, casterGuid);
        Assert.False(await host.PlayerStateAsync("Resdead", p => p.IsAlive));

        await target.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(casterGuid + 99, true));
        await target.CollectAsync();
        Assert.False(await host.PlayerStateAsync("Resdead", p => PlayerResurrection.GetRequest(p)!.Accepted));
        await target.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(casterGuid, true));
        await target.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
        Assert.False(await host.PlayerStateAsync("Resdead", p => p.IsAlive));
        await target.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(casterGuid, true));
        Assert.DoesNotContain(await target.CollectAsync(), p => p.Opcode == WorldOpcode.MsgMoveTeleportAck);
        await target.SendAsync(WorldOpcode.MsgMoveTeleportAck, TeleportAck(targetGuid + 99));
        await target.CollectAsync();
        Assert.False(await host.PlayerStateAsync("Resdead", p => p.IsAlive));
        await target.SendAsync(WorldOpcode.MsgMoveTeleportAck, TeleportAck(targetGuid));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Resdead")!.IsAlive, "resurrection after teleport acknowledgement");
        await host.OnWorldAsync(() =>
        {
            Player victim = host.World.FindOnlinePlayer("Resdead")!;
            Player caster = host.World.FindOnlinePlayer("Reshealer")!;
            Assert.Equal((caster.X, caster.Y, caster.Z), (victim.X, victim.Y, victim.Z));
            Assert.InRange(victim.Health, 250u, 999u); // ordinary world regeneration can follow restoration immediately
            Assert.Equal(200u, victim.GetUInt32(UpdateFields.UnitFieldPower1));
            Assert.Null(victim.Combat.Corpse);
            Assert.False(victim.Flags.HasFlag(PlayerFlags.Ghost));
            Assert.Null(PlayerResurrection.GetRequest(victim));
            Assert.Empty(victim.Map!.Combat.Corpses);
        });
        await WorldTestHost.WaitForAsync(() => host.Characters.Life((int)targetGuid) is { Health: 250, IsGhost: false, Corpse: null }, "the completed resurrection save");

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Resdead")!.Health = 123);
        await target.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(casterGuid, true));
        await target.SendAsync(WorldOpcode.MsgMoveTeleportAck, TeleportAck(targetGuid));
        await target.CollectAsync();
        Assert.InRange(await host.PlayerStateAsync("Resdead", p => p.Health), 123u, 249u);
        await target.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await target.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await target.LoginAsync(targetGuid);
        Assert.True(await host.PlayerStateAsync("Resdead", p => p.IsAlive));
        Assert.False(await host.PlayerStateAsync("Resdead", p => p.Flags.HasFlag(PlayerFlags.Ghost)));
        Assert.Null(await host.PlayerStateAsync("Resdead", p => p.Combat.Corpse));
    }

    [Fact]
    public async Task DeclineClearsOfferWithoutTeleport_AndAReplacementOfferCanBeAccepted()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient healer = await host.EnterWorldAsync("RESNOHEAL", "Resnoheal");
        await using WorldTestClient target = await host.EnterWorldAsync("RESNODEAD", "Resnodead");
        ulong guid = await ConfigureAsync(host, "Resnoheal", "Resnodead");
        await CastAsync(healer, SpellCastTargets.ForUnit(new ObjectGuid(guid)));
        await target.ReadUntilAsync(WorldOpcode.SmsgResurrectRequest);
        await target.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(0, false));
        await target.CollectAsync();
        Assert.Null(await host.PlayerStateAsync("Resnodead", PlayerResurrection.GetRequest));
        Assert.False(await host.PlayerStateAsync("Resnodead", p => p.IsAlive));
        await CastAsync(healer, SpellCastTargets.ForUnit(new ObjectGuid(guid)));
        await target.ReadUntilAsync(WorldOpcode.SmsgResurrectRequest);
        ulong caster = await host.PlayerStateAsync("Resnoheal", p => p.Guid.Value);
        await target.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(caster, true));
        await target.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
    }

    [Fact]
    public async Task CrossMapGhost_CorpseCastCompletesNormalCastBar_ThenFarAckRestoresLife()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient healer = await host.EnterWorldAsync("RESFARHEAL", "Resfarheal");
        await using WorldTestClient target = await host.EnterWorldAsync("RESFARDEAD", "Resfardead");
        ulong guid = await ConfigureAsync(host, "Resfarheal", "Resfardead");
        SpellCastTargets targets = await host.OnWorldAsync(() =>
        {
            Player victim = host.World.FindOnlinePlayer("Resfardead")!;
            var map = victim.Map!;
            Assert.True(map.Combat.RepopPlayer(victim));
            ObjectGuid body = victim.Combat.Corpse!.Guid;
            map.RemovePlayer(victim);
            victim.MapId = 1;
            victim.Relocate(1000, 1000, victim.Z, 0, host.World.NowMs);
            host.World.GetMap(1).AddPlayer(victim);
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            spells.Store = new SpellStore(spells.Store.All.Select(s => s.Id == Resurrection ? s with { CastTime = new SpellCastTime(100, 0, 100) } : s), [], []);
            return new SpellCastTargets { Mask = SpellCastTargetFlags.Unit | SpellCastTargetFlags.CorpseAlly, Unit = victim.Guid, Corpse = body };
        });
        await target.CollectAsync();
        await CastAsync(healer, targets);
        await target.ReadUntilAsync(WorldOpcode.SmsgResurrectRequest);
        ulong casterGuid = await host.PlayerStateAsync("Resfarheal", p => p.Guid.Value);
        await target.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(casterGuid, true));
        await target.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        Assert.False(await host.PlayerStateAsync("Resfardead", p => p.IsAlive));
        await target.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Resfardead")!.IsAlive, "far resurrection after worldport acknowledgement");
        Assert.Equal(0u, await host.PlayerStateAsync("Resfardead", p => p.MapId));
        Assert.Null(await host.PlayerStateAsync("Resfardead", p => p.Combat.Corpse));
        await WorldTestHost.WaitForAsync(() => host.Characters.Life((int)guid) is { Health: 250, IsGhost: false, Corpse: null }, "far resurrection save");
    }

    [Fact]
    public async Task SupersedingTeleportDoesNotConsumeAcceptedOfferAtTheWrongLocation()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient healer = await host.EnterWorldAsync("RESSHHEAL", "Resshheal");
        await using WorldTestClient target = await host.EnterWorldAsync("RESSHDEAD", "Resshdead");
        ulong guid = await ConfigureAsync(host, "Resshheal", "Resshdead");
        await CastAsync(healer, SpellCastTargets.ForUnit(new ObjectGuid(guid)));
        await target.ReadUntilAsync(WorldOpcode.SmsgResurrectRequest);
        ulong caster = await host.PlayerStateAsync("Resshheal", p => p.Guid.Value);
        await target.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(caster, true));
        await target.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
        await host.OnWorldAsync(() =>
        {
            Player victim = host.World.FindOnlinePlayer("Resshdead")!;
            var teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            Assert.True(teleports.TeleportTo(victim, victim.MapId, victim.X + 50, victim.Y, victim.Z, 0));
        });
        await target.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
        await target.SendAsync(WorldOpcode.MsgMoveTeleportAck, TeleportAck(guid));
        await target.CollectAsync();
        Assert.False(await host.PlayerStateAsync("Resshdead", p => p.IsAlive));
        Assert.Null(await host.PlayerStateAsync("Resshdead", PlayerResurrection.GetRequest));
    }

    [Fact]
    public async Task SettlementBlocksDeclineAndAcceptance_AndDefersAckRestorationUntilReleased()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient healer = await host.EnterWorldAsync("RESHOLDHEAL", "Resholdheal");
        await using WorldTestClient target = await host.EnterWorldAsync("RESHOLDDEAD", "Resholddead");
        ulong guid = await ConfigureAsync(host, "Resholdheal", "Resholddead");
        await CastAsync(healer, SpellCastTargets.ForUnit(new ObjectGuid(guid)));
        await target.ReadUntilAsync(WorldOpcode.SmsgResurrectRequest);
        ulong caster = await host.PlayerStateAsync("Resholdheal", p => p.Guid.Value);
        Guid operation = Guid.NewGuid();
        await host.OnWorldAsync(() =>
        {
            Player victim = host.World.FindOnlinePlayer("Resholddead")!;
            Assert.True(victim.BeginQuestSettlement(operation));
            var feature = host.WorldServices.GetRequiredService<PlayerResurrectionFeature>();
            feature.Respond(victim, new ObjectGuid(caster), true);
            feature.Respond(victim, ObjectGuid.Empty, false);
            Assert.False(PlayerResurrection.GetRequest(victim)!.Accepted);
            Assert.False(host.WorldServices.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(victim));
            Assert.True(victim.EndQuestSettlement(operation));
        });
        await target.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(caster, true));
        await target.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
        await host.OnWorldAsync(() => Assert.True(host.World.FindOnlinePlayer("Resholddead")!.BeginQuestSettlement(operation)));
        await target.SendAsync(WorldOpcode.MsgMoveTeleportAck, TeleportAck(guid));
        await target.CollectAsync();
        Assert.False(await host.PlayerStateAsync("Resholddead", p => p.IsAlive));
        Assert.True(await host.PlayerStateAsync("Resholddead", p => PlayerResurrection.GetRequest(p)!.Accepted));
        await host.OnWorldAsync(() => Assert.True(host.World.FindOnlinePlayer("Resholddead")!.EndQuestSettlement(operation)));
        await target.SendAsync(WorldOpcode.MsgMoveTeleportAck, TeleportAck(guid));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Resholddead")!.IsAlive, "resurrection after settlement releases the teleport acknowledgement");
    }

    [Fact]
    public async Task LogoutDuringAcceptedTeleport_ClearsRequest_AndRelogCannotReplayIt()
    {
        await using WorldTestHost host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player);
        await using WorldTestClient healer = await host.EnterWorldAsync("RESQUITHEAL", "Resquitheal");
        await using WorldTestClient target = await host.EnterWorldAsync("RESQUITDEAD", "Resquitdead");
        ulong guid = await ConfigureAsync(host, "Resquitheal", "Resquitdead");
        Player old = await host.PlayerAsync("Resquitdead");
        await host.OnWorldAsync(() => Assert.True(old.Map!.Combat.RepopPlayer(old)));
        await CastAsync(healer, SpellCastTargets.ForUnit(new ObjectGuid(guid)));
        await target.ReadUntilAsync(WorldOpcode.SmsgResurrectRequest);
        ulong caster = await host.PlayerStateAsync("Resquitheal", p => p.Guid.Value);
        await target.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(caster, true));
        await target.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
        await target.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await target.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await host.OnWorldAsync(() =>
        {
            Assert.Null(PlayerResurrection.GetRequest(old));
            Assert.False(host.WorldServices.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(old));
        });
        await target.LoginAsync(guid);
        Assert.False(await host.PlayerStateAsync("Resquitdead", p => p.IsAlive));
        await target.SendAsync(WorldOpcode.CmsgResurrectResponse, Response(caster, true));
        await target.SendAsync(WorldOpcode.MsgMoveTeleportAck, TeleportAck(guid));
        await target.CollectAsync();
        Assert.False(await host.PlayerStateAsync("Resquitdead", p => p.IsAlive));
        Assert.Null(await host.PlayerStateAsync("Resquitdead", PlayerResurrection.GetRequest));
    }

    private static async Task CastAsync(WorldTestClient healer, SpellCastTargets targets)
    {
        var payload = new PacketWriter();
        payload.WriteUInt32(Resurrection);
        targets.Write(payload);
        // CMSG has separate unit and corpse GUIDs (read order follows cmangos + wow_messages).
        // SpellCastTargets.Write is the source SMSG writer, which deliberately emits one GUID.
        if ((targets.Mask & SpellCastTargetFlags.Unit) != 0
            && (targets.Mask & (SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.CorpseEnemy)) != 0)
        {
            payload.WritePackedGuid(targets.Corpse.Value);
        }
        await healer.SendAsync(WorldOpcode.CmsgCastSpell, payload.ToArray());
        await healer.ReadUntilAsync(WorldOpcode.SmsgCastResult);
    }

    private static byte[] Response(ulong caster, bool accept)
    {
        var writer = new PacketWriter();
        writer.WriteUInt64(caster);
        writer.WriteByte(accept ? (byte)1 : (byte)0);
        return writer.ToArray();
    }

    private static byte[] TeleportAck(ulong guid)
    {
        var writer = new PacketWriter();
        writer.WriteUInt64(guid);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        return writer.ToArray();
    }

    private static void AssertOffer(byte[] payload, ulong caster)
    {
        var reader = new PacketReader(payload);
        Assert.Equal(caster, reader.ReadUInt64());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(string.Empty, reader.ReadCString());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal(0, reader.Remaining);
    }

    private static Task<ulong> ConfigureAsync(WorldTestHost host, string healer, string target) => host.OnWorldAsync(() =>
    {
        Player caster = host.World.FindOnlinePlayer(healer)!;
        Player victim = host.World.FindOnlinePlayer(target)!;
        caster.Relocate(victim.X + 4, victim.Y, victim.Z, 0, host.World.NowMs);
        SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
        feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
        {
            Id = Resurrection, Name = "Test Resurrection", AttributesEx2 = SpellAttributesEx2.AllowDeadTarget,
            RangeIndex = 4, Range = new SpellRange(0, 30),
            Effects = [new SpellEffectInfo
            {
                Effect = SpellEffectName.Resurrect, BasePoints = 24, BaseDice = 1, DieSides = 1,
                TargetA = SpellImplicitTarget.UnitFriend,
            }],
        }], [], []);
        feature.Spellbook.LearnSpell(caster, Resurrection);
        victim.MaxHealth = 1000;
        victim.SetUInt32(UpdateFields.UnitFieldMaxpower1, 800);
        victim.Map!.Combat.KillPlayer(victim);
        return victim.Guid.Value;
    });
}
