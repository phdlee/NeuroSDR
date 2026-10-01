namespace KiwiSDRPlugin.Jnx;

public static class BitUtil
{
    public static int BitCount(int v)
    {
        int bc = 0;
        uint u = (uint)v;
        while (u != 0)
        {
            bc++;
            u &= u - 1;
        }
        return bc;
    }

    public static int BitReverse(int v, int bits)
    {
        int r = 0;
        for (int i = 0; i < bits; i++)
        {
            r = (r << 1) | (v & 1);
            v >>= 1;
        }
        return r;
    }

    public static string LeadingZeros(int v, int width) => v.ToString().PadLeft(width, '0');

    public static string ToHex(int v, int width)
    {
        string s = v.ToString("X");
        if (width < 0) width = -width;
        return s.PadLeft(width, '0');
    }
}
