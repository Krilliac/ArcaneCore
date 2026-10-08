using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Character;

/// <summary>Operational vmangos commands: UnitCommands.cpp:1034-1051, 2683-2715 and CharacterCommands.cpp:3509-3565.</summary>
public sealed class GmParityCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("die", AccountSecurity.GameMaster, "Syntax: .die\nKill the selected unit or yourself without kill credit.", Die, RetailLevel: 3),
        new ChatCommand("damage", AccountSecurity.GameMaster, "Syntax: .damage #amount\nDeal direct physical damage to the selected unit.", Damage, RetailLevel: 3),
        new ChatCommand("aura", AccountSecurity.Administrator, "Syntax: .aura #spell\nApply a spell's auras to the selected unit or yourself.", Aura, RetailLevel: 4),
        new ChatCommand("additemset", AccountSecurity.GameMaster, "Syntax: .additemset #itemset|#itemset-link\nGive every item in a set to the selected player or yourself.", AddItemSet, RetailLevel: 3),
    ];

    private static bool Die(CommandContext context, string text)
    {
        if (text.Trim().Length != 0)
        {
            return false;
        }

        Unit? target = GmSelectedUnit.Require(context);
        if (target is null)
        {
            return true;
        }

        if (target is Player player)
        {
            if (!context.CanActOn(player))
            {
                return true;
            }

            // HandleDieHelper: a player in god mode (an invincibility threshold) loses it first (Player::SetCheatGod(false)).
            player.InvincibilityHpThreshold = 0;
        }

        // vmangos DieCommandCredit defaults off: the creature's loot recipient is cleared and the unit deals its whole health to
        // itself (no kill credit, no loot, no durability loss; a creature's own death prevention still holds).
        if (target.IsAlive && target.Map is { } map)
        {
            (target as Creature)?.ClearLootRecipient();
            map.Combat.DealDamage(target, target, target.Health, durabilityLoss: false);
        }

        return true;
    }

    private static bool Aura(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32KeyFromLink("Hspell", out uint spellId) || spellId == 0 || !args.IsEmpty)
        {
            return false;
        }

        Unit? target = GmSelectedUnit.Require(context);
        if (target is null || target is Player player && !context.CanActOn(player))
        {
            return true;
        }

        // HandleAuraHelper: an unknown spell is a syntax failure; a spell without an aura effect gets LANG_SPELL_NO_HAVE_AURAS.
        SpellSystem spells = context.Session.Services.GetRequiredService<SpellFeature>().System;
        if (spells.Store.Get(spellId) is null)
        {
            return false;
        }

        if (!spells.AddAura(target, spellId, caster: context.Player))
        {
            context.Reply(SpellNoHaveAuras(spellId));
        }

        return true;
    }

    private static bool Damage(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractInt32(out int amount) || !args.IsEmpty)
        {
            return false;
        }

        if (context.Player.Selection.IsEmpty)
        {
            context.Reply(GmStrings.SelectCharOrCreature);
            return true;
        }

        Unit? target = GmSelectedUnit.Require(context);
        if (target is null || target is Player player && !context.CanActOn(player) || amount <= 0 || !target.IsAlive)
        {
            return true;
        }

        if (target.Map is { } map)
        {
            uint dealt = map.Combat.DealDamage(context.Player, target, (uint)amount, durabilityLoss: false);
            if (!ReferenceEquals(target, context.Player) && dealt > 0)
            {
                context.Session.Send(WorldOpcode.SmsgAttackerstateupdate,
                    CombatPackets.AttackerStateUpdate(HitInfo.AffectsVictim, context.Player.Guid, target.Guid, dealt,
                        [new SubDamage(0, dealt, 0, 0)], VictimState.Normal, 0));
            }
        }

        return true;
    }

    private static bool AddItemSet(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32KeyFromLink("Hitemset", out uint setId) || !args.IsEmpty)
        {
            return false;
        }

        if (setId == 0)
        {
            context.Reply(NoItemsFromItemsetFound(setId));
            return true;
        }

        Player target = context.SelectedPlayerOrSelf() ?? context.Player;
        if (!context.CanActOn(target))
        {
            return true;
        }

        if (target.IsQuestSettlementPending)
        {
            context.Reply("This player's quest reward is still settling.");
            return true;
        }

        if (LiveItemTemplateStore.Unwrap(target.Inventory.Templates) is not { } store)
        {
            context.Reply("Item templates are unavailable.");
            return true;
        }

        ItemTemplate[] pieces = [.. store.All.Where(item => item.SetId == setId).OrderBy(item => item.Entry)];
        if (pieces.Length == 0)
        {
            context.Reply(NoItemsFromItemsetFound(setId));
            return true;
        }

        foreach (ItemTemplate template in pieces)
        {
            var destinations = new List<ItemPosCount>();
            InventoryResult result = target.Inventory.CanStoreNewItem(template.Entry, 1, destinations, out _);
            if (result != InventoryResult.Ok || destinations.Count == 0)
            {
                context.Reply(GmStrings.ItemCannotCreate(template.Entry, 1));
                continue;
            }

            Item item = target.Inventory.StoreNewItem(destinations, template, 1);
            if (ReferenceEquals(target, context.Player))
            {
                foreach (ItemPosCount position in destinations)
                {
                    target.Inventory.GetItem(position.Bag, position.Slot)?.SetBinding(false);
                }
            }

            context.Player.Session.Send(WorldOpcode.SmsgItemPushResult,
                ItemPackets.ItemPushResult(context.Player.Guid, item, 1, received: false, created: true, showInChat: true));
            if (!ReferenceEquals(target, context.Player))
            {
                target.Session.Send(WorldOpcode.SmsgItemPushResult,
                    ItemPackets.ItemPushResult(target.Guid, item, 1, received: true, created: false, showInChat: true));
            }
        }

        return true;
    }

    /// <summary>LANG_NO_ITEMS_FROM_ITEMSET_FOUND (502; mangos-classic mangos.sql:3854): "No items from itemset '%u' found.".</summary>
    internal static string NoItemsFromItemsetFound(uint setId)
        => string.Create(CultureInfo.InvariantCulture, $"No items from itemset '{setId}' found.");

    /// <summary>LANG_SPELL_NO_HAVE_AURAS (1165; mangos-classic mangos.sql:4138): "Spell %u not have auras.".</summary>
    internal static string SpellNoHaveAuras(uint spellId)
        => string.Create(CultureInfo.InvariantCulture, $"Spell {spellId} not have auras.");
}

/// <summary>vmangos UnitCommands.cpp:2245-2278, Chat.cpp:589.</summary>
public sealed class GmParityModifyExtension : ICommandExtension
{
    public string Path => "modify";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("speed", AccountSecurity.Moderator, "Syntax: .modify speed #rate\nSet the selected player's run speed rate.", Speed, RetailLevel: 2),
        new ChatCommand("scale", AccountSecurity.GameMaster, "Syntax: .modify scale #scale\nSet the selected unit's model scale (0 exclusive to 100 inclusive).", Scale, RetailLevel: 3),
    ];

    private static bool Speed(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractFloat(out float rate) || !args.IsEmpty)
        {
            return false;
        }

        if (!float.IsFinite(rate))
        {
            context.Reply(GmStrings.BadValue);
            return true;
        }

        if (context.Commands.Gm.LevelOf(context.Security) < 4 && rate > 4f)
        {
            rate = 4f;
        }

        if (rate < 0.1f || rate > 100f)
        {
            context.Reply(GmStrings.BadValue);
            return true;
        }

        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply(GmStrings.NoCharSelected);
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        string link = GmStrings.PlayerLink(target.Name);
        if (context.Session.Services.GetService<NpcServicesFeature>()?.Flights is { } flights && flights.IsFlying(target))
        {
            context.Reply(CharInFlight(link));
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"You set the speed to {rate:0.00} from normal of {link}."));
        if (!ReferenceEquals(target, context.Player))
        {
            target.SendSystemMessage(string.Create(CultureInfo.InvariantCulture,
                $"{GmStrings.PlayerLink(context.Player.Name)} set your speed to {rate:0.00} from normal."));
        }

        UnitSpeed.SetRate(target, MoveType.Run, rate);
        return true;
    }

    /// <summary>LANG_CHAR_IN_FLIGHT (21; mangos-classic mangos.sql:3443): "%s is flying command failed.".</summary>
    private static string CharInFlight(string link) => $"{link} is flying command failed.";

    private static bool Scale(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractFloat(out float scale) || !args.IsEmpty)
        {
            return false;
        }

        if (!float.IsFinite(scale) || scale <= 0 || scale > 100)
        {
            context.Reply(GmStrings.BadValue);
            return true;
        }

        Unit? target = GmSelectedUnit.Require(context);
        if (target is null || target is Player player && !context.CanActOn(player))
        {
            return true;
        }

        if (target is Player subject)
        {
            // LANG_YOU_CHANGE_SIZE (147) / LANG_YOURS_SIZE_CHANGED (148), mangos-classic mangos.sql:3533-3534; players only, as vmangos.
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"You set the size {scale:0.00} of {GmStrings.PlayerLink(subject.Name)}."));
            if (!ReferenceEquals(subject, context.Player))
            {
                subject.SendSystemMessage(string.Create(CultureInfo.InvariantCulture,
                    $"{GmStrings.PlayerLink(context.Player.Name)} set your size to {scale:0.00}."));
            }
        }

        // Unit::SetObjectScale then Unit::UpdateModelData: with the client model data loaded, the bounding radius, combat reach and
        // collision height are recomputed from the display model (SpellSystem.UpdateDisplayModel); without it they follow the scale's ratio.
        float old = target.GetFloat(UpdateFields.ObjectFieldScaleX);
        target.SetFloat(UpdateFields.ObjectFieldScaleX, scale);
        SpellSystem spells = context.Session.Services.GetRequiredService<SpellFeature>().System;
        if (spells.DisplayModelResolver is not null)
        {
            spells.UpdateDisplayModel(target);
        }
        else
        {
            float ratio = old > 0 ? scale / old : scale;
            target.SetFloat(UpdateFields.UnitFieldBoundingradius, target.GetFloat(UpdateFields.UnitFieldBoundingradius) * ratio);
            target.SetFloat(UpdateFields.UnitFieldCombatreach, target.GetFloat(UpdateFields.UnitFieldCombatreach) * ratio);
            target.Locomotion.CollisionHeight *= ratio;
        }

        return true;
    }
}
