using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Ranged;

/// <summary>
/// CMSG_CANCEL_AUTO_REPEAT_SPELL (621) over a real session (ranged lane S02; vmangos HandleCancelAutoRepeatSpellOpcode,
/// SpellHandler.cpp:439-444). The spell is an auto-repeat shape with no weapon checks. Waits are on packets with the harness'
/// generous deadline, never on a fixed window.
/// </summary>
public sealed class AutoRepeatLoopbackTests
{
    private static byte[] CastPayload(uint spell) => [.. BitConverter.GetBytes(spell), 0, 0];

    [Fact]
    public async Task CancelAutoRepeatSpell_StopsTheToggle_AndTellsTheClient()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("SHOOTER", "Shooter", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await gm.SendChatAsync(ChatType.Say, Language.Common, $".learn {SelfShoot}");
        await gm.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        await gm.CollectAsync();

        await gm.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(SelfShoot));
        await gm.ReadUntilAsync(WorldOpcode.SmsgSpellStart);
        await host.WaitForWorldAsync(() => HasAutoRepeat(host, "Shooter"), "auto-repeat toggled on");

        await gm.SendAsync(WorldOpcode.CmsgCancelAutoRepeatSpell, []);

        Assert.Empty(await gm.ReadUntilAsync(WorldOpcode.SmsgCancelAutoRepeat));
        await host.WaitForWorldAsync(() => !HasAutoRepeat(host, "Shooter"), "auto-repeat toggled off");
    }

    [Fact]
    public async Task CancelAutoRepeatSpell_WithNothingToggled_SendsNothingAndKeepsTheSessionAlive()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("IDLER", "Idler");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgCancelAutoRepeatSpell, []);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(Heal));

        List<WorldOpcode> seen = [];
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        seen.AddRange((await client.CollectAsync()).Select(p => p.Opcode));
        Assert.DoesNotContain(WorldOpcode.SmsgCancelAutoRepeat, seen);
    }

    private static bool HasAutoRepeat(WorldTestHost host, string name)
    {
        var player = host.World.FindOnlinePlayer(name)!;
        var spells = ((ArcaneCore.World.Net.WorldSession)player.Session).Services.GetService(typeof(ArcaneCore.World.Spells.SpellFeature)) as ArcaneCore.World.Spells.SpellFeature;
        return spells!.System.HasAutoRepeat(player);
    }
}
