namespace ArcaneCore.Protocol.Versioning;

/// <summary>
/// The update-field layout of one client build: where each object type's field block ends (multi-version design §3.2).
/// S0 exposes only the block ends that the update mask sizes from. Semantic field ids arrive in S2, when the
/// values-update builder takes the layout instead of the generated 5875 constants.
/// </summary>
public interface IUpdateFieldLayout
{
    int ObjectEnd { get; }

    int ItemEnd { get; }

    int ContainerEnd { get; }

    int UnitEnd { get; }

    int PlayerEnd { get; }

    int GameObjectEnd { get; }

    int DynamicObjectEnd { get; }

    int CorpseEnd { get; }
}
