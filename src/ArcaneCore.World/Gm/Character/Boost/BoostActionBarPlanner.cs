using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>One action-bar write: button index (0..119) and the packed value (action | type &lt;&lt; 24; spell type is 0).</summary>
internal readonly record struct BoostActionButtonWrite(byte Button, uint Packed);

/// <summary>
/// Pure action-bar layout for <c>.character boost</c>: which spell goes on which of the 120 buttons. A button is
/// <c>action | type &lt;&lt; 24</c> and a spell button is the spell id (vmangos Player.h ACTION_BUTTON_SPELL = 0,
/// MAX_ACTION_BUTTONS = 120; SMSG_ACTION_BUTTONS carries the 120 values, MasterPlayer.cpp SendInitialActionButtons).
/// <para>
/// Layout: Attack (6603) first, then the class's spells by the spell level of their first rank, highest known rank only.
/// The primary page starts at button 0 for every class except the warrior, whose start bar is the Battle Stance page at
/// button 72 (ClassicDB playercreateinfo_action rows (1,1,72,6603,0),(1,1,73,78,0) against (1,4,0,6603,0), (4,11,0,6603,0)).
/// The overflow is buttons 12..23, the client's second page; that is an ArcaneCore layout choice, not a wire fact. The Defensive
/// and Berserker stance pages are never written (their offsets are not in the references). Item and macro buttons are kept.
/// </para>
/// Holds no state; safe from any thread.
/// </summary>
internal static class BoostActionBarPlanner
{
    /// <summary>The Attack spell (vmangos playercreateinfo_action: first button of every class).</summary>
    public const uint Attack = 6603;

    /// <summary>First button of the warrior's Battle Stance page.</summary>
    public const byte WarriorPrimaryStart = 72;

    /// <summary>Buttons per bar page.</summary>
    public const int PageSize = 12;

    /// <summary>First button of the overflow page.</summary>
    public const byte OverflowStart = 12;

    private const uint ShapeshiftBarAttribute = 0x10; // SPELL_ATTR_EX2_USE_SHAPESHIFT_BAR (vmangos SpellDefines.h:910)
    private const uint ActionMask = 0x00FFFFFF;
    private const uint SpellType = 0;
    private const int MaxChain = 64;

    /// <summary>The primary page start for a class.</summary>
    public static byte PrimaryStart(Class playerClass) => playerClass == Class.Warrior ? WarriorPrimaryStart : (byte)0;

    /// <summary>
    /// The writes that bring <paramref name="current"/> to the layout. A button that holds a lower rank of a spell whose higher
    /// rank is known is upgraded in place; a layout spell already on any button is left; the others take the free buttons of the
    /// primary page, then the overflow page. Returns only buttons whose value changes; re-running on the result yields none.
    /// </summary>
    public static IReadOnlyList<BoostActionButtonWrite> Plan(
        IReadOnlyCollection<uint> known,
        Func<uint, SpellInfo?> spells,
        SpellRankChains ranks,
        uint classFamily,
        byte primaryStart,
        ReadOnlySpan<uint> current)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(ranks);
        HashSet<uint> knownSet = [.. known];
        List<uint> layout = Layout(knownSet, spells, ranks, classFamily);

        uint[] work = current.ToArray();
        int count = Math.Min(work.Length, Player.ActionButtonCount);
        var onBar = new HashSet<uint>();
        for (int button = 0; button < count; button++)
        {
            uint packed = work[button];
            if (packed == 0 || (packed >> 24) != SpellType)
            {
                continue;
            }

            uint spell = packed & ActionMask;
            uint first = ranks.First(spell);
            if (first != 0)
            {
                foreach (uint top in layout)
                {
                    if (top != spell && ranks.First(top) == first)
                    {
                        work[button] = top;
                        spell = top;
                        break;
                    }
                }
            }

            onBar.Add(spell);
        }

        foreach (uint spell in layout)
        {
            if (onBar.Contains(spell))
            {
                continue;
            }

            int slot = FreeButton(work, count, primaryStart);
            if (slot < 0)
            {
                break;
            }

            work[slot] = spell;
            onBar.Add(spell);
        }

        var writes = new List<BoostActionButtonWrite>();
        for (int button = 0; button < count; button++)
        {
            if (work[button] != current[button])
            {
                writes.Add(new BoostActionButtonWrite((byte)button, work[button]));
            }
        }

        return writes;
    }

    /// <summary>
    /// The spells to place, in order: Attack when known, then the known class-family spells that are active, shown and not on
    /// the shapeshift bar, highest known rank of each chain only, by the first rank's spell level, then the first rank's id.
    /// </summary>
    internal static List<uint> Layout(HashSet<uint> known, Func<uint, SpellInfo?> spells, SpellRankChains ranks, uint classFamily)
    {
        var result = new List<uint>();
        if (known.Contains(Attack))
        {
            result.Add(Attack);
        }

        var entries = new List<(uint Level, uint First, uint Id)>();
        foreach (uint id in known)
        {
            if (id == Attack || spells(id) is not { } spell || classFamily == 0 || spell.SpellFamilyName != classFamily
                || spell.IsPassive || spell.HasAttribute(SpellAttributes.DoNotDisplay)
                || ((uint)spell.AttributesEx2 & ShapeshiftBarAttribute) != 0 || IsSuperseded(id, known, ranks))
            {
                continue;
            }

            uint first = ranks.First(id);
            uint levelOf = first != 0 && spells(first) is { } firstSpell ? firstSpell.SpellLevel : spell.SpellLevel;
            entries.Add((levelOf, first != 0 ? first : id, id));
        }

        entries.Sort(static (a, b) =>
        {
            int byLevel = a.Level.CompareTo(b.Level);
            if (byLevel != 0)
            {
                return byLevel;
            }

            int byFirst = a.First.CompareTo(b.First);
            return byFirst != 0 ? byFirst : a.Id.CompareTo(b.Id);
        });
        result.AddRange(entries.Select(entry => entry.Id));
        return result;
    }

    private static bool IsSuperseded(uint id, HashSet<uint> known, SpellRankChains ranks)
    {
        uint cursor = ranks.Next(id);
        for (int depth = 0; cursor != 0 && depth < MaxChain; depth++, cursor = ranks.Next(cursor))
        {
            if (known.Contains(cursor))
            {
                return true;
            }
        }

        return false;
    }

    private static int FreeButton(uint[] work, int count, byte primaryStart)
    {
        for (int button = primaryStart; button < Math.Min(primaryStart + PageSize, count); button++)
        {
            if (work[button] == 0)
            {
                return button;
            }
        }

        for (int button = OverflowStart; button < Math.Min(OverflowStart + PageSize, count); button++)
        {
            if (work[button] == 0)
            {
                return button;
            }
        }

        return -1;
    }
}
