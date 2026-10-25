using System.Reflection;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>
/// Plan 1.5: every debug write is audited, checked per command and not per type. A command marked <see cref="ChatCommand.IsWrite"/> must
/// have a handler that reaches <see cref="DebugWrite.Record"/> (through the methods of its own type), and a command that reaches Record
/// must be marked (the run-time half of the check is <see cref="DebugWriteLog.Strict"/>, which the test host turns on). A command that
/// predates the flag and whose last word is a write verb is covered by the same rule unless its path is in <see cref="Grandfathered"/>;
/// the list may only shrink (a command that becomes compliant must leave it, see the stale-entry test).
/// </summary>
public sealed class DebugWriteEnforcementTests
{
    /// <summary>The last word of a command path that makes it a write by name (plan 1.2 verbs).</summary>
    internal static readonly string[] WriteVerbs =
        ["set", "reset", "add", "remove", "clear", "on", "off", "start", "stop", "pause", "resume", "step", "flush", "retry", "recheck", "inject"];

    private static readonly MethodInfo[] RecordMethods =
        [.. typeof(DebugWrite).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == nameof(DebugWrite.Record))];

    /// <summary>Command paths of write-verb commands that existed before the DebugWriteLog and are not marked IsWrite. A new command never goes here.</summary>
    private static readonly HashSet<string> Grandfathered = new(StringComparer.Ordinal)
    {
        "account pin set",
        "account pin clear",
        "account totp set",
        "account totp clear",
        "debug vis clear",
        "debug alloc start",
        "debug alloc stop",
        "event start",
        "event stop",
        "honor add",
        "honor reset",
        "gobject add",
        "npc add",
        "quest add",
        "quest remove",
        "tele add",
        "bot party add",
        "bot party remove",
        "bg start",
        "bg stop",
        "cooldown clear",
        "anticheat set",
        "packetcheck on",
        "packetcheck off",
        "packetcheck reset",
        "creature add",
        "ai stop",
        "ai reset",
        "movement set",
        "botctl stop",
        "playerbot start",
        "playerbot stop",
        "playerbot chaos on",
        "playerbot chaos off",
        "playerbot chaos clear",
        "playerbot party add",
        "playerbot party remove",
        "playerbot siege waypoint add",
        "playerbot siege waypoint clear",
        "playerbot siege start",
        "playerbot siege stop",
        "playerbot fleet reset",
        "playerbot qa reset",
        "rates set",
        "rates reset",
    };

    internal static IEnumerable<(string Path, ChatCommand Command)> Commands(IReadOnlyList<ChatCommand> roots, string prefix = "")
    {
        foreach (ChatCommand command in roots)
        {
            string path = prefix.Length == 0 ? command.Name : prefix + " " + command.Name;
            if (command.Handler is not null)
            {
                yield return (path, command);
            }

            foreach ((string, ChatCommand) child in Commands(command.SubCommands, path))
            {
                yield return child;
            }
        }
    }

    internal static bool IsWriteByName(ChatCommand command) => WriteVerbs.Contains(command.Name, StringComparer.OrdinalIgnoreCase);

    private static Type Outermost(Type type)
    {
        while (type is { IsNested: true, DeclaringType: { } outer }) type = outer;
        return type;
    }

    /// <summary>
    /// Whether <paramref name="handler"/> reaches <see cref="DebugWrite.Record"/>: it follows calls, constructions and method references
    /// out of the handler's own method into methods of the same outermost type (nested closure classes included), nothing wider, so a
    /// sibling handler in the same type does not vouch for this one.
    /// </summary>
    internal static bool Reaches(CommandHandler handler)
    {
        MethodBase start = handler.Method;
        Type scope = Outermost(start.DeclaringType!);
        HashSet<MethodBase> seen = [start];
        Queue<MethodBase> work = new([start]);
        while (work.TryDequeue(out MethodBase? method))
        {
            foreach (MethodBase callee in Callees(method))
            {
                if (RecordMethods.Any(r => r.MetadataToken == callee.MetadataToken && r.Module == callee.Module)) return true;
                if (callee.DeclaringType is { } declaring && Outermost(declaring) == scope && seen.Add(callee)) work.Enqueue(callee);
            }
        }

        return false;
    }

    private static IEnumerable<MethodBase> Callees(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } ? method.DeclaringType.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            // call, callvirt, newobj; ldftn, ldvirtftn (the two-byte 0xFE 0x06 / 0xFE 0x07 forms: a lambda or method group being bound).
            int operand;
            if (il[i] is 0x28 or 0x6F or 0x73) operand = i + 1;
            else if (il[i] == 0xFE && il[i + 1] is 0x06 or 0x07 && i + 5 < il.Length) operand = i + 2;
            else continue;

            MethodBase? target = null;
            try
            {
                target = method.Module.ResolveMethod(BitConverter.ToInt32(il, operand), typeArguments, methodArguments);
            }
            catch (Exception e) when (e is ArgumentException or BadImageFormatException)
            {
                // Not a method token: the byte was an operand of another instruction.
            }

            if (target is not null) yield return target;
        }
    }

    private static string Describe(IEnumerable<string> lines) => string.Join("\n", lines.Select(l => "  " + l));

    private static string Where(ChatCommand command) => command.Handler!.Method.DeclaringType?.FullName + "." + command.Handler.Method.Name;

    [Fact]
    public void EveryFlaggedWriteCommand_ReachesRecord_AndEveryCommandThatRecordsIsFlagged()
    {
        CommandTable table = ChatCommands.CreateTable();
        (string Path, ChatCommand Command)[] all = [.. Commands(table.Roots)];
        Assert.True(all.Length > 100, "the commands must be enumerated; found " + all.Length);

        string[] violations =
        [
            .. all.Where(c => c.Command.IsWrite && !Reaches(c.Command.Handler!)).Select(c => $".{c.Path} is IsWrite but its handler never reaches DebugWrite.Record ({Where(c.Command)})"),
            .. all.Where(c => !c.Command.IsWrite && Reaches(c.Command.Handler!)).Select(c => $".{c.Path} reaches DebugWrite.Record but is not marked IsWrite ({Where(c.Command)})"),
        ];

        Assert.True(violations.Length == 0, "plan 1.5:\n" + Describe(violations));
    }

    [Fact]
    public void EveryWriteVerbCommand_IsFlaggedAndRecords_OrIsGrandfathered()
    {
        CommandTable table = ChatCommands.CreateTable();
        (string Path, ChatCommand Command)[] writers = [.. Commands(table.Roots).Where(c => IsWriteByName(c.Command))];
        Assert.True(writers.Length > 10, "the write-verb commands must be enumerated; found " + writers.Length);

        string[] violations =
        [
            .. writers.Where(w => !w.Command.IsWrite && !Grandfathered.Contains(w.Path))
                .Select(w => $".{w.Path} is a write verb with no IsWrite flag and no DebugWrite.Record ({Where(w.Command)})"),
        ];

        Assert.True(violations.Length == 0, "write-verb commands must be marked IsWrite and call DebugWrite.Record (plan 1.5):\n" + Describe(violations));
    }

    [Fact]
    public void TheGrandfatherList_HasNoStaleEntries()
    {
        CommandTable table = ChatCommands.CreateTable();
        Dictionary<string, ChatCommand> writers = Commands(table.Roots).Where(c => IsWriteByName(c.Command)).ToDictionary(c => c.Path, c => c.Command, StringComparer.Ordinal);
        string[] stale = [.. Grandfathered.Where(path => !writers.TryGetValue(path, out ChatCommand? command) || command.IsWrite)];

        Assert.True(stale.Length == 0, "paths that no longer need the grandfather entry (flagged, renamed or removed): " + string.Join(", ", stale));
    }

    // ---- the checker itself ----

    [Fact]
    public void TheChecker_FollowsTheHandlersOwnCalls_AndNothingWiderThanItsType()
    {
        Assert.True(Reaches(Compliant.Handle));
        Assert.False(Reaches(Violating.Handle));
        Assert.True(Reaches(Delegating.Handle), "a helper in the same type counts");
        Assert.True(Reaches(SiblingRecorder.Records));
        Assert.False(Reaches(SiblingRecorder.Silent), "a sibling that records does not vouch for a handler that does not");
        Assert.True(Reaches(CompliantByLambda.Create().Handler!));
        Assert.False(Reaches(ViolatingByLambda.Create().Handler!));
        Assert.True(Reaches(LambdaWithHelper.Create().Handler!), "a lambda that calls a private helper of its type that records");
    }

    [Fact]
    public void TheEnumerator_FindsHandlersAtAnyDepth_AndTheNameRuleOnlyMatchesWriteVerbLeaves()
    {
        ChatCommand tree = new("debug", AccountSecurity.GameMaster, "g", null,
        [
            new ChatCommand("show", AccountSecurity.GameMaster, "r", Violating.Handle),
            new ChatCommand("speed", AccountSecurity.GameMaster, "g", null,
            [
                new ChatCommand("SET", AccountSecurity.GameMaster, "w", Compliant.Handle, IsWrite: true),
                new ChatCommand("reset", AccountSecurity.GameMaster, "w", Violating.Handle),
            ]),
            new ChatCommand("set", AccountSecurity.GameMaster, "group without handler", null, [new ChatCommand("info", AccountSecurity.GameMaster, "r", Violating.Handle)]),
        ]);

        (string Path, ChatCommand Command)[] found = [.. Commands([tree])];

        Assert.Equal(["debug show", "debug speed SET", "debug speed reset", "debug set info"], found.Select(f => f.Path));
        Assert.Equal(["debug speed SET", "debug speed reset"], found.Where(f => IsWriteByName(f.Command)).Select(f => f.Path));
        Assert.Equal([false, true, false, false], found.Select(f => Reaches(f.Command.Handler!)));
    }
}

// Top-level on purpose: Reaches scopes to the outermost type, which must not be the test class.
internal static class Compliant
{
    public static bool Handle(CommandContext context, string args)
    {
        DebugWrite.Record(context, "self", "thing", "1", "2", DebugWriteScope.Live);
        return true;
    }
}

internal static class Delegating
{
    public static bool Handle(CommandContext context, string args) => Apply(context);

    private static bool Apply(CommandContext context)
    {
        DebugWrite.Record(context, "self", "thing", "1", "2", DebugWriteScope.Live);
        return true;
    }
}

internal static class SiblingRecorder
{
    public static bool Records(CommandContext context, string args)
    {
        DebugWrite.Record(context, "self", "thing", "1", "2", DebugWriteScope.Live);
        return true;
    }

    public static bool Silent(CommandContext context, string args)
    {
        context.Reply("changed " + args);
        return true;
    }
}

internal static class CompliantByLambda
{
    public static ChatCommand Create() => new("set", AccountSecurity.GameMaster, "h", (context, args) =>
    {
        DebugWrite.Record(context, "self", "other", args, args, DebugWriteScope.Live, sensitive: true);
        return true;
    });
}

internal static class LambdaWithHelper
{
    public static ChatCommand Create() => new("set", AccountSecurity.GameMaster, "h", (context, args) => Apply(context, args));

    private static bool Apply(CommandContext context, string args)
    {
        DebugWrite.Record(context, "self", "other", args, args, DebugWriteScope.Action);
        return true;
    }
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
