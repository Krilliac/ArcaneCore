using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>MSG_TALENT_WIPE_CONFIRM from the server (TalentPackets.WipeConfirm): u64 trainer guid (0: nothing spent), u32 cost.</summary>
public sealed record TalentWipeConfirmView(ulong Trainer, uint Cost);

/// <summary>One line of SMSG_GOSSIP_MESSAGE: u32 index, u8 icon, u8 coded, CString text.</summary>
public sealed record GossipOptionView(uint Index, byte Icon, bool Coded, string Text);

/// <summary>SMSG_GOSSIP_MESSAGE (NpcPackets.GossipMessage): u64 npc, u32 text id, the option lines (the quest lines are not decoded).</summary>
public sealed record GossipMenuView(ulong Npc, uint TextId, IReadOnlyList<GossipOptionView> Options);

/// <summary>
/// Talent client actions and reply decoders for scenarios (talents lane), kept apart from <see cref="ScenarioBot"/> and
/// <see cref="ScenarioDecoders"/> so other lanes' harness work does not collide. Layouts follow the server's own handlers
/// (<c>TalentHandlers</c>, <c>QuestNpcInteractionHandlers</c>, <c>NpcServiceHandlers</c>) and writers (<see cref="TalentPackets"/>,
/// <see cref="NpcPackets"/>).
/// </summary>
public static class ScenarioTalents
{
    /// <summary>CMSG_LEARN_TALENT: u32 talent id, u32 zero-based rank (vmangos Player::LearnTalent; refusals are silent).</summary>
    public static Task<bool> LearnTalentAsync(this ScenarioBot bot, uint talentId, uint rank)
    {
        ArgumentNullException.ThrowIfNull(bot);
        var w = new PacketWriter(8);
        w.WriteUInt32(talentId);
        w.WriteUInt32(rank);
        return bot.SendAsync(WorldOpcode.CmsgLearnTalent, w.ToArray());
    }

    /// <summary>MSG_TALENT_WIPE_CONFIRM from the client: u64 trainer guid (the "yes" of the respec dialog).</summary>
    public static Task<bool> WipeConfirmAsync(this ScenarioBot bot, ObjectGuid trainer)
    {
        ArgumentNullException.ThrowIfNull(bot);
        return bot.SendAsync(WorldOpcode.MsgTalentWipeConfirm, ScenarioPackets.Guid(trainer.Value));
    }

    /// <summary>CMSG_GOSSIP_HELLO: u64 npc.</summary>
    public static Task<bool> GossipHelloAsync(this ScenarioBot bot, ObjectGuid npc)
    {
        ArgumentNullException.ThrowIfNull(bot);
        return bot.SendAsync(WorldOpcode.CmsgGossipHello, ScenarioPackets.Guid(npc.Value));
    }

    /// <summary>CMSG_GOSSIP_SELECT_OPTION: u64 npc, u32 option index (an uncoded line).</summary>
    public static Task<bool> GossipSelectAsync(this ScenarioBot bot, ObjectGuid npc, uint index)
    {
        ArgumentNullException.ThrowIfNull(bot);
        return bot.SendAsync(WorldOpcode.CmsgGossipSelectOption, ScenarioPackets.GuidUInt32(npc.Value, index));
    }

    /// <summary>PLAYER_CHARACTER_POINTS1: the unspent talent points the client shows.</summary>
    public static Task<uint> FreeTalentPointsAsync(this ScenarioBot bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        return bot.ReadAsync(p => p.GetUInt32(UpdateFields.PlayerCharacterPoints1));
    }

    /// <summary>Decode MSG_TALENT_WIPE_CONFIRM (exactly 12 bytes).</summary>
    public static TalentWipeConfirmView WipeConfirm(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length != 12)
        {
            throw new FormatException($"MSG_TALENT_WIPE_CONFIRM: expected 12 bytes, got {payload.Length}");
        }

        return new TalentWipeConfirmView(BitConverter.ToUInt64(payload, 0), BitConverter.ToUInt32(payload, 8));
    }

    /// <summary>Decode the head and the option lines of SMSG_GOSSIP_MESSAGE.</summary>
    public static GossipMenuView GossipMessage(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var r = new PacketReader(payload);
        ulong npc = r.ReadUInt64();
        uint textId = r.ReadUInt32();
        uint count = r.ReadUInt32();
        if (count > 64)
        {
            throw new FormatException($"SMSG_GOSSIP_MESSAGE: {count} options");
        }

        var options = new List<GossipOptionView>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint index = r.ReadUInt32();
            byte icon = r.ReadByte();
            bool coded = r.ReadByte() != 0;
            options.Add(new GossipOptionView(index, icon, coded, CString(ref r)));
        }

        return new GossipMenuView(npc, textId, options);
    }

    private static string CString(ref PacketReader reader)
    {
        var bytes = new List<byte>();
        for (byte b = reader.ReadByte(); b != 0; b = reader.ReadByte())
        {
            bytes.Add(b);
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }
}

/// <summary>
/// The talent lifecycle of one warrior bot (<see cref="Bot"/>), end to end through ordinary client packets: raised to level 12 through
/// the ordinary level-up (it then has 3 points, vmangos GiveLevel -> InitTalentForLevel, Player.cpp:3197); learns ranks 1 and 2 of a
/// first-row talent; a second-row talent is refused with fewer than 5 points spent in the tree (vmangos Player::LearnTalent, the
/// <c>row * 5</c> tier gate); rank 3 spends the last point; the class trainer's "unlearn my talents" gossip line asks for the price
/// (MSG_TALENT_WIPE_CONFIRM) and confirming it removes the talents, gives the points back and takes exactly that money
/// (Player::ResetTalents); a rank learned again survives a logout and login with the points and the respec price.
/// <para>
/// Needs content that a live server only has with the talent DBCs configured: a talent catalog (<c>Talents:TalentDbcPath</c> and
/// <c>TalentTabDbcPath</c>) with a warrior first-row talent of three ranks and a second-row talent in the same tree, and a spawned
/// warrior class trainer on a map the bots may use (<c>World:Playerbots:AllowedMaps</c>). Without them the run fails at a named
/// "content:" step before the bot is changed. The bot's talents are reset for free first, so a rerun starts clean.
/// </para>
/// </summary>
public sealed class TalentLifecycleScenario : IPlayerbotScenario
{
    /// <summary>The bot this scenario logs in (a human warrior, created on first use).</summary>
    public const string Bot = "Scntalent";

    public const byte Level = 12;

    /// <summary>Step names a missing-content run fails at.</summary>
    public const string CatalogStep = "content: a talent catalog (Talents:TalentDbcPath, Talents:TalentTabDbcPath)";

    public const string TrainerStep = "content: a spawned class trainer of the bot's class";

    public string Name => "talent-lifecycle";

    public string Description => "a warrior raised to 12 learns, is tier-gated, respecs at its trainer for money and keeps it across a relog";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ScenarioBot bot = await context.StepAsync("login " + Bot, () => context.LoginAsync(Bot, race: 1, characterClass: 1)).ConfigureAwait(false);
        TalentService talents = await context.StepAsync(CatalogStep, () => Task.FromResult(
            context.Services.GetService<TalentFeature>()?.Service
            ?? throw new ScenarioAssertionException("the talent system is inert: no talent catalog is loaded"))).ConfigureAwait(false);
        (TalentRecord first, TalentRecord second) = await context.StepAsync("content: a first-row talent of three ranks and a second-row talent in its tree",
            () => context.ReadAsync(() => PickTalents(talents.Catalog, bot.RequirePlayer()))).ConfigureAwait(false);
        TrainerSpot trainer = await context.StepAsync(TrainerStep, () => context.ReadAsync(() => FindTrainer(context, bot.RequirePlayer()))).ConfigureAwait(false);

        await context.StepAsync("start clean: no talent spent", () => context.ReadAsync(() =>
        {
            Player player = bot.RequirePlayer();
            talents.ResetTalents(player, noCost: true);
            talents.InitTalentForLevel(player);
            return talents.UsedPoints(player) == 0 ? true : throw new ScenarioAssertionException("the free reset left talents");
        })).ConfigureAwait(false);

        uint points = await context.StepAsync($"reach level {Level} through the ordinary level-up and get its points", async () =>
        {
            byte level = (byte)await context.RaiseLevelAsync(bot, Level).ConfigureAwait(false);
            uint expected = TalentRules.PointsForLevel(level, talents.Options.PointsRate);
            ScenarioContext.Expect(expected >= 3, $"level {level} gives {expected} points, fewer than the three this scenario spends");
            ScenarioContext.ExpectEqual(expected, await bot.FreeTalentPointsAsync().ConfigureAwait(false), "free talent points");
            return expected;
        }).ConfigureAwait(false);

        await context.StepAsync($"learn ranks 1 and 2 of talent {first.Id}", async () =>
        {
            await LearnAsync(context, bot, first, 0).ConfigureAwait(false);
            await LearnAsync(context, bot, first, 1).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(points - 2, await bot.FreeTalentPointsAsync().ConfigureAwait(false), "free talent points after two ranks");
        }).ConfigureAwait(false);

        await context.StepAsync($"second-row talent {second.Id} is refused with 2 of 5 points spent in the tree", async () =>
        {
            // The handler ran when SendAsync returned (world thread); a refusal sends nothing, so the state is the answer.
            ScenarioContext.Expect(await bot.LearnTalentAsync(second.Id, 0).ConfigureAwait(false), "CMSG_LEARN_TALENT not admitted");
            ScenarioContext.Expect(!await bot.ReadAsync(p => talents.HasSpell(p, second.RankSpells[0])).ConfigureAwait(false), "the second-row talent was learned");
            ScenarioContext.ExpectEqual(points - 2, await bot.FreeTalentPointsAsync().ConfigureAwait(false), "free talent points after the refusal");
            TalentLearnOutcome why = await bot.ReadAsync(p => TalentRules.EvaluateLearn(talents.Catalog, spell => talents.HasSpell(p, spell),
                second.Id, 0, talents.FreePoints(p), 1u << ((int)p.Class - 1)).Outcome).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(TalentLearnOutcome.TierLocked, why, "refusal reason");
        }).ConfigureAwait(false);

        await context.StepAsync($"learn rank 3 of talent {first.Id}", async () =>
        {
            await LearnAsync(context, bot, first, 2).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(points - 3, await bot.FreeTalentPointsAsync().ConfigureAwait(false), "free talent points after three ranks");
        }).ConfigureAwait(false);

        await context.StepAsync("stand at the trainer", async () =>
        {
            await context.PlaceAsync(bot, trainer.MapId, trainer.X + 1.5f, trainer.Y, trainer.Z, MathF.PI).ConfigureAwait(false);
            QuestNpcFeature npcs = context.Services.GetRequiredService<QuestNpcFeature>();
            await context.WaitUntilAsync($"{bot.Name} can talk to the trainer", () => bot.Session?.Player is { IsInWorld: true } player
                && player.VisibleObjects.Contains(trainer.Guid)
                && npcs.Services.InteractableNpc(player, trainer.Guid, NpcFlags.Trainer) is { } npc
                && TalentTrainerRules.CanTrainAndResetTalentsOf(player, npc)).ConfigureAwait(false);
        }).ConfigureAwait(false);

        (uint cost, uint money) = await context.StepAsync("the unlearn gossip line asks to confirm the price", async () =>
        {
            uint price = await bot.ReadAsync(talents.ResetCost).ConfigureAwait(false);
            await context.GiveMoneyAsync(bot, price).ConfigureAwait(false);
            long mark = bot.Mark();
            ScenarioContext.Expect(await bot.GossipHelloAsync(trainer.Guid).ConfigureAwait(false), "CMSG_GOSSIP_HELLO not admitted");
            GossipMenuView menu = await bot.WaitForPacketAsync(WorldOpcode.SmsgGossipMessage, ScenarioTalents.GossipMessage,
                m => m.Npc == trainer.Guid.Value, mark).ConfigureAwait(false);

            // A client picks the line by its text; the harness reads which line the server made the unlearn option.
            int line = await context.ReadAsync(() => context.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(bot.RequirePlayer())?.Menu
                .GossipItems.Select((item, i) => (item, i)).FirstOrDefault(t => t.item.OptionId == GossipOption.UnlearnTalents, (null!, -1)).i ?? -1).ConfigureAwait(false);
            ScenarioContext.Expect(line >= 0 && menu.Options.Any(o => o.Index == (uint)line), "the trainer's menu has no unlearn line");

            mark = bot.Mark();
            ScenarioContext.Expect(await bot.GossipSelectAsync(trainer.Guid, (uint)line).ConfigureAwait(false), "CMSG_GOSSIP_SELECT_OPTION not admitted");
            TalentWipeConfirmView confirm = await bot.WaitForPacketAsync(WorldOpcode.MsgTalentWipeConfirm, ScenarioTalents.WipeConfirm, since: mark).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(trainer.Guid.Value, confirm.Trainer, "wipe confirm trainer");
            ScenarioContext.ExpectEqual(price, confirm.Cost, "wipe confirm cost");
            return (confirm.Cost, await bot.ReadAsync(p => p.Money).ConfigureAwait(false));
        }).ConfigureAwait(false);

        RespecState respec = await context.StepAsync("confirming the respec removes the talents, returns the points and takes the price", async () =>
        {
            ScenarioContext.Expect(await bot.WipeConfirmAsync(trainer.Guid).ConfigureAwait(false), "MSG_TALENT_WIPE_CONFIRM not admitted");
            ScenarioContext.ExpectEqual(points, await bot.FreeTalentPointsAsync().ConfigureAwait(false), "free talent points after the respec");
            ScenarioContext.ExpectEqual(money - cost, await bot.ReadAsync(p => p.Money).ConfigureAwait(false), $"{bot.Name} money after the respec");
            ScenarioContext.Expect(await bot.ReadAsync(p => first.RankSpells.Take(3).All(spell => !talents.HasSpell(p, spell))).ConfigureAwait(false),
                "a rank of the first talent is still known");
            return await bot.ReadAsync(p => talents.StateOf(p).Respec).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await context.StepAsync($"learn rank 1 of talent {first.Id} again", () => LearnAsync(context, bot, first, 0)).ConfigureAwait(false);

        await context.StepAsync("after a relog the talent, the points and the respec price are kept", async () =>
        {
            await context.Services.GetRequiredService<ManagedPlayerbotFeature>().StopAsync(bot.BotId.ToString(), context.CancellationToken).ConfigureAwait(false);
            ScenarioBot again = await context.LoginAsync(Bot).ConfigureAwait(false);
            ScenarioContext.Expect(await again.ReadAsync(p => talents.HasSpell(p, first.RankSpells[0])).ConfigureAwait(false), "rank 1 is not known after the relog");
            ScenarioContext.ExpectEqual(points - 1, await again.FreeTalentPointsAsync().ConfigureAwait(false), "free talent points after the relog");
            ScenarioContext.ExpectEqual(respec, await again.ReadAsync(p => talents.StateOf(p).Respec).ConfigureAwait(false), "respec state after the relog");
        }).ConfigureAwait(false);
    }

    /// <summary>Learn one rank and wait for its SMSG_LEARNED_SPELL.</summary>
    private static async Task LearnAsync(ScenarioContext context, ScenarioBot bot, TalentRecord talent, uint rank)
    {
        uint spell = talent.RankSpells[(int)rank];
        long mark = bot.Mark();
        ScenarioContext.Expect(await bot.LearnTalentAsync(talent.Id, rank).ConfigureAwait(false), "CMSG_LEARN_TALENT not admitted");
        await bot.WaitForPacketAsync(WorldOpcode.SmsgLearnedSpell, payload => BitConverter.ToUInt32(payload, 0), s => s == spell, mark).ConfigureAwait(false);
        ScenarioContext.Expect(await context.ReadAsync(() => context.Services.GetRequiredService<TalentFeature>().Service!.HasSpell(bot.RequirePlayer(), spell)).ConfigureAwait(false),
            $"rank {rank + 1} of talent {talent.Id} is not in the book");
    }

    /// <summary>
    /// The first tree of the player's class (tab order) with a first-row talent of at least three ranks and a second-row talent, neither
    /// with a prerequisite; the lowest ids win.
    /// </summary>
    private static (TalentRecord First, TalentRecord Second) PickTalents(TalentCatalog catalog, Player player)
    {
        foreach (TalentTabRecord tab in catalog.TabsForClassMask(1u << ((int)player.Class - 1)))
        {
            IReadOnlyList<TalentRecord> tree = catalog.TalentsOfTab(tab.Id);
            TalentRecord? first = tree.Where(t => t.Row == 0 && t.RankCount >= 3 && t.DependsOn == 0 && t.DependsOnSpell == 0).MinBy(t => t.Id);
            TalentRecord? second = tree.Where(t => t.Row == 1 && t.DependsOn == 0 && t.DependsOnSpell == 0).MinBy(t => t.Id);
            if (first is not null && second is not null)
            {
                return (first, second);
            }
        }

        throw new ScenarioAssertionException($"no tree of class {player.Class} has a first-row talent of three ranks and a second-row talent");
    }

    private readonly record struct TrainerSpot(ObjectGuid Guid, uint MapId, float X, float Y, float Z);

    /// <summary>
    /// The nearest spawn of a class trainer (creature_template trainer_type 0 and the bot's class, with the trainer NPC flag) on the bot's
    /// map, else on the first other map the bots may use. The trainer is checked again where the bot stands next to it.
    /// </summary>
    private static TrainerSpot FindTrainer(ScenarioContext context, Player player)
    {
        CreatureContent? content = context.Services.GetService<CreatureWorldFeature>()?.Content;
        if (content is null)
        {
            throw new ScenarioAssertionException("no creature content is loaded");
        }

        HashSet<uint> entries = [.. content.Templates
            .Where(t => (t.NpcFlags & (uint)NpcFlags.Trainer) != 0 && t.TrainerType == (uint)TrainerType.Class && t.TrainerClass == (byte)player.Class)
            .Select(t => t.Entry)];
        PlayerbotOptions options = context.Services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new PlayerbotOptions();
        foreach (uint map in new[] { player.MapId }.Concat(options.AllowedMaps.Where(m => m != player.MapId)))
        {
            CreatureSpawn? nearest = content.GetSpawns(map).Where(s => entries.Contains(s.Entry))
                .OrderBy(s => map == player.MapId ? ((s.X - player.X) * (s.X - player.X)) + ((s.Y - player.Y) * (s.Y - player.Y)) : 0f)
                .ThenBy(s => s.Guid)
                .FirstOrDefault();
            if (nearest is not null)
            {
                return new TrainerSpot(ObjectGuid.WithEntry(HighGuid.Unit, nearest.Entry, nearest.Guid), map, nearest.X, nearest.Y, nearest.Z);
            }
        }

        throw new ScenarioAssertionException($"no class trainer of class {player.Class} is spawned on a map the bots may use");
    }
}
