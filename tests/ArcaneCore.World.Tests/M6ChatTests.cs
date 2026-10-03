using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// M6 chat end to end: say/yell ranges, faction rules for emotes and whispers, whisper
/// informs with AFK/DND replies, language checks, text emotes, animations and /who.
/// Every character starts at the same spot; tests move them with <see cref="WorldTestHost.PlaceAsync"/>.
/// </summary>
public sealed class M6ChatTests
{
    private const float StartX = -8949.95f;
    private const float StartY = -132.493f;
    private const float StartZ = 83.5312f;

    private const byte Human = 1;
    private const byte Orc = 2;

    [Fact]
    public async Task Say_ReachesTheSayRange_YellReachesFurther()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient speaker = await host.EnterWorldAsync("SPEAKER", "Speaker");
        await using WorldTestClient near = await host.EnterWorldAsync("NEAR", "Near");
        await using WorldTestClient far = await host.EnterWorldAsync("FAR", "Far");
        await host.PlaceAsync("Near", StartX + 10, StartY, StartZ);
        await host.PlaceAsync("Far", StartX + 60, StartY, StartZ); // beyond 25 yd, inside 300 yd
        await DrainAsync(speaker, near, far);

        await speaker.SendChatAsync(ChatType.Say, Language.Common, "Hello there");
        ChatMessage said = await speaker.ReadChatAsync(); // the speaker sees its own line
        Assert.Equal(new ChatMessage(ChatType.Say, Language.Common, 1, 1, "Hello there", ChatTag.None), said);
        Assert.Equal(said, await near.ReadChatAsync());
        Assert.DoesNotContain(await far.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        await speaker.SendChatAsync(ChatType.Yell, Language.Common, "OVER HERE");
        ChatMessage yelled = await far.ReadChatAsync();
        Assert.Equal((ChatType.Yell, 1ul, (ulong?)1ul, "OVER HERE"), (yelled.Type, yelled.Sender, yelled.Sender2, yelled.Text));
    }

    [Fact]
    public async Task Say_CrossesFactions_ButCustomEmotesDoNot()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await using WorldTestClient friend = await host.EnterWorldAsync("FRIEND", "Friend");
        await using WorldTestClient orc = await host.EnterWorldAsync("ORC", "Orc", race: Orc);
        await DrainAsync(human, friend, orc);

        // /say reaches everyone in range; the other faction's client garbles the language itself.
        await human.SendChatAsync(ChatType.Say, Language.Common, "For the Alliance");
        Assert.Equal(Language.Common, (await orc.ReadChatAsync()).Language);
        Assert.Equal("For the Alliance", (await friend.ReadChatAsync()).Text);

        // /e is plain text, so it stays within the faction (vmangos Player::TextEmote).
        await human.SendChatAsync(ChatType.Emote, Language.Common, "salutes.");
        ChatMessage emote = await friend.ReadChatAsync();
        Assert.Equal(new ChatMessage(ChatType.Emote, Language.Universal, 1, null, "salutes.", ChatTag.None), emote);
        Assert.DoesNotContain(await orc.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);
    }

    [Fact]
    public async Task Emote_ReachesTheOtherFaction_WhenTwoSideChatIsAllowed()
    {
        await using var host = WorldTestHost.Start(configure: o => o.AllowTwoSideChat = true);
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await using WorldTestClient orc = await host.EnterWorldAsync("ORC", "Orc", race: Orc);
        await DrainAsync(human, orc);

        await human.SendChatAsync(ChatType.Emote, Language.Common, "bows.");
        Assert.Equal("bows.", (await orc.ReadChatAsync()).Text);

        // Two-side chat also turns the faction languages into Universal.
        await human.SendChatAsync(ChatType.Say, Language.Common, "Hello, friend");
        Assert.Equal(Language.Universal, (await orc.ReadChatAsync()).Language);
    }

    [Fact]
    public async Task Whisper_DeliversInform_AndAfkDndReplies()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient sender = await host.EnterWorldAsync("SENDER", "Sender");
        await using WorldTestClient receiver = await host.EnterWorldAsync("RECEIVER", "Receiver");
        await host.PlaceAsync("Receiver", StartX + 5000, StartY, StartZ); // whispers ignore distance
        await DrainAsync(sender, receiver);

        await sender.SendChatAsync(ChatType.Whisper, Language.Common, "psst", target: "rECEIVER");
        Assert.Equal(new ChatMessage(ChatType.Whisper, Language.Universal, 1, null, "psst", ChatTag.None), await receiver.ReadChatAsync());
        Assert.Equal(new ChatMessage(ChatType.WhisperInform, Language.Universal, 2, null, "psst", ChatTag.None), await sender.ReadChatAsync());

        // AFK with a message: the whisperer is told, and the inform carries the AFK tag.
        await receiver.SendChatAsync(ChatType.Afk, Language.Universal, "brb");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Receiver")!.IsAfk, "AFK");
        await sender.SendChatAsync(ChatType.Whisper, Language.Common, "you there?", target: "Receiver");
        Assert.Equal(ChatTag.Afk, (await sender.ReadChatAsync()).Tag);
        Assert.Equal(new ChatMessage(ChatType.Afk, Language.Universal, 2, null, "brb", ChatTag.None), await sender.ReadChatAsync());

        // Going DND ends AFK (vmangos); the DND reply wins.
        await receiver.SendChatAsync(ChatType.Dnd, Language.Universal, "busy");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Receiver") is { IsDnd: true, IsAfk: false }, "DND");
        await sender.SendChatAsync(ChatType.Whisper, Language.Common, "hello?", target: "Receiver");
        Assert.Equal(ChatTag.Dnd, (await sender.ReadChatAsync()).Tag);
        Assert.Equal(new ChatMessage(ChatType.Dnd, Language.Universal, 2, null, "busy", ChatTag.None), await sender.ReadChatAsync());

        // An empty /dnd while DND turns it off.
        await receiver.SendChatAsync(ChatType.Dnd, Language.Universal, "");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Receiver") is { IsDnd: false, IsAfk: false }, "DND off");
    }

    [Fact]
    public async Task Whisper_ToNobody_OrTheOtherFaction_IsRefused_ExceptForStaff()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await using WorldTestClient orc = await host.EnterWorldAsync("ORC", "Orc", race: Orc);
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Moderator);
        await DrainAsync(human, orc, gm);

        await human.SendChatAsync(ChatType.Whisper, Language.Common, "hi", target: "nobody");
        var notFound = new PacketReader(await human.ReadUntilAsync(WorldOpcode.SmsgChatPlayerNotFound));
        Assert.Equal("Nobody", notFound.ReadCString()); // the normalized name

        await human.SendChatAsync(ChatType.Whisper, Language.Common, "hi", target: "Orc");
        Assert.Empty(await human.ReadUntilAsync(WorldOpcode.SmsgChatWrongFaction));
        Assert.DoesNotContain(await orc.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        await gm.SendChatAsync(ChatType.Whisper, Language.Common, "GM here", target: "Orc");
        Assert.Equal("GM here", (await orc.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task Chat_InAnUnknownLanguage_IsRefused_AndGmModeSpeaksUniversal()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Moderator);
        await DrainAsync(human, gm);

        await human.SendChatAsync(ChatType.Say, Language.Orcish, "Lok'tar");
        var notification = new PacketReader(await human.ReadUntilAsync(WorldOpcode.SmsgNotification));
        Assert.Equal("You don't know that language", notification.ReadCString()); // mangos_string 806 has no full stop
        Assert.DoesNotContain(await gm.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        // Universal itself is only accepted for AFK/DND (vmangos IsLanguageAllowedForChatType).
        await human.SendChatAsync(ChatType.Say, Language.Universal, "plain");
        Assert.DoesNotContain(await gm.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Staff")!.SetGameMaster(true));
        await gm.SendChatAsync(ChatType.Say, Language.Common, "In GM mode");
        Assert.Equal(Language.Universal, (await human.ReadChatAsync()).Language);
    }

    [Fact]
    public async Task GmChatBadge_TagsStaffChat()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Player");
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Moderator);
        await DrainAsync(player, gm);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm chat on");
        await gm.ReadChatAsync(); // the command's confirmation
        await gm.SendChatAsync(ChatType.Say, Language.Common, "Badge");
        Assert.Equal(ChatTag.Gm, (await player.ReadChatAsync()).Tag);
    }

    [Fact]
    public async Task TextEmote_NamesTheTarget_ForEveryoneInRange()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient waver = await host.EnterWorldAsync("WAVER", "Waver");
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Target");
        await DrainAsync(waver, target);

        await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(101, 0xFFFFFFFF, target: 2));
        byte[] expected = [.. U64(1), .. U32(101), .. U32(0xFFFFFFFF), .. U32(7), .. "Target"u8, 0];
        Assert.Equal(expected, await waver.ReadUntilAsync(WorldOpcode.SmsgTextEmote));
        Assert.Equal(expected, await target.ReadUntilAsync(WorldOpcode.SmsgTextEmote));

        await waver.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(34, 0, target: 0));
        byte[] untargeted = [.. U64(1), .. U32(34), .. U32(0), .. U32(1), 0]; // no target: length 1, a single NUL
        Assert.Equal(untargeted, await target.ReadUntilAsync(WorldOpcode.SmsgTextEmote));
    }

    [Fact]
    public async Task Emote_OnlyTheClientsOwnAnimations_ArePlayed()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient waver = await host.EnterWorldAsync("WAVER", "Waver");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHER", "Watcher");
        await DrainAsync(waver, watcher);

        await waver.SendAsync(WorldOpcode.CmsgEmote, U32(10)); // EMOTE_ONESHOT_DANCE: not client-initiated
        await waver.SendAsync(WorldOpcode.CmsgEmote, U32(3));  // EMOTE_ONESHOT_WAVE
        byte[] wave = [.. U32(3), .. U64(1)];
        Assert.Equal(wave, await waver.ReadUntilAsync(WorldOpcode.SmsgEmote));
        Assert.Equal(wave, await watcher.ReadUntilAsync(WorldOpcode.SmsgEmote));
        Assert.DoesNotContain(await watcher.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgEmote);
    }

    [Fact]
    public async Task Who_FiltersByFactionLevelRaceClassZoneAndName()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient alpha = await host.EnterWorldAsync("ALPHA", "Alpha");
        await using WorldTestClient beta = await host.EnterWorldAsync("BETA", "Beta");
        await using WorldTestClient orc = await host.EnterWorldAsync("ORC", "Grom", race: Orc);
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Administrator);
        await DrainAsync(alpha, beta, orc, gm);

        Assert.Equal(new[] { "Alpha", "Beta", "Staff" }, await WhoAsync(alpha));
        Assert.Equal(new[] { "Grom" }, await WhoAsync(orc));
        Assert.Equal(new[] { "Alpha", "Beta", "Grom", "Staff" }, await WhoAsync(gm)); // staff see both factions
        Assert.Equal(new[] { "Beta" }, await WhoAsync(alpha, name: "ET"));
        Assert.Equal(new[] { "Alpha" }, await WhoAsync(alpha, strings: ["xyz", "lph"])); // any search string may match
        Assert.Empty(await WhoAsync(alpha, levelMin: 2));
        Assert.Equal(new[] { "Alpha", "Beta", "Staff" }, await WhoAsync(alpha, levelMax: 255));
        Assert.Empty(await WhoAsync(alpha, raceMask: 1u << Orc));
        Assert.Empty(await WhoAsync(alpha, classMask: 1u << 2)); // paladins only
        Assert.Equal(new[] { "Alpha", "Beta", "Staff" }, await WhoAsync(alpha, zones: [12]));
        Assert.Empty(await WhoAsync(alpha, zones: [14]));
        Assert.Empty(await WhoAsync(alpha, guild: "Knights")); // nobody has a guild yet
    }

    [Fact]
    public async Task Who_HidesStaffAbove_GmLevelInWhoList()
    {
        await using var host = WorldTestHost.Start(configure: o => o.GmLevelInWhoList = AccountSecurity.Player);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Player");
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.GameMaster);
        await DrainAsync(player, gm);

        Assert.Equal(new[] { "Player" }, await WhoAsync(player));
    }

    // --- helpers -------------------------------------------------------------------

    private static async Task DrainAsync(params WorldTestClient[] clients)
    {
        foreach (WorldTestClient client in clients)
        {
            await client.CollectAsync();
        }
    }

    private static async Task<List<string>> WhoAsync(
        WorldTestClient client, uint levelMin = 0, uint levelMax = 100, string name = "", string guild = "",
        uint raceMask = 0xFFFFFFFF, uint classMask = 0xFFFFFFFF, uint[]? zones = null, string[]? strings = null)
    {
        var who = new PacketWriter(64);
        who.WriteUInt32(levelMin);
        who.WriteUInt32(levelMax);
        who.WriteCString(name);
        who.WriteCString(guild);
        who.WriteUInt32(raceMask);
        who.WriteUInt32(classMask);
        who.WriteUInt32((uint)(zones?.Length ?? 0));
        foreach (uint zone in zones ?? [])
        {
            who.WriteUInt32(zone);
        }

        who.WriteUInt32((uint)(strings?.Length ?? 0));
        foreach (string term in strings ?? [])
        {
            who.WriteCString(term);
        }

        await client.SendAsync(WorldOpcode.CmsgWho, who.ToArray());
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgWho));
        uint listed = reader.ReadUInt32();
        uint online = reader.ReadUInt32();
        Assert.Equal(listed, online); // not truncated: the online count equals the listed count (vmangos)
        var names = new List<string>();
        for (int i = 0; i < listed; i++)
        {
            names.Add(reader.ReadCString());
            Assert.Equal(string.Empty, reader.ReadCString()); // guild
            Assert.Equal(1u, reader.ReadUInt32());            // level
            Assert.Equal(1u, reader.ReadUInt32());            // class: warrior
            Assert.Contains(reader.ReadUInt32(), new uint[] { Human, Orc });
            Assert.Equal(12u, reader.ReadUInt32());           // zone
        }

        Assert.Equal(0, reader.Remaining);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static byte[] TextEmote(uint textEmote, uint emoteNumber, ulong target) => [.. U32(textEmote), .. U32(emoteNumber), .. U64(target)];

    private static byte[] U32(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] U64(ulong value)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        return b;
    }
}
