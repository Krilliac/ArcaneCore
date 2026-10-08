using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_ITEM_QUERY_SINGLE_RESPONSE: VanillaServerMessage, IWorldMessage {
    public required uint Item { get; set; }
    public struct OptionalFound {
        public required Vanilla.ItemClassAndSubClass ClassAndSubClass { get; set; }
        public required string Name1 { get; set; }
        public required string Name2 { get; set; }
        public required string Name3 { get; set; }
        public required string Name4 { get; set; }
        /// <summary>
        /// id from ItemDisplayInfo.dbc
        /// </summary>
        public required uint DisplayId { get; set; }
        public required Vanilla.ItemQuality Quality { get; set; }
        public required Vanilla.ItemFlag Flags { get; set; }
        public required uint BuyPrice { get; set; }
        public required uint SellPrice { get; set; }
        public required Vanilla.InventoryType InventoryType { get; set; }
        public required Vanilla.AllowedClass AllowedClass { get; set; }
        public required Vanilla.AllowedRace AllowedRace { get; set; }
        public required uint ItemLevel { get; set; }
        public required uint RequiredLevel { get; set; }
        public required Vanilla.Skill RequiredSkill { get; set; }
        public required uint RequiredSkillRank { get; set; }
        public required uint RequiredSpell { get; set; }
        public required uint RequiredHonorRank { get; set; }
        public required uint RequiredCityRank { get; set; }
        public required Vanilla.Faction RequiredFaction { get; set; }
        /// <summary>
        /// cmangos/vmangos/mangoszero: send value only if reputation faction id setted ( needed for some items)
        /// </summary>
        public required uint RequiredFactionRank { get; set; }
        public required uint MaxCount { get; set; }
        public required uint Stackable { get; set; }
        public required uint ContainerSlots { get; set; }
        public const int StatsLength = 10;
        public required ItemStat[] Stats { get; set; }
        public const int DamagesLength = 5;
        public required ItemDamageType[] Damages { get; set; }
        public required int Armor { get; set; }
        public required int HolyResistance { get; set; }
        public required int FireResistance { get; set; }
        public required int NatureResistance { get; set; }
        public required int FrostResistance { get; set; }
        public required int ShadowResistance { get; set; }
        public required int ArcaneResistance { get; set; }
        public required uint Delay { get; set; }
        public required uint AmmoType { get; set; }
        public required float RangedRangeModification { get; set; }
        public const int SpellsLength = 5;
        public required ItemSpells[] Spells { get; set; }
        public required Vanilla.Bonding Bonding { get; set; }
        public required string Description { get; set; }
        public required uint PageText { get; set; }
        public required Vanilla.Language Language { get; set; }
        public required Vanilla.PageTextMaterial PageTextMaterial { get; set; }
        /// <summary>
        /// cmangos/vmangos/mangoszero: id from QuestCache.wdb
        /// </summary>
        public required uint StartQuest { get; set; }
        public required uint LockId { get; set; }
        /// <summary>
        /// cmangos/vmangos/mangoszero: id from Material.dbc
        /// </summary>
        public required uint Material { get; set; }
        public required Vanilla.SheatheType SheatheType { get; set; }
        /// <summary>
        /// cmangos/vmangos/mangoszero: id from ItemRandomProperties.dbc
        /// </summary>
        public required uint RandomProperty { get; set; }
        public required uint Block { get; set; }
        public required Vanilla.ItemSet ItemSet { get; set; }
        public required uint MaxDurability { get; set; }
        public required Vanilla.Area Area { get; set; }
        public required Vanilla.Map Map { get; set; }
        public required Vanilla.BagFamily BagFamily { get; set; }
    }
    public required OptionalFound? Found { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        if (Found is { } found) {
            await w.WriteULong((ulong)found.ClassAndSubClass, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name1, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name2, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name3, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name4, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.DisplayId, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.Quality, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.Flags, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.BuyPrice, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.SellPrice, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.InventoryType, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.AllowedClass, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.AllowedRace, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.ItemLevel, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.RequiredLevel, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.RequiredSkill, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.RequiredSkillRank, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.RequiredSpell, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.RequiredHonorRank, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.RequiredCityRank, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.RequiredFaction, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.RequiredFactionRank, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.MaxCount, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.Stackable, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.ContainerSlots, cancellationToken).ConfigureAwait(false);

            foreach (var v in found.Stats) {
                await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
            }

            foreach (var v in found.Damages) {
                await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
            }

            await w.WriteInt(found.Armor, cancellationToken).ConfigureAwait(false);

            await w.WriteInt(found.HolyResistance, cancellationToken).ConfigureAwait(false);

            await w.WriteInt(found.FireResistance, cancellationToken).ConfigureAwait(false);

            await w.WriteInt(found.NatureResistance, cancellationToken).ConfigureAwait(false);

            await w.WriteInt(found.FrostResistance, cancellationToken).ConfigureAwait(false);

            await w.WriteInt(found.ShadowResistance, cancellationToken).ConfigureAwait(false);

            await w.WriteInt(found.ArcaneResistance, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.Delay, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.AmmoType, cancellationToken).ConfigureAwait(false);

            await w.WriteFloat(found.RangedRangeModification, cancellationToken).ConfigureAwait(false);

            foreach (var v in found.Spells) {
                await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
            }

            await w.WriteUInt((uint)found.Bonding, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Description, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.PageText, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.Language, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.PageTextMaterial, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.StartQuest, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.LockId, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.Material, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.SheatheType, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.RandomProperty, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.Block, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.ItemSet, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.MaxDurability, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.Area, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.Map, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.BagFamily, cancellationToken).ConfigureAwait(false);

        }
    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 88, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 88, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_ITEM_QUERY_SINGLE_RESPONSE> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        OptionalFound? optionalFound = null;
        if (__size < bodySize) {
            var classAndSubClass = (Vanilla.ItemClassAndSubClass)await r.ReadULong(cancellationToken).ConfigureAwait(false);
            __size += 8;

            var name1 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name1.Length + 1;

            var name2 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name2.Length + 1;

            var name3 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name3.Length + 1;

            var name4 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name4.Length + 1;

            var displayId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var quality = (Vanilla.ItemQuality)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var flags = (ItemFlag)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var buyPrice = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var sellPrice = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var inventoryType = (Vanilla.InventoryType)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var allowedClass = (AllowedClass)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var allowedRace = (AllowedRace)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var itemLevel = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var requiredLevel = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var requiredSkill = (Vanilla.Skill)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var requiredSkillRank = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var requiredSpell = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var requiredHonorRank = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var requiredCityRank = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var requiredFaction = (Vanilla.Faction)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var requiredFactionRank = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var maxCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var stackable = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var containerSlots = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var stats = new ItemStat[OptionalFound.StatsLength];
            for (var i = 0; i < OptionalFound.StatsLength; ++i) {
                stats[i] = await Vanilla.ItemStat.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);
                __size += 8;
            }

            var damages = new ItemDamageType[OptionalFound.DamagesLength];
            for (var i = 0; i < OptionalFound.DamagesLength; ++i) {
                damages[i] = await Vanilla.ItemDamageType.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);
                __size += 12;
            }

            var armor = await r.ReadInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var holyResistance = await r.ReadInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var fireResistance = await r.ReadInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var natureResistance = await r.ReadInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var frostResistance = await r.ReadInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var shadowResistance = await r.ReadInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var arcaneResistance = await r.ReadInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var delay = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var ammoType = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var rangedRangeModification = await r.ReadFloat(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var spells = new ItemSpells[OptionalFound.SpellsLength];
            for (var i = 0; i < OptionalFound.SpellsLength; ++i) {
                spells[i] = await Vanilla.ItemSpells.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);
                __size += 24;
            }

            var bonding = (Vanilla.Bonding)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var description = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += description.Length + 1;

            var pageText = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var language = (Vanilla.Language)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var pageTextMaterial = (Vanilla.PageTextMaterial)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var startQuest = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var lockId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var material = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var sheatheType = (Vanilla.SheatheType)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var randomProperty = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var block = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var itemSet = (Vanilla.ItemSet)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var maxDurability = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var map = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var bagFamily = (Vanilla.BagFamily)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            optionalFound = new OptionalFound {
                ClassAndSubClass = classAndSubClass,
                Name1 = name1,
                Name2 = name2,
                Name3 = name3,
                Name4 = name4,
                DisplayId = displayId,
                Quality = quality,
                Flags = flags,
                BuyPrice = buyPrice,
                SellPrice = sellPrice,
                InventoryType = inventoryType,
                AllowedClass = allowedClass,
                AllowedRace = allowedRace,
                ItemLevel = itemLevel,
                RequiredLevel = requiredLevel,
                RequiredSkill = requiredSkill,
                RequiredSkillRank = requiredSkillRank,
                RequiredSpell = requiredSpell,
                RequiredHonorRank = requiredHonorRank,
                RequiredCityRank = requiredCityRank,
                RequiredFaction = requiredFaction,
                RequiredFactionRank = requiredFactionRank,
                MaxCount = maxCount,
                Stackable = stackable,
                ContainerSlots = containerSlots,
                Stats = stats,
                Damages = damages,
                Armor = armor,
                HolyResistance = holyResistance,
                FireResistance = fireResistance,
                NatureResistance = natureResistance,
                FrostResistance = frostResistance,
                ShadowResistance = shadowResistance,
                ArcaneResistance = arcaneResistance,
                Delay = delay,
                AmmoType = ammoType,
                RangedRangeModification = rangedRangeModification,
                Spells = spells,
                Bonding = bonding,
                Description = description,
                PageText = pageText,
                Language = language,
                PageTextMaterial = pageTextMaterial,
                StartQuest = startQuest,
                LockId = lockId,
                Material = material,
                SheatheType = sheatheType,
                RandomProperty = randomProperty,
                Block = block,
                ItemSet = itemSet,
                MaxDurability = maxDurability,
                Area = area,
                Map = map,
                BagFamily = bagFamily,
            };
        }

        return new SMSG_ITEM_QUERY_SINGLE_RESPONSE {
            Item = item,
            Found = optionalFound,
        };
    }

    internal int Size() {
        var size = 0;

        // item: Generator.Generated.DataTypeItem
        size += 4;

        if (Found is { } found) {
            // class_and_sub_class: Generator.Generated.DataTypeEnum
            size += 8;

            // name1: Generator.Generated.DataTypeCstring
            size += found.Name1.Length + 1;

            // name2: Generator.Generated.DataTypeCstring
            size += found.Name2.Length + 1;

            // name3: Generator.Generated.DataTypeCstring
            size += found.Name3.Length + 1;

            // name4: Generator.Generated.DataTypeCstring
            size += found.Name4.Length + 1;

            // display_id: Generator.Generated.DataTypeInteger
            size += 4;

            // quality: Generator.Generated.DataTypeEnum
            size += 4;

            // flags: Generator.Generated.DataTypeFlag
            size += 4;

            // buy_price: Generator.Generated.DataTypeGold
            size += 4;

            // sell_price: Generator.Generated.DataTypeGold
            size += 4;

            // inventory_type: Generator.Generated.DataTypeEnum
            size += 4;

            // allowed_class: Generator.Generated.DataTypeFlag
            size += 4;

            // allowed_race: Generator.Generated.DataTypeFlag
            size += 4;

            // item_level: Generator.Generated.DataTypeLevel32
            size += 4;

            // required_level: Generator.Generated.DataTypeLevel32
            size += 4;

            // required_skill: Generator.Generated.DataTypeEnum
            size += 4;

            // required_skill_rank: Generator.Generated.DataTypeInteger
            size += 4;

            // required_spell: Generator.Generated.DataTypeSpell
            size += 4;

            // required_honor_rank: Generator.Generated.DataTypeInteger
            size += 4;

            // required_city_rank: Generator.Generated.DataTypeInteger
            size += 4;

            // required_faction: Generator.Generated.DataTypeEnum
            size += 4;

            // required_faction_rank: Generator.Generated.DataTypeInteger
            size += 4;

            // max_count: Generator.Generated.DataTypeInteger
            size += 4;

            // stackable: Generator.Generated.DataTypeInteger
            size += 4;

            // container_slots: Generator.Generated.DataTypeInteger
            size += 4;

            // stats: Generator.Generated.DataTypeArray
            size += found.Stats.Sum(e => 8);

            // damages: Generator.Generated.DataTypeArray
            size += found.Damages.Sum(e => 12);

            // armor: Generator.Generated.DataTypeInteger
            size += 4;

            // holy_resistance: Generator.Generated.DataTypeInteger
            size += 4;

            // fire_resistance: Generator.Generated.DataTypeInteger
            size += 4;

            // nature_resistance: Generator.Generated.DataTypeInteger
            size += 4;

            // frost_resistance: Generator.Generated.DataTypeInteger
            size += 4;

            // shadow_resistance: Generator.Generated.DataTypeInteger
            size += 4;

            // arcane_resistance: Generator.Generated.DataTypeInteger
            size += 4;

            // delay: Generator.Generated.DataTypeInteger
            size += 4;

            // ammo_type: Generator.Generated.DataTypeInteger
            size += 4;

            // ranged_range_modification: Generator.Generated.DataTypeFloatingPoint
            size += 4;

            // spells: Generator.Generated.DataTypeArray
            size += found.Spells.Sum(e => 24);

            // bonding: Generator.Generated.DataTypeEnum
            size += 4;

            // description: Generator.Generated.DataTypeCstring
            size += found.Description.Length + 1;

            // page_text: Generator.Generated.DataTypeInteger
            size += 4;

            // language: Generator.Generated.DataTypeEnum
            size += 4;

            // page_text_material: Generator.Generated.DataTypeEnum
            size += 4;

            // start_quest: Generator.Generated.DataTypeInteger
            size += 4;

            // lock_id: Generator.Generated.DataTypeInteger
            size += 4;

            // material: Generator.Generated.DataTypeInteger
            size += 4;

            // sheathe_type: Generator.Generated.DataTypeEnum
            size += 4;

            // random_property: Generator.Generated.DataTypeInteger
            size += 4;

            // block: Generator.Generated.DataTypeInteger
            size += 4;

            // item_set: Generator.Generated.DataTypeEnum
            size += 4;

            // max_durability: Generator.Generated.DataTypeInteger
            size += 4;

            // area: Generator.Generated.DataTypeEnum
            size += 4;

            // map: Generator.Generated.DataTypeEnum
            size += 4;

            // bag_family: Generator.Generated.DataTypeEnum
            size += 4;

        }
        return size;
    }

}

