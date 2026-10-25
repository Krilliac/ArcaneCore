namespace ArcaneCore.TestFixtures;

/// <summary>
/// The hello and frame lines the world's map feed writer produces for feed revision 2. One file, compiled into both test projects
/// (ArcaneCore.World.Tests asserts <c>MapFeedJson.HelloLine</c> and <c>MapFeedJson.WriteFrame</c> write exactly these; ArcaneCore.Atlas.Tests feeds
/// them to the client), so a writer change that breaks the first test cannot leave the second one passing on a stale copy.
/// </summary>
internal static class MapFeedWireFixture
{
    public const string RevisionTwoHelloLine =
        """{"op":"hello","protocol":1,"rev":2,"sha":"abc123","pid":4242,"maps":[{"map":0,"instance":0,"players":0,"creatures":3}]}""";

    public const string RevisionTwoFrameLine =
        """{"op":"frame","seq":1,"clockMs":1000,"map":0,"instance":0,"grids":[[32,48]],"counters":{"players":0,"bots":0,"creatures":2,"gameobjects":1,"emitted":3,"truncated":0,"nonFinite":0,"pathsOmitted":0,"dropped":0,"online":0,"diffMs":0,"deferredPaths":0,"directFallbackPaths":0,"noPathPaths":0,"homePathTeleports":0},"entities":[{"g":"F13000012B000001","k":"creature","e":299,"n":"Wolf","x":10,"y":10,"z":5,"o":0,"hp":75,"cb":true,"d":1234,"mt":"chase"},{"g":"F13000012B000002","k":"creature","e":299,"n":"Wolf","x":250,"y":10,"z":5,"o":0,"hp":100,"d":55,"mt":"idle"},{"g":"F110000000000001","k":"gameobject","e":2843,"n":"Chest","x":1,"y":2,"z":3,"o":0}]}""";
}
