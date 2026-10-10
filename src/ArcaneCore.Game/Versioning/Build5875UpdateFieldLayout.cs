using ArcaneCore.Protocol.Versioning;

namespace ArcaneCore.Game.Versioning;

/// <summary>The build-5875 update-field layout, read straight from the generated <see cref="UpdateFields"/> table.</summary>
public sealed class Build5875UpdateFieldLayout : IUpdateFieldLayout
{
    public static readonly Build5875UpdateFieldLayout Instance = new();

    private Build5875UpdateFieldLayout()
    {
    }

    public int ObjectEnd => UpdateFields.ObjectEnd;

    public int ItemEnd => UpdateFields.ItemEnd;

    public int ContainerEnd => UpdateFields.ContainerEnd;

    public int UnitEnd => UpdateFields.UnitEnd;

    public int PlayerEnd => UpdateFields.PlayerEnd;

    public int GameObjectEnd => UpdateFields.GameobjectEnd;

    public int DynamicObjectEnd => UpdateFields.DynamicobjectEnd;

    public int CorpseEnd => UpdateFields.CorpseEnd;
}
