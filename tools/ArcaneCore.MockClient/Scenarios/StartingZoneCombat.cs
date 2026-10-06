using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Serialization;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;

namespace ArcaneCore.MockClient.Scenarios;

/// <summary>Optional, finite combat extension for a privately configured build-5875 Kobold spawn.</summary>
internal static class StartingZoneCombat
{
    private const uint KoboldEntry = 6;

    internal sealed record CombatEvidence(ulong Target, uint StartingHealth, uint FinalHealth,
        uint QuestCount, uint RequiredCount, int Frames, int Bytes,
        float PlayerX, float PlayerY, float PlayerZ,
        uint ClientTime, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HeroicEvidence? Heroic = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ulong>? ActiveAttackers = null);

    internal sealed record HeroicEvidence(uint RageBeforeQueue, uint RageAfterQueue, uint RageAfterExecution,
        bool QueueStarted, bool ExecutionSucceeded, uint MeleeSpellId, uint School, uint Damage,
        uint TargetHealthBefore, uint TargetHealthAfter, int Frames, int Bytes);

    internal static async Task<CombatEvidence> RunAsync(ScenarioConnection connection, ulong character, uint quest, uint spawn,
        (float X, float Y, float Z) approach, float startX, float startY, float startZ, uint clientTime,
        CancellationToken cancellationToken, bool heroic = false, uint expectedQuestCount = 1,
        CombatBudget? sharedBudget = null, bool waitForCombatExit = true)
    {
        if (expectedQuestCount is 0 or > 10) throw new ArgumentOutOfRangeException(nameof(expectedQuestCount));
        ulong target = ObjectGuid.WithEntry(HighGuid.Unit, KoboldEntry, spawn).Value;
        if (!float.IsFinite(approach.X) || !float.IsFinite(approach.Y) || !float.IsFinite(approach.Z)
            || Distance(startX, startY, startZ, approach.X, approach.Y, approach.Z) > 250f)
        {
            throw new MockProtocolException("observed Kobold position is outside the configured 250-yard combat bound");
        }

        CombatBudget budget = sharedBudget ?? new CombatBudget();
        AttackerState? lastIncoming = null;
        var incomingAttackers = new List<ulong>(4);
        ushort? lastSwingError = null;
        int diagnosticFrames = 0;
        void CheckPlayer()
        {
            IReadOnlyDictionary<int, uint> playerFields = connection.FieldsOf(character);
            if (TryObservedPlayerDeath(playerFields, out _))
                throw new MockProtocolException($"player died during Kobold approach/combat: target={target}, "
                    + $"targetHealth={connection.FieldsOf(target).GetValueOrDefault(UpdateFields.UnitFieldHealth)}, "
                    + $"questCount={QuestProgress(playerFields, quest)}/{expectedQuestCount}, "
                    + $"lastIncoming={lastIncoming}, lastSwingError={lastSwingError}, frames={budget.Frames}");
        }
        budget.BeforeRead = CheckPlayer;
        budget.FrameObserved = frame =>
        {
            if (frame.Opcode == (ushort)WorldOpcode.SmsgAttackerstateupdate
                && TryReadAttackerState(frame.Payload, out AttackerState incoming) && incoming.Target == character)
            {
                lastIncoming = incoming;
                if (!incomingAttackers.Contains(incoming.Attacker)) incomingAttackers.Add(incoming.Attacker);
                if (diagnosticFrames++ < 16)
                    Console.WriteLine($"starting-zone incoming attacker={incoming.Attacker} target={incoming.Target} "
                        + $"damage={incoming.TotalDamage} spell={incoming.MeleeSpellId} school={incoming.School} "
                        + $"questCount={expectedQuestCount} frames={budget.Frames}");
            }
            if (frame.Opcode is (ushort)WorldOpcode.SmsgAttackswingNotinrange
                or (ushort)WorldOpcode.SmsgAttackswingBadfacing or (ushort)WorldOpcode.SmsgAttackswingDeadtarget
                or (ushort)WorldOpcode.SmsgAttackstop or (ushort)WorldOpcode.SmsgCancelCombat)
            {
                lastSwingError = frame.Opcode;
                if (diagnosticFrames++ < 16)
                    Console.WriteLine($"starting-zone swing-error opcode=0x{frame.Opcode:X4} target={target} "
                        + $"questCount={expectedQuestCount} frames={budget.Frames}");
            }
            CheckPlayer();
        };
        CheckPlayer();
        clientTime = await WalkAsync(connection, budget, startX, startY, startZ, approach.X, approach.Y, approach.Z, clientTime, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<int, uint> initial = connection.FieldsOf(target);
        for (int index = 0; (initial.GetValueOrDefault(UpdateFields.ObjectFieldEntry) != KoboldEntry
            || initial.GetValueOrDefault(UpdateFields.UnitFieldHealth) == 0 || !TryObservedPosition(connection, target, out _, out _, out _))
            && index < 64; index++)
        {
            _ = await budget.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
            initial = connection.FieldsOf(target);
        }
        if (initial.GetValueOrDefault(UpdateFields.ObjectFieldEntry) != KoboldEntry
            || initial.GetValueOrDefault(UpdateFields.UnitFieldHealth) == 0
            || !TryObservedPosition(connection, target, out _, out _, out _))
            throw new MockProtocolException("configured Kobold spawn was not observed with entry 6, positive health, and position");
        (float tx, float ty, float tz) = ObservedPosition(connection, target);
        if (Distance(startX, startY, startZ, tx, ty, tz) > 250f
            || Distance(approach.X, approach.Y, approach.Z, tx, ty, tz) > 250f)
            throw new MockProtocolException("observed Kobold position is outside the configured 250-yard combat bound");
        uint startingHealth = initial.GetValueOrDefault(UpdateFields.UnitFieldHealth);
        clientTime = await WalkAsync(connection, budget, approach.X, approach.Y, approach.Z, tx, ty, tz, clientTime, cancellationToken).ConfigureAwait(false);

        ThrowIfObservedPlayerDeath(connection.FieldsOf(character), target,
            connection.FieldsOf(target).GetValueOrDefault(UpdateFields.UnitFieldHealth), quest,
            expectedQuestCount, sawMatchingSwingDamage: false, sawHealthDecrease: false, sawMatchingSwing: false);

        await connection.SendAsync(WorldOpcode.CmsgAttackswing, ScenarioWire.Guid(target), cancellationToken).ConfigureAwait(false);
        HeroicEvidence? heroicEvidence = null;
        uint rageBeforeQueue = 0;
        uint rageAfterQueue = 0;
        bool queueStarted = false;
        bool queueRequested = false;
        uint targetHealthBeforeHeroic = 0;
        bool heroicExecution = false;
        bool heroicCastSuccess = false;
        AttackerState? heroicState = null;
        bool sawMatchingSwingDamage = false;
        bool sawHealthDecrease = false;
        bool sawDeath = false;
        bool sawMatchingSwing = false;
        MockQuestKill? observedKill = null;
        while (!IsCombatComplete(sawDeath, observedKill is not null,
            QuestProgress(connection.FieldsOf(character), quest), expectedQuestCount,
            sawMatchingSwingDamage, sawHealthDecrease, heroic, heroicExecution))
        {
            WorldFrame frame = await budget.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
            if (heroic && queueRequested && frame.Opcode == (ushort)WorldOpcode.SmsgCastResult && frame.Payload.Length >= 5
                && BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload) == 78)
            {
                if (frame.Payload[4] != 0 && queueRequested)
                {
                    byte reason = frame.Payload.Length > 5 ? frame.Payload[5] : (byte)0;
                    throw new MockProtocolException($"Heroic Strike execution was rejected with result 0x{reason:X2}");
                }

                if (frame.Payload[4] == 0)
                    heroicCastSuccess = true;
            }
            if (heroic && queueRequested && frame.Opcode == (ushort)WorldOpcode.SmsgSpellStart
                && IsSpellStart(frame.Payload, 78, character))
            {
                queueStarted = true;
                rageAfterQueue = connection.FieldsOf(character)
                    .GetValueOrDefault(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage);
            }
            if (frame.Opcode == (ushort)WorldOpcode.SmsgAttackerstateupdate)
            {
                if (!TryReadAttackerState(frame.Payload, out AttackerState state))
                    throw new MockProtocolException("malformed SMSG_ATTACKERSTATEUPDATE during Kobold combat");
                if (state.Attacker == character && state.Target == target)
                {
                    sawMatchingSwing = true;
                    if (state.TotalDamage > 0) sawMatchingSwingDamage = true;
                    if (heroic && queueRequested && state.MeleeSpellId == 78)
                        heroicState = state;
                }
            }
            IReadOnlyDictionary<int, uint> fields = connection.FieldsOf(target);
            uint health = fields.GetValueOrDefault(UpdateFields.UnitFieldHealth);
            if (health < startingHealth) sawHealthDecrease = true;
            if (health == 0 && fields.GetValueOrDefault(UpdateFields.ObjectFieldEntry) == KoboldEntry)
                sawDeath = true;

            IReadOnlyDictionary<int, uint> playerFields = connection.FieldsOf(character);
            ThrowIfObservedPlayerDeath(playerFields, target, health, quest, expectedQuestCount,
                sawMatchingSwingDamage, sawHealthDecrease, sawMatchingSwing);

            if (heroic && queueStarted && heroicState is { } observedHeroic
                && IsHeroicExecution(observedHeroic, heroicCastSuccess, targetHealthBeforeHeroic, health))
            {
                heroicExecution = true;
                heroicEvidence = new HeroicEvidence(rageBeforeQueue, rageAfterQueue,
                    connection.FieldsOf(character).GetValueOrDefault(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage),
                    queueStarted, true, observedHeroic.MeleeSpellId, observedHeroic.School, observedHeroic.TotalDamage,
                    targetHealthBeforeHeroic, health, budget.Frames, budget.Bytes);
            }
            if (heroic && !queueRequested && health == 0)
                throw new MockProtocolException("the observed Kobold died before natural rage reached Heroic Strike cost");

            if (frame.Opcode == (ushort)WorldOpcode.SmsgQuestupdateAddKill)
            {
                MockQuestKill kill = ScenarioWire.QuestKill(frame.Payload);
                if (!IsExpectedKillCredit(kill, target, quest, expectedQuestCount, 10))
                    throw new MockProtocolException("Kobold quest-kill packet did not match quest 7, entry 6, count 1, required 10, and the observed target GUID");
                observedKill = kill;
            }

            if (heroic && !queueRequested && !queueStarted && health > 0)
            {
                uint rage = connection.FieldsOf(character).GetValueOrDefault(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage);
                if (rage >= 150)
                {
                    rageBeforeQueue = rage;
                    targetHealthBeforeHeroic = health;
                    var packet = new PacketWriter(16);
                    packet.WriteUInt32(78);
                    SpellCastTargets.ForUnit(new ObjectGuid(target)).Write(packet);
                    await connection.SendAsync(WorldOpcode.CmsgCastSpell, packet.ToArray(), cancellationToken).ConfigureAwait(false);
                    queueRequested = true;
                }
            }
        }

        if (!sawMatchingSwing || !sawMatchingSwingDamage || !sawHealthDecrease)
            throw new MockProtocolException("Kobold combat lacked a matching attacker-state packet and positive damage proof");

        uint progress = QuestProgress(connection.FieldsOf(character), quest);
        MockQuestKill killCredit = observedKill ?? throw new MockProtocolException("Kobold death did not produce quest credit");
        bool expectedComplete = expectedQuestCount == 10;
        if (!IsExpectedKillCredit(killCredit, target, quest, expectedQuestCount, 10)
            || progress != expectedQuestCount
            || (expectedComplete ? !StartingZoneProbe.IsQuestComplete(connection.FieldsOf(character), quest)
                : !StartingZoneProbe.IsQuestIncomplete(connection.FieldsOf(character), quest)))
            throw new MockProtocolException($"Kobold death did not produce the exact quest-7 counter {expectedQuestCount}/10");

        if (waitForCombatExit)
        {
            await connection.SendAsync(WorldOpcode.CmsgAttackstop, [], cancellationToken).ConfigureAwait(false);
            await WaitForCombatExitAsync(connection, character, budget, cancellationToken).ConfigureAwait(false);
        }
        if (heroic && !heroicExecution)
            throw new MockProtocolException("natural-rage Heroic Strike execution was not observed");
        uint finalHealth = connection.FieldsOf(target).GetValueOrDefault(UpdateFields.UnitFieldHealth);
        IReadOnlyList<ulong>? activeEvidence = waitForCombatExit ? null : incomingAttackers.AsReadOnly();
        return new CombatEvidence(target, startingHealth, finalHealth, progress, killCredit.RequiredCount,
            budget.Frames, budget.Bytes, tx, ty, tz, clientTime, heroicEvidence, activeEvidence);
    }

    internal static bool IsSpellStart(ReadOnlySpan<byte> body, uint spell, ulong caster)
    {
        try
        {
            var reader = new PacketReader(body);
            _ = reader.ReadPackedGuid();
            return reader.ReadPackedGuid() == caster && reader.ReadUInt32() == spell;
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    internal static bool IsHeroicExecution(AttackerState state, bool castSuccess, uint healthBefore, uint healthAfter)
        => castSuccess && state.MeleeSpellId == 78 && state.School == 1 && state.TotalDamage > 0
            && healthAfter < healthBefore;

    internal static ulong? SelectActiveEligibleAttacker(IEnumerable<ulong> incomingAttackers, ulong character,
        IReadOnlySet<ulong> allowedTargets, Func<ulong, bool> isLiveEntry6)
    {
        foreach (ulong attacker in incomingAttackers)
        {
            if (attacker != 0 && attacker != character && allowedTargets.Contains(attacker) && isLiveEntry6(attacker))
                return attacker;
        }

        return null;
    }

    internal static bool IsActiveEntrySixTarget(IReadOnlyDictionary<int, uint> fields, ulong character)
    {
        if (fields.GetValueOrDefault(UpdateFields.ObjectFieldEntry) != KoboldEntry
            || fields.GetValueOrDefault(UpdateFields.UnitFieldHealth) == 0)
            return false;
        if (!fields.TryGetValue(UpdateFields.UnitFieldTarget, out uint targetLow)
            || targetLow != (uint)character)
            return false;
        if (!fields.TryGetValue(UpdateFields.UnitFieldTarget + 1, out uint targetHigh)
            || targetHigh != (uint)(character >> 32))
            return false;
        if (!fields.TryGetValue(UpdateFields.UnitFieldFlags, out uint flags)
            || (flags & (uint)UnitFlags.InCombat) == 0)
            return false;
        return true;
    }

    internal static async Task WaitForCombatExitAsync(ScenarioConnection connection, ulong character,
        CombatBudget budget, CancellationToken cancellationToken)
    {
        while (true)
        {
            IReadOnlyDictionary<int, uint> fields = connection.FieldsOf(character);
            if (fields.TryGetValue(UpdateFields.UnitFieldFlags, out uint flags)
                && (flags & (uint)UnitFlags.InCombat) == 0)
                return;

            _ = await budget.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<uint> WalkAsync(ScenarioConnection connection, CombatBudget budget, float sx, float sy, float sz,
        float tx, float ty, float tz, uint clientTime, CancellationToken cancellationToken)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Distance(sx, sy, sz, tx, ty, tz) / 7f));
        for (int index = 1; index <= steps; index++)
        {
            float t = index / (float)steps;
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            clientTime = checked(clientTime + 1000);
            float x = sx + ((tx - sx) * t), y = sy + ((ty - sy) * t), z = sz + ((tz - sz) * t);
            float orientation = MathF.Atan2(ty - sy, tx - sx);
            await connection.SendAsync(WorldOpcode.MsgMoveStop, StartingZoneProbe.Movement(x, y, z, orientation, clientTime), cancellationToken).ConfigureAwait(false);
            await budget.DrainQuietAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        return clientTime;
    }

    private static (float X, float Y, float Z) ObservedPosition(ScenarioConnection connection, ulong target)
    {
        // ScenarioConnection owns canonical movement tracking.
        var position = connection.PositionOf(target)
            ?? throw new MockProtocolException("configured Kobold position was not present in canonical movement tracking");
        return (position.X, position.Y, position.Z);
    }

    private static bool TryObservedPosition(ScenarioConnection connection, ulong target, out float x, out float y, out float z)
    {
        var position = connection.PositionOf(target);
        if (position is not null)
        {
            x = position.X;
            y = position.Y;
            z = position.Z;
            return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
        }

        (x, y, z) = (0f, 0f, 0f);
        return false;
    }

    private static float Distance(float ax, float ay, float az, float bx, float by, float bz)
        => MathF.Sqrt(MathF.Pow(ax - bx, 2) + MathF.Pow(ay - by, 2) + MathF.Pow(az - bz, 2));

    internal static bool IsExpectedKillCredit(MockQuestKill kill, ulong target, uint quest,
        uint expectedCount, uint requiredCount)
        => kill.QuestId == quest && kill.CreatureId == KoboldEntry && kill.Guid != 0 && kill.Guid == target
            && kill.Count == expectedCount && kill.RequiredCount == requiredCount;

    internal static bool IsCombatComplete(bool sawTargetDeath, bool sawQuestCredit, uint questProgress,
        uint expectedQuestCount, bool sawMatchingSwingDamage, bool sawHealthDecrease,
        bool heroic, bool heroicExecution)
        => sawTargetDeath && sawQuestCredit && questProgress == expectedQuestCount
            && sawMatchingSwingDamage && sawHealthDecrease && (!heroic || heroicExecution);

    internal static bool TryObservedPlayerDeath(IReadOnlyDictionary<int, uint> fields, out uint health)
    {
        if (fields.TryGetValue(UpdateFields.UnitFieldHealth, out health))
            return health == 0;

        health = 0;
        return false;
    }

    internal static void ThrowIfObservedPlayerDeath(IReadOnlyDictionary<int, uint> playerFields,
        ulong target, uint targetHealth, uint quest, uint expectedQuestCount,
        bool sawMatchingSwingDamage, bool sawHealthDecrease, bool sawMatchingSwing)
    {
        if (!TryObservedPlayerDeath(playerFields, out uint playerHealth)) return;

        uint progress = QuestProgress(playerFields, quest);
        throw new MockProtocolException($"player died during Kobold combat: playerHealth={playerHealth}, "
            + $"target={target}, targetHealth={targetHealth}, questCount={progress}/{expectedQuestCount}, "
            + $"matchingSwingDamage={sawMatchingSwingDamage}, healthDecrease={sawHealthDecrease}, "
            + $"matchingSwing={sawMatchingSwing}");
    }

    internal static uint QuestProgress(IReadOnlyDictionary<int, uint> fields, uint quest)
    {
        for (int slot = 0; slot < ArcaneCore.Game.Quests.QuestConstants.MaxQuestLogSize; slot++)
        {
            int start = UpdateFields.PlayerQuestLog11 + slot * ArcaneCore.Game.Quests.QuestConstants.FieldsPerSlot;
            if (fields.GetValueOrDefault(start) == quest)
                return fields.GetValueOrDefault(start + 1) & 0x3Fu;
        }

        return 0;
    }

    internal static bool TryReadAttackerState(ReadOnlySpan<byte> body, out AttackerState state)
    {
        try
        {
            var reader = new PacketReader(body);
            _ = reader.ReadUInt32();
            ulong attacker = reader.ReadPackedGuid();
            ulong target = reader.ReadPackedGuid();
            if (attacker == 0 || target == 0) throw new MockProtocolException("attacker-state contained an empty GUID");
            uint damage = reader.ReadUInt32();
            byte subCount = reader.ReadByte();
            if (subCount > 16) throw new MockProtocolException("attacker-state subdamage count exceeded bound");
            uint firstSchool = 0;
            float firstDisplay = 0;
            for (int index = 0; index < subCount; index++)
            {
                uint componentSchool = reader.ReadUInt32();
                float schoolDamage = reader.ReadSingle();
                if (!float.IsFinite(schoolDamage)) throw new MockProtocolException("attacker-state component was non-finite");
                if (index == 0)
                {
                    firstSchool = componentSchool;
                    firstDisplay = schoolDamage;
                }
                _ = reader.ReadUInt32(); _ = reader.ReadUInt32(); _ = reader.ReadInt32();
            }

            _ = reader.ReadUInt32(); // victim state
            _ = reader.ReadUInt32(); // attacker state
            uint school = subCount == 0 ? 0 : firstSchool;
            float displayDamage = subCount == 0 ? 0 : firstDisplay;
            uint meleeSpellId = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // blocked
            if (reader.Remaining != 0) throw new MockProtocolException("attacker-state body had trailing bytes");
            state = new(attacker, target, damage, meleeSpellId, school, displayDamage);
            return true;
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException or MockProtocolException)
        {
            state = default;
            return false;
        }
    }

    internal readonly record struct AttackerState(ulong Attacker, ulong Target, uint TotalDamage,
        uint MeleeSpellId, uint School, float DisplayDamage);

    internal sealed class CombatBudget
    {
        private readonly int _frameLimit;
        private readonly int _byteLimit;
        private int _frames;
        private int _bytes;

        internal Action? BeforeRead { get; set; }
        internal Action<WorldFrame>? FrameObserved { get; set; }

        internal CombatBudget(int frameLimit = 4096, int byteLimit = 1024 * 1024)
        {
            if (frameLimit <= 0) throw new ArgumentOutOfRangeException(nameof(frameLimit));
            if (byteLimit <= 0) throw new ArgumentOutOfRangeException(nameof(byteLimit));
            _frameLimit = frameLimit;
            _byteLimit = byteLimit;
        }

        internal int Frames => _frames;
        internal int Bytes => _bytes;

        internal async Task<WorldFrame> ReadAsync(ScenarioConnection connection, CancellationToken cancellationToken)
        {
            BeforeRead?.Invoke();
            await connection.WaitForTrafficAsync(cancellationToken).ConfigureAwait(false);
            return await ReadFrameAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        internal async Task DrainQuietAsync(ScenarioConnection connection, CancellationToken cancellationToken)
        {
            for (int index = 0; index < 32; index++)
            {
                BeforeRead?.Invoke();
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                quiet.CancelAfter(TimeSpan.FromMilliseconds(25));
                try
                {
                    await connection.WaitForTrafficAsync(quiet.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await ReadFrameAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<WorldFrame> ReadFrameAsync(ScenarioConnection connection, CancellationToken cancellationToken)
        {
            if (++_frames > _frameLimit) throw new MockProtocolException($"combat phase exceeded {_frameLimit} frames");
            WorldFrame frame = await connection.ReadAsync(cancellationToken).ConfigureAwait(false);
            _bytes = checked(_bytes + frame.Payload.Length + 4);
            if (_bytes > _byteLimit) throw new MockProtocolException("combat phase exceeded its byte budget");
            FrameObserved?.Invoke(frame);
            return frame;
        }
    }
}
