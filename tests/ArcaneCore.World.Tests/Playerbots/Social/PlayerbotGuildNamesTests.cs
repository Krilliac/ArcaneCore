using System.Text.RegularExpressions;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Kernel.WorldData.Chat;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The guild names bots draw and the screening that stands between a name and a guild. The charter rules are vmangos IsValidCharterName
/// (length 2..24, one script) and the reserved charter list (PetitionsHandler.cpp:81-96); every other check is a filter the server
/// installs, and a name that any installed check refuses is never used.
/// </summary>
public sealed class PlayerbotGuildNamesTests
{
    private const string Good = "Silver Wolves";

    private sealed class Reserved(params string[] names) : ICharterNameBlacklist
    {
        public bool IsReserved(string lowercaseName) => names.Contains(lowercaseName, StringComparer.Ordinal);
    }

    private static PlayerbotGuildNameRules Rules(
        ICharterNameBlacklist? blacklist = null, ICharterAntispamFilter? antispam = null, Func<string, bool>? taken = null,
        Func<string, bool>? petition = null, NameCatalog? catalog = null, ChatWordFilter? wordFilter = null, PlayerbotChatSafetyFilter? safety = null)
        => new(new GuildOptions(), blacklist, antispam, taken ?? (_ => false), petition ?? (_ => false), catalog, wordFilter, safety);

    private static PlayerbotChatSafetyFilter SafetyFilterFlagging(string term, bool pattern = false)
    {
        string path = Path.Combine(Path.GetTempPath(), "arcane-guildname-terms-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ \"categories\": { \"Harassment\": { \"" + (pattern ? "patterns" : "terms") + "\": [ \"" + term + "\" ] } } }");
        try
        {
            PlayerbotChatSafetyFilter filter = PlayerbotChatSafetyFilter.Load(path, replaceDefaults: true);
            Assert.Empty(filter.Problems);
            return filter;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Generate_ProducesNamesThatPassTheCharterRules()
    {
        var options = new GuildOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int seed = 0; seed < 300; seed++)
        {
            var random = new Random(seed);
            for (int i = 0; i < 20; i++)
            {
                string name = PlayerbotGuildNames.Generate(random);
                seen.Add(name);
                Assert.True(CharterNameRules.IsValid(name, options, null), name);
                Assert.InRange(name.Length, 4, GuildOptions.MaxCharterNameLength);
                Assert.Matches("^[A-Za-z]+( [A-Za-z]+)*$", name); // letters and single spaces only
                Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(Rules(), name));
            }
        }

        Assert.True(seen.Count > 200, $"only {seen.Count} distinct names in 6000 draws");
        Assert.Equal(PlayerbotGuildNames.Generate(new Random(77)), PlayerbotGuildNames.Generate(new Random(77))); // seeded, so reproducible
        Assert.Throws<ArgumentNullException>(() => PlayerbotGuildNames.Generate(null!));
    }

    [Fact]
    public void Screen_AcceptsAGoodName_AndAppliesTheCharterRulesAtTheirBoundaries()
    {
        PlayerbotGuildNameRules rules = Rules();
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(rules, Good));
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(rules, new string('A', GuildOptions.MaxCharterNameLength)));
        Assert.Equal(PlayerbotGuildNameVerdict.Invalid, PlayerbotGuildNames.Screen(rules, new string('A', GuildOptions.MaxCharterNameLength + 1)));
        Assert.Equal(PlayerbotGuildNameVerdict.Invalid, PlayerbotGuildNames.Screen(rules, "A")); // under the 2 character minimum
        Assert.Equal(PlayerbotGuildNameVerdict.Invalid, PlayerbotGuildNames.Screen(rules, string.Empty));
        Assert.Equal(PlayerbotGuildNameVerdict.Invalid, PlayerbotGuildNames.Screen(rules, " Silver Wolves"));
        Assert.Equal(PlayerbotGuildNameVerdict.Invalid, PlayerbotGuildNames.Screen(rules, "Silver Wolves "));
        Assert.Equal(PlayerbotGuildNameVerdict.Invalid, PlayerbotGuildNames.Screen(rules, "Silver  Wolves")); // two spaces
        Assert.Equal(PlayerbotGuildNameVerdict.Invalid, PlayerbotGuildNames.Screen(rules, "Silver Wolves 2")); // a digit passes the charter rules but not the bots'
        Assert.Equal(PlayerbotGuildNameVerdict.Invalid, PlayerbotGuildNames.Screen(rules, "Silver Wolves")); // no-break space
        Assert.Equal(PlayerbotGuildNameVerdict.Invalid, PlayerbotGuildNames.Screen(rules, "Silvér Wolves")); // one script, but not plain letters
        Assert.Equal(PlayerbotGuildNameVerdict.Invalid, PlayerbotGuildNames.Screen(rules, "Silver Вolves")); // mixed Latin and Cyrillic
        Assert.Throws<ArgumentNullException>(() => PlayerbotGuildNames.Screen(rules, null!));

        // The reserved charter list is part of the charter rules, matched on the lower-cased name.
        Assert.Equal(PlayerbotGuildNameVerdict.Invalid, PlayerbotGuildNames.Screen(Rules(blacklist: new Reserved("silver wolves")), Good));
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(Rules(blacklist: new Reserved("silver wolf")), Good));
    }

    [Fact]
    public async Task Screen_RejectsATakenName_CaseInsensitive()
    {
        // The rule is built from the live guild manager (vmangos GuildMgr::GetGuildByName compares without case).
        await using WorldTestHost host = await SocialTestSupport.StartAsync();
        await using WorldTestClient founder = await host.EnterWorldAsync("NAMETAKEN", "Nametaken");
        uint leader = await SocialTestSupport.CharacterIdAsync(host, "Nametaken");

        await host.OnWorldAsync(() =>
        {
            Assert.Equal(GuildAdminResult.Ok, SocialTestSupport.Social(host).Context.Guilds.Create(leader, Good, out _));
            PlayerbotGuildNameRules rules = SocialTestSupport.NewPass(host).NameRules();
            Assert.Equal(PlayerbotGuildNameVerdict.Taken, PlayerbotGuildNames.Screen(rules, Good));
            Assert.Equal(PlayerbotGuildNameVerdict.Taken, PlayerbotGuildNames.Screen(rules, "SILVER WOLVES"));
            Assert.Equal(PlayerbotGuildNameVerdict.Taken, PlayerbotGuildNames.Screen(rules, "silver wolves"));
            Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(rules, "Silver Wolf")); // a different name
        });
    }

    [Fact]
    public async Task Screen_RejectsANameThatAPendingPetitionHolds()
    {
        await using WorldTestHost host = await SocialTestSupport.StartAsync();
        await using WorldTestClient owner = await host.EnterWorldAsync("NAMEPETITION", "Namepetition");
        uint ownerId = await SocialTestSupport.CharacterIdAsync(host, "Namepetition");

        await host.OnWorldAsync(() =>
        {
            SocialTestSupport.Social(host).Context.Petitions.Load([new PetitionData(990, (int)ownerId, 0, "Amber Foxes", [])]);
            Assert.NotNull(SocialTestSupport.Social(host).Context.Petitions.GetByOwner(ownerId));
            PlayerbotGuildNameRules rules = SocialTestSupport.NewPass(host).NameRules();
            Assert.Equal(PlayerbotGuildNameVerdict.PetitionHeld, PlayerbotGuildNames.Screen(rules, "Amber Foxes"));
            Assert.Equal(PlayerbotGuildNameVerdict.PetitionHeld, PlayerbotGuildNames.Screen(rules, "AMBER FOXES"));
            Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(rules, "Amber Fox"));
        });
    }

    [Fact]
    public void Screen_RejectsANameTheCharterAntispamFilterCallsSpam()
    {
        // vmangos-style antispam: compared without case and without spaces.
        var antispam = new PatternCharterAntispamFilter(["silverwolves"]);
        Assert.Equal(PlayerbotGuildNameVerdict.Spam, PlayerbotGuildNames.Screen(Rules(antispam: antispam), Good));
        Assert.Equal(PlayerbotGuildNameVerdict.Spam, PlayerbotGuildNames.Screen(Rules(antispam: antispam), "The SilverWolves"));
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(Rules(antispam: antispam), "Amber Foxes"));
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(Rules(), Good)); // no filter installed, no check

        // The first refusal is the verdict: a taken name is Taken before it is Spam.
        Assert.Equal(PlayerbotGuildNameVerdict.Taken, PlayerbotGuildNames.Screen(Rules(antispam: antispam, taken: _ => true), Good));
    }

    [Fact]
    public void Screen_RejectsANameWithAProfaneOrReservedWord()
    {
        // Invented neutral stand-ins for the catalog's patterns: the catalog is checked on the whole name and on each word.
        var catalog = new NameCatalog(
            profane: [new Regex("^wolves$", RegexOptions.IgnoreCase)], reserved: [new Regex("^foxes$", RegexOptions.IgnoreCase)], sources: [],
            reservedExact: new HashSet<string> { "amber banner" });

        Assert.Equal(PlayerbotGuildNameVerdict.NameCatalog, PlayerbotGuildNames.Screen(Rules(catalog: catalog), Good)); // only the word "Wolves" matches
        Assert.Equal(PlayerbotGuildNameVerdict.NameCatalog, PlayerbotGuildNames.Screen(Rules(catalog: catalog), "Amber Foxes")); // a reserved word
        Assert.Equal(PlayerbotGuildNameVerdict.NameCatalog, PlayerbotGuildNames.Screen(Rules(catalog: catalog), "Amber Banner")); // the whole name is reserved
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(Rules(catalog: catalog), "Amber Hearth"));
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(Rules(catalog: null), Good));

        // The chat word filter's name-scope patterns reach the catalog the same way (NameCatalog.WithFilterProfane).
        NameCatalog withFilter = NameCatalog.Empty.WithFilterProfane([new Regex("hearth", RegexOptions.IgnoreCase)]);
        Assert.Equal(PlayerbotGuildNameVerdict.NameCatalog, PlayerbotGuildNames.Screen(Rules(catalog: withFilter), "Amber Hearth"));
    }

    [Fact]
    public void Screen_RejectsANameTheChatWordFilterCensorsOrBlocks()
    {
        var censor = new ChatWordFilter([new ChatWordFilterRule(1, "wolves", ChatWordFilterScope.Chat, ChatWordFilterAction.Censor)]);
        var block = new ChatWordFilter([new ChatWordFilterRule(2, "wolves", ChatWordFilterScope.Chat, ChatWordFilterAction.Block)]);
        Assert.Equal("Silver ******", censor.Apply(Good)); // what the filter would do to the name in chat
        Assert.Null(block.Apply(Good));

        Assert.Equal(PlayerbotGuildNameVerdict.WordFilter, PlayerbotGuildNames.Screen(Rules(wordFilter: censor), Good));
        Assert.Equal(PlayerbotGuildNameVerdict.WordFilter, PlayerbotGuildNames.Screen(Rules(wordFilter: block), Good));
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(Rules(wordFilter: censor), "Amber Foxes")); // comes back unchanged
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(Rules(wordFilter: ChatWordFilter.Empty), Good));
    }

    [Fact]
    public void Screen_RejectsANameTheBotChatSafetyFilterFlags()
    {
        PlayerbotChatSafetyFilter flagging = SafetyFilterFlagging("silver wolves");
        Assert.True(flagging.Screen(Good, PlayerbotChatSafetyCategories.All).Flagged);
        Assert.Equal(PlayerbotGuildNameVerdict.Unsafe, PlayerbotGuildNames.Screen(Rules(safety: flagging), Good));
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(Rules(safety: flagging), "Amber Foxes"));

        // Every category counts for a name, whatever categories the chat has narrowed itself to.
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(Rules(safety: PlayerbotChatSafetyFilter.Default), Good));
    }

    [Fact]
    public void TryGenerate_ReturnsAScreenedName_AndSkipsOnesTheScreeningRefuses()
    {
        string first = PlayerbotGuildNames.Generate(new Random(5));
        string? name = PlayerbotGuildNames.TryGenerate(Rules(taken: candidate => candidate == first), new Random(5));
        Assert.NotNull(name);
        Assert.NotEqual(first, name);
        Assert.Equal(PlayerbotGuildNameVerdict.Ok, PlayerbotGuildNames.Screen(Rules(), name));
        Assert.Equal(first, PlayerbotGuildNames.TryGenerate(Rules(), new Random(5))); // nothing refused, the first draw stands
    }

    [Fact]
    public void TryGenerate_FailsClosed_WhenEveryCandidateIsRejected()
    {
        int checks = 0;
        string? name = PlayerbotGuildNames.TryGenerate(Rules(taken: _ =>
        {
            checks++;
            return true;
        }), new Random(3));

        Assert.Null(name);
        Assert.Equal(PlayerbotGuildNames.MaxAttempts, checks); // bounded: it gives up, it does not loop

        // A filter that refuses every name does the same.
        PlayerbotChatSafetyFilter refusesAll = SafetyFilterFlagging("[a-z]", pattern: true);
        Assert.Null(PlayerbotGuildNames.TryGenerate(Rules(safety: refusesAll), new Random(3)));
        Assert.Throws<ArgumentNullException>(() => PlayerbotGuildNames.TryGenerate(null!, new Random(3)));
    }
}
