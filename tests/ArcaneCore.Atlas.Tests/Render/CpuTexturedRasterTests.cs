using System.Numerics;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Materials;
using ArcaneCore.Atlas.Render;
using ArcaneCore.Atlas.Tests.Textures;
using ArcaneCore.Atlas.Textures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Render;

/// <summary>
/// The CPU renderer's textured path (design section 2.7): a v2 chunk whose batches carry a "generic" material is drawn with the
/// texture sampled at the interpolated uv0, through <see cref="CpuMaterials"/>. The probe texture is a 2x2 palettized BLP built in
/// code (red, green, blue, white; row-major from the top-left), so every expected colour is the texel itself and is exact.
/// </summary>
public sealed class CpuTexturedRasterTests
{
    private const int Size = 64;
    private const string TexturePath = "Textures\\Test\\Probe.blp";

    private static readonly CameraState Camera = Views.TopCamera;
    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly Rgb Green = new(0, 255, 0);
    private static readonly Rgb Blue = new(0, 0, 255);
    private static readonly Rgb White = new(255, 255, 255);

    [Fact]
    public void UnlitQuad_ShowsEachTexelAtItsQuadrantCentre()
    {
        RenderedFrame frame = Render(MaterialFlags.Unlit, BlendMode.Opaque, alpha: null);

        foreach ((double dx, double dy, Rgb expected) in Quadrants())
        {
            (int x, int y) = Pixels.Of(Camera, new WorldPoint(100 + dx, 200 + dy, 0), Size, Size);
            Assert.Equal(expected, frame.Buffer.Get(x, y));
        }
    }

    [Fact]
    public void LitQuad_IsTheTexelThroughTheSameShadeAsAFlatColour()
    {
        RenderedFrame frame = Render(MaterialFlags.None, BlendMode.Opaque, alpha: null);

        (int x, int y) = Pixels.Of(Camera, new WorldPoint(100 - 7.5, 200 + 7.5, 0), Size, Size);
        var a = new Vector3(85, 185, 0);
        var b = new Vector3(115, 185, 0);
        var c = new Vector3(115, 215, 0);
        Rgb expected = CpuRenderer.ShadeTriangle(Red, a, b, c, LayerIds.RefM2Tex);
        Assert.Equal(expected, frame.Buffer.Get(x, y));
        Assert.NotEqual(Red, expected);
    }

    [Fact]
    public void AlphaKey_DropsTexelsBelowTheReference_AndKeepsTheOthers()
    {
        // alpha plane (8-bit): red 255, green 0, blue 255, white 0; alphaRef 0.5.
        RenderedFrame frame = Render(MaterialFlags.Unlit, BlendMode.AlphaKey, alpha: [255, 0, 255, 0], alphaRef: 0.5);

        Assert.Equal(Red, At(frame, -7.5, 7.5));
        Assert.Equal(Rgb.Background, At(frame, 7.5, 7.5));
        Assert.Equal(Blue, At(frame, -7.5, -7.5));
        Assert.Equal(Rgb.Background, At(frame, 7.5, -7.5));
    }

    [Fact]
    public void AlphaBlend_MixesWithWhatIsBehind()
    {
        // The red texel has alpha 128 of 255 over the chart background.
        RenderedFrame frame = Render(MaterialFlags.Unlit, BlendMode.AlphaBlend, alpha: [128, 255, 255, 255]);

        Rgb mixed = At(frame, -7.5, 7.5);
        Rgb bg = Rgb.Background;
        double a = 128 / 255.0;
        Assert.InRange(mixed.R, (int)(255 * a) + (int)(bg.R * (1 - a)) - 2, (int)(255 * a) + (int)(bg.R * (1 - a)) + 2);
        Assert.InRange(mixed.G, (int)(bg.G * (1 - a)) - 2, (int)(bg.G * (1 - a)) + 2);
        Assert.Equal(Green, At(frame, 7.5, 7.5));
    }

    [Fact]
    public void ARenderIsDeterministic()
    {
        byte[] first = Render(MaterialFlags.Unlit, BlendMode.Opaque, alpha: null).Buffer.Pixels;
        byte[] second = Render(MaterialFlags.Unlit, BlendMode.Opaque, alpha: null).Buffer.Pixels;

        Assert.Equal(first, second);
    }

    [Fact]
    public void AMissingTexture_DrawsThePlaceholder_NotAHole()
    {
        var files = new MemoryClientFiles();
        RenderedFrame frame = Render(MaterialFlags.Unlit, BlendMode.Opaque, alpha: null, files: files);

        Rgb centre = At(frame, 0, 0);
        Assert.NotEqual(Rgb.Background, centre);
    }

    [Fact]
    public void WithoutATextureCache_TheSameChunkRendersAsAFlatLayerColour()
    {
        var rig = new RenderRig(new FakeSource().Add(LayerIds.RefM2Tex, Views.TileOf(Camera), Quad(MaterialFlags.Unlit, BlendMode.Opaque)));

        RenderedFrame frame = rig.Frame(ReferenceView(), Size, Size);

        Assert.NotEqual(Rgb.Background, At(frame, 0, 0));
        Assert.NotEqual(Red, At(frame, -7.5, 7.5));
    }

    [Fact]
    public void TheMaterialRegistry_HasGeneric_AndRefusesDuplicates()
    {
        var materials = new CpuMaterials();

        Assert.True(materials.TryGet("generic", out _));
        Assert.False(materials.TryGet("nonsense", out _));
        Assert.Throws<ArgumentException>(() => materials.Register("generic", materials.Get("generic")));
        materials.Register("terrain", materials.Get("generic"));
        Assert.True(materials.TryGet("terrain", out _));
    }

    [Fact]
    public void AnUnknownMaterialKind_FallsBackToGeneric_InsteadOfFailingTheFrame()
    {
        var material = new Material("character", BlendMode.Opaque, MaterialFlags.Unlit, [new TextureRef("client:" + TexturePath)]);
        CpuRenderer rig = Rig(Files(null), material);

        RenderedFrame frame = Draw(rig);

        Assert.Equal(Red, At(frame, -7.5, 7.5));
    }

    [Fact]
    public void AKindRegisteredByALaneModule_InTheSharedService_IsDrawnByTheRendererTheRenderModuleBuilt()
    {
        var source = new FakeSource().Add(LayerIds.RefM2Tex, Views.TileOf(Camera), Quad(new Material("probe", BlendMode.Opaque, MaterialFlags.Unlit, [])));
        using AtlasContext context = AtlasModules.Compose(
            new AtlasPaths(null, null),
            NullLoggerFactory.Instance,
            headless: true,
            [new LaneModule("layers", ModuleOrder.Foundation, c => c.Layers.Add(source)), new TexturesModule(), new RenderMaterialsModule(), new LaneModule("lane-a2", ModuleOrder.Foundation + 60, c => c.Services.Get<CpuMaterials>()!.Register("probe", new SolidMaterial(Green))), new RenderModule()]);
        Assert.Empty(context.Problems);

        RenderedFrame frame = ((CpuRenderer)context.Services.Get<IHeadlessRenderer>()!).Rasterize(ReferenceView(), Size, Size, CancellationToken.None);

        Assert.Equal(Green, At(frame, 0, 0));
    }

    private sealed class LaneModule(string name, int order, Action<AtlasContext> register) : IAtlasModule
    {
        public string Name { get; } = name;

        public int Order { get; } = order;

        public void Register(AtlasContext context) => register(context);
    }

    private sealed class SolidMaterial(Rgb colour) : ICpuMaterial
    {
        public BatchShader Prepare(Material material, CpuTextureSet textures, Rgb flat) => new SolidShader(colour);
    }

    private sealed class SolidShader(Rgb colour) : BatchShader(BlendMode.Opaque)
    {
        public override bool Shade(double u, double v, out Rgb shaded, out double alpha)
        {
            shaded = colour;
            alpha = 1.0;
            return true;
        }
    }

    private static IEnumerable<(double Dx, double Dy, Rgb Colour)> Quadrants()
    {
        yield return (-7.5, 7.5, Red);
        yield return (7.5, 7.5, Green);
        yield return (-7.5, -7.5, Blue);
        yield return (7.5, -7.5, White);
    }

    private static Rgb At(RenderedFrame frame, double dx, double dy)
    {
        (int x, int y) = Pixels.Of(Camera, new WorldPoint(100 + dx, 200 + dy, 0), Size, Size);
        return frame.Buffer.Get(x, y);
    }

    private static MemoryClientFiles Files(byte[]? alpha)
    {
        var files = new MemoryClientFiles();
        files.Add(
            TexturePath,
            BlpTestWriter.Palettized(2, 2, [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 255)], [0, 1, 2, 3], alpha is null ? (byte)0 : (byte)8, alpha));
        return files;
    }

    private static GeometryChunk Quad(MaterialFlags flags, BlendMode blend, double alphaRef = 0)
        => Quad(new Material("generic", blend, flags, [new TextureRef("client:" + TexturePath)], AlphaRef: alphaRef));

    /// <summary>The 30 x 30 quad around the camera target; u grows east, v grows south (image rows run down).</summary>
    private static GeometryChunk Quad(Material material)
    {
        TileKey tile = Views.TileOf(Camera);
        var builder = new GeometryBuilder(LayerIds.RefM2Tex, tile, new WorldPoint(0, 0, 0));
        Vector3 up = Vector3.UnitZ;
        uint a = builder.AddVertex(85, 185, 0, new VertexExtras { Normal = up, Uv0 = new Vector2(0, 1) });
        uint b = builder.AddVertex(115, 185, 0, new VertexExtras { Normal = up, Uv0 = new Vector2(1, 1) });
        uint c = builder.AddVertex(115, 215, 0, new VertexExtras { Normal = up, Uv0 = new Vector2(1, 0) });
        uint d = builder.AddVertex(85, 215, 0, new VertexExtras { Normal = up, Uv0 = new Vector2(0, 0) });
        builder.BeginBatch(material);
        builder.AddTriangle(a, b, c, edges: false);
        builder.AddTriangle(a, c, d, edges: false);
        builder.EndBatch();
        return builder.Build();
    }

    private static CpuRenderer Rig(MemoryClientFiles files, Material material)
    {
        var source = new FakeSource().Add(LayerIds.RefM2Tex, Views.TileOf(Camera), Quad(material));
        var registry = new LayerRegistry();
        registry.Add(source);
        var cache = new LayerCache(registry);
        var textures = new TextureCache(files, new GeneratedTextureRegistry());
        return new CpuRenderer(registry, cache, textures, new CpuMaterials());
    }

    private static RenderedFrame Draw(CpuRenderer renderer)
        => renderer.Rasterize(ReferenceView(), Size, Size, CancellationToken.None);

    /// <summary>The textured layers are Reference-view layers, so the view shows the reference side only.</summary>
    private static ViewState ReferenceView()
        => Views.Of(Camera, (LayerIds.RefM2Tex, Views.Style())) with { Compare = CompareMode.Reference };

    private static RenderedFrame Render(MaterialFlags flags, BlendMode blend, byte[]? alpha, double alphaRef = 0, MemoryClientFiles? files = null)
    {
        var material = new Material("generic", blend, flags, [new TextureRef("client:" + TexturePath)], AlphaRef: alphaRef);
        return Draw(Rig(files ?? Files(alpha), material));
    }
}
