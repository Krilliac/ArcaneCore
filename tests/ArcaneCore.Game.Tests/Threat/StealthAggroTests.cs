using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Threat;

/// <summary>
/// Creatures and stealth: a stealthed player the creature cannot detect is not attacked (vmangos CallAIMoveLOS, Maps/GridNotifiersImpl.h:57-69,
/// Unit::CanDetectStealthOf, Objects/Unit.cpp:6543-6616: sniffed, a level 4 creature notices a level 1 rogue inside 3.3 yd and is alerted up to
/// 8.3 yd), and the alert (AI/CreatureAI.cpp:349-385).
/// </summary>
public sealed class StealthAggroTests
{
    private const uint Stealth = 950001;

    private sealed class Rig : IDisposable
    {
        public Rig(float wolfX, Func<CreatureTemplate, CreatureTemplate>? tweak = null, CreatureOptions? options = null)
        {
            Arena = new ThreatArena(Spell(Stealth, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModStealth)) with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 });
            var registry = new StealthRegistry();
            Arena.Kit.System.RegisterAura(AuraType.ModStealth, StealthAuras.Handler(registry));
            StealthServices.Install(Arena.Map, new StealthServices(Arena.Kit.System, registry, StealthOptions.Default));
            Arena.Tank.Level = 1;
            // no relocation notify: the tests call the AI hook themselves
            Wolf = Arena.SpawnCreature(90, wolfX, tweak, options ?? new CreatureOptions { AiRelocationNotifyDelayMs = 3_600_000 });
            Wolf.Level = 4;
            Wolf.Orientation = MathF.PI - 0.3f;
            System = Arena.Systems[^1];
            Arena.TankSession.Clear();
        }

        public ThreatArena Arena { get; }

        public Creature Wolf { get; }

        public CreatureMapSystem System { get; }

        public Player Rogue => Arena.Tank;

        public void Hide() => Arena.Cast(Rogue, Rogue, Stealth);

        public void Reveal() => Arena.Kit.System.RemoveAuras(Rogue, Stealth);

        public void Move(float x) => Wolf.Relocate(x, 0, Wolf.Z, Wolf.Orientation, 0);

        /// <summary>SMSG_AI_REACTION packets of the wolf the rogue received: their reaction values.</summary>
        public List<uint> Reactions()
        {
            var list = new List<uint>();
            foreach ((WorldOpcode opcode, byte[] payload) in Arena.TankSession.Sent)
            {
                if (opcode == WorldOpcode.SmsgAiReaction)
                {
                    var reader = new PacketReader(payload);
                    if (reader.ReadUInt64() == Wolf.Guid.Value)
                    {
                        list.Add(reader.ReadUInt32());
                    }
                }
            }

            return list;
        }

        public void Dispose() => Arena.Dispose();
    }

    [Fact]
    public void AnUnstealthedPlayer_IsAttackedOnSight_AsBefore()
    {
        using var rig = new Rig(8f);

        Assert.True(rig.System.CanAggroOnSight(rig.Wolf, rig.Rogue));
    }

    [Fact]
    public void AStealthedPlayerTheCreatureCannotDetect_IsNotAttacked_UntilItIsCloseOrStealthEnds()
    {
        using var rig = new Rig(8f);
        rig.Hide();

        Assert.False(rig.System.CanAggroOnSight(rig.Wolf, rig.Rogue));

        rig.Move(3.2f); // inside the sniffed 3.33 yd
        Assert.True(rig.System.CanAggroOnSight(rig.Wolf, rig.Rogue));

        rig.Move(8f);
        Assert.False(rig.System.CanAggroOnSight(rig.Wolf, rig.Rogue));
        rig.Reveal();
        Assert.True(rig.System.CanAggroOnSight(rig.Wolf, rig.Rogue));
    }

    [Fact]
    public void TheLineOfSightHook_AttacksADetectedRogue_AndOnlyAlertsForOneItCannotSee()
    {
        using var rig = new Rig(3.2f);
        rig.Hide();
        rig.System.CallAiMoveInLineOfSight(rig.Wolf, rig.Rogue);
        Assert.Same(rig.Rogue, rig.Wolf.Combat.Victim); // detected inside 3.33 yd: the creature attacks
        Assert.DoesNotContain(0u, rig.Reactions());
        Assert.Contains(2u, rig.Reactions());

        using var far = new Rig(6f);
        far.Hide();
        far.System.CallAiMoveInLineOfSight(far.Wolf, far.Rogue);
        Assert.Null(far.Wolf.Combat.Victim);
        Assert.Equal([0u], far.Reactions()); // inside the 5 yd alert band
    }

    [Fact]
    public void TheAlert_TurnsTheCreatureToThePlayer_IsSentOnce_AndWaitsTenSecondsForTheNext()
    {
        using var rig = new Rig(6f);
        rig.Hide();

        rig.System.CallAiMoveInLineOfSight(rig.Wolf, rig.Rogue);
        Assert.Equal([0u], rig.Reactions());
        Assert.Equal(MathF.PI, rig.Wolf.Orientation, 0.01f); // from (6,0) to the rogue at the origin: due west

        rig.System.CallAiMoveInLineOfSight(rig.Wolf, rig.Rogue);
        Assert.Equal([0u], rig.Reactions()); // the cooldown holds

        Run(rig.Arena.Kit.World, 9000);
        rig.System.CallAiMoveInLineOfSight(rig.Wolf, rig.Rogue);
        Assert.Single(rig.Reactions());

        Run(rig.Arena.Kit.World, 1500);
        rig.System.CallAiMoveInLineOfSight(rig.Wolf, rig.Rogue);
        Assert.Equal([0u, 0u], rig.Reactions()); // 10.5 s after the first
    }

    [Fact]
    public void BeyondTheAlertBand_NothingHappens()
    {
        using var rig = new Rig(12f);
        rig.Hide();

        rig.System.CallAiMoveInLineOfSight(rig.Wolf, rig.Rogue);

        Assert.Empty(rig.Reactions());
        Assert.Null(rig.Wolf.Combat.Victim);
    }

    [Fact]
    public void ACivilian_APassiveCreature_AStunnedOne_OneInCombat_AndTheSwitchOff_DoNotAlert()
    {
        using var civilian = new Rig(6f, t => t with { Civilian = true });
        civilian.Hide();
        civilian.System.CallAiMoveInLineOfSight(civilian.Wolf, civilian.Rogue);
        Assert.Empty(civilian.Reactions());

        using var passive = new Rig(6f);
        passive.Hide();
        passive.Wolf.ReactState = CreatureReactState.Passive;
        passive.System.CallAiMoveInLineOfSight(passive.Wolf, passive.Rogue);
        Assert.Empty(passive.Reactions());

        using var stunned = new Rig(6f);
        stunned.Hide();
        stunned.Wolf.UnitFlags |= UnitFlags.Stunned;
        stunned.System.CallAiMoveInLineOfSight(stunned.Wolf, stunned.Rogue);
        Assert.Empty(stunned.Reactions());

        using var fighting = new Rig(6f);
        fighting.Hide();
        fighting.Arena.Map.Combat.SetInCombatState(fighting.Wolf, 0);
        fighting.System.CallAiMoveInLineOfSight(fighting.Wolf, fighting.Rogue);
        Assert.Empty(fighting.Reactions());

        using var off = new Rig(6f, options: new CreatureOptions { AiRelocationNotifyDelayMs = 3_600_000, StealthAlertEnabled = false });
        off.Hide();
        off.System.CallAiMoveInLineOfSight(off.Wolf, off.Rogue);
        Assert.Empty(off.Reactions());
    }
}
