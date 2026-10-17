using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace HotScripts;

/// <summary>What the host tests read back to tell the versions apart.</summary>
public static class Marker
{
#if V2
    public const int Version = 2;
#else
    public const int Version = 1;
#endif
}

/// <summary>The ids this module claims (none has a built-in script or handler).</summary>
public static class Ids
{
    public const uint SpellId = 990201;
    public const uint ExtraSpellId = 990202;
    public const uint MapId = 9901;
    public const byte EventType = 200;
    public const byte ActionType = 200;
}

/// <summary>A spell script; version 2 also serves <see cref="Ids.ExtraSpellId"/>.</summary>
#if V2
[SpellScript(Ids.SpellId, Ids.ExtraSpellId)]
#else
[SpellScript(Ids.SpellId)]
#endif
public sealed class HotSpellScript : ISpellScript
{
}

/// <summary>An instance script (ScriptDev2 <c>instance_*</c>) for <see cref="Ids.MapId"/>.</summary>
[InstanceScript(Ids.MapId)]
public sealed class HotInstanceScript(Map instance) : InstanceData(instance)
{
}

/// <summary>An EventAI event type the server does not have.</summary>
public sealed class HotEventHandler : EventAiEventHandler
{
    public override byte EventType => Ids.EventType;

    public override bool Check(EventAiContext context, EventAiHolder holder, Unit? invoker) => false;
}

/// <summary>An EventAI action type the server does not have.</summary>
public sealed class HotActionHandler : EventAiActionHandler
{
    public override byte ActionType => Ids.ActionType;

    public override bool Execute(EventAiContext context, CreatureAiAction action, EventAiInvocation invocation) => true;
}
