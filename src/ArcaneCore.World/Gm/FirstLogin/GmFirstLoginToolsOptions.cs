using ArcaneCore.Kernel.Accounts;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Gm.FirstLogin;

/// <summary>
/// Opt-in policy for the first-login GM spellbook bootstrap. The policy only selects
/// catalogued, active spells for the normal spellbook load and persistence lifecycle.
/// </summary>
public sealed class GmFirstLoginToolsOptions
{
    public const string SectionName = "World:GmCommands:FirstLoginTools";

    /// <summary>Teach a staff character's first login the reviewed GM tool spells (off by default; when off the other keys are not read).</summary>
    public bool Enabled { get; set; }

    /// <summary>The lowest account security that gets the tools (Moderator, GameMaster or Administrator).</summary>
    public AccountSecurity MinimumSecurity { get; set; } = AccountSecurity.GameMaster;

    /// <summary>Also teach the catalog's developer-only spells.</summary>
    public bool IncludeDeveloperSpells { get; set; } = true;

    /// <summary>Show the staff member a short guide to the taught tools at that first login.</summary>
    public bool ShowToolGuide { get; set; } = true;

    /// <summary>
    /// Optional operator subset. Empty means the reviewed rank-filtered catalog;
    /// populated values narrow it to known catalog IDs and never add new spells.
    /// </summary>
    public uint[] SpellIds { get; set; } = [];

    /// <summary>Catalog spell ids never taught (at most 64, unique).</summary>
    public uint[] ExcludedSpellIds { get; set; } = [];

    /// <summary>Bind exactly the nested <c>World:GmCommands:FirstLoginTools</c> section.</summary>
    public static GmFirstLoginToolsOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new GmFirstLoginToolsOptions();
        if (!configuration.GetValue<bool>($"{SectionName}:Enabled"))
            return options;
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// Validate operator input before a character or spellbook mutation is attempted.
    /// Disabled configuration is deliberately inert, so an incomplete disabled block
    /// cannot affect ordinary logins.
    /// </summary>
    public void Validate()
    {
        if (!Enabled)
            return;

        if (!Enum.IsDefined(MinimumSecurity) || MinimumSecurity < AccountSecurity.Moderator)
            throw new InvalidOperationException($"{SectionName}: MinimumSecurity must be Moderator, GameMaster, or Administrator.");

        ValidateIds(nameof(SpellIds), SpellIds, maxCount: 64, allowEmpty: true);
        ValidateIds(nameof(ExcludedSpellIds), ExcludedSpellIds, maxCount: 64, allowEmpty: true);

        HashSet<uint> catalogIds = GmFirstLoginToolCatalog.All.Select(tool => tool.Id).ToHashSet();
        uint unknownSubset = SpellIds.FirstOrDefault(id => !catalogIds.Contains(id));
        if (unknownSubset != 0)
            throw new InvalidOperationException($"{SectionName}: SpellIds contains unknown catalog spell id {unknownSubset}.");
        uint unknownExcluded = ExcludedSpellIds.FirstOrDefault(id => !catalogIds.Contains(id));
        if (unknownExcluded != 0)
            throw new InvalidOperationException($"{SectionName}: ExcludedSpellIds contains unknown catalog spell id {unknownExcluded}.");
    }

    private static void ValidateIds(string propertyName, uint[]? ids, int maxCount, bool allowEmpty)
    {
        if (ids is null || (!allowEmpty && ids.Length == 0) || ids.Length > maxCount)
            throw new InvalidOperationException($"{SectionName}: {propertyName} must contain at most {maxCount} ids.");
        if (ids.Any(id => id == 0))
            throw new InvalidOperationException($"{SectionName}: {propertyName} cannot contain spell id 0.");
        if (ids.Distinct().Count() != ids.Length)
            throw new InvalidOperationException($"{SectionName}: {propertyName} cannot contain duplicates.");
    }
}
