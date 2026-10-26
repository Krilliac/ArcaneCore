using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Parity test C of the design (7.3): nothing the war layer decides reaches a bot's normal layer through behaviour. Two real worlds get the same
/// characters, the same seeds and the same simulated minutes; in one War Mode is on while the lane and the reply hook run and is then switched
/// off for the rest, in the other it is never on. Every bot's normal-layer rows (affinity, familiarity, trust, flags) must be identical at the
/// end, whatever the war layer made the first world do.
/// <para>
/// A leak this catches: a lane that read the war layer toward a bot would, with <c>RudeAtBots</c> on, send a rude emote at C (C records it in its
/// own normal layer), and a reply that read the war layer would answer differently and move a bot's memory.
/// </para>
/// <para>
/// Non-vacuity: the war world sent a war-driven rude emote at the real player that the never-on world did not, and in both worlds a bot greeted
/// another bot and a bot answered. Last-seen stamps are not compared (each world stamps them from its own real clock); an extra event still
/// shows in affinity, familiarity or flags.
/// </para>
/// </summary>
public sealed class PlayerbotRivalryWarParityWorldTests
{
    private const int WarMinutes = 40, QuietMinutes = 40;

    private static readonly string[] Names = ["Rvpara", "Rvparb", "Rvparc", "Rvparp"];

    private sealed record Outcome(
        long StartMs, SortedDictionary<string, string> SeededRows, SortedDictionary<string, string> FinalRows,
        List<PlayerbotRivalrySend> Sends, List<(ulong Emoter, uint Emote, string Target)> Heard, Dictionary<string, ulong> Guids, Dictionary<string, Guid> Bots);

    private static SortedDictionary<string, string> NormalRows(SocialWorld world, Dictionary<string, ulong> guids)
    {
        Dictionary<ulong, string> names = guids.ToDictionary(p => p.Value, p => p.Key);
        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string bot in new[] { "Rvpara", "Rvparb", "Rvparc" })
            foreach (PlayerbotRelation row in RivalryKit.Memory(world).Snapshot(world.Bots[bot]))
                rows[$"{bot}->{(names.TryGetValue(row.OtherGuid, out string? name) ? name : "guid " + row.OtherGuid)}"] =
                    $"aff={row.Affinity} fam={row.Familiarity} trust={row.TrustPermille} flags={row.Flags}";
        return rows;
    }

    private static List<(string Bot, ulong Target, uint Emote, bool Rude)> BotSends(Outcome outcome)
        => [.. outcome.Sends.Where(s => s.TargetIsBot).Select(s => (outcome.Bots.First(p => p.Value == s.Bot).Key, s.Target, s.Emote, s.Rude))];

    private static async Task<Outcome> RunAsync(bool warWorld, bool rudeAtBots, long? startMs)
    {
        // FriendlyAffinity 0 (allowed): a blank row would read as Friendly if the normal layer took a war-only row for a known relation.
        await using SocialWorld world = await RivalryKit.StartAsync(o =>
        {
            o.Social.Rivalry.Emotes.RudeAtBots = rudeAtBots;
            o.Social.Rivalry.Emotes.FriendlyAffinity = 0;
        }, chat: true);
        await world.AddBotAsync("Rvpara");
        await world.AddBotAsync("Rvparb");
        await world.AddBotAsync("Rvparc", race: RivalryKit.Undead);
        await using WorldTestClient player = await world.AddPlayerAsync("Rvparp", RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Rvpara", ("Rvparb", 2f), ("Rvparc", 3f), ("Rvparp", 4f));
        var guids = new Dictionary<string, ulong>();
        foreach (string name in Names) guids[name] = (await RivalryKit.GuidAsync(world, name)).Value;

        // A is killed once by C (a bot) and once by P (the player). With War Mode on the war layer makes each a rival of A; the normal layer holds
        // the same -20 (Disliked, not Rival) in both worlds.
        world.Options.Social.WarMode.Enabled = warWorld;
        await RivalryKit.SeedAsync(world, "Rvpara", "Rvparc", killerIsBot: true, kills: 1);
        await RivalryKit.SeedAsync(world, "Rvpara", "Rvparp", killerIsBot: false, kills: 1);
        PlayerbotRelation toPlayer = RivalryKit.RelationOf(world, world.Bots["Rvpara"], guids["Rvparp"])!;
        Assert.Equal(-20, toPlayer.Affinity);
        Assert.False(toPlayer.Flags.HasFlag(PlayerbotRelationFlags.Rival));
        Assert.Equal(warWorld, RivalryKit.Memory(world).IsRival(toPlayer));

        // B holds a war-only relation for C (the normal rules evicted it, the war layer kept it): visible only under War Mode, and there a blank row.
        await world.Host.OnWorldAsync(() =>
        {
            PlayerbotSocialMemory memory = RivalryKit.Memory(world);
            memory.Load([new PlayerbotRelation(world.Bots["Rvparb"], (int)RivalryKit.Online(world, "Rvparb").Guid.Low, guids["Rvparc"], true, 0, 0, 500,
                PlayerbotRelationFlags.WarOnly, memory.NowMs, string.Empty, string.Empty, -60, PlayerbotRelationFlags.WarSeeded | PlayerbotRelationFlags.Rival)]);
            return true;
        });
        PlayerbotRelation? evicted = RivalryKit.RelationOf(world, world.Bots["Rvparb"], guids["Rvparc"]);
        Assert.Equal(warWorld, evicted is not null);
        if (evicted is not null) Assert.True(evicted.Familiarity == 0 && evicted.Affinity == 0);
        SortedDictionary<string, string> seeded = NormalRows(world, guids);

        // One start time for both worlds, chosen in the first: B's greeting to C passes its roll and the pick is one C answers (not a nod).
        ulong b = guids["Rvparb"], c = guids["Rvparc"];
        long start = startMs ?? RivalryKit.TimeWhere(t => RivalryKit.GreetPasses(b, c, t)
            && PlayerbotRivalryRules.PickGreeting(PlayerbotStance.Neutral, PlayerbotRivalryRules.PickRoll(b, c, t)) != PlayerbotSocialEmotes.TextEmotes.Nod);

        for (int minute = 0; minute < WarMinutes + QuietMinutes; minute++)
        {
            if (warWorld && minute == WarMinutes) world.Options.Social.WarMode.Enabled = false;
            int before = RivalryKit.Lane(world).Sends.Count;
            await RivalryKit.RunAsync(world, start + minute * 60_000L);
            if (RivalryKit.Lane(world).Sends.Count > before) await Task.Delay(400); // let the bots that were emoted at answer before the next minute
        }

        var heard = new List<(ulong Emoter, uint Emote, string Target)>();
        await Task.Delay(1500);
        heard.AddRange((await RivalryKit.ListenAsync(player)).Emotes);
        return new Outcome(start, seeded, NormalRows(world, guids), [.. RivalryKit.Lane(world).Sends], heard, guids, new Dictionary<string, Guid>(world.Bots));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarModeOnThenOff_LeavesEveryBotsNormalLayerExactlyAsAWorldThatNeverHadIt(bool rudeAtBots)
    {
        Outcome war = await RunAsync(warWorld: true, rudeAtBots, startMs: null);
        Outcome calm = await RunAsync(warWorld: false, rudeAtBots, startMs: war.StartMs);

        Assert.Equal(war.Guids, calm.Guids); // same characters, same ids: the rolls are the same rolls
        Assert.Equal(calm.SeededRows, war.SeededRows); // the premise: equal normal layers before anything happens

        // Non-vacuity, war world: it taunted the real player (a war-layer rival); the calm world, where the player is only disliked, did not.
        ulong playerGuid = war.Guids["Rvparp"];
        Assert.Contains(war.Sends, s => s.Rude && s.Target == playerGuid && !s.TargetIsBot);
        Assert.DoesNotContain(calm.Sends, s => s.Rude && s.Target == playerGuid);

        // Non-vacuity, both worlds: B greeted C, and C emoted at B more often than the lane sent C's own greetings: a reply happened.
        foreach ((string label, Outcome world) in new[] { ("war", war), ("calm", calm) })
        {
            ulong bTarget = world.Guids["Rvparb"], cTarget = world.Guids["Rvparc"];
            Guid b = world.Bots["Rvparb"], c = world.Bots["Rvparc"];
            Assert.True(world.Sends.Any(s => s.Bot == b && s.Target == cTarget && s.TargetIsBot && !s.Rude), $"{label}: B never greeted C");
            int heardFromC = world.Heard.Count(e => e.Emoter == cTarget && e.Target == "Rvparb");
            int sentByC = world.Sends.Count(s => s.Bot == c && s.Target == bTarget);
            Assert.True(heardFromC > sentByC, $"{label}: C emoted at B {heardFromC} times and the lane sent {sentByC}: no bot answered");
        }

        // What the lane sent to bots is identical, send for send (design 7.1): the war layer decides nothing toward a bot.
        List<(string Bot, ulong Target, uint Emote, bool Rude)> toBotsCalm = BotSends(calm), toBotsWar = BotSends(war);
        Assert.NotEmpty(toBotsCalm);
        Assert.Equal(toBotsCalm, toBotsWar);

        // The assertion the test exists for.
        Assert.Equal(calm.FinalRows, war.FinalRows);
    }
}
