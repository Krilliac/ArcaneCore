using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.PacketHeuristics;

/// <summary>Shared fixtures of the packet heuristics tests: a private ledger and clock on the host's feature, real client and bot sessions.</summary>
internal static class PacketHeuristicsKit
{
    internal static PacketHeuristicsFeature FeatureOf(WorldTestHost host) => host.WorldServices.GetRequiredService<PacketHeuristicsFeature>();

    /// <summary>
    /// Switches the checks on for this host's feature with a private ledger (so no other test's findings interfere) and a clock the test controls.
    /// <paramref name="now"/> is the fake clock in milliseconds; null keeps the real one.
    /// </summary>
    internal static (PacketHeuristicsFeature Feature, AnomalyLedger Ledger) Enable(WorldTestHost host, Action<PacketHeuristicsOptions>? tweak = null, Func<long>? now = null)
    {
        PacketHeuristicsFeature feature = FeatureOf(host);
        var ledger = new AnomalyLedger();
        feature.Ledger = ledger;
        if (now is not null)
        {
            feature.NowMs = now;
        }

        var options = new PacketHeuristicsOptions { Enabled = true };
        tweak?.Invoke(options);
        Assert.True(feature.TryApply(options, out IReadOnlyList<string> problems), string.Join(" ", problems));
        return (feature, ledger);
    }

    /// <summary>A managed bot (a socketless session of a server-owned account) logged into the world; the caller kicks it.</summary>
    internal static async Task<WorldSession> EnterBotAsync(WorldTestHost host, string username, string character)
    {
        Account owner = await host.Accounts.CreateAsync(new Account { Username = username, Salt = new byte[32], Verifier = new byte[32] });
        WorldSession session = await WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes,
            host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance);
        var create = new PacketWriter();
        create.WriteCString(character);
        create.WriteByte(1);
        create.WriteByte(1);
        for (int i = 0; i < 8; i++)
        {
            create.WriteByte(0);
        }

        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        var login = new PacketWriter();
        login.WriteUInt64((ulong)(await session.Services.GetRequiredService<ICharacterStore>().GetByAccountAsync(owner.Id)).Single().Id);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await host.WaitForWorldAsync(() => session.Player is not null, "bot login");
        return session;
    }

    /// <summary>A managed bot that has not logged in (it sits at the character screen).</summary>
    internal static async Task<WorldSession> NewBotAtCharacterScreenAsync(WorldTestHost host, string username)
    {
        Account owner = await host.Accounts.CreateAsync(new Account { Username = username, Salt = new byte[32], Verifier = new byte[32] });
        return await WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes, host.World, host.Registry,
            new WorldSessionOptions(), NullLogger.Instance);
    }

    /// <summary>A client in the world with the session it runs on.</summary>
    internal static async Task<(WorldTestClient Client, WorldSession Session)> EnterClientAsync(WorldTestHost host, string account, string character,
        AccountSecurity security = AccountSecurity.Player)
    {
        WorldTestClient client = await host.EnterWorldAsync(account, character, security);
        Account stored = (await host.Accounts.FindByUsernameAsync(account))!;
        return (client, host.Registry.Find(stored.Id)!);
    }

    /// <summary>
    /// Waits until everything the client sent has been dispatched by the session task (a ping is answered in order) and the world thread has
    /// drained the session's queue. The barrier ping is the session's first, so it adds no cadence finding.
    /// </summary>
    internal static async Task SettleAsync(WorldTestHost host, WorldTestClient client, WorldSession session)
    {
        await client.SendAsync(WorldOpcode.CmsgPing, PingPayload(1));
        await client.ReadUntilAsync(WorldOpcode.SmsgPong);
        await host.WaitForWorldAsync(() => session.QueuedWorldPackets == 0, "the world queue drains");
        await Task.Delay(30); // the packet being handled when the queue emptied
    }

    internal static byte[] PingPayload(uint sequence)
    {
        byte[] payload = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, sequence);
        return payload;
    }

    internal static byte[] U32(uint value) => BitConverter.GetBytes(value);

    internal static long Total(PacketHeuristicsFeature feature, PacketCheck check)
        => feature.Count(check, PacketTraffic.Player) + feature.Count(check, PacketTraffic.Bot) + feature.Count(check, PacketTraffic.Staff) + feature.Count(check, PacketTraffic.Tool);
}
