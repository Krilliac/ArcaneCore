using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Chat;

/// <summary>
/// The CMSG_MESSAGECHAT gates in vmangos order (ChatHandler.cpp HandleChatMessageOpcode): type range,
/// addon switch, language knowledge, SPELL_AURA_MOD_LANGUAGE, mute and flood control, and the mute
/// check of the emote opcodes. The clock is fixed so the one-second flood window is deterministic.
/// </summary>
public sealed class ChatGateTests
{
    private const uint LanguageAuraSpell = 930501;

    private static WorldTestHost StartHost(OffsetClock clock, ChatMessageSpy? spy = null) => WorldTestHost.Start(configureServices: services =>
    {
        services.AddSingleton<TimeProvider>(clock);
        if (spy is not null)
        {
            services.AddSingleton<IChatMessageHandler>(spy);
        }
    });

    [Fact]
    public async Task ModLanguageAura_OverridesTheSpokenLanguage_ButNotForAGameMaster()
    {
        await using WorldTestHost host = StartHost(new OffsetClock());
        await using WorldTestClient caster = await host.EnterWorldAsync("CASTER", "Caster");
        await using WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener");
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Moderator);
        await caster.CollectAsync();
        await listener.CollectAsync();
        await gm.CollectAsync();

        await host.OnWorldAsync(() =>
        {
            foreach (string name in new[] { "Caster", "Staff" })
            {
                Player player = host.World.FindOnlinePlayer(name)!;
                SpellFeature spells = ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();
                var spell = new SpellInfo
                {
                    Id = LanguageAuraSpell, RangeIndex = SpellConstants.RangeIndexSelfOnly,
                    Effects = [new SpellEffectInfo
                    {
                        Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModLanguage, MiscValue = (int)Language.Demonic,
                        TargetA = SpellImplicitTarget.UnitCaster,
                    }, new(), new()],
                };
                spells.System.Store = new SpellStore([.. spells.System.Store.All.Where(s => s.Id != LanguageAuraSpell), spell], [], []);
                Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, LanguageAuraSpell, SpellCastTargets.ForSelf(), triggered: true));
            }
        });

        // vmangos ChatHandler.cpp: "overwrite it by SPELL_AURA_MOD_LANGUAGE auras".
        await caster.SendChatAsync(ChatType.Say, Language.Common, "Hello");
        Assert.Equal(Language.Demonic, (await listener.ReadChatAsync()).Language);

        // The override sits in the non-GM branch: a game master in GM mode stays Universal.
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Staff")!.SetGameMaster(true));
        await gm.SendChatAsync(ChatType.Say, Language.Common, "Staff speaking");
        Assert.Equal(Language.Universal, (await listener.ReadChatAsync()).Language);
    }

    [Fact]
    public async Task FloodControl_MutesAfterTheCountIsReached_AndTheNoticeUsesTheReferenceTimeFormat()
    {
        var clock = new OffsetClock();
        await using WorldTestHost host = StartHost(clock);
        await using WorldTestClient spammer = await host.EnterWorldAsync("SPAMMER", "Spammer");
        await using WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener");
        await spammer.CollectAsync();
        await listener.CollectAsync();

        // vmangos MasterPlayer::UpdateSpeakTime: the first message opens the window, messages 2..11
        // count (the 10th counted one, i.e. the 11th message, trips the mute and is still delivered);
        // the 12th finds the mute (the check precedes the counter).
        for (int i = 1; i <= 11; i++)
        {
            await spammer.SendChatAsync(ChatType.Say, Language.Common, $"m{i}");
        }

        for (int i = 1; i <= 11; i++)
        {
            Assert.Equal($"m{i}", (await listener.ReadChatAsync()).Text);
        }

        await spammer.SendChatAsync(ChatType.Say, Language.Common, "m12");
        var notification = new PacketReader(await spammer.ReadUntilAsync(WorldOpcode.SmsgNotification));
        Assert.Equal("You must wait 10 Seconds. before speaking again.", notification.ReadCString()); // mangos_string 705 + secsToTimeString
        Assert.DoesNotContain(await listener.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        // Still muted a few seconds in, with the remaining time shown.
        clock.Advance(7);
        await spammer.SendChatAsync(ChatType.Say, Language.Common, "still muted");
        notification = new PacketReader(await spammer.ReadUntilAsync(WorldOpcode.SmsgNotification));
        Assert.Equal("You must wait 3 Seconds. before speaking again.", notification.ReadCString());

        // CanSpeak is m_muteTime <= now: at the end of the mute the player speaks again.
        clock.Advance(3);
        await spammer.SendChatAsync(ChatType.Say, Language.Common, "free again");
        Assert.Equal("free again", (await listener.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task FloodControl_ExemptsStaff_AfkDndAndAddonTraffic_AndCanBeDisabled()
    {
        var clock = new OffsetClock();
        await using WorldTestHost host = StartHost(clock);
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Moderator);
        await using WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener");
        await using WorldTestClient spammer = await host.EnterWorldAsync("SPAMMER", "Spammer");
        await gm.CollectAsync();
        await listener.CollectAsync();
        await spammer.CollectAsync();

        for (int i = 1; i <= 30; i++)
        {
            await gm.SendChatAsync(ChatType.Say, Language.Common, $"g{i}");
        }

        for (int i = 1; i <= 30; i++)
        {
            Assert.Equal($"g{i}", (await listener.ReadChatAsync()).Text);
        }

        // Addon traffic neither counts nor is blocked (vmangos: "LANG_ADDON should not be ... affected by flood control").
        for (int i = 0; i < 30; i++)
        {
            await spammer.SendChatAsync(ChatType.Battleground, Language.Addon, "x");
        }

        await spammer.SendChatAsync(ChatType.Say, Language.Common, "after addons");
        Assert.Equal("after addons", (await listener.ReadChatAsync()).Text);

        // Mute the spammer, then show that an AFK message (no flood check) still goes through.
        for (int i = 1; i <= 11; i++)
        {
            await spammer.SendChatAsync(ChatType.Say, Language.Common, $"s{i}");
        }

        await spammer.SendChatAsync(ChatType.Say, Language.Common, "muted");
        await spammer.ReadUntilAsync(WorldOpcode.SmsgNotification);
        await spammer.SendChatAsync(ChatType.Afk, Language.Universal, "brb");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Spammer")!.IsAfk, "AFK while muted");

        // ChatFlood.MessageCount = 0 turns the counter off.
        clock.Advance(60);
        host.WorldServices.GetRequiredService<ChatFeature>().Options.FloodMessageCount = 0;
        await listener.CollectAsync();
        for (int i = 1; i <= 30; i++)
        {
            await spammer.SendChatAsync(ChatType.Say, Language.Common, $"n{i}");
        }

        for (int i = 1; i <= 30; i++)
        {
            Assert.Equal($"n{i}", (await listener.ReadChatAsync()).Text);
        }
    }

    [Fact]
    public async Task MutedSpeaker_CannotEmote_OrTextEmote_ButCanWhisperStaffOnly()
    {
        var clock = new OffsetClock();
        await using WorldTestHost host = StartHost(clock);
        await using WorldTestClient muted = await host.EnterWorldAsync("MUTED", "Muted");
        await using WorldTestClient friend = await host.EnterWorldAsync("FRIEND", "Friend");
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Moderator);
        await muted.CollectAsync();
        await friend.CollectAsync();
        await gm.CollectAsync();

        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<ChatFeature>()
            .MuteUntil(host.World.FindOnlinePlayer("Muted")!, clock.UnixNow + 30));

        // vmangos HandleEmoteOpcode / HandleTextEmoteOpcode: CanSpeak() before anything else.
        await muted.SendAsync(WorldOpcode.CmsgEmote, BitConverter.GetBytes(3u));
        Assert.Equal("You must wait 30 Seconds. before speaking again.", new PacketReader(await muted.ReadUntilAsync(WorldOpcode.SmsgNotification)).ReadCString());
        Assert.DoesNotContain(await muted.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgEmote);

        await muted.SendAsync(WorldOpcode.CmsgTextEmote, [.. BitConverter.GetBytes(34u), .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(0ul)]);
        Assert.Equal("You must wait 30 Seconds. before speaking again.", new PacketReader(await muted.ReadUntilAsync(WorldOpcode.SmsgNotification)).ReadCString());

        // Whispers: "Can only whisper GMs while muted" (ChatHandler.cpp:420) — a plain target is refused.
        await muted.SendChatAsync(ChatType.Whisper, Language.Common, "psst", target: "Friend");
        Assert.Equal("You must wait 30 Seconds. before speaking again.", new PacketReader(await muted.ReadUntilAsync(WorldOpcode.SmsgNotification)).ReadCString());
        Assert.DoesNotContain(await friend.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        await muted.SendChatAsync(ChatType.Whisper, Language.Common, "help me", target: "Staff");
        Assert.Equal("help me", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task UnknownLanguage_GetsTheReferenceNotificationText()
    {
        await using WorldTestHost host = StartHost(new OffsetClock());
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await human.CollectAsync();

        await human.SendChatAsync(ChatType.Say, Language.Orcish, "Lok'tar");

        // classic-db mangos_string 806 (vmangos LANG_NOT_LEARNED_LANGUAGE): no trailing full stop.
        Assert.Equal("You don't know that language", new PacketReader(await human.ReadUntilAsync(WorldOpcode.SmsgNotification)).ReadCString());
    }

    [Fact]
    public async Task MessageTypesAtOrAboveTheMaximum_AreDropped_BeforeAnyOtherCheck()
    {
        await using WorldTestHost host = StartHost(new OffsetClock());
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await using WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener");
        await human.CollectAsync();
        await listener.CollectAsync();

        // 0x5E is MAX_CHAT_MSG_TYPE (SharedDefines.h:1303). With an unknown language the in-range type
        // would earn a notification; the out-of-range one is dropped silently first.
        await human.SendChatAsync((ChatType)0x5E, Language.Orcish, "x");
        await human.SendChatAsync((ChatType)0x80, Language.Common, "x");
        Assert.DoesNotContain(await human.CollectAsync(), p => p.Opcode is WorldOpcode.SmsgNotification or WorldOpcode.SmsgMessagechat);
        Assert.DoesNotContain(await listener.CollectAsync(), p => p.Opcode is WorldOpcode.SmsgNotification or WorldOpcode.SmsgMessagechat);
    }

    [Fact]
    public async Task AddonChannelOption_DecidesWhetherAddonMessagesAreOfferedToTheFeatures()
    {
        var spy = new ChatMessageSpy();
        await using WorldTestHost host = StartHost(new OffsetClock(), spy);
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await human.CollectAsync();

        await human.SendChatAsync(ChatType.Battleground, Language.Addon, "on");
        await host.WaitForWorldAsync(() => spy.Count == 1, "an addon message offered while AddonChannel is on");

        // vmangos "Disabled addon channel?": dropped before any feature sees it.
        host.WorldServices.GetRequiredService<ChatFeature>().Options.AddonChannel = false;
        await human.SendChatAsync(ChatType.Battleground, Language.Addon, "off");
        await human.SendChatAsync(ChatType.Battleground, Language.Addon, "off again");
        await human.SendChatAsync(ChatType.Say, Language.Common, "sync"); // a later message proves the earlier ones were handled
        await human.ReadChatAsync();
        Assert.Equal(1, spy.Count);
    }

    [Theory]
    [InlineData(0, "0 Second.")]
    [InlineData(1, "1 Second.")]
    [InlineData(10, "10 Seconds.")]
    [InlineData(60, "1 Minute ")]
    [InlineData(90, "1 Minute 30 Seconds.")]
    [InlineData(3600, "1 Hour ")]
    [InlineData(7322, "2 Hours 2 Minutes 2 Seconds.")]
    [InlineData(86400, "1 Day ")]
    [InlineData(2 * 86400 + 3600 + 61, "2 Days 1 Hour 1 Minute 1 Second.")]
    public void SecsToTimeString_MatchesTheReferenceFormat(long seconds, string expected)
        => Assert.Equal(expected, ChatText.SecsToTimeString(seconds)); // vmangos shared/Util.cpp:197-248

    /// <summary>A clock that stands still until advanced (the flood window is whole seconds).</summary>
    private sealed class OffsetClock : TimeProvider
    {
        private readonly DateTimeOffset _start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        private long _advanced;

        internal long UnixNow => GetUtcNow().ToUnixTimeSeconds();

        internal void Advance(long seconds) => Interlocked.Add(ref _advanced, seconds);

        public override DateTimeOffset GetUtcNow() => _start.AddSeconds(Interlocked.Read(ref _advanced));
    }

    /// <summary>Counts the addon messages that reach the <see cref="IChatMessageHandler"/> seam and consumes none.</summary>
    private sealed class ChatMessageSpy : IChatMessageHandler
    {
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        public bool TryHandle(WorldSession session, Player player, ClientChatMessage message)
        {
            if (message.Language == Language.Addon)
            {
                Interlocked.Increment(ref _count);
            }

            return false;
        }
    }
}
