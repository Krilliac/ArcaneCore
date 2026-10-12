using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.WorldState;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Economy;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Rates;

/// <summary>
/// The rates <c>.rates</c> can change live. Each one names the option object its consumer reads at every use (checked per consumer in
/// docs/areas/rates.md), so a change needs no re-send and no restart. Not here, with the reason: the player speed rates (own command,
/// <c>.movement</c>, which also re-sends the speeds), <c>Skills:Gain*</c> and <c>Skills:Chance*</c> (an immutable record copied into each
/// player's skills at login), <c>Rate.InstanceResetTime</c> (read only when the schedules are built at start), and the respec cost keys
/// (not vmangos rates and not in a shared option object a GM tool reaches).
/// </summary>
public static class RateCatalog
{
    private const float DefaultMax = 100f;

    private const string Live = "live";

    private const string NewSpawns = "creatures created or respawned afterwards";

    private static readonly IReadOnlyList<RateTarget> None = [];

    /// <summary>Every live-tunable rate, in display order.</summary>
    public static IReadOnlyList<RateDefinition> All { get; } = Build();

    private static Func<RateScope, IReadOnlyList<RateTarget>> On<T>(Func<RateScope, T?> find, Func<T, float> get, Action<T, float> set)
        where T : class
        => scope => find(scope) is { } owner ? [new RateTarget(() => get(owner), v => set(owner, v))] : None;

    private static Func<RateScope, IReadOnlyList<RateTarget>> Both(Func<RateScope, IReadOnlyList<RateTarget>> first, Func<RateScope, IReadOnlyList<RateTarget>> second)
        => scope =>
        {
            IReadOnlyList<RateTarget> a = first(scope);
            IReadOnlyList<RateTarget> b = second(scope);
            return a.Count == 0 ? b : b.Count == 0 ? a : [.. a, .. b];
        };

    private static RateDefinition Rate(
        string name, string description, Func<RateScope, IReadOnlyList<RateTarget>> resolve, float min = 0f, float max = DefaultMax, string appliesTo = Live,
        params string[] aliases)
        => new(name, description, min, max, resolve, appliesTo, aliases);

    private static ProgressionOptions? Progression(RateScope s) => s.Services.GetService<ProgressionFeature>()?.Progression.Options;

    private static QuestNpcOptions? Quests(RateScope s) => s.Services.GetService<QuestNpcFeature>()?.Options;

    private static RestOptions? Rest(RateScope s) => s.Services.GetService<RestFeature>()?.Rest.Options;

    private static ReputationRates? Reputation(RateScope s) => s.Services.GetService<ReputationFeature>()?.Service.Rates;

    private static HonorOptions? Honor(RateScope s) => s.Services.GetService<HonorFeature>()?.Options;

    private static CombatOptions? Combat(RateScope s)
        => CombatEnvironment.For(s.World) is { } environment && !ReferenceEquals(environment, CombatEnvironment.Default) ? environment.Options : null;

    private static LootOptions? Loot(RateScope s) => s.Services.GetService<GameObjectLootFeature>()?.Options;

    private static GameObjectOptions? GameObjects(RateScope s) => s.Services.GetService<GameObjectLootFeature>()?.ObjectOptions;

    private static CreatureStatRates? Creatures(RateScope s) => s.Services.GetService<CreatureWorldFeature>()?.Options.Rates;

    private static CreatureOptions? CreatureConfig(RateScope s) => s.Services.GetService<CreatureWorldFeature>()?.Options;

    private static EconomyOptions? Economy(RateScope s) => s.Services.GetService<EconomyFeature>()?.Options;

    private static IReadOnlyList<RateDefinition> Build()
    {
        var all = new List<RateDefinition>
        {
            // Experience.
            Rate("Rate.XP.Kill", "kill experience of a normal creature", On(Progression, o => o.RateXpKill, (o, v) => o.RateXpKill = v), aliases: ["xp", "xp.normal"]),
            Rate("Rate.XP.Kill.Elite", "kill experience of an elite creature", On(Progression, o => o.RateXpKillElite, (o, v) => o.RateXpKillElite = v), aliases: ["xp.elite"]),
            Rate("Rate.XP.Quest", "quest experience", On(Quests, o => o.RateXpQuest, (o, v) => o.RateXpQuest = v)),
            Rate("Rate.XP.Explore", "exploration experience", On(s => WorldStateHooks.For(s.World).ExplorationSettings, o => o.RateXp, (o, v) => o.RateXp = v)),
            Rate("Rate.XP.Personal.Min", "lowest rate .modify xprate accepts", On(Progression, o => o.RateXpPersonalMin, (o, v) => o.RateXpPersonalMin = v)),
            Rate("Rate.XP.Personal.Max", "highest rate a player below GameMaster may set with .modify xprate", On(Progression, o => o.RateXpPersonalMax, (o, v) => o.RateXpPersonalMax = v)),

            // Rested experience.
            Rate("Rate.Rest.InGame", "rested pool growth while online", On(Rest, o => o.RateInGame, (o, v) => o.RateInGame = v), aliases: ["rest"]),
            Rate("Rate.Rest.Offline.InTavernOrCity", "rested pool growth logged out at an inn or city", On(Rest, o => o.RateOfflineInTavernOrCity, (o, v) => o.RateOfflineInTavernOrCity = v), aliases: ["rest.tavern"]),
            Rate("Rate.Rest.Offline.InWilderness", "rested pool growth logged out elsewhere", On(Rest, o => o.RateOfflineInWilderness, (o, v) => o.RateOfflineInWilderness = v), aliases: ["rest.wilderness"]),

            // Reputation, honor, talents.
            Rate("Rate.Reputation.Gain", "every reputation change", On(Reputation, o => o.Gain, (o, v) => o.Gain = v), aliases: ["rep", "reputation"]),
            Rate("Rate.Reputation.LowLevel.Kill", "reputation from gray kills", On(Reputation, o => o.LowLevelKill, (o, v) => o.LowLevelKill = v), aliases: ["rep.lowlevel"]),
            Rate("Rate.Honor", "honor earned from kills, battleground bonuses and quests", On(Honor, o => o.Rate, (o, v) => o.Rate = v), aliases: ["honor"]),
            Rate("Rate.Talent", "talent points per level", On(s => s.Services.GetService<TalentFeature>()?.Options, o => (float)o.PointsRate, (o, v) => o.PointsRate = v), aliases: ["talent"]),

            // Power and health regeneration, fall damage.
            Rate("Rate.Health", "spirit health regeneration", On(Combat, o => o.RateHealth, (o, v) => o.RateHealth = v), aliases: ["health", "regen.health"]),
            Rate("Rate.Mana", "mana regeneration", On(Combat, o => o.RateMana, (o, v) => o.RateMana = v), aliases: ["mana", "regen.mana"]),
            Rate("Rate.Rage.Income", "rage gained from damage", On(Combat, o => o.RateRageIncome, (o, v) => o.RateRageIncome = v), aliases: ["rage"]),
            Rate("Rate.Rage.Loss", "out-of-combat rage decay", On(Combat, o => o.RateRageLoss, (o, v) => o.RateRageLoss = v)),
            Rate("Rate.Energy", "energy regeneration", On(Combat, o => o.RateEnergy, (o, v) => o.RateEnergy = v), aliases: ["energy"]),
            Rate("Rate.Damage.Fall", "fall damage", On(s => LocomotionEnvironment.RegisteredOptions(s.World), o => o.RateDamageFall, (o, v) => o.RateDamageFall = v), aliases: ["fall"]),

            // Loot and money.
            Rate("Rate.Drop.Item.Poor", "chance of an ungrouped poor (grey) loot row", On(Loot, o => o.DropItemPoorRate, (o, v) => o.DropItemPoorRate = v), aliases: ["drop.poor"]),
            Rate("Rate.Drop.Item.Normal", "chance of an ungrouped common (white) loot row", On(Loot, o => o.DropItemNormalRate, (o, v) => o.DropItemNormalRate = v), aliases: ["drop.normal"]),
            Rate("Rate.Drop.Item.Uncommon", "chance of an ungrouped uncommon (green) loot row", On(Loot, o => o.DropItemUncommonRate, (o, v) => o.DropItemUncommonRate = v), aliases: ["drop.uncommon"]),
            Rate("Rate.Drop.Item.Rare", "chance of an ungrouped rare (blue) loot row", On(Loot, o => o.DropItemRareRate, (o, v) => o.DropItemRareRate = v), aliases: ["drop.rare"]),
            Rate("Rate.Drop.Item.Epic", "chance of an ungrouped epic (purple) loot row", On(Loot, o => o.DropItemEpicRate, (o, v) => o.DropItemEpicRate = v), aliases: ["drop.epic"]),
            Rate("Rate.Drop.Item.Legendary", "chance of an ungrouped legendary (orange) loot row", On(Loot, o => o.DropItemLegendaryRate, (o, v) => o.DropItemLegendaryRate = v), aliases: ["drop.legendary"]),
            Rate("Rate.Drop.Item.Artifact", "chance of an ungrouped artifact loot row", On(Loot, o => o.DropItemArtifactRate, (o, v) => o.DropItemArtifactRate = v), aliases: ["drop.artifact"]),
            Rate("Rate.Drop.Item.Referenced", "chance of an ungrouped reference loot row", On(Loot, o => o.DropItemReferencedRate, (o, v) => o.DropItemReferencedRate = v), aliases: ["drop.referenced"]),
            Rate("Rate.Drop.Money", "creature, chest, container and pickpocket money, and quest reward money",
                Both(On(Loot, o => o.MoneyRate, (o, v) => o.MoneyRate = v), On(Quests, o => o.RateDropMoney, (o, v) => o.RateDropMoney = v)), aliases: ["money"]),
            Rate("Rate.Corpse.Decay.Looted", "how long a looted-out corpse stays (0: a third of the respawn delay)", On(Loot, o => o.LootedCorpseDecayRate, (o, v) => o.LootedCorpseDecayRate = v), aliases: ["corpse"]),

            // Gathering, auctions.
            Rate("Rate.Mining.Amount", "uses of a mining node", On(GameObjects, o => o.MiningAmountRate, (o, v) => o.MiningAmountRate = v)),
            Rate("Rate.Mining.Next", "chance of another use of a mining node", On(GameObjects, o => o.MiningNextRate, (o, v) => o.MiningNextRate = v)),
            Rate("Rate.Auction.Time", "auction durations", On(Economy, o => o.AuctionRateTime, (o, v) => o.AuctionRateTime = v), min: 0.1f),
            Rate("Rate.Auction.Deposit", "auction deposit", On(Economy, o => o.AuctionRateDeposit, (o, v) => o.AuctionRateDeposit = v)),
            Rate("Rate.Auction.Cut", "auction house cut", On(Economy, o => o.AuctionRateCut, (o, v) => o.AuctionRateCut = v)),

            // Creatures.
            Rate("Rate.Creature.Aggro", "creature aggro radius (0: no aggro on sight)", On(CreatureConfig, o => o.AggroRate, (o, v) => o.AggroRate = v), aliases: ["aggro"]),
        };

        all.AddRange(CreatureRates());
        return all;
    }

    private static IEnumerable<RateDefinition> CreatureRates()
    {
        (string Rank, string Label, Func<CreatureStatRates, float> Hp, Action<CreatureStatRates, float> SetHp, Func<CreatureStatRates, float> Damage,
            Action<CreatureStatRates, float> SetDamage, Func<CreatureStatRates, float> Spell, Action<CreatureStatRates, float> SetSpell)[] ranks =
        [
            ("Normal", "normal", o => o.NormalHp, (o, v) => o.NormalHp = v, o => o.NormalDamage, (o, v) => o.NormalDamage = v, o => o.NormalSpellDamage, (o, v) => o.NormalSpellDamage = v),
            ("Elite.Elite", "elite (and unknown rank)", o => o.EliteHp, (o, v) => o.EliteHp = v, o => o.EliteDamage, (o, v) => o.EliteDamage = v, o => o.EliteSpellDamage, (o, v) => o.EliteSpellDamage = v),
            ("Elite.RAREELITE", "rare elite", o => o.RareEliteHp, (o, v) => o.RareEliteHp = v, o => o.RareEliteDamage, (o, v) => o.RareEliteDamage = v, o => o.RareEliteSpellDamage, (o, v) => o.RareEliteSpellDamage = v),
            ("Elite.WORLDBOSS", "world boss", o => o.WorldBossHp, (o, v) => o.WorldBossHp = v, o => o.WorldBossDamage, (o, v) => o.WorldBossDamage = v, o => o.WorldBossSpellDamage, (o, v) => o.WorldBossSpellDamage = v),
            ("Elite.RARE", "rare", o => o.RareHp, (o, v) => o.RareHp = v, o => o.RareDamage, (o, v) => o.RareDamage = v, o => o.RareSpellDamage, (o, v) => o.RareSpellDamage = v),
        ];
        foreach (var r in ranks)
        {
            yield return Rate($"Rate.Creature.{r.Rank}.HP", $"spawn health of {r.Label} creatures", On(Creatures, r.Hp, r.SetHp), min: 0.01f, appliesTo: NewSpawns);
            yield return Rate($"Rate.Creature.{r.Rank}.Damage", $"melee and ranged weapon damage of {r.Label} creatures", On(Creatures, r.Damage, r.SetDamage));
            yield return Rate($"Rate.Creature.{r.Rank}.SpellDamage", $"damage spells of {r.Label} creatures", On(Creatures, r.Spell, r.SetSpell));
        }
    }
}
