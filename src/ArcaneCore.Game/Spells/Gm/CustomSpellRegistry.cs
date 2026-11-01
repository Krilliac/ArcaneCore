using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.Game.Spells.Gm;

/// <summary>One repurposed spell a staff account may be taught and cast.</summary>
/// <param name="SpellId">The Spell.dbc id.</param>
/// <param name="MinimumSecurity">The account security a caster needs (a stray learned copy gives a lower account nothing).</param>
/// <param name="Note">One line on what the spell is used for.</param>
public sealed record CustomSpell(uint SpellId, AccountSecurity MinimumSecurity, string Note);

/// <summary>
/// The custom spell registry: spells that are NOT inert by design (see <see cref="DeliberatelyInertSpells"/>) but repurposed as staff tools.
/// No retail client sends a cast of these for a player, so a cast by an account below <see cref="CustomSpell.MinimumSecurity"/> is refused by
/// <see cref="GmSpellbook"/> and, with <c>AntiCheat:SpellHoneypot</c> on, recorded as a honeypot signal (<c>CastSpellHoneypot.IsTrap</c>).
/// The ids moved out of the inert registry: 18800 and 22690 were test spells there. No client patch and no spellbook tab: the spells show in
/// the ordinary spellbook of the account that was taught them.
/// Immutable after type initialisation; safe from any thread.
/// </summary>
public static class CustomSpellRegistry
{
    private const string Repurposed = "repurposed safe spell for staff accounts";

    private static readonly CustomSpell[] s_all =
    [
        new(5, AccountSecurity.GameMaster, Repurposed),
        new(47, AccountSecurity.GameMaster, "Sprint (TEST)"),
        new(1979, AccountSecurity.GameMaster, Repurposed),
        new(5238, AccountSecurity.GameMaster, Repurposed),
        new(6716, AccountSecurity.GameMaster, Repurposed),
        new(13954, AccountSecurity.GameMaster, Repurposed),
        new(18209, AccountSecurity.GameMaster, "Test Grow"),
        new(18210, AccountSecurity.GameMaster, "Test Shrink"),
        new(18800, AccountSecurity.GameMaster, "Light Test"),
        new(21074, AccountSecurity.GameMaster, Repurposed),
        new(22690, AccountSecurity.GameMaster, Repurposed),
        new(29607, AccountSecurity.Administrator, "Debug Frost Spell"),
    ];

    private static readonly FrozenDictionary<uint, CustomSpell> s_byId = s_all.ToFrozenDictionary(spell => spell.SpellId);

    /// <summary>Every entry, ordered by spell id.</summary>
    public static IReadOnlyList<CustomSpell> All => s_all;

    /// <summary>The entry of <paramref name="spellId"/>, if it is a custom spell.</summary>
    public static bool TryGet(uint spellId, [NotNullWhen(true)] out CustomSpell? entry) => s_byId.TryGetValue(spellId, out entry);

    /// <summary>Whether <paramref name="spellId"/> is a custom spell.</summary>
    public static bool Contains(uint spellId) => s_byId.ContainsKey(spellId);
}
