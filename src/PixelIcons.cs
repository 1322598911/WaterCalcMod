using System.Collections.Generic;
using UnityEngine;

namespace WaterCalcMod
{
    /// <summary>
    /// 代码里画的 16×16 像素图标：不依赖游戏图集，永远不会缺图。
    /// 玩家 2026-11-13 定案：统一成游戏自己的像素风 —— 1px 线宽、外轮廓为主、一处强调色，
    /// 五个键从左到右必须一眼分得开：净水助手（净水器+水滴）/ 手册（翻开的书）/
    /// 性价比（橙 S 金币）/ 效率（时钟）/ 接水（水滴）。
    /// </summary>
    public static class PixelIcons
    {
        public const int Size = 16;

        private static readonly Color Line = new Color(0xE8 / 255f, 0xF0 / 255f, 0xF2 / 255f, 1f);
        private static readonly Color Accent = new Color(0x7F / 255f, 0xD6 / 255f, 0xD6 / 255f, 1f);
        // 金币专用暖橙：和游戏里「钱」的橙色同一挂，和青色图标区分得开。
        private static readonly Color Coin = new Color(0xF2 / 255f, 0xA3 / 255f, 0x3C / 255f, 1f);

        private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

        // '.' 透明，'#' 主色，'+' 点缀

        // 净水助手：一台净水器外壳，里面一颗水滴（和「接水」的裸水滴区分）。
        private static readonly string[] Purifier =
        {
            "................",
            "................",
            "..############..",
            "..#..........#..",
            "..#....##....#..",
            "..#...####...#..",
            "..#..##++##..#..",
            "..#..##++##..#..",
            "..#...####...#..",
            "..#....##....#..",
            "..#..........#..",
            "..#.##....##.#..",
            "..############..",
            "................",
            "................",
            "................",
        };

        // 手册：翻开的书，中间装订线，两页各三道字。
        private static readonly string[] Book =
        {
            "................",
            "................",
            "................",
            "...####..####...",
            "..#....##....#..",
            "..#.##.##.##.#..",
            "..#....##....#..",
            "..#.##.##.##.#..",
            "..#....##....#..",
            "..######.#####..",
            ".....##.##......",
            "................",
            "................",
            "................",
            "................",
            "................",
        };

        // 性价比：硬币 + ¥（玩家 2026-11-13 第六轮：中间不能是「9」那种 S，$ 或 ￥ 都行）。
        // 外圈与时钟同款 10×10 圆表盘；整体比原来下移一格（玩家：图标有点太上了）。
        private static readonly string[] CoinIcon =
        {
            "................",
            "................",
            "................",
            ".....######.....",
            "...##......##...",
            "...#........#...",
            "...#..+...+.#...",
            "...#...+.+..#...",
            "...#..+++++.#...",
            "...#....+...#...",
            "...#..+++++.#...",
            "...##......##...",
            ".....######.....",
            "................",
            "................",
            "................",            "................",
        };

        // 效率：时钟（表盘 + 12 点短针 + 3 点强调分针）。
        // 玩家 2026-11-13：「时间不好看」—— 旧图标是个圆角方块里一个点，看不出是钟；
        // 这里重画成圆表盘，白描边 + 青色指针；再按玩家要求收成 10 宽 × 10 高。
        private static readonly string[] Clock =
        {
            "................",
            "................",
            "................",
            ".....######.....",
            "...##......##...",
            "...#........#...",
            "...#...##...#...",
            "...#...##...#...",
            "...#...##++.#...",
            "...#....++..#...",
            "...#........#...",
            "...##......##...",
            ".....######.....",
            "................",
            "................",
            "................",
        };

        // 接水：💧 那种实心水滴（尖顶 + 圆底，白色描边 + 青色水体）。
        // 玩家 2026-11-13（第四轮）：水滴太大、不像水滴 —— 收成 8 宽 × 11 高，
        // 顶上一根细尖，下面才是圆肚，和书/金币/时钟排一行时不再顶格。
        private static readonly string[] Water =
        {
            "................",
            "................",
            ".......##.......",
            "......#++#......",
            "......#++#......",
            "......#++#......",
            ".....#++++#.....",
            ".....#++++#.....",
            "....#++++++#....",
            "....#++++++#....",
            "....#++++++#....",
            ".....#++++#.....",
            "......#++#......",
            ".......##.......",
            "................",
            "................",
        };

        // 计算：小计算器/网格（计算页内部还在用）。
        private static readonly string[] Calc =
        {
            "................",
            "....########....",
            "...#........#...",
            "...#..++++..#...",
            "...#........#...",
            "...#.##..##.#...",
            "...#.##..##.#...",
            "...#........#...",
            "...#.##..##.#...",
            "...#.##..##.#...",
            "...#........#...",
            "....########....",
            "................",
            "................",
            "................",
            "................",
        };

        private static readonly string[] Info =
        {
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

        // 多台净水器「点选这台」：圆圈里一个实心点。
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

        public static Sprite Get(string name, out int w, out int h)
        {
            w = Size; h = Size;
            Sprite sp;
            if (cache.TryGetValue(name, out sp) && sp != null) return sp;

            string[] rows = RowsFor(name);
            if (rows == null) return null;
            Color accent = AccentFor(name, Accent);

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
                        Color col = c == '#' ? Line : (c == '+' ? accent : new Color(0, 0, 0, 0));
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

        /// <summary>同图案换色：标题栏每个快捷键一个颜色，16px 下也能一眼区分。</summary>
        public static Sprite Get(string name, Color line, Color accent, out int w, out int h)
        {
            w = Size; h = Size;
            string key = name + "#" + ColorUtility.ToHtmlStringRGB(line) + "#" + ColorUtility.ToHtmlStringRGB(accent);
            Sprite sp;
            if (cache.TryGetValue(key, out sp) && sp != null) return sp;
            string[] rows = RowsFor(name);
            if (rows == null) return null;
            try
            {
                var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                var px = new Color32[Size * Size];
                for (int y = 0; y < Size; y++)
                {
                    string row = rows[Size - 1 - y];
                    for (int x = 0; x < Size; x++)
                    {
                        char c = x < row.Length ? row[x] : '.';
                        Color col = c == '#' ? line : (c == '+' ? accent : new Color(0, 0, 0, 0));
                        px[y * Size + x] = col;
                    }
                }
                tex.SetPixels32(px);
                tex.Apply(false, false);
                sp = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f, 0,
                                   SpriteMeshType.FullRect);
                sp.name = "wc_" + key;
                cache[key] = sp;
                return sp;
            }
            catch (System.Exception e)
            {
                PurifierUi.LogQuiet("[icon] tint missing " + name + " " + e.GetType().Name);
                return null;
            }
        }

        private static Color AccentFor(string name, Color def)
        {
            if (name == "coin" || name == "money" || name == "tag") return Coin;
            return def;
        }

        private static string[] RowsFor(string name)
        {
            switch (name)
            {
                case "water": return Water;
                case "book": return Book;
                case "note": return Book;
                case "coin": return CoinIcon;
                case "money": return CoinIcon;
                case "tag": return CoinIcon;
                case "clock": return Clock;
                case "bolt": return Clock;
                case "purifier": return Purifier;
                case "info": return Info;
                case "gear": return Gear;
                case "serve": return Serve;
                case "pick": return Pick;
                case "calc": return Calc;
                default: return null;
            }
        }
    }
}
