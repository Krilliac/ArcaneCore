using ArcaneCore.Game;
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
    public void TheCreatureReportersOwnSubject_NamesTheCreatureGuid()
    {
        Assert.True(MapFeedAnomalies.TryGuid(ArcaneCore.Game.Creatures.CreatureAnomalies.Subject(3100, 812), out ulong creature));
        Assert.Equal(ObjectGuid.WithEntry(HighGuid.Unit, 3100, 812).Value, creature);
    }

    [Fact]
    public void ABotOrPlayerSubject_NamesThePlayerGuid_WhateverTheNameHolds()
    {
        Assert.True(MapFeedAnomalies.TryGuid("bot Thrall (guid 1234)", out ulong bot));
        Assert.Equal(ObjectGuid.Player(1234).Value, bot);

        Assert.True(MapFeedAnomalies.TryGuid("player Name With (parens) (guid 7)", out ulong player));
        Assert.Equal(ObjectGuid.Player(7).Value, player);
    }

    [Theory]
    [InlineData("")]
    [InlineData("something")]
    [InlineData("bot Thrall (guid 0)")]
    [InlineData("bot Thrall (guid abc)")]
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
