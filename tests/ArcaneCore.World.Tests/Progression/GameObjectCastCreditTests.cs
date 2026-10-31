using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Progression;

/// <summary>
/// A spell that reached a game object credits CastedCreatureOrGO for that object to the caster's charmer, owner or the caster himself
/// (vmangos Spell::DoAllEffectOnTarget for a GOTargetInfo, Spell.cpp:1845-1851, then Player::RewardPlayerAndGroupAtCast, Player.cpp:20019-20040),
/// carrying the spell id so a quest that needs a cast of that spell (3825: spell 13484 on object 160840) matches.
/// </summary>
public sealed class GameObjectCastCreditTests : IDisposable
{
    private const uint ObjectEntry = 160840;
    private const uint QuestSpell = 13484;

    private readonly WorldRuntime _world = new(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
        new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
    private readonly Recorder _credits = new();
    private readonly QuestObjectiveAdapter _adapter;
    private readonly GameObject _object;

    public GameObjectCastCreditTests()
    {
        _adapter = new QuestObjectiveAdapter(_credits);
        Map map = _world.GetMap(0);
        var values = new uint[GameObjectTemplate.DataCount];
        var template = new GameObjectTemplate { Entry = ObjectEntry, Type = (uint)GameObjectType.Goober, DisplayId = 1, Name = "Quest object", Data = values };
        var system = new GameObjectMapSystem(map, new GameObjectContent([template], [], [], [], []));
        map.AddUpdater(system);
        _object = system.Summon(ObjectEntry, 1, 0, 83.5f, 0)!;
    }

    public void Dispose()
    {
        _adapter.Dispose();
        _world.Dispose();
    }

    private Player AddPlayer(int id)
    {
        var player = new Player(new CharacterRecord
        {
            Id = id, AccountId = id, Name = $"Castcredit{id}", Race = (byte)Race.Human, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 1, ZoneId = 12, Z = 83.5f,
        }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new NullSession(id));
        _world.AddPlayer(player);
        return player;
    }

    [Fact]
    public void APlayersSpellOnAnObject_CreditsThePlayerWithTheObjectEntryAndSpell()
    {
        Player caster = AddPlayer(1);

        _adapter.OnSpellHitGameObject(caster, _object, QuestSpell);

        Assert.Equal([(caster.Guid, ObjectEntry, _object.Guid, false, QuestSpell)], _credits.Casts);
    }

    [Fact]
    public void ACreatureAPlayerOwns_CreditsItsOwner_NotTheCreature()
    {
        Player owner = AddPlayer(1);
        var template = new CreatureTemplate { Entry = 991020, Name = "Owned helper", MinLevelHealth = 50, MaxLevelHealth = 50, MinLevel = 1, MaxLevel = 1, Faction = 35 };
        var spawn = new CreatureSpawn { Guid = 5, Entry = template.Entry, MapId = 0, X = 1, Y = 0, Z = 83.5f };
        var helper = new Creature(5, template, spawn, new CreatureContent([template], [spawn], [], [], []), new Random(1));
        _world.GetMap(0).AddObject(helper);
        SummonService.ApplyOwner(helper, owner, 0);

        _adapter.OnSpellHitGameObject(helper, _object, QuestSpell);

        Assert.Equal([(owner.Guid, ObjectEntry, _object.Guid, false, QuestSpell)], _credits.Casts);
    }

    [Fact]
    public void ACreatureNoPlayerControls_CreditsNobody()
    {
        AddPlayer(1);
        var template = new CreatureTemplate { Entry = 991021, Name = "Wild caster", MinLevelHealth = 50, MaxLevelHealth = 50, MinLevel = 1, MaxLevel = 1, Faction = 14 };
        var spawn = new CreatureSpawn { Guid = 6, Entry = template.Entry, MapId = 0, X = 1, Y = 0, Z = 83.5f };
        var wild = new Creature(6, template, spawn, new CreatureContent([template], [spawn], [], [], []), new Random(1));
        _world.GetMap(0).AddObject(wild);

        _adapter.OnSpellHitGameObject(wild, _object, QuestSpell);

        Assert.Empty(_credits.Casts);
    }

    [Fact]
    public void AReleasedGhostGroupCaster_IsNotCredited()
    {
        Player caster = AddPlayer(1);
        caster.Combat.DeathState = DeathState.Dead;
        caster.Flags |= PlayerFlags.Ghost;
        var grouped = new QuestObjectiveAdapter(_credits, p => new ArcaneCore.Game.Progression.RewardGroup([p.Guid], false));
        try
        {
            grouped.OnSpellHitGameObject(caster, _object, QuestSpell);
        }
        finally
        {
            grouped.Dispose();
        }

        Assert.Empty(_credits.Casts);
    }

    private sealed class NullSession(int id) : IPlayerSession
    {
        public int AccountId => id;
        public ArcaneCore.Kernel.Accounts.AccountSecurity Security => ArcaneCore.Kernel.Accounts.AccountSecurity.Player;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }

    private sealed class Recorder : IQuestObjectiveEvents
    {
        public List<(ObjectGuid Player, uint Entry, ObjectGuid Guid, bool IsCreature, uint SpellId)> Casts { get; } = [];

        public void KilledMonsterCredit(Player player, uint entry, ObjectGuid guid) { }
        public void CastedCreatureOrGo(Player player, uint entry, ObjectGuid guid, bool isCreature, uint spellId)
            => Casts.Add((player.Guid, entry, guid, isCreature, spellId));
        public void TalkedToCreature(Player player, uint entry, ObjectGuid guid) { }
        public void ItemAdded(Player player, uint entry, uint count) { }
        public void ItemRemoved(Player player, uint entry, uint count) { }
        public void AreaExploredOrEventHappens(Player player, uint questId) { }
        public void FailQuest(Player player, uint questId) { }
        public void MoneyChanged(Player player) { }
    }
}
