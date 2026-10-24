using System.Reflection;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>
/// Plan 1.5: every debug write is audited. A registered command whose last word is a write verb must be handled by a type that calls
/// <see cref="DebugWrite.Record"/>, so a new write cannot slip through. The commands that predate the DebugWriteLog are listed in
/// <see cref="Grandfathered"/>; the list may only shrink (a type that starts recording must leave it, see the stale-entry test).
/// </summary>
public sealed class DebugWriteEnforcementTests
{
    /// <summary>The last word of a command path that makes it a write (plan 1.2 verbs).</summary>
    internal static readonly string[] WriteVerbs =
        ["set", "reset", "add", "remove", "clear", "on", "off", "start", "stop", "pause", "resume", "step", "flush", "retry", "recheck", "inject"];

    private static readonly MethodInfo[] RecordMethods =
        [.. typeof(DebugWrite).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == nameof(DebugWrite.Record))];

    /// <summary>Handler types (outermost) of write-verb commands that existed before the DebugWriteLog. New types never go here.</summary>
    private static readonly HashSet<string> Grandfathered = new(StringComparer.Ordinal)
    {
        "ArcaneCore.World.AntiCheat.AntiCheatCommands",
        "ArcaneCore.World.AntiCheat.PacketHeuristics.PacketHeuristicsCommands",
        "ArcaneCore.World.Battlegrounds.BattlegroundCommands",
        "ArcaneCore.World.Creatures.CreatureCommands",
        "ArcaneCore.World.Gm.DebugDraw.DebugDrawCommands",
        "ArcaneCore.World.Gm.LogonSecurity.AccountLoginSecurityCommands",
        "ArcaneCore.World.Gm.Manipulate.AiCommands",
        "ArcaneCore.World.Gm.Objects.GmObjectCommands",
        "ArcaneCore.World.Gm.Quest.QuestCommands",
        "ArcaneCore.World.Honor.HonorCommands",
        "ArcaneCore.World.Locomotion.MovementRateCommands",
        "ArcaneCore.World.Ops.Diagnostics.DiagnosticsCommands",
        "ArcaneCore.World.Playerbots.Chaos.PlayerbotChaosCommands",
        "ArcaneCore.World.Playerbots.Commands.PlayerbotExtraCommands",
        "ArcaneCore.World.Playerbots.Control.BotControlCommands",
        "ArcaneCore.World.Playerbots.PlayerbotCommands",
        "ArcaneCore.World.Playerbots.PlayerbotFleetCommands",
        "ArcaneCore.World.Playerbots.Qa.PlayerbotQaCommands",
        "ArcaneCore.World.Rates.RateCommands",
        "ArcaneCore.World.Spells.SpellCommands",
        "ArcaneCore.World.Teleport.TeleportCommands",
        "ArcaneCore.World.WorldState.GameEventCommands",
    };

    internal static IEnumerable<(string Path, ChatCommand Command)> WriteCommands(IReadOnlyList<ChatCommand> roots, string prefix = "")
    {
        foreach (ChatCommand command in roots)
        {
            string path = prefix.Length == 0 ? command.Name : prefix + " " + command.Name;
            if (command.Handler is not null && WriteVerbs.Contains(command.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return (path, command);
            }

            foreach ((string, ChatCommand) child in WriteCommands(command.SubCommands, path))
            {
                yield return child;
            }
        }
    }

    internal static Type? HandlerType(ChatCommand command)
    {
        Type? type = command.Handler?.Method.DeclaringType;
        while (type is { IsNested: true }) type = type.DeclaringType;
        return type;
    }

    /// <summary>Whether any method of <paramref name="type"/> (or a type nested in it, which holds the lambdas) calls <see cref="DebugWrite.Record"/>.</summary>
    internal static bool Records(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            if (CallsRecord(method)) return true;
        }

        return type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Any(Records);
    }

    private static bool CallsRecord(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) return false;
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } ? method.DeclaringType.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F)) continue; // call, callvirt
            int token = BitConverter.ToInt32(il, i + 1);
            try
            {
                MethodBase? target = method.Module.ResolveMethod(token, typeArguments, methodArguments);
                if (target is not null && RecordMethods.Any(r => r.MetadataToken == target.MetadataToken && r.Module == target.Module)) return true;
            }
            catch (Exception e) when (e is ArgumentException or BadImageFormatException)
            {
                // Not a method token: the byte was an operand of another instruction.
            }
        }

        return false;
    }

    private static string Describe(IEnumerable<(string Path, ChatCommand Command)> found)
        => string.Join("\n", found.Select(f => $"  .{f.Path}  ({HandlerType(f.Command)?.FullName})"));

    [Fact]
    public void EveryRegisteredWriteVerbCommand_RecordsADebugWrite_OrIsGrandfathered()
    {
        CommandTable table = ChatCommands.CreateTable();
        (string Path, ChatCommand Command)[] writers = [.. WriteCommands(table.Roots)];
        Assert.True(writers.Length > 10, "the write-verb commands must be enumerated; found " + writers.Length);

        (string Path, ChatCommand Command)[] violations =
            [.. writers.Where(w => HandlerType(w.Command) is not { } type || (!Records(type) && !Grandfathered.Contains(type.FullName!)))];

        Assert.True(violations.Length == 0,
            "write-verb commands must call DebugWrite.Record (plan 1.5):\n" + Describe(violations));
    }

    [Fact]
    public void TheGrandfatherList_HasNoStaleEntries()
    {
        CommandTable table = ChatCommands.CreateTable();
        (string Path, ChatCommand Command)[] writers = [.. WriteCommands(table.Roots)];
        string[] stale = [.. Grandfathered.Where(name => !writers.Any(w => HandlerType(w.Command)?.FullName == name && !Records(HandlerType(w.Command)!)))];

        Assert.True(stale.Length == 0, "types that no longer need the grandfather entry (they record, or have no write-verb command): " + string.Join(", ", stale));
    }

    // ---- the checker itself ----

    [Fact]
    public void TheChecker_AcceptsAHandlerThatRecords_AndFlagsOneThatDoesNot()
    {
        Assert.True(Records(typeof(Compliant)));
        Assert.False(Records(typeof(Violating)));
        Assert.True(Records(HandlerType(CompliantByLambda.Create())!), "a lambda lives in a nested closure class of its type");
        Assert.False(Records(HandlerType(ViolatingByLambda.Create())!));
    }

    [Fact]
    public void TheEnumerator_FindsWriteVerbLeavesAtAnyDepth_AndIgnoresReadsAndGroups()
    {
        ChatCommand tree = new("debug", AccountSecurity.GameMaster, "g", null,
        [
            new ChatCommand("show", AccountSecurity.GameMaster, "r", Violating.Handle),
            new ChatCommand("speed", AccountSecurity.GameMaster, "g", null,
            [
                new ChatCommand("SET", AccountSecurity.GameMaster, "w", Compliant.Handle),
                new ChatCommand("reset", AccountSecurity.GameMaster, "w", Violating.Handle),
            ]),
            new ChatCommand("set", AccountSecurity.GameMaster, "group without handler", null, [new ChatCommand("info", AccountSecurity.GameMaster, "r", Violating.Handle)]),
        ]);

        (string Path, ChatCommand Command)[] found = [.. WriteCommands([tree])];

        Assert.Equal(["debug speed SET", "debug speed reset"], found.Select(f => f.Path));
        Assert.Equal([true, false], found.Select(f => Records(HandlerType(f.Command)!)));
    }
}

// Top-level on purpose: HandlerType walks up to the outermost type, which must not be the test class.
internal static class Compliant
{
    public static bool Handle(CommandContext context, string args)
    {
        DebugWrite.Record(context, "debug thing set", "self", "thing", "1", "2", DebugWriteScope.Live);
        return true;
    }
}

internal static class CompliantByLambda
{
    public static ChatCommand Create() => new("set", AccountSecurity.GameMaster, "h", (context, args) =>
    {
        DebugWrite.Record(context, "debug other set", "self", "other", args, args, DebugWriteScope.Live, personal: true);
        return true;
    });
}

internal static class Violating
{
    public static bool Handle(CommandContext context, string args)
    {
        context.Reply("changed " + args);
        return true;
    }
}

internal static class ViolatingByLambda
{
    public static ChatCommand Create() => new("clear", AccountSecurity.GameMaster, "h", (context, args) =>
    {
        context.Reply("cleared");
        return true;
    });
}
