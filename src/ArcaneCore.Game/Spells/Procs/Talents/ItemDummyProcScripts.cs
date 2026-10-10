using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Procs.Talents;

/// <summary>
/// The item, set-bonus and remaining class cases of vmangos <c>Unit::HandleDummyAuraProc</c> (UnitAuraProcHandler.cpp:550-1145) that have no
/// effect of their own: without a script the DUMMY aura only counts its charge, so the trinket stacks never fall off and the set bonuses never
/// fire. One script serves every id; each case follows the vmangos branch it names.
/// </summary>
public sealed class ItemDummyProc : IProcScript
{
    /// <summary>Twisted Reflection (boss spell, :680-682) → 21064 at the victim.</summary>
    public const uint TwistedReflection = 21063;

    /// <summary>Unstable Power (Zandalarian Hero Charm, :684-690): one stack of 24659 per proc.</summary>
    public const uint UnstablePower = 24658;

    /// <summary>Restless Strength (Zandalarian Hero Medallion, :692-697): one stack of 24662 per proc.</summary>
    public const uint RestlessStrength = 24661;

    /// <summary>Oracle Healing Bonus (Garments of the Oracle, :896-905): 10% of the heal back on the caster as 26170.</summary>
    public const uint OracleHealingBonus = 26169;

    /// <summary>Greater Heal (Vestments of Faith 4 pieces, :907-911) → 28810.</summary>
    public const uint FaithGreaterHeal = 28809;

    /// <summary>Healing Touch (Dreamwalker Raiment, :920-927): 30% of the spell's mana cost back as 28742.</summary>
    public const uint DreamwalkerHealingTouch = 28719;

    /// <summary>Healing Touch Refund (Idol of Longevity, :929-934) → 28848 on self.</summary>
    public const uint IdolOfLongevity = 28847;

    /// <summary>Clean Escape (:944-949): a Vanish with an effect → 23583.</summary>
    public const uint CleanEscape = 23582;

    /// <summary>Holy Power (Redemption Armor, :1058-1085): a buff by the target's class.</summary>
    public const uint HolyPower = 28789;

    /// <summary>Totemic Power (The Earthshatterer, :1094-1121): a buff by the target's class.</summary>
    public const uint TotemicPower = 28823;

    /// <summary>Lesser Healing Wave (Totem of Flowing Water, :1123-1128) → 28850 on self.</summary>
    public const uint TotemOfFlowingWater = 28849;

    public static readonly uint[] Spells =
    [
        TwistedReflection, UnstablePower, RestlessStrength, OracleHealingBonus, FaithGreaterHeal, DreamwalkerHealingTouch, IdolOfLongevity,
        CleanEscape, HolyPower, TotemicPower, TotemOfFlowingWater,
    ];

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Aura.Type != AuraType.Dummy)
        {
            return null;
        }

        SpellSystem system = context.System;
        Unit owner = context.Owner;
        Unit? target = context.Target;
        int? basePoints = null;
        uint trigger;
        switch (context.Holder.Spell.Id)
        {
            case TwistedReflection:
                trigger = 21064;
                break;
            case UnstablePower:
                system.RemoveScriptAuraStack(owner, 24659);
                return AuraProcResult.Ok;
            case RestlessStrength:
                system.RemoveScriptAuraStack(owner, 24662);
                return AuraProcResult.Ok;
            case OracleHealingBonus:
                int heal = (int)(context.Amount * 0.1f);
                if (heal == 0)
                {
                    return AuraProcResult.Failed;
                }

                basePoints = heal;
                target = owner;
                trigger = 26170;
                break;
            case FaithGreaterHeal:
                trigger = 28810;
                break;
            case DreamwalkerHealingTouch:
                if (context.ProcSpell is not { } healingTouch)
                {
                    return AuraProcResult.Failed;
                }

                basePoints = TalentProcSupport.BasePoints((int)(healingTouch.ManaCost * 30 / 100));
                target = owner;
                trigger = 28742;
                break;
            case IdolOfLongevity:
                target = owner;
                trigger = 28848;
                break;
            case CleanEscape:
                if (context.ProcSpell is not { } vanish || vanish.Effects.Count == 0 || vanish.Effects[0].Effect == SpellEffectName.None)
                {
                    return AuraProcResult.Failed;
                }

                trigger = 23583;
                target ??= owner; // Vanish is self-cast: its "victim" is the rogue
                break;
            case HolyPower:
            case TotemicPower:
                if (target is null || ClassBuff(context.Holder.Spell.Id == HolyPower, target.Class) is not { } buff)
                {
                    return AuraProcResult.Failed;
                }

                trigger = buff;
                break;
            case TotemOfFlowingWater:
                target = owner;
                trigger = 28850;
                break;
            default:
                return AuraProcResult.Ok;
        }

        return system.Store.Get(trigger) is { } spell
            ? system.TriggerProccedSpell(owner, target, spell, context.Holder, context.CooldownMs, basePoints)
            : AuraProcResult.Failed;
    }

    /// <summary>The Holy Power / Totemic Power buff of a class: mana regeneration, spell power, attack power or armor (null: none).</summary>
    internal static uint? ClassBuff(bool holyPower, Class cls) => cls switch
    {
        Class.Paladin or Class.Priest or Class.Shaman or Class.Druid => holyPower ? 28795u : 28824u,
        Class.Mage or Class.Warlock => holyPower ? 28793u : 28825u,
        Class.Hunter or Class.Rogue => holyPower ? 28791u : 28826u,
        Class.Warrior => holyPower ? 28790u : 28827u,
        _ => null,
    };
}
