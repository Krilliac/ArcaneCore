using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// The heals of paladin, shaman and priest bots as the 1.12 data shapes them. The spell rows below are modelled on the real
/// rows of the playable-content world.db (spell_template, read-only): Holy Light and Flash of Light are SCRIPT_EFFECT spells
/// (effect 77) whose heal is the rank-less sibling spell 19968 / 19993; Healing Wave and Chain Heal name TARGET_UNIT_FRIEND_CHAIN_HEAL
/// (45); Prayer of Healing and Holy Nova heal the caster's party (20, with a radius); Desperate Prayer heals the caster (1).
/// A bot's heal list admits every heal that can land on one unit, and the cast reaches the friend through the ordinary server path.
/// </summary>
public sealed class PlayerbotHealerSpellsTests
{
    // --- the real rows (cast times from spell_cast_times; ranges 5 = 40 yards, 1 = self) -----------------------------------------

    private static SpellInfo Row(uint id, string name, string rank, uint family, ulong familyFlags, uint icon, int castMs, int rangeIndex, float range,
        SpellEffectInfo effect, uint manaCost = 0, SpellDuration duration = default) => new()
    {
        Id = id, Name = name, Rank = rank, SpellFamilyName = family, SpellFamilyFlags = familyFlags, SpellIconId = icon,
        CastTime = new SpellCastTime(castMs, 0, castMs), RangeIndex = (uint)rangeIndex, Range = new SpellRange(0f, range),
        StartRecoveryCategory = id is 19968 or 19993 ? 0u : 133u, StartRecoveryTime = id is 19968 or 19993 ? 0u : 1500u,
        DamageClass = SpellDamageClass.Magic, Duration = duration, ManaCost = manaCost, Effects = [effect],
    };

    private static SpellEffectInfo Effect(SpellEffectName effect, SpellImplicitTarget target, int basePoints, int dieSides = 1,
        float perLevel = 0f, uint chain = 0, float radius = 0f, float multiplier = 1f) => new()
    {
        Effect = effect, TargetA = target, BasePoints = basePoints, BaseDice = 1, DieSides = dieSides, RealPointsPerLevel = perLevel,
        ChainTarget = chain, Radius = radius, DamageMultiplier = multiplier,
    };

    // Holy Light Rank 1 (635) and its heal (19968): the script casts 19968 with 635's value (HolyLightScript).
    private static readonly SpellInfo HolyLight1 = Row(635, "Holy Light", "Rank 1", 10, 1UL << 31, 70, 2500, 5, 40f,
        Effect(SpellEffectName.ScriptEffect, SpellImplicitTarget.UnitFriend, 38, 9, 0.8f));
    private static readonly SpellInfo HolyLightHeal = Row(19968, "Holy Light", "", 10, 1UL << 14, 70, 2500, 13, 50000f,
        Effect(SpellEffectName.Heal, SpellImplicitTarget.UnitFriend, 0));
    private static readonly SpellInfo FlashOfLight1 = Row(19750, "Flash of Light", "Rank 1", 10, 1UL << 30, 242, 1500, 5, 40f,
        Effect(SpellEffectName.ScriptEffect, SpellImplicitTarget.UnitFriend, 61, 11, 1f));
    private static readonly SpellInfo FlashOfLightHeal = Row(19993, "Flash of Light", "", 10, 1UL << 13, 242, 0, 13, 50000f,
        Effect(SpellEffectName.Heal, SpellImplicitTarget.UnitFriend, 0));
    private static readonly SpellInfo HealingWave1 = Row(331, "Healing Wave", "Rank 1", 11, 64, 13, 1500, 5, 40f,
        Effect(SpellEffectName.Heal, SpellImplicitTarget.UnitFriendChainHeal, 33, 11, 0.7f));
    private static readonly SpellInfo ChainHeal1 = Row(1064, "Chain Heal", "Rank 1", 11, 256, 963, 2500, 5, 40f,
        Effect(SpellEffectName.Heal, SpellImplicitTarget.UnitFriendChainHeal, 319, 49, 2.5f, chain: 3, multiplier: 0.5f));
    private static readonly SpellInfo LesserHealingWave1 = Row(8004, "Lesser Healing Wave", "Rank 1", 11, 128, 964, 1500, 5, 40f,
        Effect(SpellEffectName.Heal, SpellImplicitTarget.UnitFriend, 161, 25, 1.7f));
    private static readonly SpellInfo DesperatePrayer1 = Row(13908, "Desperate Prayer", "Rank 1", 6, 1UL << 24, 73, 0, 1, 0f,
        Effect(SpellEffectName.Heal, SpellImplicitTarget.UnitCaster, 133, 37, 2.4f));
    private static readonly SpellInfo PrayerOfHealing1 = Row(596, "Prayer of Healing", "Rank 1", 6, 512, 540, 3000, 1, 0f,
        Effect(SpellEffectName.Heal, SpellImplicitTarget.EnumUnitsPartyWithinCasterRange, 300, 21, 1.3f, radius: 30f));
    private static readonly SpellInfo HolyNova1 = Row(23455, "Holy Nova", "Rank 1", 6, 1UL << 27, 1874, 0, 1, 0f,
        Effect(SpellEffectName.Heal, SpellImplicitTarget.EnumUnitsPartyWithinCasterRange, 51, 9, 0.4f, radius: 10f));
    private static readonly SpellInfo Renew1 = Row(139, "Renew", "Rank 1", 6, 64, 321, 0, 5, 40f,
        Effect(SpellEffectName.ApplyAura, SpellImplicitTarget.UnitFriend, 8) with { AuraType = AuraType.PeriodicHeal, Amplitude = 3000 },
        duration: new SpellDuration(15000, 0, 15000));

    private static SpellStore StoreOf(params SpellInfo[] spells) => new(spells, [], []);

    private static readonly SpellInfo[] EveryRow =
    [
        HolyLight1, HolyLightHeal, FlashOfLight1, FlashOfLightHeal, HealingWave1, ChainHeal1, LesserHealingWave1, DesperatePrayer1,
        PrayerOfHealing1, HolyNova1, Renew1,
    ];

    // --- the classification -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(635u, (int)PlayerbotHealReach.Friend)]            // ScriptEffect -> 19968
    [InlineData(19750u, (int)PlayerbotHealReach.Friend)]          // ScriptEffect -> 19993
    [InlineData(331u, (int)PlayerbotHealReach.ChainFriend)]       // Healing Wave: TargetA 45
    [InlineData(1064u, (int)PlayerbotHealReach.ChainFriend)]      // Chain Heal: TargetA 45, three jumps
    [InlineData(8004u, (int)PlayerbotHealReach.Friend)]
    [InlineData(13908u, (int)PlayerbotHealReach.SelfOnly)]        // Desperate Prayer: TargetA 1
    public void EveryHeal_ThatCanLandOnOneUnit_IsListed_WithItsReach(uint id, int expected)
    {
        var reach = (PlayerbotHealReach)expected;
        SpellInfo spell = EveryRow.Single(s => s.Id == id);
        PlayerbotAbilities book = PlayerbotAbilities.Resolve(EveryRow, [], StoreOf(EveryRow));
        Assert.Contains(spell, book.DirectHeals);
        Assert.Equal(reach, book.HealReachOf(spell));
        Assert.Equal(reach is PlayerbotHealReach.Friend or PlayerbotHealReach.ChainFriend, book.HealsAnotherUnit(spell));
    }

    [Fact]
    public void GroupHeals_AreClassifiedAsParty_AndNeverAimedAtOneUnit()
    {
        PlayerbotAbilities book = PlayerbotAbilities.Resolve(EveryRow, [], StoreOf(EveryRow));
        Assert.DoesNotContain(PrayerOfHealing1, book.DirectHeals);
        Assert.DoesNotContain(HolyNova1, book.DirectHeals);
        Assert.Equal(PlayerbotHealReach.Party, PlayerbotAbilities.ClassifyHeal(PrayerOfHealing1, AuraType.None, null)!.Value.Reach);
        Assert.Equal(PlayerbotHealReach.Party, PlayerbotAbilities.ClassifyHeal(HolyNova1, AuraType.None, null)!.Value.Reach);
        Assert.Equal([Renew1], book.PeriodicHeals);
        Assert.Equal(PlayerbotHealReach.Friend, book.HealReachOf(Renew1));
    }

    [Fact]
    public void AScriptedHeal_IsAmountedByItsScriptValue_AndRanksStrongestFirst()
    {
        SpellInfo holyLight2 = HolyLight1 with { Id = 639, Rank = "Rank 2", Effects = [HolyLight1.Effects[0] with { BasePoints = 80 }] };
        SpellInfo[] book = [HolyLight1, holyLight2, HealingWave1, FlashOfLight1, HolyLightHeal, FlashOfLightHeal];
        PlayerbotAbilities abilities = PlayerbotAbilities.Resolve(book, [], StoreOf(book));
        Assert.Equal(81, abilities.HealAmountOf(holyLight2));
        Assert.Equal([holyLight2, FlashOfLight1, HolyLight1, HealingWave1], abilities.DirectHeals.Where(s => s.Rank.Length > 0)); // 81, 62, 39, 34
    }

    [Fact]
    public void AScriptEffect_IsOnlyMatchedToAHealSiblingOfTheSameFamilyAndIcon()
    {
        // Same name, rank-less, with a Heal effect, but another family or another icon: not what the script casts.
        SpellInfo otherFamily = HolyLightHeal with { Id = 900_503, SpellFamilyName = 11 };
        SpellInfo otherIcon = HolyLightHeal with { Id = 900_504, SpellIconId = 71 };
        Assert.Null(PlayerbotAbilities.ClassifyHeal(HolyLight1, AuraType.None, StoreOf(HolyLight1, otherFamily, otherIcon)));
        Assert.NotNull(PlayerbotAbilities.ClassifyHeal(HolyLight1, AuraType.None, StoreOf(HolyLight1, otherFamily, otherIcon, HolyLightHeal)));
        // A script effect with no heal sibling at all (Judgement-shaped) is not a heal either.
        SpellInfo judgement = Row(900_502, "Judgement Like", "Rank 1", 10, 0, 9, 0, 5, 40f, Effect(SpellEffectName.ScriptEffect, SpellImplicitTarget.UnitFriend, 0));
        Assert.Null(PlayerbotAbilities.ClassifyHeal(judgement, AuraType.None, StoreOf(judgement)));
    }

    [Fact]
    public void TheScriptedHealSibling_IsTheLowestId_AndAReplacedStoreIsReindexed()
    {
        SpellInfo higher = HolyLightHeal with { Id = 19969 };
        Assert.NotNull(PlayerbotAbilities.ClassifyHeal(HolyLight1, AuraType.None, StoreOf(HolyLight1, higher, HolyLightHeal)));
        SpellStore without = StoreOf(HolyLight1);
        Assert.Null(PlayerbotAbilities.ClassifyHeal(HolyLight1, AuraType.None, without));
        Assert.NotNull(PlayerbotAbilities.ClassifyHeal(HolyLight1, AuraType.None, StoreOf(HolyLight1, higher)));
    }

    [Fact]
    public void ASelfOnlyHeal_IsNeverChosenForAFriend_ButStillForTheBot()
    {
        var priest = new PlayerbotPriestRotation();
        SpellInfo[] book = [DesperatePrayer1, LesserHealingWave1];
        RotationState state = RotationTestKit.State(priest, book, RotationTestKit.Self(5, 50f), null, PlayerbotRole.Healer);
        // Desperate Prayer (134) is the heal nearest a 130-point wound, but it would fill the bot.
        var friend = new RotationUnit { Guid = new ObjectGuid(7), Health = 870, MaxHealth = 1000 };
        Assert.Same(LesserHealingWave1, PlayerbotClassRotation.MostEfficient(state, friend, state.Spells.DirectHeals));
        var self = new RotationUnit { Guid = RotationTestKit.BotGuid, IsSelf = true, Health = 870, MaxHealth = 1000 };
        Assert.Same(DesperatePrayer1, PlayerbotClassRotation.MostEfficient(state, self, state.Spells.DirectHeals));
    }

    // --- the casts through the server ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PaladinBot_HealsAWoundedPartyMember_WithHolyLight_ByTheTriggeredHeal()
    {
        await using Healers h = await Healers.StartAsync(Class.Paladin, [HolyLight1, HolyLightHeal], learn: [HolyLight1.Id], friends: 1);
        uint before = await h.WoundAsync(0, 1);
        Assert.True(await h.UpdateOutOfCombatAsync()); // the party heal: Holy Light, a SCRIPT_EFFECT spell
        // The 635 script casts 19968 with the rank's value (39..47 at level 1): far more than out-of-combat regeneration gives in the cast time.
        await h.Host.WaitForWorldAsync(() => h.Friends[0].Player!.Health >= before + 39, "the friend is healed by Holy Light's triggered heal");
    }

    [Fact]
    public async Task PaladinBot_HealsItself_WithHolyLight_WhenItIsTheTarget()
    {
        await using Healers h = await Healers.StartAsync(Class.Paladin, [HolyLight1, HolyLightHeal], learn: [HolyLight1.Id], friends: 0);
        uint before = await h.Host.OnWorldAsync(() => { h.Bot.Player!.Health = 1; return h.Bot.Player.Health; });
        Assert.True(await h.UpdateOutOfCombatAsync());
        await h.Host.WaitForWorldAsync(() => h.Bot.Player!.Health >= before + 39, "the bot heals itself with Holy Light");
    }

    [Fact]
    public async Task PaladinBot_ThatAssistsAFriend_HealsIt_WithTheScriptedHeal()
    {
        await using Healers h = await Healers.StartAsync(Class.Paladin, [HolyLight1, HolyLightHeal], learn: [HolyLight1.Id], friends: 1, grouped: false);
        uint before = await h.WoundAsync(0, 1);
        Assert.True(await h.Host.OnWorldAsync(() =>
        {
            h.Brain.Contention.BeginAssist(new ObjectGuid(0xF130000000000001UL), h.Friends[0].Player!.Guid);
            return h.Brain.TryHealTagger(h.Bot.Player!);
        }));
        await h.Host.WaitForWorldAsync(() => h.Friends[0].Player!.Health >= before + 39, "the tagger is healed by Holy Light");
    }

    [Fact]
    public async Task ShamanBot_HealsAWoundedPartyMember_WithHealingWave()
    {
        await using Healers h = await Healers.StartAsync(Class.Shaman, [HealingWave1], learn: [HealingWave1.Id], friends: 1);
        uint before = await h.WoundAsync(0, 1);
        Assert.True(await h.UpdateOutOfCombatAsync()); // Healing Wave names TARGET_UNIT_FRIEND_CHAIN_HEAL (45)
        await h.Host.WaitForWorldAsync(() => h.Friends[0].Player!.Health >= before + 34, "the friend is healed by Healing Wave");
    }

    [Fact]
    public async Task ShamanBot_HealsItself_WithHealingWave_WhenItIsTheTarget()
    {
        await using Healers h = await Healers.StartAsync(Class.Shaman, [HealingWave1], learn: [HealingWave1.Id], friends: 0);
        uint before = await h.Host.OnWorldAsync(() => { h.Bot.Player!.Health = 1; return h.Bot.Player.Health; });
        Assert.True(await h.UpdateOutOfCombatAsync()); // sent as a unit target naming the bot (TARGET_UNIT_FRIEND_CHAIN_HEAL)
        await h.Host.WaitForWorldAsync(() => h.Bot.Player!.Health >= before + 34, "the bot heals itself with Healing Wave");
    }

    [Fact]
    public async Task ShamanBot_ChainHeal_HitsTheWoundedFriendAndJumpsToTheNextOne()
    {
        await using Healers h = await Healers.StartAsync(Class.Shaman, [ChainHeal1], learn: [ChainHeal1.Id], friends: 2);
        await h.WoundAsync(0, 1);
        await h.WoundAsync(1, 5);
        Assert.True(await h.UpdateOutOfCombatAsync());
        // 320+ on the primary and half of it on the jump: both friends have far less than that to heal.
        await h.Host.WaitForWorldAsync(() => h.Friends[0].Player!.Health == h.Friends[0].Player!.MaxHealth, "Chain Heal fills the friend");
        await h.Host.WaitForWorldAsync(() => h.Friends[1].Player!.Health == h.Friends[1].Player!.MaxHealth, "Chain Heal fills the one it jumps to");
    }

    [Fact]
    public async Task SelfOnlyHeal_StaysInTheBotsOwnList_ButIsNeverCastAtATagger()
    {
        await using Healers h = await Healers.StartAsync(Class.Priest, [DesperatePrayer1], learn: [DesperatePrayer1.Id], friends: 1, grouped: false);
        uint friendBefore = await h.WoundAsync(0, 1);
        uint botBefore = await h.Host.OnWorldAsync(() => { h.Bot.Player!.Health = Math.Max(1u, h.Bot.Player.MaxHealth / 2); return h.Bot.Player.Health; });
        await h.Host.OnWorldAsync(() =>
        {
            PlayerbotAbilities book = h.Brain.CombatSpells.Abilities(h.Bot.Player!);
            Assert.Contains(DesperatePrayer1, book.DirectHeals); // the warrior content's own self-only Test Heal is there too
            Assert.All(book.DirectHeals, heal => Assert.Equal(PlayerbotHealReach.SelfOnly, book.HealReachOf(heal)));
            Assert.Equal(PlayerbotHealReach.SelfOnly, book.HealReachOf(DesperatePrayer1));
            h.Brain.Contention.BeginAssist(new ObjectGuid(0xF130000000000001UL), h.Friends[0].Player!.Guid);
            Assert.False(h.Brain.TryHealTagger(h.Bot.Player!)); // it would heal the bot, not its tagger
            return true;
        });
        Assert.Equal(friendBefore, await h.Host.OnWorldAsync(() => h.Friends[0].Player!.Health));
        Assert.InRange(await h.Host.OnWorldAsync(() => h.Bot.Player!.Health), botBefore, botBefore + 19); // nothing was cast

        // The bot's own heal is untouched: wounded, it casts Desperate Prayer on itself.
        Assert.True(await h.UpdateOutOfCombatAsync());
        await h.Host.WaitForWorldAsync(() => h.Bot.Player!.Health >= botBefore + 20 || h.Bot.Player.Health == h.Bot.Player.MaxHealth, "the bot heals itself");
    }

    /// <summary>A bot of <c>botClass</c> with the given spells in the store and book, and wounded-able friends (grouped with it).</summary>
    private sealed class Healers : IAsyncDisposable
    {
        private bool _stopped;

        internal required WorldTestHost Host { get; init; }

        internal required WorldSession Bot { get; init; }

        internal required List<WorldSession> Friends { get; init; }

        internal required PlayerbotBrain Brain { get; init; }

        internal static async Task<Healers> StartAsync(Class botClass, SpellInfo[] spells, uint[] learn, int friends, bool grouped = true)
        {
            WorldTestHost host = WorldTestHost.Start();
            WorldSession bot = await CombatTestSessions.EnterAsync(host, "HLRBOT", "Hlrbot");
            var others = new List<WorldSession>();
            for (int i = 0; i < friends; i++) others.Add(await CombatTestSessions.EnterAsync(host, "HLRFRI" + i, "Hlrfri" + (char)('a' + i)));
            var brain = new PlayerbotBrain(bot, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
            await host.OnWorldAsync(() =>
            {
                Player me = bot.Player!;
                me.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)botClass); // a warrior made a class below (the test content has no such creation rows)
                SpellFeature feature = bot.Services.GetRequiredService<SpellFeature>();
                // A rotation drives a bot once its book holds one of the class's abilities by name (a real book always does): an attack the heals never use.
                SpellInfo attack = Row(900_600, botClass switch { Class.Paladin => "Hammer of Justice", Class.Shaman => "Earth Shock", _ => "Smite" },
                    "Rank 1", 0, 0, 1, 0, 5, 30f, Effect(SpellEffectName.SchoolDamage, SpellImplicitTarget.UnitEnemy, 9));
                feature.System.Store = new SpellStore(feature.System.Store.All.Concat(spells).Append(attack), [], []);
                foreach (uint id in learn.Append(attack.Id)) Assert.True(feature.Spellbook.LearnSpell(me, id));
                bot.ManagedBudget = new ManagedActionBudget(8);
                if (grouped)
                {
                    GroupManager groups = host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
                    foreach (WorldSession friend in others)
                    {
                        groups.Invite(me, friend.Player!.Name);
                        groups.Accept(friend.Player);
                        Assert.True(groups.AreInSameGroup(me.Guid, friend.Player.Guid));
                    }
                }

                return true;
            });
            return new Healers { Host = host, Bot = bot, Friends = others, Brain = brain };
        }

        /// <summary>Set friend <paramref name="index"/> to <paramref name="health"/> and return what it holds.</summary>
        internal Task<uint> WoundAsync(int index, uint health) => Host.OnWorldAsync(() =>
        {
            Player player = Friends[index].Player!;
            Assert.True(player.MaxHealth >= 60, "room for a 47-point heal");
            player.Health = health;
            return player.Health;
        });

        internal Task<bool> UpdateOutOfCombatAsync() => Host.OnWorldAsync(() => Brain.CombatSpells.UpdateOutOfCombat(Bot.Player!, 0));

        public async ValueTask DisposeAsync()
        {
            if (_stopped) return;
            _stopped = true;
            Brain.Stop();
            foreach (WorldSession friend in Friends)
            {
                friend.Kick();
                await friend.ManagedClosed;
            }

            Bot.Kick();
            await Bot.ManagedClosed;
            await Host.DisposeAsync();
        }
    }
}
