using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Net;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Form = ArcaneCore.Game.Spells.ShapeshiftForm;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// Builds a <see cref="RotationState"/> from the live world (world thread): the bot, its victim, group, attackers, pet, totems
/// and consumables, plus the castability check of vmangos CombatBotBaseAI::CanTryToCastSpell (CombatBotBaseAI.cpp:2807-2860)
/// over the ordinary spell rules (cooldowns, global cooldown, power, aura states, form, reagents, equipped items, range,
/// line of sight). The check only avoids pointless requests; the ordinary CMSG_CAST_SPELL handler still decides.
/// </summary>
internal static class PlayerbotCombatView
{
    /// <summary>vmangos SpellCheck leeway at cast start for a player (SpellConstants.PlayerStrictRangeLeeway).</summary>
    private const float RangeLeeway = 1.25f;

    private const uint ItemClassConsumable = 0;
    private const uint ItemClassWeapon = 2;
    private const uint ItemClassArmor = 4;
    private const uint SubClassPotion = 1;
    private const uint SubClassBandage = 7;
    private const uint SubClassShield = 6;
    private const uint SubClassWand = 19;

    private sealed record ExtraViewState(RotationItem? ManaPotion, bool MainHandHasWeapon, bool MainHandHasTempEnchant);

    private static readonly ConditionalWeakTable<RotationState, ExtraViewState> Extras = new();

    internal static void AttachExtra(RotationState state, RotationItem? manaPotion, bool mainHandHasWeapon = false, bool mainHandHasTempEnchant = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        Extras.AddOrUpdate(state, new ExtraViewState(manaPotion, mainHandHasWeapon, mainHandHasTempEnchant));
    }

    internal static RotationItem? GetManaPotion(RotationState state)
        => state is not null && Extras.TryGetValue(state, out ExtraViewState? extra) ? extra.ManaPotion : null;

    internal static bool CanImbueMainHand(RotationState state)
    {
        if (state is null)
            return false;
        if (Extras.TryGetValue(state, out ExtraViewState? extra))
            return extra.MainHandHasWeapon && !extra.MainHandHasTempEnchant;

        return !state.Self.HasAura(PlayerbotShamanRotation.WindfuryWeapon)
            && !state.Self.HasAura(PlayerbotShamanRotation.RockbiterWeapon)
            && !state.Self.HasAura(PlayerbotShamanRotation.FlametongueWeapon)
            && !state.Self.HasAura(PlayerbotShamanRotation.FrostbrandWeapon);
    }

    internal static RotationState Build(WorldSession session, SpellFeature feature, Player player, Unit? victim,
        PlayerbotAbilities spells, PlayerbotRole role, Func<SpellInfo, bool> blocked, bool kiteHolding = false)
    {
        SpellSystem system = feature.System;
        Map? map = player.Map;
        GroupManager? groups = session.Services.GetService<SocialFeature>()?.Context.Groups;
        var units = new Dictionary<ObjectGuid, Unit> { [player.Guid] = player };

        RotationUnit self = Describe(system, player, player, groups);
        RotationUnit? victimView = null;
        if (victim is { IsInWorld: true } && map is not null && ReferenceEquals(victim.Map, map))
        {
            units[victim.Guid] = victim;
            victimView = Describe(system, player, victim, groups);
        }

        var party = new List<RotationUnit>();
        bool grouped = false;
        if (groups?.GetGroup(player.Guid) is { } group && map is not null)
        {
            foreach (GroupMemberSlot slot in group.Members)
            {
                if (slot.Guid == player.Guid) continue;
                grouped = true;
                if (map.FindObject(slot.Guid) is Player member && member.IsInWorld)
                {
                    units[member.Guid] = member;
                    party.Add(Describe(system, player, member, groups));
                }
            }
            party.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        var attackers = new List<RotationUnit>();
        foreach (Unit attacker in player.Combat.Attackers)
        {
            if (!attacker.IsInWorld || !attacker.IsAlive || !ReferenceEquals(attacker.Map, map)) continue;
            units[attacker.Guid] = attacker;
            attackers.Add(Describe(system, player, attacker, groups));
        }
        attackers.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        RotationPetStatus petStatus = RotationPetStatus.None;
        RotationUnit? petView = null;
        bool petFighting = false, petFightingCrowdControlled = false;
        if (!player.PetGuid.IsEmpty && map?.FindObject(player.PetGuid) is Creature pet)
        {
            units[pet.Guid] = pet;
            petStatus = pet.IsAlive ? RotationPetStatus.Alive : RotationPetStatus.Dead;
            petView = Describe(system, player, pet, groups);
            petFighting = pet.Combat.Victim is not null;
            petFightingCrowdControlled = pet.Combat.Victim is { } petVictim && system.HasAuraPetShouldAvoidBreaking(petVictim, pet);
        }

        ArcaneCore.Kernel.Characters.Pets.PersistentPetSnapshot? cachedPet = null;
        bool hasCachedPet = player.Class == Class.Hunter && player.PetGuid.IsEmpty
            && session.Services.GetService<PetsFeature>()?.Service.TryGetCachedCurrentPet(player, out cachedPet) == true
            && cachedPet is not null;
        if (hasCachedPet && cachedPet!.Health == 0)
        {
            petStatus = RotationPetStatus.Dead;
        }

        bool canCallPet = hasCachedPet && cachedPet!.Health > 0;

        RotationUnit? tameCandidate = null;
        if (player.Class == Class.Hunter && player.PetGuid.IsEmpty && !hasCachedPet && !player.Combat.IsInCombat
            && spells.Has(PlayerbotHunterRotation.TameBeast) && map is not null
            && FindTameCandidate(session, player, map) is { } beast)
        {
            units[beast.Guid] = beast;
            tameCandidate = Describe(system, player, beast, groups);
        }

        Item? mainHand = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand);
        Item? ranged = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged);
        Item? offHand = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.OffHand);
        Form form = ShapeshiftService.GetForm(player);
        uint? formFlags = form == Form.None ? null : session.Services.GetService<StanceFeature>()?.Service?.GetFormFlags(form);

        var cooldownSpells = new HashSet<uint>();
        var cooldownCategories = new HashSet<uint>();
        foreach (InitialSpellCooldown cooldown in system.GetActiveCooldowns(player))
        {
            cooldownSpells.Add(cooldown.SpellId);
            if (cooldown.Category != 0) cooldownCategories.Add(cooldown.Category);
        }

        IReadOnlyList<SpellAuraHolder> ownAuras = system.GetAuras(player);
        var totems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (player.Class == Class.Shaman && map is not null)
        {
            foreach (ObjectGuid guid in player.VisibleObjects)
            {
                if (map.FindObject(guid) is Creature totem && totem.IsAlive && TotemQuery.GetOwnerGuid(totem) == player.Guid)
                    totems.Add(totem.Template.Name);
            }
        }

        var comboService = session.Services.GetService<ComboFeature>()?.Service;

        bool CanCast(SpellInfo spell, RotationUnit target)
        {
            if (blocked(spell) || !units.TryGetValue(target.Guid, out Unit? unit)) return false;
            // A stunned, feared or confused bot sent its rotation anyway and the server answered CastResult Stunned at every think
            // (S-8); a spell that breaks the state (the PvP trinket, Will of the Forsaken) still passes.
            if (system.CheckCasterState(player, spell) != SpellCastResult.CastOk) return false;
            if (cooldownSpells.Contains(spell.Id) || spell.Category != 0 && cooldownCategories.Contains(spell.Category)) return false;
            if (spell.StartRecoveryTime > 0 && !system.IsGlobalCooldownReady(player, spell.StartRecoveryCategory)) return false;
            if (spell.CasterAuraState != AuraState.None && !HasAuraState(player, spell.CasterAuraState)) return false;
            if (spell.TargetAuraState != AuraState.None && !HasAuraState(unit, spell.TargetAuraState)) return false;
            if (spell.NeedsComboPoints && comboService is { } combo
                && SpellSystem.IsExplicitUnitTarget(spell.Effects[0].TargetA)
                && unit.Guid != combo.GetComboTarget(player))
                return false;
            if (spell.IsPositive && ArcaneCore.Game.Spells.Rules.Immunity.ImmunityRules.IsImmuneToSpell(system, unit, spell, ReferenceEquals(unit, player))) return false;
            // A crowd-control spell on a target at the immune diminishing level lands with no aura (vmangos Spell::DoSpellHitOnUnit): do not waste the cast.
            if (!spell.IsPositive && !ReferenceEquals(unit, player) && system.IsFullyDiminished is { } diminished && diminished(player, unit, spell)) return false;
            if (spell.Effects.Any(e => !e.IsEmpty && e.TargetA == SpellImplicitTarget.UnitCasterPet) && petStatus != RotationPetStatus.Alive) return false;
            if (!HasPower(player, spell)) return false;
            if (spell.GetErrorAtShapeshiftedCast((uint)form, formFlags) != SpellCastResult.CastOk) return false;
            if (!HasReagents(player, spell) || !HasEquippedItem(player, spell)) return false;
            if (spell.HasEffect(SpellEffectName.EnchantItemTemporary)
                && (mainHand is null || !ArcaneCore.Game.Crafting.Enchanting.ItemTargetRules.IsFit(spell, mainHand.Template)))
                return false;
            if (spell.HasAttribute(SpellAttributes.UsesRangedSlot) && !HasAmmoFor(player, ranged)) return false;
            if (FacingCastCheck.IsFromBehindOnly(spell) && !FacingCastCheck.IsBehindTarget(player, unit, strict: true)) return false;
            // CheckCast asks SpellLineOfSight before it ever sends SPELL_START. A refusal is remembered only for
            // PlayerbotCombatSpells.RefusalBackoffMs, so a priest in a Warsong flag room resubmitted Shadow Word: Pain
            // (and any other targeted spell) once that memory lapsed. Out of range is cheaper and already refuses.
            if (!InRange(player, unit, spell)) return false;
            return SpellLineOfSight.Check(player, spell, unit, triggered: false) == SpellCastResult.CastOk;
        }

        bool canUseItemsInForm = !ArcaneCore.Game.Items.ItemUse.ItemUseService.IsShapeShifted(player);
        var state = new RotationState
        {
            Spells = spells,
            Self = self,
            Role = role,
            Victim = victimView,
            Party = party,
            Attackers = attackers,
            InCombat = player.Combat.IsInCombat,
            PowerType = player.PowerType,
            Power = SpellSystem.GetPower(player, player.PowerType),
            MaxPower = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)player.PowerType),
            ComboPoints = ComboPoints(session, player),
            Form = form,
            IsMoving = player.Movement.HasFlag(MovementFlags.MaskMoving),
            IsRooted = system.IsRooted(player),
            IsSlowed = ownAuras.Any(h => !h.IsRemoved && h.HasAura(AuraType.ModDecreaseSpeed)),
            IsStealthed = ownAuras.Any(h => !h.IsRemoved && h.HasAura(AuraType.ModStealth)),
            WearsShield = offHand?.Template is { Class: ItemClassArmor, SubClass: SubClassShield },
            Pet = petStatus,
            PetUnit = petView,
            PetFighting = petFighting,
            PetFightingCrowdControlled = petFightingCrowdControlled,
            CanCallPet = canCallPet,
            TameCandidate = tameCandidate,
            AutoRepeatActive = system.HasAutoRepeat(player),
            KiteHolding = kiteHolding,
            HasRangedWeapon = ranged?.Template is { Class: ItemClassWeapon } rangedTemplate && rangedTemplate.SubClass != SubClassWand
                && !(ranged.MaxDurability > 0 && ranged.Durability == 0),
            HasWand = ranged?.Template is { Class: ItemClassWeapon, SubClass: SubClassWand }
                && !(ranged.MaxDurability > 0 && ranged.Durability == 0),
            HealingPotion = canUseItemsInForm ? FindConsumable(player, system, cooldownSpells, cooldownCategories, bandage: false) : null,
            Bandage = canUseItemsInForm ? FindConsumable(player, system, cooldownSpells, cooldownCategories, bandage: true) : null,
            Totems = totems,
            InGroup = grouped,
            CanCast = CanCast,
        };

        RotationItem? manaPotion = canUseItemsInForm && player.PowerType == PowerType.Mana
            && PlayerbotConsumables.FindBestManaPotion(player, system.Store, cooldownSpells, cooldownCategories) is { } mp
                ? new RotationItem(mp.Guid, mp.BagSlot, mp.Slot, mp.Template.Entry)
                : null;
        bool mainHandHasWeapon = mainHand?.Template is { Class: ItemClassWeapon }
            && !(mainHand.MaxDurability > 0 && mainHand.Durability == 0);
        bool mainHandHasTempEnchant = mainHand is not null && mainHand.GetUInt32(UpdateFields.ItemFieldEnchantment + 3) != 0;
        AttachExtra(state, manaPotion, mainHandHasWeapon, mainHandHasTempEnchant);
        return state;
    }

    private const float TameReach = 28f;
    private const uint TameScanIntervalMs = 5_000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, uint> s_lastTameScan = new();

    /// <summary>
    /// The nearest beast a pet-less hunter bot can tame from where it stands (SummonService.TameFailure rules: tameable beast, level at most the
    /// hunter's, unowned; plus not elite and not already fighting). Scans the map at most every 5 s per bot so live hunters exercise Tame Beast.
    /// </summary>
    private static Creature? FindTameCandidate(WorldSession session, Player player, Map map)
    {
        uint now = session.World.NowMs;
        if (s_lastTameScan.TryGetValue(player.Guid.Value, out uint last) && unchecked(now - last) < TameScanIntervalMs)
            return null;
        s_lastTameScan[player.Guid.Value] = now;
        if (map.FindUpdater<CreatureMapSystem>() is not { } creatures)
            return null;

        Creature? best = null;
        float bestSq = TameReach * TameReach;
        foreach (Creature c in creatures.Creatures)
        {
            if (!c.IsInWorld || !c.IsAlive || c.Level > player.Level || c.Template.Rank != 0 || !c.OwnerGuid.IsEmpty || !c.CharmerGuid.IsEmpty
                || c.Combat.IsInCombat || !Game.Pets.SummonService.IsTameable(c))
                continue;
            float dx = c.X - player.X, dy = c.Y - player.Y, dz = c.Z - player.Z;
            float sq = (dx * dx) + (dy * dy) + (dz * dz);
            if (sq < bestSq)
            {
                best = c;
                bestSq = sq;
            }
        }

        return best;
    }

    /// <summary>A snapshot of <paramref name="unit"/> as <paramref name="bot"/> sees it.</summary>
    internal static RotationUnit Describe(SpellSystem system, Player bot, Unit unit, GroupManager? groups)
    {
        var auras = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool periodicHeal = false;
        uint harmful = 0, helpful = 0;
        foreach (SpellAuraHolder holder in system.GetAuras(unit))
        {
            if (holder.IsRemoved) continue;
            auras.Add(holder.Spell.Name);
            periodicHeal |= holder.HasAura(AuraType.PeriodicHeal);
            if (holder.Spell.Dispel is > 0 and < 32)
            {
                if (holder.Spell.Dispel is > 0 and < 32)
                {
                    if (holder.IsPositive) helpful |= 1u << (int)holder.Spell.Dispel;
                    else harmful |= 1u << (int)holder.Spell.Dispel;
                }
            }
        }

        bool self = ReferenceEquals(unit, bot);
        Player? asPlayer = unit as Player;
        Form unitForm = ShapeshiftService.GetForm(unit);
        bool shield = asPlayer?.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.OffHand)?.Template is { Class: ItemClassArmor, SubClass: SubClassShield };
        return new RotationUnit
        {
            Guid = unit.Guid,
            IsSelf = self,
            IsPlayer = asPlayer is not null,
            Class = asPlayer is not null ? (byte)asPlayer.Class : (byte)0,
            Level = unit.Level,
            Health = unit.Health,
            MaxHealth = unit.MaxHealth,
            IsAlive = unit.IsAlive,
            Distance = self ? 0f : Distance(bot, unit),
            InMeleeRange = !self && InMeleeRange(bot, unit),
            IsCasting = system.GetState(unit.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting },
            IsMoving = unit is Creature creature ? creature.IsMoving : unit.Movement.HasFlag(MovementFlags.MaskMoving),
            TargetsBot = !self && ReferenceEquals(unit.Combat.Victim, bot),
            InCombat = unit.Combat.IsInCombat,
            CreatureType = unit is Creature c ? c.Template.CreatureType : 0,
            PowerType = unit.PowerType,
            IsTank = shield || unitForm is Form.DefensiveStance or Form.Bear or Form.DireBear,
            HasPeriodicHeal = periodicHeal,
            HarmfulDispelMask = harmful,
            HelpfulDispelMask = helpful,
            IsCrowdControlled = !self && system.HasBreakableByDamageCrowdControlAura(unit, bot),
            Auras = auras,
        };
    }

    private static float CombatReach(Unit unit)
    {
        float reach = unit.GetFloat(UpdateFields.UnitFieldCombatreach);
        return reach > 0 ? reach : SpellConstants.DefaultCombatReach;
    }

    /// <summary>The melee reach of vmangos CanReachWithMeleeAutoAttack as SpellSystem.CheckRange uses it (2D, both reaches + 4/3 + 1, at least 5).</summary>
    internal static bool InMeleeRange(Unit caster, Unit target)
    {
        float reach = Math.Max(SpellConstants.AttackDistance, CombatReach(caster) + CombatReach(target) + 1f + (4f / 3f));
        float dx = caster.X - target.X, dy = caster.Y - target.Y;
        return (dx * dx) + (dy * dy) < reach * reach;
    }

    /// <summary>vmangos Spell::CheckRange at cast start (SpellSystem.CheckRange with the strict player leeway).</summary>
    internal static bool InRange(Unit caster, Unit target, SpellInfo spell)
    {
        if (ReferenceEquals(caster, target) || spell.RangeIndex == SpellConstants.RangeIndexSelfOnly) return true;
        if (spell.RangeIndex == SpellConstants.RangeIndexCombat) return spell.IsNextMeleeSwing || InMeleeRange(caster, target);
        float combatDistance = Math.Max(0f, Distance(caster, target) - CombatReach(caster) - CombatReach(target));
        if (spell.Range.Max > 0 && combatDistance > spell.Range.Max + RangeLeeway) return false;
        return spell.Range.Min <= 0 || combatDistance >= spell.Range.Min;
    }

    private static bool HasAuraState(Unit unit, AuraState state)
        => (unit.GetUInt32(UpdateFields.UnitFieldAurastate) & (1u << ((int)state - 1))) != 0;

    private static bool HasPower(Player player, SpellInfo spell)
    {
        if (spell.PowerType == SpellMath.PowerHealth)
        {
            int healthCost = spell.CalculatePowerCost(player.Level, player.Health, player.MaxHealth,
                player.GetUInt32(UpdateFields.UnitFieldBaseHealth), player.GetUInt32(UpdateFields.UnitFieldBaseMana), player.Health);
            return player.Health > (uint)healthCost;
        }

        if (spell.PowerType is < 0 or > (int)PowerType.Happiness) return true;
        var power = (PowerType)spell.PowerType;
        uint current = SpellSystem.GetPower(player, power);
        int cost = spell.CalculatePowerCost(player.Level, current, player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power),
            player.GetUInt32(UpdateFields.UnitFieldBaseHealth), player.GetUInt32(UpdateFields.UnitFieldBaseMana), player.Health);
        return current >= (uint)cost;
    }

    private static bool HasReagents(Player player, SpellInfo spell)
    {
        foreach (SpellReagent reagent in spell.Reagents)
        {
            if (reagent.IsPresent && player.Inventory.GetItemCount(reagent.ItemId) < reagent.Count) return false;
        }
        foreach (uint tool in spell.Totems)
        {
            if (tool != 0 && player.Inventory.GetItemCount(tool) < 1) return false;
        }
        return true;
    }

    /// <summary>Spell.dbc EquippedItemClass / SubClassMask / InventoryTypeMask (vmangos Spell::CheckItems): some non-broken equipped item in the required slot must match.</summary>
    private static bool HasEquippedItem(Player player, SpellInfo spell)
    {
        // -1 is "none" in the client data; a zero class with no subclass mask (synthetic rows) asks for nothing either.
        if (spell.EquippedItemClass < 0 || (spell.EquippedItemClass == 0 && spell.EquippedItemSubClassMask == 0 && spell.EquippedItemInventoryTypeMask == 0))
            return true;
        bool usesRanged = spell.HasAttribute(SpellAttributes.UsesRangedSlot);
        foreach ((byte slot, Item item) in player.Inventory.Equipped)
        {
            if (item.MaxDurability > 0 && item.Durability == 0) continue;
            ItemTemplate template = item.Template;
            if (template.Class != (uint)spell.EquippedItemClass) continue;
            if (spell.EquippedItemClass == (int)ItemClassWeapon)
            {
                if (usesRanged && slot != InventorySlots.Ranged) continue;
                if (!usesRanged && slot != InventorySlots.MainHand) continue;
            }
            if (spell.EquippedItemSubClassMask != 0 && (spell.EquippedItemSubClassMask & (1 << (int)template.SubClass)) == 0) continue;
            if (spell.EquippedItemInventoryTypeMask != 0 && (spell.EquippedItemInventoryTypeMask & (1 << (int)template.InventoryType)) == 0) continue;
            return true;
        }
        return false;
    }

    /// <summary>A bow, gun or crossbow shoots only with ammunition in the bags (a wand or thrown weapon needs none).</summary>
    private static bool HasAmmoFor(Player player, Item? ranged)
    {
        if (ranged is { MaxDurability: > 0, Durability: 0 }) return false;
        if (ranged?.Template is not { Class: ItemClassWeapon } template || template.SubClass is SubClassWand or 16) return true;
        uint ammo = player.Inventory.AmmoId;
        return ammo != 0 && player.Inventory.GetItemCount(ammo) > 0;
    }

    private static int ComboPoints(WorldSession session, Player player)
        => session.Services.GetService<ComboFeature>()?.Service?.GetComboPoints(player)
            ?? (int)((player.GetUInt32(UpdateFields.PlayerFieldBytes) >> 8) & 0xFF);

    /// <summary>
    /// The first carried healing potion (a consumable potion whose use spell heals) or bandage (a consumable bandage whose use
    /// spell is a heal-over-time) the bot can use now: usable by it and off cooldown.
    /// </summary>
    private static RotationItem? FindConsumable(Player player, SpellSystem system, HashSet<uint> cooldownSpells,
        HashSet<uint> cooldownCategories, bool bandage)
    {
        foreach (Item item in player.Inventory.AllItems.Take(128))
        {
            ItemTemplate template = item.Template;
            if (template.Class != ItemClassConsumable || template.SubClass != (bandage ? SubClassBandage : SubClassPotion)
                || item.Count == 0 || !InventorySlots.IsInventoryPos(item.BagSlot, item.Slot)) continue;
            ItemSpell use = template.Spells[0];
            if (use.SpellId == 0 || use.Trigger != 0 || system.Store.Get(use.SpellId) is not { } spell) continue;
            bool heals = bandage ? spell.HasAura(AuraType.PeriodicHeal) : spell.HasEffect(SpellEffectName.Heal);
            if (!heals || cooldownSpells.Contains(use.SpellId) || use.Category != 0 && cooldownCategories.Contains(use.Category)
                || player.Inventory.CanUseItem(item) != InventoryResult.Ok) continue;
            return new RotationItem(item.Guid, item.BagSlot, item.Slot, template.Entry);
        }
        return null;
    }
}
