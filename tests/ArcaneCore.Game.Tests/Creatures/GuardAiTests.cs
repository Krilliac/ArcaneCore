using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// GuardAI (mangos Object/GuardAI.cpp) and creature-versus-creature aggro over a synthetic FactionTemplate catalog: who a guard
/// attacks on sight, how a hostile mob and a guard find each other through the creature relocation notify, and the switches.
/// Masks follow DBCEnums.h: FACTION_MASK_PLAYER 1, ALLIANCE 2, HORDE 4, MONSTER 8; the rest are made up for the test.
/// </summary>
public sealed class GuardAiTests
{
    private const uint PlayerFaction = 1;          // what TestWorld.CreatePlayer gives a player
    private const uint AllianceGuardFaction = 10;  // friendly to alliance players, hostile to horde and monsters
    private const uint ContestedGuardFaction = 11; // neutral to players, FACTION_TEMPLATE_FLAG_ATTACK_PVP_ACTIVE_PLAYERS
    private const uint NeutralGuardFaction = 12;   // neutral to players, friendly to its own town
    private const uint HostileMobFaction = 20;     // hostile to players and to the alliance guard, and vice versa
    private const uint TownMenaceFaction = 21;     // hostile to players and the alliance; no guard template is hostile to it
    private const uint TownsfolkFaction = 31;      // friendly to the neutral guard
    private const uint NeutralBeastFaction = 40;   // neutral to everyone

    private const uint MobEntry = 300;
    private const uint CivilianEntry = 301;

    private static readonly FactionTemplateCatalog Catalog = new(
    [
        new FactionTemplateRecord(PlayerFaction, 1, 0, OwnMask: 1 | 2, FriendlyMask: 2, HostileMask: 4),
        new FactionTemplateRecord(AllianceGuardFaction, 100, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 4 | 8),
        new FactionTemplateRecord(ContestedGuardFaction, 101, FactionTemplateCatalog.ContestedGuardFlag, OwnMask: 64, FriendlyMask: 0, HostileMask: 8),
        new FactionTemplateRecord(NeutralGuardFaction, 102, 0, OwnMask: 64, FriendlyMask: 64, HostileMask: 8),
        new FactionTemplateRecord(HostileMobFaction, 200, 0, OwnMask: 8, FriendlyMask: 8, HostileMask: 1 | 2),
        new FactionTemplateRecord(TownMenaceFaction, 201, 0, OwnMask: 16, FriendlyMask: 16, HostileMask: 1 | 2),
        new FactionTemplateRecord(TownsfolkFaction, 301, 0, OwnMask: 64, FriendlyMask: 64, HostileMask: 0),
        new FactionTemplateRecord(NeutralBeastFaction, 400, 0, OwnMask: 32, FriendlyMask: 0, HostileMask: 0),
    ]);

    private sealed record Town(WorldRuntime World, Map Map, CreatureMapSystem System, Creature Guard) : IDisposable
    {
        public void Dispose() => World.Dispose();

        public Creature Spawn(uint faction, float x, float y, float z = 83.5f, bool civilian = false)
            => System.SpawnTemporary(Template(civilian ? CivilianEntry : MobEntry) with { Faction = faction, Civilian = civilian }, x, y, z, 0);
    }

    private static CreatureTemplate GuardTemplate(uint faction = AllianceGuardFaction, string aiName = "")
        => Template(GuardEntry) with { Faction = faction, ExtraFlags = 0x400, AIName = aiName };

    /// <summary>
    /// A guard at the origin (a temporary spawn: it loads its own grid, so no player is needed to bring the town in); the faction
    /// hostility and the faction combat hooks. The guard's own relocation notify is run off before a test starts.
    /// </summary>
    private static Town Start(CreatureTemplate? guard = null, CreatureOptions? options = null)
    {
        CreatureContent content = Content([guard ?? GuardTemplate()], []);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(
            content, new CreatureAiServices { Hostility = new FactionCreatureHostility(Catalog) }, options ?? new CreatureOptions { RespawnPacifyMs = 0 });
        map.Combat.Hooks = new FactionCombatHooks(Catalog);
        Creature spawned = system.SpawnTemporary(guard ?? GuardTemplate(), 0, 0, 83.5f, 0);
        Run(runtime, 1100);
        Assert.Same(spawned, Assert.Single(system.Creatures));
        return new Town(runtime, map, system, spawned);
    }

    // --- selection -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0x400u, "", typeof(GuardAI))]            // the GUARD extra flag (mangos CreatureAISelector.cpp:85-88)
    [InlineData(0x400u, "AggressorAI", typeof(AggressorAI))] // an AIName wins over the flag
    [InlineData(0u, "GuardAI", typeof(GuardAI))]         // the registered name without the flag
    [InlineData(0u, "", typeof(AggressorAI))]
    public void TheGuardFlag_SelectsGuardAI_UnlessAnAiNameIsSet(uint extraFlags, string aiName, Type expected)
    {
        using Town t = Start(Template(GuardEntry) with { Faction = AllianceGuardFaction, ExtraFlags = extraFlags, AIName = aiName });

        Assert.IsType(expected, t.Guard.AI);
        Assert.True(t.Guard.AI!.AggroesOnSight);
    }

    // --- players -----------------------------------------------------------------------------------------------

    [Fact]
    public void AContestedGuard_AttacksAContestedPvpPlayer_AndIgnoresAnUnflaggedOne()
    {
        using Town flagged = Start(GuardTemplate(ContestedGuardFaction));
        (Player contested, _) = AddPlayer(flagged.World, 1, 10, 0);
        contested.Flags |= PlayerFlags.ContestedPvp; // before the relocation notify of joining the map runs
        Run(flagged.World, 1100);
        Assert.Same(contested, flagged.Guard.Combat.Victim);

        using Town calm = Start(GuardTemplate(ContestedGuardFaction));
        (Player bystander, _) = AddPlayer(calm.World, 2, 10, 0);
        Run(calm.World, 1100);
        Assert.Null(calm.Guard.Combat.Victim);
        Assert.False(calm.System.CanGuardAggroOnSight(calm.Guard, bystander));
    }

    [Fact]
    public void AnAllianceGuard_IgnoresAnAlliancePlayer()
    {
        using Town t = Start();
        (Player player, _) = AddPlayer(t.World, 1, 8, 0);
        player.Flags |= PlayerFlags.ContestedPvp; // not a contested guard: the flag means nothing to it

        Run(t.World, 1100);

        Assert.Null(t.Guard.Combat.Victim);
    }

    // --- creatures ---------------------------------------------------------------------------------------------

    [Fact]
    public void AHostileMobAndAGuard_AcquireEachOtherOnSight()
    {
        using Town t = Start();
        Creature mob = t.Spawn(HostileMobFaction, 10, 0);
        Assert.IsType<AggressorAI>(mob.AI);
        Assert.Null(t.Guard.Combat.Victim);

        Run(t.World, 1100); // the mob's relocation notify runs both directions (mangos CreatureCreatureRelocationWorker)

        Assert.Same(mob, t.Guard.Combat.Victim);
        Assert.Same(t.Guard, mob.Combat.Victim);
        Assert.Equal(MovementGeneratorType.Chase, t.Guard.Motion.CurrentType);
    }

    [Fact]
    public void AGuard_AttacksAMobThatIsHostileToPlayers_EvenWhenItsOwnTemplateIsNotHostileToIt()
    {
        // The guard's template hates horde and monsters, not the menace's own mask, so its own hostility says no; the menace hates
        // players (FACTION_MASK_PLAYER) and that alone makes the guard attack (GuardAI.cpp:74 IsHostileToPlayers). The pair is
        // attackable because the menace is hostile to the guard (FactionCombatHooks: hostile in either direction), which is also why
        // the menace, an aggressor, picks the guard up.
        using Town t = Start();
        Creature menace = t.Spawn(TownMenaceFaction, 8, 0);
        var hostility = new FactionCreatureHostility(Catalog);
        Assert.False(hostility.IsHostile(t.Guard, menace));
        Assert.True(hostility.IsHostileToPlayers(menace));
        Assert.False(hostility.IsHostileToPlayers(t.Guard));

        Run(t.World, 1100);

        Assert.Same(menace, t.Guard.Combat.Victim);
        Assert.Same(t.Guard, menace.Combat.Victim);
    }

    [Fact]
    public void AGuard_DefendsAFriendlyCreature_FromItsAttacker_WhenTheOptionIsOn()
    {
        using Town t = Start(GuardTemplate(NeutralGuardFaction));
        Creature townsman = t.Spawn(TownsfolkFaction, 5, 0, civilian: true);
        Assert.IsType<ReactorAI>(townsman.AI);
        (Player player, _) = AddPlayer(t.World, 1, 8, 0);
        Run(t.World, 1100);
        Assert.Null(t.Guard.Combat.Victim); // a neutral player minding its business

        Assert.True(t.Map.Combat.Attack(player, townsman));
        t.Guard.AI!.MoveInLineOfSight(player);

        Assert.Same(player, t.Guard.Combat.Victim);
    }

    [Fact]
    public void AGuard_DoesNotDefendANeutralCreature_NorAnyoneWithTheOptionOff()
    {
        using Town neutral = Start(GuardTemplate(NeutralGuardFaction));
        Creature beast = neutral.Spawn(NeutralBeastFaction, 5, 0);
        (Player hunter, _) = AddPlayer(neutral.World, 1, 8, 0);
        Assert.True(neutral.Map.Combat.Attack(hunter, beast));
        neutral.Guard.AI!.MoveInLineOfSight(hunter);
        Assert.Null(neutral.Guard.Combat.Victim);

        using Town off = Start(GuardTemplate(NeutralGuardFaction), new CreatureOptions { RespawnPacifyMs = 0, GuardsDefendFriendlies = false });
        Creature townsman = off.Spawn(TownsfolkFaction, 5, 0, civilian: true);
        (Player player, _) = AddPlayer(off.World, 2, 8, 0);
        Assert.True(off.Map.Combat.Attack(player, townsman));
        off.Guard.AI!.MoveInLineOfSight(player);
        Assert.Null(off.Guard.Combat.Victim);
    }

    [Fact]
    public void AGuardWithAVictim_IgnoresEveryoneElse()
    {
        using Town t = Start();
        Creature first = t.Spawn(HostileMobFaction, 6, 0);
        Run(t.World, 1100);
        Assert.Same(first, t.Guard.Combat.Victim);

        Creature second = t.Spawn(HostileMobFaction, 0, 6);
        Run(t.World, 1100);

        Assert.Same(first, t.Guard.Combat.Victim);
        Assert.False(t.System.CanGuardAggroOnSight(t.Guard, second));
        Assert.Same(t.Guard, second.Combat.Victim); // the mob, an aggressor, picks the guard up anyway
    }

    [Fact]
    public void CreatureAggroOnCreaturesOff_KeepsGuardsAndMobsApart()
    {
        using Town t = Start(options: new CreatureOptions { RespawnPacifyMs = 0, CreatureAggroOnCreatures = false });
        Creature mob = t.Spawn(HostileMobFaction, 8, 0);

        Run(t.World, 2200);

        Assert.Null(t.Guard.Combat.Victim);
        Assert.Null(mob.Combat.Victim);
        Assert.False(t.System.CanGuardAggroOnSight(t.Guard, mob));
        Assert.False(t.System.CanAggroOnSight(mob, t.Guard));
    }

    [Fact]
    public void TheVerticalLimit_AndTheAggroRadius_ApplyToCreatureTargets()
    {
        using Town t = Start();
        Creature above = t.Spawn(HostileMobFaction, 5, 0, z: 83.5f + 6f);  // more than 3 yd above
        Creature far = t.Spawn(HostileMobFaction, 25, 0);                   // beyond the 18 yd radius

        Run(t.World, 1100);

        Assert.Null(t.Guard.Combat.Victim);
        Assert.Null(above.Combat.Victim);
        Assert.Null(far.Combat.Victim);
    }

    [Fact]
    public void AnEvadingCreature_IsNotATargetOnSight()
    {
        using Town t = Start();
        Creature mob = t.Spawn(HostileMobFaction, 8, 0);
        mob.IsEvading = true;

        Assert.False(t.System.CanGuardAggroOnSight(t.Guard, mob));
        Assert.False(t.System.CanAggroOnSight(t.Guard, mob));
    }
}
