using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Review finding 89 across sessions: a raid's permanent lock reaches the members and the characters database. Two scripted bots
/// form a group and the leader converts it to a raid (CMSG_GROUP_RAID_CONVERT); the leader enters a raid instance alone and is locked
/// to it the way a boss kill locks everyone inside (vmangos DungeonMap::PermBindAllPlayers); the group's permanent bind is stored
/// under the leader (vmangos <c>group_instance</c>, here in the scenario world's SQLite characters database); then the member, who
/// was outside, enters, lands in the leader's instance and is locked too (vmangos DungeonMap::Add: "players also become permanently
/// bound when they enter", SMSG_INSTANCE_SAVE_CREATED).
/// The scenario world has only the continents, so the raid map (Molten Core, 409) is added to the map registry on the world thread.
/// </summary>
public sealed class RaidLockScenarioTests
{
    private const uint MoltenCore = 409;

    [Fact]
    public async Task ALockedRaidLeader_StoresTheGroupLock_AndTheMemberWhoWasOutsideIsLockedOnEntry()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        InstanceManager instances = world.Services.GetRequiredService<InstanceFeature>().Instances;
        await world.RunPassingAsync(new DelegateScenario("raid-lock", async context =>
        {
            (ScenarioBot leader, ScenarioBot member) = await PlayerbotScenarioCatalog.PairAsync(context);
            await ScenarioSteps.LeaveAnyGroupAsync(context, leader, member);
            await ScenarioSteps.FormGroupAsync(context, leader, member);
            Group group = await context.ReadAsync(() =>
                context.Services.GetRequiredService<SocialFeature>().Context.Groups.GetGroup(leader.Guid)!);

            await context.StepAsync("the leader makes it a raid", async () =>
            {
                ScenarioContext.Expect(await leader.SendAsync(WorldOpcode.CmsgGroupRaidConvert, []), "raid convert refused");
                await context.WaitUntilAsync("the group is a raid", () => group.IsRaid);
            });

            await context.StepAsync("the raid map is known", () => context.ReadAsync(() => WorldMaps.Of(world.Host.World).Load(new MapContent(
                [.. MapRegistry.DefaultContinents, new MapTemplate(MoltenCore, 0, MapType.Raid, 0, 40, 7, 0, -7510f, -1036f, "Molten Core", "")],
                [], [], [], []))));

            uint instanceId = await context.StepAsync("the leader enters the raid", async () =>
            {
                await context.PlaceAsync(leader, MoltenCore, 1096f, -467f, -104f);
                return await leader.ReadAsync(p => p.Map!.InstanceId);
            });

            await context.StepAsync("a boss kill locks everyone inside", async () =>
            {
                await context.ReadAsync(() =>
                {
                    Player player = leader.RequirePlayerForTests();
                    instances.PermBindAllPlayers(player.Map!, player);
                    return 0;
                });
                ScenarioContext.Expect(await context.ReadAsync(() => instances.GetPlayerBind(leader.Guid, MoltenCore) is { Permanent: true }), "leader locked");
                ScenarioContext.Expect(await context.ReadAsync(() => instances.GetGroupBind(group, MoltenCore) is { Permanent: true }), "group locked");
                ScenarioContext.Expect(await context.ReadAsync(() => instances.GetPlayerBind(member.Guid, MoltenCore) is null), "member outside not locked yet");
            });

            await context.StepAsync("the group lock is stored under the leader", async () =>
            {
                // The instance writes are queued off the world thread (InstanceWriteQueue); poll the database, bounded.
                var expected = new GroupInstanceBindRecord((int)leader.Guid.Low, instanceId, Permanent: true);
                bool stored = false;
                for (int attempt = 0; attempt < 100 && !stored; attempt++)
                {
                    InstanceStoreSnapshot rows = await world.WithScopeAsync(sp => sp.GetRequiredService<IInstanceStore>().LoadAsync());
                    stored = rows.GroupBinds.Contains(expected);
                    if (!stored)
                    {
                        await Task.Delay(20);
                    }
                }

                ScenarioContext.Expect(stored, "no group_instance row for the leader's lock");
            });

            await context.StepAsync("the member enters and is locked to the leader's instance", async () =>
            {
                long mark = member.Mark();
                await context.PlaceAsync(member, MoltenCore, 1098f, -467f, -104f);
                ScenarioContext.ExpectEqual(instanceId, await member.ReadAsync(p => p.Map!.InstanceId), "member's instance");
                ScenarioContext.Expect(await context.ReadAsync(() => instances.GetPlayerBind(member.Guid, MoltenCore) is { Permanent: true } bind
                    && bind.Save.InstanceId == instanceId), "member locked");
                ScenarioContext.Expect(member.Received(WorldOpcode.SmsgInstanceSaveCreated, payload => payload, mark).Count > 0,
                    "member told of the lock (SMSG_INSTANCE_SAVE_CREATED)");
            });
        }));
    }
}
