using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Death.Ghost;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// The ghost aura (vmangos Player::ApplyGhostForm, Player.cpp:4561-4577, and Aura::HandleAuraGhost, SpellAuras.cpp:5639-5659):
/// spell 8326 is auras 95 (ghost), 31 (+25% run) and 58 (+25% swim); the night elf wisp 20584 is 31 and 58 at +50% and a
/// transform. The spells here are shaped like classic-db's spell_template rows for those ids.
/// </summary>
public sealed class GhostFormTests
{
    private const long T = 1_700_000_000;

    private static readonly SpellInfo GhostSpell = SpellTestKit.Spell(CombatConstants.GhostSpellId,
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Ghost),
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModIncreaseSpeed),
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModIncreaseSwimSpeed)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        Attributes = (SpellAttributes)763363584, // classic-db: includes CASTABLE_WHILE_DEAD (0x800000), which a triggered cast by a dead caster needs
        AttributesEx3 = 0x100000, // ALLOW_AURA_WHILE_DEAD, as classic-db has it for 8326 and 20584 (a ghost is dead)
    };

    private static readonly SpellInfo WispSpell = SpellTestKit.Spell(CombatConstants.WispGhostSpellId,
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModIncreaseSpeed),
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModIncreaseSwimSpeed)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        Attributes = (SpellAttributes)763363584, // classic-db: includes CASTABLE_WHILE_DEAD (0x800000), which a triggered cast by a dead caster needs
        AttributesEx3 = 0x100000, // ALLOW_AURA_WHILE_DEAD, as classic-db has it for 8326 and 20584 (a ghost is dead)
    };

    private sealed class Rig : IDisposable
    {
        public Rig(bool aura = true, bool withGhostSpell = true, Race race = Race.Human, bool knowsWisp = false, List<string>? warnings = null)
        {
            Kit = withGhostSpell ? new SpellTestKit(GhostSpell, WispSpell) : new SpellTestKit(WispSpell);
            DeathHooks.Register(Kit.World, new DeathHooks(new DeathOptions { GhostFormAura = aura }, new FixedDeathClock(T)));
            var form = new GhostForm(Kit.World, () => Kit.System, warnings is null ? null : warnings.Add);
            DeathSeams.Of(Kit.World).TryRegisterGhostForm(form);
            var session = new FakeSession(4);
            Player = CombatTestKit.AddPlayer(Kit.World, 4, 10, 10, session, race);
            Session = session;
            Kit.World.RunTick(1);
            session.Clear();
            if (knowsWisp)
            {
                Kit.Spellbook.LearnSpell(Player, GhostForm.WispSpiritSpellId);
            }
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public MapCombat Combat => Kit.World.GetMap(0).Combat;

        public SpellAuraHolder[] Auras(uint spell) => [.. Kit.System.GetAuras(Player).Where(h => h.Spell.Id == spell)];

        public void DieAndRelease()
        {
            Player.Health = 0;
            Combat.KillPlayer(Player);
            Assert.True(Combat.RepopPlayer(Player));
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void ReleasingTheSpirit_CastsTheGhostSpell_ThatSetsTheFlagAndTheVisibilityBit()
    {
        using var rig = new Rig();
        rig.DieAndRelease();

        Assert.Single(rig.Auras(CombatConstants.GhostSpellId));
        Assert.NotEqual(0u, (uint)(rig.Player.Flags & PlayerFlags.Ghost));
        Assert.Equal(GhostAuras.VisFlagGhost, rig.Player.GetByte(UpdateFields.UnitFieldBytes1, GhostAuras.VisFlagByte) & GhostAuras.VisFlagGhost);
    }

    [Fact]
    public void TheWaterWalkOrder_IsStillSentOnce_EvenThoughTheAuraSetsTheFlagFirst()
    {
        using var rig = new Rig();
        rig.DieAndRelease();

        Assert.Single(rig.Session.Sent, p => p.Opcode == WorldOpcode.SmsgMoveWaterWalk);
    }

    [Fact]
    public void TheGhostSpell_GivesPlusTwentyFivePercentRunAndSwimSpeed()
    {
        using var rig = new Rig();
        float run = UnitSpeed.Get(rig.Player, MoveType.Run);
        float swim = UnitSpeed.Get(rig.Player, MoveType.Swim);
        rig.DieAndRelease();
        CombatTestKit.AckPendingMovement(rig.Player); // the speed changes take effect on the client's ack

        Assert.Equal(run * 1.25f, UnitSpeed.Get(rig.Player, MoveType.Run), 3);
        Assert.Equal(swim * 1.25f, UnitSpeed.Get(rig.Player, MoveType.Swim), 3);
    }

    [Fact]
    public void Resurrecting_RemovesTheAura_TheFlagTheVisibilityBitAndTheSpeed_AndOrdersLandWalk()
    {
        using var rig = new Rig();
        float run = UnitSpeed.Get(rig.Player, MoveType.Run);
        rig.DieAndRelease();
        CombatTestKit.AckPendingMovement(rig.Player);
        rig.Session.Clear();

        rig.Combat.ResurrectPlayer(rig.Player, 0.5f, applySickness: false);
        CombatTestKit.AckPendingMovement(rig.Player);

        Assert.Empty(rig.Auras(CombatConstants.GhostSpellId));
        Assert.Equal(0u, (uint)(rig.Player.Flags & PlayerFlags.Ghost));
        Assert.Equal(0, rig.Player.GetByte(UpdateFields.UnitFieldBytes1, GhostAuras.VisFlagByte) & GhostAuras.VisFlagGhost);
        Assert.Equal(run, UnitSpeed.Get(rig.Player, MoveType.Run), 3);
        Assert.Single(rig.Session.Sent, p => p.Opcode == WorldOpcode.SmsgMoveLandWalk);
    }

    [Fact]
    public void AnAlivePlayerResurrected_GetsNoSpuriousLandWalkOrder()
    {
        using var rig = new Rig();

        rig.Combat.ResurrectPlayer(rig.Player, 1f, applySickness: false);

        Assert.DoesNotContain(rig.Session.Sent, p => p.Opcode == WorldOpcode.SmsgMoveLandWalk);
    }

    [Fact]
    public void ANightElfWhoKnowsTheWispPassive_GetsTheWispSpellToo()
    {
        using var rig = new Rig(race: Race.NightElf, knowsWisp: true);
        float run = UnitSpeed.Get(rig.Player, MoveType.Run);
        rig.DieAndRelease();
        CombatTestKit.AckPendingMovement(rig.Player);

        Assert.Single(rig.Auras(CombatConstants.WispGhostSpellId));
        Assert.Single(rig.Auras(CombatConstants.GhostSpellId));
        Assert.Equal(run * 1.5f, UnitSpeed.Get(rig.Player, MoveType.Run), 3); // the wisp's +50% is the highest increase

        rig.Combat.ResurrectPlayer(rig.Player, 0.5f, applySickness: false);
        Assert.Empty(rig.Auras(CombatConstants.WispGhostSpellId));
        Assert.Empty(rig.Auras(CombatConstants.GhostSpellId));
    }

    [Fact]
    public void WithoutTheWispPassive_NoWisp()
    {
        using var rig = new Rig(race: Race.NightElf, knowsWisp: false);
        rig.DieAndRelease();

        Assert.Empty(rig.Auras(CombatConstants.WispGhostSpellId));
        Assert.Single(rig.Auras(CombatConstants.GhostSpellId));
    }

    [Fact]
    public void WithTheOptionOff_OnlyTheFlagAndWaterWalking_AsBefore()
    {
        using var rig = new Rig(aura: false);
        float run = UnitSpeed.Get(rig.Player, MoveType.Run);
        rig.DieAndRelease();

        Assert.Empty(rig.Auras(CombatConstants.GhostSpellId));
        Assert.NotEqual(0u, (uint)(rig.Player.Flags & PlayerFlags.Ghost));
        Assert.Equal(run, UnitSpeed.Get(rig.Player, MoveType.Run), 3);
        Assert.Single(rig.Session.Sent, p => p.Opcode == WorldOpcode.SmsgMoveWaterWalk);
    }

    [Fact]
    public void AMissingGhostSpell_FallsBackToTheFlag_AndWarnsOnce()
    {
        var warnings = new List<string>();
        using var rig = new Rig(withGhostSpell: false, warnings: warnings);
        rig.DieAndRelease();
        CombatTestKit.AckPendingMovement(rig.Player);
        rig.Combat.ResurrectPlayer(rig.Player, 0.5f, applySickness: false);
        rig.DieAndRelease();

        Assert.NotEqual(0u, (uint)(rig.Player.Flags & PlayerFlags.Ghost));
        Assert.Single(warnings);
        Assert.Contains("8326", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ARestoredGhost_GetsTheAuraBackAtLogin()
    {
        using var rig = new Rig();
        var snapshot = new ArcaneCore.Kernel.Characters.CorpseSnapshot(0, 50, 60, 83.5f, 2f, T - 10, (byte)CorpseType.ResurrectablePve);
        PlayerLife.ApplyGhostState(rig.Player);

        rig.Combat.RestoreGhost(rig.Player, snapshot);

        Assert.Single(rig.Auras(CombatConstants.GhostSpellId));
    }

    [Fact]
    public void TheGhostAuraModule_RegistersAlongsideEveryBuiltInModule()
    {
        // A second registration of aura 95 (another lane) would make SpellSystem construction throw at startup.
        using var kit = new SpellTestKit();

        Assert.Contains(typeof(GhostAuras), SpellHandlerModules.BuiltIn);
    }
}
