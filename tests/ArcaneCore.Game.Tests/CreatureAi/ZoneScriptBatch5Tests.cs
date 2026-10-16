using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Zone script batch 5: the Spitelash naga (azshara.cpp), the Rabid Thistle Bear (darkshore.cpp), Feero Ironhand (ashenvale.cpp) and Overlord
/// Mok'Morokk (dustwallow_marsh.cpp), against a real map with a recording spell caster and quest service. The escorts walk the 33 and 8 original
/// <c>script_waypoint</c> rows of ClassicDB z2815.
/// </summary>
public sealed class ZoneScriptBatch5Tests
{
    private const float Z = 83.5f;

    private sealed class RecordingCaster : ICreatureSpellCaster
    {
        public List<(Creature Caster, uint Spell, Unit? Target, bool Triggered)> Casts { get; } = [];

        /// <summary>What every cast answers (a refused cast leaves a script's timer expired).</summary>
        public CreatureCastResult Result { get; set; } = CreatureCastResult.Ok;

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add((caster, spellId, target, triggered));
            return Result;
        }

        public bool IsCasting(Creature caster) => false;

        public bool HasAura(Unit unit, uint spellId) => false;

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    private sealed class Quests : IScriptQuestEvents, IEventAiQuestEvents
    {
        public HashSet<uint> Incomplete { get; } = [];

        public List<(Player Player, uint QuestId)> Completed { get; } = [];

        public List<(Player Player, uint QuestId)> Failed { get; } = [];

        public List<(Player Player, uint Entry, ObjectGuid Source)> Credits { get; } = [];

        public bool IsQuestIncomplete(Player player, uint questId) => Incomplete.Contains(questId);

        public void AreaExploredOrEventHappens(Player player, uint questId) => Completed.Add((player, questId));

        public void FailQuest(Player player, uint questId) => Failed.Add((player, questId));

        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) => Credits.Add((player, creatureEntry, source));

        public void GroupEventFailHappens(Player player, uint questId) => Failed.Add((player, questId));

        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];

        public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup)
        {
            Assert.True(rewardGroup);
            Completed.Add((player, questId));
        }

        public void KillCredit(Player player, uint creatureEntry, Creature source)
        {
        }
    }

    /// <summary>One script creature (plus extra templates and spawns) next to a player, with its script texts, a recording caster and quest service.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(uint entry, Action<CreatureTemplateBuilder>? configure = null, int[]? textIds = null, bool hostile = false, float x = 0f, float y = 0f,
            float z = Z, CreatureTemplate[]? extraTemplates = null, CreatureSpawn[]? extraSpawns = null,
            IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? path = null)
        {
            var aiContent = new CreatureAiContent([], [.. (textIds ?? []).Select(id => new CreatureAiText(id, $"text{id}", 6, 0, 0))]);
            CreatureContent content = new([Template(entry, configure), .. extraTemplates ?? []], [Spawn(1, entry, x, y, z), .. extraSpawns ?? []], [], [], [],
                aiContent, scriptWaypoints: path);
            var services = new CreatureAiServices
            {
                ScriptQuests = Quest, QuestEvents = Quest, Spells = Caster, Hostility = hostile ? new AlwaysHostile() : FactionCreatureHostility.Empty,
            };
            (World, Map, System) = CreateAiSystem(content, services);
            (Player, Session) = AddPlayer(World, 1, x + 1f, y);
            Npc = Assert.Single(System.Creatures, c => c.Template.Entry == entry);
        }

        public RecordingCaster Caster { get; } = new();

        public Quests Quest { get; } = new();

        public WorldRuntime World { get; }

        public Map Map { get; }

        public CreatureMapSystem System { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public Creature Npc { get; }

        public T Ai<T>() where T : CreatureAI => Assert.IsType<T>(Npc.AI);

        /// <summary>The (speaker, line) pairs the player has heard since the last <see cref="FakeSession.Clear"/>.</summary>
        public List<(string Name, string Message)> Chat() => [.. Packets(Session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p)).Select(c => (c.Name, c.Message))];

        public List<string> Heard() => [.. Chat().Select(c => c.Message)];

        public void RunUntil(Func<bool> done, int limitMs = 60_000)
        {
            for (int elapsed = 0; elapsed < limitMs && !done(); elapsed += 100)
            {
                World.RunTick(100);
            }
        }

        /// <summary>Ticks until <paramref name="done"/> and returns the milliseconds that took (to the next 100 ms).</summary>
        public int TimeUntil(Func<bool> done, int limitMs = 60_000)
        {
            int elapsed = 0;
            for (; elapsed < limitMs && !done(); elapsed += 100)
            {
                World.RunTick(100);
            }

            Assert.True(done(), $"not done within {limitMs} ms");
            return elapsed;
        }

        public void Dispose() => World.Dispose();
    }

    private static Action<CreatureTemplateBuilder> Health(uint health, uint faction) => b =>
    {
        b.Faction = faction;
        b.MinLevelHealth = health;
        b.MaxLevelHealth = health;
    };

    [Theory]
    [InlineData(SpitelashAI.NpcWarrior, typeof(SpitelashAI))]
    [InlineData(SpitelashAI.NpcScreamer, typeof(SpitelashAI))]
    [InlineData(SpitelashAI.NpcSerpentGuard, typeof(SpitelashAI))]
    [InlineData(SpitelashAI.NpcSiren, typeof(SpitelashAI))]
    [InlineData(SpitelashAI.NpcMyrmidon, typeof(SpitelashAI))]
    [InlineData(RabidBearAI.Entry, typeof(RabidBearAI))]
    [InlineData(FeeroIronhandAI.Entry, typeof(FeeroIronhandAI))]
    [InlineData(MorokkAI.Entry, typeof(MorokkAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        using var rig = new Rig(entry);
        Assert.IsType(ai, rig.Npc.AI);
    }

    [Fact]
    public void TheEscortQuestsAreListedAsScripted()
    {
        IReadOnlyCollection<uint> quests = new CreatureAiFactory().ScriptedEventQuests;
        Assert.Contains(FeeroIronhandAI.QuestSuppliesToAuberdine, quests);
        Assert.Contains(MorokkAI.QuestChallengeMorokk, quests);
    }

    // --- the Spitelash ---------------------------------------------------------------------------------------

    private static Rig SpitelashRig(uint entry, Action<CreatureTemplateBuilder>? configure = null, bool hostile = true)
        => new(entry, configure ?? (b => b.Faction = 74), hostile: hostile);

    [Theory]
    [InlineData(SpitelashAI.NpcWarrior, SpitelashAI.SpellDisarm, false, 4000, 10000)]
    [InlineData(SpitelashAI.NpcScreamer, SpitelashAI.SpellScreech, true, 7000, 15000)]
    [InlineData(SpitelashAI.NpcSerpentGuard, SpitelashAI.SpellFrostShock, false, 7000, 13000)]
    [InlineData(SpitelashAI.NpcMyrmidon, SpitelashAI.SpellStrike, false, 3000, 7000)]
    public void ASpitelashUsesItsOneAbilityOnItsTarget_AfterItsFirstTimer_AndThenEveryCooldown(uint entry, uint spell, bool onSelf, int initialMs, int cooldownMs)
    {
        using Rig rig = SpitelashRig(entry);
        Assert.Empty(rig.Caster.Casts);
        rig.System.AttackStart(rig.Npc, rig.Player);

        // A timer fires when it is below the elapsed time: the first update after it ran out, and nothing before.
        int first = rig.TimeUntil(() => rig.Caster.Casts.Count == 1);
        Assert.InRange(first, initialMs, initialMs + 300);
        Assert.Equal(spell, rig.Caster.Casts[0].Spell);
        Assert.Same(onSelf ? rig.Npc : rig.Player, rig.Caster.Casts[0].Target);
        Assert.False(rig.Caster.Casts[0].Triggered);

        int second = rig.TimeUntil(() => rig.Caster.Casts.Count == 2);
        Assert.InRange(second, cooldownMs, cooldownMs + 300);
        Assert.Equal(spell, rig.Caster.Casts[1].Spell);
    }

    [Fact]
    public void ATimerDoesNotRunBeforeTheFight()
    {
        using Rig rig = SpitelashRig(SpitelashAI.NpcWarrior, hostile: false);
        Run(rig.World, 20_000);
        Assert.Empty(rig.Caster.Casts);
        Assert.Equal(4000u, Assert.Single(rig.Ai<SpitelashAI>().Timers).TimerMs);
    }

    [Fact]
    public void ARefusedCastLeavesTheTimerExpired_AndTheNextUpdateTriesAgain()
    {
        using Rig rig = SpitelashRig(SpitelashAI.NpcWarrior);
        rig.Caster.Result = CreatureCastResult.Failed;
        rig.System.AttackStart(rig.Npc, rig.Player);

        Run(rig.World, 4_500);
        int refused = rig.Caster.Casts.Count;
        Assert.True(refused >= 3, $"retried each update after the timer ran out, got {refused}");

        rig.Caster.Result = CreatureCastResult.Ok;
        rig.World.RunTick(100);
        Assert.Equal(refused + 1, rig.Caster.Casts.Count);
        Run(rig.World, 9_000);
        Assert.Equal(refused + 1, rig.Caster.Casts.Count); // now it waits its 10 s cooldown
        Run(rig.World, 1_500);
        Assert.Equal(refused + 2, rig.Caster.Casts.Count);
    }

    [Fact]
    public void ASpitelashEvadeRestoresItsTimers()
    {
        using Rig rig = SpitelashRig(SpitelashAI.NpcSerpentGuard);
        var ai = rig.Ai<SpitelashAI>();
        rig.System.AttackStart(rig.Npc, rig.Player);
        Run(rig.World, 3_000);
        Assert.True(Assert.Single(ai.Timers).TimerMs < 7000);

        ai.EnterEvadeMode();

        Assert.Equal(7000u, Assert.Single(ai.Timers).TimerMs);
        Assert.Equal(0u, ai.MorphMs);
    }

    [Fact]
    public void TheSirenHasFourAbilities_RenewOnTheWoundedFriend_ShootAndFrostShotOnAFighter_FrostNovaOnHerself()
    {
        using Rig rig = SpitelashRig(SpitelashAI.NpcSiren);
        var ai = rig.Ai<SpitelashAI>();
        Assert.Equal([SpitelashAI.SpellRenew, SpitelashAI.SpellShoot, SpitelashAI.SpellFrostShot, SpitelashAI.SpellFrostNova], ai.Timers.Select(t => t.Ability.Spell));
        Assert.Equal([4000u, 3000u, 7000u, 10000u], ai.Timers.Select(t => t.TimerMs));

        // Wounded and in a fight with the player, she is her own lowest-health friend.
        rig.Map.Combat.DealDamage(rig.Player, rig.Npc, 5);
        var seen = new Dictionary<uint, int>();
        int elapsed = 0;
        for (; elapsed < 16_000; elapsed += 100)
        {
            int before = rig.Caster.Casts.Count;
            rig.World.RunTick(100);
            foreach ((Creature _, uint spell, Unit? target, bool _) in rig.Caster.Casts.Skip(before))
            {
                seen.TryAdd(spell, elapsed);
                Assert.Same(spell is SpitelashAI.SpellRenew or SpitelashAI.SpellFrostNova ? rig.Npc : rig.Player, target);
            }
        }

        // The earlier timers pass a tick on to the later ones when one of them casts, so each is up to a few ticks late.
        Assert.InRange(seen[SpitelashAI.SpellShoot], 3000, 3600);
        Assert.InRange(seen[SpitelashAI.SpellRenew], 4000, 4700);
        Assert.InRange(seen[SpitelashAI.SpellFrostShot], 7000, 7800);
        Assert.InRange(seen[SpitelashAI.SpellFrostNova], 10000, 10900);
    }

    [Fact]
    public void TheSirenDoesNotRenewAnUnwoundedGroup()
    {
        using Rig rig = SpitelashRig(SpitelashAI.NpcSiren);
        rig.System.AttackStart(rig.Npc, rig.Player);

        Run(rig.World, 12_000);

        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == SpitelashAI.SpellRenew);
        Assert.Contains(rig.Caster.Casts, c => c.Spell == SpitelashAI.SpellShoot);
    }

    [Fact]
    public void ARandomAbilityPicksAmongTheLivingFightersOnItsThreatList()
    {
        using Rig rig = SpitelashRig(SpitelashAI.NpcSiren);
        (Player second, _) = AddPlayer(rig.World, 2, 1, 1);
        rig.Map.Combat.DealDamage(rig.Player, rig.Npc, 1);
        rig.Map.Combat.DealDamage(second, rig.Npc, 1);

        Run(rig.World, 30_000);

        var shots = rig.Caster.Casts.Where(c => c.Spell is SpitelashAI.SpellShoot or SpitelashAI.SpellFrostShot).ToList();
        Assert.True(shots.Count >= 4);
        Assert.All(shots, c => Assert.True(ReferenceEquals(c.Target, rig.Player) || ReferenceEquals(c.Target, second)));
        Assert.Contains(shots, c => ReferenceEquals(c.Target, rig.Player));
        Assert.Contains(shots, c => ReferenceEquals(c.Target, second));
    }

    [Theory]
    [InlineData(118u)]
    [InlineData(12824u)]
    [InlineData(12825u)]
    [InlineData(12826u)]
    public void APlayerOnFragmentedMagicWhoPolymorphsOneStartsTheFiveSecondCountdown(uint polymorph)
    {
        using Rig rig = SpitelashRig(SpitelashAI.NpcWarrior);
        rig.Quest.Incomplete.Add(SpitelashAI.QuestFragmentedMagic);
        var ai = rig.Ai<SpitelashAI>();

        ai.OnSpellHit(rig.Player, new SpellInfo { Id = polymorph });

        Assert.Equal(5000u, ai.MorphMs);
    }

    [Fact]
    public void ThePolymorphIsIgnoredWithoutTheQuestFromANonPlayerOrOfAnotherSpell()
    {
        using Rig rig = SpitelashRig(SpitelashAI.NpcWarrior);
        var ai = rig.Ai<SpitelashAI>();

        ai.OnSpellHit(rig.Player, new SpellInfo { Id = 118 }); // the quest is not in the log
        Assert.Equal(0u, ai.MorphMs);

        rig.Quest.Incomplete.Add(SpitelashAI.QuestFragmentedMagic);
        ai.OnSpellHit(rig.Npc, new SpellInfo { Id = 118 }); // a creature's polymorph
        ai.OnSpellHit(rig.Player, new SpellInfo { Id = 12827 }); // not a rank of Polymorph the script lists
        ai.OnSpellHit(rig.Player, new SpellInfo { Id = SpitelashAI.SpellDisarm });
        Assert.Equal(0u, ai.MorphMs);
    }

    [Fact]
    public void ThePolymorphedNagaBacksFireAndGoesFiveSecondsIntoTheFight_AndASecondHitDoesNotRestartIt()
    {
        using Rig rig = SpitelashRig(SpitelashAI.NpcSerpentGuard);
        rig.Quest.Incomplete.Add(SpitelashAI.QuestFragmentedMagic);
        var ai = rig.Ai<SpitelashAI>();
        rig.System.AttackStart(rig.Npc, rig.Player);
        ai.OnSpellHit(rig.Player, new SpellInfo { Id = 118 });

        Run(rig.World, 1_000);
        uint left = ai.MorphMs;
        Assert.InRange(left, 3_800u, 4_100u);
        ai.OnSpellHit(rig.Player, new SpellInfo { Id = 12824 });
        Assert.Equal(left, ai.MorphMs);

        Run(rig.World, 3_700);
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == SpitelashAI.SpellPolymorphBackfire);
        Assert.True(rig.Npc.IsAlive);

        Run(rig.World, 500);
        (Creature caster, _, Unit? target, bool triggered) = Assert.Single(rig.Caster.Casts, c => c.Spell == SpitelashAI.SpellPolymorphBackfire);
        Assert.Same(rig.Npc, caster);
        Assert.Same(rig.Npc, target);
        Assert.True(triggered);
        Assert.False(rig.Npc.IsAlive); // ForcedDespawn
        Assert.Equal(0u, ai.MorphMs);
    }

    [Fact]
    public void TheCountdownRunsOnlyInCombat_AndARefusedBackfireIsRetried()
    {
        using Rig rig = SpitelashRig(SpitelashAI.NpcWarrior, hostile: false);
        rig.Quest.Incomplete.Add(SpitelashAI.QuestFragmentedMagic);
        var ai = rig.Ai<SpitelashAI>();
        ai.OnSpellHit(rig.Player, new SpellInfo { Id = 118 });

        Run(rig.World, 10_000); // no fight: the UpdateVictim guard returns first
        Assert.Equal(5000u, ai.MorphMs);
        Assert.Empty(rig.Caster.Casts);

        rig.Caster.Result = CreatureCastResult.Failed;
        rig.System.AttackStart(rig.Npc, rig.Player);
        Run(rig.World, 5_500);
        Assert.Contains(rig.Caster.Casts, c => c.Spell == SpitelashAI.SpellPolymorphBackfire);
        Assert.True(rig.Npc.IsAlive);
        Assert.NotEqual(0u, ai.MorphMs); // still counting: the cast was refused, so no despawn

        rig.Caster.Result = CreatureCastResult.Ok;
        Run(rig.World, 200);
        Assert.False(rig.Npc.IsAlive);
    }

    // --- the Rabid Thistle Bear ------------------------------------------------------------------------------

    private static Rig BearRig(float treetenderDistance = 0f)
    {
        List<CreatureTemplate> extras = [Template(RabidBearAI.CapturedEntry, b => b.Faction = 35), Template(RabidBearAI.NpcTharnariunTreetender, b => b.Faction = 35)];
        List<CreatureSpawn> spawns = treetenderDistance > 0 ? [Spawn(2, RabidBearAI.NpcTharnariunTreetender, treetenderDistance, 0)] : [];
        return new Rig(RabidBearAI.Entry, b => b.Faction = 44, hostile: false, extraTemplates: [.. extras], extraSpawns: [.. spawns]);
    }

    private static void Capture(Rig rig, Unit? caster = null, uint spell = RabidBearAI.SpellBearCaptured)
        => rig.Ai<RabidBearAI>().OnSpellHit(caster ?? rig.Player, new SpellInfo { Id = spell });

    [Fact]
    public void TheBearCastsRabiesOnItsTopThreatEveryTwelveToEighteenSeconds()
    {
        using Rig rig = BearRig();
        var ai = rig.Ai<RabidBearAI>();
        Assert.InRange(ai.RabiesMs, RabidBearAI.RabiesMinMs, RabidBearAI.RabiesMaxMs);
        rig.System.AttackStart(rig.Npc, rig.Player);

        int first = rig.TimeUntil(() => rig.Caster.Casts.Count == 1);
        Assert.InRange(first, 12_000, 18_300);
        (Creature caster, uint spell, Unit? target, bool triggered) = rig.Caster.Casts[0];
        Assert.Same(rig.Npc, caster);
        Assert.Equal(RabidBearAI.SpellRabies, spell);
        Assert.Same(rig.Player, target);
        Assert.False(triggered);

        int second = rig.TimeUntil(() => rig.Caster.Casts.Count == 2);
        Assert.InRange(second, 12_000, 18_300);
    }

    [Fact]
    public void TheBearsCaptureWithTheQuestIncompleteCreditsThePlayerAndMakesTheBearFollow()
    {
        using Rig rig = BearRig();
        rig.Quest.Incomplete.Add(RabidBearAI.QuestPlaguedLands);
        rig.System.AttackStart(rig.Npc, rig.Player);
        rig.World.RunTick(100);
        Assert.True(rig.Npc.Combat.IsInCombat);
        var ai = rig.Ai<RabidBearAI>();

        Capture(rig);

        Assert.Equal(RabidBearAI.CapturedEntry, rig.Npc.Template.Entry);
        Assert.False(rig.Npc.Combat.IsInCombat);
        Assert.Equal(35u, rig.Npc.FactionTemplate);
        (Player credited, uint entry, ObjectGuid source) = Assert.Single(rig.Quest.Credits);
        Assert.Same(rig.Player, credited);
        Assert.Equal(RabidBearAI.CapturedEntry, entry); // the credit is for the new entry, as UpdateEntry ran first
        Assert.Equal(rig.Npc.Guid, source);
        Assert.Equal(MovementGeneratorType.Follow, rig.Npc.Motion.CurrentType);
        Assert.True(ai.Captured);
        Assert.Equal(RabidBearAI.DespawnAfterCaptureMs, ai.DespawnMs);
        Assert.True(rig.Npc.IsAlive);
    }

    [Fact]
    public void TheBearsCaptureWithoutTheQuestDespawnsItAfterASecond_AndGivesNoCredit()
    {
        using Rig rig = BearRig();

        Capture(rig);

        Assert.Equal(RabidBearAI.CapturedEntry, rig.Npc.Template.Entry);
        Assert.Empty(rig.Quest.Credits);
        Assert.NotEqual(MovementGeneratorType.Follow, rig.Npc.Motion.CurrentType);
        Run(rig.World, 800);
        Assert.True(rig.Npc.IsAlive);
        Run(rig.World, 400);
        Assert.False(rig.Npc.IsAlive);
    }

    [Fact]
    public void OnlyAPlayersBearCapturedTrapCountsAsTheCapture()
    {
        using Rig rig = BearRig();
        rig.Quest.Incomplete.Add(RabidBearAI.QuestPlaguedLands);

        Capture(rig, caster: rig.Npc); // not a player
        Capture(rig, spell: 12345);

        Assert.Equal(RabidBearAI.Entry, rig.Npc.Template.Entry);
        Assert.False(rig.Ai<RabidBearAI>().Captured);
        Assert.Empty(rig.Quest.Credits);
    }

    [Fact]
    public void ACapturedBearGoesThreeMinutesLater()
    {
        using Rig rig = BearRig();
        rig.Quest.Incomplete.Add(RabidBearAI.QuestPlaguedLands);
        Capture(rig);

        Run(rig.World, 170_000);
        Assert.True(rig.Npc.IsAlive);
        Assert.InRange(rig.Ai<RabidBearAI>().DespawnMs, 1u, 10_100u);
        Run(rig.World, 11_000);
        Assert.False(rig.Npc.IsAlive);
    }

    [Fact]
    public void TheDespawnCountdownIsHeldWhileTheCapturedBearFights()
    {
        using Rig rig = BearRig();
        rig.Quest.Incomplete.Add(RabidBearAI.QuestPlaguedLands);
        Capture(rig);
        var ai = rig.Ai<RabidBearAI>();
        Run(rig.World, 10_000);
        uint left = ai.DespawnMs;
        Assert.InRange(left, 169_000u, 170_100u);

        rig.System.AttackStart(rig.Npc, rig.Player);
        rig.World.RunTick(100);
        Assert.True(rig.Npc.Combat.IsInCombat);
        Run(rig.World, 500);

        Assert.Equal(left, ai.DespawnMs);
    }

    [Fact]
    public void ACapturedBearThatSeesTharnariunTreetenderWithin10YardsStopsAndGoesAfterASecond()
    {
        using Rig rig = BearRig(treetenderDistance: 9f);
        rig.Quest.Incomplete.Add(RabidBearAI.QuestPlaguedLands);
        Creature treetender = Assert.Single(rig.System.Creatures, c => c.Template.Entry == RabidBearAI.NpcTharnariunTreetender);
        Capture(rig);
        Assert.Equal(MovementGeneratorType.Follow, rig.Npc.Motion.CurrentType);

        rig.Ai<RabidBearAI>().MoveInLineOfSight(treetender);

        Assert.NotEqual(MovementGeneratorType.Follow, rig.Npc.Motion.CurrentType);
        Run(rig.World, 800);
        Assert.True(rig.Npc.IsAlive);
        Run(rig.World, 400);
        Assert.False(rig.Npc.IsAlive);
    }

    [Fact]
    public void TheTreetenderBeingSeenOnEveryUpdateDoesNotPostponeTheDespawn()
    {
        using Rig rig = BearRig(treetenderDistance: 9f);
        rig.Quest.Incomplete.Add(RabidBearAI.QuestPlaguedLands);
        Capture(rig);
        Creature treetender = Assert.Single(rig.System.Creatures, c => c.Template.Entry == RabidBearAI.NpcTharnariunTreetender);

        for (int i = 0; i < 15 && rig.Npc.IsAlive; i++)
        {
            rig.Ai<RabidBearAI>().MoveInLineOfSight(treetender); // the engine reports a creature in sight on every update
            rig.World.RunTick(100);
        }

        Assert.False(rig.Npc.IsAlive);
    }

    [Fact]
    public void TheTreetenderDoesNothingBeforeTheCaptureOrFromFarAwayOrWhenAnotherCreature()
    {
        using Rig rig = BearRig(treetenderDistance: 9f);
        rig.Quest.Incomplete.Add(RabidBearAI.QuestPlaguedLands);
        Creature treetender = Assert.Single(rig.System.Creatures, c => c.Template.Entry == RabidBearAI.NpcTharnariunTreetender);
        var ai = rig.Ai<RabidBearAI>();

        ai.MoveInLineOfSight(treetender); // not captured yet
        Run(rig.World, 2_000);
        Assert.True(rig.Npc.IsAlive);

        Capture(rig);
        treetender.Relocate(rig.Npc.X + 11f, rig.Npc.Y, rig.Npc.Z); // 11 yd away
        ai.MoveInLineOfSight(treetender);
        ai.MoveInLineOfSight(rig.Player); // a player in range
        Run(rig.World, 2_000);
        Assert.True(rig.Npc.IsAlive);
        Assert.Equal(MovementGeneratorType.Follow, rig.Npc.Motion.CurrentType);
    }

    [Fact]
    public void TheTreetenderIsMeasuredOnTheFlat()
    {
        using Rig rig = BearRig(treetenderDistance: 9f);
        rig.Quest.Incomplete.Add(RabidBearAI.QuestPlaguedLands);
        Creature treetender = Assert.Single(rig.System.Creatures, c => c.Template.Entry == RabidBearAI.NpcTharnariunTreetender);
        Capture(rig);
        treetender.Relocate(rig.Npc.X + 9f, rig.Npc.Y, rig.Npc.Z + 40f); // 40 yd above: IsWithinDist(..., false) ignores the height

        rig.Ai<RabidBearAI>().MoveInLineOfSight(treetender);

        Run(rig.World, 1_200);
        Assert.False(rig.Npc.IsAlive);
    }

    [Fact]
    public void ARespawnedBearIsNotCapturedAndHasNoDespawnPending()
    {
        using Rig rig = BearRig();
        rig.Quest.Incomplete.Add(RabidBearAI.QuestPlaguedLands);
        var ai = rig.Ai<RabidBearAI>();
        Capture(rig);
        Assert.True(ai.Captured);

        ai.OnRespawn();

        Assert.False(ai.Captured);
        Assert.Equal(0u, ai.DespawnMs);
        Assert.InRange(ai.RabiesMs, RabidBearAI.RabiesMinMs, RabidBearAI.RabiesMaxMs);
    }

    // --- Feero Ironhand --------------------------------------------------------------------------------------

    // The 33 script_waypoint rows of Feero Ironhand (4484) in ClassicDB z2815: point, x, y, z, orientation, wait (ms).
    private static readonly CreatureWaypoint[] s_feeroPath =
    [
        new(1, 3178.57f, 188.52f, 4.27f, 0f, 0), new(2, 3189.82f, 198.56f, 5.62f, 0f, 0), new(3, 3215.21f, 185.78f, 6.43f, 0f, 0),
        new(4, 3224.05f, 183.08f, 6.74f, 0f, 0), new(5, 3228.11f, 194.97f, 7.51f, 0f, 0), new(6, 3225.33f, 201.78f, 7.25f, 0f, 0),
        new(7, 3233.33f, 226.88f, 10.18f, 0f, 0), new(8, 3274.12f, 225.83f, 10.72f, 0f, 0), new(9, 3321.63f, 209.82f, 12.36f, 0f, 0),
        new(10, 3369.66f, 226.21f, 11.69f, 0f, 0), new(11, 3402.35f, 227.2f, 9.48f, 0f, 0), new(12, 3441.92f, 224.75f, 10.85f, 0f, 0),
        new(13, 3453.87f, 220.31f, 12.52f, 0f, 0), new(14, 3472.51f, 213.68f, 13.26f, 0f, 0), new(15, 3515.49f, 212.96f, 9.76f, 0f, 5000),
        new(16, 3516.21f, 212.84f, 9.52f, 0f, 20000), new(17, 3548.22f, 217.12f, 7.34f, 0f, 0), new(18, 3567.57f, 219.43f, 5.22f, 0f, 0),
        new(19, 3659.85f, 209.68f, 2.27f, 0f, 0), new(20, 3734.9f, 177.64f, 6.75f, 0f, 0), new(21, 3760.24f, 162.51f, 7.49f, 0f, 5000),
        new(22, 3761.58f, 161.14f, 7.37f, 0f, 20000), new(23, 3801.17f, 129.87f, 9.38f, 0f, 0), new(24, 3815.53f, 118.53f, 10.14f, 0f, 0),
        new(25, 3894.58f, 44.88f, 15.49f, 0f, 0), new(26, 3972.83f, 0.42f, 17.34f, 0f, 0), new(27, 4026.41f, -7.63f, 16.77f, 0f, 0),
        new(28, 4086.24f, 12.32f, 16.12f, 0f, 0), new(29, 4158.79f, 50.67f, 25.86f, 0f, 0), new(30, 4223.48f, 99.52f, 35.47f, 0f, 5000),
        new(31, 4224.28f, 100.02f, 35.49f, 0f, 10000), new(32, 4243.45f, 117.44f, 38.83f, 0f, 0), new(33, 4264.18f, 134.22f, 42.96f, 0f, 0),
    ];

    private static readonly int[] s_feeroTexts =
    [
        FeeroIronhandAI.SayQuestStart, FeeroIronhandAI.SayFirstAmbushStart, FeeroIronhandAI.SayFirstAmbushEnd, FeeroIronhandAI.SaySecondAmbushStart,
        FeeroIronhandAI.SayScoutSecondAmbush, FeeroIronhandAI.SaySecondAmbushEnd, FeeroIronhandAI.SayFinalAmbushStart, FeeroIronhandAI.SayBalizarFinalAmbush,
        FeeroIronhandAI.SayFinalAmbushAttack, FeeroIronhandAI.SayQuestEnd,
    ];

    private static CreatureTemplate Named(uint entry, string name) => Template(entry, b =>
    {
        b.Name = name;
        b.Faction = 14;
    });

    private static Rig FeeroRig()
    {
        CreatureWaypoint start = s_feeroPath[0];
        return new Rig(FeeroIronhandAI.Entry, b =>
            {
                b.Name = "Feero";
                b.Faction = 123;
                b.NpcFlags = (uint)NpcFlags.QuestGiver;
                b.UnitFlags = (uint)UnitFlags.ImmuneToNpc;
            }, s_feeroTexts, hostile: true, x: start.X, y: start.Y, z: start.Z,
            extraTemplates:
            [
                Named(FeeroIronhandAI.NpcDarkStrandAssassin, "Assassin"), Named(FeeroIronhandAI.NpcForsakenScout, "Scout"),
                Named(FeeroIronhandAI.NpcAligar, "Aligar"), Named(FeeroIronhandAI.NpcBalizar, "Balizar"), Named(FeeroIronhandAI.NpcCaedakar, "Caedakar"),
            ],
            path: s_feeroPath.Select(p => (FeeroIronhandAI.Entry, EscortAI.EscortPathId, p)));
    }

    /// <summary>Puts the escort on <paramref name="point"/> as if it had walked there, and lets the escort AI hear of the arrival.</summary>
    private static void Reach(Rig rig, CreatureWaypoint[] path, uint point)
    {
        var ai = Assert.IsAssignableFrom<EscortAI>(rig.Npc.AI);
        Assert.True(ai.SetNextWaypointByPointId(point));
        CreatureWaypoint at = path.Single(p => p.Point == point);
        rig.Npc.Relocate(at.X, at.Y, at.Z);
        ai.OnMovementInform(MovementGeneratorType.Point, point);
    }

    private static List<Creature> Summons(Rig rig, uint entry) => [.. rig.System.Creatures.Where(c => c.Template.Entry == entry && !ReferenceEquals(c, rig.Npc))];

    [Fact]
    public void Feero_AcceptingTheQuestSaysHisLineTakesTheEscortFactionAndStartsAtARun_AnotherQuestDoesNothing()
    {
        using Rig rig = FeeroRig();
        var ai = rig.Ai<FeeroIronhandAI>();

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(123u, rig.Npc.FactionTemplate);
        Assert.Empty(rig.Heard());

        ai.OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);

        Assert.Equal([$"text{FeeroIronhandAI.SayQuestStart}"], rig.Heard());
        Assert.Equal(FeeroIronhandAI.FactionEscortNeutralPassive, rig.Npc.FactionTemplate);
        Assert.Equal(TemporaryFactionFlags.RestoreRespawn, rig.Npc.TemporaryFactionFlags);
        Assert.Equal(UnitFlags.None, rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.True(ai.IsRunning);
        Assert.Equal(0u, rig.Npc.NpcFlags); // the escort start clears the NPC flags
    }

    [Fact]
    public void Feero_WalksHisPathAfterTheFirstDelay()
    {
        using Rig rig = FeeroRig();
        rig.Ai<FeeroIronhandAI>().OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);
        float startX = rig.Npc.X;

        Run(rig.World, 2_000);
        Assert.Equal(startX, rig.Npc.X, 0.5f);
        Run(rig.World, 4_000);
        Assert.True(rig.Npc.X > startX + 3f, "he walks to the second point and on");
    }

    [Fact]
    public void Feero_RespawningGivesHimBackTheCreatureImmunityAndHisOwnFaction()
    {
        using Rig rig = FeeroRig();
        var ai = rig.Ai<FeeroIronhandAI>();
        ai.OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);

        ai.OnRespawn();

        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(UnitFlags.ImmuneToNpc, rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc);
        Assert.Equal(123u, rig.Npc.FactionTemplate);
    }

    [Fact]
    public void Feero_TheFirstAmbushIsFourAssassinsInAFanThirtyYardsAway_AllAttackingHim()
    {
        using Rig rig = FeeroRig();
        rig.Ai<FeeroIronhandAI>().OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);
        rig.Session.Clear();

        Reach(rig, s_feeroPath, 15);

        Assert.Equal([$"text{FeeroIronhandAI.SayFirstAmbushStart}"], rig.Heard());
        List<Creature> assassins = Summons(rig, FeeroIronhandAI.NpcDarkStrandAssassin);
        Assert.Equal(4, assassins.Count);
        Assert.Equal(4, rig.Ai<FeeroIronhandAI>().CreaturesCount);
        float[] angles = [1.25f, 1.25f - (MathF.PI / 4), 1.25f - (MathF.PI / 2), 1.25f - (3 * MathF.PI / 4)];
        foreach (float angle in angles)
        {
            float x = rig.Npc.X + (30f * MathF.Cos(angle)), y = rig.Npc.Y + (30f * MathF.Sin(angle));
            Assert.Contains(assassins, a => MathF.Abs(a.X - x) < 0.05f && MathF.Abs(a.Y - y) < 0.05f);
        }

        Assert.All(assassins, a => Assert.Same(rig.Npc, a.Combat.Victim));
    }

    [Fact]
    public void Feero_TheAmbushersGoTwentySecondsAfterTheirFightIfTheyAreNotInCombat()
    {
        using Rig rig = FeeroRig();
        var ai = rig.Ai<FeeroIronhandAI>();
        ai.OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);
        Reach(rig, s_feeroPath, 15);
        List<Creature> assassins = Summons(rig, FeeroIronhandAI.NpcDarkStrandAssassin);
        foreach (Creature assassin in assassins)
        {
            rig.System.StopCombatInPlace(assassin);
        }

        Run(rig.World, 19_000);
        Assert.All(assassins, a => Assert.Contains(a, rig.System.Creatures));
        Run(rig.World, 2_000);
        Assert.All(assassins, a => Assert.DoesNotContain(a, rig.System.Creatures));
    }

    [Fact]
    public void Feero_TheLastAssassinToDieMakesHimSpeak_NotTheOthers()
    {
        using Rig rig = FeeroRig();
        rig.Ai<FeeroIronhandAI>().OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);
        Reach(rig, s_feeroPath, 15);
        List<Creature> assassins = Summons(rig, FeeroIronhandAI.NpcDarkStrandAssassin);
        rig.Session.Clear();

        foreach (Creature assassin in assassins.Take(3))
        {
            rig.Map.Combat.Kill(null, assassin);
        }

        Assert.Equal(1, rig.Ai<FeeroIronhandAI>().CreaturesCount);
        Assert.Empty(rig.Heard());

        rig.Map.Combat.Kill(null, assassins[3]);

        Assert.Equal(0, rig.Ai<FeeroIronhandAI>().CreaturesCount);
        Assert.Equal([("Feero", $"text{FeeroIronhandAI.SayFirstAmbushEnd}")], rig.Chat());
    }

    [Fact]
    public void Feero_TheSecondAmbushIsThreeScoutsAndOnlyTheSecondToArriveCallsOutToHim()
    {
        using Rig rig = FeeroRig();
        rig.Ai<FeeroIronhandAI>().OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);
        rig.Session.Clear();

        Reach(rig, s_feeroPath, 21);

        List<Creature> scouts = Summons(rig, FeeroIronhandAI.NpcForsakenScout);
        Assert.Equal(3, scouts.Count);
        Assert.Equal([("Feero", $"text{FeeroIronhandAI.SaySecondAmbushStart}"), ("Scout", $"text{FeeroIronhandAI.SayScoutSecondAmbush}")], rig.Chat());
        float[] angles = [0.95f, 0.95f - (MathF.PI / 3), 0.95f - (2 * MathF.PI / 3)];
        foreach (float angle in angles)
        {
            float x = rig.Npc.X + (30f * MathF.Cos(angle)), y = rig.Npc.Y + (30f * MathF.Sin(angle));
            Assert.Contains(scouts, a => MathF.Abs(a.X - x) < 0.05f && MathF.Abs(a.Y - y) < 0.05f);
        }

        rig.Session.Clear();
        foreach (Creature scout in scouts)
        {
            rig.Map.Combat.Kill(null, scout);
        }

        Assert.Equal([("Feero", $"text{FeeroIronhandAI.SaySecondAmbushEnd}")], rig.Chat());
    }

    [Fact]
    public void Feero_TheFinalAmbushIsTheThreeElitesAtTheirSpots_BalizarYellsAndTheLastDeathEndsTheQuestLine()
    {
        using Rig rig = FeeroRig();
        rig.Ai<FeeroIronhandAI>().OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);
        rig.Session.Clear();

        Reach(rig, s_feeroPath, 30);

        Assert.Equal([("Feero", $"text{FeeroIronhandAI.SayFinalAmbushStart}"), ("Balizar", $"text{FeeroIronhandAI.SayBalizarFinalAmbush}")], rig.Chat());
        uint[] elites = [FeeroIronhandAI.NpcBalizar, FeeroIronhandAI.NpcAligar, FeeroIronhandAI.NpcCaedakar];
        for (int i = 0; i < 3; i++)
        {
            Creature elite = Assert.Single(Summons(rig, elites[i]));
            Assert.Equal(FeeroIronhandAI.ElitePositions[i].X, elite.X, 2);
            Assert.Equal(FeeroIronhandAI.ElitePositions[i].Y, elite.Y, 2);
            Assert.Same(rig.Npc, elite.Combat.Victim);
        }

        rig.Session.Clear();
        foreach (uint elite in elites.Take(2))
        {
            rig.Map.Combat.Kill(null, Assert.Single(Summons(rig, elite)));
        }

        Assert.Empty(rig.Heard());
        rig.Map.Combat.Kill(null, Assert.Single(Summons(rig, elites[2])));
        Assert.Equal([("Feero", $"text{FeeroIronhandAI.SayQuestEnd}")], rig.Chat());
    }

    [Fact]
    public void Feero_HeYellsOnlyTheFirstTimeBalizarAttacksHim()
    {
        using Rig rig = FeeroRig();
        var ai = rig.Ai<FeeroIronhandAI>();
        ai.OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);
        Reach(rig, s_feeroPath, 30);
        Creature balizar = Assert.Single(Summons(rig, FeeroIronhandAI.NpcBalizar));
        Creature aligar = Assert.Single(Summons(rig, FeeroIronhandAI.NpcAligar));
        rig.Session.Clear();

        ai.OnAttackedBy(aligar);
        ai.OnAttackedBy(rig.Player);
        Assert.False(ai.IsAttacked);
        Assert.Empty(rig.Heard());

        ai.OnAttackedBy(balizar);
        ai.OnAttackedBy(balizar);

        Assert.True(ai.IsAttacked);
        Assert.Equal([("Feero", $"text{FeeroIronhandAI.SayFinalAmbushAttack}")], rig.Chat());
    }

    [Fact]
    public void Feero_HisAttackedByHasNoBaseReaction_HeDoesNotAttackBack()
    {
        using Rig rig = FeeroRig();
        var ai = rig.Ai<FeeroIronhandAI>();
        ai.OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);
        Reach(rig, s_feeroPath, 15);
        Creature assassin = Summons(rig, FeeroIronhandAI.NpcDarkStrandAssassin)[0];
        Assert.Null(rig.Npc.Combat.Victim);

        ai.OnAttackedBy(assassin); // the source override is just the Balizar yell: no AttackStart

        Assert.Null(rig.Npc.Combat.Victim);
    }

    [Fact]
    public void Feero_ThePointOneToThirtyOneCreditsTheQuestToTheGroup_OnlyWithAPlayer()
    {
        using Rig rig = FeeroRig();
        var ai = rig.Ai<FeeroIronhandAI>();
        ai.OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);
        Reach(rig, s_feeroPath, 30);

        Assert.Empty(rig.Quest.Completed);
        Reach(rig, s_feeroPath, 31);

        Assert.Equal([(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine)], rig.Quest.Completed);
    }

    [Fact]
    public void Feero_WithoutAnEscortPlayerThePointThirtyOneCreditsNobody()
    {
        using Rig rig = FeeroRig();
        var ai = rig.Ai<FeeroIronhandAI>();
        Assert.True(ai.Start(run: true)); // no player

        Reach(rig, s_feeroPath, 31);

        Assert.Empty(rig.Quest.Completed);
    }

    [Fact]
    public void Feero_TheWaveCounterResetsOnEvadeOnlyWhileHeIsNotEscorting()
    {
        using Rig rig = FeeroRig();
        var ai = rig.Ai<FeeroIronhandAI>();
        ai.OnQuestAccept(rig.Player, FeeroIronhandAI.QuestSuppliesToAuberdine);
        Reach(rig, s_feeroPath, 15);
        Creature balizarSource = Summons(rig, FeeroIronhandAI.NpcDarkStrandAssassin)[0];
        ai.OnAttackedBy(balizarSource);
        Assert.Equal(4, ai.CreaturesCount);

        ai.OnEvade(); // escorting: the counter stays
        Assert.Equal(4, ai.CreaturesCount);

        ai.Stop();
        ai.OnEvade();
        Assert.Equal(0, ai.CreaturesCount);
        Assert.False(ai.IsAttacked);
    }

    [Fact]
    public void Feero_TheCounterWrapsLikeTheSourcesUnsignedByte()
    {
        using Rig rig = FeeroRig();
        var ai = rig.Ai<FeeroIronhandAI>();
        Assert.Equal(0, ai.CreaturesCount);

        // A summon that dies with the counter at zero wraps it to 255, so no "last one dead" line is spoken (uint8 --m_uiCreaturesCount).
        Creature stray = rig.System.SpawnTemporary(Named(FeeroIronhandAI.NpcDarkStrandAssassin, "Assassin"), rig.Npc.X + 5, rig.Npc.Y, rig.Npc.Z, 0f);
        rig.Session.Clear();
        ai.OnSummonedCreatureJustDied(stray);

        Assert.Equal(255, ai.CreaturesCount);
        Assert.Empty(rig.Heard());
    }

    // --- Overlord Mok'Morokk ---------------------------------------------------------------------------------

    // The eight script_waypoint rows of Overlord Mok'Morokk (4500) in ClassicDB z2815: point, x, y, z, orientation, wait (ms).
    private static readonly CreatureWaypoint[] s_morokkPath =
    [
        new(1, -3125.6f, -2885.67f, 34.731f, 0f, 2500), new(2, -3120.26f, -2877.83f, 34.917f, 0f, 0), new(3, -3116.49f, -2850.67f, 34.869f, 0f, 0),
        new(4, -3093.47f, -2819.19f, 34.432f, 0f, 0), new(5, -3104.73f, -2802.02f, 33.954f, 0f, 0), new(6, -3105.91f, -2780.23f, 34.469f, 0f, 0),
        new(7, -3116.08f, -2757.9f, 34.734f, 0f, 0), new(8, -3125.23f, -2733.96f, 33.189f, 0f, 0),
    ];

    private static Rig MorokkRig()
    {
        CreatureWaypoint start = s_morokkPath[0];
        return new Rig(MorokkAI.Entry, b =>
            {
                b.Name = "Morokk";
                b.Faction = 29;
                b.NpcFlags = (uint)NpcFlags.QuestGiver;
                b.UnitFlags = (uint)(UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc);
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
            }, [MorokkAI.SayChallenge, MorokkAI.SayScared], hostile: true, x: start.X, y: start.Y, z: start.Z,
            path: s_morokkPath.Select(p => (MorokkAI.Entry, EscortAI.EscortPathId, p)));
    }

    private static void AcceptAndPause(Rig rig)
    {
        rig.Ai<MorokkAI>().OnQuestAccept(rig.Player, MorokkAI.QuestChallengeMorokk);
        Reach(rig, s_morokkPath, 1);
        Assert.True(rig.Ai<MorokkAI>().HasEscortState(EscortAI.EscortState.Paused));
    }

    [Fact]
    public void Morokk_AcceptingTheQuestStartsHimAtARunWithoutChangingHisFaction_AnotherQuestDoesNothing()
    {
        using Rig rig = MorokkRig();
        var ai = rig.Ai<MorokkAI>();

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));

        ai.OnQuestAccept(rig.Player, MorokkAI.QuestChallengeMorokk);

        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.True(ai.IsRunning);
        Assert.Equal(29u, rig.Npc.FactionTemplate);
    }

    [Fact]
    public void Morokk_StopsAtThePointOneAndChallengesAPlayerWithinSeventyYards()
    {
        using Rig rig = MorokkRig();
        AcceptAndPause(rig);
        rig.Session.Clear();

        rig.World.RunTick(100);

        Assert.Equal([$"text{MorokkAI.SayChallenge}"], rig.Heard());
        Assert.Equal(MorokkAI.FactionHostile, rig.Npc.FactionTemplate);
        Assert.Equal(UnitFlags.None, rig.Npc.UnitFlags & UnitFlags.ImmuneToPlayer);
        Assert.Equal(CreatureReactState.Aggressive, rig.Npc.ReactState);
        Assert.Same(rig.Player, rig.Npc.Combat.Victim);
        Assert.False(rig.Ai<MorokkAI>().IsSuccess);
        Assert.True(rig.Ai<MorokkAI>().HasEscortState(EscortAI.EscortState.Paused)); // still paused until he is beaten
    }

    [Fact]
    public void Morokk_APlayerBeyondSeventyYardsGetsNoChallenge_HeWalksOn()
    {
        using Rig rig = MorokkRig();
        AcceptAndPause(rig);
        rig.Player.Relocate(rig.Npc.X + 80f, rig.Npc.Y, rig.Npc.Z);
        rig.Session.Clear();

        rig.World.RunTick(100);

        Assert.Empty(rig.Heard());
        Assert.Equal(29u, rig.Npc.FactionTemplate);
        Assert.False(rig.Ai<MorokkAI>().HasEscortState(EscortAI.EscortState.Paused));
    }

    [Fact]
    public void Morokk_ThePlayerRangeIsUnderSeventyPlusBothCombatReaches()
    {
        using Rig rig = MorokkRig();
        AcceptAndPause(rig);
        float reach = 70f + rig.Npc.GetFloat(UpdateFields.UnitFieldCombatreach) + rig.Player.GetFloat(UpdateFields.UnitFieldCombatreach);
        rig.Player.Relocate(rig.Npc.X + reach + 0.5f, rig.Npc.Y, rig.Npc.Z);

        rig.World.RunTick(100);

        Assert.False(rig.Ai<MorokkAI>().HasEscortState(EscortAI.EscortState.Paused));
        Assert.Null(rig.Npc.Combat.Victim);
    }

    [Fact]
    public void Morokk_BeatenTo30PercentHeGivesTheGroupCreditAndRunsAway()
    {
        using Rig rig = MorokkRig();
        AcceptAndPause(rig);
        var ai = rig.Ai<MorokkAI>();
        rig.World.RunTick(100); // the challenge
        Assert.Equal(100u, rig.Npc.MaxHealth);

        rig.Map.Combat.DealDamage(rig.Player, rig.Npc, 69, startsCombat: false); // 31%: not yet
        rig.World.RunTick(100);
        Assert.Empty(rig.Quest.Completed);
        Assert.False(ai.IsSuccess);
        Assert.True(rig.Npc.Combat.IsInCombat);
        rig.Session.Clear();

        rig.Map.Combat.DealDamage(rig.Player, rig.Npc, 1, startsCombat: false); // 30%
        rig.World.RunTick(100);

        Assert.Equal([(rig.Player, MorokkAI.QuestChallengeMorokk)], rig.Quest.Completed);
        Assert.Equal(MorokkAI.FactionRunning, rig.Npc.FactionTemplate);
        Assert.True(ai.IsSuccess);
        Assert.False(rig.Npc.Combat.IsInCombat);
        Assert.Equal(UnitFlags.ImmuneToPlayer, rig.Npc.UnitFlags & UnitFlags.ImmuneToPlayer);
        Assert.Equal(CreatureReactState.Passive, rig.Npc.ReactState);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Paused));
        Assert.Equal([$"text{MorokkAI.SayScared}"], rig.Heard());
    }

    [Fact]
    public void Morokk_NothingHappensAt30PercentUnlessHeIsPausedAtTheChallenge()
    {
        using Rig rig = MorokkRig();
        rig.Ai<MorokkAI>().OnQuestAccept(rig.Player, MorokkAI.QuestChallengeMorokk); // escorting, not yet paused
        rig.System.AttackStart(rig.Npc, rig.Player);
        rig.Map.Combat.DealDamage(rig.Player, rig.Npc, 80, startsCombat: false);

        rig.World.RunTick(100);

        Assert.Empty(rig.Quest.Completed);
        Assert.Equal(29u, rig.Npc.FactionTemplate);
        Assert.False(rig.Ai<MorokkAI>().IsSuccess);
    }

    [Fact]
    public void Morokk_HeWalksOnAfterBeingBeatenAndItsMotionWasCleared()
    {
        using Rig rig = MorokkRig();
        AcceptAndPause(rig);
        rig.World.RunTick(100);
        rig.Map.Combat.DealDamage(rig.Player, rig.Npc, 75, startsCombat: false);
        rig.World.RunTick(100);
        Assert.True(rig.Ai<MorokkAI>().IsSuccess);
        float y = rig.Npc.Y;

        Run(rig.World, 6_000);

        Assert.True(rig.Ai<MorokkAI>().HasEscortState(EscortAI.EscortState.Escorting));
        Assert.True(rig.Npc.Y > y + 2f, "he walks the rest of the path");
    }

    [Fact]
    public void Morokk_ReachingHomeWithoutHavingLostFailsTheQuestAndDespawnsHim()
    {
        using Rig rig = MorokkRig();
        AcceptAndPause(rig);

        rig.Ai<MorokkAI>().OnReachedHome();

        Assert.Equal([(rig.Player, MorokkAI.QuestChallengeMorokk)], rig.Quest.Failed);
        Assert.False(rig.Npc.IsAlive);
    }

    [Fact]
    public void Morokk_ReachingHomeAfterHavingLostOrWithoutAnEscortDoesNothing()
    {
        using Rig rig = MorokkRig();
        rig.Ai<MorokkAI>().OnReachedHome(); // not escorting
        Assert.Empty(rig.Quest.Failed);
        Assert.True(rig.Npc.IsAlive);

        AcceptAndPause(rig);
        rig.World.RunTick(100);
        rig.Map.Combat.DealDamage(rig.Player, rig.Npc, 75, startsCombat: false);
        rig.World.RunTick(100);
        Assert.True(rig.Ai<MorokkAI>().IsSuccess);

        rig.Ai<MorokkAI>().OnReachedHome();

        Assert.Empty(rig.Quest.Failed);
        Assert.True(rig.Npc.IsAlive);
    }

    [Fact]
    public void Morokk_ARespawnGivesHimTheSpawnFaction()
    {
        using Rig rig = MorokkRig();
        rig.Npc.FactionTemplate = 35;

        rig.Ai<MorokkAI>().OnRespawn();

        Assert.Equal(MorokkAI.FactionSpawn, rig.Npc.FactionTemplate);
    }

    [Fact]
    public void Morokk_ADeadPlayerGetsNoChallenge()
    {
        using Rig rig = MorokkRig();
        AcceptAndPause(rig);
        rig.Map.Combat.Kill(null, rig.Player);
        Assert.False(rig.Player.IsAlive);
        rig.Session.Clear();

        rig.World.RunTick(100);

        Assert.Empty(rig.Heard());
        Assert.Equal(29u, rig.Npc.FactionTemplate);
    }
}
