using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Zone script batch 4: Calvin Montague (tirisfal_glades.cpp), Taskmaster Fizzule (the_barrens.cpp), Aquementas (tanaris.cpp) and Deathstalker
/// Faerleia (silverpine_forest.cpp), against a real map with a recording spell caster and quest service.
/// </summary>
public sealed class ZoneScriptBatch4Tests
{
    private sealed class RecordingCaster : ICreatureSpellCaster
    {
        public List<(Creature Caster, uint Spell, Unit? Target, bool Triggered)> Casts { get; } = [];

        public event Action<Unit, Unit, SpellInfo>? SpellHit;

        /// <summary>What the spell system does when a spell lands: the creature system hears of it through <see cref="SpellHit"/>.</summary>
        public void Land(Unit caster, Unit target, uint spellId) => SpellHit?.Invoke(caster, target, new SpellInfo { Id = spellId });

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add((caster, spellId, target, triggered));
            return CreatureCastResult.Ok;
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
        public List<(Player Player, uint QuestId)> Completed { get; } = [];

        public void AreaExploredOrEventHappens(Player player, uint questId) => Completed.Add((player, questId));

        public void FailQuest(Player player, uint questId)
        {
        }

        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source)
        {
        }

        public void GroupEventFailHappens(Player player, uint questId)
        {
        }

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

    /// <summary>One script creature (plus extra templates) next to a player, with its script texts, a recording caster and quest service.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(uint entry, Action<CreatureTemplateBuilder>? configure = null, int[]? textIds = null, bool hostile = false, float x = 0f, float y = 0f, params uint[] extraEntries)
        {
            var aiContent = new CreatureAiContent([], [.. (textIds ?? []).Select(id => new CreatureAiText(id, $"text{id}", 0, 0, 0))]);
            CreatureTemplate[] extras = [.. extraEntries.Select(e => Template(e, b => b.Faction = 35))];
            CreatureContent content = new([Template(entry, configure), .. extras], [Spawn(1, entry, x, y)], [], [], [], aiContent);
            var services = new CreatureAiServices { ScriptQuests = Quest, QuestEvents = Quest, Spells = Caster };
            if (hostile)
            {
                services = new CreatureAiServices { ScriptQuests = Quest, QuestEvents = Quest, Spells = Caster, Hostility = new AlwaysHostile() };
            }

            (World, Map, System) = CreateAiSystem(content, services);
            (Player, Session) = AddPlayer(World, 1, x + 1f, y);
            Npc = Assert.Single(System.Creatures, c => c.Entry == entry);
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

        public List<string> Heard() => [.. Packets(Session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];

        public void RunUntil(Func<bool> done, int limitMs = 60_000)
        {
            for (int elapsed = 0; elapsed < limitMs && !done(); elapsed += 100)
            {
                World.RunTick(100);
            }
        }

        public void Dispose() => World.Dispose();
    }

    private static Action<CreatureTemplateBuilder> Friendly(uint npcFlags = 0) => b =>
    {
        b.Faction = 35;
        b.NpcFlags = npcFlags;
    };

    [Theory]
    [InlineData(CalvinMontagueAI.Entry, typeof(CalvinMontagueAI))]
    [InlineData(TaskmasterFizzuleAI.Entry, typeof(TaskmasterFizzuleAI))]
    [InlineData(AquementasAI.Entry, typeof(AquementasAI))]
    [InlineData(DeathstalkerFaerleiaAI.Entry, typeof(DeathstalkerFaerleiaAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        using var rig = new Rig(entry);
        Assert.IsType(ai, rig.Npc.AI);
    }

    [Fact]
    public void TheEventQuestsAreListedAsScripted()
    {
        IReadOnlyCollection<uint> quests = new CreatureAiFactory().ScriptedEventQuests;
        Assert.Contains(CalvinMontagueAI.QuestCalvin, quests);
        Assert.Contains(DeathstalkerFaerleiaAI.QuestPyrewoodAmbush, quests);
    }

    // --- Calvin Montague -------------------------------------------------------------------------------------

    [Fact]
    public void Calvin_StartsFriendlyAndImmuneToPlayers()
    {
        using var rig = new Rig(CalvinMontagueAI.Entry, Friendly(), [CalvinMontagueAI.SayComplete]);
        Assert.Equal(UnitFlags.ImmuneToPlayer, rig.Npc.UnitFlags & UnitFlags.ImmuneToPlayer);
        Assert.Equal(CreatureReactState.Aggressive, rig.Npc.ReactState);
        Assert.Equal(35u, rig.Npc.FactionTemplate);
    }

    [Fact]
    public void Calvin_AcceptingQuest590_TurnsHimHostileAndHeAttacks_AnotherQuestDoesNothing()
    {
        using var rig = new Rig(CalvinMontagueAI.Entry, Friendly(), [CalvinMontagueAI.SayComplete]);
        var ai = rig.Ai<CalvinMontagueAI>();

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.Equal(35u, rig.Npc.FactionTemplate);
        Assert.Equal(UnitFlags.ImmuneToPlayer, rig.Npc.UnitFlags & UnitFlags.ImmuneToPlayer);

        ai.OnQuestAccept(rig.Player, CalvinMontagueAI.QuestCalvin);
        Assert.Equal(CalvinMontagueAI.FactionHostile, rig.Npc.FactionTemplate);
        Assert.Equal(TemporaryFactionFlags.RestoreCombatStop | TemporaryFactionFlags.RestoreRespawn, rig.Npc.TemporaryFactionFlags);
        Assert.Equal(UnitFlags.None, rig.Npc.UnitFlags & UnitFlags.ImmuneToPlayer);
        Assert.Same(rig.Player, rig.Npc.Combat.Victim);
    }

    [Fact]
    public void Calvin_BeatenUnder15Percent_GivesUpSpeaksCreditsDrinksAndResets()
    {
        using var rig = new Rig(CalvinMontagueAI.Entry, Friendly(), [CalvinMontagueAI.SayComplete]);
        var ai = rig.Ai<CalvinMontagueAI>();
        ai.OnQuestAccept(rig.Player, CalvinMontagueAI.QuestCalvin);

        // A lethal hit from another player: the credit still goes to his victim (the player who took the quest), see the class summary.
        (Player other, _) = AddPlayer(rig.World, 2, 1, 1);
        rig.Map.Combat.DealDamage(other, rig.Npc, rig.Npc.Health);
        Assert.True(rig.Npc.IsAlive);
        rig.World.RunTick(100);

        // The fight is over and his faction is back; he is passive and says nothing yet.
        Assert.Equal(1, ai.Phase);
        Assert.False(rig.Npc.Combat.IsInCombat);
        Assert.Equal(35u, rig.Npc.FactionTemplate);
        Assert.Equal(CreatureReactState.Passive, rig.Npc.ReactState);
        Assert.Empty(rig.Heard());
        Assert.Empty(rig.Quest.Completed);

        // Five seconds after the hit: his line.
        rig.RunUntil(() => ai.Phase == 2);
        Assert.Equal([$"text{CalvinMontagueAI.SayComplete}"], rig.Heard());
        Assert.Empty(rig.Quest.Completed);

        // Seven and a half seconds later: the credit, and he drinks.
        rig.RunUntil(() => ai.Phase == 3);
        Assert.Equal([(rig.Player, CalvinMontagueAI.QuestCalvin)], rig.Quest.Completed);
        (Creature caster, uint spell, Unit? target, bool triggered) = Assert.Single(rig.Caster.Casts);
        Assert.Same(rig.Npc, caster);
        Assert.Equal(CalvinMontagueAI.SpellDrinkSd, spell);
        Assert.Same(rig.Npc, target);
        Assert.True(triggered);

        // Seven and a half seconds after that he evades, which resets him for the next player.
        rig.RunUntil(() => ai.Phase == 0);
        Assert.Equal(0, ai.Phase);
        Assert.Equal(UnitFlags.ImmuneToPlayer, rig.Npc.UnitFlags & UnitFlags.ImmuneToPlayer);
        Assert.Equal(CreatureReactState.Aggressive, rig.Npc.ReactState);
        Assert.Single(rig.Quest.Completed);
    }

    [Fact]
    public void Calvin_AHitLandingExactlyOn15PercentDoesNotTrigger_OneMoreHitDoes()
    {
        using var rig = new Rig(CalvinMontagueAI.Entry, b =>
        {
            Friendly()(b);
            b.MinLevelHealth = 100;
            b.MaxLevelHealth = 100;
        }, [CalvinMontagueAI.SayComplete]);
        var ai = rig.Ai<CalvinMontagueAI>();
        Assert.Equal(100u, rig.Npc.MaxHealth);
        ai.OnQuestAccept(rig.Player, CalvinMontagueAI.QuestCalvin);

        rig.Map.Combat.DealDamage(rig.Player, rig.Npc, 85, startsCombat: false); Assert.Equal(15u, rig.Npc.Health);
        rig.World.RunTick(100);
        Assert.Equal(15u, rig.Npc.Health);
        Assert.Equal(0, ai.Phase);
        Assert.True(rig.Npc.Combat.IsInCombat);

        rig.Map.Combat.DealDamage(rig.Player, rig.Npc, 1); // 14%: strictly under
        rig.World.RunTick(100);
        Assert.Equal(1, ai.Phase);
        Assert.False(rig.Npc.Combat.IsInCombat);
    }

    [Fact]
    public void Calvin_TheStepsFollowTheSourceTimers()
    {
        using var rig = new Rig(CalvinMontagueAI.Entry, Friendly(), [CalvinMontagueAI.SayComplete]);
        var ai = rig.Ai<CalvinMontagueAI>();
        ai.OnQuestAccept(rig.Player, CalvinMontagueAI.QuestCalvin);
        rig.Map.Combat.DealDamage(rig.Player, rig.Npc, rig.Npc.Health);
        rig.World.RunTick(100); // gives up; 4.9 s of the first 5 s step left

        Run(rig.World, 4_700);
        Assert.Equal(1, ai.Phase);
        Run(rig.World, 300);
        Assert.Equal(2, ai.Phase);

        Run(rig.World, 7_300);
        Assert.Equal(2, ai.Phase);
        Run(rig.World, 300);
        Assert.Equal(3, ai.Phase);
    }

    // --- Taskmaster Fizzule ----------------------------------------------------------------------------------

    private static void Cast(Rig rig, uint spellId, Unit caster) => rig.Ai<TaskmasterFizzuleAI>().OnSpellHit(caster, new SpellInfo { Id = spellId });

    private static Rig FizzuleRig() => new(TaskmasterFizzuleAI.Entry, b => b.Faction = 32);

    [Fact]
    public void Fizzule_FirstFlareMakesHimImmuneToPlayers_SecondMakesHimFriendly()
    {
        using Rig rig = FizzuleRig();
        var ai = rig.Ai<TaskmasterFizzuleAI>();
        Assert.Equal(32u, rig.Npc.FactionTemplate);

        Cast(rig, TaskmasterFizzuleAI.SpellFlare, rig.Player);
        Assert.Equal(1, ai.FlareCount);
        Assert.True(ai.IsCalmed);
        Assert.Equal(UnitFlags.ImmuneToPlayer, rig.Npc.UnitFlags & UnitFlags.ImmuneToPlayer);
        Assert.Equal(32u, rig.Npc.FactionTemplate);

        Cast(rig, TaskmasterFizzuleAI.SpellFlare, rig.Player);
        Assert.Equal(2, ai.FlareCount);
        Assert.Equal(TaskmasterFizzuleAI.FactionFriendly, rig.Npc.FactionTemplate);
        Assert.Equal(TemporaryFactionFlags.RestoreReachHome, rig.Npc.TemporaryFactionFlags);
        Assert.Equal(0u, rig.Npc.NpcFlags & (uint)NpcFlags.QuestGiver);

        Cast(rig, TaskmasterFizzuleAI.SpellFlare, rig.Player); // a third flare does nothing
        Assert.Equal(2, ai.FlareCount);
    }

    [Fact]
    public void Fizzule_OnlyAPlayersFlareCounts()
    {
        using Rig rig = FizzuleRig();
        var ai = rig.Ai<TaskmasterFizzuleAI>();

        Cast(rig, TaskmasterFizzuleAI.SpellFlare, rig.Npc);
        Cast(rig, 12345, rig.Player);

        Assert.Equal(0, ai.FlareCount);
        Assert.False(ai.IsCalmed);
        Assert.Equal(UnitFlags.None, rig.Npc.UnitFlags & UnitFlags.ImmuneToPlayer);
    }

    [Fact]
    public void Fizzule_ASaluteAfterTheSecondFlareMakesHimSaluteBackAndOfferQuests()
    {
        using Rig rig = FizzuleRig();
        var ai = rig.Ai<TaskmasterFizzuleAI>();

        rig.Npc.ReceiveEmote(rig.Player, TaskmasterFizzuleAI.TextEmoteSalute); // too early: count is 0
        Assert.Equal(0, ai.FlareCount);
        Cast(rig, TaskmasterFizzuleAI.SpellFlare, rig.Player);
        rig.Npc.ReceiveEmote(rig.Player, TaskmasterFizzuleAI.TextEmoteSalute); // still too early: count is 1
        Assert.Equal(1, ai.FlareCount);
        Cast(rig, TaskmasterFizzuleAI.SpellFlare, rig.Player);
        rig.Npc.ReceiveEmote(rig.Player, 1); // some other emote
        Assert.Equal(2, ai.FlareCount);
        Assert.Equal(0u, rig.Npc.NpcFlags & (uint)NpcFlags.QuestGiver);
        rig.Session.Clear();

        rig.Npc.ReceiveEmote(rig.Player, TaskmasterFizzuleAI.TextEmoteSalute);

        Assert.Equal(3, ai.FlareCount);
        Assert.Equal((uint)NpcFlags.QuestGiver, rig.Npc.NpcFlags & (uint)NpcFlags.QuestGiver);
        byte[] emote = Assert.Single(Packets(rig.Session, WorldOpcode.SmsgEmote));
        Assert.Equal(TaskmasterFizzuleAI.EmoteOneshotSalute, BinaryPrimitives.ReadUInt32LittleEndian(emote));

        rig.Npc.ReceiveEmote(rig.Player, TaskmasterFizzuleAI.TextEmoteSalute); // a second salute is ignored
        Assert.Equal(3, ai.FlareCount);
    }

    [Fact]
    public void Fizzule_GoblinFollyDoesItAllAtOnce()
    {
        using Rig rig = FizzuleRig();
        var ai = rig.Ai<TaskmasterFizzuleAI>();

        Cast(rig, TaskmasterFizzuleAI.SpellFolly, rig.Player);

        Assert.Equal(4, ai.FlareCount);
        Assert.True(ai.IsCalmed);
        Assert.Equal(UnitFlags.ImmuneToPlayer, rig.Npc.UnitFlags & UnitFlags.ImmuneToPlayer);
        Assert.Equal(TaskmasterFizzuleAI.FactionFriendly, rig.Npc.FactionTemplate);
        Assert.Equal((uint)NpcFlags.QuestGiver, rig.Npc.NpcFlags & (uint)NpcFlags.QuestGiver);
    }

    [Fact]
    public void Fizzule_AfterTwoMinutesHeIsHostileAgainAndTheFlaresCountFromZero()
    {
        using Rig rig = FizzuleRig();
        var ai = rig.Ai<TaskmasterFizzuleAI>();
        Cast(rig, TaskmasterFizzuleAI.SpellFolly, rig.Player);

        Run(rig.World, TaskmasterFizzuleAI.CalmMs - 1_000);
        Assert.True(ai.IsCalmed);
        Assert.Equal((uint)NpcFlags.QuestGiver, rig.Npc.NpcFlags & (uint)NpcFlags.QuestGiver);

        Run(rig.World, 2_000);
        Assert.False(ai.IsCalmed);
        Assert.Equal(0, ai.FlareCount);
        Assert.Equal(0u, rig.Npc.NpcFlags & (uint)NpcFlags.QuestGiver);
        Assert.Equal(UnitFlags.None, rig.Npc.UnitFlags & UnitFlags.ImmuneToPlayer);
        Assert.Equal(32u, rig.Npc.FactionTemplate); // the evade home gave the faction back (he never left home)
    }

    [Fact]
    public void Fizzule_AFlareLandedByTheSpellSystemReachesHisScript()
    {
        using Rig rig = FizzuleRig();
        var ai = rig.Ai<TaskmasterFizzuleAI>();

        rig.Caster.Land(rig.Player, rig.Npc, TaskmasterFizzuleAI.SpellFlare);

        Assert.Equal(1, ai.FlareCount);
        Assert.True(ai.IsCalmed);

        rig.Caster.Land(rig.Npc, rig.Npc, TaskmasterFizzuleAI.SpellFlare); // not a player
        Assert.Equal(1, ai.FlareCount);
    }

    [Fact]
    public void Fizzule_WhileCalmedAnEvadeOnlyStopsTheFightWhereHeStands()
    {
        using Rig rig = FizzuleRig();
        var ai = rig.Ai<TaskmasterFizzuleAI>();
        Assert.False(ai.OnEnterEvadeMode()); // not calmed: the normal evade
        Cast(rig, TaskmasterFizzuleAI.SpellFlare, rig.Player);
        rig.System.AttackStart(rig.Npc, rig.Player);
        Assert.True(rig.Npc.Combat.IsInCombat);

        Assert.True(ai.OnEnterEvadeMode());

        Assert.False(rig.Npc.Combat.IsInCombat);
    }

    // --- Aquementas ------------------------------------------------------------------------------------------

    private static Rig AquementasRig(bool hostile = true)
        => new(AquementasAI.Entry, b => b.Faction = 91, [AquementasAI.SayAggro], hostile);

    private static void GiveItems(Player player, params (uint Entry, uint Count)[] items)
    {
        var templates = new[] { AquementasAI.ItemSilverTotem, AquementasAI.ItemBookOfAquor, AquementasAI.ItemSilveryClaws, AquementasAI.ItemIrontreeHeart }
            .Select(e => new ItemTemplate { Entry = e, Class = 12, Name = $"item{e}", DisplayId = 1, Stackable = 20, Quality = 1 }).ToArray();
        player.Inventory.Templates = new ItemTemplateStore(templates, []);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        foreach ((uint entry, uint count) in items.Where(i => i.Count > 0))
        {
            Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(entry, count, out _));
        }
    }

    [Fact]
    public void Aquementas_IsFriendlyAtFirstAndAnElementalTenSecondsLater()
    {
        using Rig rig = AquementasRig(hostile: false);
        Assert.Equal(AquementasAI.FactionFriendly, rig.Npc.FactionTemplate);

        Run(rig.World, 9_000);
        Assert.Equal(AquementasAI.FactionFriendly, rig.Npc.FactionTemplate);
        Run(rig.World, 2_000);
        Assert.Equal(AquementasAI.FactionElemental, rig.Npc.FactionTemplate);
    }

    [Fact]
    public void Aquementas_AggroYellsAndGivesTheTotemToAPlayerWithTheThreeQuestPieces()
    {
        using Rig rig = AquementasRig();
        GiveItems(rig.Player, (AquementasAI.ItemBookOfAquor, 1), (AquementasAI.ItemSilveryClaws, 11), (AquementasAI.ItemIrontreeHeart, 1));

        rig.System.AttackStart(rig.Npc, rig.Player);
        rig.World.RunTick(100);

        Assert.Contains($"text{AquementasAI.SayAggro}", rig.Heard());
        Assert.Equal(1u, rig.Player.Inventory.GetItemCount(AquementasAI.ItemSilverTotem));
    }

    [Theory]
    [InlineData(10u, 1u, 1u, 0u)] // ten claws are one short
    [InlineData(11u, 0u, 1u, 0u)] // no book
    [InlineData(11u, 1u, 0u, 0u)] // no heart
    [InlineData(11u, 1u, 1u, 1u)] // already has the totem
    public void Aquementas_NoTotemUnlessTheCountsAreExactlyRight(uint claws, uint book, uint heart, uint totem)
    {
        using Rig rig = AquementasRig();
        GiveItems(rig.Player, (AquementasAI.ItemSilveryClaws, claws), (AquementasAI.ItemBookOfAquor, book), (AquementasAI.ItemIrontreeHeart, heart),
            (AquementasAI.ItemSilverTotem, totem));

        rig.System.AttackStart(rig.Npc, rig.Player);
        rig.World.RunTick(100);

        Assert.Equal(totem, rig.Player.Inventory.GetItemCount(AquementasAI.ItemSilverTotem));
    }

    [Fact]
    public void Aquementas_CastsFrostShockOnTheVictimAndAquaJetOnHimself_OnTheSourceTimers()
    {
        using Rig rig = AquementasRig();
        rig.System.AttackStart(rig.Npc, rig.Player);

        Run(rig.World, 700);
        Assert.Empty(rig.Caster.Casts);
        Run(rig.World, 500); // 1 s: the first Frost Shock
        (Creature _, uint spell, Unit? target, _) = Assert.Single(rig.Caster.Casts);
        Assert.Equal(AquementasAI.SpellFrostShock, spell);
        Assert.Same(rig.Player, target);

        Run(rig.World, 4_000); // 5 s: the first Aqua Jet, on himself
        Assert.Equal([AquementasAI.SpellFrostShock, AquementasAI.SpellAquaJet], rig.Caster.Casts.Select(c => c.Spell));
        Assert.Same(rig.Npc, rig.Caster.Casts[1].Target);

        Run(rig.World, 11_000); // both repeat every 15 s: Frost Shock at 16.1 s, Aqua Jet at 20.2 s
        Assert.Equal(AquementasAI.SpellFrostShock, Assert.Single(rig.Caster.Casts.Skip(2)).Spell);
        Run(rig.World, 5_000);
        Assert.Equal([AquementasAI.SpellFrostShock, AquementasAI.SpellAquaJet, AquementasAI.SpellFrostShock, AquementasAI.SpellAquaJet],
            rig.Caster.Casts.Select(c => c.Spell));
    }

    // --- Deathstalker Faerleia -------------------------------------------------------------------------------

    private static readonly uint[] s_council =
    [
        DeathstalkerFaerleiaAI.NpcSmithers, DeathstalkerFaerleiaAI.NpcThatcher, DeathstalkerFaerleiaAI.NpcHendricks, DeathstalkerFaerleiaAI.NpcWilhelm,
        DeathstalkerFaerleiaAI.NpcHartin, DeathstalkerFaerleiaAI.NpcHigarth, DeathstalkerFaerleiaAI.NpcCooper, DeathstalkerFaerleiaAI.NpcBrunswick,
        DeathstalkerFaerleiaAI.NpcMorrison,
    ];

    private static Rig FaerleiaRig()
        => new(DeathstalkerFaerleiaAI.Entry, Friendly((uint)NpcFlags.QuestGiver), [DeathstalkerFaerleiaAI.SayStart, DeathstalkerFaerleiaAI.SayCompleted],
            x: -400f, y: 1505f, extraEntries: s_council);

    private static List<Creature> Council(Rig rig) => [.. rig.System.Creatures.Where(c => s_council.Contains(c.Entry))];

    private static void KillCouncil(Rig rig)
    {
        foreach (Creature c in Council(rig).Where(c => c.IsAlive))
        {
            rig.Map.Combat.Kill(null, c);
        }
    }

    [Fact]
    public void Faerleia_AcceptingTheQuestSaysHerLineAndStartsTheEvent_AnotherQuestDoesNot()
    {
        using Rig rig = FaerleiaRig();
        var ai = rig.Ai<DeathstalkerFaerleiaAI>();

        ai.OnQuestAccept(rig.Player, 999_999);
        Assert.False(ai.EventStarted);

        ai.OnQuestAccept(rig.Player, DeathstalkerFaerleiaAI.QuestPyrewoodAmbush);

        Assert.True(ai.EventStarted);
        Assert.Equal(0u, rig.Npc.NpcFlags & (uint)NpcFlags.QuestGiver);
        Assert.Equal([$"text{DeathstalkerFaerleiaAI.SayStart}"], rig.Heard());
    }

    [Fact]
    public void Faerleia_TheCouncilComesInFourWaves_EachOnlyWhenTheLastIsDead_AndTheLastCreditsTheQuest()
    {
        using Rig rig = FaerleiaRig();
        var ai = rig.Ai<DeathstalkerFaerleiaAI>();
        ai.OnQuestAccept(rig.Player, DeathstalkerFaerleiaAI.QuestPyrewoodAmbush);

        Run(rig.World, 9_000);
        Assert.Empty(Council(rig));
        rig.RunUntil(() => Council(rig).Count > 0, 3_000);
        Assert.Equal([DeathstalkerFaerleiaAI.NpcSmithers], Council(rig).Select(c => c.Entry));
        Assert.Equal(1, ai.AliveCouncilmen);

        // The wave timer does not run while the wave lives.
        Run(rig.World, 30_000);
        Assert.Single(Council(rig));

        KillCouncil(rig);
        Assert.Equal(0, ai.AliveCouncilmen);
        Assert.Single(rig.Caster.Casts, c => c.Spell == DeathstalkerFaerleiaAI.SpellDrinkPotion && c.Caster == rig.Npc);

        rig.RunUntil(() => Council(rig).Count(c => c.IsAlive) > 0, 15_000);
        Assert.Equal([DeathstalkerFaerleiaAI.NpcThatcher, DeathstalkerFaerleiaAI.NpcHendricks], Council(rig).Where(c => c.IsAlive).Select(c => c.Entry).Order());
        KillCouncil(rig);

        rig.RunUntil(() => Council(rig).Count(c => c.IsAlive) > 0, 15_000);
        Assert.Equal(3, Council(rig).Count(c => c.IsAlive));
        Assert.Empty(rig.Quest.Completed);
        KillCouncil(rig);

        rig.RunUntil(() => Council(rig).Count(c => c.IsAlive) > 0, 15_000);
        Assert.Equal([DeathstalkerFaerleiaAI.NpcCooper, DeathstalkerFaerleiaAI.NpcBrunswick, DeathstalkerFaerleiaAI.NpcMorrison],
            Council(rig).Where(c => c.IsAlive).Select(c => c.Entry).Order());
        Assert.Empty(rig.Quest.Completed);

        KillCouncil(rig);

        Assert.Equal([(rig.Player, DeathstalkerFaerleiaAI.QuestPyrewoodAmbush)], rig.Quest.Completed);
        Assert.Contains($"text{DeathstalkerFaerleiaAI.SayCompleted}", rig.Heard());
        Assert.False(ai.EventStarted);
        Assert.Equal((uint)NpcFlags.QuestGiver, rig.Npc.NpcFlags & (uint)NpcFlags.QuestGiver);
    }

    [Fact]
    public void Faerleia_ASummonedCouncilmanWalksTowardTheMeetingPlace()
    {
        using Rig rig = FaerleiaRig();
        rig.Ai<DeathstalkerFaerleiaAI>().OnQuestAccept(rig.Player, DeathstalkerFaerleiaAI.QuestPyrewoodAmbush);

        rig.RunUntil(() => Council(rig).Count > 0, 12_000);
        Creature smithers = Assert.Single(Council(rig));
        float startX = smithers.X, startY = smithers.Y;
        const float MeetingX = -410.69f, MeetingY = 1498.04f;
        float before = MathF.Sqrt(((startX - MeetingX) * (startX - MeetingX)) + ((startY - MeetingY) * (startY - MeetingY)));

        Run(rig.World, 3_000);

        float after = Distance2D(smithers, MeetingX, MeetingY);
        Assert.True(after < before - 1f, $"moved from {before} to {after} yards of the meeting place");
    }

    [Fact]
    public void Faerleia_IfSheDiesTheQuestFailedPacketGoesToThePlayerAndTheEventEnds()
    {
        using Rig rig = FaerleiaRig();
        rig.Ai<DeathstalkerFaerleiaAI>().OnQuestAccept(rig.Player, DeathstalkerFaerleiaAI.QuestPyrewoodAmbush);
        rig.Session.Clear();

        rig.Map.Combat.Kill(null, rig.Npc);

        byte[] packet = Assert.Single(Packets(rig.Session, WorldOpcode.SmsgQuestgiverQuestFailed));
        Assert.Equal(DeathstalkerFaerleiaAI.QuestPyrewoodAmbush, BinaryPrimitives.ReadUInt32LittleEndian(packet));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
        Assert.Empty(rig.Quest.Completed);
        Assert.False(rig.Ai<DeathstalkerFaerleiaAI>().EventStarted);
    }

    [Fact]
    public void Faerleia_DyingWithoutAnEventSendsNothing()
    {
        using Rig rig = FaerleiaRig();
        rig.Session.Clear();

        rig.Map.Combat.Kill(null, rig.Npc);

        Assert.Empty(Packets(rig.Session, WorldOpcode.SmsgQuestgiverQuestFailed));
    }
}
