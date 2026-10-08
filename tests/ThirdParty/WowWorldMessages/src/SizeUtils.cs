namespace WowWorldMessages;

public static class SizeUtils
{
    public static int PackedGuidLength(this ulong v)
    {
        var size = 0;

        for (var i = 0; i < 8; i++)
        {
            if (((v >> (i * 8)) & 0xff) != 0)
            {
                size += 1;
            }
        }

        return size + 1;
    }
}