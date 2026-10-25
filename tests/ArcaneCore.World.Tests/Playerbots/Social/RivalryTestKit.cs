using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Tests.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>What a real player's client heard: the text emotes it was sent (who, which, aimed at whom) and the system lines.</summary>
internal sealed record Heard(List<(ulong Emoter, uint Emote, string Target)> Emotes, List<string> SystemLines);

/// <summary>
/// Shared set-up of the P6a world tests (<c>PlayerbotRivalry*WorldTests</c>): the zones a quiet-place check needs, seeding of rivals, driving the
/// lane by hand at a chosen world time, reading what a player's client heard, and searching the hash rolls for a time that passes or fails.
/// <para>
/// Faction note: the Human start is on map 0 with the Undead (race 5) start, so a Horde character of <see cref="Undead"/> and an Alliance
/// character of <see cref="SocialTestKit.Human"/> can be moved next to each other on one map; an Orc starts on Kalimdor and could not be.
/// </para>
/// </summary>
internal static class RivalryKit
{
    public const byte Undead = 5;

    public const uint OrdinaryZone = 12, CapitalZone = 1519;

    public static readonly AreaTemplate Ordinary = new(OrdinaryZone, 0, 0, OrdinaryZone, 0, 1, "Ordinary zone", 0, 0);

    public static readonly AreaTemplate Capital = new(CapitalZone, 0, 0, CapitalZone, (uint)AreaFlags.Capital, 1, "Capital zone", 0, 0);

    /// <summary>The thirty-minute bucket of the rolls, in milliseconds.</summary>
    public const long Bucket = 30 * 60_000L;

    /// <summary>A frozen world (no pass runs by itself) with the area table a rude emote needs, every greeting chance at its ceiling.</summary>
    public static async Task<SocialWorld> StartAsync(Action<PlayerbotOptions>? configure = null, bool chat = false)
    {
        SocialWorld world = await SocialWorld.StartAsync(options =>
        {
            options.Social.Rivalry.Emotes.GreetStrangerChance = 1.0;
            if (chat)
            {
                options.Chat.Enabled = true;
                options.Chat.PerPlayerCooldownSeconds = 0;
                options.Chat.Speech.EmoteReplies = true;
                // No real-time gaps: whether a bot answers is then decided by the rules alone, never by how fast the test ran.
                options.Chat.Speech.EmoteReplyGapSeconds = 0;
                options.Chat.Speech.BotGapSeconds = 0;
                options.Chat.Speech.TargetGapSeconds = 0;
                options.Chat.Speech.BotLinesPerMinute = 60;
                options.Chat.Speech.DedupeSeconds = 0; // the same emote twice to one target is two hearings in these tests, not a repeat
            }

            configure?.Invoke(options);
        }, frozen: true);
        await world.SetAreasAsync(Ordinary, Capital);
        return world;
    }

    public static PlayerbotRivalryLane Lane(SocialWorld world) => SocialTestKit.Feature(world.Host).Rivalry;

    public static PlayerbotRivalryOptOut OptOut(SocialWorld world) => SocialTestKit.Feature(world.Host).RivalryOptOut;

    public static PlayerbotSocialMemory Memory(SocialWorld world) => PartyTestHost.Feature(world.Host).Social.Memory;

    public static Player Online(SocialWorld world, string name) => world.Host.World.FindOnlinePlayer(name)!;

    public static async Task<ObjectGuid> GuidAsync(SocialWorld world, string name) => await world.Host.PlayerStateAsync(name, p => p.Guid);

    /// <summary>Run the rivalry lane once at world time <paramref name="nowMs"/>, every character in the world standing in <paramref name="zone"/> first and <paramref name="beforeRun"/> called on the same world step (world thread).</summary>
    public static Task RunAsync(SocialWorld world, long nowMs, uint zone = OrdinaryZone, Action? beforeRun = null)
        => world.Host.OnWorldAsync(() =>
        {
            foreach (Player player in world.Host.World.OnlinePlayers) player.ZoneId = zone;
            beforeRun?.Invoke(); // on the same world step as the run: a state set here (combat) is still true when the lane looks
            Lane(world).Run(SocialTestKit.BotPass(world.Host, nowMs));
            return true;
        });

    /// <summary>As above with a zone per named character; every other character is put in the ordinary zone.</summary>
    public static Task RunInZonesAsync(SocialWorld world, long nowMs, params (string Name, uint Zone)[] zones)
        => world.Host.OnWorldAsync(() =>
        {
            foreach (Player player in world.Host.World.OnlinePlayers) player.ZoneId = OrdinaryZone;
            foreach ((string name, uint zone) in zones) Online(world, name).ZoneId = zone;
            Lane(world).Run(SocialTestKit.BotPass(world.Host, nowMs));
            return true;
        });

    /// <summary>Note <paramref name="kills"/> PvP kills of <paramref name="killer"/> on <paramref name="bot"/> (-20 each): three make a rival (-60 at or below -50).</summary>
    public static Task SeedAsync(SocialWorld world, string bot, string killer, bool killerIsBot, int kills = 3)
        => world.Host.OnWorldAsync(() =>
        {
            Player self = Online(world, bot), other = Online(world, killer);
            for (int i = 0; i < kills; i++)
                Memory(world).Note(world.Bots[bot], (int)self.Guid.Low, other.Guid.Value, killerIsBot, PlayerbotSocialEvent.KilledBy, otherFaction: self.Team != other.Team);
            return true;
        });

    /// <summary>A stored relation with the given affinity (effective as of now), loaded as a restart would.</summary>
    public static Task LoadAsync(SocialWorld world, string bot, ulong other, short affinity, PlayerbotRelationFlags flags = PlayerbotRelationFlags.Interacted)
        => world.Host.OnWorldAsync(() =>
        {
            PlayerbotSocialMemory memory = Memory(world);
            memory.Load([new PlayerbotRelation(world.Bots[bot], (int)Online(world, bot).Guid.Low, other, false, affinity, 8, 500, flags, memory.NowMs, string.Empty, string.Empty)]);
            return true;
        });

    public static PlayerbotRelation? RelationOf(SocialWorld world, Guid bot, ulong other)
        => Memory(world).TryGet(bot, other, out PlayerbotRelation relation) ? relation : null;

    /// <summary>The first world time (a bucket's first minute) after <paramref name="afterMs"/> at which <paramref name="yes"/> holds; throws when none does.</summary>
    public static long TimeWhere(Func<long, bool> yes, long afterMs = 0)
    {
        for (long bucket = afterMs / Bucket + 1; bucket < 400_000; bucket++)
        {
            long now = bucket * Bucket + 60_000;
            if (yes(now)) return now;
        }

        throw new InvalidOperationException("no world time gives the wanted roll");
    }

    /// <summary>The chance with which <paramref name="botGuid"/> greets a stranger, as the lane computes it with <c>GreetStrangerChance</c> at its ceiling.</summary>
    public static double StrangerChance(ulong botGuid)
        => PlayerbotRivalryRules.GreetChance(PlayerbotStance.Neutral, known: false, strangerChance: 1.0, PlayerbotTraits.For(botGuid).Sociability);

    public static bool GreetPasses(ulong bot, ulong target, long nowMs) => PlayerbotRivalryRules.GreetRoll(bot, target, nowMs) < StrangerChance(bot);

    /// <summary>Everything the client was sent since it last looked (it waits for the stream to go quiet).</summary>
    public static async Task<Heard> ListenAsync(WorldTestClient client)
    {
        var emotes = new List<(ulong, uint, string)>();
        var lines = new List<string>();
        foreach ((WorldOpcode opcode, byte[] payload) in await client.CollectAsync())
        {
            if (opcode == WorldOpcode.SmsgTextEmote) emotes.Add(ParseTextEmote(payload));
            else if (opcode == WorldOpcode.SmsgMessagechat && ChatMessage.Parse(payload) is { Type: ChatType.System } system) lines.Add(system.Text);
        }

        return new Heard(emotes, lines);
    }

    /// <summary>SMSG_TEXT_EMOTE: u64 emoter, u32 text emote, u32 emote number, u32 target-name length (with its NUL), the name.</summary>
    public static (ulong Emoter, uint Emote, string Target) ParseTextEmote(byte[] payload)
    {
        ulong emoter = BitConverter.ToUInt64(payload, 0);
        uint emote = BitConverter.ToUInt32(payload, 8);
        uint length = BitConverter.ToUInt32(payload, 16);
        string target = length <= 1 ? string.Empty : System.Text.Encoding.UTF8.GetString(payload, 20, (int)length - 1);
        return (emoter, emote, target);
    }

    /// <summary>SMSG_TEXT_EMOTE as the server's broadcast builds it, handed to a bot's session (the bot hears an emote aimed at it by name).</summary>
    public static Task HearAsync(SocialWorld world, string bot, ObjectGuid emoter, uint emote)
        => world.Host.OnWorldAsync(() =>
        {
            ((ArcaneCore.World.Net.WorldSession)Online(world, bot).Session).Send(WorldOpcode.SmsgTextEmote, ArcaneCore.World.Packets.ChatPackets.BuildTextEmote(emoter, emote, 0, bot));
            return true;
        });

    /// <summary>
    /// Wait (real time, bounded) until <paramref name="done"/> holds for the text emotes the client has heard so far, then a quiet second more so an
    /// emote that should not exist has had its chance; every text emote heard, in order.
    /// </summary>
    public static async Task<List<(ulong Emoter, uint Emote, string Target)>> HearUntilAsync(WorldTestClient client,
        Func<List<(ulong Emoter, uint Emote, string Target)>, bool> done, int seconds = 15)
    {
        var all = new List<(ulong Emoter, uint Emote, string Target)>();
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done(all) && DateTime.UtcNow < deadline) all.AddRange((await ListenAsync(client)).Emotes);
        await Task.Delay(1000);
        all.AddRange((await ListenAsync(client)).Emotes);
        return all;
    }

    /// <summary>The emotes of <paramref name="speakers"/> in <paramref name="heard"/>, by speaker, optionally only those aimed at <paramref name="target"/>.</summary>
    public static Dictionary<ulong, List<uint>> BySpeaker(IEnumerable<(ulong Emoter, uint Emote, string Target)> heard, IReadOnlyCollection<ulong> speakers, string? target = null)
    {
        Dictionary<ulong, List<uint>> bySpeaker = speakers.ToDictionary(s => s, _ => new List<uint>());
        foreach ((ulong emoter, uint emote, string aimedAt) in heard)
            if (bySpeaker.TryGetValue(emoter, out List<uint>? list) && (target is null || aimedAt == target)) list.Add(emote);
        return bySpeaker;
    }

    /// <summary>Wait for <paramref name="expected"/> emotes from <paramref name="speakers"/> (aimed at <paramref name="target"/> when given), then collect them by speaker.</summary>
    public static async Task<Dictionary<ulong, List<uint>>> RepliesAsync(WorldTestClient client, IReadOnlyCollection<ulong> speakers, int expected, int seconds = 15, string? target = null)
        => BySpeaker(await HearUntilAsync(client, all => BySpeaker(all, speakers, target).Values.Sum(list => list.Count) >= expected, seconds), speakers, target);

    public static bool IsRude(uint emote) => PlayerbotEmoteMoods.Classify(emote) == PlayerbotEmoteMood.Rude;

    public static readonly HashSet<uint> RudeEmotes =
        [PlayerbotSocialEmotes.TextEmotes.Rude, PlayerbotSocialEmotes.TextEmotes.Chicken, PlayerbotSocialEmotes.TextEmotes.Threaten,
         PlayerbotSocialEmotes.TextEmotes.Mock, PlayerbotSocialEmotes.TextEmotes.Violin];

    public static readonly HashSet<uint> NeutralGreetings =
        [PlayerbotSocialEmotes.TextEmotes.Wave, PlayerbotSocialEmotes.TextEmotes.Nod, PlayerbotSocialEmotes.TextEmotes.Bow];

    public static readonly HashSet<uint> FriendlyGreetings =
        [PlayerbotSocialEmotes.TextEmotes.Wave, PlayerbotSocialEmotes.TextEmotes.Bow, PlayerbotSocialEmotes.TextEmotes.Salute,
         PlayerbotSocialEmotes.TextEmotes.Cheer, PlayerbotSocialEmotes.TextEmotes.Hail];

    /// <summary>The system line the first taunt of a login is followed by (design 4.3).</summary>
    public const string DisclosureText = "A bot rival taunted you. Whisper \"rivals off\" to any bot of your faction to stop bot taunts.";
}
