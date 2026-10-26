using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Characterises <c>SpellSystem.Cast</c> before the Wave 3 helper extraction: the order of the cost, packet and effect
/// side effects, the channel start values, and the fault path (ledger sites, label, exception data, rethrow, cast finished).
/// </summary>
public sealed class SpellCastPipelineCharacterisationTests
{
    private const uint CostedHeal = 995100;
    private const uint FaultSelfSpell = 995101;
    private const uint FaultEnemySpell = 995102;

    private sealed class CastProbeFault(string message) : Exception(message);

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private static SpellInfo Costed() => SpellTestKit.Spell(CostedHeal, SpellTestKit.Effect(SpellEffectName.Heal, 20)) with
    {
        PowerType = (int)PowerType.Rage,
        ManaCost = 30,
        RecoveryTime = 5000,
    };

    [Fact]
    public void InstantCast_HasSpentCostStartedCooldownAndSentSpellGo_BeforeTheEffectRuns()
    {
        using var kit = new SpellTestKit(Costed());
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, CostedHeal);
        SpellSystem.SetPower(player, PowerType.Rage, 100);
        player.Health = 10;
        SpellInfo spell = kit.Store.Get(CostedHeal)!;

        uint rageAtEffect = uint.MaxValue;
        bool readyAtEffect = true;
        int spellGoAtEffect = -1;
        int activeCooldownsAtEffect = -1;
        kit.System.RegisterEffect(SpellEffectName.Heal, c =>
        {
            rageAtEffect = SpellSystem.GetPower(player, PowerType.Rage);
            readyAtEffect = kit.System.IsSpellReady(player, spell);
            spellGoAtEffect = session.Sent.Count(p => p.Opcode == WorldOpcode.SmsgSpellGo);
            activeCooldownsAtEffect = kit.System.GetActiveCooldowns(player).Count();
        });

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, CostedHeal, SpellCastTargets.ForSelf()));

        Assert.Equal(70u, rageAtEffect);
        Assert.False(readyAtEffect);
        Assert.Equal(1, spellGoAtEffect);
        Assert.True(activeCooldownsAtEffect >= 1);
        Assert.Equal(70u, SpellSystem.GetPower(player, PowerType.Rage));
        Assert.Null(kit.System.GetState(player.Guid)?.CurrentCast);
        List<WorldOpcode> opcodes = [.. session.Sent.Select(p => p.Opcode)
            .Where(o => o is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgCastResult or WorldOpcode.SmsgSpellGo)];
        Assert.Equal([WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgCastResult, WorldOpcode.SmsgSpellGo], opcodes);
    }

    [Fact]
    public void ChannelCast_StartsTheChannelWithItsDurationTargetAndSpell_AndStaysTheCurrentCast()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, SpellTestKit.ChannelSpell);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, SpellTestKit.ChannelSpell, SpellCastTargets.ForSelf()));

        SpellCast cast = kit.System.GetState(player.Guid)!.CurrentCast!;
        Assert.Equal(SpellCastState.Casting, cast.State);
        Assert.Equal(5000, cast.Timer);
        Assert.Equal(player.Guid.Value, player.GetUInt64(UpdateFields.UnitFieldChannelObject));
        Assert.Equal(SpellTestKit.ChannelSpell, player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Equal(SpellPackets.BuildChannelStart(SpellTestKit.ChannelSpell, 5000), SpellTestKit.Packets(session, WorldOpcode.MsgChannelStart).Single());
    }

    [Fact]
    public void ThrowingEffect_RethrowsTheSameException_RecordsBothLedgerSites_TagsItsData_AndFinishesTheCast()
    {
        SpellInfo spell = SpellTestKit.Spell(FaultSelfSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 0));
        var logger = new CapturingLogger();
        using var kit = new SpellTestKit(spell);
        var system = new SpellSystem(kit.Store, () => kit.Now, spellbook: kit.Spellbook, random: new Random(1), logger: logger) { MapUpdateIntervalMs = 0 };
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, FaultSelfSpell);
        var thrown = new CastProbeFault("cast-pipeline-probe-1");
        system.RegisterEffect(SpellEffectName.Dummy, c => throw thrown);

        CastProbeFault caught = Assert.Throws<CastProbeFault>(() => system.HandleCastRequest(player, FaultSelfSpell, SpellCastTargets.ForSelf()));

        Assert.Same(thrown, caught);
        string who = $"{player.Guid} (Name={player.Name}, Level={player.Level}, Map={player.MapId})";
        Assert.Equal(FaultSelfSpell, caught.Data["SpellId"]);
        Assert.Equal($"Spell {FaultSelfSpell}", caught.Data["SpellName"]);
        Assert.Equal(who, caught.Data["Caster"]);
        Assert.Equal(who, caught.Data["Target"]);
        string type = typeof(CastProbeFault).FullName!;
        // PR #1325 merged the cast-site and script-fault sites into one "spell.script" site: both records land there.
        long spellFaults = FaultLedger.Shared.Snapshot().Where(s => s.Site == "spell.script" && s.ExceptionType == type).Sum(s => s.Count);
        Assert.True(spellFaults >= 2, $"expected both spell.script records, got {spellFaults}");
        string line = Assert.Single(logger.Lines, l => l.Contains("Spell script fault in Cast (", StringComparison.Ordinal));
        Assert.Contains($"SpellId={FaultSelfSpell}, SpellName=Spell {FaultSelfSpell}, Caster={who}, Target={who}", line, StringComparison.Ordinal);
        Assert.Null(system.GetState(player.Guid)?.CurrentCast);
        Assert.Single(SpellTestKit.Packets(session, WorldOpcode.SmsgSpellGo)); // the fault came after SMSG_SPELL_GO, not before
    }

    [Fact]
    public void ThrowingEffect_OnAnotherTarget_NamesThatTargetNotTheCasterInTheFaultData()
    {
        SpellInfo spell = SpellTestKit.Spell(FaultEnemySpell, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            School = SpellSchool.Fire,
        };
        using var kit = new SpellTestKit(spell);
        (Player caster, _) = kit.AddPlayer(1);
        SpellTestKit.FlagForPvp(caster);
        (Player target, _) = kit.AddHostilePlayer(2, 10, 0);
        kit.World.RunTick(0);
        kit.Spellbook.Teach(caster, FaultEnemySpell);
        var thrown = new CastProbeFault("cast-pipeline-probe-2");
        kit.System.RegisterEffect(SpellEffectName.SchoolDamage, c => throw thrown);

        CastProbeFault caught = Assert.Throws<CastProbeFault>(
            () => kit.System.HandleCastRequest(caster, FaultEnemySpell, SpellCastTargets.ForUnit(target.Guid)));

        Assert.Equal($"{caster.Guid} (Name={caster.Name}, Level={caster.Level}, Map={caster.MapId})", caught.Data["Caster"]);
        Assert.Equal($"{target.Guid} (Name={target.Name}, Level={target.Level}, Map={target.MapId})", caught.Data["Target"]);
        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast);
    }
}
