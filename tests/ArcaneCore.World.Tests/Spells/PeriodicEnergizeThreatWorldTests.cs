using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class PeriodicEnergizeThreatWorldTests
{
    private const uint PeriodicRage = 992001;
    private const uint PeriodicMana = 992002;
    private const uint DirectRage = 992003;
    private const uint PeriodicEnergy = 992004;
    private const uint PeriodicHappiness = 992005;
    private const uint NoHelpfulRage = 992006;
    private const uint ThreatModifier = 992007;
    private const uint MatchingSchoolRage = 992008;
    private const uint OtherSchoolRage = 992009;

    [Fact]
    public async Task PeriodicRage_AssistsHalfEffectiveGain_SplitAcrossTwoHostileReferences()
    {
        await using var kit = new Fixture(PeriodicRageSpell(PeriodicRage, PowerType.Rage, 40));
        await kit.OnWorldAsync(() =>
        {
            Player caster = kit.AddOwner();
            Player target = kit.AddOwner();
            Creature first = kit.AddCreature(1);
            Creature second = kit.AddCreature(2);
            first.Combat.Threat.AddThreat(target, 1);
            second.Combat.Threat.AddThreat(target, 1);
            kit.Cast(caster, target, PeriodicRage);

            kit.Spells.System.Update(1000);

            Assert.Equal(40u, SpellSystem.GetPower(target, PowerType.Rage));
            Assert.Equal(10f, first.Combat.Threat.GetThreat(caster));
            Assert.Equal(10f, second.Combat.Threat.GetThreat(caster));
        });
    }

    [Fact]
    public async Task PeriodicMana_IsExcluded_AndDirectEnergizeCreatesNoThreat()
    {
        await using var kit = new Fixture(PeriodicRageSpell(PeriodicMana, PowerType.Mana, 40),
            DirectSpell(DirectRage, PowerType.Rage, 20));
        await kit.OnWorldAsync(() =>
        {
            Player caster = kit.AddOwner();
            Player target = kit.AddOwner();
            Creature enemy = kit.AddCreature(1);
            enemy.Combat.Threat.AddThreat(target, 1);
            kit.Cast(caster, target, PeriodicMana);
            kit.Spells.System.Update(1000);
            Assert.Equal(40u, SpellSystem.GetPower(target, PowerType.Mana));
            Assert.Equal(0f, enemy.Combat.Threat.GetThreat(caster));

            Assert.Equal(SpellCastResult.CastOk,
                kit.Spells.System.CastSpell(caster, DirectRage, SpellCastTargets.ForUnit(target.Guid), triggered: true));
            Assert.Equal(20u, SpellSystem.GetPower(target, PowerType.Rage));
            Assert.Equal(0f, enemy.Combat.Threat.GetThreat(caster));
        });
    }

    [Fact]
    public async Task PeriodicEnergy_AssistsHalfGain_ButHappinessIsExcluded()
    {
        await using var kit = new Fixture(PeriodicRageSpell(PeriodicEnergy, PowerType.Energy, 40),
            PeriodicRageSpell(PeriodicHappiness, PowerType.Happiness, 40));
        await kit.OnWorldAsync(() =>
        {
            Player caster = kit.AddOwner();
            Player target = kit.AddOwner();
            Creature enemy = kit.AddCreature(1);
            enemy.Combat.Threat.AddThreat(target, 1);
            target.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy, 100);
            target.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Happiness, 100);
            kit.Cast(caster, target, PeriodicEnergy);
            kit.Spells.System.Update(1000);
            Assert.Equal(40u, SpellSystem.GetPower(target, PowerType.Energy));
            Assert.Equal(20f, enemy.Combat.Threat.GetThreat(caster));

            kit.Spells.System.RemoveAuras(target, PeriodicEnergy);
            kit.Cast(caster, target, PeriodicHappiness);
            kit.Spells.System.Update(1000);
            Assert.Equal(40u, SpellSystem.GetPower(target, PowerType.Happiness));
            Assert.Equal(20f, enemy.Combat.Threat.GetThreat(caster));
        });
    }

    [Fact]
    public async Task PeriodicRage_UsesEffectiveClampedGain_AndZeroGainAddsNoThreat()
    {
        await using var kit = new Fixture(PeriodicRageSpell(PeriodicRage, PowerType.Rage, 40));
        await kit.OnWorldAsync(() =>
        {
            Player caster = kit.AddOwner();
            Player target = kit.AddOwner();
            Creature enemy = kit.AddCreature(1);
            enemy.Combat.Threat.AddThreat(target, 1);
            target.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage, 10);
            kit.Cast(caster, target, PeriodicRage);
            kit.Spells.System.Update(1000);

            Assert.Equal(10u, SpellSystem.GetPower(target, PowerType.Rage));
            Assert.Equal(5f, enemy.Combat.Threat.GetThreat(caster));
            kit.Spells.System.Update(1000);
            Assert.Equal(5f, enemy.Combat.Threat.GetThreat(caster));
        });
    }

    [Fact]
    public async Task DepartedCaster_PreservesPowerButAddsNoThreat()
    {
        await using var kit = new Fixture(PeriodicRageSpell(PeriodicRage, PowerType.Rage, 40));
        await kit.OnWorldAsync(() =>
        {
            Player caster = kit.AddOwner();
            Player target = kit.AddOwner();
            Creature enemy = kit.AddCreature(1);
            enemy.Combat.Threat.AddThreat(target, 1);
            kit.Cast(caster, target, PeriodicRage);
            kit.World.LogoutPlayer(caster);
            kit.Spells.System.Update(1000);

            Assert.Equal(40u, SpellSystem.GetPower(target, PowerType.Rage));
            Assert.Equal(0f, enemy.Combat.Threat.GetThreat(caster));
        });
    }

    [Fact]
    public async Task PeriodicRage_ClampsPowerWithoutUnderflowThreat_AndNoHelpfulSuppressesAssist()
    {
        await using var kit = new Fixture(PeriodicRageSpell(PeriodicRage, PowerType.Rage, 40),
            PeriodicRageSpell(NoHelpfulRage, PowerType.Rage, 10) with { AttributesEx4 = 0x08 }); // SPELL_ATTR_EX4_NO_HELPFUL_THREAT
        await kit.OnWorldAsync(() =>
        {
            Player caster = kit.AddOwner();
            Player target = kit.AddOwner();
            Creature enemy = kit.AddCreature(1);
            enemy.Combat.Threat.AddThreat(target, 1);
            SpellSystem.SetPower(target, PowerType.Rage, 30);
            target.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage, 10);
            kit.Cast(caster, target, PeriodicRage);
            kit.Spells.System.Update(1000);
            Assert.Equal(10u, SpellSystem.GetPower(target, PowerType.Rage));
            Assert.Equal(0f, enemy.Combat.Threat.GetThreat(caster));
            Assert.DoesNotContain(enemy.Combat.Threat.Entries, entry => ReferenceEquals(entry.Target, caster));

            kit.Spells.System.RemoveAuras(target, PeriodicRage);
            target.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage, 100);
            SpellSystem.SetPower(target, PowerType.Rage, 0);
            kit.Cast(caster, target, NoHelpfulRage);
            kit.Spells.System.Update(1000);
            Assert.Equal(10u, SpellSystem.GetPower(target, PowerType.Rage));
            Assert.Equal(0f, enemy.Combat.Threat.GetThreat(caster));
            Assert.DoesNotContain(enemy.Combat.Threat.Entries, entry => ReferenceEquals(entry.Target, caster));
        });
    }

    [Fact]
    public async Task PeriodicRage_UsesMatchingSchoolThreatModifierOnce_AndSkipsOtherSchool()
    {
        SpellInfo modifier = new()
        {
            Id = ThreatModifier, Name = "Helpful threat modifier", RangeIndex = 4, Range = new SpellRange(0, 0),
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = 99, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster, AuraType = AuraType.ModThreat, MiscValue = 1 << (int)SpellSchool.Holy }, new(), new()],
            Duration = new SpellDuration(-1, 0, -1),
        };
        await using var kit = new Fixture(modifier,
            PeriodicRageSpell(MatchingSchoolRage, PowerType.Rage, 20) with { School = SpellSchool.Holy },
            PeriodicRageSpell(OtherSchoolRage, PowerType.Rage, 20) with { School = SpellSchool.Fire });
        await kit.OnWorldAsync(() =>
        {
            Player caster = kit.AddOwner();
            Player target = kit.AddOwner();
            Creature enemy = kit.AddCreature(1);
            enemy.Combat.Threat.AddThreat(target, 1);
            kit.Cast(caster, caster, ThreatModifier);
            kit.Cast(caster, target, MatchingSchoolRage);
            kit.Spells.System.Update(1000);
            Assert.Equal(20u, SpellSystem.GetPower(target, PowerType.Rage));
            Assert.Equal(20f, enemy.Combat.Threat.GetThreat(caster));
            kit.Spells.System.RemoveAuras(target, MatchingSchoolRage);
            kit.Cast(caster, target, OtherSchoolRage);
            kit.Spells.System.Update(1000);
            Assert.Equal(40u, SpellSystem.GetPower(target, PowerType.Rage));
            Assert.Equal(30f, enemy.Combat.Threat.GetThreat(caster));
        });
    }

    private static SpellInfo PeriodicRageSpell(uint id, PowerType power, int amount) => new()
    {
        Id = id, Name = $"Periodic energize {id}", RangeIndex = 4, Range = new SpellRange(0, 30),
        Duration = new SpellDuration(5000, 0, 5000),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura, BasePoints = amount - 1, BaseDice = 1, DieSides = 1,
            TargetA = SpellImplicitTarget.Unit, AuraType = AuraType.PeriodicEnergize, Amplitude = 1000,
            MiscValue = (int)power,
        }, new(), new()],
    };

    private static SpellInfo DirectSpell(uint id, PowerType power, int amount) => new()
    {
        Id = id, Name = $"Direct energize {id}", RangeIndex = 4, Range = new SpellRange(0, 30),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.Energize, BasePoints = amount - 1, BaseDice = 1, DieSides = 1,
            TargetA = SpellImplicitTarget.Unit, MiscValue = (int)power,
        }, new(), new()],
    };

    private static Player Owner(IPlayerSession session, int id) => new(new CharacterRecord
    {
        Id = id, AccountId = id, Name = $"EnergizeOwner{id}", Race = (byte)Race.Human,
        Class = (byte)Class.Warrior, Gender = (byte)Gender.Male, Level = 1, ZoneId = 12,
    }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly SpellInfo[] _spells;
        private int _nextOwner = 1;

        internal Fixture(params SpellInfo[] spells)
        {
            _spells = spells;
            _services = new ServiceCollection().AddLogging().AddSingleton<TeleportFeature>().BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                new NoopSaveQueue(), NullLogger<WorldRuntime>.Instance);
            _services.GetRequiredService<TeleportFeature>().Attach(World);
            Spells = new SpellFeature(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance);
            Spells.Attach(World);
            Spells.System.Store = new SpellStore(_spells, [], []);
            // The threat formula's aura and talent modifiers, as ThreatFeature binds them to every map of the daemon.
            World.GetMap(0).Combat.ThreatModifiers = new SpellThreatModifiers(Spells.System);
            World.Start();
        }

        internal WorldRuntime World { get; }
        internal SpellFeature Spells { get; }
        internal Task OnWorldAsync(Action action) => World.InvokeAsync(() => { action(); return true; }).WaitAsync(TimeSpan.FromSeconds(5));
        internal Player AddOwner()
        {
            int id = _nextOwner++;
            Player player = Owner(new RecordingSession(id), id);
            foreach (PowerType power in new[] { PowerType.Mana, PowerType.Rage, PowerType.Focus, PowerType.Energy, PowerType.Happiness })
            {
                player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power, 100);
            }
            World.AddPlayer(player);
            World.NotifyLoggedIn(player);
            return player;
        }
        internal Creature AddCreature(uint guid)
        {
            var template = new CreatureTemplate { Entry = 992010, Name = "Energize threat creature", MinLevelHealth = 60, MaxLevelHealth = 60, MinLevel = 1, MaxLevel = 1, Faction = 32 };
            var spawn = new CreatureSpawn { Guid = guid, Entry = template.Entry, MapId = 0, X = 1, Y = 0, Z = 0 };
            Creature creature = new(guid, template, spawn, new CreatureContent([template], [spawn], [], [], []), new Random(1));
            World.GetMap(0).AddObject(creature);
            return creature;
        }
        internal void Cast(Unit caster, Unit target, uint spell)
            => Assert.Equal(SpellCastResult.CastOk, Spells.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true));
        public async ValueTask DisposeAsync()
        {
            World.Stop();
            await Spells.DisposeAsync();
            World.Dispose();
            await _services.DisposeAsync();
        }
    }

    private sealed class RecordingSession : IPlayerSession
    {
        private readonly int _accountId;
        internal RecordingSession(int accountId) => _accountId = accountId;
        public int AccountId => _accountId;
        public AccountSecurity Security => AccountSecurity.Player;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }

    private sealed class NoopSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state) { }
    }
}
