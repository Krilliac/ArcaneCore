using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// The pet fixes of the "pet data, frame and runaway" change: the shipped create spells reach a summoned imp (A), the owner receives the
/// pet's create block before SMSG_PET_SPELLS (B), the pet number field (C) and the loyalty runaway (E). Spell ids are the retail ones:
/// Summon Imp 688, Firebolt rank 1 3110 (classic-db petcreateinfo_spell row 416).
/// </summary>
public sealed class PetFixesTests
{
    private const uint SummonImpSpell = 688;
    private const uint FireboltRank1 = 3110;

    private static IEnumerable<SpellInfo> ImpSpells() =>
    [
        Spell(SummonImpSpell, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)ImpEntry)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        Spell(FireboltRank1, Effect(SpellEffectName.SchoolDamage, 8, SpellImplicitTarget.UnitEnemy)) with { RangeIndex = 4, Range = new SpellRange(0, 30) },
    ];

    private static PetContent ImpContent() => new([], [new PetCreateSpells(ImpEntry, [FireboltRank1])]);

    private static PetTestKit ImpKit()
    {
        var kit = new PetTestKit(ImpSpells(), ImpContent());
        kit.Service.InstallDemons(kit.Spells.System);
        return kit;
    }

    [Fact]
    public void SummonImp_WithTheShippedCreateSpellRow_LearnsFireboltAndListsOneSpellInThePetBar()
    {
        using PetTestKit kit = ImpKit();
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.Level = 10;
        session.Clear();

        kit.Cast(owner, SummonImpSpell);

        Creature imp = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        Assert.Equal([(FireboltRank1, ActionType.Disabled)], imp.Summon!.Charm!.SpellStates.Select(s => (s.Key, s.Value)));
        var r = new PacketReader(Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells)));
        r.Skip(8 + 4 + 4 + 40);
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(ActionButton.Make(FireboltRank1, ActionType.Disabled).Packed, r.ReadUInt32());
    }

    private const uint CallPetSpell = 991002;

    /// <summary>What the owner's session received, in order: a create block of a GUID, a values block of a GUID with its UNIT_FIELD_SUMMON, or the pet bar.</summary>
    private static List<string> OwnerTimeline(FakeSession session, Player owner, Creature pet)
    {
        var events = new List<string>();
        foreach ((WorldOpcode opcode, byte[] payload) in session.Sent)
        {
            if (opcode == WorldOpcode.SmsgPetSpells)
            {
                events.Add("petspells");
                events.Add(BitConverter.ToUInt64(payload, 0) == pet.Guid.Value ? "petspells-guid-ok" : "petspells-guid-WRONG");
            }
            else if (opcode == WorldOpcode.SmsgUpdateObject)
            {
                foreach (ParsedBlock block in CreatureTestKit.ParseUpdateObject(payload))
                {
                    if (block.Type is ObjectUpdateType.CreateObject or ObjectUpdateType.CreateObject2 && block.Guids.Contains(pet.Guid.Value))
                    {
                        events.Add("create-pet");
                        events.Add($"create-pet-number={(block.Values.TryGetValue(UpdateFields.UnitFieldPetnumber, out uint number) ? number : 0)}");
                    }
                    else if (block.Type == ObjectUpdateType.Values && block.Guids.Contains(owner.Guid.Value)
                        && block.Values.TryGetValue(UpdateFields.UnitFieldSummon, out uint low) && low == (uint)(pet.Guid.Value & 0xFFFFFFFFu))
                    {
                        events.Add("owner-summon");
                    }
                }
            }
        }

        return events;
    }

    [Fact]
    public void SummonImp_DeliversThePetCreateBlockAndTheOwnersSummonFieldBeforeThePetBar()
    {
        using PetTestKit kit = ImpKit();
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.Level = 10;
        session.Clear();

        kit.Cast(owner, SummonImpSpell);

        Creature imp = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        List<string> events = OwnerTimeline(session, owner, imp);
        Assert.Contains("create-pet", events);
        Assert.Contains("owner-summon", events);
        int bar = events.IndexOf("petspells");
        Assert.True(bar > events.IndexOf("create-pet"), $"pet bar before the pet create block: {string.Join(", ", events)}");
        Assert.True(bar > events.IndexOf("owner-summon"), $"pet bar before UNIT_FIELD_SUMMON: {string.Join(", ", events)}");
        Assert.Contains("petspells-guid-ok", events);
        Assert.Contains($"create-pet-number={imp.Summon!.Charm!.PetNumber}", events); // the create block already carries the pet number
        Assert.NotEqual(0u, imp.Summon.Charm.PetNumber);
    }

    [Fact]
    public async Task CallPet_DeliversThePetCreateBlockAndTheOwnersSummonFieldBeforeThePetBar()
    {
        using PetTestKit kit = new([Spell(CallPetSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))]);
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 801, PetEntry, 5, 0, 50, 0, 0, 1, [1, 2, 3], []));
        await kit.Service.ReadCurrentPetAsync(owner);
        session.Clear();

        kit.Cast(owner, CallPetSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        List<string> events = OwnerTimeline(session, owner, pet);
        int bar = events.IndexOf("petspells");
        Assert.True(bar >= 0, "no pet bar sent");
        Assert.True(events.IndexOf("create-pet") is >= 0 and var create && create < bar, $"pet bar before the pet create block: {string.Join(", ", events)}");
        Assert.True(events.IndexOf("owner-summon") is >= 0 and var summon && summon < bar, $"pet bar before UNIT_FIELD_SUMMON: {string.Join(", ", events)}");
        Assert.Contains("petspells-guid-ok", events);
        Assert.Contains("create-pet-number=801", events);
    }

    [Fact]
    public void SummonImp_WritesThePetNumberToUnitFieldPetnumber()
    {
        using PetTestKit kit = ImpKit();
        (Player owner, _) = kit.AddPlayer(1);
        owner.Level = 10;

        kit.Cast(owner, SummonImpSpell);

        Creature imp = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        uint number = imp.Summon!.Charm!.PetNumber;
        Assert.NotEqual(0u, number);
        Assert.Equal(number, imp.GetUInt32(UpdateFields.UnitFieldPetnumber));
        Assert.Equal(number, imp.Guid.Entry); // the pet number is also the entry part of the HIGHGUID_PET guid
    }

    [Fact]
    public async Task CallPet_WritesTheStablePetNumberToTheField()
    {
        using PetTestKit kit = new([Spell(CallPetSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))]);
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 802, PetEntry, 5, 0, 50, 0, 0, 1, [1, 2, 3], []));
        await kit.Service.ReadCurrentPetAsync(owner);

        kit.Cast(owner, CallPetSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(802u, pet.GetUInt32(UpdateFields.UnitFieldPetnumber));
    }

    [Fact]
    public void AGuardianKeepsAZeroPetNumberField()
    {
        using PetTestKit kit = ImpKit();
        (Player owner, _) = kit.AddPlayer(1);

        kit.Cast(owner, GuardianSpell);

        Creature guardian = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(0u, guardian.GetUInt32(UpdateFields.UnitFieldPetnumber));
    }

    [Fact]
    public async Task HunterPet_CarriesTheHappinessBarFocusPowerTypeAndWarriorClassOfVmangosPets()
    {
        using PetTestKit kit = new([Spell(CallPetSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))]);
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 803, PetEntry, 5, 0, 50, 0, 600_000, 1, [], []));
        await kit.Service.ReadCurrentPetAsync(owner);

        kit.Cast(owner, CallPetSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal((SummonService.HappinessMax, 600_000u), (pet.GetUInt32(UpdateFields.UnitFieldMaxpower5), pet.GetUInt32(UpdateFields.UnitFieldPower5)));
        Assert.Equal(PowerType.Focus, pet.PowerType);
        Assert.Equal((SummonService.FocusMax, SummonService.FocusMax), (pet.GetUInt32(UpdateFields.UnitFieldMaxpower3), pet.GetUInt32(UpdateFields.UnitFieldPower3)));
        Assert.Equal((byte)Class.Warrior, pet.GetByte(UpdateFields.UnitFieldBytes0, 1));
        Assert.Equal((byte)2, pet.GetByte(UpdateFields.UnitFieldBytes0, 2));
    }

    private const uint FelhunterEntry = 417;
    private const uint SummonFelhunterSpell = 691;

    [Fact]
    public void AWarlockDemonRestoredAfterATemporaryUnsummon_CarriesItsPetNumberField()
    {
        using var kit = new PetTestKit(
            [Spell(SummonFelhunterSpell, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)FelhunterEntry)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 }],
            extraTemplates:
            [
                CreatureTestKit.Template(FelhunterEntry, b =>
                {
                    b.Faction = 14;
                    b.MinLevel = 5;
                    b.MaxLevel = 5;
                    b.MinLevelHealth = 100;
                    b.MaxLevelHealth = 100;
                }) with { CreatureType = global::ArcaneCore.Game.Pets.Control.CharmService.CreatureTypeDemon },
            ]);
        kit.Service.InstallDemons(kit.Spells.System);
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        owner.Level = 10;
        kit.Cast(owner, SummonFelhunterSpell);
        Creature demon = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        uint number = demon.Summon!.Charm!.PetNumber;
        Assert.Equal(number, demon.GetUInt32(UpdateFields.UnitFieldPetnumber));

        Assert.True(kit.Service.UnsummonPetTemporarily(owner));
        Creature back = Assert.IsType<Creature>(kit.Service.ResummonTemporarilyUnsummonedPet(owner));

        Assert.NotSame(demon, back);
        Assert.NotEqual(0u, number);
        Assert.Equal(number, back.GetUInt32(UpdateFields.UnitFieldPetnumber));
    }

    private static PetLevelStats StatsRow(uint entry, uint health)
        => new(entry, 1, health, 0, 0, 0, 0, 1, 1, 1, 1, 1);

    [Theory]
    [InlineData(true, 700u)]   // vmangos Pet.cpp:1288: creatureId = HUNTER_PET ? 1 : entry
    [InlineData(false, 0u)]
    public async Task HunterPetStats_ComeFromCreatureId1_AndFallBackToTheOwnEntry(bool withSharedRow, uint sharedHealth)
    {
        PetLevelStats[] rows = withSharedRow ? [StatsRow(1, sharedHealth), StatsRow(PetEntry, 300)] : [StatsRow(PetEntry, 300)];
        using PetTestKit kit = new([Spell(CallPetSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))], new PetContent(rows, []));
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 804, PetEntry, 1, 0, 50, 0, 0, 1, [], []));
        await kit.Service.ReadCurrentPetAsync(owner);

        kit.Cast(owner, CallPetSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(withSharedRow ? 700u : 300u, pet.MaxHealth);
    }

    [Fact]
    public void AWarlockPet_IgnoresTheHunterRowsOfCreatureId1()
    {
        var content = new PetContent([StatsRow(1, 700), StatsRow(ImpEntry, 300)], [new PetCreateSpells(ImpEntry, [FireboltRank1])]);
        using var kit = new PetTestKit(ImpSpells(), content);
        kit.Service.InstallDemons(kit.Spells.System);
        (Player owner, _) = kit.AddPlayer(1);
        owner.Level = 10;

        kit.Cast(owner, SummonImpSpell);

        Assert.Equal(300u, Assert.Single(kit.Creatures.Creatures, c => c.IsPet).MaxHealth);
    }

    // --- E: runaway and loyalty -------------------------------------------------------------------------

    private const uint BeastEntry = 5241;
    private const uint DismissSpell = 991003;

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private static PetTestKit BeastKit(CapturingLogger logger) => new(
        [
            Spell(DismissSpell, Effect(SpellEffectName.DismissPet, 0)),
            Spell(CallPetSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0)),
        ],
        extraTemplates:
        [
            CreatureTestKit.Template(BeastEntry, b =>
            {
                b.MinLevel = 20;
                b.MaxLevel = 20;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
                b.Faction = 14;
            }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 },
        ],
        logger: logger);

    private static (Player Hunter, FakeSession Session, Creature Pet) TameBeast(PetTestKit kit)
    {
        (Player hunter, FakeSession session) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 30;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        return (hunter, session, Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515)));
    }

    [Fact]
    public void HunterPet_SurvivesItsTargetEvading_AndCallPetStillWorksAfterADismiss()
    {
        var logger = new CapturingLogger();
        using PetTestKit kit = BeastKit(logger);
        (Player hunter, FakeSession session, Creature pet) = TameBeast(kit);
        Creature enemy = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, 8, 5, 83.5f, 0);
        kit.Controller.HandleCommand(pet, CommandState.Attack, enemy);
        Assert.Same(enemy, pet.Combat.Victim);

        kit.Creatures.EnterEvadeMode(enemy);
        kit.Run(3_000);

        Assert.Same(pet, kit.Creatures.FindCreature(pet.Guid));
        Assert.Equal(pet.Guid, hunter.PetGuid);
        Assert.DoesNotContain(logger.Lines, l => l.Contains("ran away", StringComparison.Ordinal));
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetBroken);

        uint number = pet.Summon!.Charm!.PetNumber;
        kit.Cast(hunter, DismissSpell);
        Assert.True(hunter.PetGuid.IsEmpty);
        session.Clear();
        kit.Cast(hunter, CallPetSpell);

        Creature called = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        Assert.Equal(number, called.Summon!.Charm!.PetNumber);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetTameFailure);
    }

    [Fact]
    public void UnfedRebelliousPet_RunsAway_WithABrokenPacketALogLineAndNoPetToCall()
    {
        var logger = new CapturingLogger();
        using PetTestKit kit = BeastKit(logger);
        (Player hunter, FakeSession session, Creature pet) = TameBeast(kit);
        uint number = pet.Summon!.Charm!.PetNumber;
        PetLoyalty.Restore(pet, PetLoyalty.Rebellious, 10, 0);
        pet.SetUInt32(UpdateFields.UnitFieldPower5, 0);
        session.Clear();

        kit.Run(12_000);

        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetBroken);
        string line = Assert.Single(logger.Lines, l => l.Contains("ran away", StringComparison.Ordinal));
        Assert.Contains($"pet number {number}", line, StringComparison.Ordinal);
        Assert.Contains($"entry {BeastEntry}", line, StringComparison.Ordinal);
        Assert.Contains(logger.Lines, l => l.Contains("pet unsummoned", StringComparison.Ordinal) && l.Contains("loyalty runaway", StringComparison.Ordinal));
        Assert.True(hunter.PetGuid.IsEmpty);

        session.Clear();
        kit.Cast(hunter, CallPetSpell);

        Assert.Equal((byte)PetTameFailureReason.NoPetAvailable,
            Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetTameFailure).Payload[0]);
        Assert.DoesNotContain(kit.Creatures.Creatures, c => c.IsPet);
    }
}
