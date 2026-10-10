using System.Buffers.Binary;
using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Character.Boost;
using ArcaneCore.World.Items;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using SpellFeature = ArcaneCore.World.Spells.SpellFeature;

namespace ArcaneCore.World.Tests.Gm.Character;

/// <summary>
/// <c>.character boost</c> through the real command table, session and world thread (ArcaneCore operator command: vmangos and
/// mangos have no equivalent, so the level is not a retail one). The spell, item and quest ids are fixtures that only have to be
/// consistent with each other. Wire facts: the 120 action-button values are the SMSG_ACTION_BUTTONS body (vmangos
/// MasterPlayer::SendInitialActionButtons); a spell button is the spell id (ACTION_BUTTON_SPELL = 0, mangos-classic Player.h:150-154);
/// Attack is spell 6603 and the warrior start bar is the Battle Stance page at button 72 (ClassicDB playercreateinfo_action rows
/// (1,1,72,6603,0) and (1,1,73,78,0), cited on <see cref="BoostActionBarPlanner"/>); the equipment slots are the vmangos
/// EquipmentSlots (MainHand 15, OffHand 16, Head 0) of <see cref="InventorySlots"/>. Accounts are Administrator so the tests hold
/// under any security map.
/// </summary>
public sealed class CharacterBoostCommandTests
{
    private const uint WarriorFamily = 4;       // vmangos SpellDefines.h SPELLFAMILY_WARRIOR
    private const uint WarriorClassMask = 1;    // 1 << (class 1 - 1)
    private const uint Attack = 6603;
    private const uint WeaponSword = 7;         // vmangos ItemSubclassWeapon ITEM_SUBCLASS_WEAPON_SWORD
    private const uint WeaponSword2H = 8;       // ITEM_SUBCLASS_WEAPON_SWORD2
    private const uint WeaponAxe = 0;           // ITEM_SUBCLASS_WEAPON_AXE (skill Axes 44, which the fixture character lacks)

    private const uint TeachNear = 990201;      // trainer row: teaches TaughtNear (spell level 4)
    private const uint TaughtNear = 990202;
    private const uint TeachFar = 990203;       // trainer row: teaches TaughtFar (spell level 40, above the boost level)
    private const uint TaughtFar = 990204;
    private const uint QuestTeachNear = 990205; // quest reward spell: teaches QuestNear (spell level 10)
    private const uint QuestNear = 990206;
    private const uint QuestTeachFar = 990207; // quest reward spell: teaches QuestFar (quest MinLevel 30)
    private const uint QuestFar = 990208;
    private const uint TrainerEntry = 8101;

    private const uint HeadJunk = 7000;         // worn before the boost, not a pool item (no source)
    private const uint HeadBest = 7001;
    private const uint HeadLowLevel = 7002;
    private const uint OneHandSword = 7010;
    private const uint TwoHandSword = 7011;
    private const uint Shield = 7020;
    private const uint AxeNoSkill = 7040;       // a one-hand axe far stronger than the sword, but the fixture character has no Axes skill
    private const uint HighLevelHead = 7030;    // item level 40: outside a level 14 pool

    private static string Link(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";

    private static ItemTemplate Armor(uint entry, InventoryType type, uint subClass, uint itemLevel, int armor, int strength)
        => new()
        {
            Entry = entry, Name = $"armor-{entry}", Class = (uint)ItemClass.Armor, SubClass = subClass, InventoryType = (uint)type,
            ItemLevel = itemLevel, RequiredLevel = itemLevel, Armor = armor, Quality = 2, Stackable = 1,
            Stats = [new ItemStat((uint)ItemStatType.Strength, strength)],
        };

    private static ItemTemplate Weapon(uint entry, InventoryType type, uint subClass, uint itemLevel, float damage)
        => new()
        {
            Entry = entry, Name = $"weapon-{entry}", Class = (uint)ItemClass.Weapon, SubClass = subClass, InventoryType = (uint)type,
            ItemLevel = itemLevel, RequiredLevel = itemLevel, Quality = 2, Stackable = 1, Delay = 2000,
            Damages = [new ItemDamage(damage, damage, 0)],
            Stats = [new ItemStat((uint)ItemStatType.Strength, 2)],
        };

    private static ItemTemplate[] ItemFixture() =>
    [
        Armor(HeadJunk, InventoryType.Head, ItemSubClasses.ArmorMisc, 1, 1, 0),
        Armor(HeadBest, InventoryType.Head, ItemSubClasses.ArmorMisc, 12, 20, 6),
        Armor(HeadLowLevel, InventoryType.Head, ItemSubClasses.ArmorMisc, 3, 5, 1),
        Armor(HighLevelHead, InventoryType.Head, ItemSubClasses.ArmorMisc, 40, 90, 40),
        // The one-hand sword and the shield need the Swords and Shield skills; the two-hander is the best weapon by score but a
        // shield tank (OneHandShield style) takes a one-hander for the main hand.
        Weapon(OneHandSword, InventoryType.Weapon, WeaponSword, 12, 14f),
        Weapon(TwoHandSword, InventoryType.TwoHandWeapon, WeaponSword2H, 13, 40f),
        Weapon(AxeNoSkill, InventoryType.Weapon, WeaponAxe, 13, 90f),
        Armor(Shield, InventoryType.Shield, ItemSubClasses.ArmorShield, 12, 80, 4),
    ];

    /// <summary>
    /// Turns the retail skill system on (a host without a catalog runs the Legacy stand-ins, where Player.Skills is null and every
    /// skill reads 300): three weapon/armor proficiency lines, so <c>player.Skills</c> exists and CanUseItem checks real skills.
    /// </summary>
    private static void ConfigureSkills(IServiceCollection services)
        => services.AddSingleton(new SkillCatalog(
            [
                new SkillLineRecord(ItemSkills.Swords, SkillCategories.Weapon, "Swords", 0),
                new SkillLineRecord(ItemSkills.TwoHandedSwords, SkillCategories.Weapon, "Two-Handed Swords", 0),
                new SkillLineRecord(ItemSkills.Shield, SkillCategories.Armor, "Shield", 0),
                new SkillLineRecord(SkillIds.LanguageCommon, SkillCategories.Languages, "Common", 0),
            ],
            [
                new SkillRaceClassInfoRecord(ItemSkills.Swords, 0, 0, 0, 0, 0),
                new SkillRaceClassInfoRecord(ItemSkills.TwoHandedSwords, 0, 0, 0, 0, 0),
                new SkillRaceClassInfoRecord(ItemSkills.Shield, 0, 0, 0, 0, 0),
                new SkillRaceClassInfoRecord(SkillIds.LanguageCommon, 0, 0, 0, 0, 0),
            ],
            [],
            []));

    /// <summary>The environment: items, the vendor that sells the pool, a class trainer, two class quests, spells and the skills.</summary>
    private static async Task<Player> InstallAsync(WorldTestHost host, string name, bool withSources = true, bool withItems = true)
    {
        Player player = await host.PlayerAsync(name);
        await host.OnWorldAsync(() =>
        {
            if (withItems)
            {
                host.WorldServices.GetRequiredService<ItemsFeature>().ReplaceTemplates(new ItemTemplateStore(ItemFixture()));
            }

            QuestNpcFeature quests = host.WorldServices.GetRequiredService<QuestNpcFeature>();
            if (withSources)
            {
                quests.Services.ReplaceNpcs(new NpcStore(NpcContent.Empty with
                {
                    VendorItems = new[] { HeadBest, HeadLowLevel, HighLevelHead, OneHandSword, TwoHandSword, AxeNoSkill, Shield }
                        .Select(item => new VendorItem { Entry = 8100, Item = item }).ToArray(),
                    TrainerSpells =
                    [
                        new TrainerSpell { Entry = TrainerEntry, Spell = TeachNear },
                        new TrainerSpell { Entry = TrainerEntry, Spell = TeachFar },
                    ],
                }));
                quests.Services.ReplaceQuests(new QuestStore(new QuestContent(
                [
                    new QuestTemplate { Entry = 9001, MinLevel = 10, RequiredClasses = WarriorClassMask, RewSpellCast = QuestTeachNear },
                    new QuestTemplate { Entry = 9002, MinLevel = 30, RequiredClasses = WarriorClassMask, RewSpellCast = QuestTeachFar },
                ], [], [])));
            }

            host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(
            [
                new CreatureTemplate { Entry = TrainerEntry, Name = "Trainer", NpcFlags = (uint)NpcFlags.Trainer, TrainerType = 0, TrainerClass = (byte)player.Class },
            ], [], [], [], []));

            SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
            spells.System.Store = new SpellStore(
            [
                new SpellInfo { Id = Attack, SpellFamilyName = WarriorFamily, SpellLevel = 1 },
                new SpellInfo { Id = TeachNear, Effects = [new SpellEffectInfo { Effect = SpellEffectName.LearnSpell, TriggerSpell = TaughtNear }] },
                new SpellInfo { Id = TaughtNear, SpellFamilyName = WarriorFamily, SpellLevel = 4 },
                new SpellInfo { Id = TeachFar, Effects = [new SpellEffectInfo { Effect = SpellEffectName.LearnSpell, TriggerSpell = TaughtFar }] },
                new SpellInfo { Id = TaughtFar, SpellFamilyName = WarriorFamily, SpellLevel = 40 },
                new SpellInfo { Id = QuestTeachNear, Effects = [new SpellEffectInfo { Effect = SpellEffectName.LearnSpell, TriggerSpell = QuestNear }] },
                new SpellInfo { Id = QuestNear, SpellFamilyName = WarriorFamily, SpellLevel = 10 },
                new SpellInfo { Id = QuestTeachFar, Effects = [new SpellEffectInfo { Effect = SpellEffectName.LearnSpell, TriggerSpell = QuestFar }] },
                new SpellInfo { Id = QuestFar, SpellFamilyName = WarriorFamily, SpellLevel = 10 },
            ], [], []);
            spells.System.LearnSpell(player, Attack);

            // Proficiency is the server's own wear rule (PlayerInventory.CanUseItem): the kit is only what this character may wear.
            player.Skills!.Set(ItemSkills.Swords, 1, 300);
            player.Skills.Set(ItemSkills.TwoHandedSwords, 1, 300);
            player.Skills.Set(ItemSkills.Shield, 1, 300);
        });
        return player;
    }

    /// <summary>Sends <paramref name="command"/> and returns every system chat line it produced, plus the other packets read meanwhile.</summary>
    private static async Task<(List<string> Lines, List<(WorldOpcode Opcode, byte[] Payload)> Packets)> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectFromAsync(WorldOpcode.SmsgMessagechat, TimeSpan.FromMilliseconds(400));
        List<string> lines = [.. packets.Where(p => p.Opcode == WorldOpcode.SmsgMessagechat).Select(p => ChatMessage.Parse(p.Payload).Text)];
        return (lines, packets);
    }

    private static Task<bool> Knows(WorldTestHost host, Player player, uint spell) => host.OnWorldAsync(() =>
        host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.HasSpell(player, spell));

    private static Task<uint> WornEntry(WorldTestHost host, Player player, byte slot) => host.OnWorldAsync(() =>
        player.Inventory.GetItem(InventorySlots.Bag0, slot)?.Template.Entry ?? 0u);

    private static async Task<WorldTestClient> EnterAsync(WorldTestHost host, string account, string character)
    {
        WorldTestClient client = await host.EnterWorldAsync(account, character, AccountSecurity.Administrator);
        // Chat in Common needs the language skill now that skills are real (ChatHandlers: Player.KnowsLanguage).
        Player sender = await host.PlayerAsync(character);
        await host.OnWorldAsync(() => sender.Skills!.LearnLanguage((uint)Language.Common));
        await client.CollectAsync();
        return client;
    }

    [Fact]
    public void Command_IsAnAdministratorChild_OfTheCharacterRoot()
    {
        ArcaneCore.World.Commands.CommandTable table = ArcaneCore.World.Commands.ChatCommands.CreateTable();
        Assert.Null(table.Resolve("character boost", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("character boost", AccountSecurity.Administrator));
        Assert.NotNull(table.Resolve("character rename", AccountSecurity.GameMaster));   // the root's other children are untouched
    }

    [Fact]
    public async Task Boost_AppliesTheLevel_LearnsSpellsUpToIt_EquipsTheKit_FillsBars_AndTopsUpMoney()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await EnterAsync(host, "BSTMAIN", "Bstmain");
        Player player = await InstallAsync(host, "Bstmain");
        await host.OnWorldAsync(() => player.Inventory.AddItemAt(InventorySlots.Bag0, InventorySlots.Head, HeadJunk, 1, out _));
        Assert.Equal(HeadJunk, await WornEntry(host, player, InventorySlots.Head));
        await gm.CollectAsync();

        (List<string> lines, List<(WorldOpcode Opcode, byte[] Payload)> packets) = await RunAsync(gm, ".character boost 14");

        // The level.
        Assert.Equal((byte)14, await host.PlayerStateAsync("Bstmain", p => p.Level));
        Assert.Contains(lines, l => l.StartsWith("Boost Bstmain: level 1->14,", StringComparison.Ordinal));

        // Spells: the trainer's and the quests' up to the level only.
        Assert.True(await Knows(host, player, TaughtNear));
        Assert.False(await Knows(host, player, TaughtFar));
        Assert.True(await Knows(host, player, QuestNear));
        Assert.False(await Knows(host, player, QuestFar));
        Assert.Contains(lines, l => l.Contains("2 spells learned", StringComparison.Ordinal));

        // Gear: the best usable piece per slot, in the right slots; the over-level piece is not a candidate; a two-hander is not
        // worn by a shield tank, which gets a one-hander and a shield.
        Assert.Equal(HeadBest, await WornEntry(host, player, InventorySlots.Head));
        Assert.Equal(OneHandSword, await WornEntry(host, player, InventorySlots.MainHand));
        Assert.Equal(Shield, await WornEntry(host, player, InventorySlots.OffHand));
        Assert.Equal(0u, await host.OnWorldAsync(() => player.Inventory.GetItemCount(HighLevelHead)));
        Assert.Equal(0u, await host.OnWorldAsync(() => player.Inventory.GetItemCount(TwoHandSword)));

        // Proficiency: the axe is a vendor item with a far higher score than the sword, but the character has no Axes skill, so the
        // server's wear rule (PlayerInventory.CanUseItem) rejects it and the main hand falls back to the usable sword.
        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.NoRequiredProficiency, await host.OnWorldAsync(() => player.Inventory.CanUseItem(ItemFixture().Single(t => t.Entry == AxeNoSkill))));
        Assert.Equal(0u, await host.OnWorldAsync(() => player.Inventory.GetItemCount(AxeNoSkill)));

        // What was worn before moved to the bags instead of being destroyed.
        Assert.Equal(1u, await host.OnWorldAsync(() => player.Inventory.GetItemCount(HeadJunk)));
        Assert.Contains(lines, l => l.Contains("1 moved to bags", StringComparison.Ordinal));

        // Money: 100 copper per level squared.
        uint money = CharacterBoost.MoneyPerLevelSquared * 14 * 14;
        Assert.Equal(money, await host.PlayerStateAsync("Bstmain", p => p.Money));
        Assert.Contains(lines, l => l.EndsWith($"money {money}.", StringComparison.Ordinal));

        // Action bars: Attack and the learned class spells on the warrior's Battle Stance page, and the full 120 values sent.
        uint[] buttons = await host.PlayerStateAsync("Bstmain", p => p.ActionButtons.ToArray());
        Assert.Equal(Attack, buttons[BoostActionBarPlanner.WarriorPrimaryStart]);
        Assert.Contains(TaughtNear, buttons);
        Assert.Contains(QuestNear, buttons);
        Assert.DoesNotContain(TaughtFar, buttons);
        byte[] sent = Assert.Single(packets, p => p.Opcode == WorldOpcode.SmsgActionButtons).Payload;
        Assert.Equal(Player.ActionButtonCount * 4, sent.Length);
        for (int i = 0; i < Player.ActionButtonCount; i++)
        {
            Assert.Equal(buttons[i], BinaryPrimitives.ReadUInt32LittleEndian(sent.AsSpan(i * 4, 4)));
        }
    }

    [Fact]
    public async Task Boost_RunTwice_ChangesNothingTheSecondTime_AndKeepsAnyLargerPurse()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await EnterAsync(host, "BSTTWICE", "Bsttwice");
        Player player = await InstallAsync(host, "Bsttwice");

        (List<string> first, _) = await RunAsync(gm, ".character boost 14");
        string firstLine = Assert.Single(first, l => l.StartsWith("Boost Bsttwice: level", StringComparison.Ordinal));
        Match m = Regex.Match(firstLine, @"(\d+) items equipped \((\d+) kept, (\d+) moved to bags\)");
        Assert.True(m.Success, firstLine);
        int total = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(total >= 3, firstLine);   // head, main hand, off hand at least
        Assert.Equal("0", m.Groups[2].Value);

        uint[] barsBefore = await host.PlayerStateAsync("Bsttwice", p => p.ActionButtons.ToArray());
        uint[] worn = await host.OnWorldAsync(() => Enumerable.Range(0, InventorySlots.EquipmentEnd)
            .Select(s => player.Inventory.GetItem(InventorySlots.Bag0, (byte)s)?.Template.Entry ?? 0u).ToArray());
        await host.OnWorldAsync(() => player.Money = 25_000);   // above the 19600 for level 14

        (List<string> second, _) = await RunAsync(gm, ".character boost 14");

        string secondLine = Assert.Single(second, l => l.StartsWith("Boost Bsttwice: level", StringComparison.Ordinal));
        Assert.Contains($"level 14->14, 0 spells learned, {total} items equipped ({total} kept, 0 moved to bags), 0 action buttons set, money 25000.", secondLine);
        Assert.Equal(barsBefore, await host.PlayerStateAsync("Bsttwice", p => p.ActionButtons.ToArray()));
        Assert.Equal(worn, await host.OnWorldAsync(() => Enumerable.Range(0, InventorySlots.EquipmentEnd)
            .Select(s => player.Inventory.GetItem(InventorySlots.Bag0, (byte)s)?.Template.Entry ?? 0u).ToArray()));
        Assert.Equal(25_000u, await host.PlayerStateAsync("Bsttwice", p => p.Money));
        Assert.Equal(0u, await host.OnWorldAsync(() => player.Inventory.GetItemCount(HeadLowLevel)));   // nothing stray was added
    }

    [Fact]
    public async Task Boost_ANamedOnlinePlayer_IsBoosted_AndToldWho()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await EnterAsync(host, "BSTGM", "Bstgm");
        await using WorldTestClient victim = await EnterAsync(host, "BSTVIC", "Bstvic");
        await InstallAsync(host, "Bstvic");

        (List<string> lines, _) = await RunAsync(gm, ".character boost bstvic 10");

        Assert.Contains(lines, l => l.StartsWith("Boost Bstvic: level 1->10,", StringComparison.Ordinal));
        Assert.Equal((byte)10, await host.PlayerStateAsync("Bstvic", p => p.Level));
        Assert.Equal((byte)1, await host.PlayerStateAsync("Bstgm", p => p.Level));   // the invoker is not the target when a name is given
        // The level change itself first tells the target "level up you to (10)" (the .modify level path); the boost notice follows.
        List<string> told = [.. (await victim.CollectFromAsync(WorldOpcode.SmsgMessagechat, TimeSpan.FromMilliseconds(400))).Where(p => p.Opcode == WorldOpcode.SmsgMessagechat).Select(p => ChatMessage.Parse(p.Payload).Text)];
        Assert.Contains($"{Link("Bstgm")} boosted you to level 10.", told);
    }

    [Theory]
    [InlineData(".character boost 0")]
    [InlineData(".character boost -3")]
    public async Task Boost_RefusesALevelBelowOne_AndChangesNothing(string command)
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await EnterAsync(host, "BSTLOW", "Bstlow");
        await InstallAsync(host, "Bstlow");
        byte max = host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.MaxPlayerLevel;

        (List<string> lines, _) = await RunAsync(gm, command);

        Assert.Equal($"Boost refused: level must be between 1 and {max}.", Assert.Single(lines));
        Assert.Equal((byte)1, await host.PlayerStateAsync("Bstlow", p => p.Level));
        Assert.Equal(0u, await host.PlayerStateAsync("Bstlow", p => p.Money));
    }

    [Fact]
    public async Task Boost_RefusesALevelAboveTheMaximum_AndChangesNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await EnterAsync(host, "BSTHIGH", "Bsthigh");
        await InstallAsync(host, "Bsthigh");
        byte max = host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.MaxPlayerLevel;

        (List<string> lines, _) = await RunAsync(gm, $".character boost {max + 1}");

        Assert.Equal($"Boost refused: level must be between 1 and {max}.", Assert.Single(lines));
        Assert.Equal((byte)1, await host.PlayerStateAsync("Bsthigh", p => p.Level));
        Assert.Equal(0u, await host.OnWorldAsync(() => (uint)Enumerable.Range(0, InventorySlots.EquipmentEnd)
            .Count(s => host.World.FindOnlinePlayer("Bsthigh")!.Inventory.GetItem(InventorySlots.Bag0, (byte)s) is not null)));
    }

    [Fact]
    public async Task Boost_RefusesALevelBelowTheCharactersOwn()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await EnterAsync(host, "BSTDOWN", "Bstdown");
        await InstallAsync(host, "Bstdown");
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".levelup 19");
        await gm.ReadUntilAsync(WorldOpcode.SmsgLevelupInfo);
        Assert.Equal((byte)20, await host.PlayerStateAsync("Bstdown", p => p.Level));
        await gm.CollectAsync();

        (List<string> lines, _) = await RunAsync(gm, ".character boost 14");

        Assert.Equal("Boost refused for Bstdown: the character is level 20, above 14.", Assert.Single(lines));
        Assert.Equal((byte)20, await host.PlayerStateAsync("Bstdown", p => p.Level));
        Assert.Equal(0u, await host.PlayerStateAsync("Bstdown", p => p.Money));
    }

    [Fact]
    public async Task Boost_RefusesAnUnknownName_AndAnExistingCharacterThatIsOffline()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await EnterAsync(host, "BSTNAME", "Bstname");
        await InstallAsync(host, "Bstname");

        (List<string> unknown, _) = await RunAsync(gm, ".character boost Nobodyhere 14");
        Assert.Equal("Player not found!", Assert.Single(unknown));

        // A character that exists but is not logged in is not "online" either.
        byte[] key = await host.AddAccountAsync("BSTOFF");
        await using (WorldTestClient creator = await host.ConnectAsync())
        {
            await creator.AuthenticateAsync("BSTOFF", key);
            await creator.CreateCharacterAsync("Bstoff");
        }

        (List<string> offline, _) = await RunAsync(gm, ".character boost Bstoff 14");
        Assert.Equal("Player not found!", Assert.Single(offline));
        Assert.Equal((byte)1, await host.PlayerStateAsync("Bstname", p => p.Level));
    }

    [Fact]
    public async Task Boost_RefusesADeadTarget_AnInCombatTarget_AndChangesNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await EnterAsync(host, "BSTSTATE", "Bststate");
        Player player = await InstallAsync(host, "Bststate");

        await host.OnWorldAsync(() => player.Map!.Combat.SetInCombatState(player, 60_000));
        (List<string> combat, _) = await RunAsync(gm, ".character boost 14");
        Assert.Equal("Boost refused for Bststate: the character is in combat.", Assert.Single(combat));
        Assert.Equal((byte)1, await host.PlayerStateAsync("Bststate", p => p.Level));
        Assert.Equal(0u, await host.PlayerStateAsync("Bststate", p => p.Money));
        Assert.False(await Knows(host, player, TaughtNear));

        await host.OnWorldAsync(() =>
        {
            MapCombat.ClearInCombat(player);
            player.Health = 0;
        });
        (List<string> dead, _) = await RunAsync(gm, ".character boost 14");
        Assert.Equal("Boost refused for Bststate: the character is dead.", Assert.Single(dead));
        Assert.Equal((byte)1, await host.PlayerStateAsync("Bststate", p => p.Level));
        Assert.Equal(0u, await host.PlayerStateAsync("Bststate", p => p.Money));
    }

    [Fact]
    public async Task Boost_RefusesWithoutItemTemplates_AndWithoutAnyItemSource_AndChangesNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await EnterAsync(host, "BSTNOITEM", "Bstnoitem");

        // Nothing loaded: no item templates.
        (List<string> none, _) = await RunAsync(gm, ".character boost 14");
        Assert.Equal("Boost refused for Bstnoitem: item templates are not loaded.", Assert.Single(none));
        Assert.Equal((byte)1, await host.PlayerStateAsync("Bstnoitem", p => p.Level));

        // Templates but no vendor, quest or loot row: no kit can be chosen honestly.
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<ItemsFeature>().ReplaceTemplates(new ItemTemplateStore(ItemFixture())));
        (List<string> noSources, _) = await RunAsync(gm, ".character boost 14");
        Assert.Equal("Boost refused for Bstnoitem: no vendor, quest or loot data is loaded to choose obtainable items from.", Assert.Single(noSources));
        Assert.Equal((byte)1, await host.PlayerStateAsync("Bstnoitem", p => p.Level));
        Assert.Equal(0u, await host.PlayerStateAsync("Bstnoitem", p => p.Money));
    }

    [Fact]
    public async Task Boost_ASyntaxError_IsTheHelpText()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await EnterAsync(host, "BSTSYN", "Bstsyn");

        (List<string> lines, _) = await RunAsync(gm, ".character boost many");

        Assert.StartsWith("Syntax: .character boost", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ShieldTankKit_PairsAOneHandWeaponWithAShield_NotATwoHander()
    {
        // The planner level of the warrior kit: OneHandShield weights with weapon and shield candidates (the test of the pure
        // planner uses armor pieces only).
        PlayerbotStatWeights weights = PlayerbotStatWeights.ShieldTank;
        ItemTemplate oneHand = Weapon(OneHandSword, InventoryType.Weapon, WeaponSword, 12, 14f);
        ItemTemplate twoHand = Weapon(TwoHandSword, InventoryType.TwoHandWeapon, WeaponSword2H, 13, 40f);
        ItemTemplate shield = Armor(Shield, InventoryType.Shield, ItemSubClasses.ArmorShield, 12, 80, 4);

        BoostGearPlan plan = BoostGearPlanner.Plan([oneHand, twoHand, shield], weights, Class.Warrior, false, _ => true);

        Assert.Equal(OneHandSword, Assert.Single(plan.Picks, p => p.Key == InventorySlots.MainHand).Value.Entry);
        Assert.Equal(Shield, Assert.Single(plan.Picks, p => p.Key == InventorySlots.OffHand).Value.Entry);

        // Without a shield the off hand is empty (listed), not filled with the unusable two-hander.
        BoostGearPlan noShield = BoostGearPlanner.Plan([oneHand, twoHand], weights, Class.Warrior, false, _ => true);
        Assert.Contains(InventorySlots.OffHand, noShield.EmptySlots);
    }
}
