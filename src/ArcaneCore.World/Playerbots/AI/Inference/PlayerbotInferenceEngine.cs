using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ArcaneCore.World.Playerbots.AI.Actions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ArcaneCore.World.Playerbots.AI.Inference;

/// <summary>
/// Zero-allocation batched playerbot policy inference engine backed by 64-byte cache-line-aligned
/// native memory buffers (<see cref="NativeMemory.AlignedAlloc"/>) and pre-bound ONNX Runtime
/// <see cref="OrtIoBinding"/> tensors (or an in-place <see cref="TensorPrimitives"/> SIMD kernel
/// when no external <c>.onnx</c> model path is configured).
/// </summary>
public sealed unsafe class PlayerbotInferenceEngine : IDisposable
{
    public const int MaxBatchSize = 128;
    public const int ObservationFeatures = 64;
    public const int ActionLogitsCount = 16;
    private const nuint CacheLineAlignment = 64;

    private readonly InferenceSession? _session;
    private readonly OrtIoBinding? _ioBinding;
    private readonly RunOptions? _runOptions;

    private float* _inputBuffer;
    private float* _outputBuffer;
    private readonly int _inputTotalFloats;
    private readonly int _outputTotalFloats;

    private readonly OrtValue? _inputOrtValue;
    private readonly OrtValue? _outputOrtValue;
    private bool _disposed;

    // Static SIMD weight vectors (16 floats = 512 bits, one AVX-512 register or two AVX2 registers)
    // used by the embedded zero-allocation policy kernel when no external ONNX model is loaded.
    private static readonly float[] IdleWeights =
    [
        0.80f, 0.20f, -0.35f, -0.60f, 0.10f, 0.00f, 0.00f, 0.00f,
        -0.90f, -0.50f, 0.00f, -0.50f, 0.00f, 0.00f, 0.00f, 0.25f
    ];

    private static readonly float[] MoveWeights =
    [
        0.10f, 0.05f, 0.45f, 0.15f, -0.05f, 0.00f, 0.00f, 0.00f,
        1.75f, -0.10f, 0.00f, -0.10f, 0.00f, 0.00f, 0.00f, 0.05f
    ];

    private static readonly float[] CastWeights =
    [
        0.25f, 0.65f, -0.05f, 0.55f, 0.00f, 0.00f, 0.00f, 0.00f,
        -0.15f, 0.05f, 0.00f, -0.10f, 0.00f, 0.00f, 0.00f, 0.40f
    ];

    private static readonly float[] PetWeights =
    [
        0.15f, 0.15f, 0.05f, 0.40f, 0.00f, 0.00f, 0.00f, 0.00f,
        -0.10f, -0.05f, 0.00f, 3.50f, 0.00f, 0.00f, 3.00f, 0.20f
    ];

    /// <summary>
    /// Initializes a zero-allocation inference engine in embedded SIMD policy mode with
    /// 64-byte aligned native input/output buffers.
    /// </summary>
    public PlayerbotInferenceEngine()
        : this(onnxModelPath: string.Empty, gpuDeviceId: 0, enableTensorRt: false)
    {
    }

    /// <summary>
    /// Initializes a zero-allocation inference engine. When <paramref name="onnxModelPath"/> is non-empty,
    /// loads the ONNX model into an <see cref="InferenceSession"/> and binds the 64-byte-aligned native
    /// buffers once via <see cref="OrtIoBinding"/>.
    /// </summary>
    public PlayerbotInferenceEngine(
        string onnxModelPath,
        int gpuDeviceId = 0,
        bool enableTensorRt = true,
        string tensorRtCachePath = "./cache/trt_11_engines")
    {
        if (gpuDeviceId is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(gpuDeviceId), "GPU device ID must be 0..15.");

        _inputTotalFloats = MaxBatchSize * ObservationFeatures;
        _outputTotalFloats = MaxBatchSize * ActionLogitsCount;

        nuint inputByteLength = (nuint)(_inputTotalFloats * sizeof(float));
        nuint outputByteLength = (nuint)(_outputTotalFloats * sizeof(float));

        _inputBuffer = (float*)NativeMemory.AlignedAlloc(inputByteLength, CacheLineAlignment);
        _outputBuffer = (float*)NativeMemory.AlignedAlloc(outputByteLength, CacheLineAlignment);

        NativeMemory.Clear(_inputBuffer, inputByteLength);
        NativeMemory.Clear(_outputBuffer, outputByteLength);

        if (string.IsNullOrWhiteSpace(onnxModelPath))
        {
            return;
        }

        try
        {
            using var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableMemoryPattern = true,
                EnableCpuMemArena = true,
            };

            if (enableTensorRt)
            {
                TryAppendTensorRtProvider(sessionOptions, gpuDeviceId, tensorRtCachePath);
            }

            _session = new InferenceSession(onnxModelPath, sessionOptions);
            _runOptions = new RunOptions();
            _ioBinding = _session.CreateIoBinding();

            long[] inputShape = [MaxBatchSize, ObservationFeatures];
            long[] outputShape = [MaxBatchSize, ActionLogitsCount];

            _inputOrtValue = OrtValue.CreateTensorValueWithData(
                OrtMemoryInfo.DefaultInstance,
                TensorElementType.Float,
                inputShape,
                (IntPtr)_inputBuffer,
                _inputTotalFloats * sizeof(float));

            _outputOrtValue = OrtValue.CreateTensorValueWithData(
                OrtMemoryInfo.DefaultInstance,
                TensorElementType.Float,
                outputShape,
                (IntPtr)_outputBuffer,
                _outputTotalFloats * sizeof(float));

            _ioBinding.BindInput("obs_input", _inputOrtValue);
            _ioBinding.BindOutput("action_output", _outputOrtValue);
            _ioBinding.SynchronizeBoundInputs();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Native address of the 64-byte aligned observation buffer.</summary>
    public nuint InputBufferAddress => (nuint)_inputBuffer;

    /// <summary>Native address of the 64-byte aligned action-logits buffer.</summary>
    public nuint OutputBufferAddress => (nuint)_outputBuffer;

    /// <summary>Whether an external ONNX Runtime session is bound via <see cref="OrtIoBinding"/>.</summary>
    public bool UsesOnnxSession => _session is not null;

    private static void TryAppendTensorRtProvider(SessionOptions sessionOptions, int gpuDeviceId, string tensorRtCachePath)
    {
        try
        {
            using var trtOptions = new OrtTensorRTProviderOptions();
            trtOptions.UpdateOptions(new Dictionary<string, string>
            {
                ["device_id"] = gpuDeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["trt_engine_cache_enable"] = "1",
                ["trt_engine_cache_path"] = string.IsNullOrWhiteSpace(tensorRtCachePath) ? "./cache/trt_11_engines" : tensorRtCachePath,
                ["trt_cuda_graph_enable"] = "1",
            });
            sessionOptions.AppendExecutionProvider_Tensorrt(trtOptions);
        }
        catch (Exception)
        {
            // Fall back cleanly to the default CPU execution provider when TensorRT 11 / CUDA
            // libraries are unavailable on the host machine.
        }
    }

    /// <summary>
    /// Exposes a zero-allocation writable <see cref="Span{T}"/> directly into the pinned native
    /// observation buffer for the bot at <paramref name="botBatchIndex"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<float> GetBotObservationBuffer(int botBatchIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)botBatchIndex, (uint)MaxBatchSize);
        return new Span<float>(_inputBuffer + (botBatchIndex * ObservationFeatures), ObservationFeatures);
    }

    /// <summary>
    /// Exposes a zero-allocation read-only <see cref="ReadOnlySpan{T}"/> over the output logits
    /// for the bot at <paramref name="botBatchIndex"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<float> GetBotLogitsBuffer(int botBatchIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)botBatchIndex, (uint)MaxBatchSize);
        return new ReadOnlySpan<float>(_outputBuffer + (botBatchIndex * ActionLogitsCount), ActionLogitsCount);
    }

    /// <summary>
    /// Executes the batched policy pass and populates <paramref name="outActions"/> with non-boxing
    /// C# 15 <see cref="PlayerbotAction"/> union values. Allocates 0 bytes on the managed heap.
    /// </summary>
    public void InferBatch(int activeBotCount, Span<PlayerbotAction> outActions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (activeBotCount <= 0)
        {
            return;
        }

        int count = Math.Min(activeBotCount, Math.Min(MaxBatchSize, outActions.Length));

        if (_session is not null && _ioBinding is not null && _runOptions is not null)
        {
            // Zero-allocation native execution: reads _inputBuffer and writes _outputBuffer directly.
            _session.RunWithBinding(_runOptions, _ioBinding);
        }
        else
        {
            EvaluateEmbeddedSimdPolicy(count);
        }

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<float> logits = new(_outputBuffer + (i * ActionLogitsCount), ActionLogitsCount);

            bool allFinite = true;
            for (int j = 0; j < ActionLogitsCount; j++)
            {
                if (!float.IsFinite(logits[j]))
                {
                    allFinite = false;
                    break;
                }
            }

            if (!allFinite)
            {
                outActions[i] = new IdleAction(0.5f);
                continue;
            }

            // Hardware-accelerated SIMD ArgMax over the first 4 action-type logits.
            int actionType = TensorPrimitives.IndexOfMax(logits[..4]);

            outActions[i] = actionType switch
            {
                0 => new IdleAction(DurationSec: Math.Clamp(logits[4], 0.1f, 5.0f)),
                1 => new MoveToAction(
                    WorldPos: new Vector3(logits[5], logits[6], logits[7]),
                    Urgency: SigmoidScalar(logits[8])),
                2 => new CastSpellAction(
                    SpellId: (uint)Math.Max(0.0f, logits[9]),
                    TargetGuid: (ulong)Math.Max(0.0f, logits[10])),
                3 => new PetCommandAction(
                    CommandId: (uint)Math.Max(0.0f, logits[11]),
                    PetTargetGuid: (ulong)Math.Max(0.0f, logits[12])),
                _ => new IdleAction(0.5f),
            };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SigmoidScalar(float x)
    {
        float result = 0f;
        TensorPrimitives.Sigmoid(new ReadOnlySpan<float>(in x), new Span<float>(ref result));
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EvaluateEmbeddedSimdPolicy(int count)
    {
        ReadOnlySpan<float> idleW = IdleWeights;
        ReadOnlySpan<float> moveW = MoveWeights;
        ReadOnlySpan<float> castW = CastWeights;
        ReadOnlySpan<float> petW = PetWeights;

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<float> obs = new(_inputBuffer + (i * ObservationFeatures), ObservationFeatures);
            Span<float> logits = new(_outputBuffer + (i * ActionLogitsCount), ActionLogitsCount);

            ReadOnlySpan<float> head = obs[..16];
            logits[0] = TensorPrimitives.Dot(head, idleW);
            logits[1] = TensorPrimitives.Dot(head, moveW);
            logits[2] = TensorPrimitives.Dot(head, castW);
            logits[3] = TensorPrimitives.Dot(head, petW);

            // Continuous action parameters projected from the observation vector:
            // [4]: idle duration seconds
            // [5..7]: target/destination WorldPos (X, Y, Z)
            // [8]: movement urgency logit
            // [9]: primary spell ID
            // [10]: target GUID
            // [11]: pet command ID
            // [12]: pet target GUID
            logits[4] = obs[4] > 0f ? obs[4] : 0.5f;
            logits[5] = obs[5];
            logits[6] = obs[6];
            logits[7] = obs[7];
            logits[8] = obs[8];
            logits[9] = obs[9];
            logits[10] = obs[10];
            logits[11] = obs[11];
            logits[12] = obs[12];
            logits[13] = obs[13];
            logits[14] = obs[14];
            logits[15] = obs[15];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inputOrtValue?.Dispose();
        _outputOrtValue?.Dispose();
        _ioBinding?.Dispose();
        _runOptions?.Dispose();
        _session?.Dispose();

        if (_inputBuffer != null)
        {
            NativeMemory.AlignedFree(_inputBuffer);
            _inputBuffer = null;
        }

        if (_outputBuffer != null)
        {
            NativeMemory.AlignedFree(_outputBuffer);
            _outputBuffer = null;
        }
    }
}
