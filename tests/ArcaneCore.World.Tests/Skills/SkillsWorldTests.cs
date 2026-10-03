using System.Collections.Concurrent;
using System.Buffers.Binary;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Skills;

/// <summary>
/// The skill system over the in-process daemon: starting spells grant skills, proficiencies and languages at
/// login, a trainer's teaching spell grants a profession, skills persist across a real logout and login, the
/// unlearn opcode obeys the UNLEARNABLE flag, and a host without skill content keeps the legacy stand-ins.
/// Content is synthetic (ids from vanilla: 201 One-Handed Swords, 668 Language Common, 2575/2576 Mining).
/// </summary>
public sealed class SkillsWorldTests
{
    private const uint SwordsSpell = 201;
    private const uint CommonSpell = 668;
    private const uint DualWieldSpell = 674;
    private const uint MiningApprentice = 2575;
    private const uint MiningJourneyman = 2576;
    private const uint FindMinerals = 2580;
    private const uint SmeltTin = 2582;
    private const uint ApprenticeMiner = 9100;

    [Fact]
    public async Task WithoutSkillContent_PlayersKeepTheLegacyStandIns()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("LEGACY", "Legacy");
        Player player = await host.PlayerAsync("Legacy");
        SkillsFeature feature = host.WorldServices.GetRequiredService<SkillsFeature>();

        Assert.False(feature.IsActive);
        Assert.Null(player.Skills);
        Assert.Equal(300u, player.Inventory.Requirements.SkillValue(player.Inventory, 999));
        Assert.IsType<DefaultItemRequirements>(player.Inventory.Requirements);
    }

    [Fact]
    public async Task Login_GrantsSkillsProficienciesAndLanguagesFromTheStartingSpells()
    {
        await using var host = WorldTestHost.Start(configureServices: Configure);
        await using WorldTestClient client = await host.EnterWorldAsync("STARTER", "Starter");
        await client.ReadUntilAsync(WorldOpcode.SmsgSetProficiency);   // the passive PROFICIENCY spell announced its mask
        Player player = await host.PlayerAsync("Starter");

        (bool active, ushort swords, ushort swordsMax, ushort common, bool knowsCommon, bool knowsOrcish, uint weaponMask, bool dualWield, uint free) =
            await host.OnWorldAsync(() => (
                host.WorldServices.GetRequiredService<SkillsFeature>().IsActive,
                player.Skills!.GetValuePure(SkillIds.Swords),
                player.Skills.GetMaxPure(SkillIds.Swords),
                player.Skills.GetValuePure(SkillIds.LanguageCommon),
                player.KnowsLanguage(ArcaneCore.Protocol.Language.Common),
                player.KnowsLanguage(ArcaneCore.Protocol.Language.Orcish),
                player.Skills.WeaponProficiency,
                player.Skills.CanDualWield,
                player.Skills.FreePrimaryProfessionPoints));

        Assert.True(active);
        Assert.Equal((1, 5), (swords, swordsMax));           // level 1: 1..5 * level
        Assert.Equal(300, common);
        Assert.True(knowsCommon);
        Assert.False(knowsOrcish);
        Assert.Equal(128u, weaponMask);
        Assert.False(dualWield);
        Assert.Equal(2u, free);
        Assert.IsType<PlayerItemRequirements>(player.Inventory.Requirements);
    }

    [Fact]
    public async Task SetProficiencyPacket_CarriesTheWeaponMask()
    {
        await using var host = WorldTestHost.Start(configureServices: Configure);
        await using WorldTestClient client = await host.EnterWorldAsync("PROFPKT", "Profpkt");
        byte[] packet = await client.ReadUntilAsync(WorldOpcode.SmsgSetProficiency);
        Assert.Equal(5, packet.Length);
        Assert.Equal((byte)ItemClass.Weapon, packet[0]);
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1)));
    }

    [Fact]
    public async Task TeachingSpell_GrantsTheProfession_SpendsAFreeSlot_AndTeachesTheSpellsTheSkillGrants()
    {
        await using var host = WorldTestHost.Start(configureServices: Configure);
        await using WorldTestClient client = await host.EnterWorldAsync("MINER", "Miner");
        Player player = await host.PlayerAsync("Miner");
        SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();

        SpellCastResult result = await host.OnWorldAsync(() => spells.System.CastSpell(player, ApprenticeMiner, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, result);

        (ushort value, ushort max, ushort step, uint free, bool knowsMining, bool knowsFind, bool knowsSmelt) = await host.OnWorldAsync(() => (
            player.Skills!.GetValuePure(SkillIds.Mining), player.Skills.GetMaxPure(SkillIds.Mining), player.Skills.GetStep(SkillIds.Mining),
            player.Skills.FreePrimaryProfessionPoints,
            spells.Spellbook.HasSpell(player, MiningApprentice), spells.Spellbook.HasSpell(player, FindMinerals), spells.Spellbook.HasSpell(player, SmeltTin)));
        Assert.Equal((1, 75, 1), (value, max, step));
        Assert.Equal(1u, free);
        Assert.True(knowsMining);
        Assert.True(knowsFind);        // learn-on-get with requirement 1
        Assert.False(knowsSmelt);      // requirement 100
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
    }

    [Fact]
    public async Task TrainerPurchase_CastsTheTeachingSpell_SoItsSkillStepEffectRuns()
    {
        await using var host = WorldTestHost.Start(configureServices: Configure);
        await using WorldTestClient client = await host.EnterWorldAsync("TRAINEE", "Trainee");
        Player player = await host.PlayerAsync("Trainee");
        SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
        SkillsFeature skills = host.WorldServices.GetRequiredService<SkillsFeature>();
        var learner = new ArcaneCore.Game.Npc.SpellSystemLearner(() => spells.System, new SkillLineAbilityCatalog([]), () => skills.Catalog);

        ArcaneCore.Game.Npc.TrainerSpellInfo info = learner.DescribeTrainerSpell(ApprenticeMiner)!;
        Assert.Equal((MiningApprentice, true, true, true), (info.LearnedSpell, info.LearnedIsPrimaryProfessionFirstRank, info.IsPrimaryProfessionLearn, info.TeachesPrimaryProfessionFirstRank));
        Assert.Equal(2u, learner.GetFreePrimaryProfessionPoints(player));

        bool bought = await host.OnWorldAsync(() => learner.CastTeachingSpell(player, ObjectGuid.Empty, ApprenticeMiner));
        Assert.True(bought);
        (ushort value, ushort max, uint free, bool known) = await host.OnWorldAsync(() => (
            player.Skills!.GetValuePure(SkillIds.Mining), player.Skills.GetMaxPure(SkillIds.Mining),
            learner.GetFreePrimaryProfessionPoints(player), learner.HasSpell(player, MiningApprentice)));
        Assert.Equal((1, 75, 1u, true), (value, max, free, known));
        Assert.Equal(1u, await host.OnWorldAsync(() => learner.GetSkillValueBase(player, SkillIds.Mining)));
    }

    [Fact]
    public async Task Skills_SurviveLogout_AndComeBackWithTheirSpellsAndFreeSlots()
    {
        await using var host = WorldTestHost.Start(configureServices: Configure);
        (byte[] key, CharacterRecord character) = await CreateAsync(host, "PERSIST", "Persist");
        SkillsFeature feature = host.WorldServices.GetRequiredService<SkillsFeature>();
        var store = (InMemoryCharacterSkillStore)host.WorldServices.GetRequiredService<ICharacterSkillStore>();
        SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();

        await using (WorldTestClient first = await LoginAsync(host, "PERSIST", key, character))
        {
            Player player = await host.PlayerAsync("Persist");
            await host.OnWorldAsync(() =>
            {
                Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, ApprenticeMiner, SpellCastTargets.ForSelf(), triggered: true));
                player.Skills!.Set(SkillIds.Mining, 40, 75);

                // A stored spell whose requirement (100) the skill (40) does not meet stays known across the load:
                // vmangos reads the skills before the spellbook, so the load-time removal finds nothing to remove.
                spells.Spellbook.LearnSpell(player, SmeltTin);
            });
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the first session leaves the world");
        await feature.Saves.FlushAllAsync(TimeSpan.FromSeconds(10));
        CharacterSkillSnapshot saved = await store.LoadAsync(character.Id);
        Assert.Contains(new CharacterSkillRow((ushort)SkillIds.Mining, 40, 75), saved.Skills);
        Assert.Contains(new CharacterSkillRow((ushort)SkillIds.Swords, 1, 5), saved.Skills);
        Assert.Contains(new CharacterSkillRow((ushort)SkillIds.LanguageCommon, 300, 300), saved.Skills);

        await using WorldTestClient second = await LoginAsync(host, "PERSIST", key, character);
        Player again = await host.PlayerAsync("Persist");
        (ushort value, ushort step, uint free, bool knowsFind, ushort swords, bool knowsSmelt) = await host.OnWorldAsync(() => (
            again.Skills!.GetValuePure(SkillIds.Mining), again.Skills.GetStep(SkillIds.Mining), again.Skills.FreePrimaryProfessionPoints,
            spells.Spellbook.HasSpell(again, FindMinerals), again.Skills.GetValuePure(SkillIds.Swords), spells.Spellbook.HasSpell(again, SmeltTin)));
        Assert.Equal((40, 1, 1u, true, 1, true), (value, step, free, knowsFind, swords, knowsSmelt));
    }

    [Fact]
    public async Task UnlearnSkill_OnlyForTheUnlearnableFlag_RemovesTheSkillItsSpellsAndRestoresTheSlot()
    {
        await using var host = WorldTestHost.Start(configureServices: Configure);
        await using WorldTestClient client = await host.EnterWorldAsync("UNLEARN", "Unlearn");
        Player player = await host.PlayerAsync("Unlearn");
        SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
        await host.OnWorldAsync(() => spells.System.CastSpell(player, ApprenticeMiner, SpellCastTargets.ForSelf(), triggered: true));

        // Swords carries no UNLEARNABLE flag: refused.
        await client.SendAsync(WorldOpcode.CmsgUnlearnSkill, BitConverter.GetBytes(SkillIds.Swords));
        await host.OnWorldAsync(() => { });
        Assert.True(await host.OnWorldAsync(() => player.Skills!.Has(SkillIds.Swords)));

        await client.SendAsync(WorldOpcode.CmsgUnlearnSkill, BitConverter.GetBytes(SkillIds.Mining));
        await host.WaitForWorldAsync(() => !player.Skills!.Has(SkillIds.Mining), "mining is unlearned");
        (bool knowsMining, bool knowsFind, uint free) = await host.OnWorldAsync(() => (
            spells.Spellbook.HasSpell(player, MiningApprentice), spells.Spellbook.HasSpell(player, FindMinerals), player.Skills!.FreePrimaryProfessionPoints));
        Assert.False(knowsMining);
        Assert.False(knowsFind);
        Assert.Equal(2u, free);
    }

    [Fact]
    public async Task LevelUp_RaisesTheMaximumOfLevelSkills()
    {
        await using var host = WorldTestHost.Start(configureServices: Configure);
        await using WorldTestClient client = await host.EnterWorldAsync("LEVELER", "Leveler");
        Player player = await host.PlayerAsync("Leveler");
        ArcaneCore.World.Progression.ProgressionFeature progression = host.WorldServices.GetRequiredService<ArcaneCore.World.Progression.ProgressionFeature>();

        ushort max = await host.OnWorldAsync(() =>
        {
            progression.Progression.GiveXp(player, 100_000);
            return player.Skills!.GetMaxPure(SkillIds.Swords);
        });
        Assert.True(player.Level >= 2);
        Assert.Equal((ushort)(5 * player.Level), max);
    }

    [Fact]
    public async Task SetSkillCommand_ChangesAKnownSkill_AndRefusesAnUnknownOne()
    {
        await using var host = WorldTestHost.Start(configureServices: Configure);
        await using WorldTestClient gm = await host.EnterWorldAsync("GMSKILL", "Gmskill", AccountSecurity.GameMaster);
        Player player = await host.PlayerAsync("Gmskill");
        await host.OnWorldAsync(() => player.Skills!.Set(SkillIds.Swords, 1, 5));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".setskill 43 3 5");
        await host.WaitForWorldAsync(() => player.Skills!.GetValuePure(SkillIds.Swords) == 3, "the skill value changes");

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".setskill 186 3 5");   // the character has no mining
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".maxskill");
        await host.WaitForWorldAsync(() => player.Skills!.GetValuePure(SkillIds.Swords) == 5, "maxskill raises the value to the maximum");
        Assert.False(await host.OnWorldAsync(() => player.Skills!.Has(SkillIds.Mining)));
    }

    private static void Configure(IServiceCollection services)
    {
        services.AddSingleton(Catalog());
        services.AddSingleton<ISpellContentStore>(new InMemorySkillSpellContentStore(Content()));
        services.AddSingleton<ICharacterSkillStore, InMemoryCharacterSkillStore>();
    }

    private static SkillCatalog Catalog()
    {
        var tier = new SkillTierRecord(21, Enumerable.Repeat(0u, 16).ToArray(), Enumerable.Range(1, 16).Select(step => (uint)(step * 75)).ToArray());
        return new SkillCatalog(
            [
                new SkillLineRecord(SkillIds.Swords, SkillCategories.Weapon, "Swords", 0),
                new SkillLineRecord(SkillIds.LanguageCommon, SkillCategories.Languages, "Common", 0),
                new SkillLineRecord(SkillIds.Mining, SkillCategories.Profession, "Mining", 0),
                new SkillLineRecord(SkillIds.DualWield, SkillCategories.Class, "Dual Wield", 0),
            ],
            [
                new SkillRaceClassInfoRecord(SkillIds.Swords, 0, 0, 0, 0, 0),
                new SkillRaceClassInfoRecord(SkillIds.LanguageCommon, 0, 0, 0, 0, 0),
                new SkillRaceClassInfoRecord(SkillIds.Mining, 0, 0, SkillRaceClassFlags.Unlearnable, 0, 21),
                new SkillRaceClassInfoRecord(SkillIds.DualWield, 0, 0, SkillRaceClassFlags.AlwaysMaxValue | SkillRaceClassFlags.MonoValue, 0, 0),
            ],
            [tier],
            [
                new SkillLineAbilityRecord(1, SkillIds.Swords, SwordsSpell, 0, 0, 0, 0, 2, 0, 0),
                new SkillLineAbilityRecord(2, SkillIds.LanguageCommon, CommonSpell, 0, 0, 0, 0, 2, 0, 0),
                new SkillLineAbilityRecord(3, SkillIds.Mining, MiningApprentice, 0, 0, 0, MiningJourneyman, 0, 0, 0),
                new SkillLineAbilityRecord(4, SkillIds.Mining, MiningJourneyman, 0, 0, 0, 0, 0, 0, 0),
                new SkillLineAbilityRecord(5, SkillIds.Mining, FindMinerals, 0, 0, 1, 0, 1, 0, 0),
                new SkillLineAbilityRecord(6, SkillIds.Mining, SmeltTin, 0, 0, 100, 0, 1, 0, 0),
                new SkillLineAbilityRecord(7, SkillIds.DualWield, DualWieldSpell, 0, 0, 0, 0, 0, 0, 0),
            ],
            SpellLearnSkillTable.Build(
            [
                new SpellSkillEffect(MiningApprentice, 1, (int)SkillIds.Mining, 0, 1),
                new SpellSkillEffect(MiningJourneyman, 1, (int)SkillIds.Mining, 1, 1),
            ]));
    }

    private static SpellContent Content() => new(
        [
            Passive(SwordsSpell, "One-Handed Swords", t =>
            {
                t.Effect1 = 25;                     // SPELL_EFFECT_WEAPON
                t.Effect2 = 60;                     // SPELL_EFFECT_PROFICIENCY
                t.EffectImplicitTargetA2 = 1;
                t.EquippedItemClass = 2;
                t.EquippedItemSubClassMask = 128;   // one-handed swords
            }),
            Passive(CommonSpell, "Language Common", t =>
            {
                t.Effect1 = 39;                     // SPELL_EFFECT_LANGUAGE
                t.EffectMiscValue1 = 7;
            }),
            Passive(DualWieldSpell, "Dual Wield", t => t.Effect1 = 40),
            Plain(MiningApprentice, "Mining", t =>
            {
                t.Effect1 = 47;                     // SPELL_EFFECT_TRADE_SKILL
                t.Effect2 = 118;                    // SPELL_EFFECT_SKILL
                t.EffectMiscValue2 = (int)SkillIds.Mining;
                t.EffectBasePoints2 = 0;
                t.EffectBaseDice2 = 1;
                t.EffectDieSides2 = 1;
                t.EffectImplicitTargetA2 = 1;
            }),
            Plain(MiningJourneyman, "Mining", t =>
            {
                t.Effect1 = 47;
                t.Effect2 = 118;
                t.EffectMiscValue2 = (int)SkillIds.Mining;
                t.EffectBasePoints2 = 1;
                t.EffectBaseDice2 = 1;
                t.EffectDieSides2 = 1;
                t.EffectImplicitTargetA2 = 1;
            }),
            Plain(FindMinerals, "Find Minerals", t => t.Effect1 = 3),
            Plain(SmeltTin, "Smelt Tin", t => t.Effect1 = 3),
            Plain(ApprenticeMiner, "Apprentice Miner", t =>
            {
                t.Effect1 = 36;                     // SPELL_EFFECT_LEARN_SPELL
                t.EffectTriggerSpell1 = MiningApprentice;
                t.EffectImplicitTargetA1 = 1;
                t.Effect2 = 44;                     // SPELL_EFFECT_SKILL_STEP
                t.EffectMiscValue2 = (int)SkillIds.Mining;
                t.EffectBasePoints2 = 0;
                t.EffectBaseDice2 = 1;
                t.EffectDieSides2 = 1;
                t.EffectImplicitTargetA2 = 1;
            }),
        ],
        [], [], [new SpellRangeRow { Id = 1 }], [],
        [
            new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = SwordsSpell },
            new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = CommonSpell },
        ],
        []);

    private static SpellTemplateRow Plain(uint id, string name, Action<SpellTemplateRow> shape)
    {
        var row = new SpellTemplateRow { Id = id, SpellName = name, RangeIndex = 1, EffectImplicitTargetA1 = 1 };
        shape(row);
        return row;
    }

    private static SpellTemplateRow Passive(uint id, string name, Action<SpellTemplateRow> shape)
    {
        SpellTemplateRow row = Plain(id, name, shape);
        row.Attributes |= 0x40;
        return row;
    }

    private static async Task<(byte[] Key, CharacterRecord Character)> CreateAsync(WorldTestHost host, string account, string name)
    {
        byte[] key = await host.AddAccountAsync(account);
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(account, key);
            await client.CreateCharacterAsync(name);
        }

        Account stored = (await host.Accounts.FindByUsernameAsync(account))!;
        return (key, (await host.Characters.GetByAccountAsync(stored.Id)).Single());
    }

    private static async Task<WorldTestClient> LoginAsync(WorldTestHost host, string account, byte[] key, CharacterRecord character)
    {
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.LoginAsync((ulong)character.Id);
        return client;
    }
}

/// <summary>An in-memory spell content store for the skills tests (the synthetic content replaces the default test spells).</summary>
internal sealed class InMemorySkillSpellContentStore(SpellContent content) : ISpellContentStore
{
    public Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);

    public Task ReplaceDbcTablesAsync(SpellDbcContent dbc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>In-memory <see cref="ICharacterSkillStore"/> (registered for the skills tests; also registered globally so other hosts persist nothing harmful).</summary>
internal sealed class InMemoryCharacterSkillStore : ICharacterSkillStore
{
    private readonly ConcurrentDictionary<int, CharacterSkillSnapshot> _rows = new();

    /// <summary>When set, writes throw (the coordinator must retain and retry).</summary>
    public bool FailWrites { get; set; }

    public int Writes { get; private set; }

    public Task<CharacterSkillSnapshot> LoadAsync(int characterId, CancellationToken cancellationToken = default)
        => Task.FromResult(_rows.GetValueOrDefault(characterId) ?? new CharacterSkillSnapshot([], []));

    public Task<bool> ReplaceSnapshotAsync(int characterId, CharacterSkillSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (FailWrites)
        {
            throw new InvalidOperationException("the skill store is down");
        }

        Writes++;
        _rows[characterId] = snapshot;
        return Task.FromResult(true);
    }
}
