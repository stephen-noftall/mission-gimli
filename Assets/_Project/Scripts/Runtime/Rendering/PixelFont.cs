using System.Collections.Generic;
using UnityEngine;

namespace MoonBaseAlpha.Rendering
{
    /// <summary>Tiny 3x5 pixel font for procedural textures (cockpit screens, stencils).</summary>
    public static class PixelFont
    {
        // rows top to bottom, 3 bits each
        static readonly Dictionary<char, string> Glyphs = new()
        {
            ['0'] = "111101101101111", ['1'] = "010110010010111", ['2'] = "111001111100111", ['3'] = "111001111001111",
            ['4'] = "101101111001001", ['5'] = "111100111001111", ['6'] = "111100111101111", ['7'] = "111001010010010",
            ['8'] = "111101111101111", ['9'] = "111101111001111", ['A'] = "010101111101101", ['B'] = "110101110101110",
            ['C'] = "011100100100011", ['D'] = "110101101101110", ['E'] = "111100110100111", ['F'] = "111100110100100",
            ['G'] = "011100101101011", ['H'] = "101101111101101", ['I'] = "111010010010111", ['J'] = "001001001101010",
            ['K'] = "101101110101101", ['L'] = "100100100100111", ['M'] = "101111111101101", ['N'] = "110101101101101",
            ['O'] = "010101101101010", ['P'] = "110101110100100", ['Q'] = "010101101110011", ['R'] = "110101110101101",
            ['S'] = "011100010001110", ['T'] = "111010010010010", ['U'] = "101101101101111", ['V'] = "101101101101010",
            ['W'] = "101101111111101", ['X'] = "101101010101101", ['Y'] = "101101010010010", ['Z'] = "111001010100111",
            ['.'] = "000000000000010", ['%'] = "101001010100101", [':'] = "000010000010000", ['/'] = "001001010100100",
            ['-'] = "000000111000000", ['+'] = "000010111010000", [' '] = "000000000000000",
        };

        /// <summary>Text with its bottom-left corner at (x, y) in a w x h pixel buffer, pixel scale s.</summary>
        public static void Draw(Color32[] p, int w, int h, int x, int y, string s, Color32 c, int scale = 1)
        {
            foreach (char ch0 in s)
            {
                if (Glyphs.TryGetValue(char.ToUpperInvariant(ch0), out var g))
                    for (int r = 0; r < 5; r++) for (int col = 0; col < 3; col++)
                        if (g[r * 3 + col] == '1')
                            for (int sy = 0; sy < scale; sy++) for (int sx = 0; sx < scale; sx++)
                            {
                                int px = x + col * scale + sx, py = y + (4 - r) * scale + sy;
                                if (px >= 0 && py >= 0 && px < w && py < h) p[py * w + px] = c;
                            }
                x += 4 * scale;
            }
        }

        public static int Width(string s, int scale = 1) => s.Length * 4 * scale - scale;
    }
}
