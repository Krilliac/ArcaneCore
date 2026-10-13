using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Chat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Chat;

/// <summary>
/// Text emotes with the client's EmotesText.dbc and Emotes.dbc (vmangos HandleTextEmoteOpcode, ChatHandler.cpp:711-753,
/// and Unit::HandleEmote, Unit.cpp:1861-1872), a creature as the target (EmoteChatBuilder names it; CreatureAI::ReceiveEmote
/// hears it). The catalog rows are synthetic: text emote 101 (WAVE) plays the one-shot emote 3, 86 (SLEEP) plays the state
/// emote 12 which HandleTextEmoteOpcode does not animate, 9 (ANGRY) plays the state emote 14 (synthetic type 1).
/// </summary>
public sealed class TextEmoteDbcTests
{
    private static EmoteCatalog Catalog() => new(
        [new TextEmoteRow(101, 3), new TextEmoteRow(86, 12), new TextEmoteRow(9, 14), new TextEmoteRow(10, 15), new TextEmoteRow(34, 0)],
        [new EmoteRow(3, 0, 0, 0), new EmoteRow(12, 0, 1, 3), new EmoteRow(14, 0, 1, 0), new EmoteRow(15, 0, 1, 0)]);

    private static byte[] TextEmote(uint textEmote, uint number, ulong target)
    {
        var w = new PacketWriter(16);
        w.WriteUInt32(textEmote);
        w.WriteUInt32(number);
        w.WriteUInt64(target);
        return w.ToArray();
    }

    private static async Task<(WorldTestHost Host, WorldTestClient Waver, WorldTestClient Watcher)> StartAsync(bool withCatalog, Action<IServiceCollection>? more = null)
    {
        WorldTestHost host = WorldTestHost.Start(configureServices: more);
        if (withCatalog)
        {
            host.WorldServices.GetRequiredService<ChatFeature>().Emotes = Catalog();
        }

        WorldTestClient waver = await host.EnterWorldAsync("WAVER", "Waver");
        WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await waver.CollectAsync();
        await watcher.CollectAsync();
        return (host, waver, watcher);
    }

    [Fact]
    public async Task AKnownOneShotTextEmote_PlaysItsAnimation_ForTheEmoterAndTheWatchers()
    {
        (WorldTestHost host, WorldTestClient waver, WorldTestClient watcher) = await StartAsync(withCatalog: true);
        await using (host)
        {
            await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(101, 0, 0));

            byte[] wave = [3, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0]; // SMSG_EMOTE: emote 3, the waver's guid
            Assert.Equal(wave, await waver.ReadUntilAsync(WorldOpcode.SmsgEmote));
            Assert.Equal(wave, await watcher.ReadUntilAsync(WorldOpcode.SmsgEmote));
            Assert.Equal(101u, BinaryPrimitives.ReadUInt32LittleEndian((await watcher.ReadUntilAsync(WorldOpcode.SmsgTextEmote)).AsSpan(8)));
        }
    }

    [Fact]
    public async Task AStateEmote_SetsTheEmoteState_AndSleepSitKneelAreNotAnimated()
    {
        (WorldTestHost host, WorldTestClient waver, WorldTestClient watcher) = await StartAsync(withCatalog: true);
        await using (host)
        {
            await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(86, 0, 0)); // sleep: no animation from the server
            await watcher.ReadUntilAsync(WorldOpcode.SmsgTextEmote);
            Assert.Equal(0u, await host.PlayerStateAsync("Waver", p => p.GetUInt32(UpdateFields.UnitNpcEmotestate)));

            await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(9, 0, 0)); // a state emote
            await watcher.ReadUntilAsync(WorldOpcode.SmsgTextEmote);
            Assert.Equal(14u, await host.PlayerStateAsync("Waver", p => p.GetUInt32(UpdateFields.UnitNpcEmotestate)));
            Assert.DoesNotContain(await waver.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgEmote);
        }
    }

    [Fact]
    public async Task AnUnknownTextEmote_IsDropped_WithTheFiles_ButAnnouncedWithout()
    {
        (WorldTestHost host, WorldTestClient waver, WorldTestClient watcher) = await StartAsync(withCatalog: true);
        await using (host)
        {
            await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(999, 0, 0));
            Assert.DoesNotContain(await watcher.CollectAsync(), p => p.Opcode is WorldOpcode.SmsgTextEmote or WorldOpcode.SmsgEmote);
        }

        (host, waver, watcher) = await StartAsync(withCatalog: false);
        await using (host)
        {
            await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(999, 0, 0));
            await watcher.ReadUntilAsync(WorldOpcode.SmsgTextEmote);
            Assert.DoesNotContain(await waver.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgEmote);
        }
    }

    [Fact]
    public async Task ACreatureTarget_IsNamed_AndItsReceiversHearTheEmote()
    {
        var heard = new EmoteRecorder();
        (WorldTestHost host, WorldTestClient waver, WorldTestClient watcher) = await StartAsync(
            withCatalog: true, more: services => services.AddSingleton<IWorldFeature>(heard));
        await using (host)
        {
            Player player = await host.PlayerAsync("Waver");
            var template = new CreatureTemplate { Entry = 990500, Name = "Emote Dummy", MinLevel = 5, MaxLevel = 5, MinLevelHealth = 60, MaxLevelHealth = 60, Faction = 35 };
            var spawn = new CreatureSpawn { Guid = 990500, Entry = 990500, MapId = 0, X = player.X + 2, Y = player.Y, Z = player.Z };
            var creature = new Creature(990500, template, spawn, new CreatureContent([template], [spawn], [], [], []), new Random(1));
            await host.OnWorldAsync(() => host.World.GetMap(0).AddObject(creature));
            await waver.CollectAsync();

            await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(101, 0, creature.Guid.Value));

            byte[] packet = await waver.ReadUntilAsync(WorldOpcode.SmsgTextEmote);
            var reader = new PacketReader(packet.AsSpan(16).ToArray());
            Assert.Equal((uint)"Emote Dummy".Length + 1, reader.ReadUInt32());
            Assert.Equal("Emote Dummy", reader.ReadCString());
            await WorldTestHost.WaitForAsync(() => heard.Heard.Count == 1, "the creature to hear the emote");
            Assert.Equal((creature.Guid, "Waver", 101u), heard.Heard.Single());
        }
    }

    [Theory]
    [InlineData(true)]  // CMSG_EMOTE wave (ChatHandler.cpp:674-675)
    [InlineData(false)] // CMSG_TEXT_EMOTE wave (ChatHandler.cpp:736-737)
    public async Task AnEmote_EndsTheAurasAnAnimationCancels(bool plainEmote)
    {
        const uint Pretend = 993900; // a self aura that ends on an animation, like Feign Death
        (WorldTestHost host, WorldTestClient waver, WorldTestClient watcher) = await StartAsync(withCatalog: true);
        await using (host)
        {
            await host.OnWorldAsync(() =>
            {
                SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
                feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
                {
                    Id = Pretend, Duration = new SpellDuration(60_000, 0, 60_000),
                    AuraInterruptFlags = SpellAuraInterruptFlags.AnimCancels,
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy, TargetA = SpellImplicitTarget.UnitCaster }],
                }], [], []);
                Player player = host.World.FindOnlinePlayer("Waver")!;
                feature.System.CastSpell(player, Pretend, SpellCastTargets.ForUnit(player.Guid), triggered: true);
            });
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(host.World.FindOnlinePlayer("Waver")!, Pretend), "the aura");

            if (plainEmote)
            {
                await waver.SendAsync(WorldOpcode.CmsgEmote, [3, 0, 0, 0]);
            }
            else
            {
                await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(101, 0, 0));
            }

            await watcher.ReadUntilAsync(WorldOpcode.SmsgEmote);
            Assert.False(await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(host.World.FindOnlinePlayer("Waver")!, Pretend)));
        }
    }

    private static uint _clock = 5000;

    private static async Task<byte[]> BlockAsync(WorldTestHost host, MovementFlags flags, float dx = 0, float turn = 0)
    {
        (float x, float y, float z, float o) = await host.PlayerStateAsync("Waver", p => (p.X, p.Y, p.Z, p.Orientation));
        var writer = new PacketWriter(48);
        new MovementInfo { Flags = flags, Time = _clock += 100, X = x + dx, Y = y, Z = z, Orientation = o + turn }.Write(writer);
        return writer.ToArray();
    }

    private static Task<uint> EmoteState(WorldTestHost host) => host.PlayerStateAsync("Waver", p => p.GetUInt32(UpdateFields.UnitNpcEmotestate));

    /// <summary>The UNIT_NPC_EMOTESTATE values in the Values updates (plain or compressed) among <paramref name="packets"/>.</summary>
    private static List<uint> EmoteStatesIn(IEnumerable<(WorldOpcode Opcode, byte[] Payload)> packets)
    {
        var states = new List<uint>();
        foreach ((WorldOpcode opcode, byte[] payload) in packets)
        {
            if (opcode is not (WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgCompressedUpdateObject))
            {
                continue;
            }

            var reader = new PacketReader(opcode == WorldOpcode.SmsgCompressedUpdateObject ? WorldTestClient.Inflate(payload) : payload);
            uint blocks = reader.ReadUInt32();
            reader.ReadByte();
            for (uint i = 0; i < blocks; i++)
            {
                if (reader.ReadByte() != (byte)ObjectUpdateType.Values)
                {
                    break; // a create block: not what this reads
                }

                reader.ReadPackedGuid();
                int words = reader.ReadByte();
                uint[] mask = new uint[words];
                for (int w = 0; w < words; w++)
                {
                    mask[w] = reader.ReadUInt32();
                }

                for (int index = 0; index < words * 32; index++)
                {
                    if ((mask[index >> 5] & (1u << (index & 31))) != 0)
                    {
                        uint value = reader.ReadUInt32();
                        if (index == UpdateFields.UnitNpcEmotestate)
                        {
                            states.Add(value);
                        }
                    }
                }
            }
        }

        return states;
    }

    /// <summary>Every packet up to and including the Values update that carries UNIT_NPC_EMOTESTATE = 0 (read timeout per packet), then what follows it.</summary>
    private static async Task<List<(WorldOpcode Opcode, byte[] Payload)>> ReadUntilStateEndsAsync(WorldTestClient client)
    {
        var seen = new List<(WorldOpcode Opcode, byte[] Payload)>();
        while (!EmoteStatesIn(seen).Contains(0u))
        {
            seen.Add(await client.ReadAsync());
        }

        seen.AddRange(await client.CollectAsync());
        return seen;
    }

    private static async Task DanceAsync(WorldTestHost host, WorldTestClient waver, WorldTestClient watcher)
    {
        await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(9, 0, 0)); // a state emote
        await watcher.ReadUntilAsync(WorldOpcode.SmsgTextEmote);
        Assert.Equal(14u, await EmoteState(host));
        await watcher.CollectAsync();
    }

    [Theory]
    [InlineData(WorldOpcode.MsgMoveHeartbeat, MovementFlags.None, 1.0f)]      // the position changed (Player::SetPosition positionChanged)
    [InlineData(WorldOpcode.MsgMoveStartForward, MovementFlags.Forward, 0f)]  // MOVEFLAG_MASK_XZ on the block, in place
    [InlineData(WorldOpcode.MsgMoveStartStrafeRight, MovementFlags.StrafeRight, 0f)]
    public async Task AStateEmote_EndsWhenThePlayerMoves_AndObserversAreToldTheStateIsZero(WorldOpcode opcode, MovementFlags flags, float dx)
    {
        (WorldTestHost host, WorldTestClient waver, WorldTestClient watcher) = await StartAsync(withCatalog: true);
        await using (host)
        {
            await DanceAsync(host, waver, watcher);

            await waver.SendAsync(opcode, await BlockAsync(host, flags, dx));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Waver")!.GetUInt32(UpdateFields.UnitNpcEmotestate) == 0, "the emote state to end");

            List<(WorldOpcode Opcode, byte[] Payload)> seen = await ReadUntilStateEndsAsync(watcher);
            Assert.Contains(seen, p => p.Opcode == opcode);
            Assert.Contains(0u, EmoteStatesIn(seen)); // the update the watcher got carries UNIT_NPC_EMOTESTATE = 0
        }
    }

    [Theory]
    [InlineData(MovementFlags.None, 0f, 0f)]                // a stop in place
    [InlineData(MovementFlags.PitchUp, 0f, 0f)]             // pitch is in MASK_MOVING but not MASK_XZ
    [InlineData(MovementFlags.TurnLeft, 0f, 1f)]            // turning in place: the orientation alone does not end it
    public async Task AStateEmote_SurvivesAStopAPitchAndATurnInPlace(MovementFlags flags, float dx, float turn)
    {
        (WorldTestHost host, WorldTestClient waver, WorldTestClient watcher) = await StartAsync(withCatalog: true);
        await using (host)
        {
            await DanceAsync(host, waver, watcher);

            await waver.SendAsync(WorldOpcode.MsgMoveHeartbeat, await BlockAsync(host, flags, dx, turn));
            Assert.Contains(await watcher.CollectFromAsync(WorldOpcode.MsgMoveHeartbeat), p => p.Opcode == WorldOpcode.MsgMoveHeartbeat); // handled and relayed before the check
            Assert.Equal(14u, await EmoteState(host));
        }
    }

    [Fact]
    public async Task AManagedBotsMovement_EndsAStateEmote_ThroughTheServerAppliedPath()
    {
        (WorldTestHost host, WorldTestClient waver, WorldTestClient watcher) = await StartAsync(withCatalog: true);
        await using (host)
        {
            await DanceAsync(host, waver, watcher);

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Waver")!;
                var movement = new MovementInfo { Flags = MovementFlags.Forward, Time = 1, X = player.X, Y = player.Y, Z = player.Z, Orientation = player.Orientation };
                return ArcaneCore.World.Handlers.MovementHandlers.MoveManagedBot((ArcaneCore.World.Net.WorldSession)player.Session, player, WorldOpcode.MsgMoveStartForward, movement);
            });

            Assert.Equal(0u, await EmoteState(host));
            Assert.Contains(0u, EmoteStatesIn(await ReadUntilStateEndsAsync(watcher)));
        }
    }

    [Fact]
    public async Task ANearTeleport_EndsAStateEmote_WhenItArrives()
    {
        (WorldTestHost host, WorldTestClient waver, WorldTestClient watcher) = await StartAsync(withCatalog: true);
        await using (host)
        {
            await DanceAsync(host, waver, watcher);
            await waver.CollectAsync();

            ulong guid = await host.PlayerStateAsync("Waver", p => p.Guid.Value);
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Waver")!;
                return host.WorldServices.GetRequiredService<TeleportFeature>().Teleports.TeleportTo(player, player.MapId, player.X + 5, player.Y, player.Z, player.Orientation);
            });
            await waver.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck);
            Assert.Equal(14u, await EmoteState(host)); // still the same position until the client acknowledges (Unit.cpp:9855 runs on the ack)

            var ack = new PacketWriter();
            ack.WriteUInt64(guid);
            ack.WriteUInt32(0);
            ack.WriteUInt32(0);
            await waver.SendAsync(WorldOpcode.MsgMoveTeleportAck, ack.ToArray());
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Waver")!.GetUInt32(UpdateFields.UnitNpcEmotestate) == 0, "the emote state to end");
            Assert.Contains(0u, EmoteStatesIn(await ReadUntilStateEndsAsync(watcher)));
        }
    }

    [Fact]
    public async Task AnotherStateTextEmote_ReplacesTheEmoteState()
    {
        (WorldTestHost host, WorldTestClient waver, WorldTestClient watcher) = await StartAsync(withCatalog: true);
        await using (host)
        {
            await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(9, 0, 0));
            await watcher.ReadUntilAsync(WorldOpcode.SmsgTextEmote);
            Assert.Equal(14u, await EmoteState(host));

            await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(10, 0, 0));
            await watcher.ReadUntilAsync(WorldOpcode.SmsgTextEmote);
            Assert.Equal(15u, await EmoteState(host));
        }
    }

    private sealed class EmoteRecorder : IWorldFeature, ITextEmoteReceiver
    {
        public List<(ObjectGuid Creature, string Player, uint TextEmote)> Heard { get; } = [];

        public void Attach(WorldRuntime world)
        {
        }

        public void ReceiveEmote(Creature creature, Player player, uint textEmote)
        {
            lock (Heard)
            {
                Heard.Add((creature.Guid, player.Name, textEmote));
            }
        }
    }
}
