using ArcaneCore.Game.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Party;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>P4 S4: a Need roll the bot lost to a groupmate who could not use the item reaches the trade ledger whether or not the social memory is on.</summary>
public sealed class PlayerbotPartyNinjaLedgerTests
{
    private static byte[] StartRoll(ulong source, uint slot, uint item)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt64(source);
        writer.WriteUInt32(slot);
        writer.WriteUInt32(item);
        return writer.ToArray();
    }

    private static byte[] RollWon(ulong source, uint slot, uint item, ulong winner)
    {
        var writer = new PacketWriter(40);
        writer.WriteUInt64(source);
        writer.WriteUInt32(slot);
        writer.WriteUInt32(item);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt64(winner);
        writer.WriteByte(50);
        writer.WriteByte((byte)RollVote.Need);
        return writer.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANinjaLootRoll_IsInTheLedger_WithTheMemoryOff_AsWellAsOn(bool memoryOn)
    {
        const uint noSuchItem = 4_000_000_001; // no template: the winner cannot use it
        const ulong source = 0xF110_0000_0000_0001UL;
        await using WorldTestHost host = PartyTestHost.Start();
        WorldSession bot = await PartyTestHost.EnterManagedAsync(host, "NINJALEDGERA", "Ninjaledgera");
        WorldSession thief = await PartyTestHost.EnterManagedAsync(host, "NINJALEDGERB", "Ninjaledgerb");
        try
        {
            var botId = Guid.NewGuid();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ledger = new PlayerbotTradeLedger(new PlayerbotSocialTradingOptions(), new PlayerbotWarModeOptions(), () => now);
            var memory = new PlayerbotSocialMemory(new PlayerbotSocialMemoryOptions { Enabled = memoryOn }, () => now);
            var recorder = new PlayerbotSocialRecorder(memory, NullLogger.Instance, ledger);
            ulong thiefGuid = thief.Player!.Guid.Value;
            await host.OnWorldAsync(() =>
            {
                recorder.Track(botId, (int)bot.Player!.Guid.Low);
                var ai = new PlayerbotPartyAI(bot, PartyTestHost.PlayerbotOptionsOf(host), botId: botId)
                {
                    Social = recorder,
                    GroupLootVote = (_, _) => RollVote.Need,
                };
                bot.Send(WorldOpcode.SmsgLootStartRoll, StartRoll(source, 0, noSuchItem));
                bot.Send(WorldOpcode.SmsgLootRollWon, RollWon(source, 0, noSuchItem, thiefGuid));
                ai.Intake(bot.Player!);
                return true;
            });

            Assert.True(ledger.IsNinja(botId, thiefGuid));
            Assert.Equal(memoryOn, memory.TryGet(botId, thiefGuid, out _));
        }
        finally
        {
            bot.Kick();
            await bot.ManagedClosed;
            thief.Kick();
            await thief.ManagedClosed;
        }
    }
}
