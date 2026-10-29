using System.Text.RegularExpressions;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Net;
using ArcaneCore.World.Net.SessionTrace;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Net.SessionTrace;

/// <summary>
/// A clock a test steps by hand. Wall time and the monotonic timestamp (1 tick = 1 ms) move together and only through <see cref="Advance"/>,
/// so the trace feature (expiry from <see cref="TimeProvider.GetUtcNow"/>, rate limiting from <see cref="TimeProvider.GetTimestamp"/>)
/// sees one consistent fake time. Starts at the real UTC instant of construction.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private long _ms = 1_000_000;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref _ms);

    public override DateTimeOffset GetUtcNow() => _start.AddMilliseconds(Interlocked.Read(ref _ms) - 1_000_000);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ms, (long)by.TotalMilliseconds);
}

/// <summary>One parsed trace line: <c>[{Endpoint}] trace {Character} guid={Guid} {Event} {Detail}</c> (session-trace spec, "Line writer").</summary>
internal sealed record TraceLine(string Raw, string Endpoint, string Character, uint Guid, string Event, string Detail);

/// <summary>A scripted client movement source: its own clock and position, packets paced in real time (mirrors AntiCheatWorldTests.Mover).</summary>
internal sealed class TraceMover(WorldTestClient client, float x, float y, float z)
{
    public uint Time = 100_000;
    public float X = x, Y = y, Z = z;

    public Task SendAsync(WorldOpcode opcode, MovementFlags flags)
    {
        var writer = new PacketWriter(48);
        new MovementInfo { Flags = flags, Time = Time, X = X, Y = Y, Z = Z }.Write(writer);
        return client.SendAsync(opcode, writer.ToArray());
    }

    /// <summary>Wait <paramref name="ms"/> of real time, advance the client clock as much, move <paramref name="yards"/> along x and send.</summary>
    public async Task StepAsync(int ms, float yards, WorldOpcode opcode = WorldOpcode.MsgMoveHeartbeat, MovementFlags flags = MovementFlags.Forward)
    {
        await Task.Delay(ms);
        Time += (uint)ms;
        X += yards;
        await SendAsync(opcode, flags);
    }
}

/// <summary>Shared helpers of the session-trace tests.</summary>
internal static class SessionTraceTestKit
{
    /// <summary>A spell with one Dummy aura on its caster; not in the shipped content, so the test owns it.</summary>
    public const uint AuraSpell = 962600;

    private static readonly Regex LineShape = new(
        @"^\[(?<ep>[^\]]+)\] trace (?<name>\S+) guid=(?<guid>\d+) (?<ev>\S+)(?: (?<detail>.*))?$", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Starts a world host whose <see cref="ILoggerFactory"/> feeds <paramref name="logs"/> and whose <see cref="TimeProvider"/> is
    /// <paramref name="clock"/>. WorldTestHost registers a NullLoggerFactory first and <c>AddLogging</c> only TryAdds, so a provider added
    /// with <c>AddLogging(b =&gt; b.AddProvider(...))</c> would never receive anything; the last registration wins instead
    /// (the pattern of CharacterAppearanceWorldTests.Register). Features take the factory by constructor injection and get this one.
    /// </summary>
    public static WorldTestHost Start(FakeTimeProvider clock, CapturingLoggerProvider logs, IDictionary<string, string?>? settings = null)
        => WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)));
            services.AddSingleton<TimeProvider>(clock);
            if (settings is not null)
            {
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            }
        });

    public static SessionTraceFeature FeatureOf(WorldTestHost host) => host.WorldServices.GetRequiredService<SessionTraceFeature>();

    /// <summary>Every line the feature's dedicated category took, parsed. A line of that category that is not a trace line at Information fails the test.</summary>
    public static TraceLine[] Lines(CapturingLoggerProvider logs)
        => [.. logs.Entries.Where(e => e.Category == SessionTraceFeature.LogCategory).Select(Parse)];

    private static TraceLine Parse(CapturedCategoryLog entry)
    {
        Assert.Equal(LogLevel.Information, entry.Level);
        Match m = LineShape.Match(entry.Message);
        Assert.True(m.Success, "not a trace line: " + entry.Message);
        return new TraceLine(entry.Message, m.Groups["ep"].Value, m.Groups["name"].Value, uint.Parse(m.Groups["guid"].Value), m.Groups["ev"].Value, m.Groups["detail"].Value);
    }

    /// <summary>Wait (real time, 10 s) until a trace line matching <paramref name="match"/> exists; returns it.</summary>
    public static async Task<TraceLine> WaitForLineAsync(CapturingLoggerProvider logs, Func<TraceLine, bool> match, string what)
    {
        TraceLine? found = null;
        await WorldTestHost.WaitForAsync(() => (found = Lines(logs).FirstOrDefault(match)) is not null, what);
        return found!;
    }

    /// <summary>The next system chat line the client receives (its own say echo and others' chat are skipped).</summary>
    public static async Task<string> CommandAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        while (true)
        {
            ChatMessage message = await client.ReadChatAsync(TimeSpan.FromSeconds(10));
            if (message.Type == ChatType.System) return message.Text;
        }
    }

    /// <summary>Sends a command and returns every system chat line of its reply (the first is awaited, the rest collected until quiet).</summary>
    public static async Task<string[]> CommandLinesAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        var lines = new List<string>();
        while (true)
        {
            ChatMessage message = await client.ReadChatAsync(TimeSpan.FromSeconds(10));
            if (message.Type == ChatType.System)
            {
                lines.Add(message.Text);
                break;
            }
        }

        foreach ((WorldOpcode opcode, byte[] payload) in await client.CollectAsync())
        {
            if (opcode == WorldOpcode.SmsgMessagechat && ChatMessage.Parse(payload) is { Type: ChatType.System } more)
            {
                lines.Add(more.Text);
            }
        }

        return [.. lines];
    }

    /// <summary>Lets the fake clock run a little over one second, which is what the feature's once-per-clock-second work waits for.</summary>
    public static void Tick(FakeTimeProvider clock) => clock.Advance(TimeSpan.FromMilliseconds(1100));

    /// <summary>Installs <see cref="AuraSpell"/> in the spell store (world thread).</summary>
    public static Task<SpellSystem> InstallAuraAsync(WorldTestHost host)
    {
        SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
        return host.OnWorldAsync(() =>
        {
            spells.System.Store = new SpellStore(
            [
                .. spells.System.Store.All,
                new SpellInfo
                {
                    Id = AuraSpell, Name = "Session trace test aura", Duration = new SpellDuration(60_000, 0, 60_000),
                    Effects =
                    [
                        new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy, TargetA = SpellImplicitTarget.UnitCaster },
                        new(), new(),
                    ],
                },
            ], [], []);
            return spells.System;
        });
    }

    /// <summary>A mover standing where the online player stands (drains the login packets first).</summary>
    public static async Task<TraceMover> MoverAsync(WorldTestHost host, WorldTestClient client, string name)
    {
        (float x, float y, float z) = await host.PlayerStateAsync(name, p => (p.X, p.Y, p.Z));
        await client.CollectAsync();
        return new TraceMover(client, x, y, z);
    }

    /// <summary>Wait until the world has stored the client's last movement block.</summary>
    public static Task SettledAsync(WorldTestHost host, string name, TraceMover mover)
        => host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name) is { } p && MathF.Abs(p.X - mover.X) < 0.01f, $"{name} at x={mover.X}");

    public static Task<uint> LowGuidAsync(WorldTestHost host, string name) => host.PlayerStateAsync(name, p => p.Guid.Low);

    public static WorldSession SessionOf(WorldTestHost host, string name)
        => host.Registry.Sessions.Single(s => string.Equals(s.Player?.Name, name, StringComparison.OrdinalIgnoreCase));
}
