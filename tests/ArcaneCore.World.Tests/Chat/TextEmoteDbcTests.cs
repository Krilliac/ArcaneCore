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
        [new TextEmoteRow(101, 3), new TextEmoteRow(86, 12), new TextEmoteRow(9, 14), new TextEmoteRow(34, 0)],
        [new EmoteRow(3, 0, 0, 0), new EmoteRow(12, 0, 1, 3), new EmoteRow(14, 0, 1, 0)]);

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
