using System.Net;
using ArcaneCore.Kernel.Net;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// Addresses that fill an <see cref="IpRateTable"/> to exactly its capacity through free slots only.
/// The table's placement is deterministic (open addressing, the first free slot of the key's probe
/// window, nothing ever removed), so a local model of the slots predicts where each candidate lands;
/// a candidate whose window is already full is skipped instead of forcing an eviction, which lets a
/// test know exactly which addresses the table tracks afterwards.
/// </summary>
internal static class TableFiller
{
    /// <summary>Distinct addresses under <paramref name="prefix"/> (two octets) that each take a free slot of a table of <paramref name="capacity"/> slots (a power of two).</summary>
    public static List<IPAddress> FreeSlotAddresses(int capacity, string prefix, IEnumerable<IpKey>? alreadyTracked = null)
    {
        int mask = capacity - 1;
        bool[] used = new bool[capacity];
        int count = 0;
        foreach (IpKey key in alreadyTracked ?? [])
        {
            Place(key, used, mask, ref count);
        }

        var addresses = new List<IPAddress>();
        for (int i = 1; count < capacity; i++)
        {
            if (i >= 100_000)
            {
                throw new InvalidOperationException("could not fill the table through free slots");
            }

            var address = IPAddress.Parse($"{prefix}.{i / 256}.{i % 256}");
            if (Place(IpKey.From(address), used, mask, ref count))
            {
                addresses.Add(address);
            }
        }

        return addresses;
    }

    private static bool Place(in IpKey key, bool[] used, int mask, ref int count)
    {
        int start = key.Mix() & mask;
        for (int i = 0; i < IpRateTable.ProbeWindow; i++)
        {
            int index = (start + i) & mask;
            if (!used[index])
            {
                used[index] = true;
                count++;
                return true;
            }
        }

        return false;
    }
}
