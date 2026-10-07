using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Gm.FirstLogin;

/// <summary>A version-5875 spell offered by the GM first-login bootstrap.</summary>
public record GmToolSpell(uint Id, string Name, AccountSecurity MinimumSecurity, bool DeveloperOnly);

/// <summary>
/// Explicit, source-audited 1.12.1 / build 5875 GM and developer tool spells.
/// MinimumSecurity is ArcaneCore's operator policy for this opt-in feature; it does
/// not claim to reproduce an official Blizzard staff-rank table.
/// </summary>
public static class GmFirstLoginToolCatalog
{
    private static readonly IReadOnlyList<GmToolSpell> _all = Array.AsReadOnly<GmToolSpell>(
    [
        new(2970, "Detect Invisibility", AccountSecurity.Moderator, false),
        new(11743, "Detect Greater Invisibility", AccountSecurity.Moderator, false),

        new(13, "Swim Speed (TEST)", AccountSecurity.GameMaster, false),
        new(26, "Bind Self (TEST)", AccountSecurity.GameMaster, false),
        new(47, "Sprint (TEST)", AccountSecurity.GameMaster, false),
        new(1557, "Full Speed", AccountSecurity.GameMaster, false),
        new(1908, "Uber Heal Over Time", AccountSecurity.GameMaster, false),
        new(10032, "Uber Stealth", AccountSecurity.GameMaster, false),
        new(18209, "Test Grow", AccountSecurity.GameMaster, false),
        new(18210, "Test Shrink", AccountSecurity.GameMaster, false),
        new(18800, "Light Test", AccountSecurity.GameMaster, false),
        new(23452, "Invisibility", AccountSecurity.GameMaster, false),

        new(260, "Charm (TEST)", AccountSecurity.Administrator, true),
        new(265, "Area Death (TEST)", AccountSecurity.Administrator, true),
        new(530, "Charm (Possess)", AccountSecurity.Administrator, true),
        new(2650, "Tame Pet (TEST)", AccountSecurity.Administrator, true),
        new(2653, "Damage 100 (TEST)", AccountSecurity.Administrator, true),
        new(2654, "Summon Tamed (TEST)", AccountSecurity.Administrator, true),
        new(5259, "Disarm (TEST)", AccountSecurity.Administrator, true),
        new(5696, "Charge (TEST)", AccountSecurity.Administrator, true),
        new(9454, "Freeze", AccountSecurity.Administrator, true),
        new(23775, "Stun Forever", AccountSecurity.Administrator, true),
        new(24199, "Knockback 35", AccountSecurity.Administrator, true),
        new(27204, "QADebug Instant Cast", AccountSecurity.Administrator, true),
        new(29607, "Debug Frost Spell", AccountSecurity.Administrator, true),
        new(31366, "Root Anybody Forever", AccountSecurity.Administrator, true),
        new(1509, "GM Only OFF", AccountSecurity.Administrator, true),
        new(18139, "GM Only ON", AccountSecurity.Administrator, true),
        new(2763, "INVIS Only ON", AccountSecurity.Administrator, true),
        new(6147, "INVIS Only OFF", AccountSecurity.Administrator, true),
        new(28432, "Set Speed", AccountSecurity.Administrator, true),
    ]);

    public static IReadOnlyList<GmToolSpell> All => _all;

    /// <summary>
    /// Selects an ordered, unique spell list. Every selected id is preflighted against
    /// the loaded store (name and non-passive status) before the list is returned, so a
    /// caller can perform the eventual spellbook mutation only after all selected
    /// records have passed validation; persistence flush and retry remain separate.
    /// </summary>
    public static IReadOnlyList<GmToolSpell> Select(
        GmFirstLoginToolsOptions options,
        AccountSecurity security,
        SpellStore store)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);

        if (!options.Enabled || security == AccountSecurity.Player || !Enum.IsDefined(security)
            || security < options.MinimumSecurity)
            return Array.Empty<GmToolSpell>();

        options.Validate();
        var subset = options.SpellIds.ToHashSet();
        var excluded = options.ExcludedSpellIds.ToHashSet();
        List<GmToolSpell> selected = [];
        foreach (GmToolSpell catalog in _all)
        {
            if ((subset.Count != 0 && !subset.Contains(catalog.Id)) || catalog.MinimumSecurity > security
                || (!options.IncludeDeveloperSpells && catalog.DeveloperOnly)
                || excluded.Contains(catalog.Id))
                continue;

            SpellInfo? loaded = store.Get(catalog.Id);
            if (loaded is null || !string.Equals(loaded.Name, catalog.Name, StringComparison.Ordinal)
                || loaded.IsPassive)
                throw new InvalidOperationException(
                    $"GM first-login catalog spell {catalog.Id} ({catalog.Name}) failed loaded-store preflight.");

            selected.Add(catalog);
        }

        return Array.AsReadOnly(selected.OrderBy(tool => tool.Id).ToArray());
    }
}
