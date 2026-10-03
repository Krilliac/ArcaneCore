using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Death.Resurrection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// Resurrection by spell (vmangos Spell::EffectResurrectNew / EffectResurrect, SpellEffects.cpp:209-263 and 5228-5251;
/// Spell::SendResurrectRequest, Spell.cpp:4934-4944; HandleResurrectResponseOpcode, MiscHandler.cpp:605-622;
/// Player::ResurrectUsingRequestData, Player.cpp:20065-20110). The spells are shaped like classic-db's rows: Resurrection rank 7
/// (10881: effect 113, 500 health, 750 mana, target mask CORPSE_ALLY), Rebirth (20484: AttributesEx3 0x10) and an old
/// percent spell (effect 18, 19%).
/// </summary>
public sealed class ResurrectionRequestTests
{
    private const long T = 1_700_000_000;
    private const uint Resurrection = 10881;
    private const uint Rebirth = 20484;
    private const uint Percent = 2011;

    private static SpellInfo Res(uint id, SpellEffectName effect, int value, int misc, uint ex3 = 0) => SpellTestKit.Spell(id,
        SpellTestKit.Effect(effect, value, SpellImplicitTarget.None, misc: misc)) with
    {
        Targets = (uint)SpellCastTargetFlags.CorpseAlly,
        AttributesEx3 = ex3,
    };

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kit = new SpellTestKit(
                Res(Resurrection, SpellEffectName.ResurrectNew, 500, 750),
                Res(Rebirth, SpellEffectName.ResurrectNew, 400, 700, ex3: 0x10),
                Res(Percent, SpellEffectName.Resurrect, 19, 0));
            DeathHooks.Register(Kit.World, new DeathHooks(new DeathOptions(), new FixedDeathClock(T)));
            WorldMaps.Of(Kit.World).Load(new MapContent([new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")], [], [], [], []));
            Teleports = new TeleportService(Kit.World, _ => { }, _ => { });
            Service = new ResurrectionService(Kit.World, () => Teleports);
            Kit.System.Resurrection = Service;
            CasterSession = new FakeSession(1);
            Caster = CombatTestKit.AddPlayer(Kit.World, 1, 0, 0, CasterSession);
            GhostSession = new FakeSession(2);
            Ghost = CombatTestKit.AddPlayer(Kit.World, 2, 50, 0, GhostSession);
            Kit.World.RunTick(1);
            Ghost.Health = 0;
            Combat.KillPlayer(Ghost);
            Assert.True(Combat.RepopPlayer(Ghost)); // a ghost at its corpse
            CombatTestKit.AckPendingMovement(Ghost);
            Kit.World.RunTick(1);
            GhostSession.Clear();
            CasterSession.Clear();
        }

        public SpellTestKit Kit { get; }

        public TeleportService Teleports { get; }

        public ResurrectionService Service { get; }

        public FakeSession CasterSession { get; }

        public FakeSession GhostSession { get; }

        public Player Caster { get; }

        public Player Ghost { get; }

        public MapCombat Combat => Kit.World.GetMap(0).Combat;

        public SpellCastResult CastAtCorpse(Unit caster, uint spell, ObjectGuid? corpse = null) => Kit.System.CastSpell(
            caster, spell, new SpellCastTargets { Mask = SpellCastTargetFlags.CorpseAlly, Corpse = corpse ?? Ghost.Combat.Corpse!.Guid }, triggered: true);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void ResurrectNew_StoresARequest_WithTheEffectValueAsHealth_AndTheMiscValueAsMana_AndAsksTheGhost()
    {
        using var rig = new Rig();

        Assert.Equal(SpellCastResult.CastOk, rig.CastAtCorpse(rig.Caster, Resurrection));

        ResurrectionRequest request = ResurrectionRequests.Get(rig.Ghost)!;
        Assert.Equal((rig.Caster.Guid, 500u, 750u), (request.Resurrector, request.Health, request.Mana));
        Assert.Equal((0u, 0f, 0f), (request.MapId, request.X, request.Y));
        Assert.False(rig.Ghost.IsAlive); // asked, not resurrected
        byte[] packet = Assert.Single(rig.GhostSession.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest).Payload;
        Assert.Equal(ResurrectionPackets.BuildRequest(rig.Caster.Guid, string.Empty, sickness: false, delayed: true), packet);
    }

    [Fact]
    public void TheRequestPacket_IsGuid_NameLengthPlusOne_Name_Sickness_Delayed()
    {
        byte[] player = ResurrectionPackets.BuildRequest(new ObjectGuid(0x0102030405060708UL), string.Empty, sickness: false, delayed: true);
        byte[] healer = ResurrectionPackets.BuildRequest(new ObjectGuid(7), "Spirit Healer", sickness: true, delayed: false);

        Assert.Equal([8, 7, 6, 5, 4, 3, 2, 1, 1, 0, 0, 0, 0, 0, 1], player);
        Assert.Equal(8 + 4 + 14 + 2, healer.Length);
        Assert.Equal(14u, BitConverter.ToUInt32(healer, 8));
        Assert.Equal("Spirit Healer\0", System.Text.Encoding.UTF8.GetString(healer, 12, 14));
        Assert.Equal([1, 0], healer[^2..]);
    }

    [Fact]
    public void ASpellWithNoResTimer_AsksForAnInstantResurrection()
    {
        using var rig = new Rig();

        rig.CastAtCorpse(rig.Caster, Rebirth);

        byte[] packet = Assert.Single(rig.GhostSession.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest).Payload;
        Assert.Equal(0, packet[^1]); // delayed = false (Rebirth: SPELL_ATTR_EX3_NO_RES_TIMER)
        Assert.Equal(0, packet[^2]);
    }

    [Fact]
    public void ASecondRequest_WhileOneIsPending_IsIgnored()
    {
        using var rig = new Rig();
        rig.CastAtCorpse(rig.Caster, Resurrection);
        Player other = CombatTestKit.AddPlayer(rig.Kit.World, 3, 1, 0, new FakeSession(3));
        rig.GhostSession.Clear();

        rig.CastAtCorpse(other, Rebirth);

        Assert.Equal(rig.Caster.Guid, ResurrectionRequests.Get(rig.Ghost)!.Resurrector);
        Assert.DoesNotContain(rig.GhostSession.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest);
    }

    [Fact]
    public void ResurrectPercent_OffersThatPercentOfTheMaximums()
    {
        using var rig = new Rig();

        rig.CastAtCorpse(rig.Caster, Percent);

        ResurrectionRequest request = ResurrectionRequests.Get(rig.Ghost)!;
        Assert.Equal(190u, request.Health); // 19% of 1000
        Assert.Equal(0u, request.Mana);     // a warrior has no mana
    }

    [Fact]
    public void ACorpseThatIsNotInTheCastersMap_IsBadTargets_AndNothingHappens()
    {
        using var rig = new Rig();

        SpellCastResult result = rig.CastAtCorpse(rig.Caster, Resurrection, corpse: new ObjectGuid(0x0123456789UL));

        Assert.Equal(SpellCastResult.BadTargets, result);
        Assert.Null(ResurrectionRequests.Get(rig.Ghost));
    }

    [Fact]
    public void ALivingPlayer_IsNotOffered()
    {
        using var rig = new Rig();
        var targets = SpellCastTargets.ForUnit(rig.Caster.Guid);

        rig.Kit.System.CastSpell(rig.Caster, Resurrection, targets, triggered: true);

        Assert.Null(ResurrectionRequests.Get(rig.Caster));
    }

    [Fact]
    public void Accepting_TeleportsToThePlayerResurrector_ThenResurrectsWithTheOfferedVitals_AndTheCorpseGoes()
    {
        using var rig = new Rig();
        rig.CastAtCorpse(rig.Caster, Resurrection);
        rig.GhostSession.Clear();

        rig.Service.Respond(rig.Ghost, rig.Caster.Guid, accept: true);

        // "Teleport before resurrecting": the ghost is moved to the caster and is dead until the teleport is acknowledged.
        Assert.False(rig.Ghost.IsAlive);
        TeleportDestination destination = rig.Teleports.DestinationOf(rig.Ghost)!.Value;
        Assert.Equal((0u, 0f, 0f), (destination.MapId, destination.X, destination.Y));
        Assert.True(rig.Teleports.HandleTeleportAck(rig.Ghost, rig.Ghost.Guid.Value));

        Assert.True(rig.Ghost.IsAlive);
        Assert.Equal((0f, 0f), (rig.Ghost.X, rig.Ghost.Y));
        Assert.Equal(500u, rig.Ghost.Health);
        Assert.Equal(0u, (uint)(rig.Ghost.Flags & PlayerFlags.Ghost));
        Assert.Null(rig.Ghost.Combat.Corpse);
        Assert.Empty(rig.Combat.Corpses);
        Assert.Equal(0u, rig.Ghost.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage));
    }

    [Fact]
    public void TheOfferedHealth_IsCappedAtTheMaximum()
    {
        using var rig = new Rig();
        rig.Ghost.MaxHealth = 300;
        rig.CastAtCorpse(rig.Caster, Resurrection);

        rig.Service.Respond(rig.Ghost, rig.Caster.Guid, accept: true);
        rig.Teleports.HandleTeleportAck(rig.Ghost, rig.Ghost.Guid.Value);

        Assert.Equal(300u, rig.Ghost.Health);
    }

    [Fact]
    public void AnAcceptanceForAnotherResurrector_IsIgnored_AndADeclineClearsTheRequest()
    {
        using var rig = new Rig();
        rig.CastAtCorpse(rig.Caster, Resurrection);

        rig.Service.Respond(rig.Ghost, new ObjectGuid(0xDEAD), accept: true);
        Assert.False(rig.Teleports.IsBeingTeleported(rig.Ghost));
        Assert.NotNull(ResurrectionRequests.Get(rig.Ghost));

        rig.Service.Respond(rig.Ghost, rig.Caster.Guid, accept: false);
        Assert.Null(ResurrectionRequests.Get(rig.Ghost));
        Assert.False(rig.Ghost.IsAlive);
    }

    [Fact]
    public void AnAcceptanceByALivingPlayer_IsIgnored()
    {
        using var rig = new Rig();
        rig.CastAtCorpse(rig.Caster, Resurrection);
        ResurrectionRequests.Set(rig.Caster, ResurrectionRequests.Get(rig.Ghost)!);

        rig.Service.Respond(rig.Caster, rig.Caster.Guid, accept: false);

        Assert.NotNull(ResurrectionRequests.Get(rig.Caster)); // alive: the response is not even read
    }

    [Fact]
    public void ACreatureResurrector_IsAskedByName_WithSickness_AndNoTeleportHappens()
    {
        using var rig = new Rig();
        var healer = new CombatTestUnit();
        healer.SetUInt32(UpdateFields.UnitNpcFlags, (uint)NpcFlags.SpiritHealer);
        healer.Spawn(rig.Kit.World.GetMap(0), 10, 0);

        rig.CastAtCorpse(healer, Resurrection);

        byte[] packet = Assert.Single(rig.GhostSession.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest).Payload;
        Assert.Equal(1, packet[^2]); // sickness: the caster is a spirit healer
        rig.Service.Respond(rig.Ghost, healer.Guid, accept: true);
        Assert.False(rig.Teleports.IsBeingTeleported(rig.Ghost));
        Assert.True(rig.Ghost.IsAlive); // at once, where it stands
        Assert.Equal(50f, rig.Ghost.X);
    }

    [Fact]
    public void DyingAgain_ClearsThePendingRequest_ButResurrectingDoesNot()
    {
        using var rig = new Rig();
        rig.CastAtCorpse(rig.Caster, Resurrection);
        rig.Combat.ResurrectPlayer(rig.Ghost, 1f, applySickness: false); // some other way back
        Assert.NotNull(ResurrectionRequests.Get(rig.Ghost));              // vmangos keeps it until the next death (Player.cpp:1522)

        rig.Combat.Kill(null, rig.Ghost);

        Assert.Null(ResurrectionRequests.Get(rig.Ghost));
    }

    [Fact]
    public void WithoutAResurrectionService_TheEffectsDoNothing()
    {
        using var rig = new Rig();
        rig.Kit.System.Resurrection = null;

        Assert.Equal(SpellCastResult.CastOk, rig.CastAtCorpse(rig.Caster, Resurrection));

        Assert.Null(ResurrectionRequests.Get(rig.Ghost));
    }

    [Fact]
    public void TheResponsePacket_IsTheResurrectorGuid_ThenTheStatus()
    {
        byte[] accept = [1, 0, 0, 0, 0, 0, 0, 0x10, 1];
        byte[] decline = [1, 0, 0, 0, 0, 0, 0, 0x10, 0];

        Assert.Equal((new ObjectGuid(0x1000000000000001UL), true), ResurrectionPackets.ReadResponse(accept));
        Assert.Equal((new ObjectGuid(0x1000000000000001UL), false), ResurrectionPackets.ReadResponse(decline));
        Assert.Null(ResurrectionPackets.ReadResponse(accept[..8]));
    }
}
