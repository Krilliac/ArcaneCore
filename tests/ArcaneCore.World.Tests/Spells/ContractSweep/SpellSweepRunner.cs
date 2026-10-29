using System.Diagnostics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Qa;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>Observations the checks read. A test double replaces it to prove a check fails when the engine misbehaves.</summary>
internal interface ISweepProbe
{
    /// <summary>The live (not removed) aura holders of <paramref name="spellId"/> on <paramref name="unit"/>.</summary>
    IReadOnlyList<SpellAuraHolder> Holders(SpellSystem system, Unit unit, uint spellId);

    /// <summary>The unit's UNIT_FIELD_FLAGS.</summary>
    UnitFlags Flags(Unit unit);

    /// <summary>Every cast the sweep makes (SpellSystem.CastSpell).</summary>
    SpellCastResult Cast(SpellSystem system, Unit caster, uint spellId, SpellCastTargets targets, bool triggered);

    /// <summary>Whether the unit has a cast or channel in progress.</summary>
    bool IsCasting(SpellSystem system, Unit unit);

    /// <summary>Whether the spell is off its own cooldown for the unit (the global cooldown ignored).</summary>
    bool IsReady(SpellSystem system, Unit unit, SpellInfo spell);

    /// <summary>When the category's cooldown ends for the unit, or null when it has none.</summary>
    uint? CategoryCooldownUntil(SpellSystem system, Unit unit, uint category);

    /// <summary>The unit's current power of <paramref name="power"/>.</summary>
    uint Power(Unit unit, PowerType power);
}

/// <summary>Reads the engine directly. The can-fail doubles derive from it and override one observation.</summary>
internal class ProductionSweepProbe : ISweepProbe
{
    public virtual IReadOnlyList<SpellAuraHolder> Holders(SpellSystem system, Unit unit, uint spellId)
        => system.GetState(unit.Guid) is { } state ? [.. state.AuraHolders.Where(h => !h.IsRemoved && h.Spell.Id == spellId)] : [];

    public virtual UnitFlags Flags(Unit unit) => unit.UnitFlags;

    public virtual SpellCastResult Cast(SpellSystem system, Unit caster, uint spellId, SpellCastTargets targets, bool triggered)
        => system.CastSpell(caster, spellId, targets, triggered);

    public virtual bool IsCasting(SpellSystem system, Unit unit) => system.GetState(unit.Guid)?.CurrentCast is not null;

    public virtual bool IsReady(SpellSystem system, Unit unit, SpellInfo spell) => system.IsSpellReadyIgnoringGlobalCooldown(unit, spell);

    public virtual uint? CategoryCooldownUntil(SpellSystem system, Unit unit, uint category)
        => system.GetState(unit.Guid) is { } state && state.CategoryCooldowns.TryGetValue(category, out uint until) ? until : null;

    public virtual uint Power(Unit unit, PowerType power) => unit.GetUInt32(UpdateFields.UnitFieldPower1 + (int)power);
}

/// <summary>The sweep's result: one outcome per swept spell and every violation found.</summary>
internal sealed class SweepResult
{
    public List<SweepSpellOutcome> Outcomes { get; } = [];

    public List<SweepViolation> Violations { get; } = [];

    public int RowsTotal { get; set; }

    public bool Full { get; set; }

    public TimeSpan Elapsed { get; set; }

    /// <summary>Spells per AuraInterruptFlags bit that no production driver fires (the check could not run for that bit).</summary>
    public SortedDictionary<string, int> UndrivenInterruptBits { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Sweeps spells through the contracts their Spell.dbc rows declare (see <see cref="SweepCategory"/>): every cast happens in a fresh
/// <see cref="SweepScene"/>, every scene ends with the invariant check (j), and an exception (thrown, or caught and logged by the
/// engine) is recorded against the spell instead of stopping the sweep.
/// </summary>
internal sealed class SpellSweepRunner
{
    /// <summary>The longest aura or cooldown the sweep simulates tick by tick; a longer one has its configured duration checked only.</summary>
    public const int MaxSimulatedMs = 10 * 60 * 1000;

    /// <summary>The longest generic cast (cast time plus channel) the sweep waits out before calling the caster stuck.</summary>
    public const int MaxCastWaitMs = 60_000;

    private static readonly (AuraType Type, UnitFlags Flag)[] FlagAuras =
    [
        (AuraType.ModStun, UnitFlags.Stunned),
        (AuraType.ModPacify, UnitFlags.Pacified),
        (AuraType.ModPacifySilence, UnitFlags.Pacified),
        (AuraType.ModSilence, UnitFlags.Silenced),
        (AuraType.ModPacifySilence, UnitFlags.Silenced),
        (AuraType.ModConfuse, UnitFlags.Confused),
        (AuraType.ModFear, UnitFlags.Fleeing),
        (AuraType.ModDisarm, UnitFlags.Disarmed),
    ];

    private readonly SpellSweepRig _rig;
    private readonly SpellSweepCatalog? _catalog;
    private readonly ISweepActions _actions;
    private readonly ISweepProbe _probe;
    private readonly PlayerbotQaSpellGapAnalyzer _gaps;
    private readonly Dictionary<(uint Type, bool Hostile), uint> _dispellers = [];

    /// <summary>The scene's position within its spell's checks: with the spell id it seeds the scene's random sources.</summary>
    private int _sceneOrdinal;
    private SweepResult _result = new();
    private SweepSpellOutcome? _current;

    public SpellSweepRunner(SpellSweepRig rig, SpellSweepCatalog? catalog, ISweepActions? actions = null, ISweepProbe? probe = null)
    {
        _rig = rig;
        _catalog = catalog;
        _actions = actions ?? new ProductionSweepActions();
        _probe = probe ?? new ProductionSweepProbe();
        _gaps = new PlayerbotQaSpellGapAnalyzer(rig.System);
        IndexDispellers(rig.System.Store);
    }

    /// <summary>Which contracts to run (all by default; the can-fail tests narrow it).</summary>
    public string Contracts { get; init; } = "abcdefghij";

    public SweepResult Run(IEnumerable<SpellInfo> spells, int rowsTotal, bool full)
    {
        _result = new SweepResult { RowsTotal = rowsTotal, Full = full };
        var clock = Stopwatch.StartNew();
        foreach (SpellInfo spell in spells)
        {
            _sceneOrdinal = 0;
            SweepSpell(spell);
        }

        _result.Elapsed = clock.Elapsed;
        return _result;
    }

    private bool Runs(char contract) => Contracts.Contains(contract, StringComparison.Ordinal);

    private Class ClassFor(SpellInfo spell)
    {
        Class? byPower = spell.PowerType switch
        {
            (int)PowerType.Rage => Class.Warrior,
            (int)PowerType.Energy => Class.Rogue,
            _ => null,
        };
        Class? owner = _catalog?.ClassOf(spell);
        if (byPower is { } power && owner is not null && SpellSweepRig.PowerFor(owner.Value) != SpellSweepRig.PowerFor(power))
        {
            return power;
        }

        return owner ?? byPower ?? Class.Mage;
    }

    private static byte LevelFor(SpellInfo spell) => (byte)Math.Clamp((int)Math.Max(Math.Max(spell.SpellLevel, spell.BaseLevel), 1), 1, 60);

    private void Violate(string category, uint spellId, string detail) => _result.Violations.Add(new SweepViolation(category, spellId, detail));

    private void Checked(char contract, string state) => _current!.Checks[contract.ToString()] = state;

    private void SweepSpell(SpellInfo spell)
    {
        _current = new SweepSpellOutcome
        {
            SpellId = spell.Id, Name = spell.Name ?? "", PlayerClass = _catalog?.ClassOf(spell)?.ToString() ?? "none",
            TargetKind = SweepTargets.KindOf(spell).ToString(),
        };
        _result.Outcomes.Add(_current);

        if (Runs('a'))
        {
            foreach (QaSpellGap gap in _gaps.Of(spell))
            {
                string category = gap.Code switch
                {
                    QaSpellGaps.Effect => SweepCategory.UnhandledEffect,
                    QaSpellGaps.Aura => SweepCategory.UnhandledAura,
                    _ => SweepCategory.UnhandledOther,
                };
                Violate(category, spell.Id, category == SweepCategory.UnhandledOther ? $"{gap.Code}:{gap.Detail}" : gap.Detail);
            }
        }

        // (a) and (b): the generic cast, with reagents, cooldowns, power and cast time out of the way.
        bool applied = false;
        Unit? auraUnit = null;
        Scene(spell, PlayerCheats.NoCooldown | PlayerCheats.NoPowerCost | PlayerCheats.NoCastTime, 5f, scene =>
        {
            SpellCastResult result = Cast(scene, triggered: false);
            if (result is SpellCastResult.Reagents or SpellCastResult.Totems or SpellCastResult.EquippedItem or SpellCastResult.EquippedItemClass)
            {
                result = Cast(scene, triggered: true);
                _current.CastResult = "triggered:" + result;
            }
            else
            {
                _current.CastResult = result.ToString();
            }

            auraUnit = FindAuraUnit(scene);
            applied = auraUnit is not null;
            _current.AppliedAura = applied;
            if (applied && Runs('b'))
            {
                CheckLifetime(scene, spell, auraUnit!);
            }

            WaitOutCast(scene, spell);
        });

        if (!applied || spell.IsPassive)
        {
            Checked('b', applied ? "not-run:passive" : "not-run:no-aura");
        }

        if (applied && !spell.IsPassive && Runs('c'))
        {
            CheckAuraInterrupts(spell);
        }
        else
        {
            Checked('c', applied ? "not-run:passive" : "not-run:no-aura");
        }

        if (Runs('d'))
        {
            CheckCastInterrupts(spell);
        }

        if (Runs('e'))
        {
            CheckCooldowns(spell);
        }

        if (Runs('f'))
        {
            CheckCost(spell);
        }

        if (Runs('g'))
        {
            CheckRange(spell);
        }

        if (Runs('h'))
        {
            CheckStacking(spell, applied);
        }

        if (Runs('i'))
        {
            CheckDispel(spell, applied);
        }
    }

    // ---- scenes -------------------------------------------------------------------------------------------------------

    /// <summary>Run <paramref name="body"/> in a fresh scene; exceptions and logged faults are recorded, and (j) runs at the end.</summary>
    private void Scene(SpellInfo spell, PlayerCheats cheats, float distance, Action<SweepScene> body, Class? cls = null)
    {
        _rig.TakeFaults();
        SweepScene? scene = null;
        try
        {
            // Every roll a scene makes (spell hit and resist, melee outcome, damage) comes from a source seeded by the spell and the
            // scene (a fixed mix, not HashCode, which is randomised per process), never from the shared one: the same spell gives the same violations whatever ran before it (default and full
            // selections, so the ratchet compares like with like) and on every run.
            int seed = unchecked((int)(((uint)SpellSweepCatalog.SampleSeed * 0x9E3779B1u) ^ (spell.Id * 0x85EBCA77u) ^ ((uint)_sceneOrdinal++ * 0xC2B2AE3Du)));
            _rig.System.Random = new Random(seed);
            _rig.Map.Combat.Random = new SeededCombatRandom(seed);
            scene = new SweepScene(_rig, spell, cls ?? ClassFor(spell), LevelFor(spell), SweepTargets.KindOf(spell), distance);
            scene.Caster.Cheats = cheats;
            body(scene);
            if (Runs('j'))
            {
                CheckInvariants(scene);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Violate(SweepCategory.Exception, spell.Id, $"{ex.GetType().Name} at {StableFrame(ex)}");
        }
        finally
        {
            try
            {
                scene?.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Violate(SweepCategory.Exception, spell.Id, $"cleanup {ex.GetType().Name} at {StableFrame(ex)}");
            }
        }

        foreach (SweepFault fault in _rig.TakeFaults())
        {
            Violate(fault.Type == SweepFaultCapture.ErrorLogType ? SweepCategory.ErrorLog : SweepCategory.LoggedFault, spell.Id,
                fault.Type == SweepFaultCapture.ErrorLogType ? $"{fault.Category}:{fault.Type}" : $"{fault.Category}:{fault.Type} at {fault.TopFrame}");
        }
    }

    /// <summary>
    /// One cast of the scene's spell, resolved: with spell batching on (<see cref="SpellSystem.SpellBatchWindowMs"/>, which
    /// ARCANECORE_TEST_SPELL_BATCH_MS sets for the whole test run) a non-triggered go that passes its check answers CastOk but only goes off
    /// at the next batch boundary, so the sweep waits for it (<see cref="SettleBatch"/>) before any check reads what the cast did.
    /// </summary>
    private SpellCastResult Cast(SweepScene scene, bool triggered)
    {
        SpellCastResult result = _probe.Cast(_rig.System, scene.Caster, scene.Spell.Id, scene.Targets, triggered);
        SettleBatch();
        return result;
    }

    /// <summary>
    /// Tick until what is held for the batch boundary has resolved: the casts' goes, and with <paramref name="swings"/> the white swings too
    /// (MapCombat.SwingBatch.cs). Held work goes off at the first update in a later batch than the one it was held in, all of it
    /// (SpellSystem.UpdateSpellBatch, MapCombat.UpdateHeldSwings), so the wait ends at the first tick past the next boundary, in 50 ms steps.
    /// A go or swing held after that is new work of the new batch (a fast swing timer holds a swing in every batch), not one left behind.
    /// A no-op with batching off (nothing is ever held), so the sweep then runs exactly as it did before batching existed. A cast waits for
    /// its own go only: waiting for a swing it provoked would let time pass that the same check without batching does not see. A boundary
    /// not reached within one window plus a tick is an invariant break (j.batch-not-settled).
    /// </summary>
    private void SettleBatch(bool swings = false)
    {
        uint window = _rig.System.SpellBatchWindowMs;
        if (window == 0 || (_rig.System.PendingBatchedGoes == 0 && (!swings || _rig.Map.Combat.PendingSwings == 0)))
        {
            return;
        }

        uint heldIn = SpellSystem.BatchIndex(_rig.World.NowMs, window);
        for (uint waited = 0; SpellSystem.BatchIndex(_rig.World.NowMs, window) == heldIn && waited <= window; waited += 50)
        {
            _rig.World.RunTick(50);
        }

        if (SpellSystem.BatchIndex(_rig.World.NowMs, window) == heldIn)
        {
            Violate(SweepCategory.BatchNotSettled, _current!.SpellId,
                $"no batch boundary {window} ms after goes={_rig.System.PendingBatchedGoes} swings={(swings ? _rig.Map.Combat.PendingSwings : 0)} were held");
        }
    }

    private Unit? FindAuraUnit(SweepScene scene)
    {
        // Prefer the intended target, then the caster, then anyone in the scene.
        foreach (Unit unit in new[] { scene.Target, scene.Caster }.Concat(scene.Units))
        {
            if (_probe.Holders(_rig.System, unit, scene.Spell.Id).Count > 0)
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>Tick until the caster's cast (or channel) is over, bounded; a cast still running then is stuck (j).</summary>
    private void WaitOutCast(SweepScene scene, SpellInfo spell)
    {
        long budget = Math.Min(MaxCastWaitMs, (long)Math.Max(0, scene.Spell.CastTime.Base) + Math.Max(0, spell.GetMaxDuration()) + 2000);
        for (long waited = 0; waited < budget && IsCasting(scene.Caster); waited += 100)
        {
            _rig.World.RunTick(100);
        }

        if (IsCasting(scene.Caster))
        {
            if (budget >= MaxCastWaitMs)
            {
                _rig.System.InterruptNonMeleeSpells(scene.Caster); // a channel longer than the wait: not a stuck cast
                _rig.World.RunTick(0);
            }
            else if (Runs('j'))
            {
                Violate(SweepCategory.StuckCasting, spell.Id, "cast still running after cast time + duration");
                _rig.System.InterruptNonMeleeSpells(scene.Caster);
            }
        }
    }

    private bool IsCasting(Unit unit) => _probe.IsCasting(_rig.System, unit);

    // ---- (b) aura lifetime --------------------------------------------------------------------------------------------

    private void CheckLifetime(SweepScene scene, SpellInfo spell, Unit unit)
    {
        IReadOnlyList<SpellAuraHolder> holders = _probe.Holders(_rig.System, unit, spell.Id);
        if (spell.IsPassive)
        {
            return;
        }

        SpellAuraHolder holder = holders[0];
        int baseMs = spell.Duration.Base;
        int maxMs = spell.Duration.Maximum;
        if (spell.Duration == default)
        {
            Checked('b', "not-run:no-duration-index");
            return;
        }

        bool permanent = baseMs == -1;
        // vmangos SpellAuraHolder ctor (SpellAuras.cpp:6682-6688) raises a computed duration below 200 ms to 300 ms ("fixes how very
        // short duration buffs are displayed in the combat log", the PvP trinket's immunity): such a holder matches its row.
        bool floored = holder.MaxDuration == ShortAuraFloorMs && Math.Min(baseMs, maxMs) < ShortAuraThresholdMs;
        if (permanent ? !holder.IsPermanent && holder.MaxDuration != -1
            : !floored && (holder.MaxDuration < Math.Min(baseMs, maxMs) || holder.MaxDuration > Math.Max(baseMs, maxMs)))
        {
            Violate(SweepCategory.DurationMismatch, spell.Id, $"dbc={baseMs}..{maxMs} holder={holder.MaxDuration}");
        }

        int expected = permanent ? -1 : holder.MaxDuration;
        if (expected == -1 || expected > MaxSimulatedMs)
        {
            _rig.World.RunTick(1000);
            Checked('b', expected == -1 ? "permanent-1s" : "long-config-only");
            if (_probe.Holders(_rig.System, unit, spell.Id).Count == 0 && unit.IsAlive && !spell.IsChanneled)
            {
                Violate(SweepCategory.RemovedEarly, spell.Id, expected == -1 ? "permanent aura gone after 1 s" : "long aura gone after 1 s");
            }

            return;
        }

        uint step = (uint)Math.Clamp(expected / 40, 50, 1000);
        Advance((uint)Math.Max(0, expected - 300), step);
        bool presentBeforeEnd = _probe.Holders(_rig.System, unit, spell.Id).Count > 0;
        if (!presentBeforeEnd && unit.IsAlive && expected > 600 && !spell.IsChanneled)
        {
            Violate(SweepCategory.RemovedEarly, spell.Id, "gone before its duration");
        }

        Advance(1300, 50);
        if (_probe.Holders(_rig.System, unit, spell.Id).Count > 0)
        {
            Violate(SweepCategory.NeverExpires, spell.Id, $"still applied 1 s after {expected} ms");
            Checked('b', "ran");
            return;
        }

        Checked('b', "ran");
        if (unit.IsAlive)
        {
            CheckRestored(scene, spell, unit);
        }
    }

    /// <summary>
    /// After expiry the unit's stat block must equal a fresh, identical unit's: a second scene casts nothing, so its caster/target is
    /// the "before" picture (stat changes of the spell's non-aura effects are rare and land in the baseline).
    /// </summary>
    private void CheckRestored(SweepScene scene, SpellInfo spell, Unit unit)
    {
        // An aura of a spell this one triggers (Food's Well Fed, a growth stack) has a lifetime of its own and rightly outlives the
        // swept aura. Take those off through the production removal first, so the picture is this spell's own restore, and theirs.
        foreach (uint triggered in spell.Effects.Select(e => e.TriggerSpell).Where(id => id != 0 && id != spell.Id).Distinct())
        {
            if (_probe.Holders(_rig.System, unit, triggered).Count > 0)
            {
                _rig.System.RemoveAuras(unit, triggered);
            }
        }

        _rig.World.RunTick(0);
        Snapshot reference = unit is Player p
            ? Snapshot.Of(scene.AddPlayer((Class)p.Class, (byte)p.Level, 20f, 20f))
            : Snapshot.Of(scene.AddCreature(hostile: true, (byte)unit.Level, 20f, 20f));
        if (Snapshot.Of(unit).Differs(reference) is { } field)
        {
            Violate(SweepCategory.NotRestored, spell.Id, field);
        }
    }

    /// <summary>vmangos SpellAuraHolder: a duration below <see cref="ShortAuraThresholdMs"/> becomes <see cref="ShortAuraFloorMs"/>.</summary>
    private const int ShortAuraFloorMs = 300, ShortAuraThresholdMs = 200;

    private void Advance(uint ms, uint step)
    {
        for (uint done = 0; done < ms;)
        {
            uint diff = Math.Min(step, ms - done);
            _rig.World.RunTick(diff);
            done += diff;
        }
    }

    // ---- (c) aura interrupt flags --------------------------------------------------------------------------------------

    private void CheckAuraInterrupts(SpellInfo spell)
    {
        uint flags = (uint)spell.AuraInterruptFlags;
        uint undriven = flags & ProductionSweepActions.UndrivenFlags;
        for (int bit = 0; bit < 32; bit++)
        {
            if ((undriven & (1u << bit)) != 0)
            {
                string key = $"0x{1u << bit:X5}";
                _result.UndrivenInterruptBits[key] = _result.UndrivenInterruptBits.GetValueOrDefault(key) + 1;
            }
        }

        var states = new List<string>();
        foreach (SweepAction action in _actions.Actions)
        {
            string state = "not-run";
            Scene(spell, PlayerCheats.NoCooldown | PlayerCheats.NoPowerCost | PlayerCheats.NoCastTime, 5f, scene =>
            {
                SpellCastResult result = Cast(scene, triggered: false);
                if (result is not SpellCastResult.CastOk)
                {
                    result = Cast(scene, triggered: true);
                }

                Unit? unit = FindAuraUnit(scene);
                if (unit is null)
                {
                    state = "not-run:no-aura";
                    return;
                }

                if (action.PlayerOnly && unit is not Player)
                {
                    state = "not-run:not-a-player";
                    return;
                }

                bool expected = (flags & action.FlagsFor(unit)) != 0;
                IReadOnlyList<SpellAuraHolder> held = _probe.Holders(_rig.System, unit, spell.Id);
                if (!action.Perform(scene, unit))
                {
                    state = "not-run:action-failed";
                    return;
                }

                // With batching on, what the action set off resolves at the batch boundary, up to one window later: an action that is a cast
                // (CastOther) goes off there, and so does the white swing a struck unit answers with in the tick after the action. A short aura
                // (300 ms) can run out meanwhile, and its expiry is held to the same boundary (SpellSystem.HoldAuraExpiry).
                _rig.World.RunTick(50);
                SettleBatch(swings: true);
                bool present = _probe.Holders(_rig.System, unit, spell.Id).Count > 0;
                if (!present && held.Count > 0 && held.All(h => h.IsRemoved && h.RemoveMode == AuraRemoveMode.Expire))
                {
                    // The aura ran out on its own before the action's effect could be seen: that proves nothing either way (a broken
                    // mapping would look as if it worked, a flagless aura as if the action wrongly ended it), so the check did not run. Without
                    // batching no production action lets time pass, so this only happens with batching on or an action that waits.
                    state = "not-run:expired";
                    return;
                }

                state = "ran";
                if (expected && present)
                {
                    Violate(SweepCategory.InterruptNotRemoved, spell.Id, $"{action.Name} flags=0x{flags:X}");
                }
                else if (!expected && !present && unit.IsAlive && !spell.IsChanneled && spell.ProcCharges == 0 && spell.ProcFlags == ProcFlags.None)
                {
                    Violate(SweepCategory.InterruptRemovedWithoutFlag, spell.Id, $"{action.Name} flags=0x{flags:X}");
                }

                if (IsCasting(scene.Caster))
                {
                    _rig.System.InterruptNonMeleeSpells(scene.Caster);
                }
            });
            states.Add($"{action.Name}={state}");
        }

        Checked('c', string.Join(',', states));
    }

    // ---- (d) cast and channel interrupt flags ---------------------------------------------------------------------------

    private void CheckCastInterrupts(SpellInfo spell)
    {
        bool channel = spell.IsChanneled;
        if (spell.CastTime.Base <= 0 && !channel)
        {
            Checked('d', "not-run:instant");
            return;
        }

        // Movement: SPELL_INTERRUPT_FLAG_MOVEMENT for a cast; a channel's CHANNEL_FLAG_MOVEMENT (or the aura/cast movement bits).
        bool expectMove = channel
            ? spell.ChannelInterruptFlags.HasFlag(SpellAuraInterruptFlags.Moving) || spell.AuraInterruptFlags.HasFlag(SpellAuraInterruptFlags.Moving)
                || spell.InterruptFlags.HasFlag(SpellInterruptFlags.Movement)
            : spell.InterruptFlags.HasFlag(SpellInterruptFlags.Movement);
        string state = "not-run";
        Scene(spell, PlayerCheats.NoCooldown | PlayerCheats.NoPowerCost, 5f, scene =>
        {
            if (Cast(scene, triggered: false) != SpellCastResult.CastOk)
            {
                state = "not-run:refused";
                return;
            }

            _rig.World.RunTick(50);
            if (!IsCasting(scene.Caster))
            {
                state = "not-run:not-casting";
                return;
            }

            // The move handlers may reject a step (speed, collision): a rejected move moved nothing and proves nothing.
            (float x, float y) = (scene.Caster.X, scene.Caster.Y);
            if (!_actions.Move(scene, scene.Caster) || (scene.Caster.X == x && scene.Caster.Y == y))
            {
                state = "not-run:move-rejected";
                _rig.System.InterruptNonMeleeSpells(scene.Caster);
                return;
            }

            _rig.World.RunTick(50);
            bool cancelled = !IsCasting(scene.Caster);
            state = "ran";
            if (expectMove && !cancelled)
            {
                Violate(SweepCategory.CastNotInterrupted, spell.Id, $"{(channel ? "channel" : "cast")} move");
            }
            else if (!expectMove && cancelled && !channel)
            {
                Violate(SweepCategory.CastInterruptedWithoutFlag, spell.Id, "cast move");
            }

            _rig.System.InterruptNonMeleeSpells(scene.Caster);
        });
        Checked('d', state);
    }

    // ---- (e) cooldowns ----------------------------------------------------------------------------------------------

    private void CheckCooldowns(SpellInfo spell)
    {
        bool own = spell.RecoveryTime > 0;
        bool category = spell.Category > 0 && spell.CategoryRecoveryTime > 0;
        if (!own && !category)
        {
            Checked('e', "not-run:no-cooldown");
            return;
        }

        if (spell.IsPassive || spell.HasAttribute(SpellAttributes.CooldownOnEvent))
        {
            Checked('e', "not-run:passive-or-on-event");
            return;
        }

        string state = "not-run";
        Scene(spell, PlayerCheats.NoPowerCost | PlayerCheats.NoCastTime, 5f, scene =>
        {
            if (Cast(scene, triggered: false) != SpellCastResult.CastOk)
            {
                state = "not-run:refused";
                return;
            }

            WaitOutCast(scene, spell);
            if (IsCasting(scene.Caster))
            {
                state = "not-run:still-casting";
                return;
            }

            state = "ran";
            uint now = _rig.World.NowMs;
            if (own && _probe.IsReady(_rig.System, scene.Caster, spell))
            {
                Violate(SweepCategory.CooldownMissing, spell.Id, $"recovery={spell.RecoveryTime}");
            }

            if (category && !(_probe.CategoryCooldownUntil(_rig.System, scene.Caster, spell.Category) is { } until && until > now))
            {
                Violate(SweepCategory.CategoryCooldownMissing, spell.Id, $"category={spell.Category} recovery={spell.CategoryRecoveryTime}");
            }

            uint wait = Math.Max(spell.RecoveryTime, spell.CategoryRecoveryTime);
            if (wait <= MaxSimulatedMs)
            {
                Advance(wait + 200, 1000);
                if (!_probe.IsReady(_rig.System, scene.Caster, spell))
                {
                    Violate(SweepCategory.CooldownNotExpired, spell.Id, $"not ready {wait + 200} ms after the cast");
                }
            }
        });
        Checked('e', state);
    }

    // ---- (f) costs --------------------------------------------------------------------------------------------------

    private void CheckCost(SpellInfo spell)
    {
        if (spell.ManaCost == 0 && spell.ManaCostPercentage == 0)
        {
            Checked('f', "not-run:no-cost");
            return;
        }

        if (spell.PowerType is not ((int)PowerType.Mana or (int)PowerType.Rage or (int)PowerType.Energy))
        {
            Checked('f', $"not-run:power-type-{spell.PowerType}");
            return;
        }

        PowerType power = (PowerType)spell.PowerType;
        Class owner = ClassFor(spell);
        Class cls = SpellSweepRig.PowerFor(owner) == power ? owner : power switch
        {
            PowerType.Rage => Class.Warrior,
            PowerType.Energy => Class.Rogue,
            _ => Class.Mage,
        };
        string state = "not-run";
        Scene(spell, PlayerCheats.NoCooldown | PlayerCheats.NoCastTime, 5f, scene =>
        {
            int field = UpdateFields.UnitFieldPower1 + (int)power;
            uint max = scene.Caster.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power);
            scene.Caster.SetUInt32(field, max);
            uint before = _probe.Power(scene.Caster, power);
            uint baseMana = scene.Caster.GetUInt32(UpdateFields.UnitFieldBaseMana);
            SpellCastResult result = Cast(scene, triggered: false);
            if (result != SpellCastResult.CastOk)
            {
                state = "not-run:refused-" + result;
                return;
            }

            // A channel pays its cost when it starts (vmangos Spell::cast takes the power before the channel runs), so it is read
            // now; a cast with a cast time cannot be running here (NoCastTime), so anything still casting must be a channel.
            uint after = _probe.Power(scene.Caster, power);
            bool channelling = IsCasting(scene.Caster);
            if (channelling)
            {
                _rig.System.InterruptNonMeleeSpells(scene.Caster);
            }

            if (channelling && !spell.IsChanneled)
            {
                state = "not-run:not-instant";
                return;
            }

            state = channelling ? "ran:channel" : "ran";
            long deducted = (long)before - after;
            long minimum = spell.ManaCost + (power == PowerType.Mana ? (long)baseMana * spell.ManaCostPercentage / 100 : 0);
            long maximum = minimum + (long)spell.ManaCostPerLevel * scene.Caster.Level;
            if (deducted < minimum - 1)
            {
                Violate(SweepCategory.CostNotDeducted, spell.Id, $"{power} expected>={minimum}");
            }
            else if (deducted > maximum + 1 && power != PowerType.Rage)
            {
                Violate(SweepCategory.CostOverDeducted, spell.Id, $"{power} expected<={maximum}");
            }
        }, cls);
        Checked('f', state);
    }

    // ---- (g) range --------------------------------------------------------------------------------------------------

    private void CheckRange(SpellInfo spell)
    {
        SweepTargetKind kind = SweepTargets.KindOf(spell);
        if (kind is not (SweepTargetKind.Hostile or SweepTargetKind.Friendly) || !SweepTargets.HasExplicitUnitTarget(spell)
            || spell.Range.Max <= 0 || spell.Range.Max >= 1000)
        {
            Checked('g', "not-run:no-unit-range");
            return;
        }

        string state = "not-run";
        Scene(spell, PlayerCheats.NoCooldown | PlayerCheats.NoPowerCost | PlayerCheats.NoCastTime, spell.Range.Max + 15f, scene =>
        {
            // Only the range refusal proves the range check: a refusal for anything else (reagents, target, line of sight) says
            // nothing about it, so it is recorded as not run (and the coverage file ratchets that).
            SpellCastResult result = Cast(scene, triggered: false);
            if (result == SpellCastResult.OutOfRange)
            {
                state = "ran";
            }
            else if (result == SpellCastResult.CastOk)
            {
                state = "ran";
                Violate(SweepCategory.OutOfRangeAccepted, spell.Id, $"max={spell.Range.Max}");
                _rig.System.InterruptNonMeleeSpells(scene.Caster);
            }
            else
            {
                state = "not-run:refused-" + result;
            }
        });
        Checked('g', state);
    }

    // ---- (h) stacking -----------------------------------------------------------------------------------------------

    private void CheckStacking(SpellInfo spell, bool applied)
    {
        if (!applied || spell.IsPassive || spell.StackAmount <= 1)
        {
            Checked('h', "not-run:" + (applied ? "no-stacks" : "no-aura"));
            return;
        }

        string state = "not-run";
        Scene(spell, PlayerCheats.NoCooldown | PlayerCheats.NoPowerCost | PlayerCheats.NoCastTime, 5f, scene =>
        {
            for (int i = 0; i < spell.StackAmount + 2; i++)
            {
                Cast(scene, triggered: true);
                _rig.World.RunTick(0);
            }

            Unit? unit = FindAuraUnit(scene);
            if (unit is null)
            {
                state = "not-run:no-aura";
                return;
            }

            state = "ran";
            // One caster's applications of a stacking aura share one holder (vmangos Unit::AddSpellAuraHolder): over the cap is a
            // holder whose count passed StackAmount, or the applications spread over several holders of the same caster.
            SpellAuraHolder[] mine = [.. _probe.Holders(_rig.System, unit, spell.Id).Where(h => h.CasterGuid == scene.Caster.Guid)];
            if (mine.Length > 1)
            {
                Violate(SweepCategory.StackOverCap, spell.Id, $"cap={spell.StackAmount} holders={mine.Length}");
            }
            else if (mine.Sum(h => (int)h.StackAmount) > spell.StackAmount)
            {
                Violate(SweepCategory.StackOverCap, spell.Id, $"cap={spell.StackAmount}");
            }
        });
        Checked('h', state);
    }

    // ---- (i) dispel -------------------------------------------------------------------------------------------------

    private void IndexDispellers(SpellStore store)
    {
        // The lowest-id player spell (class family) with a DISPEL effect of each type, for friendly and for hostile targets.
        foreach (SpellInfo spell in store.All.Where(s => SpellSweepCatalog.ClassOfFamily(s.SpellFamilyName) is not null).OrderBy(s => s.Id))
        {
            foreach (SpellEffectInfo effect in spell.Effects)
            {
                if (effect.Effect != SpellEffectName.Dispel || effect.MiscValue is < 1 or > 4)
                {
                    continue;
                }

                bool hostile = effect.TargetA is SpellImplicitTarget.UnitEnemy || (effect.TargetA is SpellImplicitTarget.Unit);
                bool friendly = effect.TargetA is SpellImplicitTarget.UnitFriend or SpellImplicitTarget.Unit;
                if (hostile)
                {
                    _dispellers.TryAdd(((uint)effect.MiscValue, true), spell.Id);
                }

                if (friendly)
                {
                    _dispellers.TryAdd(((uint)effect.MiscValue, false), spell.Id);
                }
            }
        }
    }

    private void CheckDispel(SpellInfo spell, bool applied)
    {
        if (!applied || spell.IsPassive || spell.Dispel is < 1 or > 4)
        {
            Checked('i', "not-run:" + (applied ? "no-dispel-type" : "no-aura"));
            return;
        }

        string state = "not-run";
        Scene(spell, PlayerCheats.NoCooldown | PlayerCheats.NoPowerCost | PlayerCheats.NoCastTime, 5f, scene =>
        {
            if (Cast(scene, triggered: false) != SpellCastResult.CastOk)
            {
                Cast(scene, triggered: true);
            }

            Unit? unit = FindAuraUnit(scene);
            if (unit is null)
            {
                state = "not-run:no-aura";
                return;
            }

            bool positive = _probe.Holders(_rig.System, unit, spell.Id)[0].IsPositive;
            // A friendly dispel removes harmful auras, a hostile one helpful auras (vmangos Spell::EffectDispel / Unit::GetDispellableAuraList).
            bool hostileDispel = positive;
            if (!_dispellers.TryGetValue((spell.Dispel, hostileDispel), out uint dispelSpell))
            {
                state = "not-run:no-dispeller";
                return;
            }

            Unit dispeller = unit is Player
                ? hostileDispel ? scene.Striker() : scene.AddPlayer(Class.Priest, 60, 3f, 3f, (Race)((Player)unit).Race)
                : hostileDispel ? scene.AddPlayer(Class.Priest, 60, 3f, 3f) : scene.AddCreature(hostile: true, 60, 3f, 3f);
            for (int i = 0; i < 5 && _probe.Holders(_rig.System, unit, spell.Id).Count > 0; i++)
            {
                _probe.Cast(_rig.System, dispeller, dispelSpell, SpellCastTargets.ForUnit(unit.Guid), triggered: true);
                _rig.World.RunTick(0);
            }

            state = "ran:" + dispelSpell;
            if (_probe.Holders(_rig.System, unit, spell.Id).Count > 0)
            {
                Violate(SweepCategory.NotDispelled, spell.Id, $"type={spell.Dispel} {(positive ? "helpful" : "harmful")}");
            }
        });
        Checked('i', state);
    }

    // ---- (j) invariants ---------------------------------------------------------------------------------------------

    /// <summary>Ticks of 100 ms after the cleanup before the invariants are read: longer than one batching interval (400 ms).</summary>
    private const int SettleTicks = 6;

    private void CheckInvariants(SweepScene scene)
    {
        uint spellId = scene.Spell.Id;
        if (IsCasting(scene.Caster))
        {
            _rig.System.InterruptNonMeleeSpells(scene.Caster);
        }

        foreach (Unit unit in scene.Units.ToArray())
        {
            _rig.System.RemoveAuras(unit, spellId);
        }

        uint before = _rig.World.NowMs;
        try
        {
            // Settle past the deferred follow-ups an aura removal schedules (vmangos BATCHING_INTERVAL, 400 ms: Spirit of Redemption
            // stuns on removal and unstuns one interval later), in tick-sized steps so each scheduled step runs on its own tick.
            for (int step = 0; step < SettleTicks; step++)
            {
                _rig.World.RunTick(100);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Violate(SweepCategory.TickStopped, spellId, $"{ex.GetType().Name} at {StableFrame(ex)}");
            return;
        }

        if (_rig.World.NowMs == before)
        {
            Violate(SweepCategory.TickStopped, spellId, "world clock did not advance");
        }

        if (IsCasting(scene.Caster))
        {
            Violate(SweepCategory.StuckCasting, spellId, "cast survives interrupt and aura removal");
        }

        foreach (Unit unit in scene.Units.Where(u => u.IsInWorld))
        {
            string who = unit is Player ? "player" : "creature";
            if (unit.Health > unit.MaxHealth)
            {
                Violate(SweepCategory.HealthOutOfRange, spellId, $"{who} health>max");
            }

            bool dead = unit.Combat.DeathState != DeathState.Alive;
            if (unit.Health == 0 && !dead)
            {
                Violate(SweepCategory.DeathStateInconsistent, spellId, $"{who} health 0 but alive state");
            }

            if (unit is Player player && (player.Flags & PlayerFlags.Ghost) != 0 && unit.Combat.DeathState == DeathState.Alive && unit.Health > 0
                && (_rig.System.GetState(unit.Guid) is not { } ghostState || !ghostState.AuraHolders.Any(h => !h.IsRemoved && h.Spell.HasAura(AuraType.Ghost))))
            {
                Violate(SweepCategory.DeathStateInconsistent, spellId, "player ghost flag while alive");
            }

            IReadOnlyList<SpellAuraHolder> holders = _rig.System.GetState(unit.Guid)?.AuraHolders.Where(h => !h.IsRemoved).ToArray() ?? [];
            UnitFlags flags = _probe.Flags(unit);
            foreach ((_, UnitFlags flag) in FlagAuras)
            {
                if ((flags & flag) != 0
                    && !(flag == UnitFlags.Stunned && dead)
                    && !FlagAuras.Where(f => f.Flag == flag).Any(f => holders.Any(h => h.Spell.HasAura(f.Type))))
                {
                    Violate(SweepCategory.LeftoverFlag, spellId, $"{who} {flag}");
                }
            }

            if (_rig.System.IsFeigningDeath(unit) && !holders.Any(h => h.Spell.HasAura(AuraType.FeignDeath)))
            {
                Violate(SweepCategory.LeftoverFlag, spellId, $"{who} feign-death");
            }

            // A player's root is the server's order (Player.IsRooted): the movement flag itself waits for the client's ack.
            bool rooted = unit is Player rootedPlayer ? rootedPlayer.IsRooted : (unit.Movement.Flags & MovementFlags.Root) != 0;
            if (rooted && !dead && !holders.Any(h => h.Spell.HasAura(AuraType.ModRoot) || h.Spell.HasAura(AuraType.ModStun)))
            {
                Violate(SweepCategory.LeftoverFlag, spellId, $"{who} root");
            }
        }
    }

    /// <summary>The top frame of an exception without file and line (stable across builds: it is part of the baseline key).</summary>
    internal static string StableFrame(Exception exception)
    {
        string frame = SweepFaultCapture.TopFrame(exception);
        int at = frame.IndexOf(" in ", StringComparison.Ordinal);
        return at > 0 ? frame[..at] : frame;
    }
}

/// <summary>The stat block an aura changes and must restore (stats, resistances, maxima, attack, display, scale).</summary>
internal sealed record Snapshot(uint[] Values)
{
    private static readonly (string Name, int Field, int Count)[] Fields =
    [
        ("stat", UpdateFields.UnitFieldStat0, 5),
        ("resistance", UpdateFields.UnitFieldResistances, 7),
        ("max-health", UpdateFields.UnitFieldMaxhealth, 1),
        ("max-power", UpdateFields.UnitFieldMaxpower1, 5),
        ("base-attack-time", UpdateFields.UnitFieldBaseattacktime, 2),
        ("min-damage", UpdateFields.UnitFieldMindamage, 2),
        ("attack-power", UpdateFields.UnitFieldAttackPower, 1),
        ("display", UpdateFields.UnitFieldDisplayid, 1),
        ("scale", UpdateFields.ObjectFieldScaleX, 1),
    ];

    public static Snapshot Of(Unit unit)
    {
        var values = new List<uint>();
        foreach ((_, int field, int count) in Fields)
        {
            for (int i = 0; i < count; i++)
            {
                values.Add(unit.GetUInt32(field + i));
            }
        }

        return new Snapshot([.. values]);
    }

    /// <summary>The first field that differs, by name, or null.</summary>
    public string? Differs(Snapshot other)
    {
        int index = 0;
        foreach ((string name, _, int count) in Fields)
        {
            for (int i = 0; i < count; i++, index++)
            {
                if (Values[index] != other.Values[index])
                {
                    return count == 1 ? name : $"{name}{i}";
                }
            }
        }

        return null;
    }
}

/// <summary>A deterministic <see cref="ICombatRandom"/> (same bounds as <see cref="SharedCombatRandom"/>).</summary>
internal sealed class SeededCombatRandom(int seed) : ICombatRandom
{
    private readonly Random _random = new(seed);

    public int Next(int minInclusive, int maxInclusive) => _random.Next(minInclusive, maxInclusive + 1);

    public float NextFloat(float min, float max) => min + (float)(_random.NextDouble() * (max - min));
}
