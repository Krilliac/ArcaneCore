using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Host.Public;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>The guest data policy (design 0.6): one test per setting value, and the linked-entity tests (a) to (g).</summary>
public sealed class GuestFrameProjectorTests
{
    private const ulong Zed = 0x0000_0000_0000_0A01;
    private const ulong Amy = 0x0000_0000_0000_0A02;
    private static readonly byte[] Key = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    private static LinksSnapshot Links(long seq, IEnumerable<ulong> linked, IEnumerable<uint>? summons = null, IEnumerable<uint>? objects = null)
        => new(true, seq, new HashSet<ulong>(linked), new HashSet<uint>(summons ?? []), new HashSet<uint>(objects ?? []));

    private static string Bytes(GuestProjection projection, byte[]? key = null)
    {
        var writer = new ArrayBufferWriter<byte>();
        GuestFrameProjector.WriteFrame(writer, projection, key ?? Key);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    private static IReadOnlyList<ulong> Guids(GuestProjection projection) => [.. projection.Entities.Select(e => e.Guid)];

    private static LiveEntity Player(ulong id, string name, double x, double y) => Frames.Entity(id, LiveKind.Player, name, x, y);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ShowPlayersFalse_NoPlayerAppearsAnywhere_WhateverTheNamesSetting(bool showPlayers, bool names)
    {
        var time = new ManualTime();
        var projector = new GuestFrameProjector(new PublicGuestOptions { ShowPlayers = showPlayers, ShowPlayerNames = names }, time);
        LiveFrame frame = Frames.Frame(10, Player(0xDEADBEEF12345678, "Secretname", 4441.5, 5551.5), Frames.Entity(100, LiveKind.Creature, "Boar", 300, 300, entry: 5));

        GuestProjection projection = projector.Project(frame, Links(10, []));
        string text = Bytes(projection);

        Assert.Equal([100UL], Guids(projection));
        Assert.DoesNotContain("Secretname", text, StringComparison.Ordinal);
        Assert.DoesNotContain("4441", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5551", text, StringComparison.Ordinal);
        AssertNoGuid(text, 0xDEADBEEF12345678);
        Assert.Contains("\"Boar\"", text, StringComparison.Ordinal);
        Assert.Empty(projection.Cells);
        Assert.False(projection.Reduced);
    }

    [Fact]
    public void ShowPlayersTrue_NamesOff_PlayersAreCharactersCalledPlayer_WithOpaqueIds()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions { ShowPlayers = true, ShowPlayerNames = false }, new ManualTime());
        LiveFrame frame = Frames.Frame(10, Player(0xDEADBEEF12345678, "Secretname", 4441.5, 5551.5));

        string text = Bytes(projector.Project(frame, Links(10, [])));

        JsonNode entity = JsonNode.Parse(text)!["data"]!["entities"]![0]!;
        Assert.Equal("character", (string?)entity["k"]);
        Assert.Equal("Player", (string?)entity["n"]);
        Assert.Equal(4441.5, (double)entity["x"]!);
        Assert.DoesNotContain("Secretname", text, StringComparison.Ordinal);
        AssertNoGuid(text, 0xDEADBEEF12345678);
        Assert.Equal(GuestFrameProjector.OpaqueId(Key, 0xDEADBEEF12345678), (string?)entity["g"]);
        Assert.Empty(JsonNode.Parse(text)!["data"]!["playerCells"]!.AsArray());
    }

    [Fact]
    public void ShowPlayersTrue_NamesOn_PlayersCarryTheirNames()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions { ShowPlayers = true, ShowPlayerNames = true }, new ManualTime());
        LiveFrame frame = Frames.Frame(10, Player(0xDEADBEEF12345678, "Secretname", 4441.5, 5551.5));

        string text = Bytes(projector.Project(frame, Links(10, [])));

        Assert.Equal("Secretname", (string?)JsonNode.Parse(text)!["data"]!["entities"]![0]!["n"]);
        AssertNoGuid(text, 0xDEADBEEF12345678);
    }

    [Fact]
    public void ShowPlayersTrue_LinkedEntitiesAreNotHidden_AndLinksAreNotNeeded()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions { ShowPlayers = true }, new ManualTime());
        LiveFrame frame = Frames.Frame(10, Player(Zed, "Zed", 100, 100), Frames.Entity(200, LiveKind.Bot, "Botty", 7771.337, 2222), Frames.Entity(201, LiveKind.GameObject, "Chest", 5, 5, entry: 9));

        GuestProjection projection = projector.Project(frame, LinksSnapshot.Unsupported);

        Assert.Equal(3, projection.Entities.Count);
        Assert.False(projection.Reduced);
    }

    [Fact]
    public void Players_AreCountedIntoCoarseCells_ThatAppearOnlyAfterTheDelay_AndOnlyAtTheMinimumCount()
    {
        var time = new ManualTime();
        var projector = new GuestFrameProjector(new PublicGuestOptions(), time);
        LiveFrame crowd = Frames.Frame(10, Player(1, "A", 4441.5, 5551.5), Player(2, "B", 4442.5, 5552.5), Player(3, "C", 4443.5, 5553.5), Player(4, "Lone", 100.5, 100.5));

        GuestProjection early = projector.Project(crowd, Links(10, []));
        time.Advance(TimeSpan.FromSeconds(119));
        GuestProjection stillEarly = projector.Project(crowd, Links(10, []));
        time.Advance(TimeSpan.FromSeconds(2));
        GuestProjection late = projector.Project(crowd, Links(10, []));
        string text = Bytes(late);

        Assert.Empty(early.Cells);
        Assert.Empty(stillEarly.Cells);
        GuestCell cell = Assert.Single(late.Cells); // the lone player's cell has fewer than 3
        Assert.Equal(3, cell.Count);
        Assert.Equal(4375, cell.X); // centre of the 250-yard cell, not any player's position
        Assert.Equal(5625, cell.Y);
        Assert.Equal(250, late.CellYards);
        Assert.DoesNotContain("4441", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5551", text, StringComparison.Ordinal);
        Assert.DoesNotContain("100.5", text, StringComparison.Ordinal);
        Assert.Empty(late.Entities);
    }

    [Fact]
    public void ACellShowsTheOldSnapshot_NotTheCurrentOne()
    {
        var time = new ManualTime();
        var projector = new GuestFrameProjector(new PublicGuestOptions { PlayerMinCellCount = 1 }, time);
        projector.Project(Frames.Frame(1, Player(1, "A", 100, 100)), Links(1, []));
        time.Advance(TimeSpan.FromSeconds(130));

        GuestProjection projection = projector.Project(Frames.Frame(2, Player(1, "A", 3100, 3100)), Links(2, []));

        GuestCell cell = Assert.Single(projection.Cells);
        Assert.Equal(125, cell.X); // where the player was 130 seconds ago
    }

    [Fact]
    public void A_HunterPet_OfARealPlayer_IsRemoved_AndUnrelatedEntitiesAreNot()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime());
        LiveFrame frame = Frames.Frame(10, Player(Zed, "Zed", 100, 100), Frames.Entity(500, LiveKind.Creature, "Wolf", 7771.337, 2222.5, entry: 17), Frames.Entity(501, LiveKind.Creature, "Boar", 300, 300, entry: 5));

        GuestProjection projection = projector.Project(frame, Links(10, [500]));
        string text = Bytes(projection);

        Assert.Equal([501UL], Guids(projection));
        AssertPositionAbsent(text, "7771.337");
        AssertPositionAbsent(text, "2222.5");
        Assert.Contains("300", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACreature_ChasingARealPlayer_IsRemoved_AndReappearsOnlyAfterTheDelay()
    {
        var time = new ManualTime();
        var projector = new GuestFrameProjector(new PublicGuestOptions { PlayerDelaySeconds = 120 }, time);
        LiveEntity chaser = Frames.Entity(600, LiveKind.Creature, "Gnoll", 6661.337, 1111.5, motion: "chase", target: Zed, entry: 30);

        GuestProjection first = projector.Project(Frames.Frame(10, Player(Zed, "Zed", 100, 100), chaser), Links(10, []));
        time.Advance(TimeSpan.FromSeconds(119));
        GuestProjection afterZedLeft = projector.Project(Frames.Frame(11, chaser), Links(11, []));
        time.Advance(TimeSpan.FromSeconds(2));
        GuestProjection afterDelay = projector.Project(Frames.Frame(12, chaser), Links(12, []));

        Assert.Empty(first.Entities);
        Assert.Empty(afterZedLeft.Entities); // Zed is gone but the delay has not passed
        AssertPositionAbsent(Bytes(first), "6661.337");
        AssertPositionAbsent(Bytes(afterZedLeft), "6661.337");
        Assert.Equal([600UL], Guids(afterDelay));
        Assert.Equal("moving", afterDelay.Entities[0].Motion); // its target is not an emitted creature: no chase
        Assert.DoesNotContain("chase", Bytes(afterDelay), StringComparison.Ordinal);
    }

    [Fact]
    public void Bots_FollowingOrGroupedWithARealPlayer_AreRemoved()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime());
        LiveFrame frame = Frames.Frame(
            10,
            Player(Amy, "Amy", 100, 100),
            Frames.Entity(700, LiveKind.Bot, "Follower", 7001.25, 7002.25, motion: "follow", target: Amy),
            Frames.Entity(701, LiveKind.Bot, "Grouped", 8001.25, 8002.25),
            Frames.Entity(702, LiveKind.Bot, "Free", 9001.25, 9002.25));

        GuestProjection projection = projector.Project(frame, Links(10, [700, 701]));
        string text = Bytes(projection);

        Assert.Equal([702UL], Guids(projection));
        AssertPositionAbsent(text, "7001.25");
        AssertPositionAbsent(text, "8001.25");
        Assert.Equal(GuestKind.Character, projection.Entities[0].Kind); // a bot is a character to a guest
        Assert.Contains("9001.25", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AGameObject_OwnedByARealPlayer_IsRemoved()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime());
        LiveFrame frame = Frames.Frame(10, Player(Zed, "Zed", 100, 100), Frames.Entity(800, LiveKind.GameObject, "Trap", 5551.75, 5552.75, entry: 77), Frames.Entity(801, LiveKind.GameObject, "Chest", 20.5, 30.5, entry: 78));

        GuestProjection projection = projector.Project(frame, Links(10, [800], objects: [77]));
        string text = Bytes(projection);

        Assert.Equal([801UL], Guids(projection));
        AssertPositionAbsent(text, "5551.75");
    }

    [Fact]
    public void ACreature_OwnedByTheHunterPet_IsRemoved_Transitively_ByLinksAndByTheFrame()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime());
        LiveFrame frame = Frames.Frame(
            10,
            Player(Zed, "Zed", 100, 100),
            Frames.Entity(500, LiveKind.Creature, "Wolf", 7771.5, 1.5, entry: 17),
            Frames.Entity(510, LiveKind.Creature, "Guardian", 7772.5, 2.5, entry: 18),
            Frames.Entity(511, LiveKind.Creature, "ChasesPet", 7773.5, 3.5, motion: "chase", target: 500, entry: 19),
            Frames.Entity(512, LiveKind.Creature, "ChasesChaser", 7774.5, 4.5, motion: "chase", target: 511, entry: 20),
            Frames.Entity(513, LiveKind.Creature, "Bystander", 40.5, 41.5, entry: 21));

        GuestProjection projection = projector.Project(frame, Links(10, [500, 510]));
        string text = Bytes(projection);

        Assert.Equal([513UL], Guids(projection));
        foreach (string x in new[] { "7771.5", "7772.5", "7773.5", "7774.5" })
        {
            AssertPositionAbsent(text, x);
        }
    }

    [Fact]
    public void ACreature_ChasingABot_KeepsChase_ButOneChasingAPlayerNeverShowsChaseOrFollow()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime());
        LiveEntity bot = Frames.Entity(702, LiveKind.Bot, "Free", 50.5, 60.5);
        LiveEntity chasesBot = Frames.Entity(900, LiveKind.Creature, "ChasesBot", 51.5, 61.5, motion: "chase", target: 702, entry: 40);
        LiveEntity chasesAbsent = Frames.Entity(901, LiveKind.Creature, "ChasesGone", 52.5, 62.5, motion: "follow", target: Zed, entry: 41);

        GuestProjection kept = projector.Project(Frames.Frame(10, bot, chasesBot), Links(10, []));
        GuestProjection collapsed = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime()).Project(Frames.Frame(10, chasesAbsent), Links(10, []));
        GuestProjection hidden = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime()).Project(Frames.Frame(10, Player(Zed, "Zed", 1, 1), chasesAbsent), Links(10, []));

        Assert.Equal("chase", kept.Entities.Single(e => e.Guid == 900).Motion);
        Assert.Equal("moving", collapsed.Entities.Single().Motion);
        Assert.DoesNotContain("follow", Bytes(collapsed), StringComparison.Ordinal);
        Assert.Empty(hidden.Entities);
    }

    [Fact]
    public void WhenLinksIsUnsupported_TheProjectorFailsClosed()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime());
        // A first, supported answer teaches the projector which entries are summons and which objects have owners.
        projector.Project(Frames.Frame(1, Frames.Entity(1, LiveKind.Creature, "Boar", 1, 1, entry: 5)), Links(1, [], summons: [17], objects: [77]));
        LiveFrame frame = Frames.Frame(
            10,
            Player(Zed, "Zed", 100, 100),
            Frames.Entity(700, LiveKind.Bot, "Bot", 7001.25, 7002.25),
            Frames.Entity(710, LiveKind.Creature, "Follower", 7011.25, 7012.25, motion: "follow", entry: 99),
            Frames.Entity(500, LiveKind.Creature, "Wolf", 7771.5, 1.5, entry: 17),
            Frames.Entity(800, LiveKind.GameObject, "Trap", 5551.75, 5552.75, entry: 77),
            Frames.Entity(801, LiveKind.GameObject, "Chest", 20.5, 30.5, entry: 78),
            Frames.Entity(501, LiveKind.Creature, "Boar", 300.5, 301.5, entry: 5));

        GuestProjection projection = projector.Project(frame, LinksSnapshot.Unsupported);
        string text = Bytes(projection);

        Assert.True(projection.Reduced);
        Assert.Equal([801UL, 501UL], Guids(projection)); // the chest's entry is known to be unowned
        foreach (string x in new[] { "7001.25", "7011.25", "7771.5", "5551.75" })
        {
            AssertPositionAbsent(text, x);
        }

        Assert.Contains("\"reduced\":true", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenLinksWasNeverSupported_EveryCreatureNearAHiddenPlayerIsDropped_BecauseNoTemplateSetIsKnown()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime());
        LiveFrame frame = Frames.Frame(
            10,
            Player(Zed, "Zed", 100.25, 100.5),
            Frames.Entity(500, LiveKind.Creature, "IdlePet", 103.125, 101.5, motion: "idle", entry: 17), // an idle pet beside the hidden player
            Frames.Entity(501, LiveKind.Creature, "Totem", 112.75, 95.5, entry: 18), // a totem planted a few yards off
            Frames.Entity(502, LiveKind.Creature, "Boar", 4000.5, 4001.5, entry: 5)); // nowhere near

        GuestProjection projection = projector.Project(frame, LinksSnapshot.Unsupported); // never any earlier answer: no summon set
        string text = Bytes(projection);

        Assert.True(projection.Reduced);
        Assert.Equal([502UL], Guids(projection));
        foreach (string x in new[] { "103.12", "112.75", "100.25", "100.5" })
        {
            AssertPositionAbsent(text, x);
        }
    }

    [Fact]
    public void WithNoOwnedObjectListYet_AFailClosedProjectionDropsEveryGameObject_AndAStaleLinksAnswerIsTheSame()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime());
        LiveFrame frame = Frames.Frame(10, Frames.Entity(801, LiveKind.GameObject, "Chest", 20.5, 30.5, entry: 78), Frames.Entity(501, LiveKind.Creature, "Boar", 300.5, 301.5, entry: 5));

        GuestProjection unknown = projector.Project(frame, LinksSnapshot.Unsupported);
        GuestProjection stale = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime()).Project(frame, Links(7, [])); // 3 frames old
        GuestProjection fresh = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime()).Project(frame, Links(8, [])); // 2 frames old: usable

        Assert.Equal([501UL], Guids(unknown));
        Assert.Equal([501UL], Guids(stale));
        Assert.True(stale.Reduced);
        Assert.Equal(2, fresh.Entities.Count);
        Assert.False(fresh.Reduced);
    }

    [Fact]
    public void OpaqueIds_AreStableWithinASession_DifferBetweenSessions_AndHideTheGuid()
    {
        byte[] other = [.. Enumerable.Range(100, 32).Select(i => (byte)i)];

        string a = GuestFrameProjector.OpaqueId(Key, 0x1122334455667788);
        string again = GuestFrameProjector.OpaqueId(Key, 0x1122334455667788);
        string b = GuestFrameProjector.OpaqueId(other, 0x1122334455667788);
        string c = GuestFrameProjector.OpaqueId(Key, 0x1122334455667789);

        Assert.Equal(16, a.Length);
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.Equal(a, again);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.DoesNotContain("1122334455667788", a, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFrame_NeverCarriesHomes_Targets_OrTheGuidFields()
    {
        var projector = new GuestFrameProjector(new PublicGuestOptions(), new ManualTime());
        LiveFrame frame = Frames.Frame(10, Frames.Entity(100, LiveKind.Creature, "Boar", 300, 300, entry: 5, motion: "chase", target: 101, home: new ArcaneCore.Atlas.Core.WorldPoint(9991.5, 9992.5, 1)), Frames.Entity(101, LiveKind.Creature, "Kodo", 310, 310, entry: 6));

        string text = Bytes(projector.Project(frame, Links(10, [])));
        JsonNode entity = JsonNode.Parse(text)!["data"]!["entities"]![0]!;

        Assert.DoesNotContain("9991", text, StringComparison.Ordinal);
        Assert.Null(entity["tg"]);
        Assert.Null(entity["home"]);
        Assert.Equal("chase", (string?)entity["mt"]); // its target is an emitted non-player creature
    }

    private static void AssertNoGuid(string text, ulong guid)
    {
        Assert.DoesNotContain(guid.ToString("x", System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(guid.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
    }

    /// <summary>The exact float text and the value rounded to one yard must appear nowhere in the bytes.</summary>
    private static void AssertPositionAbsent(string text, string exact)
    {
        Assert.DoesNotContain(exact, text, StringComparison.Ordinal);
        string rounded = Math.Round(double.Parse(exact, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.DoesNotContain(rounded + ".", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":" + rounded + ",", text, StringComparison.Ordinal);
    }
}
