using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Duel;

/// <summary>
/// Two same-team players on map 0 with the spell kit, a duel service on a stepping clock and a game object system that knows the Duel Flag
/// template (classic-db gameobject_template 21680, type 16).
/// </summary>
internal sealed class DuelRig : IDisposable
{
    public const uint FlagEntry = 21680;
    public const uint DebuffA = 930301;
    public const uint DebuffB = 930302;
    public const uint Buff = 930303;
    public const uint ReflectableDebuff = 930304;
    public const uint ReflectAura = 930305;
    public const uint DuelSpell = 7266;

    public Spells.SpellTestKit Kit { get; }

    public WorldRuntime World => Kit.World;

    public Map Map { get; }

    public Player A { get; }

    public Player B { get; }

    public FakeSession SessionA { get; }

    public FakeSession SessionB { get; }

    public DuelService Service { get; }

    public GameObjectMapSystem Objects { get; }

    /// <summary>The service clock: whole Unix seconds.</summary>
    public long Now { get; set; } = 1_800_000_000;

    public DuelRig(DuelOptions? options = null, bool withFlagTemplate = true, params SpellInfo[] extraSpells)
    {
        Kit = new Spells.SpellTestKit([.. extraSpells,
            // classic-db spell_template 7266: Effect1 83, TargetA 25 (TARGET_UNIT), EffectMiscValue 21680. Range and duration are synthetic: SpellRange/SpellDuration
            // rows are client DBC data that is not on this machine.
            Spell(DuelSpell, Effect(SpellEffectName.Duel, 0, SpellImplicitTarget.Unit, misc: 21680)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                Duration = new SpellDuration(5000, 0, 5000),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(GrovelSpell, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModStun)) with
            {
                Duration = new SpellDuration(10000, 0, 10000),
                SpellVisual = 1,
                Attributes = SpellAttributes.AuraIsDebuff,
            },
            Spell(DebuffA, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                Duration = new SpellDuration(60000, 0, 60000),
                Attributes = SpellAttributes.AuraIsDebuff,
                SpellVisual = 1,
            },
            Spell(DebuffB, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                Duration = new SpellDuration(60000, 0, 60000),
                Attributes = SpellAttributes.AuraIsDebuff,
                SpellVisual = 1,
            },
            Spell(Buff, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitFriend, AuraType.PeriodicHeal, amplitude: 1000)) with
            {
                Duration = new SpellDuration(60000, 0, 60000),
                SpellVisual = 1,
            },
            // A reflectable magic damage-over-time spell and a 100% reflect aura (proc-engine lane: reflected holders and the duel).
            Spell(ReflectableDebuff, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                Duration = new SpellDuration(60000, 0, 60000),
                Attributes = SpellAttributes.AuraIsDebuff,
                DamageClass = SpellDamageClass.Magic,
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(ReflectAura, Effect(SpellEffectName.ApplyAura, 100, SpellImplicitTarget.UnitCaster, AuraType.ReflectSpells)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            }]);
        Map = World.GetMap(0);
        (A, SessionA) = Kit.AddPlayer(1, 10, 10);
        (B, SessionB) = Kit.AddPlayer(2, 12, 10);
        A.MaxHealth = B.MaxHealth = 1000;
        A.Health = B.Health = 1000;
        Kit.System.UnixSecondsClock = () => Now;
        Service = new DuelService(options ?? new DuelOptions(), () => Now);
        Service.Install(Kit.System);
        Kit.Spellbook.Teach(A, DuelSpell);
        Kit.Spellbook.Teach(B, DuelSpell);
        DuelService.Register(World, Service);
        var flagTemplate = new GameObjectTemplate
        {
            Entry = FlagEntry,
            Type = (uint)GameObjectType.DuelArbiter,
            DisplayId = 787,
            Name = "Duel Flag",
            Data = new uint[GameObjectTemplate.DataCount],
        };
        Objects = new GameObjectMapSystem(Map, new GameObjectContent(withFlagTemplate ? [flagTemplate] : [], [], [], [], []));
        Map.AddUpdater(Objects);
    }

    public const uint GrovelSpell = DuelService.GrovelSpellId;

    /// <summary>Spawn a flag between the two players and begin the duel A challenges B (the tail of EffectDuel).</summary>
    public GameObject Challenge(uint despawnAfterSeconds = 0)
    {
        GameObject flag = Objects.Summon(FlagEntry, (A.X + B.X) / 2, (A.Y + B.Y) / 2, A.Z, A.Orientation, despawnAfterSeconds)
            ?? throw new InvalidOperationException("flag template missing");
        Service.Begin(A, B, flag);
        return flag;
    }

    /// <summary>B accepts and the clock runs until the flag is on.</summary>
    public void AcceptAndStart()
    {
        Service.Accept(B);
        Now += Service.Options.StartDelaySeconds;
        Tick();
    }

    /// <summary>One map update, which runs the duel tick and drops finished duels.</summary>
    public void Tick() => World.RunTick(50);

    public static List<byte[]> Packets(FakeSession session, WorldOpcode opcode)
        => [.. session.Sent.ToArray().Where(p => p.Opcode == opcode).Select(p => p.Payload)];

    public void ClearPackets()
    {
        SessionA.Clear();
        SessionB.Clear();
    }

    public void Dispose() => Kit.Dispose();
}
