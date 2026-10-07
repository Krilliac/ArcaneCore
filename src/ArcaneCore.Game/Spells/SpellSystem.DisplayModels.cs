using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;

namespace ArcaneCore.Game.Spells;

public readonly record struct DisplayModelGeometry(float NativeScale, float BoundingRadius, float CombatReach,
    float CollisionHeight = 0, float ModelScale = 1.0f, bool HasModelData = true);

public sealed partial class SpellSystem
{
    public Func<uint, DisplayModelGeometry?>? DisplayModelResolver { get; set; }

    public void UpdateDisplayModel(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        DisplayModelGeometry? geometry = DisplayModelResolver?.Invoke(target.DisplayId);
        if (geometry is not { } model || !float.IsFinite(model.NativeScale) || model.NativeScale <= 0)
        {
            target.SetFloat(UpdateFields.UnitFieldBoundingradius, 1.5f);
            target.SetFloat(UpdateFields.UnitFieldCombatreach, 1.5f);
            return;
        }

        float normalized = target.GetFloat(UpdateFields.ObjectFieldScaleX) / model.NativeScale;
        target.SetFloat(UpdateFields.UnitFieldBoundingradius,
            float.IsFinite(model.BoundingRadius) && model.BoundingRadius > 0 ? normalized * model.BoundingRadius : 1.5f);
        target.SetFloat(UpdateFields.UnitFieldCombatreach,
            float.IsFinite(model.CombatReach) && model.CombatReach > 0 ? normalized * model.CombatReach : 1.5f);
        if (model.HasModelData)
        {
            float height = normalized * (model.CollisionHeight > 0 && model.ModelScale > 0
                ? model.CollisionHeight / model.ModelScale : 2.0f);
            if (float.IsFinite(height) && height > 0 && MathF.Abs(target.Locomotion.CollisionHeight - height) > 0.0001f)
            {
                target.Locomotion.CollisionHeight = height;
                target.Locomotion.InvalidateEnvironmentSample();
            }
        }
    }
}
