namespace ArcaneCore.World.Playerbots.AI.Inference;

/// <summary>
/// <c>World:Playerbots:Inference</c>: zero-allocation batched playerbot policy inference engine
/// (<see cref="PlayerbotInferenceEngine"/>; docs/areas/playbots.md).
/// </summary>
public sealed class PlayerbotInferenceOptions
{
    /// <summary>Run the zero-allocation batched playerbot inference pass each world tick (off by default).</summary>
    public bool Enabled { get; set; }

    /// <summary>Optional path to an ONNX policy model (obs_input [128,64] -&gt; action_output [128,16]); empty uses the built-in SIMD policy kernel.</summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>GPU device index for ONNX Runtime TensorRT execution provider (0..15).</summary>
    public int GpuDeviceId { get; set; }

    /// <summary>Enable the TensorRT execution provider when loading an ONNX model (falls back to CPU when unavailable; off by default).</summary>
    public bool EnableTensorRt { get; set; }

    /// <summary>Directory where TensorRT engine caches are stored (non-empty, at most 260 characters).</summary>
    public string TensorRtCachePath { get; set; } = "./cache/trt_11_engines";

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Inference";
        if (ModelPath is null || ModelPath.Length > 260 || ModelPath.Any(char.IsControl))
            throw new InvalidOperationException($"{section}: ModelPath must be at most 260 characters and contain no control characters.");
        if (GpuDeviceId is < 0 or > 15)
            throw new InvalidOperationException($"{section}: GpuDeviceId must be 0..15.");
        if (string.IsNullOrWhiteSpace(TensorRtCachePath) || TensorRtCachePath.Length > 260 || TensorRtCachePath.Any(char.IsControl))
            throw new InvalidOperationException($"{section}: TensorRtCachePath must be non-empty, at most 260 characters, and contain no control characters.");
    }
}
