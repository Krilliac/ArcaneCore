using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>Actual daemon spell resolver/sinks and public player/map lifecycle, without socket or reflection seams.</summary>
public sealed class AuraCasterLifecycleTests
{
    private const uint Period = 100000;
    private const uint DamageAura = 991001;
    private const uint HealAura = 991002;
    private const uint TriggerAura = 991003;
    private const uint TriggeredDamage = 991004;

    [Theory]
    [InlineData(DamageAura)]
    [InlineData(TriggerAura)]
    public async Task DepartedCasterAura_ActualCreatureDeathDoesNotCreditSameGuidReplacement(uint auraId)
    {
        await using var kit = new Kit();
        await kit.OnWorldAsync(() =>
        {
            Player old = kit.AddOwner();
            Creature target = kit.AddCreature(1);
            target.Health = 3;
            kit.Cast(old, target, auraId);
            SpellAuraHolder holder = Assert.Single(kit.Spells.System.GetAuras(target));

            kit.World.LogoutPlayer(old);
            Player replacement = kit.AddOwner();
            Assert.Equal(old.Guid, replacement.Guid);
            Assert.NotSame(old, replacement);
            Assert.Null(old.Map);
            Assert.Same(replacement, kit.World.FindOnlinePlayer(old.Guid));
            Assert.Same(replacement, kit.Spells.System.Units.Find(target, holder.CasterGuid));
            Assert.Same(holder, Assert.Single(kit.Spells.System.GetAuras(target)));

            kit.Spells.System.Update(Period);

            Assert.Equal(0u, target.Health);
            // This bare creature has no CreatureMapSystem; assert the combat death path,
            // without claiming the separate creature corpse/respawn lifecycle ran.
            Assert.Equal(DeathState.Corpse, target.Combat.DeathState);
            (Unit? killer, Unit victim) = Assert.Single(kit.Deaths);
            Assert.Same(target, victim);
            Assert.Same(target, killer); // Existing missing-owner target fallback remains in force.
            Assert.Empty(kit.Objectives.Kills);
            Assert.Equal(0f, target.Combat.Threat.GetThreat(replacement));
            Assert.Equal(0, ((RecordingSession)replacement.Session).LogoutCount);
            Assert.Equal(old.Guid, holder.CasterGuid); // Wire metadata is not rebound either.
        });
    }

    [Fact]
    public async Task DepartedHealCasterAura_RealHealingThreatNeverMovesToSameGuidReplacement()
    {
        await using var kit = new Kit();
        await kit.OnWorldAsync(() =>
        {
            Player old = kit.AddOwner();
            Creature target = kit.AddCreature(1);
            Creature enemy = kit.AddCreature(2);
            target.Health = target.MaxHealth - 20;
            enemy.Combat.Threat.AddThreat(target, 5);
            kit.Cast(old, target, HealAura);
            kit.World.LogoutPlayer(old);
            Player replacement = kit.AddOwner();
            Assert.Same(replacement, kit.Spells.System.Units.Find(target, old.Guid));
            uint health = target.Health;

            kit.Spells.System.Update(Period);

            Assert.Equal(health + 7, target.Health);
            Assert.Equal(0f, enemy.Combat.Threat.GetThreat(replacement));
            Assert.Equal(8.5f, enemy.Combat.Threat.GetThreat(target));
            Assert.False(replacement.Combat.IsInCombat);
            Assert.Empty(kit.Objectives.Kills);
            Assert.Empty(kit.Deaths);
        });
    }

    [Fact]
    public async Task SameOwnerMapTransfer_PreservesActualPeriodicKillAndQuestCredit()
    {
        await using var kit = new Kit();
        await kit.OnWorldAsync(() =>
        {
            Player owner = kit.AddOwner();
            Creature target = kit.AddCreature(1);
            target.Health = 3;
            kit.Cast(owner, target, DamageAura);
            SpellAuraHolder holder = Assert.Single(kit.Spells.System.GetAuras(target));
            Map source = owner.Map!;
            Map destination = kit.World.GetMap(1);
            // A map transfer removes map membership without firing world logout hooks.
            // Both exact objects move atomically on the real world thread.
            source.RemovePlayer(owner);
            source.RemoveObject(target);
            owner.MapId = target.MapId = destination.MapId;
            destination.AddPlayer(owner);
            destination.AddObject(target);
            Assert.Same(owner, kit.World.FindOnlinePlayer(owner.Guid));
            Assert.Same(owner, kit.Spells.System.Units.Find(target, holder.CasterGuid));
            Assert.Same(holder, Assert.Single(kit.Spells.System.GetAuras(target)));

            kit.Spells.System.Update(Period);

            Assert.Equal(0u, target.Health);
            Assert.Same(owner, Assert.Single(kit.Deaths).Killer);
            (Player credited, uint entry, ObjectGuid guid) = Assert.Single(kit.Objectives.Kills);
            Assert.Same(owner, credited);
            Assert.Equal(target.Entry, entry);
            Assert.Equal(target.Guid, guid);
            Assert.Equal(0, ((RecordingSession)owner.Session).LogoutCount);
        });
    }

    [Fact]
    public async Task LateOldCasterRemoval_DoesNotForgetReplacementSelfAura()
    {
        await using var kit = new Kit();
        await kit.OnWorldAsync(() =>
        {
            Player old = kit.AddOwner();
            kit.Cast(old, old, HealAura);
            kit.World.LogoutPlayer(old);
            Player replacement = kit.AddOwner();
            replacement.Health -= 20;
            kit.Cast(replacement, replacement, HealAura);
            SpellAuraHolder fresh = Assert.Single(kit.Spells.System.GetAuras(replacement));
            uint health = replacement.Health;

            // A delayed hook holding the departed instance must not remove the state now
            // stored under the same GUID for a different exact player.
            kit.Spells.System.RemoveUnit(old);
            kit.World.RemovePlayer(old);

            Assert.Same(replacement, kit.World.FindOnlinePlayer(old.Guid));
            Assert.Same(fresh, Assert.Single(kit.Spells.System.GetAuras(replacement)));
            kit.Spells.System.Update(Period);
            Assert.Equal(health + 7, replacement.Health);
            Assert.Same(fresh, Assert.Single(kit.Spells.System.GetAuras(replacement)));
        });
    }

    [Fact]
    public async Task LogoutAndSameObjectReentry_FreshCastCannotReactivateOldForeignAuraOwnership()
    {
        await using var kit = new Kit();
        await kit.OnWorldAsync(() =>
        {
            Player owner = kit.AddOwner();
            Creature target = kit.AddCreature(1);
            target.Health = 3;
            kit.Cast(owner, target, DamageAura);
            SpellAuraHolder oldHolder = Assert.Single(kit.Spells.System.GetAuras(target));
            kit.World.LogoutPlayer(owner);
            kit.World.AddPlayer(owner);
            kit.World.NotifyLoggedIn(owner);
            kit.Cast(owner, owner, HealAura);
            Assert.Same(owner, kit.Spells.System.Units.Find(target, oldHolder.CasterGuid));

            kit.Spells.System.Update(Period);

            Assert.Equal(0u, target.Health);
            Assert.Same(target, Assert.Single(kit.Deaths).Killer);
            Assert.Empty(kit.Objectives.Kills);
            Assert.True(kit.Spells.System.HasAura(owner, HealAura));
            Assert.Equal(1, ((RecordingSession)owner.Session).LogoutCount);
        });
    }

    private static Player Owner(IPlayerSession session) => new(new CharacterRecord
    {
        Id = 1, AccountId = 1, Name = "Auraowner", Race = (byte)Race.Human,
        Class = (byte)Class.Warrior, Gender = (byte)Gender.Male, Level = 1, ZoneId = 12,
    }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);

    private static SpellInfo Aura(uint id, AuraType type) => new()
    {
        Id = id, Name = $"Owned aura {id}", RangeIndex = 4, Range = new SpellRange(0, 30),
        Duration = new SpellDuration(200000, 0, 200000),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura, BasePoints = 6, BaseDice = 1, DieSides = 1,
            TargetA = SpellImplicitTarget.Unit, AuraType = type, Amplitude = Period,
            TriggerSpell = type == AuraType.PeriodicTriggerSpell ? TriggeredDamage : 0,
        }, new(), new()],
    };

    private sealed class Kit : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly QuestObjectiveAdapter _objectives;

        internal Kit()
        {
            _services = new ServiceCollection()
                .AddSingleton<ILogger<TeleportFeature>>(NullLogger<TeleportFeature>.Instance)
                .AddSingleton<TeleportFeature>()
                .BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                new NoopSaveQueue(), NullLogger<WorldRuntime>.Instance);
            _services.GetRequiredService<TeleportFeature>().Attach(World);
            Spells = new SpellFeature(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance);
            Spells.Attach(World); // Installs the actual WorldSpellUnitResolver and WorldSpellDamageSink.
            Spells.System.Store = new SpellStore([
                Aura(DamageAura, AuraType.PeriodicDamage), Aura(HealAura, AuraType.PeriodicHeal),
                Aura(TriggerAura, AuraType.PeriodicTriggerSpell),
                new SpellInfo
                {
                    Id = TriggeredDamage, Name = "Owned trigger", RangeIndex = 4, Range = new SpellRange(0, 30),
                    Effects = [new SpellEffectInfo
                    {
                        Effect = SpellEffectName.SchoolDamage, BasePoints = 6, BaseDice = 1, DieSides = 1,
                        TargetA = SpellImplicitTarget.Unit,
                    }, new(), new()],
                },
            ], [], []);
            _objectives = new QuestObjectiveAdapter(Objectives);
            World.MapCreated += map =>
            {
                _objectives.Attach(map);
                map.Combat.UnitKilled += (killer, victim) => Deaths.Add((killer, victim));
            };
            World.Start();
        }

        internal WorldRuntime World { get; }
        internal SpellFeature Spells { get; }
        internal RecordingObjectives Objectives { get; } = new();
        internal List<(Unit? Killer, Unit Victim)> Deaths { get; } = [];

        internal Task OnWorldAsync(Action action) => World.InvokeAsync(() =>
        {
            action();
            return true;
        }).WaitAsync(TimeSpan.FromSeconds(5));

        internal Player AddOwner()
        {
            Player player = Owner(new RecordingSession());
            World.AddPlayer(player);
            World.NotifyLoggedIn(player);
            return player;
        }

        internal Creature AddCreature(uint counter)
        {
            var template = new CreatureTemplate
            {
                Entry = 991010, Name = "Aura attribution creature", MinLevelHealth = 60, MaxLevelHealth = 60,
                MinLevel = 1, MaxLevel = 1, Faction = 32,
            };
            var spawn = new CreatureSpawn { Guid = counter, Entry = template.Entry, MapId = 0, X = 1, Y = 0, Z = 0 };
            var creature = new Creature(counter, template, spawn, new CreatureContent([template], [spawn], [], [], []), new Random(1));
            World.GetMap(0).AddObject(creature);
            return creature;
        }

        internal void Cast(Unit caster, Unit target, uint spell)
            => Assert.Equal(SpellCastResult.CastOk, Spells.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true));

        public async ValueTask DisposeAsync()
        {
            World.Stop();
            await Spells.DisposeAsync();
            _objectives.Dispose();
            World.Dispose();
            await _services.DisposeAsync();
        }
    }

    private sealed class RecordingObjectives : IQuestObjectiveEvents
    {
        internal List<(Player Player, uint Entry, ObjectGuid Guid)> Kills { get; } = [];
        public void KilledMonsterCredit(Player player, uint entry, ObjectGuid guid) => Kills.Add((player, entry, guid));
        public void CastedCreatureOrGo(Player player, uint entry, ObjectGuid guid, bool isCreature, uint spellId) { }
        public void TalkedToCreature(Player player, uint entry, ObjectGuid guid) { }
        public void ItemAdded(Player player, uint entry, uint count) { }
        public void ItemRemoved(Player player, uint entry, uint count) { }
        public void AreaExploredOrEventHappens(Player player, uint questId) { }
        public void FailQuest(Player player, uint questId) { }
        public void MoneyChanged(Player player) { }
    }

    private sealed class RecordingSession : IPlayerSession
    {
        public int AccountId => 1;
        public AccountSecurity Security => AccountSecurity.Player;
        internal int LogoutCount { get; private set; }
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() => LogoutCount++;
    }

    private sealed class NoopSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state) { }
    }
}
