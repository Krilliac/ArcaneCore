using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Combat.RotationTestKit;
using Form = ArcaneCore.Game.Spells.ShapeshiftForm;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// Each class rotation (vmangos PartyBotAI::UpdateInCombatAI_&lt;Class&gt; / UpdateOutOfCombatAI_&lt;Class&gt;) as a pure function: a
/// synthetic spellbook and one fight state give the expected next cast, item or pet command.
/// </summary>
public sealed class PlayerbotClassRotationTests
{
    private static readonly PlayerbotWarriorRotation Warrior = new();
    private static readonly PlayerbotRogueRotation Rogue = new();
    private static readonly PlayerbotHunterRotation Hunter = new();
    private static readonly PlayerbotMageRotation Mage = new();
    private static readonly PlayerbotPriestRotation Priest = new();
    private static readonly PlayerbotWarlockRotation Warlock = new();
    private static readonly PlayerbotPaladinRotation Paladin = new();
    private static readonly PlayerbotShamanRotation Shaman = new();
    private static readonly PlayerbotDruidRotation Druid = new();

    // --- warrior ---------------------------------------------------------------------------------------------------------

    private static SpellInfo[] WarriorBook =>
    [
        Aura("Battle Stance", 0, SpellImplicitTarget.UnitCaster), Aura("Defensive Stance", 0, SpellImplicitTarget.UnitCaster),
        Aura("Battle Shout", 1, SpellImplicitTarget.UnitCaster), Hostile("Charge"), Hostile("Execute"), Hostile("Pummel"),
        Hostile("Shield Bash"), Debuff("Rend"), Hostile("Heroic Strike"), Hostile("Taunt"),
    ];

    [Fact]
    public void Warrior_TakesBattleStance_ThenShouts_BetweenFights()
    {
        Assert.Equal("Battle Stance", Next(Warrior.OutOfCombat(State(Warrior, WarriorBook, Self(1), null, PlayerbotRole.MeleeDps, inCombat: false))));
        Assert.Equal("Battle Shout", Next(Warrior.OutOfCombat(State(Warrior, WarriorBook, Self(1, 100, "Battle Stance"), null,
            PlayerbotRole.MeleeDps, inCombat: false, more: s => s.Form = Form.BattleStance))));
        Assert.Null(Next(Warrior.OutOfCombat(State(Warrior, WarriorBook, Self(1, 100, "Battle Stance", "Battle Shout"), null,
            PlayerbotRole.MeleeDps, inCombat: false, more: s => s.Form = Form.BattleStance))));
    }

    [Fact]
    public void Warrior_ChargesToPull_InterruptsACaster_ExecutesBelowTwentyPercent_AndDumpsRageOnHeroicStrike()
    {
        Assert.Equal("Charge", Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1), Victim(15f), PlayerbotRole.MeleeDps, inCombat: false))));
        Assert.Equal("Pummel", Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1), Victim(3f, casting: true), PlayerbotRole.MeleeDps))));
        Assert.Equal("Shield Bash", Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1), Victim(3f, casting: true), PlayerbotRole.MeleeDps,
            blocked: ["Pummel"], more: s => s.WearsShield = true))));
        Assert.Equal("Execute", Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1), Victim(3f, healthPercent: 15f), PlayerbotRole.MeleeDps))));
        Assert.Equal("Rend", Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1), Victim(3f), PlayerbotRole.MeleeDps, power: 200))));
        Assert.Equal("Heroic Strike", Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1), Victim(3f, auras: "Rend"),
            PlayerbotRole.MeleeDps, power: 400))));
        Assert.Null(Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1), Victim(3f, auras: "Rend"), PlayerbotRole.MeleeDps, power: 200))));
    }

    [Fact]
    public void WarriorTank_TauntsAVictimThatTurnedOnAGroupMate()
    {
        RotationState state = State(Warrior, WarriorBook, Self(1), Victim(3f, targetsBot: false), PlayerbotRole.Tank,
            more: s => s.Party.Add(Member(7, 8, 60f)));
        Assert.Equal("Taunt", Next(Warrior.InCombat(state)));
        // Alone there is nobody to take it from.
        Assert.NotEqual("Taunt", Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1), Victim(3f, targetsBot: false), PlayerbotRole.Tank))));
    }

    // --- rogue -----------------------------------------------------------------------------------------------------------

    private static SpellInfo[] RogueBook =>
    [
        Aura("Stealth", 1, SpellImplicitTarget.UnitCaster), Hostile("Sinister Strike"), Hostile("Eviscerate"), Aura("Slice and Dice", 1, SpellImplicitTarget.UnitCaster),
        Hostile("Kick"), Hostile("Cheap Shot"), Hostile("Garrote"),
    ];

    [Fact]
    public void Rogue_StealthsBeforeAPull_OpensFromStealth_AndFinishesAtFiveComboPoints()
    {
        Assert.Equal("Stealth", Next(Rogue.OutOfCombat(State(Rogue, RogueBook, Self(4), Victim(25f), PlayerbotRole.MeleeDps, inCombat: false))));
        Assert.Null(Next(Rogue.OutOfCombat(State(Rogue, RogueBook, Self(4), null, PlayerbotRole.MeleeDps, inCombat: false))));
        RotationUnit beast = Victim(3f) with { PowerType = PowerType.Rage };
        Assert.Equal("Cheap Shot", Next(Rogue.InCombat(State(Rogue, RogueBook, Self(4), beast, PlayerbotRole.MeleeDps, inCombat: false,
            more: s => s.IsStealthed = true))));
        Assert.Equal("Garrote", Next(Rogue.InCombat(State(Rogue, RogueBook, Self(4), Victim(3f), PlayerbotRole.MeleeDps, inCombat: false,
            more: s => s.IsStealthed = true))));
        Assert.Equal("Sinister Strike", Next(Rogue.InCombat(State(Rogue, RogueBook, Self(4), Victim(3f), PlayerbotRole.MeleeDps,
            more: s => s.ComboPoints = 4))));
        Assert.Equal("Slice and Dice", Next(Rogue.InCombat(State(Rogue, RogueBook, Self(4), Victim(3f), PlayerbotRole.MeleeDps,
            more: s => s.ComboPoints = 5))));
        Assert.Equal("Eviscerate", Next(Rogue.InCombat(State(Rogue, RogueBook, Self(4, 100, "Slice and Dice"), Victim(3f), PlayerbotRole.MeleeDps,
            more: s => s.ComboPoints = 5))));
        // A nearly dead victim is finished from three points.
        Assert.Equal("Eviscerate", Next(Rogue.InCombat(State(Rogue, RogueBook, Self(4), Victim(3f, healthPercent: 20f), PlayerbotRole.MeleeDps,
            more: s => s.ComboPoints = 3))));
        Assert.Equal("Kick", Next(Rogue.InCombat(State(Rogue, RogueBook, Self(4), Victim(3f, casting: true), PlayerbotRole.MeleeDps))));
    }

    // --- hunter ----------------------------------------------------------------------------------------------------------

    private static SpellInfo[] HunterBook =>
    [
        Hostile("Auto Shot", 0), Hostile("Arcane Shot"), Debuff("Serpent Sting"), Hostile("Raptor Strike"), Debuff("Wing Clip"),
        Aura("Aspect of the Hawk", 1, SpellImplicitTarget.UnitCaster), SelfSpell("Call Pet"), Hostile("Revive Pet") with { AttributesEx2 = SpellAttributesEx2.AllowDeadTarget }, Debuff("Hunter's Mark"),
    ];

    [Fact]
    public void Hunter_ShootsBeyondItsDeadZone_AndStrikesInMelee()
    {
        RotationState far = State(Hunter, HunterBook, Self(3, 100, "Aspect of the Hawk"), Victim(20f, auras: "Hunter's Mark"), PlayerbotRole.RangeDps,
            more: s => s.HasRangedWeapon = true);
        Assert.Equal("Auto Shot", Next(Hunter.InCombat(far)));
        Assert.Equal(30f, Hunter.PreferredRange(far));
        Assert.Equal("Arcane Shot", Next(Hunter.InCombat(State(Hunter, HunterBook, Self(3, 100, "Aspect of the Hawk"),
            Victim(20f, auras: "Hunter's Mark"), PlayerbotRole.RangeDps, more: s => { s.HasRangedWeapon = true; s.AutoRepeatActive = true; }))));
        RotationState close = State(Hunter, HunterBook, Self(3, 100, "Aspect of the Hawk"), Victim(3f, auras: "Hunter's Mark"), PlayerbotRole.RangeDps,
            more: s => s.HasRangedWeapon = true);
        Assert.Equal("Wing Clip", Next(Hunter.InCombat(close)));
        Assert.Equal("Raptor Strike", Next(Hunter.InCombat(State(Hunter, HunterBook, Self(3, 100, "Aspect of the Hawk"),
            Victim(3f, auras: ["Hunter's Mark", "Wing Clip"]), PlayerbotRole.RangeDps, more: s => s.HasRangedWeapon = true))));
        // No bow: a melee hunter.
        Assert.Equal(PlayerbotClassRotation.MeleeRange, Hunter.PreferredRange(State(Hunter, HunterBook, Self(3), Victim(), PlayerbotRole.RangeDps)));
    }

    [Fact]
    public void Hunter_RevivesOrCallsItsPet_AndSendsItAtTheTarget()
    {
        var pet = new RotationUnit { Guid = new ObjectGuid(0xF140000000000099UL), Distance = 2f, Health = 0, MaxHealth = 100, IsAlive = false };
        Assert.Equal("Revive Pet", Next(Hunter.OutOfCombat(State(Hunter, HunterBook, Self(3, 100, "Aspect of the Hawk"), null, PlayerbotRole.RangeDps,
            inCombat: false, more: s => { s.Pet = RotationPetStatus.Dead; s.PetUnit = pet; }))));
        Assert.Equal("Call Pet", Next(Hunter.OutOfCombat(State(Hunter, HunterBook, Self(3, 100, "Aspect of the Hawk"), null, PlayerbotRole.RangeDps,
            inCombat: false, more: s => s.CanCallPet = true))));
        Assert.Null(Next(Hunter.OutOfCombat(State(Hunter, HunterBook, Self(3, 100, "Aspect of the Hawk"), null, PlayerbotRole.RangeDps, inCombat: false))));
        Assert.Equal("pet attack", Next(Hunter.InCombat(State(Hunter, HunterBook, Self(3, 100, "Aspect of the Hawk"), Victim(20f), PlayerbotRole.RangeDps,
            more: s => { s.Pet = RotationPetStatus.Alive; s.PetUnit = pet with { IsAlive = true, Health = 100 }; s.HasRangedWeapon = true; }))));
        Assert.Equal("pet follow", Next(Hunter.OutOfCombat(State(Hunter, HunterBook, Self(3, 100, "Aspect of the Hawk"), null, PlayerbotRole.RangeDps,
            inCombat: false, more: s => { s.Pet = RotationPetStatus.Alive; s.PetUnit = pet with { IsAlive = true, Health = 100 }; s.PetFighting = true; }))));
    }

    // --- mage ------------------------------------------------------------------------------------------------------------

    private static SpellInfo[] MageBook =>
    [
        Hostile("Fireball"), Hostile("Frostbolt"), Hostile("Counterspell"), Aura("Arcane Intellect"), Aura("Arcane Brilliance"),
        Aura("Frost Armor", 1, SpellImplicitTarget.UnitCaster), SelfSpell("Ice Block"), Hostile("Shoot", 0),
    ];

    [Fact]
    public void Mage_BuffsItselfThenItsGroup_AndCastsFrostboltBeforeFireball()
    {
        Assert.Equal("Arcane Intellect", Next(Mage.OutOfCombat(State(Mage, MageBook, Self(8), null, PlayerbotRole.RangeDps, inCombat: false))));
        RotationState group = State(Mage, MageBook, Self(8), null, PlayerbotRole.RangeDps, inCombat: false,
            more: s => s.Party.Add(Member(7, 1)));
        RotationAction brilliance = Mage.OutOfCombat(group)!.Value;
        Assert.Equal("Arcane Brilliance", brilliance.Spell!.Name);
        Assert.Equal(BotGuid, brilliance.Target);
        Assert.Equal("Frost Armor", Next(Mage.OutOfCombat(State(Mage, MageBook, Self(8, 100, "Arcane Intellect"), null, PlayerbotRole.RangeDps, inCombat: false))));
        Assert.Equal("Frostbolt", Next(Mage.InCombat(State(Mage, MageBook, Self(8), Victim(25f), PlayerbotRole.RangeDps))));
        Assert.Equal("Fireball", Next(Mage.InCombat(State(Mage, MageBook, Self(8), Victim(25f), PlayerbotRole.RangeDps, blocked: ["Frostbolt"]))));
        Assert.Equal("Counterspell", Next(Mage.InCombat(State(Mage, MageBook, Self(8), Victim(25f, casting: true), PlayerbotRole.RangeDps))));
        Assert.Equal("Ice Block", Next(Mage.InCombat(State(Mage, MageBook, Self(8, 5f), Victim(25f), PlayerbotRole.RangeDps))));
        Assert.Equal(PlayerbotClassRotation.CasterRange, Mage.PreferredRange(State(Mage, MageBook, Self(8), null, PlayerbotRole.RangeDps)));
    }

    [Fact]
    public void AnOutOfManaCaster_Wands()
    {
        RotationState empty = State(Mage, MageBook, Self(8), Victim(25f), PlayerbotRole.RangeDps, power: 3, maxPower: 100,
            blocked: ["Fireball", "Frostbolt"], more: s => s.HasWand = true);
        Assert.Equal("Shoot", Next(Mage.InCombat(empty)));
        Assert.Null(Next(Mage.InCombat(State(Mage, MageBook, Self(8), Victim(25f), PlayerbotRole.RangeDps, power: 3, maxPower: 100,
            blocked: ["Fireball", "Frostbolt"]))));
    }

    // --- priest ----------------------------------------------------------------------------------------------------------

    private static SpellInfo[] PriestBook =>
    [
        Hostile("Smite"), Heal("Lesser Heal", 50), Heal("Heal", 300), Renew("Renew", 10), Aura("Power Word: Fortitude"),
        Dispel("Dispel Magic", 1),
    ];

    [Fact]
    public void PriestHealer_HealsItselfBelowSixtyPercent_TheMostInjuredMember_AndFightsWhenNobodyIsHurt()
    {
        RotationAction self = Priest.InCombat(State(Priest, PriestBook, Self(5, 40f), Victim(25f), PlayerbotRole.Healer))!.Value;
        Assert.Equal(BotGuid, self.Target);
        Assert.Equal("Lesser Heal", self.Spell!.Name); // 60 missing: Lesser Heal (50) fits better than Heal (300)
        RotationAction member = Priest.InCombat(State(Priest, PriestBook, Self(5), Victim(25f), PlayerbotRole.Healer,
            more: s => { s.Party.Add(Member(7, 1, 70f)); s.Party.Add(Member(8, 4, 30f)); }))!.Value;
        Assert.Equal(new ObjectGuid(8), member.Target);
        Assert.Equal("Smite", Next(Priest.InCombat(State(Priest, PriestBook, Self(5), Victim(25f), PlayerbotRole.Healer))));
        Assert.Equal("Renew", Next(Priest.InCombat(State(Priest, PriestBook, Self(5), Victim(25f), PlayerbotRole.Healer,
            more: s => s.Party.Add(Member(7, 1, 85f))))));
    }

    [Fact]
    public void Priest_BuffsAndDispelsItsGroup()
    {
        Assert.Equal("Power Word: Fortitude", Next(Priest.OutOfCombat(State(Priest, PriestBook, Self(5), null, PlayerbotRole.Healer, inCombat: false))));
        RotationAction dispel = Priest.OutOfCombat(State(Priest, PriestBook, Self(5, 100, "Power Word: Fortitude"), null, PlayerbotRole.Healer,
            inCombat: false, more: s => s.Party.Add(Member(9, 1, 100f, harmfulDispel: 1u << 1, auras: "Power Word: Fortitude"))))!.Value;
        Assert.Equal("Dispel Magic", dispel.Spell!.Name);
        Assert.Equal(new ObjectGuid(9), dispel.Target);
        // A poison is not magic.
        Assert.Null(Next(Priest.OutOfCombat(State(Priest, PriestBook, Self(5, 100, "Power Word: Fortitude"), null, PlayerbotRole.Healer,
            inCombat: false, more: s => s.Party.Add(Member(9, 1, 100f, harmfulDispel: 1u << 4, auras: "Power Word: Fortitude"))))));
    }

    // --- warlock ---------------------------------------------------------------------------------------------------------

    private static SpellInfo[] WarlockBook(bool voidwalker) =>
    [
        Hostile("Shadow Bolt"), Debuff("Immolate"), Debuff("Corruption"), Hostile("Shadowburn"), Aura("Demon Skin", 1, SpellImplicitTarget.UnitCaster),
        SelfSpell("Summon Imp", SpellEffectName.SummonPet), .. voidwalker ? [SelfSpell("Summon Voidwalker", SpellEffectName.SummonPet)] : Array.Empty<SpellInfo>(),
    ];

    [Fact]
    public void Warlock_SummonsADemonByRole_ThenDotsAndBolts()
    {
        Assert.Equal("Demon Skin", Next(Warlock.OutOfCombat(State(Warlock, WarlockBook(false), Self(9), null, PlayerbotRole.RangeDps, inCombat: false))));
        Assert.Equal("Summon Imp", Next(Warlock.OutOfCombat(State(Warlock, WarlockBook(false), Self(9, 100, "Demon Skin"), null, PlayerbotRole.RangeDps, inCombat: false))));
        Assert.Equal("Summon Voidwalker", Next(Warlock.OutOfCombat(State(Warlock, WarlockBook(true), Self(9, 100, "Demon Skin"), null,
            PlayerbotRole.RangeDps, inCombat: false))));
        Assert.Equal("Summon Imp", Next(Warlock.OutOfCombat(State(Warlock, WarlockBook(true), Self(9, 100, "Demon Skin"), null,
            PlayerbotRole.RangeDps, inCombat: false, more: s => s.Party.Add(Member(7, 1))))));
        Assert.Equal("Immolate", Next(Warlock.InCombat(State(Warlock, WarlockBook(false), Self(9), Victim(25f), PlayerbotRole.RangeDps))));
        Assert.Equal("Corruption", Next(Warlock.InCombat(State(Warlock, WarlockBook(false), Self(9), Victim(25f, auras: "Immolate"), PlayerbotRole.RangeDps))));
        Assert.Equal("Shadow Bolt", Next(Warlock.InCombat(State(Warlock, WarlockBook(false), Self(9), Victim(25f, auras: ["Immolate", "Corruption"]),
            PlayerbotRole.RangeDps))));
        Assert.Equal("Shadowburn", Next(Warlock.InCombat(State(Warlock, WarlockBook(false), Self(9), Victim(25f, healthPercent: 5f), PlayerbotRole.RangeDps))));
    }

    // --- paladin ---------------------------------------------------------------------------------------------------------

    private static SpellInfo[] PaladinBook =>
    [
        Aura("Devotion Aura", 1, SpellImplicitTarget.UnitCaster), Aura("Seal of Righteousness", 1, SpellImplicitTarget.UnitCaster),
        Hostile("Judgement", 0), Aura("Blessing of Might"), Aura("Blessing of Wisdom"), SelfSpell("Lay on Hands"), Heal("Holy Light", 40),
        Dispel("Cleanse", 4),
    ];

    [Fact]
    public void Paladin_KeepsItsAuraAndBlessings_SealsThenJudges()
    {
        Assert.Equal("Devotion Aura", Next(Paladin.OutOfCombat(State(Paladin, PaladinBook, Self(2), null, PlayerbotRole.Healer, inCombat: false))));
        Assert.Equal("Blessing of Wisdom", Next(Paladin.OutOfCombat(State(Paladin, PaladinBook, Self(2, 100, "Devotion Aura"), null,
            PlayerbotRole.Healer, inCombat: false))));
        RotationAction might = Paladin.OutOfCombat(State(Paladin, PaladinBook, Self(2, 100, "Devotion Aura", "Blessing of Wisdom"), null,
            PlayerbotRole.Healer, inCombat: false, more: s => s.Party.Add(Member(7, 1) with { PowerType = PowerType.Rage })))!.Value;
        Assert.Equal(("Blessing of Might", new ObjectGuid(7)), (might.Spell!.Name, might.Target));
        Assert.Equal("Seal of Righteousness", Next(Paladin.InCombat(State(Paladin, PaladinBook, Self(2), Victim(3f), PlayerbotRole.MeleeDps))));
        Assert.Equal("Judgement", Next(Paladin.InCombat(State(Paladin, PaladinBook, Self(2, 100, "Seal of Righteousness"), Victim(3f),
            PlayerbotRole.MeleeDps, power: 100, maxPower: 100))));
        Assert.Equal("Lay on Hands", Next(Paladin.InCombat(State(Paladin, PaladinBook, Self(2, 10f), Victim(3f), PlayerbotRole.MeleeDps))));
        Assert.Equal("Cleanse", Next(Paladin.InCombat(State(Paladin, PaladinBook, Self(2), Victim(3f), PlayerbotRole.MeleeDps,
            more: s => s.Party.Add(Member(7, 1, 100f, harmfulDispel: 1u << 4))))));
        Assert.Equal(PlayerbotClassRotation.MeleeRange, Paladin.PreferredRange(State(Paladin, PaladinBook, Self(2), null, PlayerbotRole.Healer)));
    }

    // --- shaman ----------------------------------------------------------------------------------------------------------

    private static SpellInfo[] ShamanBook =>
    [
        Hostile("Lightning Bolt"), Hostile("Earth Shock"), SelfSpell("Searing Totem", SpellEffectName.SummonTotem),
        SelfSpell("Strength of Earth Totem", SpellEffectName.SummonTotem), Heal("Healing Wave", 60), Aura("Lightning Shield", 1, SpellImplicitTarget.UnitCaster),
    ];

    [Fact]
    public void Shaman_ShocksACaster_BoltsFromRange_AndDropsAFireTotemOnce()
    {
        Assert.Equal("Lightning Shield", Next(Shaman.OutOfCombat(State(Shaman, ShamanBook, Self(7), null, PlayerbotRole.Healer, inCombat: false))));
        Assert.Equal("Earth Shock", Next(Shaman.InCombat(State(Shaman, ShamanBook, Self(7), Victim(15f, casting: true), PlayerbotRole.Healer))));
        Assert.Equal("Lightning Bolt", Next(Shaman.InCombat(State(Shaman, ShamanBook, Self(7), Victim(15f), PlayerbotRole.Healer))));
        Assert.Equal("Searing Totem", Next(Shaman.InCombat(State(Shaman, ShamanBook, Self(7), Victim(15f), PlayerbotRole.Healer,
            blocked: ["Lightning Bolt"]))));
        Assert.Null(Next(Shaman.InCombat(State(Shaman, ShamanBook, Self(7), Victim(15f), PlayerbotRole.Healer, blocked: ["Lightning Bolt"],
            more: s => s.Totems.Add("Searing Totem II")))));
        Assert.Equal("Strength of Earth Totem", Next(Shaman.InCombat(State(Shaman, ShamanBook, Self(7), Victim(3f), PlayerbotRole.MeleeDps,
            blocked: ["Lightning Bolt", "Earth Shock"], more: s => s.Totems.Add("Searing Totem")))));
        Assert.Equal(PlayerbotClassRotation.CasterRange, Shaman.PreferredRange(State(Shaman, ShamanBook, Self(7), null, PlayerbotRole.Healer)));
        Assert.Equal(PlayerbotClassRotation.MeleeRange, Shaman.PreferredRange(State(Shaman, ShamanBook, Self(7), null, PlayerbotRole.MeleeDps)));
    }

    // --- druid -----------------------------------------------------------------------------------------------------------

    private static SpellInfo[] DruidBook =>
    [
        Aura("Mark of the Wild"), Aura("Cat Form", 0, SpellImplicitTarget.UnitCaster), Aura("Bear Form", 0, SpellImplicitTarget.UnitCaster),
        Hostile("Claw"), Hostile("Ferocious Bite"), Hostile("Maul"), Debuff("Moonfire"), Hostile("Wrath"), Heal("Healing Touch", 80), Hostile("Growl"),
    ];

    [Fact]
    public void Druid_FightsInItsRolesForm()
    {
        Assert.Equal("Mark of the Wild", Next(Druid.OutOfCombat(State(Druid, DruidBook, Self(11), null, PlayerbotRole.Healer, inCombat: false))));
        Assert.Equal("Cat Form", Next(Druid.OutOfCombat(State(Druid, DruidBook, Self(11, 100, "Mark of the Wild"), null, PlayerbotRole.MeleeDps, inCombat: false))));
        Assert.Equal("Claw", Next(Druid.InCombat(State(Druid, DruidBook, Self(11), Victim(3f), PlayerbotRole.MeleeDps, more: s => s.Form = Form.Cat))));
        Assert.Equal("Ferocious Bite", Next(Druid.InCombat(State(Druid, DruidBook, Self(11), Victim(3f), PlayerbotRole.MeleeDps,
            more: s => { s.Form = Form.Cat; s.ComboPoints = 5; }))));
        Assert.Equal("Maul", Next(Druid.InCombat(State(Druid, DruidBook, Self(11), Victim(3f), PlayerbotRole.Tank, more: s => s.Form = Form.Bear))));
        Assert.Equal("Growl", Next(Druid.InCombat(State(Druid, DruidBook, Self(11), Victim(3f, targetsBot: false), PlayerbotRole.Tank,
            more: s => { s.Form = Form.Bear; s.Party.Add(Member(7, 8)); }))));
        Assert.Equal("Moonfire", Next(Druid.InCombat(State(Druid, DruidBook, Self(11), Victim(25f), PlayerbotRole.Healer))));
        Assert.Equal("Wrath", Next(Druid.InCombat(State(Druid, DruidBook, Self(11), Victim(25f, auras: "Moonfire"), PlayerbotRole.Healer))));
        Assert.Equal(PlayerbotClassRotation.MeleeRange, Druid.PreferredRange(State(Druid, DruidBook, Self(11), null, PlayerbotRole.MeleeDps,
            more: s => s.Form = Form.Cat)));
        Assert.Equal(PlayerbotClassRotation.CasterRange, Druid.PreferredRange(State(Druid, DruidBook, Self(11), null, PlayerbotRole.Healer)));
    }

    // --- emergencies -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ABotLowOnHealth_DrinksAPotion_OrBandagesWhenNothingIsOnIt()
    {
        var potion = new RotationItem(new ObjectGuid(0x4000000000000001UL), 255, 23, 118);
        var bandage = new RotationItem(new ObjectGuid(0x4000000000000002UL), 255, 24, 1251);
        RotationUnit attacker = Victim(3f);
        Assert.Equal("item", Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1, 20f), Victim(3f), PlayerbotRole.MeleeDps,
            more: s => s.HealingPotion = potion))));
        RotationAction bandaging = Warrior.InCombat(State(Warrior, WarriorBook, Self(1, 35f), Victim(10f), PlayerbotRole.MeleeDps,
            more: s => s.Bandage = bandage))!.Value;
        Assert.Equal((RotationActionKind.UseItem, bandage), (bandaging.Kind, bandaging.Item));
        // Something hitting the bot would break the channel; a fresh bandage debuff forbids another.
        Assert.NotEqual("item", Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1, 35f), Victim(10f), PlayerbotRole.MeleeDps,
            more: s => { s.Bandage = bandage; s.Attackers.Add(attacker); }))));
        Assert.NotEqual("item", Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1, 35f, PlayerbotClassRotation.RecentlyBandaged), Victim(10f),
            PlayerbotRole.MeleeDps, more: s => s.Bandage = bandage))));
        Assert.NotEqual("item", Next(Warrior.InCombat(State(Warrior, WarriorBook, Self(1, 20f), Victim(3f), PlayerbotRole.MeleeDps,
            inCombat: false, more: s => s.HealingPotion = potion))));
    }

    [Fact]
    public void EveryClassHasARotation()
    {
        foreach (Class cls in new[] { Class.Warrior, Class.Paladin, Class.Hunter, Class.Rogue, Class.Priest, Class.Shaman, Class.Mage, Class.Warlock, Class.Druid })
        {
            PlayerbotClassRotation rotation = Assert.IsAssignableFrom<PlayerbotClassRotation>(PlayerbotRotations.For(cls));
            Assert.Equal(cls, rotation.Class);
            Assert.NotEmpty(rotation.Abilities);
        }
        Assert.Equal(9, PlayerbotRotations.Every.Count);
    }
}
