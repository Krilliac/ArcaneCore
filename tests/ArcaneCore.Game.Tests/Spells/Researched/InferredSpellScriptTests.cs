using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic.Researched;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Researched;

/// <summary>
/// The four spells of the 2026-10-29 research that no core or public source documents, implemented as best-evidence behaviour:
/// Enigma Blizzard Bonus 26127, ToWoW - Sillithus PvP Flag 30176, Silithyst Cap Reward 31247 and Agro Drones Effect 25153. All of it is inferred,
/// not verified against a retail capture (docs/areas/spells.md); the tests pin the inferred behaviour so a retail capture can change it on purpose.
/// </summary>
public sealed class InferredSpellScriptTests
{
    private const uint Blizzard = 10;
    private const uint Enigma = 26127;
    private const uint ArcaneMissilesLike = 970_111;
    private const uint Kick = 970_110;
    private const uint FamilyMage = 3;
    private const ulong BlizzardFlags = 0x80080;
    private const int ChannelMs = 8000;

    /// <summary>A random whose <c>Next(100)</c> answers a fixed roll and counts how often it is asked; every other draw is the seeded sequence.</summary>
    private sealed class RollRandom(int roll) : Random(1)
    {
        public int Rolls { get; private set; }

        public void Reset() => Rolls = 0;

        public override int Next(int maxValue)
        {
            if (maxValue != 100)
            {
                return base.Next(maxValue);
            }

            Rolls++;
            return roll;
        }
    }

    private static SpellInfo Channel(uint id, ulong familyFlags) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(ChannelMs, 0, ChannelMs),
            SpellVisual = 1,
            School = SpellSchool.Frost,
            PreventionType = 1,
            ChannelInterruptFlags = (SpellAuraInterruptFlags)SpellChannelInterruptFlags.Delay | SpellAuraInterruptFlags.Action,
            SpellFamilyName = FamilyMage,
            SpellFamilyFlags = familyFlags,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellInfo EnigmaBonus() =>
        Spell(Enigma, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.OverrideClassScripts, misc: 4235) with { ItemType = 0x80 }) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
            SpellFamilyName = FamilyMage,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellInfo InterruptEffect() =>
        Spell(Kick, Effect(SpellEffectName.InterruptCast, 0, SpellImplicitTarget.UnitEnemy)) with
        {
            Duration = new SpellDuration(4000, 0, 4000),
            School = SpellSchool.Normal,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static void FlushSpellBatch(SpellTestKit kit) => kit.Advance(kit.System.SpellBatchWindowMs);

    private sealed class EnigmaScene : IDisposable
    {
        public EnigmaScene(int roll, bool withBonus, uint channel = Blizzard)
        {
            Kit = new SpellTestKit(Channel(Blizzard, BlizzardFlags), Channel(ArcaneMissilesLike, 0x200), EnigmaBonus(), InterruptEffect());
            Random = new RollRandom(roll);
            Kit.System.Random = Random;
            (Attacker, _) = Kit.AddPlayer(1);
            FlagForPvp(Attacker);
            (Caster, Session) = Kit.AddHostilePlayer(2, 2);
            Kit.Spellbook.Teach(Caster, Blizzard, ArcaneMissilesLike);
            if (withBonus)
            {
                Assert.Equal(SpellCastResult.CastOk, Kit.System.CastSpell(Caster, Enigma, SpellCastTargets.ForSelf(), triggered: true));
                FlushSpellBatch(Kit);
                Assert.True(Kit.System.HasAura(Caster, Enigma));
            }

            Random.Reset();
            Assert.Equal(SpellCastResult.CastOk, Kit.System.HandleCastRequest(Caster, channel, SpellCastTargets.ForSelf()));
            FlushSpellBatch(Kit);
            Cast = Kit.System.GetState(Caster.Guid)!.CurrentCast!;
            Assert.Equal(SpellCastState.Casting, Cast.State);
            Start = Cast.Timer; // with spell batching on, the batch boundary has already run some of the channel
            Session.Clear();
        }

        public SpellTestKit Kit { get; }

        public RollRandom Random { get; }

        public Player Attacker { get; }

        public Player Caster { get; }

        public FakeSession Session { get; }

        public SpellCast Cast { get; }

        /// <summary>The channel time left when the channel is running and the scene hands over.</summary>
        public int Start { get; private set; }

        public void Hit() => Kit.System.OnDamageTaken(Caster, Attacker, 5, periodic: false);

        public void Interrupt()
        {
            Kit.System.CastSpell(Attacker, Kick, SpellCastTargets.ForUnit(Caster.Guid), triggered: true);
            FlushSpellBatch(Kit);
        }

        public void Dispose() => Kit.Dispose();
    }

    // --- 26127 Enigma Blizzard Bonus ------------------------------------------------------------------------------------------------------

    [Fact]
    public void EnigmaBonus_WinningRoll_IgnoresPushbackAndInterruptForTheWholeChannel()
    {
        using var scene = new EnigmaScene(roll: 29, withBonus: true);
        Assert.Equal(1, scene.Random.Rolls); // one roll, at channel start

        scene.Hit();
        scene.Hit();
        Assert.Equal(scene.Start, scene.Cast.Timer);
        Assert.DoesNotContain(scene.Session.Sent, p => p.Opcode == WorldOpcode.MsgChannelUpdate);

        scene.Interrupt();
        Assert.Same(scene.Cast, scene.Kit.System.GetState(scene.Caster.Guid)!.CurrentCast);
        Assert.Equal(SpellCastState.Casting, scene.Cast.State);
        Assert.False(scene.Kit.System.IsSchoolLocked(scene.Caster, SpellSchool.Frost));
        Assert.Equal(1, scene.Random.Rolls); // the hits and the kick rolled nothing again

        scene.Kit.Advance(ChannelMs + 500);
        Assert.Null(scene.Kit.System.GetState(scene.Caster.Guid)!.CurrentCast); // it ran to its end
        Assert.True(scene.Cast.Completed);
    }

    [Fact]
    public void EnigmaBonus_LosingRoll_PushbackAndInterruptApplyAsEver()
    {
        using var scene = new EnigmaScene(roll: 30, withBonus: true);
        Assert.Equal(1, scene.Random.Rolls);

        scene.Hit();
        Assert.Equal(scene.Start - 1000, scene.Cast.Timer);
        Assert.Contains(scene.Session.Sent, p => p.Opcode == WorldOpcode.MsgChannelUpdate);

        scene.Interrupt();
        Assert.Null(scene.Kit.System.GetState(scene.Caster.Guid)!.CurrentCast);
        Assert.True(scene.Kit.System.IsSchoolLocked(scene.Caster, SpellSchool.Frost));
    }

    [Fact]
    public void EnigmaBonus_WithoutTheAura_RollsNothingAndThePushbackApplies()
    {
        using var scene = new EnigmaScene(roll: 0, withBonus: false);
        Assert.Equal(0, scene.Random.Rolls);

        scene.Hit();
        Assert.Equal(scene.Start - 1000, scene.Cast.Timer);
        scene.Interrupt();
        Assert.Null(scene.Kit.System.GetState(scene.Caster.Guid)!.CurrentCast);
    }

    [Fact]
    public void EnigmaBonus_AnotherMageChannel_RollsNothingAndIsPushedBack()
    {
        using var scene = new EnigmaScene(roll: 0, withBonus: true, channel: ArcaneMissilesLike);
        Assert.Equal(0, scene.Random.Rolls); // the aura's class mask (Blizzard) does not select it

        scene.Hit();
        Assert.Equal(scene.Start - 1000, scene.Cast.Timer);
        scene.Interrupt();
        Assert.Null(scene.Kit.System.GetState(scene.Caster.Guid)!.CurrentCast);
    }

    [Fact]
    public void EnigmaBonus_AWinningChannel_StillEndsWhenTheCasterCancelsIt()
    {
        using var scene = new EnigmaScene(roll: 0, withBonus: true);

        scene.Kit.System.CancelChannel(scene.Caster); // only interrupt effects and pushback are blocked

        Assert.Null(scene.Kit.System.GetState(scene.Caster.Guid)!.CurrentCast);
    }

    [Fact]
    public void EnigmaBonus_TheNextChannel_RollsAgain()
    {
        using var scene = new EnigmaScene(roll: 0, withBonus: true);
        scene.Kit.System.CancelChannel(scene.Caster);
        scene.Kit.Advance(2000);
        scene.Random.Reset();

        Assert.Equal(SpellCastResult.CastOk, scene.Kit.System.HandleCastRequest(scene.Caster, Blizzard, SpellCastTargets.ForSelf()));
        FlushSpellBatch(scene.Kit);

        Assert.Equal(SpellCastState.Casting, scene.Kit.System.GetState(scene.Caster.Guid)!.CurrentCast!.State);
        Assert.Equal(1, scene.Random.Rolls);
    }

    // --- 30176 ToWoW - Sillithus PvP Flag -------------------------------------------------------------------------------------------------

    private static SpellTestKit PvpFlagKit() => ScriptKit(Spell(30176, Effect(SpellEffectName.Dummy, 0)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 });

    private static SpellTestKit ScriptKit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Fact]
    public void SilithusPvpFlag_FlagsAnUnflaggedCarrierOnTheTick()
    {
        using SpellTestKit kit = PvpFlagKit();
        (Player carrier, _) = kit.AddPlayer(1);
        Assert.Equal(UnitFlags.None, carrier.UnitFlags & UnitFlags.Pvp);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(carrier, 30176, SpellCastTargets.ForSelf(), triggered: true));
        FlushSpellBatch(kit);

        Assert.Equal(UnitFlags.Pvp, carrier.UnitFlags & UnitFlags.Pvp);
        Assert.Equal(CombatConstants.PvpFlagTimerMs, carrier.Combat.PvpFlagTimer);
    }

    [Fact]
    public void SilithusPvpFlag_LeavesAnAlreadyFlaggedCarrierAlone()
    {
        using SpellTestKit kit = PvpFlagKit();
        (Player carrier, _) = kit.AddPlayer(1);
        FlagForPvp(carrier);
        carrier.Combat.PvpFlagTimer = 1234;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(carrier, 30176, SpellCastTargets.ForSelf(), triggered: true));
        FlushSpellBatch(kit);

        Assert.Equal(UnitFlags.Pvp, carrier.UnitFlags & UnitFlags.Pvp);
        Assert.Equal(1234u, carrier.Combat.PvpFlagTimer); // not refreshed
    }

    // --- 31247 Silithyst Cap Reward -------------------------------------------------------------------------------------------------------

    private const uint CenarionCircle = 609;

    private static SpellInfo CapReward() => Spell(31247,
        Effect(SpellEffectName.ScriptEffect, 1),
        Effect(SpellEffectName.Unk122, 10, misc: (int)CenarionCircle)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static (ReputationService Service, Player Player) CapRewardSetup(SpellTestKit kit)
    {
        (Player player, _) = kit.AddPlayer(1);
        var factions = new FactionCatalog(
        [
            new FactionRecord(CenarionCircle, 3, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], 0, "Cenarion Circle"),
        ]);
        var service = new ReputationService(factions, roll: () => 0.0) { SendForcedReactions = false };
        ReputationEnvironment.Register(kit.System, service);
        service.GainModifier = ReputationSpellHandlers.GainModifier(kit.System);
        service.Track(player, service.Create(player, CharacterReputationData.Empty));
        service.BuildInitializeFactions(player);
        return (service, player);
    }

    [Fact]
    public void SilithystCapReward_GivesTenCenarionCircleReputation()
    {
        using SpellTestKit kit = ScriptKit(CapReward());
        (ReputationService service, Player player) = CapRewardSetup(kit);
        int before = service.GetReputation(player, CenarionCircle);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 31247, SpellCastTargets.ForSelf(), triggered: true));
        FlushSpellBatch(kit);

        Assert.Equal(10, service.GetReputation(player, CenarionCircle) - before);
    }

    [Fact]
    public void SilithystCapReward_GoesThroughTheSpellRateAndTheDiplomacyAura()
    {
        using SpellTestKit kit = ScriptKit(CapReward(),
            Spell(940003, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModReputationGain)) with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 });
        (ReputationService service, Player player) = CapRewardSetup(kit);
        kit.System.CastSpell(player, 940003, SpellCastTargets.ForSelf(), triggered: true);
        FlushSpellBatch(kit);
        int before = service.GetReputation(player, CenarionCircle);

        kit.System.CastSpell(player, 31247, SpellCastTargets.ForSelf(), triggered: true);
        FlushSpellBatch(kit);

        Assert.Equal(11, service.GetReputation(player, CenarionCircle) - before); // 10 * 110%, as for SPELL_EFFECT_REPUTATION
    }

    [Fact]
    public void SilithystCapReward_DoesNotGiveEffect122AMeaningOfItsOwn()
    {
        using SpellTestKit kit = ScriptKit(CapReward(), Spell(970_200, Effect(SpellEffectName.Unk122, 10, misc: (int)CenarionCircle)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        (ReputationService service, Player player) = CapRewardSetup(kit);
        int before = service.GetReputation(player, CenarionCircle);

        kit.System.CastSpell(player, 970_200, SpellCastTargets.ForSelf(), triggered: true);
        FlushSpellBatch(kit);

        Assert.Equal(before, service.GetReputation(player, CenarionCircle)); // another spell with effect 122 is untouched
    }

    // --- 25153 Agro Drones Effect ---------------------------------------------------------------------------------------------------------

    private const uint SoldierEntry = 15229;
    private const uint OtherEntry = 15230;

    private sealed class DroneScene : IDisposable
    {
        public DroneScene()
        {
            Kit = ScriptKit(Spell(25153, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.LocationCasterSrc, targetB: SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc) with { Radius = 30f })
                with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 });
            Kit.System.Relations = new FakeRelations { Hostile = { } };
            (Victim, _) = Kit.AddPlayer(1, 0, 0);
            (Bystander, _) = Kit.AddPlayer(2, 0, 3);
            Kit.System.Relations = new FakeRelations { Hostile = { Victim.Guid, Bystander.Guid } };
            CreatureTemplate Template(uint entry) => CreatureTestKit.Template(entry, t =>
            {
                t.MinLevelHealth = 100;
                t.MaxLevelHealth = 100;
            });
            Templates = [Template(SoldierEntry), Template(AgroDronesEffectScript.VeknissDroneEntry), Template(OtherEntry)];
            (_, _, Creatures) = CreateAiSystem(CreatureTestKit.Content(Templates, []), new CreatureAiServices { UnitSpells = new SpellSystemUnitSpellQueries(Kit.System) }, world: Kit.World);
            Soldier = Spawn(SoldierEntry, 5);
            Soldier.Combat.Victim = Victim;
        }

        public SpellTestKit Kit { get; }

        public CreatureTemplate[] Templates { get; }

        public CreatureMapSystem Creatures { get; }

        public Player Victim { get; }

        public Player Bystander { get; }

        public Creature Soldier { get; }

        public Creature Spawn(uint entry, float x) => Creatures.SpawnTemporary(Templates.First(t => t.Entry == entry), x, 0, Victim.Z, 0);

        public void Cast()
        {
            Assert.Equal(SpellCastResult.CastOk, Kit.System.CastSpell(Soldier, 25153, SpellCastTargets.ForSelf(), triggered: true));
            FlushSpellBatch(Kit);
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void AgroDrones_ANearIdleDroneAttacksTheSoldiersVictim()
    {
        using var scene = new DroneScene();
        Creature drone = scene.Spawn(AgroDronesEffectScript.VeknissDroneEntry, 12);
        Assert.Null(drone.Combat.Victim);

        scene.Cast();

        Assert.Same(scene.Victim, drone.Combat.Victim);
        Assert.True(drone.Combat.IsInCombat);
    }

    [Fact]
    public void AgroDrones_AFarDroneIsLeftAlone()
    {
        using var scene = new DroneScene();
        Creature far = scene.Spawn(AgroDronesEffectScript.VeknissDroneEntry, 5 + 31); // beyond the 30 yd radius from the soldier

        scene.Cast();

        Assert.Null(far.Combat.Victim);
        Assert.False(far.Combat.IsInCombat);
    }

    [Fact]
    public void AgroDrones_ADeadDroneIsLeftAlone()
    {
        using var scene = new DroneScene();
        Creature dead = scene.Spawn(AgroDronesEffectScript.VeknissDroneEntry, 10);
        scene.Kit.World.GetMap(0).Combat.DealDamage(scene.Bystander, dead, dead.Health + 1, direct: false);
        Assert.False(dead.IsAlive);

        scene.Cast();

        Assert.Null(dead.Combat.Victim);
    }

    [Fact]
    public void AgroDrones_ADroneAlreadyInCombatKeepsItsOwnVictim()
    {
        using var scene = new DroneScene();
        Creature busy = scene.Spawn(AgroDronesEffectScript.VeknissDroneEntry, 10);
        Assert.True(scene.Creatures.AttackStart(busy, scene.Bystander));

        scene.Cast();

        Assert.Same(scene.Bystander, busy.Combat.Victim);
    }

    [Fact]
    public void AgroDrones_OnlyVeknissDronesAreAffected()
    {
        using var scene = new DroneScene();
        Creature other = scene.Spawn(OtherEntry, 10);

        scene.Cast();

        Assert.Null(other.Combat.Victim);
        Assert.False(other.Combat.IsInCombat);
    }

    [Fact]
    public void AgroDrones_NothingHappensWhenTheSoldierHasNoVictim()
    {
        using var scene = new DroneScene();
        Creature drone = scene.Spawn(AgroDronesEffectScript.VeknissDroneEntry, 10);
        scene.Soldier.Combat.Victim = null;

        scene.Cast();

        Assert.Null(drone.Combat.Victim);
    }

    // --- registration -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheScripts_AreRegistered()
    {
        var registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        foreach (uint id in new uint[] { 30176, 31247, 25153 })
        {
            Assert.True(registry.Find(id) is not null, $"spell {id} has no script");
        }
    }
}
