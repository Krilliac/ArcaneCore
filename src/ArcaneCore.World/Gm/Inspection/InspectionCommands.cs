using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Inspection;

/// <summary>
/// Read-only GM inspection commands for the selected world object. <c>.pinfo</c> is the GM audit lane's (<see cref="Audit.AuditCommands"/>,
/// vmangos HandlePInfoCommand with the account, level, position and chat state) and <c>.guid</c> the lookup lane's
/// (<see cref="Lookup.GuidCommand"/>, vmangos HandleGUIDCommand: the selection only, "No selection." without one).
/// </summary>
public sealed class InspectionCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("distance", AccountSecurity.GameMaster, "Syntax: .distance\nDisplay the 3D distance to the selected object.", Distance),
        new ChatCommand("angle", AccountSecurity.GameMaster, "Syntax: .angle\nDisplay the angle to the selected object.", Angle),
    ];

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
