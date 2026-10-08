using System.Reflection;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>
/// The gossip scripts installed on <see cref="QuestNpcServices.GossipScript"/>, outermost first. Several features wrap or chain the slot
/// (crafting's <c>ProfessionSpecializationGossip</c>, the dungeon <c>EmiGossipScript</c>, <see cref="NpcGossipScriptChain"/>), and the
/// order follows feature attach order, so a test asks whether its script is among the layers rather than whether it is the outermost.
/// </summary>
internal static class GossipScriptLayers
{
    public static IReadOnlyList<INpcGossipScript> Of(INpcGossipScript? script)
    {
        var found = new List<INpcGossipScript>();
        Walk(script, found);
        return found;
    }

    private static void Walk(INpcGossipScript? script, List<INpcGossipScript> found)
    {
        if (script is null || found.Contains(script))
        {
            return;
        }

        found.Add(script);
        for (Type? type = script.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                switch (field.GetValue(script))
                {
                    case INpcGossipScript inner:
                        Walk(inner, found);
                        break;
                    case IEnumerable<INpcGossipScript> inners:
                        foreach (INpcGossipScript inner in inners)
                        {
                            Walk(inner, found);
                        }

                        break;
                }
            }
        }
    }
}
