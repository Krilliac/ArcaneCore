using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>
/// Shared steps of the <c>progression-*</c> scenarios: they set a bot up while it is scripted, then hand it back to its own
/// brain (autonomous mode) and wait for what the brain does by itself through the ordinary handlers. The setup helpers act on
/// world state like the context's own helpers and exist only on this harness.
/// </summary>
public static class ScenarioProgression
{
    /// <summary>Hand <paramref name="bot"/> back to <see cref="PlayerbotBrain"/> (the run's release stops it afterwards).</summary>
    public static Task GoAutonomousAsync(this ScenarioContext context, ScenarioBot bot)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bot);
        return context.StepAsync($"{bot.Name} thinks for itself", async () => ScenarioContext.Expect(
            await context.Services.GetRequiredService<ManagedPlayerbotFeature>().SetControllerAsync(bot.BotId, null).ConfigureAwait(false),
            $"{bot.Name} is not running"));
    }

    /// <summary>Log <paramref name="bot"/> out (saved) and back in, scripted; returns the new controller.</summary>
    public static Task<ScenarioBot> RelogAsync(this ScenarioContext context, ScenarioBot bot)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bot);
        return context.StepAsync($"{bot.Name} logs out and back in", async () =>
        {
            PlayerbotOperationResult stopped = await context.Services.GetRequiredService<ManagedPlayerbotFeature>()
                .StopAsync(bot.BotId.ToString(), context.CancellationToken).ConfigureAwait(false);
            ScenarioContext.Expect(stopped.Success, $"stopping {bot.Name} failed: {stopped.Code}");
            return await context.LoginAsync(bot.Name).ConfigureAwait(false);
        });
    }

    /// <summary>How many <paramref name="opcode"/> packets the scripted controller itself sent (the brain's own actions are not in the log).</summary>
    internal static int SentCount(ScenarioBot bot, WorldOpcode opcode)
        => bot.Log.Snapshot().Count(packet => packet.Direction == ScenarioPacketDirection.Sent && packet.Opcode == opcode);

    /// <summary>The bot's talent state: (free points, spent points, the known talent rank spells in id order).</summary>
    internal static (uint Free, uint Used, uint[] Ranks) TalentState(TalentService talents, Player player)
        => (talents.FreePoints(player), talents.UsedPoints(player),
            [.. talents.Catalog.Talents.SelectMany(talent => talent.RankSpells).Where(spell => spell != 0 && talents.HasSpell(player, spell)).Order()]);

    /// <summary>The first spawn of creature <paramref name="entry"/> on map 0 or 1 (null when the world has none).</summary>
    internal static CreatureSpawn? FindSpawn(ScenarioContext context, uint entry)
    {
        CreatureContent? content = context.Services.GetService<CreatureWorldFeature>()?.Content;
        return content is null ? null : new uint[] { 0, 1 }.SelectMany(map => content.GetSpawns(map, entry)).FirstOrDefault();
    }

    /// <summary>Stand <paramref name="bot"/> two yards beside creature spawn <paramref name="entry"/> and wait until it sees it.</summary>
    internal static async Task NextToAsync(ScenarioContext context, ScenarioBot bot, uint entry)
    {
        CreatureSpawn spawn = FindSpawn(context, entry) ?? throw new ScenarioAssertionException($"no spawn of creature {entry} on map 0 or 1");
        await context.PlaceAsync(bot, spawn.MapId, spawn.X - 2f, spawn.Y, spawn.Z).ConfigureAwait(false);
        await context.WaitUntilAsync($"{bot.Name} sees creature {entry}", () => bot.RequirePlayer().VisibleObjects
            .Any(guid => bot.RequirePlayer().Map?.FindObject(guid) is Game.Creatures.Creature { Entry: var seen } && seen == entry)).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>progression-talents</c>: a level-12 warrior with three free talent points, left to its own brain, spends all three
/// through CMSG_LEARN_TALENT (its premade build, or vmangos LearnRandomTalents order where the catalog lacks the build's talent),
/// and the spent ranks and the free-point count are the same after a logout and login. Needs only a talent catalog
/// (Talents:TalentDbcPath, or a registered TalentCatalog); it is a live-safe scenario of the catalog (any earlier talents of the
/// bot are reset first).
/// </summary>
public sealed class ProgressionTalentsScenario : IPlayerbotScenario
{
    public const string BotName = "Scntalent";

    public string Name => "progression-talents";

    public string Description => "a level-12 warrior spends its three talent points by itself and keeps them over a relog";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        TalentService talents = await context.StepAsync("a talent catalog is loaded", () => Task.FromResult(
            context.Services.GetService<TalentFeature>()?.Service
            ?? throw new ScenarioAssertionException("the talent feature is inert (no Talents:TalentDbcPath / TalentTabDbcPath)"))).ConfigureAwait(false);
        ScenarioBot bot = await context.StepAsync("login " + BotName, () => context.LoginAsync(BotName, 1, 1)).ConfigureAwait(false);
        await context.StepAsync("level 12 with three free points", async () =>
        {
            ScenarioContext.Expect(await context.RaiseLevelAsync(bot, 12).ConfigureAwait(false) >= 12, $"{BotName} is below level 12");
            (uint free, uint used, _) = await bot.ReadAsync(player =>
            {
                talents.ResetTalents(player, noCost: true);
                // The level change hook does not cover every level route yet; settle the points the way login does.
                talents.InitTalentForLevel(player);
                return ScenarioProgression.TalentState(talents, player);
            }).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(0u, used, "points spent before the run");
            ScenarioContext.ExpectEqual(3u, free, "free talent points at level 12");
        }).ConfigureAwait(false);

        await context.GoAutonomousAsync(bot).ConfigureAwait(false);
        uint[] ranks = await context.StepAsync("all three points are spent by the bot", async () =>
        {
            await context.WaitUntilAsync($"{BotName} has no free talent points", () =>
                talents.FreePoints(bot.RequirePlayer()) == 0, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            (uint free, uint used, uint[] known) = await bot.ReadAsync(player => ScenarioProgression.TalentState(talents, player)).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(3u, used, "spent talent points");
            ScenarioContext.Expect(ScenarioProgression.SentCount(bot, WorldOpcode.CmsgLearnTalent) == 0,
                "the scripted controller sent no talent request (the brain did)");
            return known;
        }).ConfigureAwait(false);

        bot = await context.RelogAsync(bot).ConfigureAwait(false);
        await context.StepAsync("the talents survive the relog", async () =>
        {
            (uint free, uint used, uint[] known) = await bot.ReadAsync(player => ScenarioProgression.TalentState(talents, player)).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(12u, (uint)await bot.ReadAsync(player => player.Level).ConfigureAwait(false), "level after the relog");
            ScenarioContext.ExpectEqual(3u, used, "spent talent points after the relog");
            ScenarioContext.ExpectEqual(0u, free, "free talent points after the relog");
            ScenarioContext.Expect(known.SequenceEqual(ranks), $"talent ranks after the relog: [{string.Join(", ", known)}], before: [{string.Join(", ", ranks)}]");
        }).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>progression-gear</c>: a warrior gets <see cref="Give"/> into its bags, then, on its own, wears every item of
/// <see cref="ExpectWorn"/> (weapons, rings and the like it scores as upgrades) through the ordinary equip handlers. Takes its
/// items in the constructor (content-specific: internal, so not in the live catalog).
/// </summary>
internal sealed class ProgressionGearScenario(IReadOnlyList<uint> give, IReadOnlyList<uint> expectWorn) : IPlayerbotScenario
{
    public const string BotName = "Scngear";

    public IReadOnlyList<uint> Give { get; } = give ?? throw new ArgumentNullException(nameof(give));

    public IReadOnlyList<uint> ExpectWorn { get; } = expectWorn ?? throw new ArgumentNullException(nameof(expectWorn));

    public string Name => "progression-gear";

    public string Description => "a warrior wears the better weapon and ring from its bags by itself";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ScenarioBot bot = await context.StepAsync("login " + BotName, () => context.LoginAsync(BotName, 1, 1)).ConfigureAwait(false);
        await context.StepAsync("the items are in the bags", async () =>
        {
            foreach (uint entry in Give)
                await context.GiveItemAsync(bot, entry).ConfigureAwait(false);
            foreach (uint entry in ExpectWorn)
                ScenarioContext.Expect(!await bot.ReadAsync(player => Worn(player, entry)).ConfigureAwait(false), $"item {entry} is already worn");
        }).ConfigureAwait(false);

        await context.GoAutonomousAsync(bot).ConfigureAwait(false);
        await context.StepAsync("the upgrades are worn", async () =>
        {
            await context.WaitUntilAsync($"{BotName} wears {string.Join(", ", ExpectWorn)}",
                () => ExpectWorn.All(entry => Worn(bot.RequirePlayer(), entry)), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            ScenarioContext.Expect(ScenarioProgression.SentCount(bot, WorldOpcode.CmsgAutoequipItem) == 0
                && ScenarioProgression.SentCount(bot, WorldOpcode.CmsgAutoequipItemSlot) == 0,
                "the scripted controller sent no equip request (the brain did)");
        }).ConfigureAwait(false);
    }

    private static bool Worn(Player player, uint entry)
    {
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot)?.Entry == entry)
                return true;
        }

        return false;
    }
}

/// <summary>
/// <c>progression-repair</c>: a warrior wearing <see cref="ArmorEntry"/> broken, standing beside an NPC with the repair flag
/// (<see cref="RepairNpcEntry"/>), repairs it by itself through CMSG_REPAIR_ITEM and pays for it. Content-specific: internal, so not in the
/// live catalog.
/// </summary>
internal sealed class ProgressionRepairScenario(uint repairNpcEntry, uint armorEntry) : IPlayerbotScenario
{
    public const string BotName = "Scnrepair";
    private const uint Purse = 10_000;

    public uint RepairNpcEntry { get; } = repairNpcEntry;

    public uint ArmorEntry { get; } = armorEntry;

    public string Name => "progression-repair";

    public string Description => "a warrior with broken armor repairs it at a repair NPC by itself";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ScenarioBot bot = await context.StepAsync("login " + BotName, () => context.LoginAsync(BotName, 1, 1)).ConfigureAwait(false);
        await context.StepAsync($"beside repair NPC {RepairNpcEntry}", () => ScenarioProgression.NextToAsync(context, bot, RepairNpcEntry)).ConfigureAwait(false);
        await context.StepAsync($"item {ArmorEntry} is worn, then broken", async () =>
        {
            await context.GiveItemAsync(bot, ArmorEntry).ConfigureAwait(false);
            byte[] position = await bot.ReadAsync(player => player.Inventory.AllItems.Where(item => item.Entry == ArmorEntry)
                .Select(item => new[] { item.BagSlot, item.Slot }).First()).ConfigureAwait(false);
            ScenarioContext.Expect(await bot.SendAsync(WorldOpcode.CmsgAutoequipItem, position).ConfigureAwait(false), "equip refused");
            uint max = await bot.ReadAsync(player => Worn(player)?.MaxDurability ?? 0).ConfigureAwait(false);
            ScenarioContext.Expect(max > 0, $"item {ArmorEntry} is not worn or has no durability");
            await context.ReadAsync(() =>
            {
                Worn(bot.RequirePlayer())!.Durability = 0;
                return true;
            }).ConfigureAwait(false);
            await context.GiveMoneyAsync(bot, Purse).ConfigureAwait(false);
        }).ConfigureAwait(false);

        uint before = await bot.ReadAsync(player => player.Money).ConfigureAwait(false);
        await context.GoAutonomousAsync(bot).ConfigureAwait(false);
        await context.StepAsync("the armor is repaired and paid for", async () =>
        {
            await context.WaitUntilAsync($"{BotName}'s item {ArmorEntry} is repaired", () => Worn(bot.RequirePlayer()) is { } item
                && item.Durability == item.MaxDurability, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            uint after = await bot.ReadAsync(player => player.Money).ConfigureAwait(false);
            ScenarioContext.Expect(after < before, $"the repair cost nothing ({before} copper before, {after} after)");
            ScenarioContext.Expect(ScenarioProgression.SentCount(bot, WorldOpcode.CmsgRepairItem) == 0,
                "the scripted controller sent no repair request (the brain did)");
        }).ConfigureAwait(false);

        Item? Worn(Player player) => player.Inventory.AllItems.FirstOrDefault(item => item.Entry == ArmorEntry
            && InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot));
    }
}

/// <summary>
/// <c>progression-quest</c>: a fresh warrior beside quest giver <see cref="GiverEntry"/>, left to its own brain under the
/// server's Quests configuration, accepts quest <see cref="QuestId"/>, completes its objectives and is rewarded for it.
/// Content-specific: internal, so not in the live catalog.
/// </summary>
internal sealed class ProgressionQuestScenario(uint questId, uint giverEntry, string botName = "Scnquest") : IPlayerbotScenario
{
    public uint QuestId { get; } = questId;

    public uint GiverEntry { get; } = giverEntry;

    public string BotName { get; } = botName;

    public string Name => "progression-quest";

    public string Description => "a warrior takes, completes and turns in a kill quest by itself";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ScenarioBot bot = await context.StepAsync("login " + BotName, () => context.LoginAsync(BotName, 1, 1)).ConfigureAwait(false);
        await context.StepAsync($"beside quest giver {GiverEntry}", () => ScenarioProgression.NextToAsync(context, bot, GiverEntry)).ConfigureAwait(false);
        await context.StepAsync($"quest {QuestId} is new to the bot", async () =>
            ScenarioContext.Expect(await context.QuestStateAsync(bot, QuestId).ConfigureAwait(false) is null, $"{BotName} already has quest {QuestId}")).ConfigureAwait(false);

        await context.GoAutonomousAsync(bot).ConfigureAwait(false);
        await context.StepAsync("the bot accepts the quest", () => context.WaitUntilAsync($"{BotName} has quest {QuestId}",
            () => QuestEntry(context, bot) is not null, TimeSpan.FromSeconds(60))).ConfigureAwait(false);
        await context.StepAsync("the bot completes and turns it in", () => context.WaitUntilAsync($"{BotName} is rewarded for quest {QuestId}",
            () => QuestEntry(context, bot)?.Rewarded == true, TimeSpan.FromMinutes(5))).ConfigureAwait(false);
    }

    private QuestStatusData? QuestEntry(ScenarioContext context, ScenarioBot bot)
        => context.Services.GetRequiredService<Npc.QuestNpcFeature>().Services.StateOf(bot.RequirePlayer())?.Quests.Get(QuestId);
}
