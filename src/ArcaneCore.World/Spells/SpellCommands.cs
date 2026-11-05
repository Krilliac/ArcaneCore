using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Npc;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// GM spell commands (vmangos Level2/Level3 .learn, .unlearn, .cast, .unaura, .cooldown). Each
/// acts on the selected player, or on the invoker when nothing is selected. The security levels
/// follow vmangos command table: learn/unlearn/unaura are GameMaster; cast/cooldown are Administrator.
/// </summary>
public sealed class SpellCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("learn", AccountSecurity.Administrator, "Syntax: .learn #spell — teach a spell to the selected player or yourself.", Learn, RetailLevel: 5),
        new ChatCommand("unlearn", AccountSecurity.GameMaster, "Syntax: .unlearn #spell — make the selected player or yourself forget a spell.", Unlearn),
        new ChatCommand("cast", AccountSecurity.Administrator, "Syntax: .cast #spell — cast a spell (triggered) on the selected player or yourself. Variants: .cast self, .cast target, .cast back, .cast dist.", Cast, Children:
        [
            // vmangos castCommandTable (Chat.cpp:195-203): back, dist, self and target, all SEC_DEVELOPER (retail level 5, as the .cast entry itself). No RetailLevel is declared
            // here: RetailCommandLevels.Apply gives the level 5 (entries "cast self" etc.), and with World:GmCommands:RetailLevels off the stored Administrator
            // security applies to .cast and to each child alike.
            new ChatCommand("self", AccountSecurity.Administrator, "Syntax: .cast self #spell [triggered]\nMake the selected unit cast a spell on itself.", CastSelf),
            new ChatCommand("target", AccountSecurity.Administrator, "Syntax: .cast target #spell [triggered]\nMake the selected unit cast a spell on its victim.", CastTarget),
            new ChatCommand("back", AccountSecurity.Administrator, "Syntax: .cast back #spell [triggered]\nMake the selected unit cast a spell on you.", CastBack),
            new ChatCommand("dist", AccountSecurity.Administrator, "Syntax: .cast dist #spell #distance [triggered]\nCast a spell at a point the given distance in front of you.", CastDist),
        ]),
        new ChatCommand("unaura", AccountSecurity.GameMaster, "Syntax: .unaura #spell|all — remove auras from the selected player or yourself.", Unaura),
        new ChatCommand("cooldown", AccountSecurity.Administrator, "Syntax: .cooldown [#spell] — clear one or every spell cooldown of the selected player or yourself.", Cooldown, Children:
        [
            // vmangos cooldownCommandTable (Chat.cpp:281-286): list, clear and clearclientside, SEC_GAMEMASTER.
            new ChatCommand("list", AccountSecurity.GameMaster, "Syntax: .cooldown list\nList the running cooldowns of the selected player or yourself.", CooldownList, RetailLevel: 3),
            new ChatCommand("clear", AccountSecurity.GameMaster, "Syntax: .cooldown clear [#spell]\nClear one or every spell cooldown of the selected player or yourself.", Cooldown, RetailLevel: 3),
            new ChatCommand("clearclientside", AccountSecurity.GameMaster, "Syntax: .cooldown clearclientside\nClear every spell cooldown of the selected player or yourself and tell its client.", CooldownClearAll, RetailLevel: 3),
        ]),
    ];

    private static SpellFeature Feature(CommandContext context) => context.Session.Services.GetRequiredService<SpellFeature>();

    private static bool TryParseSpell(string args, out uint spellId)
        => uint.TryParse(args.Trim().TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out spellId);

    private static Player? Target(CommandContext context)
    {
        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply("No player selected.");
            return null;
        }

        return context.CanActOn(target) ? target : null;
    }

    private static bool Learn(CommandContext context, string args)
    {
        if (!TryParseSpell(args, out uint spellId))
        {
            return false;
        }

        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        SpellFeature feature = Feature(context);
        if (feature.System.Store.Get(spellId) is null)
        {
            context.Reply($"Spell {spellId} does not exist.");
            return true;
        }

        context.Reply(feature.System.LearnSpell(target, spellId)
            ? $"{target.Name} learned spell {spellId}."
            : $"{target.Name} already knows spell {spellId}.");
        return true;
    }

    private static bool Unlearn(CommandContext context, string args)
    {
        if (!TryParseSpell(args, out uint spellId))
        {
            return false;
        }

        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        SpellFeature feature = Feature(context);
        if (!target.CanMutateQuestSettlementState)
        {
            context.Reply($"{target.Name} is settling a quest reward; try again in a moment.");
            return true;
        }

        if (!feature.Spellbook.ForgetSpell(target, spellId))
        {
            context.Reply($"{target.Name} does not know spell {spellId}.");
            return true;
        }

        // vmangos Player::removeSpell: SMSG_REMOVED_SPELL, and passive auras go with the spell.
        target.Session.Send(WorldOpcode.SmsgRemovedSpell, SpellPackets.BuildRemovedSpell(spellId));
        feature.System.RemoveAuras(target, spellId);
        context.Reply($"{target.Name} forgot spell {spellId}.");
        return true;
    }

    private static bool Cast(CommandContext context, string args)
    {
        // A shift-click link is taken like a number; a trailing "triggered" is accepted, and the cast is always triggered (this command never cast anything else).
        if (!TryParseCastArgs(context, args, withDistance: false, out uint spellId, out _, out _))
        {
            return false;
        }

        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        // An unknown spell keeps the cast result (NotFound); only a known but broken one is refused here.
        if (Feature(context).System.Store.Get(spellId) is not null && !SpellIsValid(context, spellId))
        {
            return true;
        }

        SpellCastTargets targets = context.IsInvoker(target)
            ? SpellCastTargets.ForSelf()
            : SpellCastTargets.ForUnit(target.Guid);
        SpellCastResult result = Feature(context).System.CastSpell(context.Player, spellId, targets, triggered: true);
        context.Reply(result == SpellCastResult.CastOk ? $"Cast spell {spellId}." : $"Spell {spellId} failed: {result}.");
        return true;
    }

    /// <summary>
    /// vmangos ExtractSpellIdFromLink (a plain number with an optional leading #, or a spell, enchant or talent shift-click link), ExtractFloat and
    /// ExtractLiteralArg "triggered" (UnitCommands.cpp:1212-1385): a spell id, an optional distance, then an optional <c>triggered</c> word. As in vmangos,
    /// anything after the <c>triggered</c> word is ignored; without that word nothing may follow. False on any syntax error.
    /// </summary>
    private static bool TryParseCastArgs(CommandContext context, string text, bool withDistance, out uint spellId, out float distance, out bool triggered)
    {
        distance = 0f;
        triggered = false;
        var args = new CommandArgs(text.Trim().TrimStart('#'));
        TalentCatalog? talents = context.Session.Services.GetService<TalentFeature>()?.Catalog;
        if (!args.ExtractSpellIdFromLink(talents, out spellId))
        {
            return false;
        }

        if (withDistance && (!args.ExtractFloat(out distance) || !float.IsFinite(distance)))
        {
            return false;
        }

        triggered = args.ExtractLiteral("triggered") is not null;
        return triggered || args.IsEmpty;
    }

    /// <summary>The selected unit, or null after replying (vmangos GetSelectedUnit, LANG_SELECT_CHAR_OR_CREATURE; a selected player must be actable on).</summary>
    private static Unit? SelectedUnit(CommandContext context)
    {
        Player player = context.Player;
        Unit? unit = player.Selection.IsEmpty ? null : player.Map?.Combat.FindUnit(player.Selection);
        if (unit is null)
        {
            context.Reply("Select a character or creature.");
            return null;
        }

        return unit is Player selectedPlayer && !context.CanActOn(selectedPlayer) ? null : unit;
    }

    private static void ReplyCast(CommandContext context, uint spellId, SpellCastResult result)
        => context.Reply(result == SpellCastResult.CastOk ? $"Cast spell {spellId}." : $"Spell {spellId} failed: {result}.");

    private static int s_facingSplineId = 0x4000_0000;

    /// <summary>
    /// The unit turns to the invoker before it casts (vmangos Unit::SetFacingToObject, Unit.cpp:2796-2804, which runs on any Unit but only while it is stopped, and
    /// Unit::SetFacingTo, Unit.cpp:2785-2794: the orientation, and a facing spline with no path that SMSG_MONSTER_MOVE carries to the observers). vmangos
    /// Unit::IsStopped (Unit.h:1401) is !HasUnitState(UNIT_STATE_MOVING), and UNIT_STATE_MOVING (UnitDefines.h:407) is set only by the roaming, chase, follow and
    /// fleeing motion generators, never by a player's own client-driven walking: so a player walking under client control counts as stopped and does turn. A
    /// creature is stopped when it has no spline. Two limits of this server, with no vmangos counterpart: a player in taxi flight is left alone (its flight
    /// spline is held by the client, and a one-point spline would cancel it), and so is a player on a transport (vmangos sends SMSG_MONSTER_MOVE_TRANSPORT there,
    /// with transport-local coordinates, which this command does not build; the creature SetFacingTo has the same gap). A player's own client receives the
    /// spline too.
    /// </summary>
    private static void FaceInvoker(CommandContext context, Unit caster)
    {
        Player invoker = context.Player;
        float angle = Creature.NormalizeOrientation(MathF.Atan2(invoker.Y - caster.Y, invoker.X - caster.X));
        if (caster is Creature { IsMoving: false } creature)
        {
            if (GmNpcCommands.SystemOf(context) is { } system)
            {
                system.SetFacingTo(creature, angle);
            }

            return;
        }

        if (caster is Player { Map: { } map } player && (player.UnitFlags & ArcaneCore.Game.UnitFlags.TaxiFlight) == 0
            && !player.Movement.HasFlag(MovementFlags.OnTransport))
        {
            player.Orientation = angle;
            uint id = unchecked((uint)Interlocked.Increment(ref s_facingSplineId));
            // Duration 1 ms: vmangos MoveSpline.cpp:122,127 (minimal_duration 1) and :164-167 (a zero-length spline is forced to length 1); the creature path writes 1 too.
            byte[] packet = CreatureMovePackets.BuildPath(player.Guid, new Vector3(player.X, player.Y, player.Z), id, SplineFacing.ToAngle(angle),
                run: !player.Movement.HasFlag(MovementFlags.WalkMode), durationMs: 1, [new Vector3(player.X, player.Y, player.Z)]);
            player.Session.Send(WorldOpcode.SmsgMonsterMove, packet);
            map.BroadcastToObservers(player, WorldOpcode.SmsgMonsterMove, packet);
        }
    }

    /// <summary>
    /// vmangos SpellMgr::IsSpellValid (SpellMgr.cpp:2298), checked by cast, cast self and cast dist (LANG_COMMAND_SPELL_BROKEN, an error rather than a syntax
    /// reply): a spell that creates, needs or teaches an item or spell that does not exist is refused with a reply. The text is mangos_string 483
    /// (LANG_COMMAND_SPELL_BROKEN, "Spell %u broken and not allowed to cast or learn!"), from mangos-classic sql/base/mangos.sql:3835.
    /// </summary>
    private static bool SpellIsValid(CommandContext context, uint spellId)
    {
        if (SpellVariants.IsSpellValid(Feature(context).System, SpellVariants.LoadedItems(context), spellId))
        {
            return true;
        }

        context.Fail("spell-broken");
        context.Reply($"Spell {spellId} broken and not allowed to cast or learn!");
        return false;
    }

    private static bool SpellExists(CommandContext context, uint spellId)
    {
        if (Feature(context).System.Store.Get(spellId) is not null)
        {
            return true;
        }

        context.Reply($"Spell {spellId} does not exist.");
        return false;
    }

    private static bool CastSelf(CommandContext context, string args)
    {
        if (!TryParseCastArgs(context, args, withDistance: false, out uint spellId, out _, out bool triggered))
        {
            return false;
        }

        if (SelectedUnit(context) is not { } caster)
        {
            return true;
        }

        if (SpellExists(context, spellId) && SpellIsValid(context, spellId))
        {
            ReplyCast(context, spellId, Feature(context).System.CastSpell(caster, spellId, SpellCastTargets.ForSelf(), triggered));
        }

        return true;
    }

    private static bool CastTarget(CommandContext context, string args)
    {
        if (!TryParseCastArgs(context, args, withDistance: false, out uint spellId, out _, out bool triggered))
        {
            return false;
        }

        if (SelectedUnit(context) is not { } caster)
        {
            return true;
        }

        if (caster.Combat.Victim is not { } victim)
        {
            context.Reply("The selected unit has no victim.");
            return true;
        }

        // vmangos has no check here; this repo is stricter (GM.LowerSecurity): a spell may not be forced onto a player the invoker cannot act on.
        if (victim is Player victimPlayer && !context.CanActOn(victimPlayer))
        {
            return true;
        }

        if (SpellExists(context, spellId))
        {
            FaceInvoker(context, caster);
            ReplyCast(context, spellId, Feature(context).System.CastSpell(caster, spellId, SpellCastTargets.ForUnit(victim.Guid), triggered));
        }

        return true;
    }

    private static bool CastBack(CommandContext context, string args)
    {
        if (!TryParseCastArgs(context, args, withDistance: false, out uint spellId, out _, out bool triggered))
        {
            return false;
        }

        if (SelectedUnit(context) is not { } caster)
        {
            return true;
        }

        if (SpellExists(context, spellId))
        {
            FaceInvoker(context, caster);
            ReplyCast(context, spellId, Feature(context).System.CastSpell(caster, spellId, SpellCastTargets.ForUnit(context.Player.Guid), triggered));
        }

        return true;
    }

    private static bool CastDist(CommandContext context, string args)
    {
        if (!TryParseCastArgs(context, args, withDistance: true, out uint spellId, out float distance, out bool triggered))
        {
            return false;
        }

        if (!SpellExists(context, spellId) || !SpellIsValid(context, spellId))
        {
            return true;
        }

        Player player = context.Player;
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = ClosePoint(player, distance) };
        ReplyCast(context, spellId, Feature(context).System.CastSpell(player, spellId, targets, triggered));
        return true;
    }

    /// <summary>
    /// vmangos GetClosePoint(x, y, z, dist) (Object.h:520, Object.cpp:2742-2783): the point <paramref name="distance"/> yards in front of the unit plus the unit's own
    /// bounding radius (GetNearPoint2DAroundPosition adds it), with z moved onto the ground there plus 0.05 (UpdateGroundPositionZ, Object.cpp:2079) when a height is
    /// known; otherwise the unit's own z. Not ported: vmangos' optional position-collision search (CONFIG_BOOL_DETECT_POS_COLLISION) and its
    /// MaNGOS::NormalizeMapCoord clamp (Object.cpp:2726-2733), so a huge distance gives a point off the map (the terrain lookup then knows no height).
    /// </summary>
    internal static (float X, float Y, float Z) ClosePoint(Player player, float distance)
        => ClosePoint(player.X, player.Y, player.Z, player.Orientation, player.BoundingRadius, distance,
            player.Map is { } map ? new Func<float, float, float, float>((px, py, pz) => map.Collision.GetHeight(px, py, pz)) : null);

    /// <summary>The close point from explicit values; <paramref name="height"/> is the ground lookup (null: none), whose answer is used when it is a real height.</summary>
    internal static (float X, float Y, float Z) ClosePoint(
        float x, float y, float z, float orientation, float radius, float distance, Func<float, float, float, float>? height)
    {
        (x, y, z) = PointInFront(x, y, z, orientation, distance + radius);
        if (height is not null)
        {
            float ground = height(x, y, z);
            if (float.IsFinite(ground) && ground > TerrainTile.InvalidHeight)
            {
                z = ground + 0.05f;
            }
        }

        return (x, y, z);
    }

    /// <summary>The point <paramref name="distance"/> yards ahead of (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) facing <paramref name="orientation"/> (radians); the height is kept.</summary>
    internal static (float X, float Y, float Z) PointInFront(float x, float y, float z, float orientation, float distance)
        => (x + (MathF.Cos(orientation) * distance), y + (MathF.Sin(orientation) * distance), z);

    private static bool Unaura(CommandContext context, string args)
    {
        Player? target;
        SpellSystem system;
        if (args.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            target = Target(context);
            if (target is null)
            {
                return true;
            }

            system = Feature(context).System;
            foreach (uint spellId in system.GetAuras(target).Select(h => h.Spell.Id).Distinct().ToArray())
            {
                system.RemoveAuras(target, spellId);
            }

            context.Reply($"Removed every aura of {target.Name}.");
            return true;
        }

        if (!TryParseSpell(args, out uint id))
        {
            return false;
        }

        target = Target(context);
        if (target is null)
        {
            return true;
        }

        system = Feature(context).System;
        bool had = system.HasAura(target, id);
        system.RemoveAuras(target, id);
        context.Reply(had ? $"Removed aura {id} from {target.Name}." : $"{target.Name} has no aura {id}.");
        return true;
    }

    private static bool Cooldown(CommandContext context, string args)
    {
        uint spellId = 0;
        if (args.Trim().Length > 0 && !TryParseSpell(args, out spellId))
        {
            return false;
        }

        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        SpellSystem system = Feature(context).System;
        uint[] spells = spellId != 0 ? [spellId] : [.. system.GetActiveCooldowns(target).Select(c => c.SpellId)];
        foreach (uint id in spells)
        {
            system.ClearCooldown(target, id);
        }

        context.Reply(spellId != 0 ? $"Cleared the cooldown of spell {spellId} for {target.Name}." : $"Cleared {spells.Length} cooldowns for {target.Name}.");
        return true;
    }

    // vmangos SpellCaster::PrintCooldownList (SpellCaster.cpp:2567): one line per running spell and category cooldown.
    private static bool CooldownList(CommandContext context, string args)
    {
        if (args.Trim().Length > 0)
        {
            return false;
        }

        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        SpellSystem system = Feature(context).System;
        IReadOnlyList<InitialSpellCooldown> cooldowns = system.GetActiveCooldowns(target);
        foreach (InitialSpellCooldown cooldown in cooldowns.OrderBy(c => c.SpellId))
        {
            string name = system.Store.Get(cooldown.SpellId)?.Name ?? "?";
            string line = string.Create(CultureInfo.InvariantCulture, $"Spell {cooldown.SpellId} ({name}) has {Duration(cooldown.CooldownMs)} cd");
            if (cooldown.Category != 0 && cooldown.CategoryCooldownMs > 0)
            {
                line += string.Create(CultureInfo.InvariantCulture, $", category {cooldown.Category} has {Duration(cooldown.CategoryCooldownMs)} cd");
            }

            context.Reply(line);
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"{target.Name} has {cooldowns.Count} cooldowns."));
        return true;
    }

    private static bool CooldownClearAll(CommandContext context, string args)
        => args.Trim().Length == 0 && Cooldown(context, string.Empty);

    // vmangos ConvertMillisecondToStr: hours, minutes, seconds and milliseconds, leading zero parts left out.
    internal static string Duration(uint ms)
    {
        var time = TimeSpan.FromMilliseconds(ms);
        var parts = new List<string>();
        if (time.Hours + (time.Days * 24) > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{time.Hours + (time.Days * 24)}h"));
        if (time.Minutes > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}m"));
        if (time.Seconds > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{time.Seconds}s"));
        if (time.Milliseconds > 0 || parts.Count == 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{time.Milliseconds}ms"));
        return string.Join(' ', parts);
    }
}
