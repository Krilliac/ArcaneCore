internal static class Probe
{
    // run-spike.ps1 edits this method: first the body (hot-applied), then the signature (restart).
    public static string Value() => "v1";
}
