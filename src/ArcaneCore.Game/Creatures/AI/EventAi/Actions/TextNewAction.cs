using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>CMaNGOS ACTION_T_TEXT_NEW54: textId, target type, optional random template id.</summary>
public sealed class TextNewAction : EventAiActionHandler
{
    public override byte ActionType => (byte)EventAiActionType.TextNew;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation)
    {
        var target = context.SelectTarget(action.Param2, invocation, out bool error);
        if (error || target is null || context.System is not { } system) return false;
        if (action.Param3 < 0) return false;
        int id = action.Param3 != 0
            ? context.Content.SelectTemplateText((uint)action.Param3,
                context.Random(0, 1_000_000) / 10_000f, count => context.Random(0, count - 1))
            : action.Param1;
        // A template with no selected line is a successful no-op in CMaNGOS.
        if (id == 0 && action.Param3 != 0) return true;
        if (context.Content.FindText(id) is { } text)
            system.Say(context.Me, text, target);
        return true;
    }
}
