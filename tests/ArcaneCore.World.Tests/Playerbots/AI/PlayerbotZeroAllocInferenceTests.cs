using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.World.Playerbots.AI.Actions;
using ArcaneCore.World.Playerbots.AI.Inference;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.AI;

public sealed class PlayerbotZeroAllocInferenceTests
{
    [Fact]
    public void UnionAction_Layout_IsCompactAndBoxingFree()
    {
        int size = Unsafe.SizeOf<PlayerbotAction>();
        Assert.Equal(24, size);
        Assert.True(size <= 32);

        PlayerbotAction idle = new IdleAction(1.25f);
        PlayerbotAction move = new MoveToAction(new Vector3(10f, 20f, 30f), 0.75f);
        PlayerbotAction cast = new CastSpellAction(133u, 0x1122334455667788UL);
        PlayerbotAction pet = new PetCommandAction(7u, 0x8877665544332211UL);

        Assert.True(idle.TryGetValue(out IdleAction idleVal) && idleVal.DurationSec == 1.25f);
        Assert.True(move.TryGetValue(out MoveToAction moveVal) && moveVal.WorldPos == new Vector3(10f, 20f, 30f) && moveVal.Urgency == 0.75f);
        Assert.True(cast.TryGetValue(out CastSpellAction castVal) && castVal.SpellId == 133u && castVal.TargetGuid == 0x1122334455667788UL);
        Assert.True(pet.TryGetValue(out PetCommandAction petVal) && petVal.CommandId == 7u && petVal.PetTargetGuid == 0x8877665544332211UL);

        Span<PlayerbotAction> buffer = stackalloc PlayerbotAction[4];
        buffer[0] = idle;
        buffer[1] = move;
        buffer[2] = cast;
        buffer[3] = pet;

        // Warmup JIT for pattern matching switch
        int warmup = CountActions(buffer);
        Assert.Equal(4, warmup);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int i = 0; i < 2500; i++)
        {
            buffer[0] = new IdleAction(0.5f);
            buffer[1] = new MoveToAction(new Vector3(1f, 2f, 3f), 0.5f);
            buffer[2] = new CastSpellAction(100u, 42UL);
            buffer[3] = new PetCommandAction(2u, 99UL);
            total += CountActions(buffer);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(10_000, total);
        Assert.Equal(0L, after - before);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountActions(ReadOnlySpan<PlayerbotAction> actions)
    {
        int count = 0;
        for (int i = 0; i < actions.Length; i++)
        {
            count += actions[i] switch
            {
                IdleAction a when a.DurationSec >= 0f => 1,
                MoveToAction m when m.Urgency >= 0f => 1,
                CastSpellAction c when c.SpellId >= 0u => 1,
                PetCommandAction p when p.CommandId >= 0u => 1,
                _ => 0,
            };
        }
        return count;
    }

    [Fact]
    public void BatchCoordinator_ZeroAllocatedBytes_Across128BotsAnd100Ticks()
    {
        using var engine = new PlayerbotInferenceEngine(string.Empty);
        Assert.Equal(0UL, (ulong)engine.InputBufferAddress % 64UL);
        Assert.Equal(0UL, (ulong)engine.OutputBufferAddress % 64UL);
        Assert.False(engine.UsesOnnxSession);

        var coordinator = new PlayerbotBatchCoordinator();
        var bots = new PlayerbotBatchSlot[PlayerbotInferenceEngine.MaxBatchSize];

        for (int i = 0; i < bots.Length; i++)
        {
            int mode = i % 4;
            bots[i] = mode switch
            {
                // Idle: high health/mana, no target, out of combat
                0 => new PlayerbotBatchSlot(
                    BotGuid: (ulong)(i + 1),
                    HealthPct: 1.0f,
                    ManaPct: 1.0f,
                    TargetDistance: 0.0f,
                    ThreatLevel: 0.0f,
                    WeatherIntensity: 0.0f,
                    DestinationPos: new Vector3(100f, 200f, 50f),
                    MoveUrgencyLogit: -1.0f,
                    PrimarySpellId: 0,
                    TargetGuid: 0,
                    PetCommandId: 0,
                    PetTargetGuid: 0,
                    HasPet: false,
                    InCombat: false),
                // MoveTo: distant target, out of combat
                1 => new PlayerbotBatchSlot(
                    BotGuid: (ulong)(i + 1),
                    HealthPct: 0.9f,
                    ManaPct: 0.9f,
                    TargetDistance: 35.0f,
                    ThreatLevel: 0.1f,
                    WeatherIntensity: 0.2f,
                    DestinationPos: new Vector3(-8900f + i, -120f + i, 82f),
                    MoveUrgencyLogit: 1.5f,
                    PrimarySpellId: 0,
                    TargetGuid: (ulong)(10_000 + i),
                    PetCommandId: 0,
                    PetTargetGuid: 0,
                    HasPet: false,
                    InCombat: false),
                // CastSpell: in combat, high mana, close target
                2 => new PlayerbotBatchSlot(
                    BotGuid: (ulong)(i + 1),
                    HealthPct: 0.85f,
                    ManaPct: 0.95f,
                    TargetDistance: 12.0f,
                    ThreatLevel: 0.8f,
                    WeatherIntensity: 0.1f,
                    DestinationPos: new Vector3(10f, 20f, 30f),
                    MoveUrgencyLogit: 0.0f,
                    PrimarySpellId: 133u,
                    TargetGuid: (ulong)(20_000 + i),
                    PetCommandId: 0,
                    PetTargetGuid: 0,
                    HasPet: false,
                    InCombat: true),
                // PetCommand: has pet, in combat with high threat
                _ => new PlayerbotBatchSlot(
                    BotGuid: (ulong)(i + 1),
                    HealthPct: 0.5f,
                    ManaPct: 0.2f,
                    TargetDistance: 10.0f,
                    ThreatLevel: 0.95f,
                    WeatherIntensity: 0.1f,
                    DestinationPos: new Vector3(5f, 5f, 5f),
                    MoveUrgencyLogit: 0.5f,
                    PrimarySpellId: 0,
                    TargetGuid: (ulong)(30_000 + i),
                    PetCommandId: 1u,
                    PetTargetGuid: (ulong)(30_000 + i),
                    HasPet: true,
                    InCombat: true),
            };
        }

        // Warmup pass so JIT compilation and static initializers do not pollute the allocation measurement
        PlayerbotBatchDispatchStats warmupStats = coordinator.UpdatePlayerbotsTick(bots, engine);
        Assert.Equal(PlayerbotInferenceEngine.MaxBatchSize, warmupStats.TotalDispatched);

        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        PlayerbotBatchDispatchStats lastStats = default;
        for (int tick = 0; tick < 100; tick++)
        {
            lastStats = coordinator.UpdatePlayerbotsTick(bots, engine);
        }
        long afterBytes = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, afterBytes - beforeBytes);
        Assert.Equal(128, lastStats.TotalDispatched);
        Assert.Equal(32, lastStats.IdleCount);
        Assert.Equal(32, lastStats.MoveCount);
        Assert.Equal(32, lastStats.CastCount);
        Assert.Equal(32, lastStats.PetCommandCount);
    }

    [Fact]
    public void OnnxIoBinding_MinimalValidGraph_RunsZeroAllocationBatchInference()
    {
        string tempModelPath = Path.Combine(Path.GetTempPath(), $"arcanecore_playerbot_policy_{Guid.NewGuid():N}.onnx");
        try
        {
            File.WriteAllBytes(tempModelPath, BuildMinimalPolicyOnnxModel());

            using var engine = new PlayerbotInferenceEngine(tempModelPath);
            Assert.True(engine.UsesOnnxSession);
            Assert.Equal(0UL, (ulong)engine.InputBufferAddress % 64UL);
            Assert.Equal(0UL, (ulong)engine.OutputBufferAddress % 64UL);

            // Populate observation buffer for 4 bots with distinct dominant logits in [0..3]
            Span<float> obs0 = engine.GetBotObservationBuffer(0);
            obs0.Clear();
            obs0[0] = 5.0f; // Idle
            obs0[4] = 2.0f; // DurationSec

            Span<float> obs1 = engine.GetBotObservationBuffer(1);
            obs1.Clear();
            obs1[1] = 5.0f; // MoveTo
            obs1[5] = -8913.2f;
            obs1[6] = -134.6f;
            obs1[7] = 81.9f;
            obs1[8] = 0.0f; // Sigmoid(0) == 0.5f

            Span<float> obs2 = engine.GetBotObservationBuffer(2);
            obs2.Clear();
            obs2[2] = 5.0f; // CastSpell
            obs2[9] = 686.0f; // Shadow Bolt
            obs2[10] = 1024.0f;

            Span<float> obs3 = engine.GetBotObservationBuffer(3);
            obs3.Clear();
            obs3[3] = 5.0f; // PetCommand
            obs3[11] = 2.0f;
            obs3[12] = 2048.0f;

            Span<PlayerbotAction> actions = stackalloc PlayerbotAction[PlayerbotInferenceEngine.MaxBatchSize];

            // Warmup ONNX Runtime OrtIoBinding pass
            engine.InferBatch(4, actions);

            Assert.True(actions[0].TryGetValue(out IdleAction idle) && MathF.Abs(idle.DurationSec - 2.0f) < 1e-4f);
            Assert.True(actions[1].TryGetValue(out MoveToAction move) && MathF.Abs(move.Urgency - 0.5f) < 1e-4f && MathF.Abs(move.WorldPos.X - (-8913.2f)) < 1e-3f);
            Assert.True(actions[2].TryGetValue(out CastSpellAction cast) && cast.SpellId == 686u && cast.TargetGuid == 1024UL);
            Assert.True(actions[3].TryGetValue(out PetCommandAction pet) && pet.CommandId == 2u && pet.PetTargetGuid == 2048UL);

            // Verify zero managed allocations during steady-state OrtIoBinding inference
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++)
            {
                engine.InferBatch(4, actions);
            }
            long afterBytes = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, afterBytes - beforeBytes);
        }
        finally
        {
            if (File.Exists(tempModelPath))
            {
                File.Delete(tempModelPath);
            }
        }
    }

    /// <summary>
    /// Builds a minimal valid ONNX ModelProto (IR v7, opset 13) with a single MatMul node:
    /// obs_input [128, 64] * W [64, 16] -> action_output [128, 16], where W is the top-16 identity projection.
    /// </summary>
    private static byte[] BuildMinimalPolicyOnnxModel()
    {
        // Weight tensor W [64, 16]: W[k, k] = 1.0f for k in 0..15
        byte[] rawWeights = new byte[64 * 16 * sizeof(float)];
        for (int k = 0; k < 16; k++)
        {
            int offset = ((k * 16) + k) * sizeof(float);
            BinaryPrimitives.WriteSingleLittleEndian(rawWeights.AsSpan(offset, sizeof(float)), 1.0f);
        }

        // TensorProto for W:
        // field 1 (dims): 64, 16
        // field 2 (data_type): 1 (FLOAT)
        // field 8 (name): "W"
        // field 9 (raw_data): rawWeights
        var initializer = new List<byte>();
        WriteVarintField(initializer, 1, 64);
        WriteVarintField(initializer, 1, 16);
        WriteVarintField(initializer, 2, 1);
        WriteStringField(initializer, 8, "W");
        WriteBytesField(initializer, 9, rawWeights);

        // NodeProto: MatMul(obs_input, W) -> action_output
        var node = new List<byte>();
        WriteStringField(node, 1, "obs_input");
        WriteStringField(node, 1, "W");
        WriteStringField(node, 2, "action_output");
        WriteStringField(node, 3, "policy_matmul");
        WriteStringField(node, 4, "MatMul");

        // ValueInfoProto for obs_input [128, 64] and action_output [128, 16]
        byte[] inputInfo = BuildTensorValueInfo("obs_input", 128, 64);
        byte[] outputInfo = BuildTensorValueInfo("action_output", 128, 16);

        // GraphProto
        var graph = new List<byte>();
        WriteBytesField(graph, 1, node);
        WriteStringField(graph, 2, "playerbot_policy");
        WriteBytesField(graph, 5, initializer);
        WriteBytesField(graph, 11, inputInfo);
        WriteBytesField(graph, 12, outputInfo);

        // OperatorSetIdProto (domain = "", version = 13)
        var opset = new List<byte>();
        WriteStringField(opset, 1, "");
        WriteVarintField(opset, 2, 13);

        // ModelProto (ir_version = 7, graph = field 7, opset_import = field 8)
        var model = new List<byte>();
        WriteVarintField(model, 1, 7);
        WriteBytesField(model, 7, graph);
        WriteBytesField(model, 8, opset);
        return [.. model];
    }

    private static byte[] BuildTensorValueInfo(string name, int dim0, int dim1)
    {
        var d0 = new List<byte>();
        WriteVarintField(d0, 1, (ulong)dim0);
        var d1 = new List<byte>();
        WriteVarintField(d1, 1, (ulong)dim1);

        var shape = new List<byte>();
        WriteBytesField(shape, 1, d0);
        WriteBytesField(shape, 1, d1);

        var tensorType = new List<byte>();
        WriteVarintField(tensorType, 1, 1); // FLOAT
        WriteBytesField(tensorType, 2, shape);

        var typeProto = new List<byte>();
        WriteBytesField(typeProto, 1, tensorType);

        var valueInfo = new List<byte>();
        WriteStringField(valueInfo, 1, name);
        WriteBytesField(valueInfo, 2, typeProto);
        return [.. valueInfo];
    }

    private static void WriteTag(List<byte> buf, int fieldNumber, int wireType)
        => WriteRawVarint(buf, (ulong)((fieldNumber << 3) | wireType));

    private static void WriteVarintField(List<byte> buf, int fieldNumber, ulong value)
    {
        WriteTag(buf, fieldNumber, 0);
        WriteRawVarint(buf, value);
    }

    private static void WriteStringField(List<byte> buf, int fieldNumber, string value)
        => WriteBytesField(buf, fieldNumber, System.Text.Encoding.UTF8.GetBytes(value));

    private static void WriteBytesField(List<byte> buf, int fieldNumber, IReadOnlyList<byte> bytes)
    {
        WriteTag(buf, fieldNumber, 2);
        WriteRawVarint(buf, (ulong)bytes.Count);
        buf.AddRange(bytes);
    }

    private static void WriteRawVarint(List<byte> buf, ulong value)
    {
        while (value >= 0x80)
        {
            buf.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buf.Add((byte)value);
    }
}
