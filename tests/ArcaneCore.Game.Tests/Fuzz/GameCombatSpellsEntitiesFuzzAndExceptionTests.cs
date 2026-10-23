using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Death.Ghost;
using ArcaneCore.Game.Death.Resurrection;
using ArcaneCore.Game.Death.Travel;
using ArcaneCore.Game.Death.Visibility;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Utility.Targets;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Game.Stats;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Game.Tests.Crafting;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Game.Updates;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Fuzz;

/// <summary>
/// Exception-path and deterministic seeded fuzz tests for <c>ArcaneCore.Game</c>
/// Entities, Updates, Spells, Combat, Stats, Conditions, Ranged, Stealth, Death, and Totems.
/// </summary>
public sealed class GameCombatSpellsEntitiesFuzzAndExceptionTests
{
    // -------------------------------------------------------------------------
    // 1. ObjectGuid & PackedGuid Fuzz + Exception Tests
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void ObjectGuid_FuzzRoundTrip_And_PackedGuidWireInvariants(int seed)
    {
        var rng = new Random(seed);
        byte[] scratch = new byte[16];

        HighGuid[] entryHighs =
        [
            HighGuid.Unit,
            HighGuid.Pet,
            HighGuid.GameObject,
            HighGuid.Transport,
        ];

        for (int i = 0; i < 400; i++)
        {
            ulong raw = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
            if (i == 0)
            {
                raw = 0UL;
            }
            else if (i == 1)
            {
                raw = ulong.MaxValue;
            }

            var guid = new ObjectGuid(raw);
            Assert.Equal(raw == 0UL, guid.IsEmpty);
            Assert.Equal(guid, new ObjectGuid(raw));
            Assert.True(guid == new ObjectGuid(raw));
            Assert.False(guid != new ObjectGuid(raw));
            Assert.Equal(raw.GetHashCode(), guid.GetHashCode());
            Assert.False(string.IsNullOrEmpty(guid.ToString()));

            // PackedGuid round-trip via PacketWriter / PacketReader and ObjectGuid.ToPacked()
            var writer = new PacketWriter(16);
            writer.WritePackedGuid(guid.Value);
            ReadOnlySpan<byte> written = writer.AsSpan();
            Assert.InRange(written.Length, 1, 9);
            Assert.Equal(written.ToArray(), guid.ToPacked());

            var reader = new PacketReader(written);
            ulong decoded = reader.ReadPackedGuid();
            Assert.Equal(guid.Value, decoded);
            Assert.Equal(0, reader.Remaining);

            var tryReader = new PacketReader(written);
            Assert.True(tryReader.TryReadPackedGuid(out ulong tryDecoded));
            Assert.Equal(guid.Value, tryDecoded);
            Assert.Equal(0, tryReader.Remaining);

            // Truncated packed-guid stream must fail cleanly without advancing TryRead position
            if (written.Length > 1)
            {
                int cutLen = rng.Next(1, written.Length);
                written[..cutLen].CopyTo(scratch);
                ReadOnlySpan<byte> truncated = scratch.AsSpan(0, cutLen);

                var truncTryReader = new PacketReader(truncated);
                Assert.False(truncTryReader.TryReadPackedGuid(out ulong zeroOut));
                Assert.Equal(0UL, zeroOut);
                Assert.Equal(0, truncTryReader.Position);

                byte[] truncatedBytes = truncated.ToArray();
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                {
                    var truncReader = new PacketReader(truncatedBytes);
                    _ = truncReader.ReadPackedGuid();
                });
            }

            // HighGuid with Entry construction round-trip
            HighGuid entryHigh = entryHighs[rng.Next(entryHighs.Length)];
            uint entry = (uint)rng.Next(1, 0x00FF_FFFF);
            uint counter24 = (uint)rng.Next(1, 0x00FF_FFFF);
            ObjectGuid withEntry = ObjectGuid.WithEntry(entryHigh, entry, counter24);
            Assert.True(withEntry.HasEntry);
            Assert.Equal(entryHigh, withEntry.High);
            Assert.Equal(entry, withEntry.Entry);
            Assert.Equal(counter24, withEntry.Counter);

            // Player / Item factory round-trip
            uint counter32 = (uint)rng.Next(1, int.MaxValue);
            ObjectGuid playerGuid = ObjectGuid.Player(counter32);
            Assert.True(playerGuid.IsPlayer);
            Assert.False(playerGuid.HasEntry);
            Assert.Equal(0u, playerGuid.Entry);
            Assert.Equal(counter32, playerGuid.Counter);

            ObjectGuid itemGuid = ObjectGuid.Item(counter32);
            Assert.False(itemGuid.IsPlayer);
            Assert.False(itemGuid.HasEntry);
            Assert.Equal(HighGuid.Item, itemGuid.High);
            Assert.Equal(counter32, itemGuid.Counter);
        }
    }

    [Fact]
    public void ObjectGuid_EmptyStreamPackedGuid_ThrowsOrReturnsFalse()
    {
        Assert.True(ObjectGuid.Empty.IsEmpty);
        Assert.False(ObjectGuid.Empty.IsPlayer);

        var emptyReader = new PacketReader(ReadOnlySpan<byte>.Empty);
        Assert.False(emptyReader.TryReadPackedGuid(out ulong result));
        Assert.Equal(0UL, result);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var r = new PacketReader(ReadOnlySpan<byte>.Empty);
            _ = r.ReadPackedGuid();
        });
    }

    // -------------------------------------------------------------------------
    // 2. UpdateMask, UpdateData & UpdateBlockWriter Exception + Fuzz Tests
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void UpdateMask_Constructor_InvalidFieldCount_ThrowsArgumentOutOfRangeException(int invalidFieldCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateMask(invalidFieldCount));
    }

    [Fact]
    public void UpdateMask_GetBit_SetBit_UnsetBit_OutOfRange_ThrowsIndexOutOfRangeException()
    {
        var mask = new UpdateMask(64);
        int maxBit = mask.BlockCount * 32;

        Assert.Throws<IndexOutOfRangeException>(() => mask.SetBit(-1));
        Assert.Throws<IndexOutOfRangeException>(() => mask.UnsetBit(-1));
        Assert.Throws<IndexOutOfRangeException>(() => mask.GetBit(-1));
        Assert.Throws<IndexOutOfRangeException>(() => mask.SetBit(maxBit));
        Assert.Throws<IndexOutOfRangeException>(() => mask.UnsetBit(maxBit));
        Assert.Throws<IndexOutOfRangeException>(() => mask.GetBit(maxBit));
    }

    [Fact]
    public void UpdateData_LifecycleMisuse_ThrowsExpectedExceptions()
    {
        var data = new UpdateData();

        // EndBlock / CancelBlock when no block is open
        Assert.Throws<InvalidOperationException>(() => data.EndBlock());
        Assert.Throws<InvalidOperationException>(() => data.CancelBlock());

        // Null callback
        Assert.Throws<ArgumentNullException>(() => data.Flush(null!, 0));

        // Double BeginBlock
        _ = data.BeginBlock();
        Assert.Throws<InvalidOperationException>(() => data.BeginBlock());

        // Flush / FlushTo while a block is open
        Assert.Throws<InvalidOperationException>(() => data.Flush((_, _) => { }, 0));
        Assert.Throws<InvalidOperationException>(() => data.FlushTo((_, _) => { }, 0));

        // CancelBlock works when nothing was written in the open block
        data.CancelBlock();
        Assert.Equal(0, data.BlockCount);
        Assert.True(data.IsEmpty);

        // CancelBlock throws once bytes were written into the open block
        PacketWriter blockWriter = data.BeginBlock();
        blockWriter.WriteByte(1);
        Assert.Throws<InvalidOperationException>(() => data.CancelBlock());
        data.EndBlock();
        Assert.Equal(1, data.BlockCount);
        Assert.False(data.IsEmpty);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void UpdateMask_And_UpdateBlockWriter_FuzzRoundTrip(int seed)
    {
        var rng = new Random(seed);

        for (int iter = 0; iter < 150; iter++)
        {
            int fieldCount = rng.Next(1, 513);
            var mask = new UpdateMask(fieldCount);
            Assert.Equal(fieldCount, mask.FieldCount);
            Assert.Equal((fieldCount + 31) >> 5, mask.BlockCount);
            Assert.True(mask.IsEmpty);
            Assert.Equal(-1, mask.NextSetBit(0));

            var expectedSet = new SortedSet<int>();
            int toggles = rng.Next(0, fieldCount + 1);
            for (int t = 0; t < toggles; t++)
            {
                int idx = rng.Next(0, fieldCount);
                if (rng.Next(4) == 0)
                {
                    mask.UnsetBit(idx);
                    expectedSet.Remove(idx);
                }
                else
                {
                    mask.SetBit(idx);
                    expectedSet.Add(idx);
                }
            }

            Assert.Equal(expectedSet.Count == 0, mask.IsEmpty);

            for (int i = 0; i < fieldCount; i++)
            {
                Assert.Equal(expectedSet.Contains(i), mask.GetBit(i));
            }

            // Walk NextSetBit and compare to SortedSet
            var walked = new List<int>();
            for (int bit = mask.NextSetBit(0); bit >= 0; bit = mask.NextSetBit(bit + 1))
            {
                walked.Add(bit);
            }
            Assert.Equal(expectedSet, walked);

            // Serialize mask via WriteTo: writes 1-byte BlockCount followed by BlockCount uint32 words
            var writer = new PacketWriter(64);
            mask.WriteTo(writer);
            var reader = new PacketReader(writer.AsSpan());
            byte blockCount = reader.ReadByte();
            Assert.Equal((byte)mask.BlockCount, blockCount);

            var decodedSet = new List<int>();
            for (int b = 0; b < blockCount; b++)
            {
                uint word = reader.ReadUInt32();
                for (int bitOffset = 0; bitOffset < 32; bitOffset++)
                {
                    if ((word & (1u << bitOffset)) != 0)
                    {
                        decodedSet.Add((b << 5) + bitOffset);
                    }
                }
            }
            Assert.Equal(0, reader.Remaining);
            Assert.Equal(expectedSet, decodedSet);

            mask.Clear();
            Assert.True(mask.IsEmpty);
        }
    }

    // -------------------------------------------------------------------------
    // 3. Entities (Player, Unit, WorldObject) Exception + Fuzz Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Player_Constructor_And_SubsystemAttachments_ThrowOnInvalidState()
    {
        var session = new FakeSession(1);
        var appearance = new PlayerAppearance(1, 1, PowerType.Mana, 1, 1, 1, 1, 100, 100);

        Assert.Throws<NullReferenceException>(() =>
        {
            _ = new Player(null!, appearance, session);
        });

        Assert.Throws<NullReferenceException>(() =>
        {
            _ = TestWorld.CreatePlayer(1, 0, 0, null!);
        });

        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        Assert.Throws<ArgumentNullException>(() => player.AttachEnchantments(null!));
        Assert.Throws<ArgumentNullException>(() => player.AttachSkills(null!));

        using var crafting = new CraftingTestKit();
        PlayerSkills skills = crafting.AttachSkills();
        Assert.Throws<InvalidOperationException>(() =>
        {
            crafting.Player.AttachSkills(skills);
        });
    }

    [Fact]
    public void Player_QuestSettlementGuards_ThrowOnInvalidTransitionsAndUnauthorizedMutations()
    {
        var session = new FakeSession(10);
        Player player = TestWorld.CreatePlayer(10, 0, 0, session);

        Assert.Throws<ArgumentException>(() => player.BeginQuestSettlement(Guid.Empty));
        Assert.Throws<InvalidOperationException>(() => player.BeginQuestSettlementPublication(Guid.NewGuid()));

        Guid opId = Guid.NewGuid();
        Guid wrongOpId = Guid.NewGuid();
        Assert.True(player.BeginQuestSettlement(opId));
        Assert.False(player.BeginQuestSettlement(Guid.NewGuid()));

        // Mutations are blocked before publication scope opens
        Assert.False(player.CanMutateQuestSettlementState);
        Assert.Throws<InvalidOperationException>(() => player.Money = 500u);

        // Wrong opId rejected for publication
        Assert.Throws<InvalidOperationException>(() => player.BeginQuestSettlementPublication(wrongOpId));
        Assert.False(player.EndQuestSettlement(wrongOpId));

        using (player.BeginQuestSettlementPublication(opId))
        {
            Assert.True(player.CanMutateQuestSettlementState);
            // Double publication scope is forbidden
            Assert.Throws<InvalidOperationException>(() => player.BeginQuestSettlementPublication(opId));
            // Ending settlement while publication scope is open is forbidden
            Assert.Throws<InvalidOperationException>(() => player.EndQuestSettlement(opId));

            // Inside publication scope, mutations are permitted
            player.Money = 250u;
            Assert.Equal(250u, player.Money);
        }

        // Outside publication scope again, mutations are blocked until EndQuestSettlement
        Assert.Throws<InvalidOperationException>(() => player.Money = 300u);

        Assert.True(player.EndQuestSettlement(opId));
        player.Money = 300u;
        Assert.Equal(300u, player.Money);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Unit_And_WorldObject_FieldsAndRelocation_FuzzInvariants(int seed)
    {
        var session = new FakeSession(99);
        Player unit = TestWorld.CreatePlayer(99, 10f, 20f, session);
        var rng = new Random(seed);

        for (int i = 0; i < 200; i++)
        {
            uint maxHp = (uint)rng.Next(1, 100_000);
            uint hp = (uint)rng.Next(0, (int)maxHp + 1);
            unit.MaxHealth = maxHp;
            unit.Health = hp;
            Assert.Equal(maxHp, unit.MaxHealth);
            Assert.Equal(hp, unit.Health);
            Assert.Equal(hp > 0, unit.IsAlive);
            Assert.InRange(unit.HealthPercent, 0f, 100f);

            int idx = rng.Next(UpdateFields.ObjectEnd, UpdateFields.PlayerEnd - 1);
            int signedVal = rng.Next(int.MinValue, int.MaxValue);
            unit.SetInt32(idx, signedVal);
            Assert.Equal(signedVal, unit.GetInt32(idx));

            for (byte b = 0; b < 4; b++)
            {
                byte byteVal = (byte)rng.Next(0, 256);
                unit.SetByte(idx, b, byteVal);
                Assert.Equal(byteVal, unit.GetByte(idx, b));
            }

            for (byte s = 0; s < 2; s++)
            {
                ushort shortVal = (ushort)rng.Next(0, 65536);
                unit.SetUInt16(idx, s, shortVal);
                Assert.Equal(shortVal, unit.GetUInt16(idx, s));
            }

            float finiteX = ((float)rng.NextDouble() * 2000f) - 1000f;
            float finiteY = ((float)rng.NextDouble() * 2000f) - 1000f;
            float finiteZ = ((float)rng.NextDouble() * 200f) - 50f;
            unit.Relocate(finiteX, finiteY, finiteZ);
            Assert.False(unit.ConsumeRejectedNonFiniteRelocation());
            Assert.Equal(finiteX, unit.X);
            Assert.Equal(finiteY, unit.Y);
            Assert.Equal(finiteZ, unit.Z);

            // Non-finite relocation must be rejected and flagged without corrupting coordinates
            unit.Relocate(float.NaN, finiteY, finiteZ);
            Assert.True(unit.ConsumeRejectedNonFiniteRelocation());
            Assert.Equal(finiteX, unit.X);
        }
    }

    // -------------------------------------------------------------------------
    // 4. Combat Subsystem Exception + Fuzz Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Combat_ServicesAndCalculators_ThrowOnNullArguments()
    {
        var (world, map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime worldScope = world;
        var session1 = new FakeSession(1);
        var session2 = new FakeSession(2);
        Player p1 = CombatTestKit.AddPlayer(world, 1, 0, 0, session1);
        Player p2 = CombatTestKit.AddPlayer(world, 2, 5, 0, session2);
        using var spellKit = new SpellTestKit();
        SpellSystem spells = spellKit.System;
        var dummySpell = new SpellInfo { Id = 1, Name = "Heal", Effects = [] };

        // ThreatList
        ThreatList threatList = p1.Combat.Threat;
        Assert.Throws<ArgumentNullException>(() => threatList.AddThreat(null!, 10f));
        Assert.Throws<ArgumentNullException>(() => threatList.GetTauntTarget(null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => threatList.GetTauntTarget(_ => null, null!));
        Assert.Throws<ArgumentNullException>(() => threatList.SelectVictim(null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => threatList.SelectVictim(_ => true, null!));
        Assert.Throws<ArgumentNullException>(() => threatList.SelectVictim(0, null!, (_, _) => true));
        Assert.Throws<ArgumentNullException>(() => threatList.SelectVictim(0, (_, _) => true, null!));

        // ThreatCalc & ThreatRules
        Assert.Throws<ArgumentNullException>(() => ThreatCalc.Calc(null, null!, 10f, false, 1u, null));
        Assert.Throws<ArgumentNullException>(() => ThreatRules.CanHaveThreatList(null!));
        Assert.Throws<ArgumentNullException>(() => ThreatRules.IsAssistThreatSuppressed(null!));
        Assert.Throws<ArgumentNullException>(() => ThreatRules.CanPullPetOwner(null!, p2));
        Assert.Throws<ArgumentNullException>(() => ThreatRules.CanPullPetOwner(p1, null!));
        Assert.Throws<ArgumentNullException>(() => ThreatRules.IsTargetUnreachable(null!));

        // HealThreat
        Assert.Throws<ArgumentNullException>(() => HealThreat.ForHeal(null!, p1, p2, dummySpell, 100u, false));
        Assert.Throws<ArgumentNullException>(() => HealThreat.ForHeal(map.Combat, null!, p2, dummySpell, 100u, false));
        Assert.Throws<ArgumentNullException>(() => HealThreat.ForHeal(map.Combat, p1, null!, dummySpell, 100u, false));
        Assert.Throws<ArgumentNullException>(() => HealThreat.ForHeal(map.Combat, p1, p2, null!, 100u, false));

        // HostileRefs
        Assert.Throws<ArgumentNullException>(() => HostileRefs.AddTempThreat(null!, 10f, true));
        Assert.Throws<ArgumentNullException>(() => HostileRefs.AddThreatPercent(null!, 10));
        Assert.Throws<ArgumentNullException>(() => HostileRefs.DeleteReferences(null!));
        Assert.Throws<ArgumentNullException>(() => HostileRefs.SetOnlineOfflineState(null!, true));
        Assert.Throws<ArgumentNullException>(() => HostileRefs.UpdateThreatTables(null!));
        Assert.Throws<ArgumentNullException>(() => HostileRefs.ThreatAssist(null!, p2, 10f, null, null));
        Assert.Throws<ArgumentNullException>(() => HostileRefs.ThreatAssist(p1, null!, 10f, null, null));

        // Taunt & SpellThreatTable
        Assert.Throws<ArgumentNullException>(() => Taunt.Apply(null!, p2));
        Assert.Throws<ArgumentNullException>(() => Taunt.Apply(p1, null!));
        Assert.Throws<ArgumentNullException>(() => Taunt.FadeOut(null!, p2));
        Assert.Throws<ArgumentNullException>(() => Taunt.FadeOut(p1, null!));
        Assert.Throws<ArgumentNullException>(() => new SpellThreatTable().Replace(null!));

        // PositionalRules
        Assert.Throws<ArgumentNullException>(() => PositionalRules.HasInArc(null!, p2));
        Assert.Throws<ArgumentNullException>(() => PositionalRules.HasInArc(p1, null!));
        Assert.Throws<ArgumentNullException>(() => PositionalRules.IsBehindTarget(null!, p2, false));
        Assert.Throws<ArgumentNullException>(() => PositionalRules.IsBehindTarget(p1, null!, false));
        Assert.Throws<ArgumentNullException>(() => PositionalRules.IsFromBehindOnly(null!));
        Assert.Throws<ArgumentNullException>(() => PositionalRules.IsFacingRequired(null!));

        // PowerRules & RegenModifiers
        Assert.Throws<ArgumentNullException>(() => PowerRules.Refund(null!, SpellMissInfo.Miss, 10u));
        Assert.Throws<ArgumentNullException>(() => new PowerRefundObserver().OnTargetOutcome(null!, default));
        Assert.Throws<ArgumentNullException>(() =>
            RegenModifiers.HealthPerTick(10f, 100f, false, true, false, false, 0, null!, [], 0));
        Assert.Throws<ArgumentNullException>(() =>
            RegenModifiers.HealthPerTick(10f, 100f, false, true, false, false, 0, [], null!, 0));

        // CombatEnvironment & SpellSystemPowerAuras
        Assert.Throws<ArgumentNullException>(() => new CombatEnvironment(null!));
        Assert.Throws<InvalidOperationException>(() => CombatEnvironment.Default.Auras = null);
        Assert.Throws<InvalidOperationException>(() => CombatEnvironment.Default.MeleeSpells = null);
        Assert.Throws<InvalidOperationException>(() => CombatEnvironment.Default.ShapeshiftForms = null);
        Assert.Throws<InvalidOperationException>(() => CombatEnvironment.Default.WorldBossLevelDiff = 5);
        Assert.Throws<ArgumentException>(() => CombatEnvironment.Register(world, CombatEnvironment.Default));
        Assert.Throws<ArgumentNullException>(() => CombatEnvironment.Register(null!, new CombatEnvironment(new CombatOptions())));
        Assert.Throws<ArgumentNullException>(() => CombatEnvironment.Register(world, null!));
        Assert.Throws<ArgumentNullException>(() => CombatEnvironment.GetOrCreate(null!, () => new CombatOptions()));
        Assert.Throws<ArgumentNullException>(() => CombatEnvironment.GetOrCreate(world, null!));
        Assert.Throws<ArgumentNullException>(() => new SpellSystemPowerAuras(null!));

        var powerAuras = new SpellSystemPowerAuras(spells);
        Assert.Throws<ArgumentNullException>(() => powerAuras.HasAura(null!, 1u, 0));
        Assert.Throws<ArgumentNullException>(() => powerAuras.HasAuraType(null!, AuraType.None));
        Assert.Throws<ArgumentNullException>(() => powerAuras.GetAuras(null!, AuraType.None));
        Assert.Throws<ArgumentNullException>(() => powerAuras.GetPowerRegenFactor(null!, PowerType.Mana));
        Assert.Throws<ArgumentNullException>(() => powerAuras.GetTotalAuraModifier(null!, AuraType.None));
        Assert.Throws<ArgumentNullException>(() => powerAuras.GetTotalAuraModifierByMisc(null!, AuraType.None, 0));
        Assert.Throws<ArgumentNullException>(() => powerAuras.GetRegenAuras(null!, AuraType.None));
        Assert.Throws<ArgumentNullException>(() => powerAuras.IsPolymorphed(null!));

        // MeleeDamageBonus, WeaponAuraModifiers, SpellSystemMeleeHooks
        Assert.Throws<ArgumentNullException>(() => MeleeDamageBonus.Done(null!, p1, p2, 10f, WeaponAttackType.BaseAttack));
        Assert.Throws<ArgumentNullException>(() => MeleeDamageBonus.Done(spells, null!, p2, 10f, WeaponAttackType.BaseAttack));
        Assert.Throws<ArgumentNullException>(() => MeleeDamageBonus.Done(spells, p1, null!, 10f, WeaponAttackType.BaseAttack));
        Assert.Throws<ArgumentNullException>(() => MeleeDamageBonus.Taken(null!, p1, p2, 10f, WeaponAttackType.BaseAttack, 1u));
        Assert.Throws<ArgumentNullException>(() => MeleeDamageBonus.Taken(spells, null!, p2, 10f, WeaponAttackType.BaseAttack, 1u));
        Assert.Throws<ArgumentNullException>(() => MeleeDamageBonus.Taken(spells, p1, null!, 10f, WeaponAttackType.BaseAttack, 1u));
        Assert.Throws<ArgumentNullException>(() => MeleeDamageBonus.HandTotalPct(null!, WeaponAttackType.BaseAttack));
        Assert.Throws<ArgumentNullException>(() => WeaponAuraModifiers.Get(null!, p1, WeaponAttackType.BaseAttack, AuraType.None));
        Assert.Throws<ArgumentNullException>(() => WeaponAuraModifiers.Get(spells, null!, WeaponAttackType.BaseAttack, AuraType.None));
        Assert.Throws<ArgumentNullException>(() => WeaponAuraModifiers.Fits(null!, WeaponAttackType.BaseAttack, dummySpell));
        Assert.Throws<ArgumentNullException>(() => WeaponAuraModifiers.Fits(p1, WeaponAttackType.BaseAttack, null!));
        Assert.Throws<ArgumentNullException>(() => new SpellSystemMeleeHooks(null!));
        var meleeHooks = new SpellSystemMeleeHooks(spells);
        Assert.Throws<ArgumentNullException>(() => meleeHooks.IsNonMeleeSpellCasted(null!));
        Assert.Throws<ArgumentNullException>(() => meleeHooks.TryCastQueuedSwingSpell(null!, p2));
        Assert.Throws<ArgumentNullException>(() => meleeHooks.TryCastQueuedSwingSpell(p1, null!));
        Assert.Throws<ArgumentNullException>(() => meleeHooks.OnMeleeAttackStopped(null!));

        // ComboPointService
        Assert.Throws<ArgumentNullException>(() => new ComboPointService(null!, (_, _) => null));
        Assert.Throws<ArgumentNullException>(() => new ComboPointService(spells, null!));
        var combo = new ComboPointService(spells, (_, _) => null);
        Assert.Throws<ArgumentNullException>(() => combo.AddComboPoints(null!, p2, 1));
        Assert.Throws<ArgumentNullException>(() => combo.AddComboPoints(p1, null!, 1));
        Assert.Throws<ArgumentNullException>(() => combo.ClearComboPoints(null!));
        Assert.Throws<ArgumentNullException>(() => combo.OnTargetGone(null!));
        Assert.Throws<ArgumentNullException>(() => combo.OnPlayerGone(null!));
        Assert.Throws<ArgumentNullException>(() => combo.Observe(null!));

        // DuelService, DuelInfo, DuelPackets, DuelSpellEffects
        Assert.Throws<ArgumentNullException>(() => new DuelService(null!, () => 0L));
        Assert.Throws<ArgumentNullException>(() => new DuelService(new DuelOptions(), null!));
        var duel = new DuelService(new DuelOptions(), () => 0L);
        Assert.Throws<ArgumentNullException>(() => DuelService.Register(null!, duel));
        Assert.Throws<ArgumentNullException>(() => DuelService.Register(world, null!));
        Assert.Throws<ArgumentNullException>(() => duel.Install(null!));
        Assert.Throws<ArgumentNullException>(() => duel.Challenge(null!, p2, 1u, 1000));
        Assert.Throws<ArgumentNullException>(() => duel.Challenge(p1, null!, 1u, 1000));
        Assert.Throws<ArgumentNullException>(() => duel.Begin(null!, p2, null!));
        Assert.Throws<ArgumentNullException>(() => duel.Begin(p1, null!, null!));
        Assert.Throws<ArgumentNullException>(() => duel.Begin(p1, p2, null!));
        Assert.Throws<ArgumentNullException>(() => duel.Accept(null!));
        Assert.Throws<ArgumentNullException>(() => duel.Cancel(null!));
        Assert.Throws<ArgumentNullException>(() => duel.Complete(null!, DuelCompleteType.Interrupted));
        Assert.Throws<ArgumentNullException>(() => duel.CombatStopWithPets(null!, true));
        Assert.Throws<ArgumentNullException>(() => new DuelInfo(null!, p2));
        Assert.Throws<ArgumentNullException>(() => new DuelInfo(p1, null!));
        Assert.Throws<ArgumentNullException>(() => DuelPackets.Winner(false, null!, "Loser"));
        Assert.Throws<ArgumentNullException>(() => DuelPackets.Winner(false, "Winner", null!));
        Assert.Throws<ArgumentNullException>(() => new DuelSpellEffects().Register(null!));

        // AuraStateService, ReactiveService, Corpse, CombatHooks, FactionCombatHooks
        Assert.Throws<ArgumentNullException>(() => new AuraStateService(null!, _ => []));
        Assert.Throws<ArgumentNullException>(() => new AuraStateService(spells, null!));
        var auraState = new AuraStateService(spells, _ => []);
        Assert.Throws<ArgumentNullException>(() => auraState.ModifyAuraState(null!, AuraState.Defense, true));
        Assert.Throws<ArgumentNullException>(() => auraState.HasAuraState(null!, AuraState.Defense));
        Assert.Throws<ArgumentNullException>(() => auraState.UpdateHealthState(null!));
        Assert.Throws<ArgumentNullException>(() => AuraStateCastChecks.Install(null!, auraState));
        Assert.Throws<ArgumentNullException>(() => AuraStateCastChecks.Install(spells, null!));

        Assert.Throws<ArgumentNullException>(() => new ReactiveService(null!, () => combo));
        Assert.Throws<ArgumentNullException>(() => new ReactiveService(auraState, null!));
        var reactive = new ReactiveService(auraState, () => combo);
        Assert.Throws<ArgumentNullException>(() => reactive.StartReactiveTimer(null!, ReactiveType.Defense, ObjectGuid.Empty));
        Assert.Throws<ArgumentNullException>(() => reactive.OnAttackAvoided(null!, p2, AttackAvoidance.Parry));
        Assert.Throws<ArgumentNullException>(() => reactive.OnAttackAvoided(p1, null!, AttackAvoidance.Parry));
        Assert.Throws<ArgumentNullException>(() => reactive.UpdateUnit(null!, 100u));
        Assert.Throws<ArgumentNullException>(() => reactive.Forget(null!));
        Assert.Throws<ArgumentNullException>(() => reactive.ClearAllReactives(null!));
        Assert.Throws<ArgumentNullException>(() => reactive.OnUnitDied(null!));
        Assert.Throws<ArgumentNullException>(() => reactive.Observe(null!));
        Assert.Throws<ArgumentNullException>(() => new ReactiveUpdater(reactive).Update(null!, 100u));

        Assert.Throws<ArgumentNullException>(() => Corpse.CreateFor(null!, false));
        Assert.Throws<ArgumentNullException>(() => Corpse.CreateBones(null!));
        Assert.Throws<ArgumentNullException>(() => Corpse.CreateAt(null!, 0, 0, 0, 0, 0, CorpseType.Bones));
        Assert.Throws<ArgumentNullException>(() => CombatHooks.Register(null!, CombatHooks.Default));
        Assert.Throws<ArgumentNullException>(() => CombatHooks.Register(world, null!));
        Assert.Throws<ArgumentNullException>(() => CombatHooks.TryRegister(null!, CombatHooks.Default));
        Assert.Throws<ArgumentNullException>(() => CombatHooks.TryRegister(world, null!));
        Assert.Throws<ArgumentNullException>(() => new FactionCombatHooks(null!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void MeleeHitTable_And_ThreatList_FuzzInvariants(int seed)
    {
        var rng = new Random(seed);
        Class[] classes = [Class.Warrior, Class.Paladin, Class.Hunter, Class.Rogue, Class.Priest, Class.Shaman, Class.Mage, Class.Warlock, Class.Druid];

        for (int i = 0; i < 300; i++)
        {
            var input = new MeleeRollInput
            {
                AttackType = (WeaponAttackType)rng.Next(0, 3),
                VictimIsPlayer = rng.Next(2) == 0,
                AttackerIsCreature = rng.Next(2) == 0,
                AttackerIsPlayerControlled = rng.Next(2) == 0,
                VictimIsPlayerControlled = rng.Next(2) == 0,
                VictimEvading = rng.Next(10) == 0,
                VictimStanding = rng.Next(4) != 0,
                FromBehind = rng.Next(2) == 0,
                AttackerCanCrush = rng.Next(2) == 0,
                VictimCreatureCanParry = rng.Next(2) == 0,
                VictimCreatureCanBlock = rng.Next(2) == 0,
                VictimLevel = (byte)rng.Next(1, 75),
                AttackerMaxSkill = rng.Next(5, 350),
                VictimMaxSkill = rng.Next(5, 350),
                AttackerWeaponSkill = rng.Next(1, 400),
                VictimDefenseSkill = rng.Next(1, 400),
                DualWield = rng.Next(2) == 0,
                HitBonus = ((float)rng.NextDouble() * 30f) - 10f,
                VictimAttackerHitBonus = ((float)rng.NextDouble() * 10f) - 5f,
                VictimAttackerCritBonus = ((float)rng.NextDouble() * 10f) - 5f,
                BaseCritChance = ((float)rng.NextDouble() * 60f) - 10f,
                DodgeChance = ((float)rng.NextDouble() * 40f) - 5f,
                ParryChance = ((float)rng.NextDouble() * 40f) - 5f,
                BlockChance = ((float)rng.NextDouble() * 40f) - 5f,
            };

            int roll = rng.Next(-500, 12_000);
            MeleeRollResult result = MeleeHitTable.Roll(in input, roll);
            Assert.True(Enum.IsDefined(result.Outcome));

            if (input.VictimEvading)
            {
                Assert.Equal(MeleeHitOutcome.Evade, result.Outcome);
            }

            if (!input.VictimStanding && !input.VictimEvading)
            {
                Assert.NotEqual(MeleeHitOutcome.Miss, result.Outcome);
                if (input.VictimIsPlayer && ((int)(MeleeHitTable.CritChance(in input) * 100) > 0 || input.AttackerIsCreature))
                {
                    Assert.Equal(MeleeHitOutcome.Crit, result.Outcome);
                }
            }

            if (input.AttackType == WeaponAttackType.RangedAttack && !input.VictimEvading)
            {
                Assert.NotEqual(MeleeHitOutcome.Dodge, result.Outcome);
                Assert.NotEqual(MeleeHitOutcome.Parry, result.Outcome);
                Assert.NotEqual(MeleeHitOutcome.Block, result.Outcome);
                Assert.NotEqual(MeleeHitOutcome.Glancing, result.Outcome);
                Assert.NotEqual(MeleeHitOutcome.Crushing, result.Outcome);
            }

            if (input.FromBehind && !input.VictimEvading)
            {
                Assert.NotEqual(MeleeHitOutcome.Parry, result.Outcome);
                Assert.NotEqual(MeleeHitOutcome.Block, result.Outcome);
                if (input.VictimIsPlayer)
                {
                    Assert.NotEqual(MeleeHitOutcome.Dodge, result.Outcome);
                }
            }

            // Armor reduction invariants: 0 <= reduced <= damage, and monotonic in armor
            uint rawDamage = (uint)rng.Next(0, 50_000);
            float armorLow = ((float)rng.NextDouble() * 10_000f) - 500f;
            float armorHigh = armorLow + ((float)rng.NextDouble() * 15_000f);
            int level = rng.Next(1, 70);

            uint reducedLow = MeleeHitTable.ApplyArmor(rawDamage, armorLow, level);
            uint reducedHigh = MeleeHitTable.ApplyArmor(rawDamage, armorHigh, level);
            Assert.True(reducedLow <= rawDamage);
            Assert.True(reducedHigh <= reducedLow);

            Class cls = classes[rng.Next(classes.Length)];
            (float lowEnd, float highEnd) = MeleeHitTable.GlancingRange(
                input.VictimDefenseSkill,
                input.AttackerWeaponSkill,
                cls);
            Assert.InRange(lowEnd, 0.01f, 1.0f);
            Assert.InRange(highEnd, lowEnd, 1.0f);
        }

        // ThreatList fuzz invariants: Threat stays non-negative and online entries stay sorted descending
        var (world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime worldScope = world;
        CombatTestUnit mob = new(60, 5000);
        Unit[] units =
        [
            CombatTestKit.AddPlayer(world, 101, 0, 0, new FakeSession(101)),
            CombatTestKit.AddPlayer(world, 102, 2, 0, new FakeSession(102)),
            CombatTestKit.AddPlayer(world, 103, 4, 0, new FakeSession(103)),
            CombatTestKit.AddPlayer(world, 104, 6, 0, new FakeSession(104)),
        ];

        ThreatList threat = mob.Combat.Threat;
        for (int step = 0; step < 200; step++)
        {
            Unit u = units[rng.Next(units.Length)];
            int op = rng.Next(6);
            switch (op)
            {
                case 0:
                    threat.AddThreat(u, ((float)rng.NextDouble() * 1000f) - 400f);
                    break;
                case 1:
                    threat.ModifyThreatPercent(u, rng.Next(-120, 200));
                    break;
                case 2:
                    threat.ApplyTempThreatModifier(u, ((float)rng.NextDouble() * 500f) - 200f);
                    break;
                case 3:
                    threat.ResetTempThreat(u);
                    break;
                case 4:
                    threat.SetOnlineState(u, rng.Next(2) == 0);
                    break;
                case 5:
                    threat.ScaleThreat(u, rng.Next(0, 250));
                    break;
            }

            float prev = float.MaxValue;
            foreach (ThreatEntry entry in threat.Entries)
            {
                Assert.True(entry.Threat >= 0f);
                Assert.True(entry.IsOnline);
                Assert.True(entry.Threat <= prev + 1e-4f);
                prev = entry.Threat;
            }
        }
    }

    // -------------------------------------------------------------------------
    // 5. Stats Subsystem Exception + Fuzz Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Stats_AgilityRateTable_And_UnitModLedger_ThrowOnInvalidArguments()
    {
        // AgilityRateTable.FromSparse validation
        float[] missingLevel1 = new float[60];
        missingLevel1[59] = 20f;
        Assert.Throws<ArgumentException>(() => AgilityRateTable.FromSparse(missingLevel1));

        float[] missingLevel60 = new float[60];
        missingLevel60[0] = 5f;
        Assert.Throws<ArgumentException>(() => AgilityRateTable.FromSparse(missingLevel60));

        float[] shortWithoutLast = new float[10];
        shortWithoutLast[0] = 5f;
        Assert.Throws<ArgumentException>(() => AgilityRateTable.FromSparse(shortWithoutLast));

        float[] negativeRate = new float[60];
        negativeRate[0] = 5f;
        negativeRate[10] = -1f;
        negativeRate[59] = 20f;
        Assert.Throws<ArgumentException>(() => AgilityRateTable.FromSparse(negativeRate));

        float[] nanRate = new float[60];
        nanRate[0] = 5f;
        nanRate[20] = float.NaN;
        nanRate[59] = 20f;
        Assert.Throws<ArgumentException>(() => AgilityRateTable.FromSparse(nanRate));

        // AgilityRateTable.FromEntries validation
        Assert.Throws<ArgumentNullException>(() => AgilityRateTable.FromEntries(null!));
        Assert.Throws<ArgumentException>(() => AgilityRateTable.FromEntries([new KeyValuePair<int, float>(0, 5f), new KeyValuePair<int, float>(60, 20f)]));
        Assert.Throws<ArgumentException>(() => AgilityRateTable.FromEntries([new KeyValuePair<int, float>(1, 0f), new KeyValuePair<int, float>(60, 20f)]));
        Assert.Throws<ArgumentException>(() => AgilityRateTable.FromEntries([new KeyValuePair<int, float>(1, float.PositiveInfinity), new KeyValuePair<int, float>(60, 20f)]));

        // AgilityRates constructor
        AgilityRateTable validTable = AgilityRateTable.FromEntries([new KeyValuePair<int, float>(1, 4f), new KeyValuePair<int, float>(60, 20f)]);
        var validDict = new Dictionary<Class, AgilityRateTable> { [Class.Warrior] = validTable };
        Assert.Throws<ArgumentNullException>(() => new AgilityRates(null!, validDict));
        Assert.Throws<ArgumentNullException>(() => new AgilityRates(validDict, null!));

        // UnitModLedger validation
        Assert.Throws<ArgumentNullException>(() => UnitModLedger.For(null!));
        Assert.Throws<ArgumentNullException>(() => UnitModLedger.Find(null!));

        var ledger = new UnitModLedger();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ledger.Apply(UnitMods.End, UnitModifierType.BasePct, 10f, true));
        Assert.Throws<IndexOutOfRangeException>(() =>
            ledger.Apply((UnitMods)(-1), UnitModifierType.BasePct, 10f, true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ledger.Apply(UnitMods.StatStrength, UnitModifierType.BaseValue, 10f, true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ledger.Apply(UnitMods.StatStrength, UnitModifierType.TotalValue, 10f, true));

        // CreatureDamageStats, FormStatListener, PlayerStatAuras, PlayerStatSystem null guards
        using var spellKit = new SpellTestKit();
        var (player, _) = spellKit.AddPlayer(1);

        Assert.Throws<ArgumentNullException>(() => CreatureDamageStats.UpdateMelee(null!, spellKit.System));
        Assert.Throws<ArgumentNullException>(() => CreatureDamageStats.UpdateRanged(null!));
        Assert.Throws<ArgumentNullException>(() => CreatureDamageStats.HasWeapon(null!));

        var formListener = new FormStatListener();
        Assert.Throws<ArgumentNullException>(() => formListener.OnFormChanged(null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => formListener.Attach(null!));
        formListener.Attach(spellKit.System);
        Assert.Throws<InvalidOperationException>(() => formListener.Attach(spellKit.System));

        var statAuras = new PlayerStatAuras();
        var term = new StatAuraTerm(AuraType.ModStat, 10, 0, true, new SpellInfo { Id = 1 }, ObjectGuid.Empty);
        Assert.Throws<ArgumentNullException>(() => statAuras.Add(null!, term));
        Assert.Throws<ArgumentNullException>(() => statAuras.Remove(null!));

        var statSystem = new PlayerStatSystem();
        Assert.Throws<ArgumentNullException>(() => statSystem.Attach(null!));
        Assert.Throws<ArgumentNullException>(() => statSystem.UpdateAll(null!));
        var dummyItem = new Item(1u, new ItemTemplate { Entry = 1 }, player.Guid);
        Assert.Throws<ArgumentNullException>(() => statSystem.ApplyItem(null!, dummyItem, 0, true));
        Assert.Throws<ArgumentNullException>(() => statSystem.ApplyItem(player, null!, 0, true));
        Assert.Throws<ArgumentNullException>(() => statSystem.OnFormChanged(null!, 0, false));
        Assert.Throws<ArgumentNullException>(() => statSystem.OnDisarmChanged(null!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void UnitModLedger_And_AgilityRateTable_FuzzReversibility(int seed)
    {
        var rng = new Random(seed);

        // 1. Sparse interpolation monotonicity between anchor points
        var entries = new List<KeyValuePair<int, float>>
        {
            new(1, 4.0f + ((float)rng.NextDouble() * 2f)),
            new(20, 8.0f + ((float)rng.NextDouble() * 3f)),
            new(40, 13.0f + ((float)rng.NextDouble() * 3f)),
            new(60, 20.0f + ((float)rng.NextDouble() * 5f)),
        };
        AgilityRateTable table = AgilityRateTable.FromEntries(entries);
        float prevRate = 0f;
        for (uint lvl = 1; lvl <= 80; lvl++)
        {
            float r = table.Get(lvl);
            Assert.True(r >= prevRate - 1e-4f);
            prevRate = r;
        }
        Assert.Equal(table.Get(1), table.Get(0));
        Assert.Equal(table.Get(60), table.Get(99));

        // 2. UnitModLedger apply + unapply returns exact neutral state
        var ledger = new UnitModLedger();
        var ops = new List<(UnitMods Mod, UnitModifierType Kind, float Val)>();

        for (int i = 0; i < 150; i++)
        {
            var mod = (UnitMods)rng.Next(0, (int)UnitMods.End);
            UnitModifierType kind = rng.Next(2) == 0
                ? UnitModifierType.BasePct
                : UnitModifierType.TotalPct;
            float val = rng.Next(-50, 150);
            if (val == 0f)
            {
                val = 15f;
            }

            ledger.Apply(mod, kind, val, apply: true);
            ops.Add((mod, kind, val));
        }

        // Unapply in shuffled order
        for (int i = ops.Count - 1; i >= 0; i--)
        {
            int swapIdx = rng.Next(i + 1);
            (ops[i], ops[swapIdx]) = (ops[swapIdx], ops[i]);
            (UnitMods mod, UnitModifierType kind, float val) = ops[i];
            ledger.Apply(mod, kind, val, apply: false);
        }

        for (int m = 0; m < (int)UnitMods.End; m++)
        {
            var mod = (UnitMods)m;
            Assert.True(ledger.IsNeutral(mod));
            Assert.Equal(0, ledger.Applied(mod));
            Assert.Equal(1.0f, ledger.BasePct(mod), 4);
            Assert.Equal(UnitModConstants.Default(UnitModifierType.TotalPct, mod), ledger.TotalPct(mod), 4);
        }
    }

    // -------------------------------------------------------------------------
    // 6. Spells Subsystem Exception + Fuzz Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Spells_CoreTypesAndRegistries_ThrowOnInvalidInputs()
    {
        // SpellReagent uint.MaxValue overflow guard in checked cast (int)itemId
        Assert.Throws<OverflowException>(() => new SpellReagent(uint.MaxValue, 1));

        // SpellInfo Reagents (> 8 or null) & Effects (> 3 or null)
        Assert.Throws<ArgumentNullException>(() => new SpellInfo { Id = 1, Reagents = null! });
        Assert.Throws<ArgumentException>(() =>
            new SpellInfo { Id = 1, Reagents = new SpellReagent[9] });
        Assert.Throws<ArgumentNullException>(() => new SpellInfo { Id = 1, Effects = null! });
        Assert.Throws<ArgumentException>(() =>
            new SpellInfo { Id = 1, Effects = new SpellEffectInfo[4] });

        // SpellMath null checks
        var effectInfo = new SpellEffectInfo { Effect = SpellEffectName.Heal, BasePoints = 10 };
        var spellInfo = new SpellInfo { Id = 100, Name = "Heal", Effects = [effectInfo] };
        Assert.Throws<ArgumentNullException>(() => SpellMath.CalculateEffectValue(null!, effectInfo, 1, Random.Shared));
        Assert.Throws<ArgumentNullException>(() => SpellMath.CalculateEffectValue(spellInfo, null!, 1, Random.Shared));
        Assert.Throws<ArgumentNullException>(() => SpellMath.CalculateEffectValue(spellInfo, effectInfo, 1, null!));

        // SpellStore null checks
        Assert.Throws<ArgumentNullException>(() => new SpellStore(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => new SpellStore([], null!, []));
        Assert.Throws<ArgumentNullException>(() => new SpellStore([], [], null!));

        // ResistOutcomeTable validation
        Assert.Throws<ArgumentNullException>(() => ResistOutcomeTable.Load(null!));
        Assert.Throws<ArgumentException>(() => ResistOutcomeTable.Load(string.Empty));
        Assert.Throws<ArgumentNullException>(() => ResistOutcomeTable.Parse(null!));
        Assert.Throws<InvalidDataException>(() => ResistOutcomeTable.Parse(["10,20,30"]));
        Assert.Throws<InvalidDataException>(() => ResistOutcomeTable.Parse(["0,not_num,20,20,20,40"]));
        Assert.Throws<InvalidDataException>(() => ResistOutcomeTable.Parse(["0,10,10,10,10,10"]));
        Assert.Throws<InvalidDataException>(() => ResistOutcomeTable.Parse(["0,100,0,0,0,0"]));

        // SpellCastTargets wire validation
        Assert.Throws<ArgumentNullException>(() => SpellCastTargets.ForSelf().Write(null!));
        var truncatedTargetsWriter = new PacketWriter(4);
        truncatedTargetsWriter.WriteUInt16((ushort)SpellCastTargetFlags.Unit);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var reader = new PacketReader(truncatedTargetsWriter.AsSpan());
            _ = SpellCastTargets.Read(ref reader);
        });

        // SpellModEngine & SpellModMath
        using var spellKit = new SpellTestKit();
        var (player, _) = spellKit.AddPlayer(1);
        var modEngine = new SpellModEngine();
        Assert.Throws<ArgumentNullException>(() => modEngine.Apply(null!, spellInfo, SpellModOp.Damage, 10f));
        Assert.Throws<ArgumentNullException>(() => modEngine.Apply(player, null!, SpellModOp.Damage, 10f));
        Assert.Throws<ArgumentNullException>(() => modEngine.CreateScope(null!, spellInfo));
        Assert.Throws<ArgumentNullException>(() => modEngine.CreateScope(player, null!));
        Assert.Throws<ArgumentNullException>(() => modEngine.Begin(null!));
        Assert.Throws<ArgumentNullException>(() => modEngine.Seal(null!));
        Assert.Throws<ArgumentNullException>(() => modEngine.Restore(null!));
        Assert.Throws<ArgumentNullException>(() => modEngine.ClassMask(null!, 0));
        Assert.Throws<ArgumentNullException>(() => modEngine.ModsOf(null!, SpellModOp.Damage));
        Assert.Throws<ArgumentNullException>(() => modEngine.Add(null!, null!));
        Assert.Throws<ArgumentNullException>(() => modEngine.Remove(null!, null!));
        Assert.Throws<ArgumentNullException>(() => SpellModMath.Evaluate(null!, spellInfo, SpellModOp.Damage, 10f));
        Assert.Throws<ArgumentNullException>(() => SpellModMath.Evaluate([], null!, SpellModOp.Damage, 10f));

        // ScriptEffectRegistry, SpellScriptAttribute, SpellScriptRegistry, SpellScriptDispatcher
        Assert.Throws<ArgumentNullException>(() => ScriptEffectRegistry.For(null!));
        ScriptEffectRegistry scriptFx = ScriptEffectRegistry.For(spellKit.System);
        Assert.Throws<ArgumentNullException>(() => scriptFx.Add(1u, null!));
        scriptFx.Add(9991u, _ => { });
        Assert.Throws<InvalidOperationException>(() => scriptFx.Add(9991u, _ => { }));

        Assert.Throws<ArgumentNullException>(() => new SpellScriptAttribute(null!));
        Assert.Throws<ArgumentNullException>(() => new SpellScriptRegistry(null!));
        Assert.Throws<ArgumentNullException>(() => SpellScriptRegistry.Discover((Assembly)null!));
        Assert.Throws<ArgumentNullException>(() => SpellScriptRegistry.Discover((IEnumerable<Assembly>)null!));
        Assert.Throws<ArgumentNullException>(() => SpellScriptRegistry.CreateScripts(null!));

        var regA = new SpellScriptRegistry([]);
        var regB = new SpellScriptRegistry([]);
        Assert.Throws<ArgumentNullException>(() => regA.ValidateSwap(null!, []));
        Assert.Throws<ArgumentNullException>(() => regA.ValidateSwap([], null!));
        Assert.Throws<ArgumentNullException>(() => regA.CommitSwap(null!, []));
        Assert.Throws<ArgumentNullException>(() => regA.CommitSwap([], null!));
        Assert.Throws<ArgumentNullException>(() => regA.Restore(null!));
        Assert.Throws<ArgumentException>(() => regA.Restore(regB.Save()));

        Assert.Throws<ArgumentNullException>(() => SpellScriptDispatcher.Install(null!, regA));
        Assert.Throws<ArgumentNullException>(() => SpellScriptDispatcher.Install(spellKit.System, null!));

        // SpellSystem registration and null guards
        SpellSystem spells = spellKit.System;
        Assert.Throws<ArgumentOutOfRangeException>(() => spells.RegisterSpellAreaRadiusOverride(0u, 0, 5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => spells.RegisterSpellAreaRadiusOverride(1u, -1, 5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => spells.RegisterSpellAreaRadiusOverride(1u, 3, 5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => spells.RegisterSpellAreaRadiusOverride(1u, 0, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => spells.RegisterSpellAreaRadiusOverride(1u, 0, float.NaN));
        spells.RegisterSpellAreaRadiusOverride(7771u, 0, 10f);
        Assert.Throws<InvalidOperationException>(() => spells.RegisterSpellAreaRadiusOverride(7771u, 0, 12f));

        Assert.Throws<ArgumentOutOfRangeException>(() => spells.RegisterSpellMaxTargetsOverride(0u, 5u));
        Assert.Throws<ArgumentOutOfRangeException>(() => spells.RegisterSpellMaxTargetsOverride(1u, 0u));
        spells.RegisterSpellMaxTargetsOverride(7772u, 3u);
        Assert.Throws<InvalidOperationException>(() => spells.RegisterSpellMaxTargetsOverride(7772u, 4u));

        Assert.Throws<ArgumentNullException>(() => spells.RegisterAura(AuraType.None, null!));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterDummyAuraHandler(1u, null!));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterPeriodicDummyAura(1u, 1000u, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => spells.RegisterPeriodicDummyAura(1u, 0u, (_, _, _) => { }));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterPeriodicTriggerScript(1u, null!));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterEffectCheck(SpellEffectName.None, null!));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterEffect(SpellEffectName.None, null!));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterCastCheck(null!));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterObserver(null!));
        Assert.Throws<ArgumentNullException>(() => spells.UnregisterObserver(null!));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterValueModifier(null!));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterChainRangeProvider(null!));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterTargetSelector(SpellImplicitTarget.None, null!, false));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterSpellTargetSelector(1u, SpellImplicitTarget.None, null!));
        Assert.Throws<ArgumentNullException>(() => spells.RegisterSpellTargetFilter(1u, null!));

        // Stances / Warlock / PetTargets guards
        Assert.Throws<ArgumentNullException>(() => FormQueries.GetForm(null!));
        Assert.Throws<ArgumentNullException>(() => new PassiveFormCastCheck(null!));
        Assert.Throws<ArgumentNullException>(() => PassiveFormCastCheck.IsNeedCastPassiveLikeSpellAtLearn(null!, 0, null));
        Assert.Throws<ArgumentNullException>(() => ShapeshiftService.GetForm(null!));
        Assert.Throws<ArgumentNullException>(() => ShapeshiftService.RefreshShapeshiftWeaponBoosts(null!, ArcaneCore.Game.Spells.ShapeshiftForm.None));
        Assert.Throws<ArgumentNullException>(() => TransformScale.GetNative(null!));
        Assert.Throws<ArgumentNullException>(() => TransformScale.Set(null!, 1f));
        Assert.Throws<ArgumentNullException>(() => TransformScale.Reset(null!));
        Assert.Throws<ArgumentNullException>(() => PetTargets.Install(null!));
        Assert.Throws<ArgumentNullException>(() => PetTargets.PetOf(null!));
        Assert.Throws<ArgumentNullException>(() => CreateHealthstoneScript.ItemFor(null!, player, 6201u));
        Assert.Throws<ArgumentNullException>(() => CreateHealthstoneScript.ItemFor(spells, null!, 6201u));
        Assert.Throws<ArgumentNullException>(() => SoulShardRules.UseTapSource(null!, null));
        Assert.Throws<ArgumentNullException>(() => SoulShardRules.IsHonorOrXpTarget(null!, player));
        Assert.Throws<ArgumentNullException>(() => SoulShardRules.IsHonorOrXpTarget(player, null!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void SpellCastTargets_And_SpellEffectValue_FuzzRoundTrip(int seed)
    {
        var rng = new Random(seed);

        for (int i = 0; i < 150; i++)
        {
            SpellCastTargetFlags mask = SpellCastTargetFlags.Self;
            ObjectGuid unitGuid = ObjectGuid.Empty;
            ObjectGuid goGuid = ObjectGuid.Empty;
            ObjectGuid itemGuid = ObjectGuid.Empty;
            (float X, float Y, float Z) src = default;
            (float X, float Y, float Z) dst = default;
            string? text = null;

            if (rng.Next(2) == 0)
            {
                mask |= SpellCastTargetFlags.Unit;
                unitGuid = ObjectGuid.WithEntry(HighGuid.Unit, (uint)rng.Next(1, 5000), (uint)rng.Next(1, 50000));
            }
            else if (rng.Next(2) == 0)
            {
                mask |= SpellCastTargetFlags.GameObject;
                goGuid = ObjectGuid.WithEntry(HighGuid.GameObject, (uint)rng.Next(1, 5000), (uint)rng.Next(1, 50000));
            }

            if (rng.Next(2) == 0)
            {
                mask |= SpellCastTargetFlags.Item;
                itemGuid = ObjectGuid.Item((uint)rng.Next(1, 50000));
            }
            if (rng.Next(2) == 0)
            {
                mask |= SpellCastTargetFlags.SourceLocation;
                src = ((float)rng.NextDouble() * 1000f, (float)rng.NextDouble() * 1000f, (float)rng.NextDouble() * 100f);
            }
            if (rng.Next(2) == 0)
            {
                mask |= SpellCastTargetFlags.DestLocation;
                dst = ((float)rng.NextDouble() * 1000f, (float)rng.NextDouble() * 1000f, (float)rng.NextDouble() * 100f);
            }
            if (rng.Next(2) == 0)
            {
                mask |= SpellCastTargetFlags.String;
                text = $"Target_{seed}_{i}";
            }

            var targets = new SpellCastTargets
            {
                Mask = mask,
                Unit = unitGuid,
                GameObject = goGuid,
                Item = itemGuid,
                Source = src,
                Dest = dst,
                Text = text,
            };

            var writer = new PacketWriter(64);
            targets.Write(writer);

            var reader = new PacketReader(writer.AsSpan());
            SpellCastTargets decoded = SpellCastTargets.Read(ref reader);
            Assert.Equal(0, reader.Remaining);
            Assert.Equal(mask, decoded.Mask);
            Assert.Equal(unitGuid, decoded.Unit);
            Assert.Equal(goGuid, decoded.GameObject);
            Assert.Equal(itemGuid, decoded.Item);
            Assert.Equal(src, decoded.Source);
            Assert.Equal(dst, decoded.Dest);
            Assert.Equal(text, decoded.Text);

            // SpellMath.CalculateEffectValue bounds check
            int basePoints = rng.Next(-200, 500);
            int dieSides = rng.Next(0, 50);
            int baseDice = rng.Next(0, 5);
            var effect = new SpellEffectInfo
            {
                Effect = SpellEffectName.SchoolDamage,
                BasePoints = basePoints,
                DieSides = dieSides,
                BaseDice = baseDice,
            };
            var spell = new SpellInfo { Id = 1, Effects = [effect] };
            int rolled = SpellMath.CalculateEffectValue(spell, effect, 60, rng);
            int minExpected = dieSides is 0 or 1
                ? basePoints + baseDice
                : basePoints + Math.Min(baseDice, dieSides);
            int maxExpected = dieSides is 0 or 1
                ? basePoints + baseDice
                : basePoints + Math.Max(baseDice, dieSides);
            Assert.InRange(rolled, minExpected, maxExpected);
        }
    }

    // -------------------------------------------------------------------------
    // 7. Conditions, Ranged, Stealth, Death, Totems Exception + Fuzz Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Conditions_Ranged_Stealth_Death_Totems_ThrowOnNullArguments()
    {
        var (world, map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime worldScope = world;
        var session1 = new FakeSession(1);
        var session2 = new FakeSession(2);
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, session1);
        Player other = CombatTestKit.AddPlayer(world, 2, 5, 0, session2);
        using var spellKit = new SpellTestKit();
        SpellSystem spells = spellKit.System;

        // Conditions
        Assert.Throws<ArgumentNullException>(() => ConditionTable.Build(null!));
        Assert.Throws<ArgumentNullException>(() => new ConditionEvaluator(null!, new ConditionContext()));
        Assert.Throws<ArgumentNullException>(() => new ConditionEvaluator(ConditionTable.Empty, null!));
        var evaluator = new ConditionEvaluator(ConditionTable.Empty, new ConditionContext());
        Assert.Throws<ArgumentNullException>(() => evaluator.IsSatisfied(1u, null!, null));
        Assert.Throws<ArgumentNullException>(() => evaluator.EvaluateOnMap(1u, null!));

        // Ranged
        Assert.Throws<ArgumentNullException>(() => PlayerAmmo.CurrentAmmoId(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerAmmo.RangedWeapon(null!, true));
        Assert.Throws<ArgumentNullException>(() => PlayerAmmo.CanUseAmmo(null!, 1u));
        Assert.Throws<ArgumentNullException>(() => PlayerAmmo.SetAmmo(null!, 1u));
        Assert.Throws<ArgumentNullException>(() => PlayerAmmo.RemoveAmmo(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerAmmo.CurrentDps(null!));
        Assert.Throws<ArgumentNullException>(() => new QuiverHaste(null!));
        Assert.Throws<ArgumentNullException>(() => new QuiverHaste(spells).Attach(null!));
        Assert.Throws<ArgumentNullException>(() => RangeLeeway.XzFlagBasedSpeed(null!));
        Assert.Throws<ArgumentNullException>(() => RangeLeeway.Bonus(null!, other));
        Assert.Throws<ArgumentNullException>(() => RangedHandlers.Register(null!));
        Assert.Throws<ArgumentNullException>(() => RangedSpellFacts.IsRanged(null!));
        Assert.Throws<ArgumentNullException>(() => RangedSpellFacts.IsAutoRepeatRanged(null!));
        Assert.Throws<ArgumentNullException>(() => RangedSpellFacts.NeedsExoticAmmo(null!));
        Assert.Throws<ArgumentNullException>(() => RangedSpellFacts.DoesNotResetCombatTimers(null!));
        Assert.Throws<ArgumentNullException>(() => RangedSpellFacts.UsesRangedWeapon(null!));
        Assert.Throws<ArgumentNullException>(() => RangedSpellFacts.IsNormalRangedAttack(null!));
        Assert.Throws<ArgumentNullException>(() => RangedSpellFacts.HasWeaponDamageEffect(null!));
        var objRegistry = new SpellObjectRegistry();
        Assert.Throws<ArgumentNullException>(() => objRegistry.Find(null!));
        Assert.Throws<ArgumentNullException>(() => objRegistry.OwnedBy(null!));
        Assert.Throws<ArgumentNullException>(() => objRegistry.InSlot(null!, 0));
        Assert.Throws<ArgumentNullException>(() => objRegistry.IsCreatedBySpell(null!, 1u));
        Assert.Throws<ArgumentNullException>(() => TrapRules.TriggerRadius(null!, TrapRadiusSource.Vmangos));
        Assert.Throws<ArgumentNullException>(() => TrapRules.OwnerMayTrigger(null!, other));
        Assert.Throws<ArgumentNullException>(() => TrapRules.OwnerMayTrigger(player, null!));

        // Stealth
        var stealthReg = new StealthRegistry();
        Assert.Throws<ArgumentNullException>(() => Invisibility.Register(null!));
        Assert.Throws<ArgumentNullException>(() => InvisibilityAuras.InvisibilityHandler(null!));
        Assert.Throws<ArgumentNullException>(() => InvisibilityAuras.CanDetect(null!, player, other));
        Assert.Throws<ArgumentNullException>(() => StealthAuras.Handler(null!));
        Assert.Throws<ArgumentNullException>(() => StealthDetection.LevelForTarget(null!, other));
        Assert.Throws<ArgumentNullException>(() => StealthDetection.CanDetectStealthOf(null!, player, other, 5f));
        Assert.Throws<ArgumentNullException>(() => StealthDetection.CanDetectStealthOf(spells, null!, other, 5f));
        Assert.Throws<ArgumentNullException>(() => StealthDetection.CanDetectStealthOf(spells, player, null!, 5f));
        Assert.Throws<ArgumentNullException>(() => new StealthDetectionUpdater(null!, _ => false));
        Assert.Throws<ArgumentNullException>(() => new StealthDetectionUpdater(stealthReg, null!));
        Assert.Throws<ArgumentNullException>(() => new StealthDetectionUpdater(stealthReg, _ => false).Update(null!, 100u));
        Assert.Throws<ArgumentNullException>(() => new StealthServices(null!, stealthReg, StealthOptions.Default));
        Assert.Throws<ArgumentNullException>(() => new StealthServices(spells, null!, StealthOptions.Default));
        Assert.Throws<ArgumentNullException>(() => new StealthServices(spells, stealthReg, null!));
        var stealthSvc = new StealthServices(spells, stealthReg, StealthOptions.Default);
        Assert.Throws<ArgumentNullException>(() => StealthServices.Find(null!));
        Assert.Throws<ArgumentNullException>(() => StealthServices.Install(null!, stealthSvc));
        Assert.Throws<ArgumentNullException>(() => StealthServices.Install(map, null!));
        Assert.Throws<ArgumentNullException>(() => stealthSvc.CanCreatureSee(null!, player, out _));
        Assert.Throws<ArgumentNullException>(() => stealthSvc.CanCreatureSee(other, null!, out _));
        Assert.Throws<ArgumentNullException>(() => stealthReg.VisibilityOf(null!));
        Assert.Throws<ArgumentNullException>(() => stealthReg.SetVisibility(null!, StealthVisibility.On));
        Assert.Throws<ArgumentNullException>(() => stealthReg.Prune(null!));
        Assert.Throws<ArgumentNullException>(() => new StealthVisibilityRule(null!, stealthReg));
        Assert.Throws<ArgumentNullException>(() => new StealthVisibilityRule(spells, null!));

        // Death
        Assert.Throws<ArgumentNullException>(() => new DeathHooks(null!, DeathClock.System));
        Assert.Throws<ArgumentNullException>(() => new DeathHooks(new DeathOptions(), null!));
        Assert.Throws<ArgumentNullException>(() => DeathHooks.Register(null!, DeathHooks.Default));
        Assert.Throws<ArgumentNullException>(() => DeathHooks.Register(world, null!));
        Assert.Throws<ArgumentNullException>(() => DeathHooks.TryRegister(null!, DeathHooks.Default));
        Assert.Throws<ArgumentNullException>(() => DeathHooks.TryRegister(world, null!));
        Assert.Throws<ArgumentNullException>(() => DeathHooks.For(null!));
        Assert.Throws<ArgumentNullException>(() => new DeathOutcomeWatcher(null!));
        Assert.Throws<ArgumentNullException>(() => DeathSeams.Of(null!));
        Assert.Throws<ArgumentNullException>(() => DeathSeams.Find(null!));
        DeathSeams seams = DeathSeams.Of(world);
        Assert.Throws<ArgumentNullException>(() => seams.TryRegisterGhostForm(null!));
        Assert.Throws<ArgumentNullException>(() => seams.TryRegisterGraveyards(null!));
        Assert.Throws<ArgumentNullException>(() => seams.TryRegisterBattlegrounds(null!));
        var ghostForm = new GhostForm(world, () => spells);
        Assert.Throws<ArgumentNullException>(() => ghostForm.Apply(null!));
        Assert.Throws<ArgumentNullException>(() => ghostForm.Remove(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerLife.Capture(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerLife.ApplyVitals(null!, null!));
        Assert.Throws<ArgumentNullException>(() => PlayerLife.HasNoBodyToReturnTo(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerLife.IsGhostWithBody(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerLife.ApplyGhostState(null!));
        Assert.Throws<ArgumentNullException>(() => PlayerLife.ReapplyAfterAuras(null!, null!));
        Assert.Throws<ArgumentNullException>(() => PlayerLife.ApplyDeathWindow(null!, null!, 0L));
        Assert.Throws<ArgumentNullException>(() => ResurrectionPackets.BuildRequest(ObjectGuid.Empty, null!, false, false));
        Assert.Throws<ArgumentNullException>(() => ResurrectionPackets.ReadResponse(null!));
        Assert.Throws<ArgumentNullException>(() => ResurrectionRequests.Get(null!));
        Assert.Throws<ArgumentNullException>(() => ResurrectionRequests.Set(null!, null!));
        Assert.Throws<ArgumentNullException>(() => ResurrectionRequests.Clear(null!));
        var resSvc = new ResurrectionService(world, () => null);
        Assert.Throws<ArgumentNullException>(() => resSvc.Request(null!, other, "Caster", 100u, 100u, false, false));
        Assert.Throws<ArgumentNullException>(() => resSvc.Request(player, null!, "Caster", 100u, 100u, false, false));
        Assert.Throws<ArgumentNullException>(() => resSvc.Respond(null!, ObjectGuid.Empty, true));
        Assert.Throws<ArgumentNullException>(() => resSvc.ResurrectUsingRequestData(null!));
        Assert.Throws<ArgumentNullException>(() => SelfResurrection.SelectResurrectionSpellId(null!, player));
        Assert.Throws<ArgumentNullException>(() => SelfResurrection.SelectResurrectionSpellId(spells, null!));
        Assert.Throws<ArgumentNullException>(() => SelfResurrection.OnPlayerDied(null!, player));
        Assert.Throws<ArgumentNullException>(() => SelfResurrection.OnPlayerDied(spells, null!));
        Assert.Throws<ArgumentNullException>(() => SelfResurrection.Use(null!, player));
        Assert.Throws<ArgumentNullException>(() => SelfResurrection.Use(spells, null!));
        Assert.Throws<ArgumentNullException>(() => CorpseQuery.Build(null!, world));
        Assert.Throws<ArgumentNullException>(() => CorpseQuery.Build(player, null!));
        Assert.Throws<ArgumentNullException>(() => GhostEntryRules.Resolve(null!, null!, null!, null!));
        Assert.Throws<ArgumentNullException>(() => GhostVisibilityRule.IsGhostCreature(null!));

        // Totems
        var groupResolver = new OwnerAwareGroupResolver(NoGroupResolver.Instance);
        Assert.Throws<ArgumentNullException>(() => groupResolver.GetGroupMembers(null!, false));
        Assert.Throws<ArgumentNullException>(() => new TotemSystem(null!, _ => null, null!, null!));
        using var petKit = new PetTestKit();
        Assert.Throws<ArgumentNullException>(() => petKit.Totems.EnsureUpdater(null!));
        Assert.Throws<ArgumentNullException>(() => petKit.Totems.GetTotem(null!, TotemSlot.Fire));
        Assert.Throws<ArgumentNullException>(() => petKit.Totems.UnsummonAll(null!));
        Assert.Throws<ArgumentNullException>(() => petKit.Totems.Unsummon(null!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void StealthDetection_Evaluate_FuzzMonotonicityInvariants(int seed)
    {
        var rng = new Random(seed);

        for (int i = 0; i < 200; i++)
        {
            int detectorLevel = rng.Next(1, 70);
            int targetLevel = rng.Next(1, 70);
            int stealthMod = rng.Next(0, 400);
            int detectMod = rng.Next(-100, 400);
            float distNear = (float)rng.NextDouble() * 15f;
            float distFar = distNear + ((float)rng.NextDouble() * 20f);
            bool hasInArc = rng.Next(2) == 0;
            bool isPlayer = rng.Next(2) == 0;

            var nearInput = new StealthDetectionInput(
                Distance: distNear,
                DetectorStunned: false,
                DetectorIsPlayer: isPlayer,
                TargetIsPlayer: true,
                DetectorLevel: detectorLevel,
                TargetLevel: targetLevel,
                TargetStealthTotal: stealthMod,
                TargetStealthLevelMod: 0,
                DetectorDetectMod: detectMod,
                TargetInDetectorArc: hasInArc,
                TargetStrength: 50f);

            var farInput = nearInput with { Distance = distFar };

            StealthDetectionResult nearRes = StealthDetection.Evaluate(in nearInput);
            StealthDetectionResult farRes = StealthDetection.Evaluate(in farInput);

            float maxDetectRange = isPlayer
                ? StealthOptions.Default.MaxPlayerDetectRange
                : StealthOptions.Default.MaxCreatureDetectRange;

            if (distNear < StealthDetection.CollisionDistance)
            {
                Assert.True(nearRes.Detected);
                Assert.False(nearRes.Alert);
                Assert.Equal(StealthDetection.CollisionDistance, nearRes.VisibleDistance);
            }
            else if (distFar <= maxDetectRange)
            {
                Assert.Equal(nearRes.VisibleDistance, farRes.VisibleDistance);
            }

            if (distFar > maxDetectRange)
            {
                Assert.False(farRes.Detected);
                Assert.False(farRes.Alert);
                Assert.Equal(0f, farRes.VisibleDistance);
            }

            // Monotonicity: if detected at far distance, must also be detected at nearer distance
            if (farRes.Detected)
            {
                Assert.True(nearRes.Detected);
            }
        }
    }
}