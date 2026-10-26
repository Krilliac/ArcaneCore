using System.Globalization;

namespace ArcaneCore.Atlas.Live;

/// <summary>Constants of the map-feed protocol (design section 9, lane L1 serves it).</summary>
internal static class LiveProtocol
{
    // ref: ATLAS-2026-10-18.md:208-230 (feed protocol 1: NDJSON over the named pipe arcanecore-mapfeed-{pid})
    // ref: claude/atlas-l1@5891e4773:src/ArcaneCore.World/Ops/MapFeed/MapFeedProtocol.cs:68 (the server's Version = 1)
    // ref: claude/atlas-l1@5891e4773:src/ArcaneCore.World/Ops/MapFeed/WorldMapFeedOptions.cs:16 (the server's PipeNameTemplate = "arcanecore-mapfeed-" + pid token)

    /// <summary>The protocol number sent in <c>hello</c> and required in the reply.</summary>
    public const int Version = 1;

    /// <summary>Longest line accepted from the server (8 MiB); a frame of 4096 entities with paths is well below one MiB.</summary>
    public const int MaxLineBytes = 8 * 1024 * 1024;

    /// <summary>Pipe name prefix; the server's process id follows.</summary>
    public const string PipePrefix = "arcanecore-mapfeed-";

    public const string OpHello = "hello";
    public const string OpSubscribe = "subscribe";
    public const string OpUnsubscribe = "unsubscribe";
    public const string OpFrame = "frame";
    public const string OpQuery = "query";
    public const string OpResult = "result";
    public const string OpError = "error";

    // Packet tools (src/ArcaneCore.World/Ops/MapFeed/MapFeedConnection.Tools.cs; docs/areas/map-feed.md, "Packet tools").
    public const string OpConsole = "console";
    public const string OpInject = "inject";
    public const string OpSend = "send";
    public const string OpTap = "tap";
    public const string OpUntap = "untap";
    public const string OpPacket = "packet";
    public const string OpTapEnd = "tapEnd";
    public const string OpConsoleLine = "consoleLine";

    /// <summary>The pipe name of the world daemon with process id <paramref name="pid"/>.</summary>
    public static string PipeName(int pid) => PipePrefix + pid.ToString(CultureInfo.InvariantCulture);
}
