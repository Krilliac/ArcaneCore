namespace WowWorldMessages;

internal static class Extensions
{
    public static uint LowerUInt(this ulong v) => (uint)(v & 0xFF_FF_FF_FF);

    public static uint UpperUInt(this ulong v) => (uint)((v >> 32) & 0xFF_FF_FF_FF);
}