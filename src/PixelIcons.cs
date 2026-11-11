using System.Collections.Generic;
using UnityEngine;

namespace WaterCalcMod
{
    /// <summary>
    /// 代码里画的 16×16 像素图标：不依赖游戏图集，永远不会缺图。
    /// 线条 1px，留 1px 内边距；主色 #E8F0F2，点缀 #7FD6D6，其余透明。
    /// </summary>
    public static class PixelIcons
    {
        public const int Size = 16;

        private static readonly Color Line = new Color(0xE8 / 255f, 0xF0 / 255f, 0xF2 / 255f, 1f);
        private static readonly Color Accent = new Color(0x7F / 255f, 0xD6 / 255f, 0xD6 / 255f, 1f);

        private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

        // '.' 透明，'#' 主色，'+' 点缀
        private static readonly string[] Water =
        {
            "................",
            ".......##.......",
            "......####......",
            "......####......",
            ".....######.....",
            ".....######.....",
            "....########....",
            "....##++++##....",
            "...###++++###...",
            "...##++##++##...",
            "...##++##++##...",
            "....##++++##....",
            ".....######.....",
            "......####......",
            "................",
            "................",
        };

        private static readonly string[] Note =
        {
            "................",
            "................",
            "..############..",
            "..#..........#..",
            "..#.########.#..",
            "..#..........#..",
            "..#.########.#..",
            "..#..........#..",
            "..#.########.#..",
            "..#..........#..",
            "..#.######...#..",
            "..#..........#..",
            "..############..",
            "................",
            "................",
            "................",
        };

        private static readonly string[] Serve =
        {
            "................",
            "................",
            "..#..........#..",
            "..#..........#..",
            "..#....++....#..",
            "..#...++++...#..",
            "..#..++++++..#..",
            "..#..++++++..#..",
            "..#...++++...#..",
            "..#..........#..",
            "..#..........#..",
            "..############..",
            "..############..",
            "................",
            "................",
            "................",
        };

        // 多台净水器「点选这台」：一个圆圈里一个实心点，选中染绿、没选中的染灰（同图不同色）。
        private static readonly string[] Pick =
        {
            "................",
            "................",
            ".....######.....",
            "....##....##....",
            "...#..####..#...",
            "...#.######.#...",
            "..#..######..#..",
            "..#..######..#..",
            "..#..######..#..",
            "...#.######.#...",
            "...#..####..#...",
            "....##....##....",
            ".....######.....",
            "................",
            "................",
            "................",
        };

        private static readonly string[] Info =        {
            "................",
            "......####......",
            "....##....##....",
            "...#..####..#...",
            "...#..####..#...",
            "..#....##.....#.",
            "..#....##.....#.",
            "..#....##.....#.",
            "..#....##.....#.",
            "..#....##.....#.",
            "..#...####....#.",
            "...#..####...#..",
            "....##....##....",
            "......####......",
            "................",
            "................",
        };

        private static readonly string[] Gear =
        {
            "................",
            "......####......",
            "...#..####..#...",
            "..##..####..##..",
            "...##########...",
            "..############..",
            "..####++++####..",
            "####++####++####",
            "####++####++####",
            "..####++++####..",
            "..############..",
            "...##########...",
            "..##..####..##..",
            "...#..####..#...",
            "......####......",
            "................",
        };

        public static Sprite Get(string name, out int w, out int h)        {
            w = Size; h = Size;
            Sprite sp;
            if (cache.TryGetValue(name, out sp) && sp != null) return sp;

            string[] rows = null;
            switch (name)
            {
                case "water": rows = Water; break;
                case "note": rows = Note; break;
                case "info": rows = Info; break;
                case "gear": rows = Gear; break;
                case "serve": rows = Serve; break;
                case "pick": rows = Pick; break;
            }
            if (rows == null) return null;

            try
            {
                var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                var px = new Color32[Size * Size];
                for (int y = 0; y < Size; y++)
                {
                    // 数组第 0 行是顶部，纹理坐标原点在左下，所以翻转。
                    string row = rows[Size - 1 - y];
                    for (int x = 0; x < Size; x++)
                    {
                        char c = x < row.Length ? row[x] : '.';
                        Color col = c == '#' ? Line : (c == '+' ? Accent : new Color(0, 0, 0, 0));
                        px[y * Size + x] = col;
                    }
                }
                tex.SetPixels32(px);
                tex.Apply(false, false);
                sp = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f, 0,
                                   SpriteMeshType.FullRect);
                sp.name = "wc_" + name;
                cache[name] = sp;
                return sp;
            }
            catch (System.Exception e)
            {
                PurifierUi.LogQuiet("[icon] missing " + name + " " + e.GetType().Name);
                return null;
            }
        }
    }
}
