using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace WaterCalcMod
{
    /// <summary>直接读存档里的液体容器（ES3 明文 JSON，按 _keys/_values 标签对解析）。</summary>
    public static class SaveReader
    {
        public class Ctr
        {
            public string name;
            public long cap;    // μL
            public long total;  // μL
            public long water;  // μL
            public int pur { get { return total > 0 ? (int)(water * 10000 / total) : 0; } }
            public long Imp { get { return total - water; } }
        }

#if DEV
        public static string SaveDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) +
                                    "Low", @"Questing Goose Studio\Probably Stolen");
            }
        }

        public static List<Ctr> Load()
        {
            var list = new List<Ctr>();
            try
            {
                if (!System.IO.Directory.Exists(SaveDir)) return list;
                var files = System.IO.Directory.GetFiles(SaveDir, "save_*.es3");
                Array.Sort(files, (a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
                foreach (var f in files)
                {
                    var got = Parse(File.ReadAllText(f));
                    if (got.Count > 0) { list = got; break; }
                }
            }
            catch (Exception) { }
            return list;
        }

        private static List<Ctr> Parse(string raw)
        {
            var res = new List<Ctr>();
            string t = raw.Replace("\\\"", "\"");
            var chunks = t.Split(new[] { "\"_keys\":[" }, StringSplitOptions.None);
            var nameRe = new Regex("\"name\":\"([^\"]*)\"");
            for (int i = 1; i < chunks.Length; i++)
            {
                string pre = chunks[i - 1], body = chunks[i];
                if (!body.Contains("LIQUID_CONTAINER_TAG")) continue;
                string nm = null;
                foreach (Match m in nameRe.Matches(pre)) nm = m.Groups[1].Value;
                long cap = Get(body, "LIQUID_CONTAINER_CAPACITY");
                long water = Get(body, "CURRENT_PART_WATER");
                long imp = 0;
                foreach (var k in new[] { "HEAVY_METAL", "ORGANIC_WASTE", "MICROBE", "CHEMICAL_CONTAMINANT", "PHYSICAL_CONTAMINANT", "MINERAL", "MICROPLASTIC" })
                    imp += Get(body, "CURRENT_PART_" + k);
                long tot = water + imp;
                if (tot <= 0) continue;
                res.Add(new Ctr { name = nm ?? "容器", cap = cap, total = tot, water = water });
            }
            return res;
        }

        private static long Get(string body, string key)
        {
            var m = Regex.Match(body, "<identifier>k__BackingField\":\"" + key + "\"[^}]*?\"valueInt\":(-?\\d+)");
            if (!m.Success) return 0;
            long v;
            return long.TryParse(m.Groups[1].Value, out v) ? v : 0;
        }
#else
        /// <summary>发布版不读存档（清单不声明 files 能力）：返回空表，界面回落到游戏内实时数据。</summary>
        public static List<Ctr> Load() { return new List<Ctr>(); }
#endif

        /// <summary>把 best 好水兑进 target，让 target 到 9900；返回需要的好水 μL，-1 表示兑不进。</summary>
        public static long NeedGood(Ctr good, Ctr target, int wantPur = 9900)
        {
            if (good == null || target == null || good.total <= 0) return -1;
            double iT = (10000.0 - wantPur) / 10000.0;
            double iG = (double)good.Imp / good.total;
            double iB = (double)target.Imp / target.total;
            if (iB <= iT) return 0;
            if (iG >= iT) return -1;
            double x = (iT * target.total - target.Imp) / (iT - iG);
            if (x < 0) return 0;
            if (target.total + x > target.cap) return -2; // 装不下
            return (long)Math.Ceiling(x);
        }
    }
}

