using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>The selector grammar, matching, ordering and cap of the bulk playerbot commands (pure; no world).</summary>
public sealed class PlayerbotFleetSelectionTests
{
    private static PlayerbotStatus Bot(string name, Guid? id = null)
        => new(id ?? Guid.NewGuid(), name, ManagedPlayerbotState.Running, true, PlayerbotGoalKind.Explore, 0, 0, 0, 0, null);

    [Fact]
    public void Parse_ExactNameGuidPrefixAll_CountForceAndToPlayer()
    {
        Assert.True(PlayerbotFleetSelection.TryParse("Wfbotone", FleetVerb.Summon, out FleetRequest name));
        Assert.Equal(FleetSelectorKind.Name, name.Kind);
        Assert.Equal("Wfbotone", name.Selector);
        Assert.Null(name.Count);
        Assert.False(name.Force);
        Assert.Null(name.ToPlayer);

        Guid id = Guid.NewGuid();
        Assert.True(PlayerbotFleetSelection.TryParse($"{id} to Gmone", FleetVerb.Summon, out FleetRequest byId));
        Assert.Equal(FleetSelectorKind.Guid, byId.Kind);
        Assert.Equal(id, byId.BotId);
        Assert.Equal("Gmone", byId.ToPlayer);

        Assert.True(PlayerbotFleetSelection.TryParse("Wfbot* 5 force to Gmone", FleetVerb.Summon, out FleetRequest prefix));
        Assert.Equal(FleetSelectorKind.Prefix, prefix.Kind);
        Assert.Equal("Wfbot", prefix.Text);
        Assert.Equal("Wfbot*", prefix.Selector);
        Assert.Equal(5, prefix.Count);
        Assert.True(prefix.Force);
        Assert.Equal("Gmone", prefix.ToPlayer);

        Assert.True(PlayerbotFleetSelection.TryParse("ALL 3", FleetVerb.Summon, out FleetRequest all));
        Assert.Equal(FleetSelectorKind.All, all.Kind);
        Assert.Equal(3, all.Count);

        Assert.True(PlayerbotFleetSelection.TryParse("Wfbot* Stormwind City to Gmone", FleetVerb.Send, out FleetRequest send));
        Assert.Equal("Stormwind City", send.Head);
        Assert.Equal("Gmone", send.ToPlayer);
        Assert.Equal("Stormwind City to Gmone", send.RawTail);

        Assert.True(PlayerbotFleetSelection.TryParse("Wfbot* to Gmone", FleetVerb.Leash, out FleetRequest leash));
        Assert.Equal("Gmone", leash.ToPlayer);
        Assert.True(PlayerbotFleetSelection.TryParse("all force", FleetVerb.Plain, out FleetRequest plain));
        Assert.True(plain.Force);
    }

    [Theory]
    [InlineData("", "Summon")]
    [InlineData("*", "Summon")]
    [InlineData("Wf*bot", "Summon")]
    [InlineData("*bot", "Summon")]
    [InlineData("W1*", "Summon")]
    [InlineData("Wf**", "Summon")]
    [InlineData("Wf* 0", "Summon")]
    [InlineData("Wf* -2", "Summon")]
    [InlineData("Wf* 2 3", "Summon")]
    [InlineData("Wf* here", "Summon")]
    [InlineData("Wf*", "Send")]
    [InlineData("Wf* extra", "Plain")]
    [InlineData("Wf* to Gm", "Plain")]
    [InlineData("Wf* force", "Leash")]
    public void Parse_RejectsBareStarInnerStarAndNonLetterPrefix(string text, string verb)
        => Assert.False(PlayerbotFleetSelection.TryParse(text, Enum.Parse<FleetVerb>(verb), out _));

    [Fact]
    public void IsMulti_RecognisesAllAndPrefixOnly()
    {
        Assert.True(PlayerbotFleetSelection.IsMulti("all"));
        Assert.True(PlayerbotFleetSelection.IsMulti("  ALL force"));
        Assert.True(PlayerbotFleetSelection.IsMulti("Wf*"));
        Assert.False(PlayerbotFleetSelection.IsMulti("Wfbot"));
        Assert.False(PlayerbotFleetSelection.IsMulti(""));
        Assert.False(PlayerbotFleetSelection.IsMulti(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void Match_PrefixIsCaseInsensitive_AndOnlySeesRegisteredBots()
    {
        // The snapshot is the registered managed bots; a real player named like a bot is simply not in it.
        PlayerbotStatus[] snapshot = [Bot("Wfbottwo"), Bot("WFBOTone"), Bot("Wfbo"), Bot("Other")];
        Assert.True(PlayerbotFleetSelection.TryParse("wfbot*", FleetVerb.Plain, out FleetRequest request));

        List<PlayerbotStatus> found = PlayerbotFleetSelection.Match(request, snapshot);

        Assert.Equal(["WFBOTone", "Wfbottwo"], found.Select(s => s.Name));
        Assert.True(PlayerbotFleetSelection.TryParse("OTHER", FleetVerb.Plain, out FleetRequest exact));
        Assert.Single(PlayerbotFleetSelection.Match(exact, snapshot));
        Assert.True(PlayerbotFleetSelection.TryParse("all", FleetVerb.Plain, out FleetRequest all));
        Assert.Equal(4, PlayerbotFleetSelection.Match(all, snapshot).Count);
        PlayerbotStatus one = snapshot[3];
        Assert.True(PlayerbotFleetSelection.TryParse(one.BotId.ToString(), FleetVerb.Plain, out FleetRequest byId));
        Assert.Equal(one, Assert.Single(PlayerbotFleetSelection.Match(byId, snapshot)));
    }

    [Fact]
    public void Match_NothingMatches_FailsClosedWithNamedMessage()
    {
        Assert.True(PlayerbotFleetSelection.TryParse("Zz*", FleetVerb.Plain, out FleetRequest request));

        Assert.Empty(PlayerbotFleetSelection.Match(request, [Bot("Wfbotone")]));
        Assert.Empty(PlayerbotFleetSelection.Match(request, []));
        Assert.Equal("No playerbot matches 'Zz*'.", PlayerbotFleetSelection.NothingMatches(request));
    }

    [Fact]
    public void Order_NearestOnTargetMapFirst_ThenOthersByName()
    {
        FleetCandidate[] input =
        [
            new(Guid.NewGuid(), "Zeta", false, 0f),
            new(Guid.NewGuid(), "Far", true, 40f),
            new(Guid.NewGuid(), "Alpha", false, 0f),
            new(Guid.NewGuid(), "Near", true, 5f),
            new(Guid.NewGuid(), "Mid", true, 20f),
        ];

        Assert.Equal(["Near", "Mid", "Far", "Alpha", "Zeta"], PlayerbotFleetSelection.Order(input).Select(c => c.Name));
    }

    [Fact]
    public void All_WithoutCountAboveCap_IsRefused_UnlessForce()
    {
        Assert.True(PlayerbotFleetSelection.NeedsForce(60, null, force: false, cap: 50));
        Assert.False(PlayerbotFleetSelection.NeedsForce(60, null, force: true, cap: 50));
        Assert.False(PlayerbotFleetSelection.NeedsForce(60, 50, force: false, cap: 50));
        Assert.True(PlayerbotFleetSelection.NeedsForce(60, 51, force: false, cap: 50));
        Assert.False(PlayerbotFleetSelection.NeedsForce(50, null, force: false, cap: 50));
        Assert.Equal("Playerbot summon: 60 bots match; give a count <= 50 or add 'force'.", PlayerbotFleetSelection.ForceMessage("summon", 60, 50));
    }

    [Fact]
    public void CommandTree_FleetChildrenAreAppendedWithExpectedRanks()
    {
        CommandTable plain = ChatCommands.Build([], [new PlayerbotCommands()], [], new GmOptions());
        CommandTable full = ChatCommands.Build([], [new PlayerbotCommands()], [new PlayerbotFleetCommands()], new GmOptions());

        string[] before = plain.Roots.Single(c => c.Name == "playerbot").SubCommands.Select(c => c.Name).ToArray();
        ChatCommand root = full.Roots.Single(c => c.Name == "playerbot");
        string[] after = root.SubCommands.Select(c => c.Name).ToArray();

        Assert.Equal(before, after.Take(before.Length));
        Assert.Equal(["summon", "send", "follow", "stay", "dismiss", "near", "revive", "fleet"], after.Skip(before.Length));
        foreach (string admin in new[] { "summon", "send", "follow", "stay", "dismiss", "revive", "fleet", "start", "stop" })
            Assert.Equal(AccountSecurity.Administrator, root.SubCommands.Single(c => c.Name == admin).Security);
        Assert.Equal(AccountSecurity.GameMaster, root.SubCommands.Single(c => c.Name == "near").Security);
        // The fleet reset deletes every bot character: its rank is Administrator at the group and at the child.
        ChatCommand fleet = root.SubCommands.Single(c => c.Name == "fleet");
        Assert.Equal(AccountSecurity.Administrator, fleet.SubCommands.Single(c => c.Name == "reset").Security);
        Assert.Contains("summon|send|follow|stay|dismiss|near|revive", root.Help, StringComparison.Ordinal);
    }
}
