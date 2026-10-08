using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
                    Duration = new SpellDuration(-1, 0, -1),
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
    public async Task FloodMute_SurvivesALogoutAndRelog_BecauseItBelongsToTheSession()
    {
        // vmangos keeps m_muteTime on the WorldSession (Player::CanSpeak, UpdateSpeakTime), and the
        // session outlives the Player: logging out to the character screen does not clear the mute.
        var clock = new OffsetClock();
        await using WorldTestHost host = WorldTestHost.Start(configure: o => o.LogoutDelayMs = 50, configureServices: services => services.AddSingleton<TimeProvider>(clock));
        await using WorldTestClient spammer = await host.EnterWorldAsync("SPAMMER", "Spammer");
        await spammer.CollectAsync();
        for (int i = 1; i <= 11; i++)
        {
            await spammer.SendChatAsync(ChatType.Say, Language.Common, $"m{i}");
        }

        await spammer.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await spammer.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await WorldTestHost.WaitForAsync(() => !host.World.IsOnline(ObjectGuid.Player(1)), "the logout");
        await spammer.LoginAsync(1);
        await spammer.CollectAsync();

        await spammer.SendChatAsync(ChatType.Say, Language.Common, "after relog");
        var notification = new PacketReader(await spammer.ReadUntilAsync(WorldOpcode.SmsgNotification));
        Assert.StartsWith("You must wait", notification.ReadCString());

        clock.Advance(10);
        await spammer.SendChatAsync(ChatType.Say, Language.Common, "free");
        Assert.DoesNotContain(await spammer.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgNotification);
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

        await host.OnWorldAsync(() =>
        {
            var chat = host.WorldServices.GetRequiredService<ChatFeature>();
            chat.MuteUntil(host.World.FindOnlinePlayer("Muted")!, clock.UnixNow + 30);
            chat.SetAcceptWhispers(host.World.FindOnlinePlayer("Staff")!, true); // a plain player only sees staff that accepts whispers
        });

        // vmangos HandleEmoteOpcode / HandleTextEmoteOpcode: CanSpeak() before anything else.
        await muted.SendAsync(WorldOpcode.CmsgEmote, BitConverter.GetBytes(3u));
        Assert.Equal("You must wait 30 Seconds. before speaking again.", new PacketReader(await muted.ReadUntilAsync(WorldOpcode.SmsgNotification)).ReadCString());
        Assert.DoesNotContain(await muted.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgEmote);

        await muted.SendAsync(WorldOpcode.CmsgTextEmote, [.. BitConverter.GetBytes(34u), .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(0ul)]);
        Assert.Equal("You must wait 30 Seconds. before speaking again.", new PacketReader(await muted.ReadUntilAsync(WorldOpcode.SmsgNotification)).ReadCString());

        // Whispers: "Can only whisper GMs while muted" (ChatHandler.cpp:417) — a plain target is refused.
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
    [InlineData(false, AccountSecurity.Player, 2)]
    [InlineData(true, AccountSecurity.Player, 2)]
    [InlineData(true, AccountSecurity.Moderator, 2)]
    [InlineData(true, AccountSecurity.Player, 0)]
    public async Task AddonMuteAndFloodControl_OnlyAppliesWhenEnabled(bool enabled, AccountSecurity security, uint messageCount)
    {
        var clock = new OffsetClock();
        var spy = new ChatMessageSpy();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IChatMessageHandler>(spy);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["World:Chat:AddonMuteAndFloodControl"] = enabled.ToString(),
                ["World:Chat:FloodMessageCount"] = messageCount.ToString(),
            }).Build());
        });
        await using WorldTestClient speaker = await host.EnterWorldAsync("SPEAKER", "Speaker", security);
        await speaker.CollectAsync();

        // Default parity: addons neither advance the counter nor respect the flood mute.
        // Opt in for a plain player with counting enabled: the third message trips the shared
        // mute and is delivered. Staff and a zero message count remain exempt from counting.
        for (int i = 0; i < 3; i++)
        {
            await speaker.SendChatAsync(ChatType.Battleground, Language.Addon, "addon");
        }

        await host.WaitForWorldAsync(() => spy.Count == 3, "the first three addon messages");
        ChatFeature chat = host.WorldServices.GetRequiredService<ChatFeature>();
        Assert.Equal(enabled && security == AccountSecurity.Player && messageCount > 0,
            await host.PlayerStateAsync("Speaker", player => !chat.CanSpeak(player)));

        // A mute source extends the end time beyond the flood mute: addons respect it only
        // when enabled, even for staff or with counting disabled, before any feature dispatch.
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<ChatRestrictionFeature>().Service.Mute(
            host.World.FindOnlinePlayer("Speaker")!.AccountId, clock.UnixNow + 30));
        await speaker.SendChatAsync(ChatType.Battleground, Language.Addon, "muted addon");
        if (enabled)
        {
            Assert.Equal("You must wait 30 Seconds. before speaking again.", new PacketReader(await speaker.ReadUntilAsync(WorldOpcode.SmsgNotification)).ReadCString());
            Assert.Equal(3, spy.Count);
        }
        else
        {
            await host.WaitForWorldAsync(() => spy.Count == 4, "an addon message despite the default mute exemption");
        }

        clock.Advance(30);
        await speaker.SendChatAsync(ChatType.Battleground, Language.Addon, "after expiry");
        await host.WaitForWorldAsync(() => spy.Count == (enabled ? 4 : 5), "addon delivery after expiry");
    }

    [Fact]
    public async Task ExpiredSessionMutes_ArePurgedWithoutTheAccountSpeakingAgain()
    {
        var clock = new OffsetClock();
        await using WorldTestHost host = StartHost(clock);
        await using WorldTestClient offline = await host.EnterWorldAsync("OFFLINE", "Offline");
        await using WorldTestClient online = await host.EnterWorldAsync("ONLINE", "Online");
        ChatFeature chat = host.WorldServices.GetRequiredService<ChatFeature>();
        // Observe retained storage, rather than calling MutedUntil for the expired account:
        // that call already pruned its own entry before this regression was fixed.
        var mutes = (Dictionary<int, long>)typeof(ChatFeature).GetField("_sessionMutes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(chat)!;
        await host.OnWorldAsync(() =>
        {
            chat.MuteUntil(host.World.FindOnlinePlayer("Offline")!, clock.UnixNow + 30);
            chat.MuteUntil(host.World.FindOnlinePlayer("Online")!, clock.UnixNow + 60);
            chat.MuteUntil(host.World.FindOnlinePlayer("Online")!, clock.UnixNow + 10); // a shorter mute cannot replace an active one
            Assert.Equal(2, mutes.Count);
        });
        await offline.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Offline") is null, "muted account disconnect");

        clock.Advance(30);
        await host.WaitForWorldAsync(() => mutes.Count == 1, "idle expiry cleanup for a disconnected account");
        Assert.False(await host.PlayerStateAsync("Online", chat.CanSpeak));
        clock.Advance(30);
        await host.WaitForWorldAsync(() => mutes.Count == 0, "idle expiry cleanup for an online account");
    }

    [Fact]
    public async Task ExpiredSessionMutes_ArePurgedWhenTheWorldHasNoMaps()
    {
        var clock = new OffsetClock();
        await using WorldTestHost host = StartHost(clock);
        await using WorldTestClient client = await host.EnterWorldAsync("SPEAKER", "Speaker");
        Player player = await host.PlayerAsync("Speaker");
        using var world = new WorldRuntime(new WorldRuntimeOptions(), host.SaveQueue, NullLogger<WorldRuntime>.Instance);
        var chat = new ChatFeature(timeProvider: clock);
        chat.Attach(world);
        var mutes = (Dictionary<int, long>)typeof(ChatFeature).GetField("_sessionMutes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(chat)!;
        chat.MuteUntil(player, clock.UnixNow + 10);
        world.RunTick(5);
        Assert.Single(mutes);

        clock.Advance(10);
        world.RunTick(5);

        Assert.Empty(world.Maps);
        Assert.Empty(mutes);
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
