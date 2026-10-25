using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Ops.MapFeed;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// Which guid an anomaly subject names (docs/areas/atlas-insight.md, "Anomalies"). The ring resolves positions from this on the world
/// thread, so a wrong parse would put a marker on the wrong object; the forms are the ledger's documented subject forms.
/// </summary>
public sealed class MapFeedSubjectGuidTests
{
    [Fact]
    public void ASpawnSubject_NamesTheCreatureOrGameObjectGuid()
    {
        Assert.True(MapFeedAnomalies.TryGuid("creature 3100 spawn 812", out ulong creature));
        Assert.Equal(ObjectGuid.WithEntry(HighGuid.Unit, 3100, 812).Value, creature);

        Assert.True(MapFeedAnomalies.TryGuid("gameobject 2843 spawn 77001 (stuck)", out ulong chest));
        Assert.Equal(ObjectGuid.WithEntry(HighGuid.GameObject, 2843, 77001).Value, chest);
    }

    [Fact]
    public void AGameObjectSubject_CanAlsoBeATransport_WhoseGuidCarriesTheTransportHighPart()
    {
        // GameObject.HighGuidOf: an elevator or tram (template type 11) is HighGuid.Transport. The subject does not say, so the ring tries both.
        Assert.True(MapFeedAnomalies.TryTransportGuid("gameobject entry 176080 spawn 88001", out ulong transport));
        Assert.Equal(ObjectGuid.WithEntry(HighGuid.Transport, 176080, 88001).Value, transport);
        Assert.Equal(GameObject.HighGuidOf((uint)GameObjectType.Transport), new ObjectGuid(transport).High);

        Assert.True(MapFeedAnomalies.TryGuid("gameobject entry 176080 spawn 88001", out ulong plain));
        Assert.Equal(GameObject.HighGuidOf((uint)GameObjectType.Chest), new ObjectGuid(plain).High);
        Assert.NotEqual(plain, transport);

        Assert.False(MapFeedAnomalies.TryTransportGuid("creature 3100 spawn 812", out ulong none));
        Assert.Equal(0ul, none);
        Assert.False(MapFeedAnomalies.TryTransportGuid("bot Thrall (guid 4D2)", out _));
    }

    [Fact]
    public void TheCreatureReportersOwnSubject_NamesTheCreatureGuid()
    {
        Assert.True(MapFeedAnomalies.TryGuid(ArcaneCore.Game.Creatures.CreatureAnomalies.Subject(3100, 812), out ulong creature));
        Assert.Equal(ObjectGuid.WithEntry(HighGuid.Unit, 3100, 812).Value, creature);
    }

    [Fact]
    public void ABotSubject_IsHex_AndAPlayerSubject_IsDecimal_WhateverTheNameHolds()
    {
        Assert.True(MapFeedAnomalies.TryGuid("bot Thrall (guid 4D2)", out ulong bot));
        Assert.Equal(ObjectGuid.Player(0x4D2).Value, bot);

        Assert.True(MapFeedAnomalies.TryGuid("bot Thrall (guid 12)", out ulong digitsOnly));
        Assert.Equal(ObjectGuid.Player(0x12).Value, digitsOnly);

        Assert.True(MapFeedAnomalies.TryGuid("player Name With (parens) (guid 18)", out ulong player));
        Assert.Equal(ObjectGuid.Player(18).Value, player);
    }

    [Theory]
    [InlineData(0x12u)]
    [InlineData(0x1A2u)]
    [InlineData(0xABCDEFu)]
    public void TheBotReporters_OwnSubjects_NameTheBotsGuid(uint low)
    {
        Player bot = PlayerWithGuid(low);

        Assert.True(MapFeedAnomalies.TryGuid(PlayerbotAnomalies.SubjectOf(bot), out ulong fromSubject));
        Assert.Equal(ObjectGuid.Player(low).Value, fromSubject);

        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject.Contains(bot.Name, StringComparison.Ordinal));
        PlayerbotGroupAnomalies.ReportNamed(bot.Name, bot.Guid, "subject-form", "pinning the subject form");
        Assert.True(MapFeedAnomalies.TryGuid(capture.Notices[0].Subject, out ulong fromNamed));
        Assert.Equal(ObjectGuid.Player(low).Value, fromNamed);
    }

    [Theory]
    [InlineData(0x12u)]
    [InlineData(0x1A2u)]
    [InlineData(0xABCDEFu)]
    public void ThePlayerReporters_OwnSubjects_NameThePlayersGuid(uint low)
    {
        Player player = PlayerWithGuid(low);

        Assert.True(MapFeedAnomalies.TryGuid(ServerAnomalies.SubjectOf(player), out ulong server));
        Assert.Equal(ObjectGuid.Player(low).Value, server);

        Assert.True(MapFeedAnomalies.TryGuid(MapCombat.SubjectOf(player), out ulong combat));
        Assert.Equal(ObjectGuid.Player(low).Value, combat);
    }

    [Fact]
    public void TheSpellSystemsSubjects_NameThePlayersGuid_AndLeaveCreaturesWithoutOne()
    {
        Player player = PlayerWithGuid(0x1A2);

        Assert.True(MapFeedAnomalies.TryGuid(SpellSystem.FormatUnitSubject(player), out ulong fromPlayer));
        Assert.Equal(ObjectGuid.Player(0x1A2).Value, fromPlayer);

        // "creature E (guid C)" names the entry and a counter, not a spawn: not a form the ring can place.
        Assert.False(MapFeedAnomalies.TryGuid("creature 3100 (guid 812)", out _));
    }

    private static Player PlayerWithGuid(uint low)
    {
        var record = new CharacterRecord
        {
            Id = (int)low,
            AccountId = (int)low,
            Name = $"Subject{low}",
            Race = 1,
            Class = 1,
            Level = 60,
            ZoneId = 12,
            HomeZoneId = 12,
        };
        return new Player(record, new PlayerAppearance(49, 1, PowerType.Mana, 60, 0, 60, 1000, 1000, 1000), new NullPlayerSession());
    }

    [Theory]
    [InlineData("")]
    [InlineData("something")]
    [InlineData("bot Thrall (guid 0)")]
    [InlineData("bot Thrall (guid xyz)")]
    [InlineData("player Thrall (guid abc)")]
    [InlineData("bot Thrall")]
    [InlineData("npc 3100 spawn 812")]
    [InlineData("creature 3100")]
    [InlineData("creature 3100 spawn")]
    [InlineData("creature 99999999999 spawn 1")]
    [InlineData("creature 1 spawn 99999999999")]
    [InlineData("bot X (guid 99999999999)")]
    [InlineData(" creature 3100 spawn 812")]
    public void AnythingElse_NamesNoGuid(string subject)
    {
        Assert.False(MapFeedAnomalies.TryGuid(subject, out ulong guid));
        Assert.Equal(0ul, guid);
    }

    [Fact]
    public void ASubjectOverThreeHundredChars_IsNotParsed_ButExactlyThreeHundredIs()
    {
        string head = "creature 3100 spawn 812";
        string atLimit = head + new string('x', 300 - head.Length);
        Assert.Equal(300, atLimit.Length);
        Assert.True(MapFeedAnomalies.TryGuid(atLimit, out _));
        Assert.False(MapFeedAnomalies.TryGuid(atLimit + "x", out ulong guid));
        Assert.Equal(0ul, guid);
    }
}
