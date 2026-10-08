using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Procs;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Procs;

/// <summary>
/// The shared rig of the talent proc tests: talent auras and trigger spells under their real Spell.dbc ids (the proc scripts key on them), and
/// a probe effect that records every value the triggered spells carry, so a test asserts the vmangos base points exactly instead of reading
/// them back through damage, resistances and caps.
/// </summary>
internal sealed class TalentProcRig : IDisposable
{
    /// <summary>An effect number no lane handles: its handler only records (caster, target, value).</summary>
    public const SpellEffectName ProbeEffect = (SpellEffectName)240;

    /// <summary>SPELL_ATTR_EX3_ALWAYS_HIT (the probes never miss).</summary>
    private const uint AlwaysHit = 0x00040000;

    public TalentProcRig(params SpellInfo[] spells)
    {
        Kit = new SpellTestKit(spells);
        Kit.System.RegisterEffect(ProbeEffect, context => Probes.Add(new ProbeHit(context.Spell.Id, context.Caster, context.Target, context.Value)));
    }

    public SpellTestKit Kit { get; }

    public SpellSystem System => Kit.System;

    public List<ProbeHit> Probes { get; } = [];

    /// <summary>A player at (x, y), level 60, optionally PvP-flagged and facing <paramref name="orientation"/>.</summary>
    public Player AddPlayer(uint guid, float x, float y, Race race = Race.Human, bool pvp = true, float orientation = 0f)
    {
        (Player player, _) = Kit.AddPlayer(guid, x, y, race: race);
        player.Level = 60;
        player.Health = player.MaxHealth = 1000;
        player.Orientation = orientation;
        if (pvp)
        {
            player.UnitFlags |= UnitFlags.Pvp;
        }

        return player;
    }

    /// <summary>Make <paramref name="unit"/> a mana user with <paramref name="maxMana"/> mana and <paramref name="createMana"/> base (create) mana.</summary>
    public static void GiveMana(Unit unit, uint maxMana, uint createMana = 0, uint current = 0)
    {
        unit.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        unit.SetUInt32(UpdateFields.UnitFieldMaxpower1, maxMana);
        unit.SetUInt32(UpdateFields.UnitFieldPower1, current);
        unit.SetUInt32(UpdateFields.UnitFieldBaseMana, createMana);
    }

    /// <summary>Put the self aura <paramref name="spellId"/> on <paramref name="unit"/> before the event under test.</summary>
    public void Apply(Unit unit, uint spellId)
    {
        RuleTestSupport.Apply(Kit, unit, spellId);
        Kit.Now++;
    }

    /// <summary>Use spell_proc_event rows (the operator-imported conditions).</summary>
    public void UseRows(params SpellProcEventRecord[] rows) => System.ProcEvents = new Rows(rows);

    public void Dispose() => Kit.Dispose();

    /// <summary>A passive self aura of <paramref name="type"/> with one effect, the shape of a talent rank spell.</summary>
    public static SpellInfo Talent(uint id, AuraType type, int amount, ProcFlags procFlags, uint family = 0, uint icon = 0, uint charges = 0,
        uint trigger = 0, int misc = 0) => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: type, misc: misc, trigger: trigger)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
        ProcFlags = procFlags,
        ProcChance = 100,
        ProcCharges = charges,
        SpellFamilyName = family,
        SpellIconId = icon,
    };

    /// <summary>A triggered spell whose single effect is the probe, aimed at the caster or at an enemy unit.</summary>
    public static SpellInfo Probe(uint id, bool atEnemy, int value = 1) => Spell(id,
        Effect(ProbeEffect, value, atEnemy ? SpellImplicitTarget.UnitEnemy : SpellImplicitTarget.UnitCaster)) with
    {
        AttributesEx3 = AlwaysHit,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
        RangeIndex = 4,
        Range = new SpellRange(0, 100),
    };

    /// <summary>A harmful magic spell of <paramref name="school"/> (the spell that causes the event).</summary>
    public static SpellInfo Bolt(uint id, SpellSchool school, uint family = 0, ulong familyFlags = 0, uint manaCost = 0, uint manaCostPct = 0) =>
        RuleTestSupport.Magic(id, school) with
        {
            SpellFamilyName = family,
            SpellFamilyFlags = familyFlags,
            ManaCost = manaCost,
            ManaCostPercentage = manaCostPct,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    public readonly record struct ProbeHit(uint SpellId, Unit Caster, Unit Target, int Value);

    private sealed class Rows(SpellProcEventRecord[] rows) : ISpellProcEventCatalog
    {
        public SpellProcEventRecord? Find(uint spellId) => rows.FirstOrDefault(r => r.Entry == spellId);
    }
}
