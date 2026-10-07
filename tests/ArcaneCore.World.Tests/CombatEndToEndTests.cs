using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>Combat opcodes end to end through the discovered <c>CombatHandlers</c>.</summary>
public sealed class CombatEndToEndTests
{
    /// <summary>Everyone is hostile (stands in for factions so two humans can fight).</summary>
    private sealed class HostileHooks : CombatHooks
    {
        public override bool IsFriendly(Unit a, Unit b) => false;
    }

    private static byte[] Guid(ulong guid) => BitConverter.GetBytes(guid);

    private static async Task<(WorldTestHost Host, WorldTestClient A, WorldTestClient B)> TwoPlayersAsync()
    {
        WorldTestHost host = WorldTestHost.Start();
        WorldTestClient a = await host.EnterWorldAsync("COMBATA", "Combata");
        WorldTestClient b = await host.EnterWorldAsync("COMBATB", "Combatb");
        await a.ReadUpdateAsync(); // B's create to A
        await b.ReadUpdateAsync(); // A's create to B
        return (host, a, b);
    }

    [Fact]
    public async Task AttackSwing_OnAFlaggedEnemy_StartsMelee_AndSwings()
    {
        (WorldTestHost host, WorldTestClient a, WorldTestClient b) = await TwoPlayersAsync();
        await using WorldTestHost h = host;
        await using WorldTestClient ca = a;
        await using WorldTestClient cb = b;
        await host.OnWorldAsync(() => host.World.GetMap(0).Combat.Hooks = new HostileHooks());

        await b.SendAsync(WorldOpcode.CmsgTogglePvp, [1]);
        await host.WaitForWorldAsync(() => (host.World.FindOnlinePlayer("Combatb")!.UnitFlags & UnitFlags.Pvp) != 0, "B flagged");

        await a.SendAsync(WorldOpcode.CmsgAttackswing, Guid(2));

        byte[] start = await a.ReadUntilAsync(WorldOpcode.SmsgAttackstart);
        Assert.Equal([.. Guid(1), .. Guid(2)], start);
        Assert.Equal(start, await b.ReadUntilAsync(WorldOpcode.SmsgAttackstart));

        byte[] swing = await b.ReadUntilAsync(WorldOpcode.SmsgAttackerstateupdate);
        Assert.Equal([0x01, 0x01, 0x01, 0x02], swing[4..8]); // packed attacker 1, packed victim 2
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Combata")!.Combat.IsInCombat, "A in combat");

        await host.OnWorldAsync(() => Assert.True(host.World.FindOnlinePlayer("Combata")!.Combat.QueueExtraAttacks(1)));
        await a.SendAsync(WorldOpcode.CmsgAttackstop, []);
        byte[] stop = await b.ReadUntilAsync(WorldOpcode.SmsgAttackstop);
        Assert.Equal([0x01, 0x01, 0x01, 0x02, 0, 0, 0, 0], stop);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Combata")!.Combat.ExtraAttacks == 0, "extra attacks cleared");
    }

    [Fact]
    public async Task AttackSwing_RefusesFriendsAndUnknownTargets()
    {
        (WorldTestHost host, WorldTestClient a, WorldTestClient b) = await TwoPlayersAsync();
        await using WorldTestHost h = host;
        await using WorldTestClient ca = a;
        await using WorldTestClient cb = b;

        // same team (default hooks): SMSG_ATTACKSTOP naming B, to A only
        await a.SendAsync(WorldOpcode.CmsgAttackswing, Guid(2));
        Assert.Equal([0x01, 0x01, 0x01, 0x02, 0, 0, 0, 0], await a.ReadUntilAsync(WorldOpcode.SmsgAttackstop));

        // unknown: no victim GUID
        await a.SendAsync(WorldOpcode.CmsgAttackswing, Guid(0x999));
        Assert.Equal([0x01, 0x01, 0x00, 0, 0, 0, 0], await a.ReadUntilAsync(WorldOpcode.SmsgAttackstop));
        Assert.Null(await host.PlayerStateAsync("Combata", p => p.Combat.Victim));
    }

    [Fact]
    public async Task CorpseQuery_WithoutACorpse_AnswersNotFound_AndSheathIsStored()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient a = await host.EnterWorldAsync("CORPSEQ", "Corpseq");

        await a.SendAsync(WorldOpcode.MsgCorpseQuery, []);
        Assert.Equal([0], await a.ReadUntilAsync(WorldOpcode.MsgCorpseQuery));

        byte[] sheath = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sheath, 1);
        await a.SendAsync(WorldOpcode.CmsgSetsheathed, sheath);
        await host.WaitForWorldAsync(
            () => host.World.FindOnlinePlayer("Corpseq")!.GetByte(UpdateFields.UnitFieldBytes2, 0) == 1, "sheath stored");
    }
}
