using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// The map services a ScriptDev-style script uses (CreatureMapSystem.ScriptedAi.cs): a TEMPSUMMON_CORPSE_DESPAWN summon that goes with its
/// death, the creatures of an entry around a point, the near teleport and the script texts.
/// </summary>
public sealed class ScriptedAiSupportTests
{
    private const uint SummonerEntry = 47101;
    private const uint SummonEntry = 47102;
    private const float Z = 83.5f;

    private static (WorldRuntime World, Map Map, CreatureMapSystem System, Creature Summoner, FakeSession Session) Setup()
    {
        var ai = new CreatureAiContent(
            [],
            [new CreatureAiText(-47101, "Charge!", 0, 0, 0) { Sound = 5804 }],
            broadcastTexts: new BroadcastTextCatalog([new BroadcastText(8906, "For the Stormpike!", string.Empty, 1, 0, 0, [0, 0, 0], [0, 0, 0])]));
        CreatureContent content = new(
            [Template(SummonerEntry, b => b.Name = "Summoner"), Template(SummonEntry, b => b.Name = "Trooper")],
            [Spawn(1, SummonerEntry, 5, 0, Z)], [], [], [], ai);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        (_, FakeSession session) = AddPlayer(world, 1, 0, 0);
        return (world, map, system, Assert.Single(system.Creatures), session);
    }

    [Fact]
    public void ACorpseDespawnSummon_StaysAlive_AndGoesWithItsDeath()
    {
        (WorldRuntime world, Map map, CreatureMapSystem system, Creature summoner, _) = Setup();
        using (world)
        {
            Creature trooper = system.SummonCorpseDespawn(summoner, SummonEntry, 8, 2, Z, 1f)!;
            Assert.NotNull(trooper);
            Assert.Same(summoner, system.SummonerOf(trooper));
            Run(world, 600_000);
            Assert.True(trooper.IsAlive); // no lifetime: it stays until it dies

            map.Combat.Kill(null, trooper);
            Assert.Contains(trooper, system.Creatures); // the corpse is there for the rest of this update
            Run(world, 100);
            Assert.DoesNotContain(trooper, system.Creatures);
            Assert.Null(map.FindObject(trooper.Guid));
        }
    }

    [Fact]
    public void AnEntryScript_IsTheAiOfAWildSummonOfTheEntry_ButNotOfAControlledPet()
    {
        (WorldRuntime world, _, CreatureMapSystem system, Creature summoner, _) = Setup();
        using (world)
        {
            system.RegisterEntryAi(SummonEntry, c => new NullCreatureAI(c));
            CreatureTemplate template = system.Content.FindTemplate(SummonEntry)!;
            Creature wild = system.SpawnSummoned(template, HighGuid.Unit, c =>
            {
                c.Summon = new SummonLinks(SummonKind.Wild, summoner.Guid, 1, TotemSlots.None, 0);
                return new CreatureHome(6, 0, Z, 0);
            });
            Creature pet = system.SpawnSummoned(template, HighGuid.Pet, c =>
            {
                c.Summon = new SummonLinks(SummonKind.Pet, summoner.Guid, 1, TotemSlots.None, 0);
                return new CreatureHome(7, 0, Z, 0);
            });

            Assert.IsType<NullCreatureAI>(wild.AI);
            Assert.IsNotType<NullCreatureAI>(pet.AI);
        }
    }

    [Fact]
    public void CreaturesOfEntryInRange_AreTheEntrysCreaturesWithinTheRange_NearestFirst()
    {
        (WorldRuntime world, _, CreatureMapSystem system, Creature summoner, _) = Setup();
        using (world)
        {
            Creature far = system.SummonCorpseDespawn(summoner, SummonEntry, 5, 30, Z, 0)!;
            Creature near = system.SummonCorpseDespawn(summoner, SummonEntry, 5, 3, Z, 0)!;
            Creature outside = system.SummonCorpseDespawn(summoner, SummonEntry, 5, 60, Z, 0)!;

            Assert.Equal([near, far], system.CreaturesOfEntryInRange(summoner, SummonEntry, 40f));
            Assert.DoesNotContain(outside, system.CreaturesOfEntryInRange(summoner, SummonEntry, 40f));
            Assert.Empty(system.CreaturesOfEntryInRange(summoner, SummonerEntry + 50, 40f));
        }
    }

    [Fact]
    public void NearTeleport_PutsTheCreatureThere_AndTellsItsObservers()
    {
        (WorldRuntime world, _, CreatureMapSystem system, Creature summoner, FakeSession session) = Setup();
        using (world)
        {
            Run(world, 100);
            session.Clear();
            system.NearTeleport(summoner, 12, 7, Z, 2f);

            Assert.Equal(12f, summoner.X);
            Assert.Equal(7f, summoner.Y);
            Assert.Equal(2f, summoner.Orientation);
            Assert.NotEmpty(Packets(session, WorldOpcode.MsgMoveTeleport));
        }
    }

    [Fact]
    public void SayText_SpeaksABroadcastText_ByItsChatType()
    {
        (WorldRuntime world, _, CreatureMapSystem system, Creature summoner, FakeSession session) = Setup();
        using (world)
        {
            Run(world, 100);
            session.Clear();
            system.SayText(summoner, 8906);
            system.SayText(summoner, 99_999); // missing: nothing

            MonsterChat chat = ParseMonsterChat(Assert.Single(Packets(session, WorldOpcode.SmsgMessagechat)));
            Assert.Equal(ChatType.MonsterYell, chat.Type);
            Assert.Equal("For the Stormpike!", chat.Message);
        }
    }

    [Fact]
    public void SayText_PlaysTheScriptDevLineSound()
    {
        (WorldRuntime world, _, CreatureMapSystem system, Creature summoner, FakeSession session) = Setup();
        using (world)
        {
            Run(world, 100);
            session.Clear();
            system.SayText(summoner, -47101);
            byte[] sound = Assert.Single(Packets(session, WorldOpcode.SmsgPlaySound));
            Assert.Equal(5804u, BitConverter.ToUInt32(sound, 0));
        }
    }
}
