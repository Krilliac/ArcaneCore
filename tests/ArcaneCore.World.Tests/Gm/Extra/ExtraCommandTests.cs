using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Extra;

public sealed class ExtraCommandTests
{
    private const uint StackingSpell = 991951;

    [Fact]
    public void NewCommandsHaveTheirSecurityLevels()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "inspect auras", "inspect group", "inspect grid", "inspect visibility", "alter auraduration", "alter aurastacks", "debug pathbench" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }

        foreach (string path in new[] { "tele group", "groupgo" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Player));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster)); // vmangos SEC_TICKETMASTER (retail level 2)
        }

        Assert.Null(table.Resolve("debug gccollect", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("debug gccollect", AccountSecurity.Administrator));
    }

    [Fact]
    public async Task AurasAreListedAndTheirDurationAndStacksEdited()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("XAURA", "Xaura", AccountSecurity.Administrator);
        await admin.CollectAsync();
        Player me = await host.PlayerAsync("Xaura");
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
        await host.OnWorldAsync(() => spells.Store = new SpellStore([Stacking()], [], []));

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".aura " + StackingSpell);
        await host.WaitForWorldAsync(() => spells.HasAura(me, StackingSpell), "aura application");
        await admin.CollectAsync();

        string[] list = await RunAsync(admin, ".inspect auras stack");
        Assert.Contains(list, l => l.Contains("aura holders, 1 matching 'stack'", StringComparison.Ordinal));
        Assert.Contains(list, l => l.StartsWith($"  {StackingSpell} [Stacking Test]", StringComparison.Ordinal) && l.Contains("stacks 1", StringComparison.Ordinal) && l.Contains("ModStat 3", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(admin, $".alter auraduration {StackingSpell} 10"), l => l.EndsWith("10.0s left of 1.0m.", StringComparison.Ordinal));
        SpellAuraHolder holder = await host.OnWorldAsync(() => spells.GetAuras(me).Single(h => h.Spell.Id == StackingSpell));
        Assert.InRange(await host.OnWorldAsync(() => holder.Duration), 9_000, 10_000);

        Assert.Contains(await RunAsync(admin, $".alter aurastacks {StackingSpell} 3"), l => l.EndsWith("3 of 5 stacks.", StringComparison.Ordinal));
        Assert.Equal(3, await host.OnWorldAsync(() => (int)holder.StackAmount));
        Assert.Equal(9, await host.OnWorldAsync(() => holder.Auras[0]!.Amount));
        Assert.Contains(await RunAsync(admin, $".alter aurastacks {StackingSpell} 9"), l => l.EndsWith("5 of 5 stacks (the spell's limit).", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(admin, ".alter auraduration 12345 10"), l => l.EndsWith("has no aura of spell 12345.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(admin, $".alter aurastacks {StackingSpell} 0"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GroupIsInspectedAndSummoned()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("XGROUPGM", "Xgroupgm", AccountSecurity.GameMaster);
        await using WorldTestClient lead = await host.EnterWorldAsync("XGROUPA", "Xgroupa");
        await using WorldTestClient member = await host.EnterWorldAsync("XGROUPB", "Xgroupb");
        Player a = await host.PlayerAsync("Xgroupa");
        Player b = await host.PlayerAsync("Xgroupb");
        GroupManager groups = host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
        await host.OnWorldAsync(() =>
        {
            groups.Invite(a, "Xgroupb");
            groups.Accept(b);
            host.World.FindOnlinePlayer("Xgroupgm")!.Selection = a.Guid;
        });
        await host.WaitForWorldAsync(() => groups.GetGroup(b.Guid) is not null, "group formed");
        await gm.CollectAsync();

        string[] info = await RunAsync(gm, ".inspect group");
        Assert.Contains(info, l => l.Contains("(party): 2 members, leader Xgroupa", StringComparison.Ordinal));
        Assert.Contains(info, l => l.StartsWith("  [1] Xgroupb: map ", StringComparison.Ordinal));

        string[] go = await RunAsync(gm, ".groupgo");
        Assert.Contains(go, l => l.Contains("summoning", StringComparison.Ordinal) && l.Contains("Xgroupa", StringComparison.Ordinal));
        Assert.Contains(go, l => l.Contains("summoning", StringComparison.Ordinal) && l.Contains("Xgroupb", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(gm, ".tele group nowhere-at-all"), l => l == "Teleport location not found!");
    }

    [Fact]
    public async Task GridVisibilityAndDebugReadoutsAnswer()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("XGRID", "Xgrid", AccountSecurity.Administrator);
        await using WorldTestClient other = await host.EnterWorldAsync("XGRIDB", "Xgridb");
        Player b = await host.PlayerAsync("Xgridb");
        await admin.CollectAsync();

        string[] grid = await RunAsync(admin, ".inspect grid");
        Assert.Contains(grid, l => l.Contains(": grid [", StringComparison.Ordinal) && l.Contains(", cell [", StringComparison.Ordinal));
        Assert.Contains(grid, l => l.StartsWith("  in the grid container: cell [", StringComparison.Ordinal));
        Assert.Contains(grid, l => l.StartsWith("  map: ", StringComparison.Ordinal) && l.Contains("grids loaded", StringComparison.Ordinal));

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Xgrid")!.Selection = b.Guid);
        string[] vis = await RunAsync(admin, ".inspect visibility");
        Assert.Contains(vis, l => l.Contains("in visibility range True", StringComparison.Ordinal));
        Assert.Contains(vis, l => l.StartsWith("  Xgridb has you in its visible set:", StringComparison.Ordinal));

        string[] bench = await RunAsync(admin, ".debug pathbench 8 20");
        Assert.Contains(bench, l => l.StartsWith("8 path queries at 20 yd on map", StringComparison.Ordinal));
        Assert.Contains(bench, l => l.Contains("no path; mean length", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(admin, ".debug gccollect"), l => l.StartsWith("Full compacting collection took", StringComparison.Ordinal));
    }

    private static SpellInfo Stacking() => new()
    {
        Id = StackingSpell,
        Name = "Stacking Test",
        StackAmount = 5,
        Duration = new SpellDuration(60_000, 0, 60_000),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura,
            AuraType = AuraType.ModStat,
            BasePoints = 2,
            BaseDice = 1,
            DieSides = 1,
            TargetA = SpellImplicitTarget.UnitCaster,
        }, new(), new()],
    };

    private static async Task<string[]> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return await client.CollectChatLinesAsync();
    }
}
