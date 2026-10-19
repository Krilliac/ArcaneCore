namespace ArcaneCore.Atlas.Render;

/// <summary>
/// An original 5x7 bitmap font for the printable ASCII range 32 to 126, authored here for the HUD (no font file, no
/// third-party glyph data). Each glyph is seven rows of five pixels; lowercase letters sit in rows 2 to 6, and the
/// letters with descenders (g, j, p, q, y) are drawn one row higher with the tail in the last row. Characters outside
/// the range draw as <c>?</c>. Text is drawn at an integer scale: every font pixel becomes a scale x scale block, and the
/// cell advance is six font pixels (five glyph columns plus one blank).
/// </summary>
internal static class BitmapFont
{
    /// <summary>First character the font has a glyph for (space).</summary>
    public const char First = ' ';

    /// <summary>Last character the font has a glyph for (tilde).</summary>
    public const char Last = '~';

    /// <summary>Glyph width in font pixels.</summary>
    public const int GlyphWidth = 5;

    /// <summary>Glyph height in font pixels.</summary>
    public const int GlyphHeight = 7;

    /// <summary>Horizontal advance per character in font pixels.</summary>
    public const int Advance = GlyphWidth + 1;

    /// <summary>Vertical advance per text line in font pixels.</summary>
    public const int LineAdvance = GlyphHeight + 2;

    /// <summary>
    /// The glyph rows, indexed by <c>character - ' '</c>. Seven rows of five characters joined by '/', '#' for a lit pixel
    /// and '.' for an unlit one. The character each entry draws is in the comment.
    /// </summary>
    private static readonly string[] Glyphs =
    [
        "...../...../...../...../...../...../.....", // ' '
        "..#../..#../..#../..#../..#../...../..#..", // '!'
        ".#.#./.#.#./.#.#./...../...../...../.....", // '"'
        ".#.#./.#.#./#####/.#.#./#####/.#.#./.#.#.", // '#'
        "..#../.####/#.#../.###./..#.#/####./..#..", // '$'
        "##..#/##.#./...#./..#../.#.../.#.##/#..##", // '%'
        ".##../#..#./#.#../.#.../#.#.#/#..#./.##.#", // '&'
        "..#../..#../.#.../...../...../...../.....", // '\''
        "...#./..#../.#.../.#.../.#.../..#../...#.", // '('
        ".#.../..#../...#./...#./...#./..#../.#...", // ')'
        "...../#.#.#/.###./#####/.###./#.#.#/.....", // '*'
        "...../..#../..#../#####/..#../..#../.....", // '+'
        "...../...../...../...../..##./..#../.#...", // ','
        "...../...../...../#####/...../...../.....", // '-'
        "...../...../...../...../...../.##../.##..", // '.'
        "....#/...#./...#./..#../.#.../.#.../#....", // '/'
        ".###./#...#/#..##/#.#.#/##..#/#...#/.###.", // '0'
        "..#../.##../..#../..#../..#../..#../.###.", // '1'
        ".###./#...#/....#/...#./..#../.#.../#####", // '2'
        ".###./#...#/....#/..##./....#/#...#/.###.", // '3'
        "...#./..##./.#.#./#..#./#####/...#./...#.", // '4'
        "#####/#..../####./....#/....#/#...#/.###.", // '5'
        "..##./.#.../#..../####./#...#/#...#/.###.", // '6'
        "#####/....#/...#./..#../.#.../.#.../.#...", // '7'
        ".###./#...#/#...#/.###./#...#/#...#/.###.", // '8'
        ".###./#...#/#...#/.####/....#/...#./.##..", // '9'
        "...../.##../.##../...../.##../.##../.....", // ':'
        "...../.##../.##../...../.##../..#../.#...", // ';'
        "...#./..#../.#.../#..../.#.../..#../...#.", // '<'
        "...../...../#####/...../#####/...../.....", // '='
        ".#.../..#../...#./....#/...#./..#../.#...", // '>'
        ".###./#...#/....#/...#./..#../...../..#..", // '?'
        ".###./#...#/#.###/#.#.#/#.###/#..../.###.", // '@'
        ".###./#...#/#...#/#####/#...#/#...#/#...#", // 'A'
        "####./#...#/#...#/####./#...#/#...#/####.", // 'B'
        ".###./#...#/#..../#..../#..../#...#/.###.", // 'C'
        "###../#..#./#...#/#...#/#...#/#..#./###..", // 'D'
        "#####/#..../#..../####./#..../#..../#####", // 'E'
        "#####/#..../#..../####./#..../#..../#....", // 'F'
        ".###./#...#/#..../#.###/#...#/#...#/.####", // 'G'
        "#...#/#...#/#...#/#####/#...#/#...#/#...#", // 'H'
        ".###./..#../..#../..#../..#../..#../.###.", // 'I'
        "..###/...#./...#./...#./...#./#..#./.##..", // 'J'
        "#...#/#..#./#.#../##.../#.#../#..#./#...#", // 'K'
        "#..../#..../#..../#..../#..../#..../#####", // 'L'
        "#...#/##.##/#.#.#/#.#.#/#...#/#...#/#...#", // 'M'
        "#...#/##..#/#.#.#/#..##/#...#/#...#/#...#", // 'N'
        ".###./#...#/#...#/#...#/#...#/#...#/.###.", // 'O'
        "####./#...#/#...#/####./#..../#..../#....", // 'P'
        ".###./#...#/#...#/#...#/#.#.#/#..#./.##.#", // 'Q'
        "####./#...#/#...#/####./#.#../#..#./#...#", // 'R'
        ".####/#..../#..../.###./....#/....#/####.", // 'S'
        "#####/..#../..#../..#../..#../..#../..#..", // 'T'
        "#...#/#...#/#...#/#...#/#...#/#...#/.###.", // 'U'
        "#...#/#...#/#...#/#...#/#...#/.#.#./..#..", // 'V'
        "#...#/#...#/#...#/#.#.#/#.#.#/##.##/#...#", // 'W'
        "#...#/#...#/.#.#./..#../.#.#./#...#/#...#", // 'X'
        "#...#/#...#/.#.#./..#../..#../..#../..#..", // 'Y'
        "#####/....#/...#./..#../.#.../#..../#####", // 'Z'
        ".###./.#.../.#.../.#.../.#.../.#.../.###.", // '['
        "#..../.#.../.#.../..#../...#./...#./....#", // '\\'
        ".###./...#./...#./...#./...#./...#./.###.", // ']'
        "..#../.#.#./#...#/...../...../...../.....", // '^'
        "...../...../...../...../...../...../#####", // '_'
        ".#.../..#../...#./...../...../...../.....", // '`'
        "...../...../.###./....#/.####/#...#/.####", // 'a'
        "#..../#..../####./#...#/#...#/#...#/####.", // 'b'
        "...../...../.###./#..../#..../#...#/.###.", // 'c'
        "....#/....#/.####/#...#/#...#/#...#/.####", // 'd'
        "...../...../.###./#...#/#####/#..../.###.", // 'e'
        "..##./.#.../###../.#.../.#.../.#.../.#...", // 'f'
        "...../.####/#...#/#...#/.####/....#/.###.", // 'g'
        "#..../#..../#.##./##..#/#...#/#...#/#...#", // 'h'
        "..#../...../.##../..#../..#../..#../.###.", // 'i'
        "...#./...../..##./...#./...#./#..#./.##..", // 'j'
        "#..../#..../#..#./#.#../##.../#.#../#..#.", // 'k'
        ".##../..#../..#../..#../..#../..#../.###.", // 'l'
        "...../...../##.#./#.#.#/#.#.#/#.#.#/#.#.#", // 'm'
        "...../...../#.##./##..#/#...#/#...#/#...#", // 'n'
        "...../...../.###./#...#/#...#/#...#/.###.", // 'o'
        "...../####./#...#/#...#/####./#..../#....", // 'p'
        "...../.####/#...#/#...#/.####/....#/....#", // 'q'
        "...../...../#.##./##..#/#..../#..../#....", // 'r'
        "...../...../.####/#..../.###./....#/####.", // 's'
        ".#.../.#.../###../.#.../.#.../.#.../..##.", // 't'
        "...../...../#...#/#...#/#...#/#..##/.##.#", // 'u'
        "...../...../#...#/#...#/#...#/.#.#./..#..", // 'v'
        "...../...../#...#/#...#/#.#.#/#.#.#/.#.#.", // 'w'
        "...../...../#...#/.#.#./..#../.#.#./#...#", // 'x'
        "...../#...#/#...#/#...#/.####/....#/.###.", // 'y'
        "...../...../#####/...#./..#../.#.../#####", // 'z'
        "...##/..#../..#../.#.../..#../..#../...##", // '{'
        "..#../..#../..#../..#../..#../..#../..#..", // '|'
        "##.../..#../..#../...#./..#../..#../##...", // '}'
        "...../...../.##.#/#.##./...../...../.....", // '~'
    ];

    private static readonly byte[][] Rows = Parse();

    /// <summary>True when the font has a glyph for <paramref name="c"/>.</summary>
    public static bool Has(char c) => c is >= First and <= Last;

    /// <summary>
    /// The glyph for <paramref name="c"/> as seven rows of five bits (bit 4 is the leftmost pixel);
    /// characters outside 32 to 126 give the glyph of <c>?</c>.
    /// </summary>
    public static ReadOnlySpan<byte> Glyph(char c) => Rows[Has(c) ? c - First : '?' - First];

    /// <summary>Width in pixels of <paramref name="text"/> at <paramref name="scale"/> (the last character's trailing blank column is not counted).</summary>
    public static int Measure(string text, int scale)
        => text.Length == 0 ? 0 : (((text.Length * Advance) - 1) * scale);

    /// <summary>
    /// Draws <paramref name="text"/> with its top-left corner at (<paramref name="x"/>, <paramref name="y"/>), opaque,
    /// clipped to the frame; returns the x just after the last character.
    /// </summary>
    public static int Draw(Framebuffer frame, int x, int y, string text, int scale, Rgb color)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        int cursor = x;
        foreach (char c in text)
        {
            ReadOnlySpan<byte> glyph = Glyph(c);
            for (int row = 0; row < GlyphHeight; row++)
            {
                for (int column = 0; column < GlyphWidth; column++)
                {
                    if ((glyph[row] & (1 << (GlyphWidth - 1 - column))) != 0)
                    {
                        frame.FillRect(
                            cursor + (column * scale),
                            y + (row * scale),
                            cursor + ((column + 1) * scale),
                            y + ((row + 1) * scale),
                            color,
                            1.0);
                    }
                }
            }

            cursor += Advance * scale;
        }

        return cursor;
    }

    private static byte[][] Parse()
    {
        int expected = Last - First + 1;
        if (Glyphs.Length != expected)
        {
            throw new InvalidOperationException($"The bitmap font has {Glyphs.Length} glyphs; printable ASCII needs {expected}.");
        }

        var rows = new byte[expected][];
        for (int i = 0; i < expected; i++)
        {
            string[] lines = Glyphs[i].Split('/');
            if (lines.Length != GlyphHeight)
            {
                throw new InvalidOperationException($"Glyph for '{(char)(First + i)}' has {lines.Length} rows; expected {GlyphHeight}.");
            }

            rows[i] = new byte[GlyphHeight];
            for (int row = 0; row < GlyphHeight; row++)
            {
                if (lines[row].Length != GlyphWidth)
                {
                    throw new InvalidOperationException($"Glyph for '{(char)(First + i)}' row {row} is not {GlyphWidth} pixels wide.");
                }

                int bits = 0;
                foreach (char pixel in lines[row])
                {
                    bits = (bits << 1) | (pixel == '#' ? 1 : pixel == '.' ? 0 : throw new InvalidOperationException($"Glyph for '{(char)(First + i)}' has '{pixel}', expected '#' or '.'."));
                }

                rows[i][row] = (byte)bits;
            }
        }

        return rows;
    }
}
