using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Procs;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The proc engine (vmangos <c>SpellCaster::ProcDamageAndSpell</c>, Objects/SpellCaster.cpp:270-323; <c>Unit::ProcDamageAndSpellFor</c>,
/// Objects/Unit.cpp:8917-9002; <c>Unit::IsTriggeredAtSpellProcEvent</c>, UnitAuraProcHandler.cpp:239-499; <c>Unit::HandleTriggers</c>,
/// Objects/Unit.cpp:4245-4345). The hit paths (white swings, spell hits and misses, periodic ticks, kills, reflects, cast ends) describe what
/// happened with a <see cref="ProcEvent"/>; the engine collects the auras of the actor and of the victim that the event can proc, rolls them,
/// then runs each proccing effect through its aura type's handler (<see cref="RegisterProcHandler"/>) or the spell's script
/// (<see cref="RegisterProcScript"/>) and spends charges. docs/areas/procs.md.
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>A proc event inside a proc handler is allowed this deep (the engine is synchronous; vmangos bounds the chain by m_canTrigger only).</summary>
    public const int MaxProcDepth = 4;

    private Dictionary<AuraType, AuraProcHandler>? _procHandlers;
    private readonly Dictionary<uint, IProcScript> _procScripts = [];
    private int _procDepth;

    /// <summary>The spell_proc_event conditions (empty until the world feature loads them).</summary>
    public ISpellProcEventCatalog ProcEvents { get; set; } = EmptySpellProcEventCatalog.Instance;

    /// <summary>
    /// Whether a unit stands outdoors, for ONLY_PROC_OUTDOORS auras (vmangos TerrainInfo::IsOutdoors): the map's collision data (outdoors outside
    /// every WMO group, and everywhere when no vmap data is loaded).
    /// </summary>
    public Func<Unit, bool> IsOutdoors { get; set; } = static unit => unit.Map?.Collision.IsOutdoors(unit.X, unit.Y, unit.Z) ?? true;

    /// <summary>Raised after an event went through the engine (diagnostics and tests).</summary>
    public event Action<Unit, ProcEvent>? ProcEventProcessed;

    private Dictionary<AuraType, AuraProcHandler> ProcHandlers => _procHandlers ??= BuiltInProcHandlers.Create();

    /// <summary>Install or replace the proc handler of an aura type (vmangos <c>AuraProcHandler[]</c>). An aura type without one procs as HandleNULLProc: OK.</summary>
    public void RegisterProcHandler(AuraType type, AuraProcHandler handler)
        => ProcHandlers[type] = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <summary>Whether an aura type has a proc handler of its own.</summary>
    public bool HasProcHandler(AuraType type) => ProcHandlers.ContainsKey(type);

    /// <summary>
    /// Install the proc script of an aura spell (vmangos <c>AuraScript::OnCheckProc</c> / <c>OnProc</c>). One script per spell; a second
    /// registration for the same spell is a startup error, like <see cref="RegisterPeriodicTriggerScript"/>.
    /// </summary>
    public void RegisterProcScript(uint spellId, IProcScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (!_procScripts.TryAdd(spellId, script))
        {
            throw new InvalidOperationException($"spell {spellId} already has a proc script");
        }
    }

    /// <summary>The proc script of <paramref name="spellId"/>, or null.</summary>
    public IProcScript? FindProcScript(uint spellId) => _procScripts.GetValueOrDefault(spellId);

    /// <summary>
    /// vmangos <c>SpellCaster::ProcDamageAndSpell</c> with <c>PROC_PROCESS_ALL</c> (this engine has no spell batching, so the instant and delayed
    /// passes are one): the attacker's auras are collected first, then the living victim's, and only then are they handled, so an aura one side
    /// applies while proccing never procs from the same event (vmangos ProcDamageAndSpell_real, SpellCaster.cpp:307-323).
    /// </summary>
    public void ProcDamageAndSpell(Unit actor, in ProcEvent procEvent)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsInWorld || (procEvent.Victim is { } v && !ReferenceEquals(v.Map, actor.Map)) || _procDepth >= MaxProcDepth
            || IsQuestSettlementPending(actor) || IsQuestSettlementPending(procEvent.Victim))
        {
            return;
        }

        _procDepth++;
        try
        {
            uint now = NowMs;
            var triggered = new List<TriggeredProc>();
            if (procEvent.AttackerFlags != ProcFlags.None)
            {
                CollectProcs(actor, isVictim: false, procEvent.Victim, procEvent, now, triggered);
            }

            if (procEvent.Victim is { IsAlive: true } victim && procEvent.VictimFlags != ProcFlags.None)
            {
                CollectProcs(victim, isVictim: true, actor, procEvent, now, triggered);
            }

            HandleTriggers(procEvent, triggered);
        }
        finally
        {
            _procDepth--;
        }

        ProcEventProcessed?.Invoke(actor, procEvent);
    }

    private readonly record struct TriggeredProc(SpellProcEventRecord? Entry, SpellAuraHolder Holder, Unit Owner, Unit? Target, ProcFlags ProcFlag, bool IsVictim, ProcFlagsEx Extra);

    /// <summary>vmangos <c>Unit::ProcDamageAndSpellFor</c> (Unit.cpp:8917-9002): the auras of <paramref name="owner"/> the event can proc.</summary>
    private void CollectProcs(Unit owner, bool isVictim, Unit? target, in ProcEvent e, uint now, List<TriggeredProc> triggered)
    {
        ProcFlags procFlag = isVictim ? e.VictimFlags : e.AttackerFlags;
        // "Charges will not generate off auto attacks or npc attacks by trying to sit down and force a crit" (> 1.7.1, Unit.cpp:8981-8989).
        ProcFlagsEx extra = isVictim && e.ProcSpell is null && e.Victim is { } sitting && !ProcFlagRules.IsStandingUp(sitting)
            ? e.Extra & ~ProcFlagsEx.CriticalHit
            : e.Extra;
        foreach (SpellAuraHolder holder in GetAuras(owner).ToArray())
        {
            // "Can not proc on self", and skip deleted auras.
            if (holder.IsRemoved || (e.ProcSpell is { } procSpell && procSpell.Id == holder.Spell.Id))
            {
                continue;
            }

            // "don't reroll chance for each target in this case"
            if (((uint)holder.Spell.AttributesEx2 & ProcAttributes.Ex2ProcCooldownOnFailure) != 0 && !IsProcSpellReady(owner, holder.Spell))
            {
                continue;
            }

            // "prevent delayed procs from removing auras applied after the proc happened (Frostbite removed by the Frostbolt that applied it)":
            // an aura of the event's own actor applied after the event began does not proc from it.
            if (holder.AppliedAtMs > now && ((isVictim && target is not null && target.Guid == holder.CasterGuid) || (!isVictim && owner.Guid == holder.CasterGuid)))
            {
                continue;
            }

            // An aura that carries a charged spell modifier spends its charges on the casts that use it, not on procs (Unit.cpp:8956-8974).
            if (holder.Charges > 0 && holder.Auras.Any(a => a is { Type: AuraType.AddFlatModifier or AuraType.AddPctModifier }))
            {
                continue;
            }

            ProcTriggerCheck result = IsTriggeredAtSpellProcEvent(owner, target, holder, e.ProcSpell, procFlag, extra, e.AttackType, isVictim,
                out SpellProcEventRecord? entry, e.SpellTriggeredByAuraOrItem);
            if (result != ProcTriggerCheck.Ok)
            {
                if (result == ProcTriggerCheck.RollFailed && ((uint)holder.Spell.AttributesEx2 & ProcAttributes.Ex2ProcCooldownOnFailure) != 0
                    && entry is { Cooldown: > 0 } failed)
                {
                    AddProcCooldown(owner, holder.Spell, failed.Cooldown);
                }

                continue;
            }

            triggered.Add(new TriggeredProc(entry, holder, owner, target, procFlag, isVictim, extra));
        }
    }

    /// <summary>
    /// vmangos <c>Unit::IsTriggeredAtSpellProcEvent</c> (UnitAuraProcHandler.cpp:239-499): the hard-coded exceptions, the spell's proc script, the
    /// spell_proc_event or Spell.dbc proc flags against the event, the extra requirements, the kill credit, the self-proc rule, the equipment the
    /// aura needs, the proc-from-procs rule, the outdoors and on-caster rules, and finally the chance (PPM for the attacker side, the CHANCE_OF_SUCCESS
    /// spell modifier).
    /// </summary>
    internal ProcTriggerCheck IsTriggeredAtSpellProcEvent(Unit owner, Unit? target, SpellAuraHolder holder, SpellInfo? procSpell, ProcFlags procFlag,
        ProcFlagsEx procExtra, WeaponAttackType attackType, bool isVictim, out SpellProcEventRecord? entry, bool spellTriggeredByAuraOrItem)
    {
        entry = null;
        SpellInfo aura = holder.Spell;

        // Flurry can't proc on additional windfury attacks.
        if (aura.SpellIconId == 108 && aura.SpellVisual == 2759 && owner.Combat.HasPendingExtraAttacks)
        {
            return ProcTriggerCheck.Failed;
        }

        // Don't proc weapons on Sap.
        if (aura.Id is 14076 or 14094 or 14095)
        {
            return ProcTriggerCheck.Failed;
        }

        if (procSpell is not null && (procExtra & ProcFlagsEx.CastEnd) == 0 && HardCodedProcCheck(owner, aura, procSpell, procFlag, procExtra, isVictim) is { } hardCoded)
        {
            return hardCoded;
        }

        if (_procScripts.GetValueOrDefault(aura.Id) is { } script
            && script.CheckProc(new ProcCheckContext(this, owner, target, holder, procSpell, procFlag, procExtra, attackType, isVictim)) is { } scripted)
        {
            return scripted;
        }

        entry = ProcEvents.Find(aura.Id);

        // Fear Ward always procs on any Fear (except ones cast by ourselves).
        if (procSpell is not null && aura.Id == 6346 && isVictim)
        {
            return procSpell.Mechanic == (uint)SpellMechanic.Fear ? ProcTriggerCheck.Ok : ProcTriggerCheck.Failed;
        }

        ProcFlags eventProcFlag = entry is { ProcFlags: > 0 } custom ? (ProcFlags)custom.ProcFlags : aura.ProcFlags;
        if (eventProcFlag == ProcFlags.None || !IsSpellProcEventCanTriggeredBy(entry, eventProcFlag, procSpell, procFlag, procExtra))
        {
            return ProcTriggerCheck.Failed;
        }

        // "In most cases req get honor or XP from kill".
        if ((eventProcFlag & ProcFlags.Kill) != 0 && owner is Player killer && (target is null || !HonorKillRewards.IsHonorOrXpTarget(killer, target)))
        {
            return ProcTriggerCheck.Failed;
        }

        // "Aura added by spell can't trigger from self (prevent drop charges/do triggers), but except periodic triggers".
        if (procSpell is not null && procSpell.Id == aura.Id && (eventProcFlag & ProcFlags.TakeHarmfulPeriodic) == 0)
        {
            return ProcTriggerCheck.Failed;
        }

        if (!isVictim && owner is Player player && (aura.AttributesEx3 & ProcAttributes.Ex3NoProcEquipRequirement) == 0
            && !MeetsProcEquipRequirement(player, aura, attackType))
        {
            return ProcTriggerCheck.Failed;
        }

        if (spellTriggeredByAuraOrItem && procSpell is not null && (procSpell.AttributesEx3 & ProcAttributes.Ex3NotAProc) == 0
            && (aura.AttributesEx3 & ProcAttributes.Ex3CanProcFromProcs) == 0)
        {
            return ProcTriggerCheck.Failed;
        }

        if ((aura.AttributesEx3 & ProcAttributes.Ex3OnlyProcOutdoors) != 0 && !IsOutdoors(owner))
        {
            return ProcTriggerCheck.Failed;
        }

        if ((aura.AttributesEx3 & ProcAttributes.Ex3OnlyProcOnCaster) != 0 && holder.Target.Guid != holder.CasterGuid)
        {
            return ProcTriggerCheck.Failed;
        }

        float chance = aura.ProcChance;
        if (entry is { CustomChance: > 0 } customChance)
        {
            chance = customChance.CustomChance;
        }

        if (!isVictim && entry is { PpmRate: > 0 } ppm)
        {
            chance = PpmChance(owner.Combat.GetAttackTime(attackType), ppm.PpmRate);
        }

        if (owner.GetCharmerOrOwnerPlayerOrSelf() is { } modOwner)
        {
            chance = SpellModifiers.Apply(modOwner, aura, SpellModOp.ChanceOfSuccess, chance);
        }

        return Random.NextDouble() * 100d < chance ? ProcTriggerCheck.Ok : ProcTriggerCheck.RollFailed;
    }

    /// <summary>vmangos <c>Unit::GetPPMProcChance</c>: a weapon of <paramref name="attackTimeMs"/> procs <paramref name="ppm"/> times a minute.</summary>
    public static float PpmChance(uint attackTimeMs, float ppm) => attackTimeMs * ppm / 600.0f;

    /// <summary>The hard-coded checks of UnitAuraProcHandler.cpp:258-385 (build 5875 branches) for an event with a spell that is not a cast end.</summary>
    private ProcTriggerCheck? HardCodedProcCheck(Unit owner, SpellInfo aura, SpellInfo procSpell, ProcFlags procFlag, ProcFlagsEx procExtra, bool isVictim)
    {
        // Eye for an Eye (> 1.9.4): only a critical harmful spell.
        if (aura.Id is 9799 or 25988)
        {
            if (!((procFlag & ProcFlags.TakeHarmfulSpell) != 0 && (procExtra & ProcFlagsEx.CriticalHit) != 0))
            {
                return ProcTriggerCheck.Failed;
            }
        }

        // Improved Lay on Hands.
        if (aura.SpellIconId == 79 && aura.SpellFamilyName == 10)
        {
            return procSpell.SpellFamilyName == 10 && procSpell.SpellIconId == 79 && procSpell.Category == 56 && !isVictim
                ? ProcTriggerCheck.Ok
                : ProcTriggerCheck.Failed;
        }

        // Wrath of Cenarius - Spell Blasting: a negative magical effect that lands.
        if (aura.Id == 25906 && !isVictim && procSpell.DamageClass == SpellDamageClass.Magic && !procSpell.IsPositive
            && (procExtra & (ProcFlagsEx.NormalHit | ProcFlagsEx.CriticalHit)) != 0
            && !(procSpell.Effects.Any(e => e.Effect == SpellEffectName.ApplyAura) && (procFlag & ProcFlags.DealHarmfulPeriodic) != 0))
        {
            return Random.NextDouble() * 100d < aura.ProcChance ? ProcTriggerCheck.Ok : ProcTriggerCheck.RollFailed;
        }

        // Omen of Clarity procs from swings only; a spell event never procs it (the swing case is the generic PPM path below).
        if (aura.Id == 16864)
        {
            return ProcTriggerCheck.Failed;
        }

        // Inspiration: a critical Prayer of Healing, Heal, Flash Heal or Greater Heal.
        if (aura.SpellIconId == 79 && aura.SpellFamilyName == 6)
        {
            const ulong heals = (1UL << 9) | (1UL << 10) | (1UL << 11) | (1UL << 12) | (1UL << 28);
            return procSpell.SpellFamilyName == 6 && (procSpell.SpellFamilyFlags & heals) != 0
                && (procExtra & ProcFlagsEx.CriticalHit) != 0 && (procFlag & ProcFlags.DealHelpfulSpell) != 0
                    ? ProcTriggerCheck.Ok
                    : ProcTriggerCheck.Failed;
        }

        // SPELL_AURA_ADD_TARGET_TRIGGER: the spell hit is handled at the cast's end (HandleAddTargetTriggerAuras); only Frosty Zap procs here.
        if (aura.Effects.Count > 0 && aura.Effects[0].Effect == SpellEffectName.ApplyAura && aura.Effects[0].AuraType == AuraType.AddTargetTrigger)
        {
            if (isVictim)
            {
                return ProcTriggerCheck.Failed;
            }

            return aura.Id == 24392 && procSpell.SpellFamilyName == aura.SpellFamilyName && (procSpell.SpellFamilyFlags & aura.Effects[0].ItemType) != 0
                ? ProcTriggerCheck.Ok
                : ProcTriggerCheck.Failed;
        }

        // Elemental Mastery: not consumed by a spell that did not benefit from the crit bonus.
        if (aura.Id == 16166 && (procExtra & ProcFlagsEx.NormalHit) != 0)
        {
            return ProcTriggerCheck.Failed;
        }

        return null;
    }

    /// <summary>vmangos <c>SpellMgr::IsSpellProcEventCanTriggeredBy</c> (Spells/SpellMgr.cpp:441-505).</summary>
    internal static bool IsSpellProcEventCanTriggeredBy(SpellProcEventRecord? entry, ProcFlags eventProcFlag, SpellInfo? procSpell, ProcFlags procFlags, ProcFlagsEx procExtra)
    {
        var eventProcEx = (ProcFlagsEx)(entry?.ProcEx ?? 0);
        if ((procFlags & eventProcFlag) == 0)
        {
            return false;
        }

        // Either procs only on cast end, or only on hit.
        if ((procExtra & ProcFlagsEx.CastEnd) != (eventProcEx & ProcFlagsEx.CastEnd))
        {
            return false;
        }

        if ((eventProcFlag & (ProcFlags.Heartbeat | ProcFlags.Kill | ProcFlags.OnTrapActivation)) != 0)
        {
            return true;
        }

        if (entry is not null)
        {
            if (procSpell is null)
            {
                if (entry.SchoolMask != 0 && (entry.SchoolMask & ProcFlagRules.MeleeSchoolMask) == 0)
                {
                    return false;
                }
            }
            else
            {
                if (entry.SchoolMask != 0 && (entry.SchoolMask & procSpell.SchoolMask()) == 0)
                {
                    return false;
                }

                if (entry.SpellFamilyName != 0 && entry.SpellFamilyName != procSpell.SpellFamilyName)
                {
                    return false;
                }
            }
        }

        if (eventProcEx == ProcFlagsEx.None)
        {
            // Don't allow proc from periodic heal if no extra requirement is defined.
            if ((eventProcFlag & (ProcFlags.DealHarmfulPeriodic | ProcFlags.TakeHarmfulPeriodic)) != 0 && (procExtra & ProcFlagsEx.PeriodicPositive) != 0)
            {
                return false;
            }

            return (procExtra & (ProcFlagsEx.NormalHit | ProcFlagsEx.CriticalHit)) != 0;
        }

        if ((eventProcEx & ProcFlagsEx.TriggerAlways) != 0)
        {
            return true;
        }

        if ((eventProcEx & ProcFlagsEx.NoPeriodic) != 0
            && ((procFlags & (ProcFlags.DealHarmfulPeriodic | ProcFlags.TakeHarmfulPeriodic)) != 0 || (procSpell is not null && AppliesPeriodicAura(procSpell))))
        {
            return false;
        }

        return (eventProcEx & procExtra) != 0;
    }

    private static bool AppliesPeriodicAura(SpellInfo spell) => spell.Effects.Any(e => e.Effect == SpellEffectName.ApplyAura && e.Amplitude != 0);

    /// <summary>vmangos UnitAuraProcHandler.cpp:433-459: a weapon or shield the aura names (EquippedItemClass) must be worn, usable and unbroken.</summary>
    private static bool MeetsProcEquipRequirement(Player player, SpellInfo aura, WeaponAttackType attackType)
    {
        if (aura.EquippedItemClass == ProcAttributes.ItemClassWeapon)
        {
            if (!PlayerCombatSkills.CanUseEquippedWeapon(player, attackType))
            {
                return false;
            }

            Item? item = PlayerCombatSkills.WeaponForAttack(player, attackType, nonBroken: true, useable: false);
            return item is not null && ((1 << (int)item.Template.SubClass) & aura.EquippedItemSubClassMask) != 0;
        }

        if (aura.EquippedItemClass == ProcAttributes.ItemClassArmor)
        {
            Item? shield = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.OffHand);
            return shield is not null && !(shield.MaxDurability > 0 && shield.Durability == 0) && (ItemClass)shield.Template.Class == ItemClass.Armor
                && ((1 << (int)shield.Template.SubClass) & aura.EquippedItemSubClassMask) != 0;
        }

        return true;
    }

    /// <summary>
    /// vmangos <c>Unit::HandleTriggers</c> (Unit.cpp:4245-4345): for every collected holder that still exists, each of its effects that the event's
    /// spell can proc (the spell_proc_event family mask or <c>Aura::CanProcFrom</c>) runs its script or its aura type's handler; a holder with
    /// charges loses one when any effect procced (OK, or FAILED with PROC_FAILURE_BURNS_CHARGE), and is removed when the last one goes.
    /// </summary>
    private void HandleTriggers(in ProcEvent e, List<TriggeredProc> triggered)
    {
        var removed = new List<(Unit Unit, uint SpellId)>();
        foreach (TriggeredProc proc in triggered)
        {
            SpellAuraHolder holder = proc.Holder;
            if (holder.IsRemoved || !proc.Owner.IsInWorld)
            {
                continue;
            }

            SpellProcEventRecord? entry = proc.Entry;
            bool useCharges = holder.Charges > 0;
            bool anyAuraProc = false;
            uint cooldown = entry?.Cooldown ?? 0;
            for (int i = 0; i < SpellConstants.MaxEffects; i++)
            {
                if (holder.IsRemoved)
                {
                    break; // an effect's handler (or the damage-proc cancel) removed the holder: its other effects do not proc
                }

                if (holder.Auras[i] is not { } aura)
                {
                    continue;
                }

                if (e.ProcSpell is { } procSpell)
                {
                    if (entry is not null)
                    {
                        if (entry.FamilyMask(i) != 0)
                        {
                            if ((procSpell.SpellFamilyFlags & entry.FamilyMask(i)) == 0)
                            {
                                continue;
                            }
                        }
                        else if (!CanProcFrom(holder, i, procSpell, (ProcFlagsEx)entry.ProcEx, proc.Extra, e.Amount != 0, useClassMask: entry.SchoolMask == 0))
                        {
                            continue;
                        }
                    }
                    else if (!CanProcFrom(holder, i, procSpell, ProcFlagsEx.None, proc.Extra, e.Amount != 0, useClassMask: true))
                    {
                        continue;
                    }
                }

                // Spells that require the target below 20% (Deadly Swiftness).
                if (holder.Spell.TargetAuraState == AuraState.Healthless20Percent
                    && (proc.Target is null || proc.Target.MaxHealth == 0 || proc.Target.Health * 5 > proc.Target.MaxHealth))
                {
                    continue;
                }

                var context = new AuraProcContext(this, proc.Owner, proc.Target, holder, aura, e.ProcSpell, proc.ProcFlag, proc.Extra, e.Amount,
                    e.OriginalAmount, cooldown, proc.IsVictim, e.AttackType, e.Reflected);
                AuraProcResult result = _procScripts.GetValueOrDefault(holder.Spell.Id)?.OnProc(context)
                    ?? (ProcHandlers.TryGetValue(aura.Type, out AuraProcHandler? handler) ? handler(context) : AuraProcResult.Ok);
                bool burnsOnFailure = ((uint)holder.Spell.Attributes & ProcAttributes.ProcFailureBurnsCharge) != 0;
                switch (result)
                {
                    case AuraProcResult.CantTrigger:
                        continue;
                    case AuraProcResult.Failed:
                        useCharges &= burnsOnFailure;
                        break;
                    case AuraProcResult.Ok:
                        useCharges &= !burnsOnFailure;
                        break;
                }

                anyAuraProc = true;
                ApplyDamageProcCancel(context);
            }

            if (useCharges && anyAuraProc && !holder.IsRemoved && DropAuraCharge(holder))
            {
                removed.Add((proc.Owner, holder.Spell.Id));
            }
        }

        foreach ((Unit unit, uint spellId) in removed.Distinct())
        {
            RemoveAuras(unit, spellId);
        }
    }

    /// <summary>
    /// vmangos <c>Aura::CanProcFrom</c> (SpellAuras.cpp:966-1003): with a class mask (the effect's EffectItemType, or the spell_proc_event mask of
    /// the effect), the triggering spell must share a bit; without one, an aura with no extra requirement procs only on an active hit or crit,
    /// and a passive one that needs a hit cannot proc from a spell that dealt nothing unless the outcome itself is a no-damage one.
    /// </summary>
    internal bool CanProcFrom(SpellAuraHolder holder, int effectIndex, SpellInfo procSpell, ProcFlagsEx eventProcEx, ProcFlagsEx procEx, bool active, bool useClassMask)
    {
        ulong mask = AffectMask(holder.Spell, effectIndex);
        if (mask == 0 && ProcEvents.Find(holder.Spell.Id) is { } entry)
        {
            mask = entry.FamilyMask(effectIndex);
        }

        if (!useClassMask || mask == 0)
        {
            if ((eventProcEx & ProcFlagsEx.TriggerAlways) == 0)
            {
                if (eventProcEx == ProcFlagsEx.None)
                {
                    return (procEx & (ProcFlagsEx.NormalHit | ProcFlagsEx.CriticalHit)) != 0 && active;
                }

                if ((eventProcEx & (ProcFlagsEx.NormalHit | ProcFlagsEx.CriticalHit) & procEx) != 0 && !active
                    && (eventProcEx & ProcFlagRules.NoDamageMask & procEx) == 0)
                {
                    return false;
                }
            }

            return true;
        }

        return (mask & procSpell.SpellFamilyFlags) != 0;
    }

    /// <summary>The class mask of an aura effect (vmangos SpellMgr::GetSpellAffectMask: EffectItemType, 64-bit through the class-mask overlay when one is installed).</summary>
    internal ulong AffectMask(SpellInfo spell, int effectIndex)
        => effectIndex < spell.Effects.Count
            ? (SpellModifiers as Mods.ISpellModEngine)?.ClassMask(spell, effectIndex) ?? spell.Effects[effectIndex].ItemType
            : 0;

    /// <summary>vmangos SpellAuraHolder::DropAuraCharge: one charge less; true when the last one is gone.</summary>
    private static bool DropAuraCharge(SpellAuraHolder holder)
    {
        if (holder.Charges <= 0)
        {
            return false;
        }

        holder.Charges--;
        return holder.Charges == 0;
    }

    /// <summary>
    /// vmangos <c>Unit::TriggerProccedSpell</c> (UnitAuraProcHandler.cpp:501-536): a living (or self) target, the triggered spell off cooldown, a
    /// triggered cast by the aura (custom base points when any is set), then the spell_proc_event hidden cooldown on the triggered spell.
    /// </summary>
    public AuraProcResult TriggerProccedSpell(Unit caster, Unit? target, SpellInfo spell, SpellAuraHolder triggeredBy, uint cooldownMs, int? basePoints0 = null,
        int? basePoints1 = null, int? basePoints2 = null, SpellInfo? procSpell = null)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(triggeredBy);
        if (target is null || (!ReferenceEquals(target, caster) && !target.IsAlive) || !IsProcSpellReady(caster, spell))
        {
            return AuraProcResult.Failed;
        }

        CastProcSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggeredBy.Spell, basePoints0, basePoints1, basePoints2, procSpell);
        if (cooldownMs > 0)
        {
            AddProcCooldown(caster, spell, cooldownMs);
        }

        return AuraProcResult.Ok;
    }

    /// <summary>A triggered cast made by an aura's proc, optionally with explicit base points (vmangos CastCustomSpell with triggeredByAura).</summary>
    /// <summary>
    /// <paramref name="procSpell"/> is the spell whose hit made the aura proc: while a PROC_TRIGGER_SPELL aura's cast runs, damage it deals does not
    /// break that spell's own crowd control (vmangos m_triggeredByParentSpellInfo, Unit.cpp:896-905).
    /// </summary>
    internal SpellCastResult CastProcSpell(Unit caster, SpellInfo spell, SpellCastTargets targets, SpellInfo auraSpell, int? basePoints0 = null,
        int? basePoints1 = null, int? basePoints2 = null, SpellInfo? procSpell = null)
    {
        (uint SpellId, uint ParentSpellId)? outerParent = _procCastParent;
        if (procSpell is not null && auraSpell.HasAura(AuraType.ProcTriggerSpell))
        {
            _procCastParent = (spell.Id, procSpell.Id);
        }

        CustomValueScope? outer = _customValues;
        try
        {
            if (basePoints0 is not null || basePoints1 is not null || basePoints2 is not null)
            {
                if (!_customValueModifierInstalled)
                {
                    _valueModifiers = [new CustomValueModifier(this), .. _valueModifiers];
                    _customValueModifierInstalled = true;
                }

                _customValues = new CustomValueScope(caster, spell, [basePoints0, basePoints1, basePoints2]);
            }

            return Prepare(caster, spell, targets, triggered: true, triggeringSpell: auraSpell, triggeredByAura: true);
        }
        finally
        {
            _customValues = outer;
            _procCastParent = outerParent;
        }
    }

    /// <summary>vmangos SpellCaster::IsSpellReady (SpellCaster.cpp:2487-2513): the spell and category cooldowns and a school lockout, but not the GCD.</summary>
    internal bool IsProcSpellReady(Unit unit, SpellInfo spell)
    {
        if (GetState(unit.Guid) is not { } state || !ReferenceEquals(state.Unit, unit))
        {
            return true;
        }

        uint now = NowMs;
        if (state.SpellCooldowns.TryGetValue(spell.Id, out uint until) && until > now)
        {
            return false;
        }

        if (spell.Category != 0 && state.CategoryCooldowns.TryGetValue(spell.Category, out until) && until > now)
        {
            return false;
        }

        return !(spell.PreventionType == SpellConstants.PreventionTypeSilence && state.SchoolLockouts.TryGetValue(spell.School, out until) && until > now);
    }

    /// <summary>The hidden cooldown of a proc (vmangos AddCooldown(spell, nullptr, false, cooldown)): server side only, the client is not told.</summary>
    internal void AddProcCooldown(Unit unit, SpellInfo spell, uint cooldownMs)
    {
        UnitSpellState state = GetOrCreateState(unit);
        uint until = NowMs + cooldownMs;
        if (!state.SpellCooldowns.TryGetValue(spell.Id, out uint current) || current < until)
        {
            state.SpellCooldowns[spell.Id] = until;
        }
    }

    /// <summary>
    /// Damage-cancel through the proc (docs/areas/procs.md, deviation): an aura whose spell both breaks on damage (AuraInterruptFlags DAMAGE) and
    /// procs on TAKEN_ANY_DAMAGE (Wyvern Sting's sleep, the only 1.12 spells with both) is skipped by the damage break when the proc engine runs
    /// (vmangos checkProcFlags) and has no proc handler of its own, so vmangos keeps it through the damage; the tooltip ("Any damage will cancel
    /// the effect") is honoured here by removing it when its damage proc fires. <see cref="AuraOptions.DamageProcCancelsAura"/> false is vmangos.
    /// </summary>
    private void ApplyDamageProcCancel(in AuraProcContext context)
    {
        if (!AuraOptions.DamageProcCancelsAura || !context.IsVictim || context.Holder.IsRemoved || context.Amount == 0
            || (context.ProcFlag & ProcFlags.TakenAnyDamage) == 0 || (context.Holder.Spell.ProcFlags & ProcFlags.TakenAnyDamage) == 0
            || (context.Holder.Spell.AuraInterruptFlags & SpellAuraInterruptFlags.Damage) == 0)
        {
            return;
        }

        RemoveAuraHolder(context.Holder);
    }
}
