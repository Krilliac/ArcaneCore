using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>Player versus player fixtures for the playerbot tests: a second managed session of the other team beside the first.</summary>
internal static class PlayerbotPvpTestKit
{
    /// <summary>Human (Alliance) and orc (Horde) race ids.</summary>
    internal const byte Human = 1;
    internal const byte Orc = 2;

    /// <summary>Log a managed warrior in under its own account and name (the shared <c>EnterAsync</c> helper makes one fixed name per host).</summary>
    internal static async Task<WorldSession> EnterAsync(WorldTestHost host, string account, string name, byte race)
    {
        Account owner = await host.Accounts.CreateAsync(new Account { Username = account, Salt = new byte[32], Verifier = new byte[32] });
        WorldSession session = await WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes,
            host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance);
        var create = new PacketWriter();
        create.WriteCString(name);
        create.WriteByte(race);
        create.WriteByte(1);
        for (int i = 0; i < 8; i++) create.WriteByte(0);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        var login = new PacketWriter();
        login.WriteUInt64((ulong)(await session.Services.GetRequiredService<ICharacterStore>().GetByAccountAsync(owner.Id)).Single().Id);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await host.WaitForWorldAsync(() => session.Player is not null, name + " login");
        return session;
    }

    /// <summary>An Alliance bot-to-be and a Horde enemy on the same map, the enemy a few yards east of the first (world thread).</summary>
    internal static async Task<(WorldSession Ally, WorldSession Enemy)> EnterPairAsync(WorldTestHost host, float gap = 4f)
    {
        WorldSession ally = await EnterAsync(host, "PVPALLY", "Pvpally", Human);
        WorldSession enemy = await EnterAsync(host, "PVPENEMY", "Pvpenemy", Orc);
        await host.OnWorldAsync(() =>
        {
            Player a = ally.Player!, e = enemy.Player!;
            if (!ReferenceEquals(a.Map, e.Map)) throw new InvalidOperationException("the two start positions are on different maps");
            e.Relocate(a.X + gap, a.Y, a.Z, 0, host.World.NowMs);
            return true;
        });
        return (ally, enemy);
    }

    /// <summary>Turn the PvP flag on (what an attack on a flagged unit or a CMSG_TOGGLE_PVP does).</summary>
    internal static void Flag(Player player) => player.UnitFlags |= UnitFlags.Pvp;
}
