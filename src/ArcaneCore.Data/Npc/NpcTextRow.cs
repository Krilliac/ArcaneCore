namespace ArcaneCore.Data.Npc;

/// <summary>
/// <c>npc_text</c> row in the cmangos-classic inline layout (ID, then per variant i = 0..7:
/// text{i}_0, text{i}_1, lang{i}, prob{i}, em{i}_0..em{i}_5). Converted to
/// <see cref="ArcaneCore.Kernel.Npc.NpcText"/> on load.
/// </summary>
public sealed class NpcTextRow
{
    public uint Id { get; set; }

    public string Text0_0 { get; set; } = string.Empty;

    public string Text0_1 { get; set; } = string.Empty;

    public uint Lang0 { get; set; }

    public float Prob0 { get; set; }

    public uint Em0_0 { get; set; }

    public uint Em0_1 { get; set; }

    public uint Em0_2 { get; set; }

    public uint Em0_3 { get; set; }

    public uint Em0_4 { get; set; }

    public uint Em0_5 { get; set; }

    public string Text1_0 { get; set; } = string.Empty;

    public string Text1_1 { get; set; } = string.Empty;

    public uint Lang1 { get; set; }

    public float Prob1 { get; set; }

    public uint Em1_0 { get; set; }

    public uint Em1_1 { get; set; }

    public uint Em1_2 { get; set; }

    public uint Em1_3 { get; set; }

    public uint Em1_4 { get; set; }

    public uint Em1_5 { get; set; }

    public string Text2_0 { get; set; } = string.Empty;

    public string Text2_1 { get; set; } = string.Empty;

    public uint Lang2 { get; set; }

    public float Prob2 { get; set; }

    public uint Em2_0 { get; set; }

    public uint Em2_1 { get; set; }

    public uint Em2_2 { get; set; }

    public uint Em2_3 { get; set; }

    public uint Em2_4 { get; set; }

    public uint Em2_5 { get; set; }

    public string Text3_0 { get; set; } = string.Empty;

    public string Text3_1 { get; set; } = string.Empty;

    public uint Lang3 { get; set; }

    public float Prob3 { get; set; }

    public uint Em3_0 { get; set; }

    public uint Em3_1 { get; set; }

    public uint Em3_2 { get; set; }

    public uint Em3_3 { get; set; }

    public uint Em3_4 { get; set; }

    public uint Em3_5 { get; set; }

    public string Text4_0 { get; set; } = string.Empty;

    public string Text4_1 { get; set; } = string.Empty;

    public uint Lang4 { get; set; }

    public float Prob4 { get; set; }

    public uint Em4_0 { get; set; }

    public uint Em4_1 { get; set; }

    public uint Em4_2 { get; set; }

    public uint Em4_3 { get; set; }

    public uint Em4_4 { get; set; }

    public uint Em4_5 { get; set; }

    public string Text5_0 { get; set; } = string.Empty;

    public string Text5_1 { get; set; } = string.Empty;

    public uint Lang5 { get; set; }

    public float Prob5 { get; set; }

    public uint Em5_0 { get; set; }

    public uint Em5_1 { get; set; }

    public uint Em5_2 { get; set; }

    public uint Em5_3 { get; set; }

    public uint Em5_4 { get; set; }

    public uint Em5_5 { get; set; }

    public string Text6_0 { get; set; } = string.Empty;

    public string Text6_1 { get; set; } = string.Empty;

    public uint Lang6 { get; set; }

    public float Prob6 { get; set; }

    public uint Em6_0 { get; set; }

    public uint Em6_1 { get; set; }

    public uint Em6_2 { get; set; }

    public uint Em6_3 { get; set; }

    public uint Em6_4 { get; set; }

    public uint Em6_5 { get; set; }

    public string Text7_0 { get; set; } = string.Empty;

    public string Text7_1 { get; set; } = string.Empty;

    public uint Lang7 { get; set; }

    public float Prob7 { get; set; }

    public uint Em7_0 { get; set; }

    public uint Em7_1 { get; set; }

    public uint Em7_2 { get; set; }

    public uint Em7_3 { get; set; }

    public uint Em7_4 { get; set; }

    public uint Em7_5 { get; set; }
}
