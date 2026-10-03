using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Tests.Talents;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.SpellMods.ModTestSupport;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// Talents are data: a talent rank spell is a passive with aura 107 or 108, learning it through the real <see cref="TalentService"/>
/// casts the passive, the aura handler registers the modifier, the cast pipeline reads it, and a respec or a higher rank takes it
/// away again (vmangos Player::LearnTalent / AddSpell / RemoveSpell, Player.cpp:20684-20800, :3606-3622). No per-talent code. The
/// spells are synthetic talent shapes, never retail data.
/// </summary>
public sealed class TalentModEndToEndTests
{
    private const uint Strike = 949001;        // instant, 15 rage, family mask 1 (Improved Heroic Strike shape target)
    private const uint CostR1 = 949011, CostR2 = 949012;       // flat cost -3 / -6
    private const uint FastR1 = 949021;                          // flat cast time -500 (Improved Fireball shape)
    private const uint CritR1 = 949031;                          // flat crit +2 (a one-rank crit talent)
    private const uint Bolt = 949002;                            // 2 s cast, family mask 1

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kit = new SpellTestKit(
                InFamily(Spell(Strike, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
                {
                    PowerType = (int)PowerType.Rage,
                    ManaCost = 15,
                    RangeIndex = 4,
                    Range = new SpellRange(0, 30),
                    StartRecoveryCategory = 0,
                    StartRecoveryTime = 0,
                }),
                InFamily(Spell(Bolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
                {
                    CastTime = new SpellCastTime(2000, 0, 0),
                    RangeIndex = 4,
                    Range = new SpellRange(0, 30),
                    StartRecoveryCategory = 0,
                    StartRecoveryTime = 0,
                }),
                Flat(CostR1, SpellModOp.Cost, -3),
                Flat(CostR2, SpellModOp.Cost, -6),
                Flat(FastR1, SpellModOp.CastingTime, -500),
                Flat(CritR1, SpellModOp.CriticalChance, 2));
            Session = new FakeSession(1, AccountSecurity.Player);
            Player = TestWorld.CreatePlayer(1, 0, 0, Session);
            Player.Level = 20;
            Kit.World.AddPlayer(Player);
            Kit.World.RunTick(0);
            (Target, _) = Kit.AddPlayer(2, 3, 0);
            Kit.Spellbook.Teach(Player, Strike, Bolt);
            Service = new TalentService(Catalog, Kit.System, new TalentOptions(), () => 1_800_000_000)
            {
                Sink = new RecordingTalentSink(),
                RankChain = new MapRankChain([]),
                KnownSpells = p => Kit.Spellbook.Spells.GetValueOrDefault(p.Guid) ?? [],
            };
            Service.InitTalentForLevel(Player);
            Player.Money = 100_000;
            Session.Clear();
        }

        public static TalentCatalog Catalog { get; } = new(
            [new TalentTabRecord(1, 1u << 0, 0)],
            [
                new TalentRecord(1, 1, 0, 0, [CostR1, CostR2, 0, 0, 0], 0, 0, 0),
                new TalentRecord(2, 1, 0, 1, [FastR1, 0, 0, 0, 0], 0, 0, 0),
                new TalentRecord(3, 1, 0, 2, [CritR1, 0, 0, 0, 0], 0, 0, 0),
            ]);

        public SpellTestKit Kit { get; }

        public FakeSession Session { get; }

        public Player Player { get; }

        public Player Target { get; }

        public TalentService Service { get; }

        public void Dispose()
        {
            Service.Dispose();
            Kit.Dispose();
        }

        /// <summary>Cast Strike with 100 rage and report the rage it cost.</summary>
        public uint CostOfStrike()
        {
            SpellSystem.SetPower(Player, PowerType.Rage, 100);
            Kit.Advance(100);
            Assert.Equal(SpellCastResult.CastOk, Kit.System.HandleCastRequest(Player, Strike, SpellCastTargets.ForUnit(Target.Guid)));
            return 100u - SpellSystem.GetPower(Player, PowerType.Rage);
        }

        public int CastTimeOfBolt()
        {
            Kit.System.HandleCastRequest(Player, Bolt, SpellCastTargets.ForUnit(Target.Guid));
            UnitSpellState state = Kit.System.GetState(Player.Guid)!;
            int castTime = state.CurrentCast!.CastTime;
            Kit.System.Interrupt(state.CurrentCast!);
            Kit.Advance(100);
            return castTime;
        }
    }

    [Fact]
    public void ACostTalent_CutsTheCost_ARankReplacesIt_ARespecRestoresIt()
    {
        using var rig = new Rig();
        Assert.Equal(15u, rig.CostOfStrike());

        Assert.True(rig.Service.LearnTalent(rig.Player, 1, 0));
        Assert.Equal(12u, rig.CostOfStrike());
        Assert.Equal(-3, Assert.Single(rig.Kit.System.Mods.ModsOf(rig.Player, SpellModOp.Cost)).Value);

        Assert.True(rig.Service.LearnTalent(rig.Player, 1, 1));
        Assert.Equal(9u, rig.CostOfStrike());
        Assert.Equal(-6, Assert.Single(rig.Kit.System.Mods.ModsOf(rig.Player, SpellModOp.Cost)).Value);   // rank 1's mod is gone

        rig.Session.Clear();
        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));

        Assert.Empty(rig.Kit.System.Mods.ModsOf(rig.Player, SpellModOp.Cost));
        Assert.Equal(15u, rig.CostOfStrike());
        // The client is told: the last SMSG_SET_FLAT_SPELL_MODIFIER for bit 0, op 14 carries 0.
        byte[] last = Packets(rig.Session, WorldOpcode.SmsgSetFlatSpellModifier).Last(p => p[0] == 0 && p[1] == (byte)SpellModOp.Cost);
        Assert.Equal(0, BitConverter.ToInt32(last, 2));
    }

    [Fact]
    public void ACastTimeTalent_ShortensTheBolt_UntilRespec()
    {
        using var rig = new Rig();
        Assert.Equal(2000, rig.CastTimeOfBolt());

        Assert.True(rig.Service.LearnTalent(rig.Player, 2, 0));
        Assert.Equal(1500, rig.CastTimeOfBolt());

        Assert.True(rig.Service.ResetTalents(rig.Player, noCost: true));
        Assert.Equal(2000, rig.CastTimeOfBolt());
    }

    [Fact]
    public void ATalentThatSurvivesARelog_ComesBackThroughTheLoginPassivePath()
    {
        using var rig = new Rig();
        Assert.True(rig.Service.LearnTalent(rig.Player, 2, 0));
        Assert.True(rig.Kit.Spellbook.HasSpell(rig.Player, FastR1));

        // Logout and login build a fresh player; the spell feature casts every known passive on it (SpellFeature.OnPlayerLoggedIn).
        (Player relogged, _) = rig.Kit.AddPlayer(7, 5, 5);
        rig.Kit.System.CastSpell(relogged, FastR1, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(-500, Assert.Single(rig.Kit.System.Mods.ModsOf(relogged, SpellModOp.CastingTime)).Value);
    }

    [Fact]
    public void TheCoverageReport_SeesTheModifierTalentsAsHandled()
    {
        using var rig = new Rig();

        TalentCoverageReport handlers = TalentEffectCoverage.Build(Rig.Catalog, rig.Kit.System);
        TalentModCoverageReport mods = TalentModCoverage.Build(Rig.Catalog, rig.Kit.System);

        Assert.Equal(4, handlers.SupportedCount);
        Assert.Empty(handlers.Gaps);
        Assert.Equal(4, mods.ModEffects);
        Assert.Empty(mods.Gaps);
    }
}
