using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas;

/// <summary>Entry point: verb dispatch for serve, ctl, run, script and mcp (docs/areas/atlas.md).</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 1 && args[0] is "layers")
        {
            foreach (LayerInfo layer in LayerCatalog.All)
            {
                Console.Out.WriteLine($"{layer.Id,-20} {layer.Group,-10} {layer.View,-10} {layer.Title}");
            }

            return 0;
        }

        Console.Error.WriteLine("usage: arcane-atlas layers");
        return 2;
    }
}
