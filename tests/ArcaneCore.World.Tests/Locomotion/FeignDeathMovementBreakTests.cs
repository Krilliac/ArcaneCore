using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>
/// Auras whose Spell.dbc AuraInterruptFlags carry MOVING (0x8) or TURNING (0x10) end when the player's client moves or turns, and
/// an NPC interaction ends the INTERACTING_CANCELS (0x400) auras without refusing the interaction. Driven through the real
/// opcode path (loopback client -> session -> MovementHandlers / QuestNpcInteractionHandlers) against a real spell system.
/// vmangos: Player::SetPosition (Player.cpp:5949-5958) -> Unit::HandleInterruptsOnMovement (Unit.cpp:10131-10140): a changed
/// position or MOVEFLAG_MASK_XZ removes MOVING|TURNING auras, a changed orientation alone removes TURNING auras;
/// HandleGossipHelloOpcode (NPCHandler.cpp:354-355) removes INTERACTING_CANCELS auras after GetNPCIfCanInteractWith succeeds.
/// Feign Death (Spell.dbc 5384) has AuraInterruptFlags 0x3C3C. The spells here are synthetic rows with exactly that flag word.
/// </summary>
public sealed class FeignDeathMovementBreakTests
{
    private const uint FeignSpell = 930701;
    private const uint TurningOnlySpell = 930702;
    private const uint MovingOnlySpell = 930703;

    /// <summary>Spell.dbc 5384 (Feign Death) AuraInterruptFlags: Action|Moving|Turning|Anim|Interacting|Looting|Attacking|ItemUse.</summary>
    private const SpellAuraInterruptFlags FeignInterrupt = (SpellAuraInterruptFlags)0x3C3C;

    private const uint BarrierFallTime = 4242;

    public enum Change { Position, Facing, None }

    private static SpellInfo AuraSpell(uint id, AuraType type, SpellAuraInterruptFlags interrupt) => new()
    {
        Id = id, Name = $"Synthetic aura {id}", RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Duration = new SpellDuration(-1, 0, -1),
        AuraInterruptFlags = interrupt,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = type, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
    };

    private static SpellSystem SpellsOf(WorldTestHost host) => host.WorldServices.GetRequiredService<SpellFeature>().System;

    /// <summary>Install the synthetic spells and cast <paramref name="spell"/> on the player (world thread, triggered: no cast time, no cost).</summary>
    private static Task CastAsync(WorldTestHost host, string name, SpellInfo spell) => host.OnWorldAsync(() =>
    {
        SpellSystem spells = SpellsOf(host);
        spells.Store = new SpellStore([.. spells.Store.All.Where(s => s.Id != spell.Id), spell], [], []);
        Player player = host.World.FindOnlinePlayer(name)!;
        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, spell.Id, SpellCastTargets.ForSelf(), triggered: true));
    });

    private static Task<bool> HasAuraAsync(WorldTestHost host, string name, uint spellId) => host.OnWorldAsync(() =>
        SpellsOf(host).GetAuras(host.World.FindOnlinePlayer(name)!).Any(h => !h.IsRemoved && h.Spell.Id == spellId));

    private static Task<bool> FeigningAsync(WorldTestHost host, string name)
        => host.OnWorldAsync(() => SpellsOf(host).IsFeigningDeath(host.World.FindOnlinePlayer(name)!));

    private static async Task<WorldTestClient> FeignedPlayerAsync(WorldTestHost host, string account, string name, SpellAuraInterruptFlags interrupt = FeignInterrupt)
    {
        WorldTestClient client = await host.EnterWorldAsync(account, name);
        await client.CollectAsync();
        await CastAsync(host, name, AuraSpell(FeignSpell, AuraType.FeignDeath, interrupt));
        Assert.True(await FeigningAsync(host, name), "setup: the player must be feigning death before it moves");
        return client;
    }

    private static Task<MovementInfo> StoredAsync(WorldTestHost host, string name) => host.PlayerStateAsync(name, p => p.Movement);

    private static byte[] Encode(MovementInfo info)
    {
        var writer = new PacketWriter(64);
        info.Write(writer);
        return writer.AsMemory().ToArray();
    }

    /// <summary>
    /// Send one movement packet and wait until the server has stored it. The block carries a unique FallTime that the handler stores
    /// verbatim, so "stored" also holds for packets that change neither position nor facing.
    /// </summary>
    private static async Task SendAndSettleAsync(WorldTestHost host, WorldTestClient client, string name, WorldOpcode opcode, MovementInfo block)
    {
        block.FallTime = BarrierFallTime;
        await client.SendAsync(opcode, Encode(block));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.Movement.FallTime == BarrierFallTime, "the movement packet to be stored");
    }

    // --- 1: each movement kind breaks Feign Death ----------------------------------------------------------------------------

    public static TheoryData<WorldOpcode, MovementFlags, Change> BreakingMoves => new()
    {
        { WorldOpcode.MsgMoveStartForward, MovementFlags.Forward, Change.Position },
        { WorldOpcode.MsgMoveHeartbeat, MovementFlags.None, Change.Position },        // position changed, no moving flags
        { WorldOpcode.MsgMoveHeartbeat, MovementFlags.Forward, Change.None },         // same position, MOVEFLAG_MASK_XZ set
        { WorldOpcode.MsgMoveStartStrafeLeft, MovementFlags.StrafeLeft, Change.Position },
        { WorldOpcode.MsgMoveJump, MovementFlags.Jumping, Change.Position },
        { WorldOpcode.MsgMoveFallLand, MovementFlags.None, Change.Position },
        { WorldOpcode.MsgMoveSetFacing, MovementFlags.None, Change.Facing },          // TURNING alone is in Feign Death's flag word
    };

    private static MovementInfo Moved(MovementInfo baseline, MovementFlags flags, Change change, uint time)
    {
        MovementInfo block = baseline;
        block.Flags = flags;
        block.Time = time;
        if (change == Change.Position)
        {
            block.X += 1.0f;
        }
        else if (change == Change.Facing)
        {
            block.Orientation += 0.5f;
        }

        if ((flags & MovementFlags.Jumping) != 0)
        {
            (block.JumpZSpeed, block.JumpCosAngle, block.JumpSinAngle, block.JumpXySpeed) = (7.96f, 1.0f, 0.0f, 0.0f);
        }

        return block;
    }

    [Theory]
    [MemberData(nameof(BreakingMoves))]
    public async Task FeigningPlayer_LosesFeignDeath_WhenItsClientMoves(WorldOpcode opcode, MovementFlags flags, Change change)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await FeignedPlayerAsync(host, "FEIGNMOVEA", "Feignmovea");
        MovementInfo baseline = await StoredAsync(host, "Feignmovea");

        await SendAndSettleAsync(host, client, "Feignmovea", opcode, Moved(baseline, flags, change, baseline.Time + 1000));

        Assert.False(await FeigningAsync(host, "Feignmovea"), $"{opcode} (flags {flags}, change {change}) must end Feign Death");
        Assert.False(await HasAuraAsync(host, "Feignmovea", FeignSpell), $"the Feign Death aura must be gone after {opcode}");
    }

    // --- 2: the client clock has nothing to do with it ---------------------------------------------------------------------------

    [Theory]
    [InlineData(1u)]          // the block's client time predates the cast (and anything the server stored)
    [InlineData(5_000_000u)]  // a client that has been up for ~83 minutes
    public async Task FeigningPlayer_LosesFeignDeath_WhateverTheClientClockReads(uint clientTime)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await FeignedPlayerAsync(host, "FEIGNMOVEB", "Feignmoveb");
        MovementInfo baseline = await StoredAsync(host, "Feignmoveb");

        await SendAndSettleAsync(host, client, "Feignmoveb", WorldOpcode.MsgMoveStartForward,
            Moved(baseline, MovementFlags.Forward, Change.Position, clientTime));

        Assert.False(await FeigningAsync(host, "Feignmoveb"), $"a move stamped {clientTime} ms must end Feign Death");
    }

    // --- 3: negative control ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FeigningPlayer_KeepsFeignDeath_OnAStationaryHeartbeat()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await FeignedPlayerAsync(host, "FEIGNMOVEC", "Feignmovec");
        MovementInfo baseline = await StoredAsync(host, "Feignmovec");

        // Identical position and orientation, no moving flags: only the client clock moves.
        await SendAndSettleAsync(host, client, "Feignmovec", WorldOpcode.MsgMoveHeartbeat, Moved(baseline, MovementFlags.None, Change.None, baseline.Time + 1000));

        MovementInfo after = await StoredAsync(host, "Feignmovec");
        Assert.Equal((baseline.X, baseline.Y, baseline.Z, baseline.Orientation), (after.X, after.Y, after.Z, after.Orientation)); // premise: nothing moved
        Assert.True(await FeigningAsync(host, "Feignmovec"), "a heartbeat that changes neither position nor facing must not end Feign Death");
    }

    // --- 4: TURNING versus MOVING (vmangos HandleInterruptsOnMovement else-branch) --------------------------------------------------

    [Theory]
    [InlineData(SpellAuraInterruptFlags.Turning, Change.Facing, true)]    // facing alone removes a TURNING aura
    [InlineData(SpellAuraInterruptFlags.Moving, Change.Facing, false)]    // ... but not a MOVING-only aura
    [InlineData(SpellAuraInterruptFlags.Moving, Change.Position, true)]
    [InlineData(SpellAuraInterruptFlags.Turning, Change.Position, true)]  // a position change removes MOVING|TURNING
    public async Task Aura_WithTurningOrMovingOnly_EndsAccordingToWhatTheClientChanged(SpellAuraInterruptFlags flag, Change change, bool removed)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("FEIGNMOVED", "Feignmoved");
        await client.CollectAsync();
        uint spell = flag == SpellAuraInterruptFlags.Turning ? TurningOnlySpell : MovingOnlySpell;
        await CastAsync(host, "Feignmoved", AuraSpell(spell, AuraType.Dummy, flag));
        Assert.True(await HasAuraAsync(host, "Feignmoved", spell), "setup: the aura must be applied");
        MovementInfo baseline = await StoredAsync(host, "Feignmoved");

        WorldOpcode opcode = change == Change.Facing ? WorldOpcode.MsgMoveSetFacing : WorldOpcode.MsgMoveHeartbeat;
        await SendAndSettleAsync(host, client, "Feignmoved", opcode, Moved(baseline, MovementFlags.None, change, baseline.Time + 1000));

        Assert.True(removed != await HasAuraAsync(host, "Feignmoved", spell), $"{flag} aura after a {change} change: expected {(removed ? "removed" : "kept")}");
    }

    // --- 5: an NPC interaction breaks Feign Death and proceeds ------------------------------------------------------------------------

    [Fact]
    public async Task FeigningPlayer_LosesFeignDeath_WhenItGreetsAnNpc_AndTheGossipStillGoesOut()
    {
        var fixture = new QuestInteractionFixture();
        QuestInteractionTestServices.Current.Value = fixture;
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally { QuestInteractionTestServices.Current.Value = null; }

        await using (host)
        {
            await using WorldTestClient client = await FeignedPlayerAsync(host, "FEIGNGOSSIP", "Feigngossip");
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Feigngossip")!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "the NPC to become visible");
            await host.OnWorldAsync(() => ((Creature)host.World.FindOnlinePlayer("Feigngossip")!.Map!.FindObject(QuestInteractionFixture.Guid)!).NpcFlags
                = (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver));
            Assert.True(await FeigningAsync(host, "Feigngossip"), "setup: still feigning before the greeting");
            await client.CollectAsync();

            var hello = new PacketWriter(8);
            hello.WriteUInt64(QuestInteractionFixture.Guid.Value);
            await client.SendAsync(WorldOpcode.CmsgGossipHello, hello.ToArray());

            // The interaction is not refused: the menu goes out (retail breaks the feign and lets the interaction proceed).
            byte[] menu = await client.ReadUntilAsync(WorldOpcode.SmsgGossipMessage);
            Assert.Equal(QuestInteractionFixture.Guid.Value, new PacketReader(menu).ReadUInt64());
            Assert.False(await FeigningAsync(host, "Feigngossip"), "CMSG_GOSSIP_HELLO must end Feign Death (AURA_INTERRUPT_INTERACTING_CANCELS)");
            Assert.False(await HasAuraAsync(host, "Feigngossip", FeignSpell));
        }
    }

    // --- 5b: a near teleport counts as positionChanged (vmangos Unit.cpp:9855 -> Player::SetPosition(..., teleport = true)) -----------

    [Fact]
    public async Task FeigningPlayer_LosesFeignDeath_WhenANearTeleportLands()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await FeignedPlayerAsync(host, "FEIGNPORT", "Feignport");

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Feignport")!;
            ArcaneCore.Game.Teleport.TeleportService teleports = host.WorldServices.GetRequiredService<ArcaneCore.World.Teleport.TeleportFeature>().Teleports;
            Assert.True(teleports.TeleportTo(player, player.Map!.MapId, player.X + 5, player.Y, player.Z, player.Orientation));
            Assert.True(teleports.HandleTeleportAck(player, player.Guid.Value));
        });

        Assert.False(await FeigningAsync(host, "Feignport"), "a landed near teleport must end Feign Death");
    }

    // --- 6: regression guard (passes today): chat is allowed while feigning ---------------------------------------------------------------

    [Fact]
    public async Task FeigningPlayer_CanStillChat()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient speaker = await FeignedPlayerAsync(host, "FEIGNCHATA", "Feignchata");
        await using WorldTestClient listener = await host.EnterWorldAsync("FEIGNCHATB", "Feignchatb");
        await listener.CollectAsync();

        await speaker.SendChatAsync(ChatType.Say, Language.Common, "playing dead");

        Assert.Equal("playing dead", (await listener.ReadChatAsync()).Text);
        Assert.True(await FeigningAsync(host, "Feignchata"), "chat must not end Feign Death");
    }
}
