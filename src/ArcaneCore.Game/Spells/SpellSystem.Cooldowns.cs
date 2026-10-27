using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos SpellCaster::IsSpellReady + HasGCD: no running spell cooldown, category cooldown or
    /// global cooldown of the spell's StartRecoveryCategory.
    /// </summary>
    public bool IsSpellReady(Unit unit, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(spell);
        return GetState(unit.Guid) is { } state ? IsSpellReady(state, spell) : !WaitsForCreatedObject(unit, spell);
    }

    /// <summary>
    /// vmangos SpellCaster::IsSpellReady proper (SpellCaster.cpp:2487-2515): <see cref="IsSpellReady(Unit, SpellInfo)"/> without the global
    /// cooldown, which vmangos keeps apart (HasGCD).
    /// </summary>
    public bool IsSpellReadyIgnoringGlobalCooldown(Unit unit, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(spell);
        return GetState(unit.Guid) is { } state ? IsSpellReady(state, spell, includeGlobal: false) : !WaitsForCreatedObject(unit, spell);
    }

    /// <summary>
    /// Ranged (hunter lane): a COOLDOWN_ON_EVENT spell waits while the object it created lives (Unit::AddGameObject). The object, not the
    /// unit's spell state, holds this, so it is asked even when the state was dropped as idle (an event spell starts no cooldown at its go).
    /// </summary>
    private bool WaitsForCreatedObject(Unit unit, SpellInfo spell)
        => spell.HasAttribute(SpellAttributes.CooldownOnEvent) && SpellObjects.IsCreatedBySpell(unit, spell.Id);

    private bool IsSpellReady(UnitSpellState state, SpellInfo spell, uint? itemCategory = null, Item? castItem = null, bool includeGlobal = true)
    {
        ItemSpellCooldown cooldown = ItemSpellCooldowns.Pick(spell, castItem);
        if (WaitsForCreatedObject(state.Unit, spell))
        {
            return false;
        }

        uint now = NowMs;
        if (state.SpellCooldowns.TryGetValue(spell.Id, out uint until) && until > now)
        {
            return false;
        }

        // crafting lane: an item spell may carry its own category (Player.cpp:22139-22160); an explicit item use passes it.
        uint category = itemCategory ?? cooldown.Category;
        if (category != 0 && state.CategoryCooldowns.TryGetValue(category, out until) && until > now)
        {
            return false;
        }

        // Spell batching: a held cast has not started its cooldown yet (that happens at its go); it blocks a second request meanwhile.
        if (_batchedGoes.Count != 0 && HasBatchedGoOnCooldown(state.Unit, spell, category))
        {
            return false;
        }

        if (spell.PreventionType == SpellConstants.PreventionTypeSilence && state.SchoolLockouts.TryGetValue(spell.School, out until) && until > now)
        {
            return false;
        }

        return !includeGlobal || !(state.GlobalCooldowns.TryGetValue(spell.StartRecoveryCategory, out until) && until > now);
    }

    /// <summary>vmangos Player::AddGCD (see <see cref="SpellConstants.GlobalCooldownCategory"/>).</summary>
    private void AddGlobalCooldown(UnitSpellState state, SpellInfo spell)
    {
        if (state.Unit is Player { Cheats: not PlayerCheats.None } cheater && cheater.HasCheat(PlayerCheats.NoCooldown))
        {
            return; // vmangos Spell::prepare (Spell.cpp:3492): no GCD under the no-cooldown cheat
        }

        int duration = (int)spell.StartRecoveryTime;
        if (spell.StartRecoveryCategory == 0 && duration == 0)
        {
            return;
        }

        // vmangos Player::AddGCD (Player.cpp:22101-22105): SPELLMOD_GLOBAL_COOLDOWN before the haste scaling, so a modified
        // duration no longer equals 1500 and is not scaled.
        duration = ModInt(state.Unit, spell, SpellModOp.GlobalCooldown, duration);

        if (spell.StartRecoveryCategory == SpellConstants.GlobalCooldownCategory && duration == 1500
            && spell.DamageClass is not (SpellDamageClass.Melee or SpellDamageClass.Ranged)
            && !spell.HasAttribute(SpellAttributes.UsesRangedSlot) && !spell.HasAttribute(SpellAttributes.IsAbility))
        {
            duration = Math.Clamp((int)(duration * CastSpeed(state.Unit)), 1000, 1500);
        }

        if (duration < 1)
        {
            return;
        }

        duration = Math.Max(1, duration - (int)MapUpdateIntervalMs);
        state.GlobalCooldowns[spell.StartRecoveryCategory] = NowMs + (uint)duration;
    }

    /// <summary>
    /// vmangos Spell::SendSpellCooldown → SpellCaster::AddCooldown: start the spell and category
    /// cooldowns unless the spell is passive. The client starts its own timers from SMSG_SPELL_GO
    /// for casts it requested (vmangos sends nothing then); for server-initiated (triggered) casts
    /// SMSG_SPELL_COOLDOWN tells the client — decision recorded in docs/areas/spells.md.
    /// COOLDOWN_ON_EVENT spells are not started here: their event (the aura fading, StartCooldownOnAuraEnd; a created object going, RemoveSpellObject) calls this with onEvent.
    /// </summary>
    private void AddCooldown(UnitSpellState state, SpellInfo spell, bool triggered, int? itemCooldownMs = null,
        int? itemCategoryCooldownMs = null, uint? itemCategory = null, uint itemId = 0, bool onEvent = false, Item? castItem = null)
    {
        if (spell.IsPassive || (spell.HasAttribute(SpellAttributes.CooldownOnEvent) && !onEvent))
        {
            return;
        }

        if (state.Unit is Player { Cheats: not PlayerCheats.None } cheater && cheater.HasCheat(PlayerCheats.NoCooldown))
        {
            return; // vmangos Spell::SendSpellCooldown (Spell.cpp:3993-3995): the no-cooldown cheat starts none
        }

        if (itemCooldownMs is < -1 || itemCategoryCooldownMs is < -1)
        {
            AnomalyLedger.Shared.Report(
                "spell",
                "cooldown-invalid",
                FormatSpellSubject(spell.Id),
                $"unit={FormatUnitSubject(state.Unit)} recoveryTime={spell.RecoveryTime} categoryRecoveryTime={spell.CategoryRecoveryTime} itemCooldownMs={itemCooldownMs} itemCategoryCooldownMs={itemCategoryCooldownMs}",
                AnomalySeverity.Warning);
        }

        // ranged (hunter lane): a ranged-slot spell also waits out the weapon speed (Player.cpp:22193-22197).
        // crafting lane: an item spell may carry its own category and times (Player.cpp:22139-22160): the explicit item-use values
        // when given, else the cast item's template; an ItemPrototype cooldown of -1 means the Spell.dbc recovery (Player.cpp:22139).
        ItemSpellCooldown picked = ItemSpellCooldowns.Pick(spell, castItem);
        uint recovery = itemCooldownMs is { } itemRecovery ? (itemRecovery < 0 ? spell.RecoveryTime : (uint)itemRecovery) : picked.RecoveryTime;
        recovery += RangedRecoveryMs(state.Unit, spell);
        uint categoryRecovery = itemCategoryCooldownMs is { } itemCategoryRecovery
            ? (itemCategoryRecovery < 0 ? spell.CategoryRecoveryTime : (uint)itemCategoryRecovery)
            : picked.CategoryRecoveryTime;
        uint category = itemCategory ?? picked.Category;
        bool itemOwned = itemCategory is not null || itemCooldownMs is not null || itemCategoryCooldownMs is not null || castItem is not null;
        if (recovery == 0 && categoryRecovery == 0)
        {
            return;
        }

        if (recovery > MaxReasonableCooldownMs || categoryRecovery > MaxReasonableCooldownMs)
        {
            AnomalyLedger.Shared.Report(
                "spell",
                "cooldown-invalid",
                FormatSpellSubject(spell.Id),
                $"unit={FormatUnitSubject(state.Unit)} recoveryMs={recovery} categoryRecoveryMs={categoryRecovery}",
                AnomalySeverity.Warning);
            // Report-only: the cooldown is applied unchanged (see the Math.Max clamps below).
        }

        // vmangos Player::AddCooldown (Player.cpp:22200-22206): the cooldown spell mod applies to the spell's own time when it
        // has one, otherwise to the category time ("blizzlike code for choosing which is recTime > categoryRecTime").
        if (recovery > 0)
        {
            recovery = (uint)Math.Max(ModInt(state.Unit, spell, SpellModOp.Cooldown, (int)recovery), 0);
        }
        else if (category != 0 && categoryRecovery > 0)
        {
            categoryRecovery = (uint)Math.Max(ModInt(state.Unit, spell, SpellModOp.Cooldown, (int)categoryRecovery), 0);
        }

        uint now = NowMs;
        if (recovery > 0)
        {
            state.SpellCooldowns[spell.Id] = now + recovery;
        }

        if (category != 0 && categoryRecovery > 0)
        {
            // vmangos CooldownMap::AddCooldown: a running category cooldown keeps its original owner and is only extended.
            if (!state.CategoryOwners.ContainsKey(category) || !state.CategoryCooldowns.TryGetValue(category, out uint running) || running <= now)
            {
                state.CategoryOwners[category] = spell.Id;
            }

            state.CategoryCooldowns[category] = now + categoryRecovery;
        }

        if (itemOwned)
        {
            state.CooldownOwners[spell.Id] = new ItemCooldownOwner(itemId != 0 ? itemId : castItem?.Entry ?? 0, category, spell.Id);
        }
        else
        {
            state.CooldownOwners.Remove(spell.Id);
        }

        if (triggered && state.Unit is Player player)
        {
            uint ms = Math.Max(recovery, categoryRecovery);
            player.Session.Send(WorldOpcode.SmsgSpellCooldown, SpellPackets.BuildSpellCooldown(player.Guid, [(spell.Id, ms)]));
        }
    }

    /// <summary>Clear a spell's cooldown and its category cooldown, and tell the client (vmangos Player::RemoveSpellCooldown → SMSG_CLEAR_COOLDOWN).</summary>
    public void ClearCooldown(Unit unit, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state)
        {
            return;
        }

        bool removed = state.SpellCooldowns.Remove(spellId);
        if (Store.Get(spellId) is { } spell)
        {
            uint category = state.CooldownOwners.GetValueOrDefault(spellId).Category;
            uint cleared = category != 0 ? category : spell.Category;
            removed |= state.CategoryCooldowns.Remove(cleared);
            state.CategoryOwners.Remove(cleared);
            state.CooldownOwners.Remove(spellId);
        }

        if (removed && unit is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgClearCooldown, SpellPackets.BuildClearCooldown(spellId, player.Guid));
        }
    }

    /// <summary>Running cooldowns for SMSG_INITIAL_SPELLS (vmangos Player::SendInitialSpells cooldown block).</summary>
    public IReadOnlyList<InitialSpellCooldown> GetActiveCooldowns(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state)
        {
            return [];
        }

        uint now = NowMs;
        var result = new List<InitialSpellCooldown>();
        foreach ((uint spellId, uint until) in state.SpellCooldowns)
        {
            if (until > now && Store.Get(spellId) is { } spell)
            {
                ItemCooldownOwner owner = state.CooldownOwners.GetValueOrDefault(spellId);
                uint categoryId = owner.Category != 0 ? owner.Category : spell.Category;
                uint category = categoryId != 0 && state.CategoryCooldowns.TryGetValue(categoryId, out uint catUntil) && catUntil > now
                    ? catUntil - now : 0;
                result.Add(new InitialSpellCooldown(spellId, owner.ItemId, categoryId, until - now, category));
            }
        }

        foreach ((uint spellId, ItemCooldownOwner owner) in state.CooldownOwners)
        {
            if ((state.SpellCooldowns.TryGetValue(spellId, out uint spellUntil) && spellUntil > now)
                || owner.Category == 0 || !state.CategoryCooldowns.TryGetValue(owner.Category, out uint until) || until <= now
                || Store.Get(spellId) is not { } spell)
                continue;
            result.Add(new InitialSpellCooldown(spellId, owner.ItemId, owner.Category, 0, until - now));
        }

        // A category cooldown nothing above lists (COOLDOWN_ON_EVENT spells such as Stealth, Prowl, Shadowmeld, Presence of Mind have no
        // cooldown of their own, only the category; a relog restores just the category row): the client keeps it under a spell of the
        // category (vmangos "client keeps track of category cd by original spellId"), so name the recorded owner or else a known spell.
        foreach ((uint categoryId, uint categoryUntil) in state.CategoryCooldowns.OrderBy(c => c.Key))
        {
            if (categoryUntil <= now || result.Any(c => c.Category == categoryId))
            {
                continue;
            }

            uint spellId = state.CategoryOwners.TryGetValue(categoryId, out uint recordedOwner) && Store.Get(recordedOwner) is not null
                ? recordedOwner : FindCategorySpell(unit, categoryId);
            if (spellId != 0)
            {
                result.Add(new InitialSpellCooldown(spellId, 0, categoryId, 0, categoryUntil - now));
            }
        }

        return result;
    }

    /// <summary>The lowest-id spell of a category the unit may use (a player's spellbook), COOLDOWN_ON_EVENT spells first; 0 when none.</summary>
    private uint FindCategorySpell(Unit unit, uint category)
    {
        uint best = 0;
        bool bestOnEvent = false;
        foreach (SpellInfo candidate in Store.All)
        {
            if (candidate.Category != category || (unit is Player player && Spellbook is { } book && !book.HasSpell(player, candidate.Id)))
            {
                continue;
            }

            bool onEvent = candidate.HasAttribute(SpellAttributes.CooldownOnEvent);
            if (best == 0 || (onEvent && !bestOnEvent) || (onEvent == bestOnEvent && candidate.Id < best))
            {
                best = candidate.Id;
                bestOnEvent = onEvent;
            }
        }

        return best;
    }

    private static void ExpireCooldowns(UnitSpellState state, uint now)
    {
        Expire(state.SpellCooldowns, now);
        Expire(state.CategoryCooldowns, now);
        Expire(state.GlobalCooldowns, now);
        foreach ((SpellSchool school, uint until) in state.SchoolLockouts.ToArray())
        {
            if (until <= now)
            {
                state.SchoolLockouts.Remove(school);
            }
        }

        static void Expire(Dictionary<uint, uint> cooldowns, uint now)
        {
            foreach ((uint key, uint until) in cooldowns.ToArray())
            {
                if (until <= now)
                {
                    cooldowns.Remove(key);
                }
            }
        }
    }
}
