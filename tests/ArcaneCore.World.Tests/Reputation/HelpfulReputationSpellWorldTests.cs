using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Protocol;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

public sealed class HelpfulReputationSpellWorldTests
{
    private const uint BootyBay = 21;
    private const uint Heal = 993001;
    private const uint PlayerFactionTemplate = 1;
    private const uint NpcFactionTemplate = 2;

    [Fact]
    public async Task HelpfulHeal_UsesReputationRankAndAtWar_WithRealFactionAdapter()
    {
        var factions = new FactionCatalog([new FactionRecord(BootyBay, 0,
            [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], name: "Booty Bay")]);
        var templates = new FactionTemplateCatalog([
            new FactionTemplateRecord(PlayerFactionTemplate, 1, 0, 1, 0, 0),
            new FactionTemplateRecord(NpcFactionTemplate, BootyBay, 0, 2, 0, 1)]);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(factions);
            services.AddSingleton(templates);
        });
        await using WorldTestClient client = await host.EnterWorldAsync("REPHELP", "Rephelp");

        await host.OnWorldAsync(() =>
        {
            SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
            Assert.IsType<ReputationSpellTargetRelations>(spells.System.Relations);
            spells.System.CombatRules = SpellCombatRules.Neutral;
            spells.System.Store = new SpellStore([.. spells.System.Store.All, HealSpell()], [], []);
            Player player = host.World.FindOnlinePlayer("Rephelp")!;
            player.FactionTemplate = PlayerFactionTemplate;
            Creature npc = AddNpc(host, player, NpcFactionTemplate);
            npc.Health = 50;
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, Heal,
                SpellCastTargets.ForUnit(npc.Guid), triggered: true));
            Assert.Equal(60u, npc.Health);

            ReputationFeature reputation = host.WorldServices.GetRequiredService<ReputationFeature>();
            Assert.True(reputation.Service.ModifyReputation(player, BootyBay, -1000));
            npc.Health = 50;
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, Heal,
                SpellCastTargets.ForUnit(npc.Guid), triggered: true));
            Assert.Equal(60u, npc.Health);

            Assert.True(reputation.Service.ModifyReputation(player, BootyBay, -5000));
            npc.Health = 50;
            Assert.Equal(SpellCastResult.BadTargets, spells.System.CastSpell(player, Heal,
                SpellCastTargets.ForUnit(npc.Guid), triggered: true));
            Assert.Equal(50u, npc.Health);

            Assert.True(reputation.Service.ModifyReputation(player, BootyBay, -36000));
            npc.Health = 50;
            Assert.Equal(SpellCastResult.BadTargets, spells.System.CastSpell(player, Heal,
                SpellCastTargets.ForUnit(npc.Guid), triggered: true));
            Assert.Equal(50u, npc.Health);

            Assert.True(reputation.Service.ModifyReputation(player, BootyBay, 42000));
            Assert.True(reputation.Service.SetAtWar(player, 0, false));
            Assert.True(reputation.Service.SetAtWar(player, 0, true));
            npc.Health = 50;
            Assert.Equal(SpellCastResult.BadTargets, spells.System.CastSpell(player, Heal,
                SpellCastTargets.ForUnit(npc.Guid), triggered: true));
            Assert.Equal(50u, npc.Health);
            Assert.True(reputation.Service.SetAtWar(player, 0, false));
            npc.Health = 50;
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, Heal,
                SpellCastTargets.ForUnit(npc.Guid), triggered: true));
            Assert.Equal(60u, npc.Health);
        });
    }

    private static SpellInfo HealSpell() => new()
    {
        Id = Heal, Name = "Synthetic reputation heal", RangeIndex = 4, Range = new SpellRange(0, 30),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.Heal, BasePoints = 9, BaseDice = 1, DieSides = 1,
            TargetA = SpellImplicitTarget.UnitFriend,
        }, new(), new()],
    };

    private static Creature AddNpc(WorldTestHost host, Player player, uint factionTemplate)
    {
        var template = new CreatureTemplate
        {
            Entry = 993010, Name = "Synthetic Booty Bay NPC", Faction = factionTemplate,
            MinLevel = 1, MaxLevel = 1, MinLevelHealth = 100, MaxLevelHealth = 100,
        };
        var spawn = new CreatureSpawn { Guid = 993011, Entry = template.Entry, MapId = player.MapId, X = player.X, Y = player.Y, Z = player.Z };
        Creature npc = new(spawn.Guid, template, spawn,
            new CreatureContent([template], [spawn], [], [], []), new Random(1));
        player.Map!.AddObject(npc);
        return npc;
    }
}
