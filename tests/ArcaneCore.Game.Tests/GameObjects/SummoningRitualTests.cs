using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTypeRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// Summoning rituals (vmangos GameObject::Use GAMEOBJECT_TYPE_SUMMONING_RITUAL, AddUniqueUse, RemoveUniqueUse and FinishRitual,
/// GameObject.cpp:739-831, 1733-1796, 1993-2027) and the summon request of SPELL_EFFECT_SUMMON_PLAYER (Player.cpp:19636-19674).
/// </summary>
public sealed class SummoningRitualTests
{
    private static GameObject OwnedRitual(GameObjectTypeRig rig, uint entry, Player owner, ObjectGuid summonTarget, uint createdBy = 698)
    {
        GameObject ritual = rig.System.Summon(entry, 3, 0, 83.5f, 0, 300)!;
        ritual.SetOwner(owner.Guid);
        ritual.SpellId = createdBy;
        rig.System.BeginRitual(ritual, owner, summonTarget);
        return ritual;
    }

    [Fact]
    public void WarlockRitual_TakesTwoHelpersOfTheOwnersRaid_WhileTheOwnerChannels_ThenSummonsAndEnds()
    {
        GameObjectTypeRig rig = Create([]);
        (Player warlock, _) = rig.Join(1);
        (Player helper1, _) = rig.Join(2, 1, 0);
        (Player helper2, _) = rig.Join(3, 0, 1);
        (Player outsider, _) = rig.Join(4, 1, 1);
        ObjectGuid summoned = ObjectGuid.Player(99);
        GameObject ritual = OwnedRitual(rig, Ritual, warlock, summoned);
        rig.Raids.Add((warlock.Guid, helper1.Guid));
        rig.Raids.Add((warlock.Guid, helper2.Guid));

        // The owner cannot help himself; nobody helps while the owner is not channelling; castersGrouped keeps strangers out.
        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(warlock, ritual.Guid));
        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(helper1, ritual.Guid));
        rig.Spells.Channeling.Add(warlock.Guid);
        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(outsider, ritual.Guid));

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(helper1, ritual.Guid));
        Assert.Empty(rig.Spells.RitualCasts);
        Assert.Equal(GameObjectState.Ready, ritual.State);

        // The third participant: the owner casts the ritual spell at the summon target (the warlock portal only), the ritual finishes.
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(helper2, ritual.Guid));
        Assert.Equal([(ritual, RitualSpell, (Unit)warlock, summoned)], rig.Spells.RitualCasts);
        Assert.Equal(GameObjectState.Active, ritual.State);
        Assert.Equal([(warlock, 698u)], rig.Spells.Cooldowns);
        rig.World.RunTick(50);
        Assert.Null(rig.System.Find(ritual.Guid));
    }

    [Fact]
    public void Ritual_GoesWithItsOwnersChannel_UnlessItsSpellWentOff()
    {
        GameObjectTypeRig rig = Create([]);
        (Player warlock, _) = rig.Join(1);
        (Player helper, _) = rig.Join(2, 1, 0);
        GameObject ritual = OwnedRitual(rig, Ritual, warlock, default);
        rig.Raids.Add((warlock.Guid, helper.Guid));
        rig.Spells.Channeling.Add(warlock.Guid);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(helper, ritual.Guid));

        // A channel of another spell changes nothing; the creating channel ending takes the ritual away.
        rig.System.OnChannelEnded(warlock, 1234);
        Assert.NotNull(rig.System.Find(ritual.Guid));
        rig.System.OnChannelEnded(warlock, 698);
        Assert.Null(rig.System.Find(ritual.Guid));
    }

    [Fact]
    public void Ritual_GoesWhenItsOwnerLeavesTheMap()
    {
        GameObjectTypeRig rig = Create([]);
        (Player warlock, _) = rig.Join(1);
        GameObject ritual = OwnedRitual(rig, Ritual, warlock, default);

        rig.World.RemovePlayer(warlock);
        rig.World.RunTick(50);
        Assert.Null(rig.System.Find(ritual.Guid));
    }

    [Fact]
    public void WildRitual_TakesAnyone_CastsByTheFirstUser_AndSacrificesARandomParticipant()
    {
        GameObjectTypeRig rig = Create([]);
        (Player first, _) = rig.Join(1);
        (Player second, _) = rig.Join(2, 1, 0);
        GameObject ritual = rig.System.Summon(WildRitual, 3, 0, 83.5f, 0)!;

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(first, ritual.Guid));
        // AddUniqueUse skips only the owner: on a wild ritual every participant channels the animation spell.
        Assert.Equal([(first, RitualAnim, ritual)], rig.Spells.Animations);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(second, ritual.Guid));

        Assert.Equal([(ritual, RitualSpell, (Unit)first, default(ObjectGuid))], rig.Spells.RitualCasts);
        (GameObject source, uint spell, Unit target, Unit? caster) = Assert.Single(rig.Spells.Casts);
        Assert.Equal(SacrificeSpell, spell);
        Assert.Same(target, caster); // the victim casts it on himself
        Assert.Contains(target, new Unit[] { first, second });
        Assert.Same(ritual, source);
    }

    [Fact]
    public void GroupedWildRitual_RefusesAUserOutsideTheFirstUsersRaid()
    {
        GameObjectTypeRig rig = Create([]);
        (Player first, _) = rig.Join(1);
        (Player stranger, _) = rig.Join(2, 1, 0);
        (Player friend, _) = rig.Join(3, 0, 1);
        rig.Raids.Add((first.Guid, friend.Guid));
        GameObject ritual = rig.System.Summon(GroupedRitual, 3, 0, 83.5f, 0)!;

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(first, ritual.Guid));
        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(stranger, ritual.Guid));
        Assert.Empty(rig.Spells.RitualCasts);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(friend, ritual.Guid));
        Assert.Single(rig.Spells.RitualCasts);
    }

    [Fact]
    public void SummonRequest_IsSentAndAcceptedWithinTwoMinutes_ByALivingPlayerOutOfCombat()
    {
        GameObjectTypeRig rig = Create([]);
        (Player warlock, _) = rig.Join(1);
        (Player target, FakeSession session) = rig.Join(2, 30, 0);
        GameObject ritual = rig.System.Summon(Ritual, 3, 0, 83.5f, 0)!;
        session.Clear();

        GameObjectSpellEffects.Offer(target, warlock, ritual, nowMs: 1_000);
        (WorldOpcode _, byte[] payload) = Assert.Single(GameObjectTestKit.Packets(session, WorldOpcode.SmsgSummonRequest));
        Assert.Equal(16, payload.Length);
        Assert.Equal(warlock.Guid.Value, BitConverter.ToUInt64(payload, 0));
        Assert.Equal((uint)Player.SummonAcceptMs, BitConverter.ToUInt32(payload, 12));

        var sink = new RecordingTeleports();
        Assert.False(GameObjectSpellEffects.Accept(target, warlock.Guid, 1_000 + Player.SummonAcceptMs + 1, sink));
        Assert.Empty(sink.Calls);

        GameObjectSpellEffects.Offer(target, warlock, ritual, nowMs: 5_000);
        Assert.True(GameObjectSpellEffects.Accept(target, warlock.Guid, 6_000, sink));
        (uint map, float x, float y, float _) = Assert.Single(sink.Calls);
        Assert.Equal(0u, map);
        Assert.InRange(MathF.Sqrt(((x - ritual.X) * (x - ritual.X)) + ((y - ritual.Y) * (y - ritual.Y))), 0.1f, 3f);
        Assert.Null(target.PendingSummon);
        Assert.False(GameObjectSpellEffects.Accept(target, warlock.Guid, 6_000, sink)); // used up
    }

    private sealed class RecordingTeleports : ITeleportSink
    {
        public List<(uint Map, float X, float Y, float Z)> Calls { get; } = [];

        public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation)
        {
            Calls.Add((mapId, x, y, z));
            return true;
        }

        public bool CanTeleport(Unit unit, uint mapId, float x, float y, float z, float orientation) => true;
    }
}
