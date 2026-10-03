using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>
/// SPELL_EFFECT_SUMMON_OBJECT_SLOT1-4 and the ownership of spell-created objects (vmangos
/// Spell::EffectSummonObject, SpellEffects.cpp:5151-5226; Unit::AddGameObject / RemoveGameObject /
/// RemoveAllGameObjects, Unit.cpp:4075-4173). Synthetic spells and templates.
/// </summary>
public sealed class SpellObjectOwnershipTests : IDisposable
{
    private const uint TrapEntry = 940001;
    private const uint SlotOne = 940101;
    private const uint SlotOneOther = 940102;
    private const uint SlotTwo = 940103;
    private const uint EventSpell = 940104;
    private const uint UnknownObject = 940105;

    private readonly SpellTestKit _kit;
    private readonly Map _map;
    private readonly GameObjectMapSystem _objects;
    private readonly Player _hunter;
    private readonly FakeSession _session;

    public SpellObjectOwnershipTests()
    {
        _kit = new SpellTestKit(
            Summon(SlotOne, SpellEffectName.SummonObjectSlot1, TrapEntry),
            Summon(SlotOneOther, SpellEffectName.SummonObjectSlot1, TrapEntry),
            Summon(SlotTwo, SpellEffectName.SummonObjectSlot2, TrapEntry),
            Summon(EventSpell, SpellEffectName.SummonObjectSlot3, TrapEntry) with
            {
                Attributes = (SpellAttributes)0x02000000, // COOLDOWN_ON_EVENT
                RecoveryTime = 15_000,
            },
            Summon(UnknownObject, SpellEffectName.SummonObjectSlot4, 999_999));
        _map = _kit.World.GetMap(0);
        var content = new GameObjectContent([GoTemplate(TrapEntry, GameObjectType.Trap, (2, 5), (4, 1))], [], [], [], []);
        _objects = new GameObjectMapSystem(_map, content);
        _map.AddUpdater(_objects);
        _map.AddUpdater(new SpellObjectSystem(_kit.System));
        RangedHandlers.Register(_kit.System);
        (_hunter, _session) = _kit.AddPlayer(1, 100, 200);
        _hunter.Level = 37;
        _hunter.Relocate(100, 200, 83.5f, 0.0f, 0);
        _kit.Spellbook.Teach(_hunter, SlotOne, SlotOneOther, SlotTwo, EventSpell, UnknownObject);
    }

    public void Dispose() => _kit.Dispose();

    private static SpellInfo Summon(uint id, SpellEffectName effect, uint entry) => Spell(id, Effect(effect, 0, SpellImplicitTarget.None, misc: (int)entry)) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private SpellCastResult Cast(uint spell, SpellCastTargets? targets = null)
        => _kit.System.CastSpell(_hunter, spell, targets ?? SpellCastTargets.ForSelf(), triggered: false);

    private void Tick(uint ms = 50)
    {
        _kit.Advance(ms, step: ms);
        _kit.World.RunTick(ms);
    }

    private IReadOnlyList<GameObject> Traps => [.. _objects.GameObjects.Where(g => g.Entry == TrapEntry)];

    [Fact]
    public void ASlotSpell_SummonsTheObjectInFrontOfTheCaster_WithTheCastersLevel_AndRecordsTheOwner()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(SlotOne));

        GameObject go = Assert.Single(Traps);
        SpellCreatedObject entry = Assert.IsType<SpellCreatedObject>(_kit.System.SpellObjects.Find(go));
        Assert.Same(_hunter, entry.Owner);
        Assert.Equal((SlotOne, 0), (entry.SpellId, entry.Slot));
        Assert.Equal(37u, go.GetUInt32(UpdateFields.GameobjectLevel));
        float distance = _hunter.BoundingRadius + 0.388999998569489f;
        Assert.Equal(100 + distance, go.X, 3); // orientation 0: straight along +x
        Assert.Equal(200f, go.Y, 3);
        Assert.Same(entry, _kit.System.SpellObjects.InSlot(_hunter, 0));
    }

    [Fact]
    public void ADestinationInTheTargets_IsUsedInsteadOfTheClosePoint()
    {
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (110f, 205f, 83.5f) };

        Assert.Equal(SpellCastResult.CastOk, Cast(SlotOne, targets));

        GameObject go = Assert.Single(Traps);
        Assert.Equal((110f, 205f), (go.X, go.Y));
    }

    [Fact]
    public void ANewObjectInTheSameSlotReplacesTheOldOne_AnotherSlotKeepsIt()
    {
        Cast(SlotOne);
        GameObject first = Assert.Single(Traps);

        Cast(SlotTwo);
        Assert.Equal(2, Traps.Count);
        Assert.Contains(first, Traps);

        Assert.Equal(SpellCastResult.CastOk, Cast(SlotOneOther));

        Assert.DoesNotContain(first, Traps);
        Assert.Equal(2, Traps.Count);
        Assert.Null(_kit.System.SpellObjects.Find(first));
        Assert.Equal(SlotOneOther, _kit.System.SpellObjects.InSlot(_hunter, 0)!.SpellId);
        Assert.Equal(SlotTwo, _kit.System.SpellObjects.InSlot(_hunter, 1)!.SpellId);
    }

    [Fact]
    public void TheObjectEndsWithTheSpellDuration_AndLeavesTheOwnersSlot()
    {
        Cast(SlotOne);
        Assert.Single(Traps);

        for (int i = 0; i < 61; i++)
        {
            Tick(1000);
        }

        Assert.Empty(Traps);
        Assert.Equal(0, _kit.System.SpellObjects.Count);
        Assert.Null(_kit.System.SpellObjects.InSlot(_hunter, 0));
    }

    [Fact]
    public void TheSpawnAnimationGoesToTheClientsOnceTheyHaveTheObject()
    {
        Cast(SlotOne);
        GameObject go = Assert.Single(Traps);
        _session.Clear();

        for (int i = 0; i < SpellObjectSystem.SpawnAnimAge + 2; i++)
        {
            Tick();
        }

        Assert.Contains(go.Guid.Value, _session.Sent.Where(p => p.Opcode == WorldOpcode.SmsgGameobjectSpawnAnim)
            .Select(p => BinaryPrimitives.ReadUInt64LittleEndian(p.Payload)));
        Assert.Single(_session.Sent, p => p.Opcode == WorldOpcode.SmsgGameobjectSpawnAnim); // once
    }

    [Fact]
    public void WhenTheOwnerLeavesTheMap_ItsObjectsGo_WithoutACooldown()
    {
        Cast(EventSpell);
        Cast(SlotOne);
        Assert.Equal(2, Traps.Count);

        _kit.World.RemovePlayer(_hunter);

        Assert.Empty(Traps);
        Assert.Equal(0, _kit.System.SpellObjects.Count);
        Assert.DoesNotContain(_kit.System.GetActiveCooldowns(_hunter), c => c.SpellId == EventSpell);
    }

    [Fact]
    public void ACooldownOnEventSpell_WaitsForTheObject_ThenStartsItsCooldown()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(EventSpell));
        Assert.False(_kit.System.IsSpellReady(_hunter, _kit.Store.Get(EventSpell)!));
        Assert.Equal(SpellCastResult.NotReady, Cast(EventSpell)); // cannot lay a second one while the first lives

        _session.Clear();
        for (int i = 0; i < 61; i++)
        {
            Tick(1000); // the object's 60 s run out
        }

        Assert.Empty(Traps);
        Assert.False(_kit.System.IsSpellReady(_hunter, _kit.Store.Get(EventSpell)!)); // the 15 s start now
        Assert.Contains(_session.Sent, p => p.Opcode == WorldOpcode.SmsgCooldownEvent);

        _kit.Advance(13_000, step: 1000); // 1 s after the object went, plus 13 s: 14 s of 15
        Assert.False(_kit.System.IsSpellReady(_hunter, _kit.Store.Get(EventSpell)!));
        _kit.Advance(2_000, step: 1000);
        Assert.True(_kit.System.IsSpellReady(_hunter, _kit.Store.Get(EventSpell)!));
    }

    [Fact]
    public void ReplacingAnEventObject_StartsTheCooldown_ByTheSlotRule()
    {
        // Another cast into slot 3 is the same event spell: it is refused while the object lives, so
        // the slot can only be replaced by a different spell.
        Cast(EventSpell);
        Assert.Single(Traps);
        SpellCreatedObject entry = _kit.System.SpellObjects.InSlot(_hunter, 2)!;

        _kit.System.RemoveSpellObject(entry, _objects, startEventCooldown: true);

        Assert.Empty(Traps);
        Assert.False(_kit.System.IsSpellReady(_hunter, _kit.Store.Get(EventSpell)!)); // 15 s from now
        _kit.Advance(15_000, step: 1000);
        Assert.True(_kit.System.IsSpellReady(_hunter, _kit.Store.Get(EventSpell)!));
    }

    [Fact]
    public void AnUnknownObjectTemplate_SummonsNothing_AndRecordsNothing()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(UnknownObject));

        Assert.Empty(Traps);
        Assert.Equal(0, _kit.System.SpellObjects.Count);
    }
}
