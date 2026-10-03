using System.Buffers.Binary;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>Writes synthetic z1.4 <c>.map</c> files (vmangos/cmangos GridMapDefines.h layout).</summary>
internal sealed class MapFileBuilder
{
    public ushort GridArea { get; set; }

    /// <summary>16×16 area flags, or null for the "no area" flag (the grid area applies everywhere).</summary>
    public ushort[]? AreaFlags { get; set; }

    public float GridHeight { get; set; }

    public float GridMaxHeight { get; set; }

    /// <summary>Height samples: float[] / ushort[] / byte[] V9 (129×129), or null for "no height".</summary>
    public Array? V9 { get; set; }

    public Array? V8 { get; set; }

    public ushort[]? Holes { get; set; }

    public bool HasLiquid { get; set; }

    public byte LiquidGlobalFlags { get; set; }

    public ushort LiquidGlobalEntry { get; set; }

    public float LiquidLevel { get; set; }

    public byte LiquidOffX { get; set; }

    public byte LiquidOffY { get; set; }

    public byte LiquidWidth { get; set; } = 128;

    public byte LiquidHeight { get; set; } = 128;

    /// <summary>Per-cell liquid flags (16×16); null for the "no type" flag.</summary>
    public byte[]? LiquidFlags { get; set; }

    public ushort[]? LiquidEntries { get; set; }

    /// <summary>Liquid surface heights (width × height); null for the "no height" flag.</summary>
    public float[]? LiquidHeights { get; set; }

    public static float[] Fill(int count, Func<int, int, int, float> value, int rowLength)
        => Enumerable.Range(0, count).Select(i => value(i, i / rowLength, i % rowLength)).ToArray();

    public byte[] Build()
    {
        var area = new List<byte>();
        U32(area, 0x41455241); // AREA
        U16(area, (ushort)(AreaFlags is null ? 1 : 0));
        U16(area, GridArea);
        if (AreaFlags is not null)
        {
            foreach (ushort a in AreaFlags)
            {
                U16(area, a);
            }
        }

        var height = new List<byte>();
        U32(height, 0x5447484D); // MHGT
        uint heightFlags = V9 switch
        {
            null => 1,
            ushort[] => 2,
            byte[] => 4,
            _ => 0,
        };
        U32(height, heightFlags);
        F32(height, GridHeight);
        F32(height, GridMaxHeight);
        foreach (Array samples in new[] { V9, V8 }.OfType<Array>())
        {
            foreach (object v in samples)
            {
                switch (v)
                {
                    case float f: F32(height, f); break;
                    case ushort s: U16(height, s); break;
                    case byte b: height.Add(b); break;
                }
            }
        }

        var liquid = new List<byte>();
        if (HasLiquid)
        {
            U32(liquid, 0x51494C4D); // MLIQ
            byte flags = (byte)((LiquidFlags is null ? 1 : 0) | (LiquidHeights is null ? 2 : 0));
            liquid.Add(flags);
            liquid.Add(LiquidGlobalFlags);
            U16(liquid, LiquidGlobalEntry);
            liquid.Add(LiquidOffX);
            liquid.Add(LiquidOffY);
            liquid.Add(LiquidWidth);
            liquid.Add(LiquidHeight);
            F32(liquid, LiquidLevel);
            if (LiquidFlags is not null)
            {
                foreach (ushort e in LiquidEntries ?? new ushort[256])
                {
                    U16(liquid, e);
                }

                liquid.AddRange(LiquidFlags);
            }

            foreach (float h in LiquidHeights ?? [])
            {
                F32(liquid, h);
            }
        }

        var holes = new List<byte>();
        foreach (ushort h in Holes ?? new ushort[256])
        {
            U16(holes, h);
        }

        uint areaOffset = 40;
        uint heightOffset = areaOffset + (uint)area.Count;
        uint liquidOffset = heightOffset + (uint)height.Count;
        uint holesOffset = liquidOffset + (uint)liquid.Count;

        var file = new List<byte>();
        U32(file, 0x5350414D); // MAPS
        U32(file, 0x342E317A); // z1.4
        U32(file, areaOffset);
        U32(file, (uint)area.Count);
        U32(file, heightOffset);
        U32(file, (uint)height.Count);
        U32(file, HasLiquid ? liquidOffset : 0);
        U32(file, (uint)liquid.Count);
        U32(file, holesOffset);
        U32(file, (uint)holes.Count);
        file.AddRange(area);
        file.AddRange(height);
        file.AddRange(liquid);
        file.AddRange(holes);
        return file.ToArray();
    }

    private static void U16(List<byte> list, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        list.AddRange(b.ToArray());
    }

    private static void U32(List<byte> list, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        list.AddRange(b.ToArray());
    }

    private static void F32(List<byte> list, float value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, value);
        list.AddRange(b.ToArray());
    }
}
