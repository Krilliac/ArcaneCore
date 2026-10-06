using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Inspection;

/// <summary>Read-only GM inspection commands for the selected world object/player.</summary>
public sealed class InspectionCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("guid", AccountSecurity.GameMaster, "Syntax: .guid\nDisplay the selected object's GUID and position.", Guid),
        new ChatCommand("distance", AccountSecurity.GameMaster, "Syntax: .distance\nDisplay the 3D distance to the selected object.", Distance),
        new ChatCommand("angle", AccountSecurity.GameMaster, "Syntax: .angle\nDisplay the angle to the selected object.", Angle),
        new ChatCommand("pinfo", AccountSecurity.GameMaster, "Syntax: .pinfo\nDisplay selected player state.", PlayerInfo),
    ];

    private static bool Guid(CommandContext context, string args)
    {
        if (args.Length != 0) return false;
        if (!TryObject(context, out WorldObject? target)) return true;
        if (!Finite(target)) { context.Reply(GmStrings.BadValue); return true; }
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"GUID {target.Guid.Value}, type {TypeName(target)}, map {target.MapId}, X {target.X:F3}, Y {target.Y:F3}, Z {target.Z:F3}"));
        return true;
    }

    private static bool Distance(CommandContext context, string args)
    {
        if (args.Length != 0) return false;
        if (!TryObject(context, out WorldObject? target)) return true;
        if (!Finite(target) || !Finite(context.Player) || target.MapId != context.Player.MapId) { context.Reply(GmStrings.BadValue); return true; }
        float distance = MathF.Sqrt(MathF.Pow(target.X - context.Player.X, 2) + MathF.Pow(target.Y - context.Player.Y, 2) + MathF.Pow(target.Z - context.Player.Z, 2));
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Distance {distance:F3}"));
        return true;
    }

    private static bool Angle(CommandContext context, string args)
    {
        if (args.Length != 0) return false;
        if (!TryObject(context, out WorldObject? target)) return true;
        if (!Finite(target) || !Finite(context.Player) || target.MapId != context.Player.MapId) { context.Reply(GmStrings.BadValue); return true; }
        float angle = MathF.Atan2(target.Y - context.Player.Y, target.X - context.Player.X);
        if (angle < 0) angle += MathF.Tau;
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Angle {angle:F3}"));
        return true;
    }

    private static bool PlayerInfo(CommandContext context, string args)
    {
        if (args.Length != 0) return false;
        Player? target = context.SelectedPlayerOrSelf();
        if (target is null) { context.Reply(GmStrings.NoCharSelected); return true; }
        if (!context.CanActOn(target)) return true;
        if (!Finite(target)) { context.Reply(GmStrings.BadValue); return true; }
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"GUID {target.Guid.Value}, type player, map {target.MapId}, zone {target.ZoneId}, level {target.Level}, health {target.Health}/{target.MaxHealth}, power {target.PowerType} {SpellSystem.GetPower(target, target.PowerType)}"));
        return true;
    }

    private static bool TryObject(CommandContext context, [NotNullWhen(true)] out WorldObject? target)
    {
        target = context.Player.Selection.IsEmpty ? context.Player : context.Player.Map?.FindObject(context.Player.Selection);
        if (target is null)
        {
            context.Reply("No object selected.");
            return false;
        }

        return target is not Player player || context.CanActOn(player);
    }

    private static bool Finite(WorldObject target)
        => float.IsFinite(target.X) && float.IsFinite(target.Y) && float.IsFinite(target.Z) && float.IsFinite(target.Orientation);

    private static string TypeName(WorldObject target) => target switch
    {
        Player => "player",
        ArcaneCore.Game.Creatures.Creature => "creature",
        ArcaneCore.Game.GameObjects.GameObject => "gameobject",
        _ => "object",
    };
}
