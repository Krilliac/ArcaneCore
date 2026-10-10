using ArcaneCore.Data.Schema;

namespace ArcaneCore.Data.Characters;

/// <summary>
/// Characters version 50 is owned by PR #74; this placeholder lets the drunk state (51) and pet loyalty (52) steps compose on their own.
/// It yields to #74's module once that merges. Delete it then, and restore the empty-gap assertion in IntegratedSchemaTests.
/// </summary>
public sealed class Reserved74SchemaGap : ReservedSchemaGap
{
    public override DatabaseComponent Component => DatabaseComponent.Characters;

    public override int SchemaVersion => 50;
}
