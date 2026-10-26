using ArcaneCore.Game;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// One class's fight logic, after vmangos PartyBotAI::UpdateInCombatAI_&lt;Class&gt; / UpdateOutOfCombatAI_&lt;Class&gt;
/// (PartyBotAI.cpp:1163-3039) on the shared helpers of CombatBotBaseAI (heal, buff and dispel target selection). A rotation is a
/// pure function of a <see cref="RotationState"/>: it names at most one action, and the caller
/// (<see cref="PlayerbotCombatSpells"/>) submits it through the ordinary client opcode. Each step is "known, castable at that
/// unit now (<see cref="RotationState.CanCast"/>), not already on it, and its condition holds", tried in the vmangos order.
/// <para>
/// Differences from the party bots, for a bot that also grinds alone: a healer with nobody to heal fights with its damage spells
/// instead of idling, an emergency potion or bandage is used at low health, a tank taunts a victim that turned to a group mate,
/// and a rogue or cat finishes early when the victim is nearly dead.
/// </para>
/// </summary>
internal abstract class PlayerbotClassRotation
{
    /// <summary>The bots' melee distance: the brain closes to it for a melee fight.</summary>
    internal const float MeleeRange = 4f;

    /// <summary>Where casters and healers stand (vmangos SetCasterChaseDistance(25)).</summary>
    internal const float CasterRange = 25f;

    /// <summary>A hunter closes to 30 yards, inside Auto Shot range and well outside its 8-yard dead zone (vmangos chases beyond 30).</summary>
    internal const float HunterRange = 30f;

    /// <summary>The 8-yard minimum of Auto Shot (vmangos PartyBotAI.cpp:755-766).</summary>
    internal const float HunterDeadZone = 8f;

    /// <summary>vmangos IsValidHealTarget / SelectBuffTarget distance.</summary>
    internal const float FriendlyRange = 30f;

    internal const uint DispelMagic = 1;
    internal const uint DispelCurse = 2;
    internal const uint DispelDisease = 3;
    internal const uint DispelPoison = 4;

    internal const string RecentlyBandaged = "Recently Bandaged";

    public abstract Class Class { get; }

    /// <summary>The spell names this class resolves from its book (<see cref="PlayerbotAbilities.Resolve"/>).</summary>
    public abstract IReadOnlyList<string> Abilities { get; }

    /// <summary>A fight (or a pull, when <see cref="RotationState.InCombat"/> is false) against <see cref="RotationState.Victim"/>.</summary>
    public RotationAction? InCombat(RotationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Victim is not { IsAlive: true }) return null;
        return Emergency(state) ?? DispelFriends(state) ?? Fight(state, state.Victim);
    }

    /// <summary>Upkeep between fights: forms, buffs on the bot and its group, pets, heals and stealth before a pull.</summary>
    public RotationAction? OutOfCombat(RotationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return DispelFriends(state) ?? Upkeep(state);
    }

    /// <summary>
    /// A pet command to send in the same think as <paramref name="chosen"/> (null: nothing, or the think chose no action). A
    /// rotation returns one action and a cast in progress ends the think, so a pet command that yields to a cast is never sent
    /// by a bot that stands and casts back to back. A class that sends its pet overrides this with <see cref="SendIdlePet"/>.
    /// (vmangos PartyBotAI does not do this in combat: it commands the pet out of combat when the pet has no victim, :2208-2216.
    /// This is a deliberate, narrower departure for bots whose think can only hold one action.) Default: none.
    /// </summary>
    public virtual RotationAction? PetCompanion(RotationState state, RotationAction? chosen) => null;

    /// <summary>Where the bot fights from (yards from its victim).</summary>
    public virtual float PreferredRange(RotationState state) => MeleeRange;

    protected abstract RotationAction? Fight(RotationState s, RotationUnit victim);

    protected abstract RotationAction? Upkeep(RotationState s);

    /// <summary>The class's friendly dispels, in the vmangos order (PartyBotAI::CheckForDispelTargets, :1037-1161).</summary>
    protected virtual IEnumerable<string> Dispels => [];

    // --- steps ------------------------------------------------------------------------------------------------------------

    /// <summary>Cast <paramref name="name"/> at <paramref name="target"/> when <paramref name="when"/> holds and the spell is ready for it.</summary>
    protected static RotationAction? Do(RotationState s, string name, RotationUnit? target, bool when = true)
        => when && Ready(s, name, target) is { } spell ? RotationAction.Cast(spell, target!) : null;

    /// <summary>
    /// The highest known rank of <paramref name="name"/> if it can be cast at <paramref name="target"/> now; an aura spell whose aura
    /// (any rank) the target already has is not ready (vmangos CanTryToCastSpell's IsSpellAppliesAura check).
    /// </summary>
    protected static SpellInfo? Ready(RotationState s, string name, RotationUnit? target)
    {
        if (target is null || s.Spells[name] is not { } spell) return null;
        if (!target.IsAlive && !spell.CanTargetDead) return null;
        if (AppliesAura(spell) && target.HasAura(spell.Name)) return null;
        return s.CanCast(spell, target) ? spell : null;
    }

    internal static bool AppliesAura(SpellInfo spell)
        => spell.Effects.Any(static e => e.Effect is SpellEffectName.ApplyAura or SpellEffectName.ApplyAreaAuraParty);

    protected static int AttackersWithin(RotationState s, float range) => s.Attackers.Count(a => a.IsAlive && a.Distance <= range);

    protected static RotationUnit? FirstAttackerOtherThan(RotationState s, RotationUnit? except)
        => s.Attackers.FirstOrDefault(a => a.IsAlive && !a.IsCrowdControlled && a.Guid != except?.Guid);

    /// <summary>Whether a class fights with weapons (vmangos CombatBotBaseAI::IsPhysicalDamageClass).</summary>
    protected static bool IsPhysicalClass(byte playerClass) => (Class)playerClass is Class.Warrior or Class.Paladin or Class.Rogue
        or Class.Hunter or Class.Shaman or Class.Druid;

    /// <summary>Whether a class uses mana.</summary>
    protected static bool UsesMana(RotationUnit unit) => unit.PowerType == PowerType.Mana;

    // --- emergencies ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A healing potion below 25% health in a fight; a mana potion at or below 20% mana; a bandage below 40% when nothing is
    /// hitting the bot in melee (a hit interrupts the channel) and the "Recently Bandaged" debuff has worn off.
    /// </summary>
    internal static RotationAction? Emergency(RotationState s)
    {
        if (!s.InCombat) return null;
        if (s.Self.HealthPercent < 25f && s.HealingPotion is { } potion) return RotationAction.Use(potion, s.Self);
        if (s.PowerType == PowerType.Mana && s.MaxPower > 0 && s.PowerPercent <= 20f
            && PlayerbotCombatView.GetManaPotion(s) is { } manaPotion)
            return RotationAction.Use(manaPotion, s.Self);
        if (s.Self.HealthPercent < 40f && s.Bandage is { } bandage && !s.Self.HasAura(RecentlyBandaged)
            && !s.Attackers.Any(a => a.IsAlive && a.InMeleeRange))
            return RotationAction.Use(bandage, s.Self);
        return null;
    }

    // --- dispels ----------------------------------------------------------------------------------------------------------

    private RotationAction? DispelFriends(RotationState s)
    {
        // vmangos: no dispel from a shapeshift form (CheckForDispelTargets, PartyBotAI.cpp:1039).
        if (s.Form != Game.Spells.ShapeshiftForm.None) return null;
        foreach (string name in Dispels)
        {
            if (Dispel(s, name) is { } action) return action;
        }
        return null;
    }

    /// <summary>vmangos SelectDispelTarget + IsValidDispelTarget: the first friend carrying a harmful aura of a type the spell removes.</summary>
    protected static RotationAction? Dispel(RotationState s, string name)
    {
        if (s.Spells[name] is not { } spell) return null;
        uint mask = DispelMask(spell);
        if (mask == 0) return null;
        foreach (RotationUnit friend in s.Friends)
        {
            if (friend.IsAlive && friend.Distance <= FriendlyRange && (friend.HarmfulDispelMask & mask) != 0 && s.CanCast(spell, friend))
                return RotationAction.Cast(spell, friend);
        }
        return null;
    }

    /// <summary>An offensive dispel (Purge) at a victim carrying a beneficial aura of a type the spell removes.</summary>
    protected static RotationAction? PurgeVictim(RotationState s, string name, RotationUnit victim)
        => s.Spells[name] is { } spell && (victim.HelpfulDispelMask & DispelMask(spell)) != 0 && s.CanCast(spell, victim)
            ? RotationAction.Cast(spell, victim) : null;

    internal static uint DispelMask(SpellInfo spell)
    {
        uint mask = 0;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.Effect == SpellEffectName.Dispel && effect.MiscValue is > 0 and < 32) mask |= 1u << effect.MiscValue;
        }
        return mask;
    }

    // --- buffs ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// vmangos SelectBuffTarget(single, group) (CombatBotBaseAI.cpp:2256-2309): the friends within 30 yards that have neither
    /// buff; two or more missing and the group version known casts that on the first, otherwise the single version.
    /// </summary>
    protected static RotationAction? Buff(RotationState s, string single, string? group = null)
    {
        SpellInfo? one = s.Spells[single];
        SpellInfo? many = group is null ? null : s.Spells[group];
        if (one is null && many is null) return null;
        RotationUnit? first = null;
        int missing = 0;
        foreach (RotationUnit friend in s.Friends)
        {
            if (!friend.IsAlive || friend.Distance > FriendlyRange || friend.HasAura(single) || group is not null && friend.HasAura(group))
                continue;
            first ??= friend;
            missing++;
        }

        if (first is null) return null;
        SpellInfo chosen = missing > 1 && many is not null ? many : one ?? many!;
        return s.CanCast(chosen, first) ? RotationAction.Cast(chosen, first) : null;
    }

    // --- heals ------------------------------------------------------------------------------------------------------------

    /// <summary>vmangos SelectHealTarget (CombatBotBaseAI.cpp:1990-2033): the bot below its threshold, else the most injured friend, else the bot's pet.</summary>
    internal static RotationUnit? SelectHealTarget(RotationState s, float selfPercent, float groupPercent)
    {
        if (s.Self.IsAlive && s.Self.HealthPercent < selfPercent) return s.Self;
        RotationUnit? best = null;
        foreach (RotationUnit member in s.Party)
        {
            if (member.IsAlive && member.Distance <= FriendlyRange && member.HealthPercent < groupPercent
                && (best is null || member.HealthPercent < best.HealthPercent))
                best = member;
        }

        if (best is null && s.Pet == RotationPetStatus.Alive && s.PetUnit is { IsAlive: true } pet
            && pet.Distance <= FriendlyRange && pet.HealthPercent < groupPercent)
            best = pet;
        return best;
    }

    /// <summary>vmangos SelectPeriodicHealTarget (:2035-2063): the first friend below its threshold without a heal-over-time.</summary>
    internal static RotationUnit? SelectPeriodicHealTarget(RotationState s, float selfPercent, float groupPercent)
    {
        if (s.Self.HealthPercent < selfPercent && !s.Self.HasPeriodicHeal) return s.Self;
        return s.Party.FirstOrDefault(m => m.IsAlive && m.Distance <= FriendlyRange && m.HealthPercent < groupPercent && !m.HasPeriodicHeal);
    }

    /// <summary>vmangos FindAndHealInjuredAlly (:1875-1882).</summary>
    protected static RotationAction? HealInjuredAlly(RotationState s, float selfPercent = 100f, float groupPercent = 100f)
        => SelectHealTarget(s, selfPercent, groupPercent) is { } target ? HealInjured(s, target) : null;

    /// <summary>vmangos HealInjuredTarget (:1943-1957): a heal-over-time for a light wound, else the best-fitting direct heal.</summary>
    internal static RotationAction? HealInjured(RotationState s, RotationUnit target)
        => (target.HealthPercent >= 80f && !target.HasPeriodicHeal ? HealPeriodic(s, target) : null) ?? HealDirect(s, target);

    internal static RotationAction? HealDirect(RotationState s, RotationUnit target)
        => MostEfficient(s, target, s.Spells.DirectHeals) is { } spell ? RotationAction.Cast(spell, target) : null;

    internal static RotationAction? HealPeriodic(RotationState s, RotationUnit target)
        => target.HasPeriodicHeal ? null
            : MostEfficient(s, target, s.Spells.PeriodicHeals.Where(h => !target.HasAura(h.Name)).ToArray()) is { } spell
                ? RotationAction.Cast(spell, target) : null;

    /// <summary>
    /// vmangos SelectMostEfficientHealingSpell (:1891-1932): over the castable heals, strongest first, the one whose amount is
    /// nearest the missing health; the scan stops at the first heal smaller than the wound. A heal that only lands on its caster is
    /// left out for any target but the bot.
    /// </summary>
    internal static SpellInfo? MostEfficient(RotationState s, RotationUnit target, IReadOnlyList<SpellInfo> heals)
    {
        SpellInfo? best = null;
        long bestDiff = long.MaxValue;
        foreach (SpellInfo heal in heals)
        {
            // A heal that lands on its caster (Desperate Prayer) is no heal for a friend: it would fill the bot, not the friend.
            if (!target.IsSelf && s.Spells.HealReachOf(heal) == PlayerbotHealReach.SelfOnly) continue;
            if (!s.CanCast(heal, target)) continue;
            long diff = (long)s.Spells.HealAmountOf(heal) - target.MissingHealth;
            if (Math.Abs(diff) < bestDiff)
            {
                bestDiff = Math.Abs(diff);
                best = heal;
            }

            if (diff < 0) break;
        }
        return best;
    }

    // --- pets -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The pet joins the bot's fight (vmangos PartyBotAI: SetIsCommandAttack + AttackStart on the bot's victim) once the bot is
    /// fighting or the pull target is within 30 yards, unless the victim is under breakable crowd control; a pet fighting a
    /// crowd-controlled unit is redirected to the bot's non-CC victim or called back to follow, as is a pet still fighting between fights.
    /// </summary>
    protected static RotationAction? CommandPet(RotationState s)
    {
        if (s.Pet != RotationPetStatus.Alive) return null;
        // A pet with a non-CC victim of its own is left to it (vmangos :2208-2216 commands a pet only when !pPet->GetVictim());
        // a pet on a crowd-controlled target is redirected to the bot's non-CC victim or called back to follow.
        if (s.Victim is { IsAlive: true, IsCrowdControlled: false } victim
            && (!s.PetFighting || s.PetFightingCrowdControlled)
            && (s.InCombat || victim.Distance <= FriendlyRange))
            return RotationAction.PetAttack(victim);
        return s.PetFightingCrowdControlled || (!s.InCombat && s.Victim is null && s.PetFighting)
            ? RotationAction.PetFollow() : null;
    }

    /// <summary>
    /// The pet command to send beside <paramref name="chosen"/>: an idle pet is sent to attack (or a pet on a crowd-controlled
    /// target is redirected or called back to follow) beside a cast or an empty think, never beside a cast aimed at the pet itself
    /// (Health Funnel, Mend Pet, a heal), which the command would interrupt.
    /// </summary>
    protected static RotationAction? SendIdlePet(RotationState s, RotationAction? chosen)
    {
        if (chosen is { } action)
        {
            if (action.Kind != RotationActionKind.Cast) return null;
            if (s.PetUnit is { } pet && action.Target == pet.Guid) return null;
        }
        RotationAction? command = CommandPet(s);
        return command is { Kind: RotationActionKind.PetAttack }
            || command is { Kind: RotationActionKind.PetFollow } && s.PetFightingCrowdControlled ? command : null;
    }

    /// <summary>The bow, gun and crossbow shots and the thrown-weapon throw of a warrior or rogue, in the order they are tried (spells 2480, 7918, 7919, 2764).</summary>
    internal const string ShootBow = "Shoot Bow";
    internal const string ShootGun = "Shoot Gun";
    internal const string ShootCrossbow = "Shoot Crossbow";
    internal const string Throw = "Throw";

    /// <summary>
    /// The ranged shot of a class that fights in melee: out of melee reach, standing still, with a ranged weapon equipped, the first
    /// of Shoot Bow, Shoot Gun, Shoot Crossbow and Throw the bot knows and can cast now (<see cref="RotationState.CanCast"/> judges the
    /// weapon type, ammunition and range). An ordinary ability, not Auto Shot: one cast per use.
    /// </summary>
    protected static RotationAction? RangedShot(RotationState s, RotationUnit victim)
    {
        bool when = !victim.InMeleeRange && s.HasRangedWeapon && !s.IsMoving;
        return Do(s, ShootBow, victim, when) ?? Do(s, ShootGun, victim, when) ?? Do(s, ShootCrossbow, victim, when) ?? Do(s, Throw, victim, when);
    }

    // --- shared rotations -------------------------------------------------------------------------------------------------

    /// <summary>A wand at low mana (vmangos: Shoot below 5-10% mana when nothing else is running).</summary>
    protected static RotationAction? Wand(RotationState s, RotationUnit victim, float manaPercent)
        => s.HasWand && !s.AutoRepeatActive && !s.IsMoving && s.PowerPercent < manaPercent
            ? Do(s, "Shoot", victim) : null;

    /// <summary>A healer grinding alone (or with a healthy group) uses its damage spells instead of idling.</summary>
    protected static bool HealerMayFight(RotationState s) => s.Role != PlayerbotRole.Healer || SelectHealTarget(s, 60f, 80f) is null;

    /// <summary>A tank's victim turned on someone else in the group.</summary>
    protected static bool NeedsTaunt(RotationState s, RotationUnit victim)
        => s.Role == PlayerbotRole.Tank && s.InGroup && victim.InCombat && !victim.TargetsBot;
}

/// <summary>The nine class rotations (stateless, shared by every bot).</summary>
internal static class PlayerbotRotations
{
    private static readonly PlayerbotClassRotation[] All =
    [
        new PlayerbotWarriorRotation(), new PlayerbotPaladinRotation(), new PlayerbotHunterRotation(), new PlayerbotRogueRotation(),
        new PlayerbotPriestRotation(), new PlayerbotShamanRotation(), new PlayerbotMageRotation(), new PlayerbotWarlockRotation(),
        new PlayerbotDruidRotation(),
    ];

    internal static IReadOnlyList<PlayerbotClassRotation> Every => All;

    internal static PlayerbotClassRotation? For(Class playerClass) => All.FirstOrDefault(r => r.Class == playerClass);
}
