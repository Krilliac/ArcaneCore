using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Scripting.Modules;

/// <summary>The settings of <see cref="DuelResetScript"/> (mod-duel-reset conf/duelreset.conf.dist).</summary>
public sealed class DuelResetSettings
{
    /// <summary>The module's configuration section (ArcaneCore.World DuelResetModule).</summary>
    public const string SectionName = "Modules:DuelReset";

    /// <summary>Load the module at all. Off by default: it changes duels in Elwynn Forest, Durotar and the Gates of Ironforge.</summary>
    public bool Enabled { get; set; }

    /// <summary><c>DuelReset.Cooldowns</c>: reset cooldowns when the duel starts and restore them when it is won.</summary>
    public bool Cooldowns { get; set; } = true;

    /// <summary><c>DuelReset.HealthMana</c>: fill health and power when the duel starts and restore them when it is won.</summary>
    public bool HealthMana { get; set; } = true;

    /// <summary><c>DuelReset.CooldownAge</c>: seconds a cooldown must have run before the start resets it.</summary>
    public uint CooldownAge { get; set; } = 30;

    /// <summary><c>DuelReset.Zones</c>: zone ids separated by ';'. "" means any zone (areas then do not matter); "0" means none.</summary>
    public string Zones { get; set; } = "0";

    /// <summary><c>DuelReset.Areas</c>: area ids separated by ';' (default Elwynn Forest, Durotar, Gates of Ironforge); "" or "0" means none.</summary>
    public string Areas { get; set; } = "12;14;809";

    /// <summary>The ids of a list; null for "" (any). Id 0 is no zone or area (the module's "none"), so it never matches an unknown position.</summary>
    internal static uint[]? ParseList(string? list)
        => string.IsNullOrWhiteSpace(list)
            ? null
            : [.. list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(uint.Parse).Where(id => id != 0)];
}

/// <summary>
/// AzerothCore mod-duel-reset (src/DuelReset.cpp, DuelReset_scripts.cpp; originally TrinityCore, ShinDarth/GigaDev90, reworked by Gozzim),
/// re-implemented on the script hooks. At the duel start, in a whitelisted zone or area, both players' short cooldowns are saved and reset
/// and their health and power filled; when the duel is won the cooldowns, health and mana from before it come back. A fled or interrupted
/// duel restores nothing, as in the module. World thread.
/// <para>
/// Deviations: the cooldown limits use the spell's own and category recovery times without spell mods (ArcaneCore has no per-player
/// cooldown-mod lookup here); the saved state is dropped at the end of every duel, where the module keeps it after a fled duel until the
/// next one overwrites it. Pet cooldowns are cleared outright (no age or ten-minute filter) at the start and again when a won duel ends;
/// they are not saved or restored, as in the module (TrinityCore duel_reset.cpp:124-126).
/// </para>
/// </summary>
public sealed class DuelResetScript : IPlayerHooks
{
    private const uint TenMinutesMs = 10 * 60 * 1000;

    private readonly Func<SpellSystem?> _spells;
    private readonly Dictionary<Player, Saved> _saved = [];
    private DuelResetSettings _settings = new();
    private uint[]? _zones = [];
    private uint[]? _areas = [12, 14, 809];

    /// <param name="settings">The configured settings.</param>
    /// <param name="spells">The world's spell system, looked up late (it may attach after the module loads).</param>
    public DuelResetScript(DuelResetSettings settings, Func<SpellSystem?> spells)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        Apply(settings);
    }

    public DuelResetSettings Settings => _settings;

    /// <summary>Take new settings (DuelReset::LoadConfig).</summary>
    public void Apply(DuelResetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        uint[]? zones = DuelResetSettings.ParseList(settings.Zones);
        uint[]? areas = DuelResetSettings.ParseList(settings.Areas);
        _settings = settings;
        _zones = zones;
        _areas = areas;
    }

    /// <summary>DuelReset::IsAllowedInArea: the zone or the area is listed, or the zone list is empty (any).</summary>
    public bool IsAllowedInArea(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_zones is null || (player.ZoneId != 0 && _zones.Contains(player.ZoneId)))
        {
            return true;
        }

        uint area = player.Map is { } map ? map.GetZoneAndAreaId(player.X, player.Y, player.Z).AreaId : 0;
        return area != 0 && _areas is not null && _areas.Contains(area);
    }

    /// <summary>Whether a saved pre-duel state exists for <paramref name="player"/> (tests).</summary>
    public bool HasSavedState(Player player) => _saved.ContainsKey(player);

    public void OnDuelStart(Player first, Player second)
    {
        if (!IsAllowedInArea(first))
        {
            return;
        }

        SpellSystem? spells = _spells();
        foreach (Player player in (Span<Player>)[first, second])
        {
            var saved = new Saved();
            if (_settings.Cooldowns && spells is not null)
            {
                uint now = spells.NowMs;
                saved.Cooldowns = [.. spells.GetActiveCooldowns(player).Select(c => new SavedCooldown(c, now))];
                ResetSpellCooldowns(spells, player, onStart: true);
            }

            if (_settings.HealthMana)
            {
                saved.Health = player.Health;
                if (UsesMana(player))
                {
                    saved.Mana = GetPower(player, PowerType.Mana);
                }

                ResetAllPowers(player);
            }

            _saved[player] = saved;
        }
    }

    public void OnDuelEnd(Player winner, Player loser, DuelCompleteType type)
    {
        // The zone is not checked here (the module: it "would open options or abuse"); fled and interrupted duels restore nothing.
        if (type == DuelCompleteType.Won)
        {
            Restore(winner);
            Restore(loser);
        }

        _saved.Remove(winner);
        _saved.Remove(loser);
    }

    public void OnLogout(Player player) => _saved.Remove(player);

    private void Restore(Player player)
    {
        if (!_saved.TryGetValue(player, out Saved? saved))
        {
            return;
        }

        if (_settings.Cooldowns && saved.Cooldowns is { } cooldowns && _spells() is { } spells)
        {
            RestoreCooldowns(spells, player, cooldowns);
        }

        if (_settings.HealthMana)
        {
            if (saved.Health is { } health)
            {
                player.Health = Math.Min(Math.Max(health, 1), player.MaxHealth);
            }

            if (saved.Mana is { } mana && UsesMana(player))
            {
                SetPower(player, PowerType.Mana, Math.Min(mana, GetMaxPower(player, PowerType.Mana)));
            }
        }
    }

    /// <summary>
    /// DuelReset::ResetSpellCooldowns: clear every running cooldown under ten minutes (own, category and remaining time); at the start
    /// only those that have run at least <see cref="DuelResetSettings.CooldownAge"/> seconds. A spell without its own (or category)
    /// recovery passes that check, as the module's unsigned arithmetic does. The owner's pet is then reset too (<see cref="ResetPetCooldowns"/>).
    /// </summary>
    private void ResetSpellCooldowns(SpellSystem spells, Player player, bool onStart)
    {
        uint ageMs = _settings.CooldownAge * 1000;
        foreach (InitialSpellCooldown cooldown in spells.GetActiveCooldowns(player).ToArray())
        {
            if (spells.Store.Get(cooldown.SpellId) is not { } spell)
            {
                continue;
            }

            uint remaining = Math.Max(cooldown.CooldownMs, cooldown.CategoryCooldownMs);
            uint total = spell.RecoveryTime;
            uint category = spell.CategoryRecoveryTime;
            if (remaining == 0 || total >= TenMinutesMs || category >= TenMinutesMs || remaining >= TenMinutesMs)
            {
                continue;
            }

            if (onStart && (!HasRun(total, remaining, ageMs) || !HasRun(category, remaining, ageMs)))
            {
                continue;
            }

            spells.ClearCooldown(player, cooldown.SpellId);
        }

        ResetPetCooldowns(spells, player);
    }

    /// <summary>
    /// duel_reset ResetSpellCooldowns "pet cooldowns" (TrinityCore duel_reset.cpp:124-126): every running cooldown of the pet is cleared, at the start
    /// and again when a won duel ends; nothing is saved for the pet. The owner is told per spell with SMSG_CLEAR_COOLDOWN and the pet's GUID
    /// (vmangos PetHandler.cpp:534; layout vmangos Spell.cpp:278-282, gtker smsg_clear_cooldown.wowm). SpellSystem.ClearCooldown notifies only
    /// for a Player, so it sends nothing for the pet and there is no duplicate packet.
    /// </summary>
    private static void ResetPetCooldowns(SpellSystem spells, Player owner)
    {
        if (owner.GetPet() is not { } pet)
        {
            return;
        }

        foreach (InitialSpellCooldown cooldown in spells.GetActiveCooldowns(pet).ToArray())
        {
            spells.ClearCooldown(pet, cooldown.SpellId);
            owner.Session.Send(WorldOpcode.SmsgClearCooldown, SpellPackets.BuildClearCooldown(cooldown.SpellId, pet.Guid));
        }
    }

    private static bool HasRun(uint recovery, uint remaining, uint ageMs) => recovery == 0 || remaining > recovery || recovery - remaining > ageMs;

    /// <summary>
    /// DuelReset::RestoreCooldownStateAfterDuel: the duel's short cooldowns are cleared, the saved ones that are still running come back,
    /// and the client is told every restored cooldown under ten minutes. Long cooldowns started in the duel (professions) are kept as they are.
    /// </summary>
    private void RestoreCooldowns(SpellSystem spells, Player player, IReadOnlyList<SavedCooldown> saved)
    {
        ResetSpellCooldowns(spells, player, onStart: false);
        uint now = spells.NowMs;
        var restored = new List<PersistedCooldown>();
        var client = new List<(uint SpellId, uint Ms)>();
        foreach (SavedCooldown c in saved)
        {
            uint spellLeft = Left(c.SpellEndMs, now);
            uint categoryLeft = Left(c.CategoryEndMs, now);
            if (spellLeft != 0)
            {
                restored.Add(new PersistedCooldown(SpellCooldownKind.Spell, c.Cooldown.SpellId, spellLeft, c.Cooldown.ItemId, c.Cooldown.Category, c.Cooldown.SpellId));
            }

            if (categoryLeft != 0 && c.Cooldown.Category != 0)
            {
                restored.Add(new PersistedCooldown(SpellCooldownKind.Category, c.Cooldown.Category, categoryLeft, c.Cooldown.ItemId, c.Cooldown.Category, c.Cooldown.SpellId));
            }

            uint left = Math.Max(spellLeft, categoryLeft);
            if (left is > 0 and <= TenMinutesMs)
            {
                client.Add((c.Cooldown.SpellId, left));
            }
        }

        // RestoreCooldowns takes absolute ends: with "now" 0 the end is the time left.
        spells.RestoreCooldowns(player, restored, nowUnixMs: 0);
        if (client.Count > 0)
        {
            player.Session.Send(WorldOpcode.SmsgSpellCooldown, SpellPackets.BuildSpellCooldown(player.Guid, client));
        }
    }

    private static uint Left(uint endMs, uint now) => endMs == 0 || unchecked((int)(endMs - now)) <= 0 ? 0 : endMs - now;

    private static bool UsesMana(Player player) => player.PowerType == PowerType.Mana || player.Class == Class.Druid;

    /// <summary>Player::ResetAllPowers (AzerothCore): full health; mana and energy full, rage empty.</summary>
    private static void ResetAllPowers(Player player)
    {
        player.Health = player.MaxHealth;
        switch (player.PowerType)
        {
            case PowerType.Mana:
            case PowerType.Energy:
                SetPower(player, player.PowerType, GetMaxPower(player, player.PowerType));
                break;
            case PowerType.Rage:
                SetPower(player, PowerType.Rage, 0);
                break;
        }
    }

    private static uint GetPower(Unit unit, PowerType power) => unit.GetUInt32(UpdateFields.UnitFieldPower1 + (int)power);

    private static uint GetMaxPower(Unit unit, PowerType power) => unit.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power);

    private static void SetPower(Unit unit, PowerType power, uint value) => unit.SetUInt32(UpdateFields.UnitFieldPower1 + (int)power, value);

    private sealed class Saved
    {
        public IReadOnlyList<SavedCooldown>? Cooldowns { get; set; }

        public uint? Health { get; set; }

        public uint? Mana { get; set; }
    }

    private readonly record struct SavedCooldown(InitialSpellCooldown Cooldown, uint SpellEndMs, uint CategoryEndMs)
    {
        public SavedCooldown(InitialSpellCooldown cooldown, uint now)
            : this(cooldown, cooldown.CooldownMs == 0 ? 0 : now + cooldown.CooldownMs, cooldown.CategoryCooldownMs == 0 ? 0 : now + cooldown.CategoryCooldownMs)
        {
        }
    }
}
