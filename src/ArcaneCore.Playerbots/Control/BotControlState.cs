using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Control;

/// <summary>
/// What <c>.botctl state|quests|inventory|auras|path</c> report about a bot, as JSON (the text form is the same tree flattened). World
/// thread: it reads the live player.
/// </summary>
internal static class BotControlState
{
    internal static string Hex(ulong guid) => "0x" + guid.ToString("X", CultureInfo.InvariantCulture);

    private static float R(float value) => MathF.Round(value, 2);

    internal static (uint Power, uint Max) Power(Player player)
    {
        int power = (int)player.PowerType;
        return power is >= 0 and < 5
            ? (player.GetUInt32(UpdateFields.UnitFieldPower1 + power), player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + power))
            : (0, 0);
    }

    internal static uint Casting(WorldSession session, Player player)
        => session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast?.Spell.Id ?? 0u;

    internal static bool Teleporting(WorldSession session, Player player)
        => session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) ?? false;

    internal static bool HasAura(WorldSession session, Player player, uint spell)
        => session.Services.GetService<SpellFeature>() is { } spells && spells.System.GetAuras(player).Any(h => !h.IsRemoved && h.Spell.Id == spell);

    internal static long ItemCount(Player player, uint entry)
        => player.Inventory.AllItems.Where(i => i.Entry == entry).Sum(i => (long)i.Count);

    internal static JsonObject State(ManagedPlayerbotFeature feature, BotController? controller, Guid botId, WorldSession session, Player player, bool full)
    {
        IServiceProvider services = session.Services;
        SpellFeature? spells = services.GetService<SpellFeature>();
        int power = (int)player.PowerType;
        uint mana = power is >= 0 and < 5 ? player.GetUInt32(UpdateFields.UnitFieldPower1 + power) : 0;
        uint maxMana = power is >= 0 and < 5 ? player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + power) : 0;
        var state = new JsonObject
        {
            ["name"] = player.Name,
            ["botId"] = botId.ToString("D"),
            ["guid"] = Hex(player.Guid.Value),
            ["controlled"] = controller is not null,
            ["mode"] = controller is null ? "autonomous" : controller.Mode.ToString().ToLowerInvariant(),
            ["inWorld"] = player.IsInWorld,
            ["map"] = player.MapId,
            ["zone"] = player.Map is null ? 0u : feature.ZoneAt(player.MapId, new(player.X, player.Y, player.Z)),
            ["x"] = R(player.X),
            ["y"] = R(player.Y),
            ["z"] = R(player.Z),
            ["o"] = R(player.Orientation),
            ["level"] = player.Level,
            ["race"] = player.Race.ToString(),
            ["class"] = player.Class.ToString(),
            ["hp"] = player.Health,
            ["maxHp"] = player.MaxHealth,
            ["hpPct"] = R(player.HealthPercent),
            ["powerType"] = player.PowerType.ToString(),
            ["power"] = mana,
            ["maxPower"] = maxMana,
            ["alive"] = player.IsAlive,
            ["ghost"] = (player.Flags & PlayerFlags.Ghost) != 0,
            ["deathState"] = player.Combat.DeathState.ToString(),
            ["inCombat"] = player.Combat.IsInCombat,
            ["attackers"] = player.Combat.Attackers.Count(a => ReferenceEquals(a.Map, player.Map)),
            ["money"] = player.Money,
            ["moving"] = PlayerbotMotion.IsActive(player),
            ["teleporting"] = services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) ?? false,
            ["casting"] = spells?.System.GetState(player.Guid)?.CurrentCast?.Spell.Id ?? 0u,
            ["standState"] = player.StandState.ToString(),
        };
        if (player.Map?.FindObject(player.Selection) is { } target) state["target"] = Unit(player, target);
        if (player.Combat.Victim is { } victim) state["victim"] = Unit(player, victim);
        if (controller is not null)
        {
            state["path"] = Path(controller, player);
            state["owner"] = controller.Owner;
            IReadOnlyList<BotControlEvent> errors = controller.Events(0, 3, errorsOnly: true);
            if (errors.Count > 0) state["lastErrors"] = new JsonArray([.. errors.Select(e => (JsonNode)(e.Kind + ": " + e.Text))]);
        }
        else if (feature.FindBrain(botId) is { } brain)
        {
            state["goal"] = brain.Goal.ToString();
        }

        if (full)
        {
            state["auras"] = Auras(session, player);
            state["quests"] = Quests(session, player);
            state["inventory"] = Inventory(player);
        }

        return state;
    }

    internal static JsonObject Unit(Player bot, WorldObject obj)
    {
        var json = new JsonObject
        {
            ["guid"] = Hex(obj.Guid.Value),
            ["name"] = BotControlUnits.NameOf(obj) ?? string.Empty,
            ["distance"] = R(PlayerbotGeometry.Distance(bot, obj)),
        };
        if (obj is Creature creature) json["entry"] = creature.Entry;
        if (obj is ArcaneCore.Game.GameObjects.GameObject go) json["entry"] = go.Entry;
        if (obj is Unit unit)
        {
            json["hpPct"] = R(unit.HealthPercent);
            json["alive"] = unit.IsAlive;
            json["level"] = unit.Level;
        }

        return json;
    }

    internal static JsonObject Path(BotController controller, Player player)
    {
        var json = new JsonObject
        {
            ["task"] = controller.MotionKind.ToString().ToLowerInvariant(),
            ["state"] = controller.MotionState.ToString().ToLowerInvariant(),
            ["walking"] = controller.HasRoute,
            ["planFailures"] = controller.PlanFailures,
            ["generation"] = controller.MotionGeneration,
        };
        if (controller.MotionDetail is { } detail) json["detail"] = detail;
        if (controller.MotionKind == BotMotionKind.Move || controller.MotionState is BotMotionState.Arrived or BotMotionState.Failed)
        {
            json["destination"] = new JsonArray(R(controller.Destination.X), R(controller.Destination.Y), R(controller.Destination.Z));
            json["distance"] = R(System.Numerics.Vector2.Distance(new(player.X, player.Y), new(controller.Destination.X, controller.Destination.Y)));
        }

        if (controller.MotionKind == BotMotionKind.Follow) json["follow"] = Hex(controller.FollowTarget.Value);
        return json;
    }

    internal static JsonArray Auras(WorldSession session, Player player)
    {
        var list = new JsonArray();
        if (session.Services.GetService<SpellFeature>() is not { } spells) return list;
        foreach (SpellAuraHolder holder in spells.System.GetAuras(player))
        {
            if (holder.IsRemoved) continue;
            list.Add(new JsonObject
            {
                ["spell"] = holder.Spell.Id,
                ["stacks"] = holder.StackAmount,
                ["durationMs"] = holder.Duration,
                ["positive"] = holder.IsPositive,
                ["caster"] = Hex(holder.CasterGuid.Value),
            });
        }

        return list;
    }

    internal static JsonArray Quests(WorldSession session, Player player)
    {
        var list = new JsonArray();
        if (session.Services.GetService<QuestNpcFeature>()?.Services is not { } npcs || npcs.StateOf(player) is not { } state) return list;
        PlayerQuestLog log = state.Quests;
        for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            uint id = log.SlotQuestId(slot);
            if (id == 0) continue;
            QuestStatusData? data = log.Get(id);
            list.Add(new JsonObject
            {
                ["id"] = id,
                ["slot"] = slot,
                ["title"] = npcs.Quests.Get(id)?.Title ?? string.Empty,
                ["status"] = data?.Status.ToString() ?? "None",
                ["kills"] = new JsonArray([.. (data?.CreatureOrGOCount ?? []).Select(c => (JsonNode)c)]),
                ["items"] = new JsonArray([.. (data?.ItemCount ?? []).Select(c => (JsonNode)c)]),
            });
        }

        return list;
    }

    /// <summary>The status of one quest (None, Incomplete, Complete, Failed ...; "Rewarded" when turned in).</summary>
    internal static string QuestStatusOf(WorldSession session, Player player, uint questId)
    {
        if (session.Services.GetService<QuestNpcFeature>()?.Services is not { } npcs || npcs.StateOf(player) is not { } state) return "Unknown";
        QuestStatusData? data = state.Quests.Get(questId);
        if (data is null) return "None";
        return data.Rewarded && state.Quests.FindSlot(questId) >= QuestConstants.MaxQuestLogSize ? "Rewarded" : data.Status.ToString();
    }

    internal static JsonArray Inventory(Player player)
    {
        var list = new JsonArray();
        foreach (Item item in player.Inventory.AllItems)
        {
            list.Add(new JsonObject
            {
                ["entry"] = item.Entry,
                ["name"] = item.Template.Name,
                ["count"] = item.Count,
                ["bag"] = item.BagSlot,
                ["slot"] = item.Slot,
                ["guid"] = Hex(item.Guid.Value),
            });
        }

        return list;
    }

    /// <summary>The first carried item of <paramref name="entry"/> (bags before the equipped slots), or null.</summary>
    internal static Item? FindItem(Player player, uint entry)
        => player.Inventory.AllItems.Where(i => i.Entry == entry)
            .OrderBy(i => i.BagSlot == InventorySlots.Bag0 && i.Slot < InventorySlots.EquipmentEnd ? 1 : 0).FirstOrDefault();

    internal static JsonObject Menu(WorldSession session, Player player)
    {
        var json = new JsonObject();
        if (session.Services.GetService<QuestNpcFeature>()?.Services is not { } npcs || npcs.StateOf(player) is not { } state) return json;
        json["menuId"] = state.Menu.MenuId;
        json["gossip"] = new JsonArray([.. state.Menu.GossipItems.Select((g, i) => (JsonNode)new JsonObject { ["index"] = i, ["text"] = g.Message, ["option"] = g.OptionId.ToString() })]);
        json["quests"] = new JsonArray([.. state.Menu.QuestItems.Select(q => (JsonNode)new JsonObject { ["id"] = q.QuestId, ["icon"] = q.Icon.ToString() })]);
        return json;
    }
}