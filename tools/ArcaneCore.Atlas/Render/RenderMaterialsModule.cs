using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Render;

/// <summary>
/// Publishes the <see cref="CpuMaterials"/> service at foundation order, so a lane module (A2 registers terrain, A3 may register
/// wmo and m2) can add its CPU material kind while it registers, and <see cref="RenderModule"/> hands the same instance to the
/// renderer. It lives with the renderer, not with the texture formats: the Textures namespace does not register Render services.
/// </summary>
public sealed class RenderMaterialsModule : IAtlasModule
{
    /// <inheritdoc />
    public string Name => "render-materials";

    /// <inheritdoc />
    public int Order => ModuleOrder.Foundation + 40;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Services.Add(new CpuMaterials());
    }
}
