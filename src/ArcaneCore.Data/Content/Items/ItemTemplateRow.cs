using ArcaneCore.Kernel.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ArcaneCore.Data.Content.Items;

/// <summary>
/// One <c>item_template</c> row. Table and column names are vmangos' (world.sql item_template,
/// as read by ObjectMgr::LoadItemPrototypes) so dumps map by name. vmangos' <c>patch</c>
/// column (part of its key, for content progression) is not carried: ArcaneCore serves one
/// build (5875) and keeps one row per entry (docs/areas/items.md).
/// </summary>
public sealed class ItemTemplateRow
{
    public uint Entry { get; set; }
    public uint Class { get; set; }
    public uint Subclass { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public uint DisplayId { get; set; }
    public uint Quality { get; set; }
    public uint Flags { get; set; }
    public uint BuyCount { get; set; }
    public uint BuyPrice { get; set; }
    public uint SellPrice { get; set; }
    public uint InventoryType { get; set; }
    public int AllowableClass { get; set; }
    public int AllowableRace { get; set; }
    public uint ItemLevel { get; set; }
    public uint RequiredLevel { get; set; }
    public uint RequiredSkill { get; set; }
    public uint RequiredSkillRank { get; set; }
    public uint RequiredSpell { get; set; }
    public uint RequiredHonorRank { get; set; }
    public uint RequiredCityRank { get; set; }
    public uint RequiredReputationFaction { get; set; }
    public uint RequiredReputationRank { get; set; }
    public uint MaxCount { get; set; }
    public uint Stackable { get; set; }
    public uint ContainerSlots { get; set; }
    public uint StatType1 { get; set; }
    public int StatValue1 { get; set; }
    public uint StatType2 { get; set; }
    public int StatValue2 { get; set; }
    public uint StatType3 { get; set; }
    public int StatValue3 { get; set; }
    public uint StatType4 { get; set; }
    public int StatValue4 { get; set; }
    public uint StatType5 { get; set; }
    public int StatValue5 { get; set; }
    public uint StatType6 { get; set; }
    public int StatValue6 { get; set; }
    public uint StatType7 { get; set; }
    public int StatValue7 { get; set; }
    public uint StatType8 { get; set; }
    public int StatValue8 { get; set; }
    public uint StatType9 { get; set; }
    public int StatValue9 { get; set; }
    public uint StatType10 { get; set; }
    public int StatValue10 { get; set; }
    public uint Delay { get; set; }
    public float RangeMod { get; set; }
    public uint AmmoType { get; set; }
    public float DmgMin1 { get; set; }
    public float DmgMax1 { get; set; }
    public uint DmgType1 { get; set; }
    public float DmgMin2 { get; set; }
    public float DmgMax2 { get; set; }
    public uint DmgType2 { get; set; }
    public float DmgMin3 { get; set; }
    public float DmgMax3 { get; set; }
    public uint DmgType3 { get; set; }
    public float DmgMin4 { get; set; }
    public float DmgMax4 { get; set; }
    public uint DmgType4 { get; set; }
    public float DmgMin5 { get; set; }
    public float DmgMax5 { get; set; }
    public uint DmgType5 { get; set; }
    public uint Block { get; set; }
    public int Armor { get; set; }
    public int HolyRes { get; set; }
    public int FireRes { get; set; }
    public int NatureRes { get; set; }
    public int FrostRes { get; set; }
    public int ShadowRes { get; set; }
    public int ArcaneRes { get; set; }
    public uint SpellId1 { get; set; }
    public uint SpellTrigger1 { get; set; }
    public int SpellCharges1 { get; set; }
    public float SpellPpmRate1 { get; set; }
    public int SpellCooldown1 { get; set; }
    public uint SpellCategory1 { get; set; }
    public int SpellCategoryCooldown1 { get; set; }
    public uint SpellId2 { get; set; }
    public uint SpellTrigger2 { get; set; }
    public int SpellCharges2 { get; set; }
    public float SpellPpmRate2 { get; set; }
    public int SpellCooldown2 { get; set; }
    public uint SpellCategory2 { get; set; }
    public int SpellCategoryCooldown2 { get; set; }
    public uint SpellId3 { get; set; }
    public uint SpellTrigger3 { get; set; }
    public int SpellCharges3 { get; set; }
    public float SpellPpmRate3 { get; set; }
    public int SpellCooldown3 { get; set; }
    public uint SpellCategory3 { get; set; }
    public int SpellCategoryCooldown3 { get; set; }
    public uint SpellId4 { get; set; }
    public uint SpellTrigger4 { get; set; }
    public int SpellCharges4 { get; set; }
    public float SpellPpmRate4 { get; set; }
    public int SpellCooldown4 { get; set; }
    public uint SpellCategory4 { get; set; }
    public int SpellCategoryCooldown4 { get; set; }
    public uint SpellId5 { get; set; }
    public uint SpellTrigger5 { get; set; }
    public int SpellCharges5 { get; set; }
    public float SpellPpmRate5 { get; set; }
    public int SpellCooldown5 { get; set; }
    public uint SpellCategory5 { get; set; }
    public int SpellCategoryCooldown5 { get; set; }
    public uint Bonding { get; set; }
    public uint PageText { get; set; }
    public uint PageLanguage { get; set; }
    public uint PageMaterial { get; set; }
    public uint StartQuest { get; set; }
    public uint LockId { get; set; }
    public uint Material { get; set; }
    public uint Sheath { get; set; }
    public uint RandomProperty { get; set; }
    public uint SetId { get; set; }
    public uint MaxDurability { get; set; }
    public uint AreaBound { get; set; }
    public uint MapBound { get; set; }
    public uint Duration { get; set; }
    public uint BagFamily { get; set; }
    public uint DisenchantId { get; set; }
    public uint FoodType { get; set; }
    public uint MinMoneyLoot { get; set; }
    public uint MaxMoneyLoot { get; set; }
    public uint WrappedGift { get; set; }
    public uint ExtraFlags { get; set; }
    public uint OtherTeamEntry { get; set; }

    /// <summary>The immutable domain template (load-time fixes applied).</summary>
    public ItemTemplate ToTemplate()
    {
        ItemTemplateRow r = this;
        return new ItemTemplate
        {
            Entry = r.Entry,
            Class = r.Class,
            SubClass = r.Subclass,
            Name = r.Name,
            Description = r.Description,
            DisplayId = r.DisplayId,
            Quality = r.Quality,
            Flags = r.Flags,
            BuyCount = r.BuyCount,
            BuyPrice = r.BuyPrice,
            SellPrice = r.SellPrice,
            InventoryType = r.InventoryType,
            AllowableClass = unchecked((uint)r.AllowableClass),
            AllowableRace = unchecked((uint)r.AllowableRace),
            ItemLevel = r.ItemLevel,
            RequiredLevel = r.RequiredLevel,
            RequiredSkill = r.RequiredSkill,
            RequiredSkillRank = r.RequiredSkillRank,
            RequiredSpell = r.RequiredSpell,
            RequiredHonorRank = r.RequiredHonorRank,
            RequiredCityRank = r.RequiredCityRank,
            RequiredReputationFaction = r.RequiredReputationFaction,
            RequiredReputationRank = r.RequiredReputationRank,
            MaxCount = r.MaxCount,
            Stackable = r.Stackable,
            ContainerSlots = r.ContainerSlots,
            Delay = r.Delay,
            RangedModRange = r.RangeMod,
            AmmoType = r.AmmoType,
            Block = r.Block,
            Armor = r.Armor,
            HolyRes = r.HolyRes,
            FireRes = r.FireRes,
            NatureRes = r.NatureRes,
            FrostRes = r.FrostRes,
            ShadowRes = r.ShadowRes,
            ArcaneRes = r.ArcaneRes,
            Bonding = r.Bonding,
            PageText = r.PageText,
            PageLanguage = r.PageLanguage,
            PageMaterial = r.PageMaterial,
            StartQuest = r.StartQuest,
            LockId = r.LockId,
            Material = r.Material,
            Sheath = r.Sheath,
            RandomProperty = r.RandomProperty,
            SetId = r.SetId,
            MaxDurability = r.MaxDurability,
            AreaBound = r.AreaBound,
            MapBound = r.MapBound,
            Duration = r.Duration,
            BagFamily = r.BagFamily,
            DisenchantId = r.DisenchantId,
            FoodType = r.FoodType,
            MinMoneyLoot = r.MinMoneyLoot,
            MaxMoneyLoot = r.MaxMoneyLoot,
            WrappedGift = r.WrappedGift,
            ExtraFlags = r.ExtraFlags,
            OtherTeamEntry = r.OtherTeamEntry,
            Stats =
            [
                new(r.StatType1, r.StatValue1),
                new(r.StatType2, r.StatValue2),
                new(r.StatType3, r.StatValue3),
                new(r.StatType4, r.StatValue4),
                new(r.StatType5, r.StatValue5),
                new(r.StatType6, r.StatValue6),
                new(r.StatType7, r.StatValue7),
                new(r.StatType8, r.StatValue8),
                new(r.StatType9, r.StatValue9),
                new(r.StatType10, r.StatValue10),
            ],
            Damages =
            [
                new(r.DmgMin1, r.DmgMax1, r.DmgType1),
                new(r.DmgMin2, r.DmgMax2, r.DmgType2),
                new(r.DmgMin3, r.DmgMax3, r.DmgType3),
                new(r.DmgMin4, r.DmgMax4, r.DmgType4),
                new(r.DmgMin5, r.DmgMax5, r.DmgType5),
            ],
            Spells =
            [
                new(r.SpellId1, r.SpellTrigger1, r.SpellCharges1, r.SpellPpmRate1, r.SpellCooldown1, r.SpellCategory1, r.SpellCategoryCooldown1),
                new(r.SpellId2, r.SpellTrigger2, r.SpellCharges2, r.SpellPpmRate2, r.SpellCooldown2, r.SpellCategory2, r.SpellCategoryCooldown2),
                new(r.SpellId3, r.SpellTrigger3, r.SpellCharges3, r.SpellPpmRate3, r.SpellCooldown3, r.SpellCategory3, r.SpellCategoryCooldown3),
                new(r.SpellId4, r.SpellTrigger4, r.SpellCharges4, r.SpellPpmRate4, r.SpellCooldown4, r.SpellCategory4, r.SpellCategoryCooldown4),
                new(r.SpellId5, r.SpellTrigger5, r.SpellCharges5, r.SpellPpmRate5, r.SpellCooldown5, r.SpellCategory5, r.SpellCategoryCooldown5),
            ],
        }.Normalized();
    }

    /// <summary>A row holding <paramref name="t"/> (content tools and tests).</summary>
    public static ItemTemplateRow FromTemplate(ItemTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new ItemTemplateRow
        {
            Entry = t.Entry,
            Class = t.Class,
            Subclass = t.SubClass,
            Name = t.Name,
            Description = t.Description,
            DisplayId = t.DisplayId,
            Quality = t.Quality,
            Flags = t.Flags,
            BuyCount = t.BuyCount,
            BuyPrice = t.BuyPrice,
            SellPrice = t.SellPrice,
            InventoryType = t.InventoryType,
            AllowableClass = unchecked((int)t.AllowableClass),
            AllowableRace = unchecked((int)t.AllowableRace),
            ItemLevel = t.ItemLevel,
            RequiredLevel = t.RequiredLevel,
            RequiredSkill = t.RequiredSkill,
            RequiredSkillRank = t.RequiredSkillRank,
            RequiredSpell = t.RequiredSpell,
            RequiredHonorRank = t.RequiredHonorRank,
            RequiredCityRank = t.RequiredCityRank,
            RequiredReputationFaction = t.RequiredReputationFaction,
            RequiredReputationRank = t.RequiredReputationRank,
            MaxCount = t.MaxCount,
            Stackable = t.Stackable,
            ContainerSlots = t.ContainerSlots,
            Delay = t.Delay,
            RangeMod = t.RangedModRange,
            AmmoType = t.AmmoType,
            Block = t.Block,
            Armor = t.Armor,
            HolyRes = t.HolyRes,
            FireRes = t.FireRes,
            NatureRes = t.NatureRes,
            FrostRes = t.FrostRes,
            ShadowRes = t.ShadowRes,
            ArcaneRes = t.ArcaneRes,
            Bonding = t.Bonding,
            PageText = t.PageText,
            PageLanguage = t.PageLanguage,
            PageMaterial = t.PageMaterial,
            StartQuest = t.StartQuest,
            LockId = t.LockId,
            Material = t.Material,
            Sheath = t.Sheath,
            RandomProperty = t.RandomProperty,
            SetId = t.SetId,
            MaxDurability = t.MaxDurability,
            AreaBound = t.AreaBound,
            MapBound = t.MapBound,
            Duration = t.Duration,
            BagFamily = t.BagFamily,
            DisenchantId = t.DisenchantId,
            FoodType = t.FoodType,
            MinMoneyLoot = t.MinMoneyLoot,
            MaxMoneyLoot = t.MaxMoneyLoot,
            WrappedGift = t.WrappedGift,
            ExtraFlags = t.ExtraFlags,
            OtherTeamEntry = t.OtherTeamEntry,
            StatType1 = t.Stats.ElementAtOrDefault(0).Type,
            StatValue1 = t.Stats.ElementAtOrDefault(0).Value,
            StatType2 = t.Stats.ElementAtOrDefault(1).Type,
            StatValue2 = t.Stats.ElementAtOrDefault(1).Value,
            StatType3 = t.Stats.ElementAtOrDefault(2).Type,
            StatValue3 = t.Stats.ElementAtOrDefault(2).Value,
            StatType4 = t.Stats.ElementAtOrDefault(3).Type,
            StatValue4 = t.Stats.ElementAtOrDefault(3).Value,
            StatType5 = t.Stats.ElementAtOrDefault(4).Type,
            StatValue5 = t.Stats.ElementAtOrDefault(4).Value,
            StatType6 = t.Stats.ElementAtOrDefault(5).Type,
            StatValue6 = t.Stats.ElementAtOrDefault(5).Value,
            StatType7 = t.Stats.ElementAtOrDefault(6).Type,
            StatValue7 = t.Stats.ElementAtOrDefault(6).Value,
            StatType8 = t.Stats.ElementAtOrDefault(7).Type,
            StatValue8 = t.Stats.ElementAtOrDefault(7).Value,
            StatType9 = t.Stats.ElementAtOrDefault(8).Type,
            StatValue9 = t.Stats.ElementAtOrDefault(8).Value,
            StatType10 = t.Stats.ElementAtOrDefault(9).Type,
            StatValue10 = t.Stats.ElementAtOrDefault(9).Value,
            DmgMin1 = t.Damages.ElementAtOrDefault(0).Min,
            DmgMax1 = t.Damages.ElementAtOrDefault(0).Max,
            DmgType1 = t.Damages.ElementAtOrDefault(0).School,
            DmgMin2 = t.Damages.ElementAtOrDefault(1).Min,
            DmgMax2 = t.Damages.ElementAtOrDefault(1).Max,
            DmgType2 = t.Damages.ElementAtOrDefault(1).School,
            DmgMin3 = t.Damages.ElementAtOrDefault(2).Min,
            DmgMax3 = t.Damages.ElementAtOrDefault(2).Max,
            DmgType3 = t.Damages.ElementAtOrDefault(2).School,
            DmgMin4 = t.Damages.ElementAtOrDefault(3).Min,
            DmgMax4 = t.Damages.ElementAtOrDefault(3).Max,
            DmgType4 = t.Damages.ElementAtOrDefault(3).School,
            DmgMin5 = t.Damages.ElementAtOrDefault(4).Min,
            DmgMax5 = t.Damages.ElementAtOrDefault(4).Max,
            DmgType5 = t.Damages.ElementAtOrDefault(4).School,
            SpellId1 = t.Spells.ElementAtOrDefault(0).SpellId,
            SpellTrigger1 = t.Spells.ElementAtOrDefault(0).Trigger,
            SpellCharges1 = t.Spells.ElementAtOrDefault(0).Charges,
            SpellPpmRate1 = t.Spells.ElementAtOrDefault(0).PpmRate,
            SpellCooldown1 = t.Spells.ElementAtOrDefault(0).Cooldown,
            SpellCategory1 = t.Spells.ElementAtOrDefault(0).Category,
            SpellCategoryCooldown1 = t.Spells.ElementAtOrDefault(0).CategoryCooldown,
            SpellId2 = t.Spells.ElementAtOrDefault(1).SpellId,
            SpellTrigger2 = t.Spells.ElementAtOrDefault(1).Trigger,
            SpellCharges2 = t.Spells.ElementAtOrDefault(1).Charges,
            SpellPpmRate2 = t.Spells.ElementAtOrDefault(1).PpmRate,
            SpellCooldown2 = t.Spells.ElementAtOrDefault(1).Cooldown,
            SpellCategory2 = t.Spells.ElementAtOrDefault(1).Category,
            SpellCategoryCooldown2 = t.Spells.ElementAtOrDefault(1).CategoryCooldown,
            SpellId3 = t.Spells.ElementAtOrDefault(2).SpellId,
            SpellTrigger3 = t.Spells.ElementAtOrDefault(2).Trigger,
            SpellCharges3 = t.Spells.ElementAtOrDefault(2).Charges,
            SpellPpmRate3 = t.Spells.ElementAtOrDefault(2).PpmRate,
            SpellCooldown3 = t.Spells.ElementAtOrDefault(2).Cooldown,
            SpellCategory3 = t.Spells.ElementAtOrDefault(2).Category,
            SpellCategoryCooldown3 = t.Spells.ElementAtOrDefault(2).CategoryCooldown,
            SpellId4 = t.Spells.ElementAtOrDefault(3).SpellId,
            SpellTrigger4 = t.Spells.ElementAtOrDefault(3).Trigger,
            SpellCharges4 = t.Spells.ElementAtOrDefault(3).Charges,
            SpellPpmRate4 = t.Spells.ElementAtOrDefault(3).PpmRate,
            SpellCooldown4 = t.Spells.ElementAtOrDefault(3).Cooldown,
            SpellCategory4 = t.Spells.ElementAtOrDefault(3).Category,
            SpellCategoryCooldown4 = t.Spells.ElementAtOrDefault(3).CategoryCooldown,
            SpellId5 = t.Spells.ElementAtOrDefault(4).SpellId,
            SpellTrigger5 = t.Spells.ElementAtOrDefault(4).Trigger,
            SpellCharges5 = t.Spells.ElementAtOrDefault(4).Charges,
            SpellPpmRate5 = t.Spells.ElementAtOrDefault(4).PpmRate,
            SpellCooldown5 = t.Spells.ElementAtOrDefault(4).Cooldown,
            SpellCategory5 = t.Spells.ElementAtOrDefault(4).Category,
            SpellCategoryCooldown5 = t.Spells.ElementAtOrDefault(4).CategoryCooldown,
        };
    }

    internal static void Configure(EntityTypeBuilder<ItemTemplateRow> entity)
    {
        entity.ToTable("item_template");
        entity.HasKey(r => r.Entry);
        entity.Property(r => r.Entry).ValueGeneratedNever();
        entity.Property(r => r.Entry).HasColumnName("entry");
        entity.Property(r => r.Class).HasColumnName("class");
        entity.Property(r => r.Subclass).HasColumnName("subclass");
        entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        entity.Property(r => r.Description).HasColumnName("description").HasMaxLength(1024).IsRequired();
        entity.Property(r => r.DisplayId).HasColumnName("display_id");
        entity.Property(r => r.Quality).HasColumnName("quality");
        entity.Property(r => r.Flags).HasColumnName("flags");
        entity.Property(r => r.BuyCount).HasColumnName("buy_count");
        entity.Property(r => r.BuyPrice).HasColumnName("buy_price");
        entity.Property(r => r.SellPrice).HasColumnName("sell_price");
        entity.Property(r => r.InventoryType).HasColumnName("inventory_type");
        entity.Property(r => r.AllowableClass).HasColumnName("allowable_class");
        entity.Property(r => r.AllowableRace).HasColumnName("allowable_race");
        entity.Property(r => r.ItemLevel).HasColumnName("item_level");
        entity.Property(r => r.RequiredLevel).HasColumnName("required_level");
        entity.Property(r => r.RequiredSkill).HasColumnName("required_skill");
        entity.Property(r => r.RequiredSkillRank).HasColumnName("required_skill_rank");
        entity.Property(r => r.RequiredSpell).HasColumnName("required_spell");
        entity.Property(r => r.RequiredHonorRank).HasColumnName("required_honor_rank");
        entity.Property(r => r.RequiredCityRank).HasColumnName("required_city_rank");
        entity.Property(r => r.RequiredReputationFaction).HasColumnName("required_reputation_faction");
        entity.Property(r => r.RequiredReputationRank).HasColumnName("required_reputation_rank");
        entity.Property(r => r.MaxCount).HasColumnName("max_count");
        entity.Property(r => r.Stackable).HasColumnName("stackable");
        entity.Property(r => r.ContainerSlots).HasColumnName("container_slots");
        entity.Property(r => r.StatType1).HasColumnName("stat_type1");
        entity.Property(r => r.StatValue1).HasColumnName("stat_value1");
        entity.Property(r => r.StatType2).HasColumnName("stat_type2");
        entity.Property(r => r.StatValue2).HasColumnName("stat_value2");
        entity.Property(r => r.StatType3).HasColumnName("stat_type3");
        entity.Property(r => r.StatValue3).HasColumnName("stat_value3");
        entity.Property(r => r.StatType4).HasColumnName("stat_type4");
        entity.Property(r => r.StatValue4).HasColumnName("stat_value4");
        entity.Property(r => r.StatType5).HasColumnName("stat_type5");
        entity.Property(r => r.StatValue5).HasColumnName("stat_value5");
        entity.Property(r => r.StatType6).HasColumnName("stat_type6");
        entity.Property(r => r.StatValue6).HasColumnName("stat_value6");
        entity.Property(r => r.StatType7).HasColumnName("stat_type7");
        entity.Property(r => r.StatValue7).HasColumnName("stat_value7");
        entity.Property(r => r.StatType8).HasColumnName("stat_type8");
        entity.Property(r => r.StatValue8).HasColumnName("stat_value8");
        entity.Property(r => r.StatType9).HasColumnName("stat_type9");
        entity.Property(r => r.StatValue9).HasColumnName("stat_value9");
        entity.Property(r => r.StatType10).HasColumnName("stat_type10");
        entity.Property(r => r.StatValue10).HasColumnName("stat_value10");
        entity.Property(r => r.Delay).HasColumnName("delay");
        entity.Property(r => r.RangeMod).HasColumnName("range_mod");
        entity.Property(r => r.AmmoType).HasColumnName("ammo_type");
        entity.Property(r => r.DmgMin1).HasColumnName("dmg_min1");
        entity.Property(r => r.DmgMax1).HasColumnName("dmg_max1");
        entity.Property(r => r.DmgType1).HasColumnName("dmg_type1");
        entity.Property(r => r.DmgMin2).HasColumnName("dmg_min2");
        entity.Property(r => r.DmgMax2).HasColumnName("dmg_max2");
        entity.Property(r => r.DmgType2).HasColumnName("dmg_type2");
        entity.Property(r => r.DmgMin3).HasColumnName("dmg_min3");
        entity.Property(r => r.DmgMax3).HasColumnName("dmg_max3");
        entity.Property(r => r.DmgType3).HasColumnName("dmg_type3");
        entity.Property(r => r.DmgMin4).HasColumnName("dmg_min4");
        entity.Property(r => r.DmgMax4).HasColumnName("dmg_max4");
        entity.Property(r => r.DmgType4).HasColumnName("dmg_type4");
        entity.Property(r => r.DmgMin5).HasColumnName("dmg_min5");
        entity.Property(r => r.DmgMax5).HasColumnName("dmg_max5");
        entity.Property(r => r.DmgType5).HasColumnName("dmg_type5");
        entity.Property(r => r.Block).HasColumnName("block");
        entity.Property(r => r.Armor).HasColumnName("armor");
        entity.Property(r => r.HolyRes).HasColumnName("holy_res");
        entity.Property(r => r.FireRes).HasColumnName("fire_res");
        entity.Property(r => r.NatureRes).HasColumnName("nature_res");
        entity.Property(r => r.FrostRes).HasColumnName("frost_res");
        entity.Property(r => r.ShadowRes).HasColumnName("shadow_res");
        entity.Property(r => r.ArcaneRes).HasColumnName("arcane_res");
        entity.Property(r => r.SpellId1).HasColumnName("spellid_1");
        entity.Property(r => r.SpellTrigger1).HasColumnName("spelltrigger_1");
        entity.Property(r => r.SpellCharges1).HasColumnName("spellcharges_1");
        entity.Property(r => r.SpellPpmRate1).HasColumnName("spellppmrate_1");
        entity.Property(r => r.SpellCooldown1).HasColumnName("spellcooldown_1");
        entity.Property(r => r.SpellCategory1).HasColumnName("spellcategory_1");
        entity.Property(r => r.SpellCategoryCooldown1).HasColumnName("spellcategorycooldown_1");
        entity.Property(r => r.SpellId2).HasColumnName("spellid_2");
        entity.Property(r => r.SpellTrigger2).HasColumnName("spelltrigger_2");
        entity.Property(r => r.SpellCharges2).HasColumnName("spellcharges_2");
        entity.Property(r => r.SpellPpmRate2).HasColumnName("spellppmrate_2");
        entity.Property(r => r.SpellCooldown2).HasColumnName("spellcooldown_2");
        entity.Property(r => r.SpellCategory2).HasColumnName("spellcategory_2");
        entity.Property(r => r.SpellCategoryCooldown2).HasColumnName("spellcategorycooldown_2");
        entity.Property(r => r.SpellId3).HasColumnName("spellid_3");
        entity.Property(r => r.SpellTrigger3).HasColumnName("spelltrigger_3");
        entity.Property(r => r.SpellCharges3).HasColumnName("spellcharges_3");
        entity.Property(r => r.SpellPpmRate3).HasColumnName("spellppmrate_3");
        entity.Property(r => r.SpellCooldown3).HasColumnName("spellcooldown_3");
        entity.Property(r => r.SpellCategory3).HasColumnName("spellcategory_3");
        entity.Property(r => r.SpellCategoryCooldown3).HasColumnName("spellcategorycooldown_3");
        entity.Property(r => r.SpellId4).HasColumnName("spellid_4");
        entity.Property(r => r.SpellTrigger4).HasColumnName("spelltrigger_4");
        entity.Property(r => r.SpellCharges4).HasColumnName("spellcharges_4");
        entity.Property(r => r.SpellPpmRate4).HasColumnName("spellppmrate_4");
        entity.Property(r => r.SpellCooldown4).HasColumnName("spellcooldown_4");
        entity.Property(r => r.SpellCategory4).HasColumnName("spellcategory_4");
        entity.Property(r => r.SpellCategoryCooldown4).HasColumnName("spellcategorycooldown_4");
        entity.Property(r => r.SpellId5).HasColumnName("spellid_5");
        entity.Property(r => r.SpellTrigger5).HasColumnName("spelltrigger_5");
        entity.Property(r => r.SpellCharges5).HasColumnName("spellcharges_5");
        entity.Property(r => r.SpellPpmRate5).HasColumnName("spellppmrate_5");
        entity.Property(r => r.SpellCooldown5).HasColumnName("spellcooldown_5");
        entity.Property(r => r.SpellCategory5).HasColumnName("spellcategory_5");
        entity.Property(r => r.SpellCategoryCooldown5).HasColumnName("spellcategorycooldown_5");
        entity.Property(r => r.Bonding).HasColumnName("bonding");
        entity.Property(r => r.PageText).HasColumnName("page_text");
        entity.Property(r => r.PageLanguage).HasColumnName("page_language");
        entity.Property(r => r.PageMaterial).HasColumnName("page_material");
        entity.Property(r => r.StartQuest).HasColumnName("start_quest");
        entity.Property(r => r.LockId).HasColumnName("lock_id");
        entity.Property(r => r.Material).HasColumnName("material");
        entity.Property(r => r.Sheath).HasColumnName("sheath");
        entity.Property(r => r.RandomProperty).HasColumnName("random_property");
        entity.Property(r => r.SetId).HasColumnName("set_id");
        entity.Property(r => r.MaxDurability).HasColumnName("max_durability");
        entity.Property(r => r.AreaBound).HasColumnName("area_bound");
        entity.Property(r => r.MapBound).HasColumnName("map_bound");
        entity.Property(r => r.Duration).HasColumnName("duration");
        entity.Property(r => r.BagFamily).HasColumnName("bag_family");
        entity.Property(r => r.DisenchantId).HasColumnName("disenchant_id");
        entity.Property(r => r.FoodType).HasColumnName("food_type");
        entity.Property(r => r.MinMoneyLoot).HasColumnName("min_money_loot");
        entity.Property(r => r.MaxMoneyLoot).HasColumnName("max_money_loot");
        entity.Property(r => r.WrappedGift).HasColumnName("wrapped_gift");
        entity.Property(r => r.ExtraFlags).HasColumnName("extra_flags");
        entity.Property(r => r.OtherTeamEntry).HasColumnName("other_team_entry");
    }
}
