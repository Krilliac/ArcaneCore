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
/// Every id here was checked against its real Spell.dbc row (vmangos spell_template, build 5875 rows and earlier): no reagent, spell focus, stance
/// or equipped-item requirement and a non-zero SpellVisual1, so <see cref="GmSpellbook.Unsuitable"/> accepts all of them.
/// Candidates the same check rejected and that are therefore NOT in the registry: 5 (Death Touch: instakill, SpellVisual1 0), 1979
/// (zzOLDSilencing Shot: equipped item class 2, no visual), 5238 (zzOLDSpell Break: stance mask 65536) and 29607 (Debug Frost Spell: no visual;
/// it stays a first-login tool through the World layer's GM tool catalog, which has its own Administrator policy).
/// Moved out of the inert registry: 18800 and 22690 were test spells there. No client patch and no spellbook tab: the spells show in
/// the ordinary spellbook of the account that was taught them.
/// Immutable after type initialisation; safe from any thread.
/// </summary>
public static class CustomSpellRegistry
{
    private static readonly CustomSpell[] s_all =
    [
        new(47, AccountSecurity.GameMaster, "Sprint (TEST): run-speed aura, +100%"),
        new(6716, AccountSecurity.GameMaster, "Test of Faith: root aura (a GM freezes a unit in place)"),
        new(13954, AccountSecurity.GameMaster, "NPC PORT TEST: teleport-units effect"),
        new(18209, AccountSecurity.GameMaster, "Test Grow: scale aura, larger"),
        new(18210, AccountSecurity.GameMaster, "Test Shrink: scale aura, smaller"),
        new(18800, AccountSecurity.GameMaster, "Light Test: dummy aura with a light visual"),
        new(21074, AccountSecurity.GameMaster, "Test NPC Resurrection: resurrect effect"),
        new(22690, AccountSecurity.GameMaster, "Test Charge: charge effect"),
    ];

    private static readonly FrozenDictionary<uint, CustomSpell> s_byId = s_all.ToFrozenDictionary(spell => spell.SpellId);

    /// <summary>Every entry, ordered by spell id.</summary>
    public static IReadOnlyList<CustomSpell> All { get; } = Array.AsReadOnly(s_all);

    /// <summary>The entry of <paramref name="spellId"/>, if it is a custom spell.</summary>
    public static bool TryGet(uint spellId, [NotNullWhen(true)] out CustomSpell? entry) => s_byId.TryGetValue(spellId, out entry);

    /// <summary>Whether <paramref name="spellId"/> is a custom spell.</summary>
    public static bool Contains(uint spellId) => s_byId.ContainsKey(spellId);
}
