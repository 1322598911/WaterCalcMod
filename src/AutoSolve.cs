using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace WaterCalcMod
{
    /// <summary>
    /// 批次 25：把网页 V1.0 的自动方案（beam search）搬进 MOD。
    /// 只消费 WaterMath 的现有工具，不复制公式，也不改 WaterPlan / WaterMath。
    /// </summary>
    public static class AutoSolve
    {
        public class Step
        {
            public string tool;
            public int dura;
            public int count = 1;
            public string label;
            public long volUl;      // 这一步之后的体积
            public double purity;   // 这一步之后的显示纯度（PurityShown）
        }

        public class Result
        {
            public WaterMath.Water w;
            public List<Step> steps = new List<Step>();
            public List<Step> rawSteps = new List<Step>();
            public int tablets, nights, filterDura, hydraDura;
            public double cost, revenue, profit, perL, daily;
            public bool ok;
            public string grade;
            public double purityShown;
            public long feedUl;
        }

        // V1.0 的对照常数（页面上的 225 / 400）。
        // 游戏真值是 325 / 300；界面启用游戏真值前由调用方改这两个静态值即可。
        public static double HydraPrice = 225.0;
        public static int HydraDuraMax = 400;
        public static double ElectricityPerNight = 0.0;
        /// <summary>时间效率成本（元/晚）。0 晚方案没有这一项；用来避免为了省耗材硬拖净化器过夜。</summary>
        public static double TimeValuePerNight = 0.0;
        public static long LastSearchMs = 0;
        // -1 = 自动，不限制；0 = 不放药片；N = 总片数必须正好 N
        public static int TabletTarget = -1;
        /// <summary>药片影子价（元/片）：自动档用来衡量“这片药留着以后用”的价值。
        /// 0 = 不启用；只在 TabletTarget = -1 且性价比自动档时由界面写入。</summary>
        public static double TabletShadow = 0.0;
        /// <summary>设置页“不用机器”时由界面置 false：净化器/过夜节点不参与搜索。</summary>
        public static bool AllowMachine = true;

        /// <summary>批次 28 的界面口径：海德拉 325/300、电 1.5/晚。对照脚本不要调用。</summary>
        public static void UseUiCost()
        {
            HydraPrice = 325.0;
            HydraDuraMax = 300;
            ElectricityPerNight = 1.5;
            // 玩家 2026-11-16：0 晚也是方案；不能让“少用一片药/一次过滤”的过夜方案
            // 只按电费 1.5 元算。这里把一晚的时间效率成本算进去（电费 1.5 + 时间 8.5 = 10 元/晚）。
            TimeValuePerNight = 8.5;
        }

        private const double WaterPriceL = 40.0;
        private const double FilterPrice = 25.0;
        private const int FilterDuraMaxV1 = 400;
        private const double TabletPrice = 6.0;

        private static readonly string[] ToolOrder =
        {
            "uv", "filter", "portable", "tablet", "purifier"
        };

        private class Node
        {
            public WaterMath.Water w;
            public List<Step> plan = new List<Step>();
            public int tablets;
            public int nights;
            public int filterDura;
            public int hydraDura;
            public double p;
        }

        /// <summary>与 V1.0 同口径的目标纯度比较值（两位截断，不是四舍五入）。</summary>
        public static double PurityShown(WaterMath.Water w)
        {
            if (w == null || w.v <= 0) return 0.0;
            double p = w.WaterPart * 100.0 / w.v;
            double rounded = Math.Floor(p * 1000000.0 + 0.5);   // JS Math.round 对正数 = floor(x+0.5)
            return Math.Truncate(rounded / 10000.0) / 100.0;
        }

        private static string GradeName(double shown)
        {
            if (shown < 90) return "沟渠水";
            if (shown < 96) return "铁锈水";
            if (shown < 98) return "幽灵水";
            if (shown < 99) return "基础水";
            if (shown < 99.9) return "优质水";
            return "纯水";
        }

        private static double GradeMult(string grade)
        {
            if (grade == "纯水") return 2.0;
            if (grade == "优质水") return 1.5;
            if (grade == "基础水") return 1.0;
            return 0.0;
        }

        private static string LabelOf(string tool)
        {
            if (tool == "uv") return "紫外线灯";
            if (tool == "filter") return "滤水器";
            if (tool == "portable") return "海德拉";
            if (tool == "tablet") return "净水药片";
            if (tool == "purifier") return "净水器";
            return tool;
        }

        private static Node Start(long waterUl, int purityBp)
        {
            // WaterMath.Build 的六项分摊就是 FillCalcImpFromPurity 用的同一套 2:4:3:6:2:3。
            // cap=0：V1 的自动方案不按容器满瓶挤出药片氯，避免把“药片治疗量”算成溢出。
            var w = WaterMath.Build(waterUl, purityBp, 0);
            w.cap = 0;
            var n = new Node();
            n.w = w;
            n.p = PurityShown(w);
            return n;
        }

        private static bool TabletCountOk(Node n)
        {
            return TabletTarget < 0 || n.tablets == TabletTarget;
        }

        private static Node Apply(Node from, string tool, double perf, double quality,
                                  int filterBudgetLeft, int tabletStock, int hydraDuraMax)
        {
            var w = from.w.Clone();
            w.cap = 0;
            int dura = 0;
            if (tool == "uv")
            {
                WaterMath.Uv(w, 1);
            }
            else if (tool == "filter")
            {
                // 先用足够大的耐久问“这步本来要多少”，再用剩余预算真跑；
                // 这样 saveFilter 的整条路线上限不会被 WaterMath.Filter 的部分过滤悄悄绕过。
                var probe = w.Clone();
                int need = WaterMath.Filter(probe, int.MaxValue);
                if (need <= 0) return null;
                if (filterBudgetLeft > 0 && need > filterBudgetLeft) return null;
                dura = WaterMath.Filter(w, filterBudgetLeft > 0 ? filterBudgetLeft : int.MaxValue);
                if (dura <= 0) return null;
            }
            else if (tool == "portable")
            {
                // 批次 29：单只海德拉只能用 hydraDuraMax 点；超预算的路线在这里直接砍掉，
                // 不能再让 BuildResult 事后按「多买几只」把成本补回来。
                int need = WaterMath.HydraDurability(w);
                if (need <= 0) return null;
                int left = hydraDuraMax > 0 ? hydraDuraMax - from.hydraDura : int.MaxValue;
                if (left <= 0 || need > left) return null;
                dura = WaterMath.Hydra(w);
                if (dura <= 0) return null;
            }
            else if (tool == "tablet")
            {
                if (TabletTarget >= 0 && from.tablets >= TabletTarget) return null;
                if (tabletStock >= 0 && from.tablets >= tabletStock) return null;
                // 药片在机器之后继续清化学/微生物；不能再被机器上限压回 99.01。
                long removed = WaterMath.Tablet(w, 1, 0);
                if (removed <= 0 && w.v == from.w.v && w.Impurity >= from.w.Impurity) return null;
            }
            else if (tool == "purifier")
            {
                long removed = WaterMath.Purify(w, perf, quality);
                if (removed <= 0) return null;
            }
            else return null;

            double p = PurityShown(w);
            long oldImp = from.w.Impurity;
            long newImp = w.Impurity;
            if (newImp >= oldImp - 1e-9) return null;    // 杂质没减少 → 这步没用
            if (p <= from.p) return null;                // 显示纯度没提升 → 这步没用

            var n = new Node();
            n.w = w;
            n.plan = new List<Step>(from.plan);
            var s = new Step();
            s.tool = tool;
            s.dura = dura;
            s.label = LabelOf(tool);
            s.volUl = w.v;
            s.purity = p;
            n.plan.Add(s);
            n.tablets = from.tablets + (tool == "tablet" ? 1 : 0);
            n.nights = from.nights + (tool == "purifier" ? 1 : 0);
            n.filterDura = from.filterDura + (tool == "filter" ? dura : 0);
            n.hydraDura = from.hydraDura + (tool == "portable" ? dura : 0);
            n.p = p;
            return n;
        }

        private static bool Better(Node a, Node b, double capPct, bool pureMode)
        {
            if (a == null) return false;
            if (b == null) return true;
            double pa = Math.Min(a.p, capPct);
            double pb = Math.Min(b.p, capPct);
            if (pa != pb) return pa > pb;
            if (a.w.WaterPart != b.w.WaterPart) return a.w.WaterPart > b.w.WaterPart;
            if (a.filterDura != b.filterDura) return a.filterDura < b.filterDura;
            if (a.plan.Count != b.plan.Count) return a.plan.Count < b.plan.Count;
            return a.p > b.p;
        }

        private static int Cmp(Node a, Node b, double capPct, bool pureMode)
        {
            if (Better(a, b, capPct, pureMode)) return -1;
            if (Better(b, a, capPct, pureMode)) return 1;
            return 0;
        }

        private static string Key(Node n)
        {
            var sb = new StringBuilder();
            sb.Append(n.p.ToString("F2")).Append('|').Append(n.w.WaterPart).Append('|');
            sb.Append(n.w.Part("heavy")).Append(',').Append(n.w.Part("organic")).Append(',')
              .Append(n.w.Part("microbe")).Append(',').Append(n.w.Part("chemical")).Append(',')
              .Append(n.w.Part("physical")).Append(',').Append(n.w.Part("mineral")).Append('|');
            sb.Append(n.filterDura);
            return sb.ToString();
        }

        /// <summary>V1.0 的相邻 filter→uv 交换；两者成分不相交，不影响结果。</summary>
        private static List<Step> CanonicalOrder(List<Step> list)
        {
            var outList = new List<Step>(list);
            for (int i = 0; i < outList.Count - 1; i++)
            {
                if (outList[i].tool == "filter" && outList[i + 1].tool == "uv")
                {
                    var t = outList[i];
                    outList[i] = outList[i + 1];
                    outList[i + 1] = t;
                }
            }
            return outList;
        }

        /// <summary>相邻 tablet / purifier 合并成 count；其余保持独立步骤。</summary>
        private static List<Step> MergeCounts(List<Step> list)
        {
            var outList = new List<Step>();
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                var prev = outList.Count > 0 ? outList[outList.Count - 1] : null;
                bool countable = s.tool == "tablet" || s.tool == "purifier";
                if (countable && prev != null && prev.tool == s.tool)
                {
                    prev.count++;
                    // 合并后的节点要保留最后一片/最后一晚之后的快照，步骤页/主页都按它显示最终体积与纯度。
                    prev.volUl = s.volUl;
                    prev.purity = s.purity;
                    continue;
                }
                var c = new Step();
                c.tool = s.tool;
                c.dura = s.dura;
                c.label = s.label;
                c.count = countable ? 1 : 1;
                c.volUl = s.volUl;
                c.purity = s.purity;
                outList.Add(c);
            }
            return outList;
        }

        private static Result BuildResult(Node best, int targetBp)
        {
            var r = new Result();
            r.w = best.w;
            // 原始顺序不合并、不换序：步骤页逐条显示，药片一片一条。
            r.rawSteps = new List<Step>(best.plan);
            r.steps = MergeCounts(CanonicalOrder(best.plan));
            for (int i = 0; i < r.steps.Count; i++)
            {
                var s = r.steps[i];
                if (s.tool == "tablet") r.tablets += s.count;
                if (s.tool == "purifier") r.nights += s.count;
                if (s.tool == "filter") r.filterDura += s.dura;
                if (s.tool == "portable") r.hydraDura += s.dura;
            }
            r.purityShown = PurityShown(best.w);
            r.grade = GradeName(r.purityShown);
            double liters = best.w.v / 1000000.0;
            r.revenue = liters * WaterPriceL * GradeMult(r.grade);
            r.cost = r.filterDura * (FilterPrice / FilterDuraMaxV1)
                   + r.tablets * TabletPrice
                   + r.hydraDura * (HydraPrice / Math.Max(1, HydraDuraMax));
            r.profit = r.revenue - r.cost;
            r.perL = liters > 0 ? r.cost / liters : 0;
            r.daily = r.profit / Math.Max(1, r.nights);
            r.ok = best.w.Purity >= targetBp && TabletCountOk(best);
            return r;
        }

        /// <summary>毛收入 − 耗材 − 电费（不含药片影子价）。</summary>
        private static double Net(Result r)
        {
            if (r == null) return double.MinValue;
            return r.revenue - r.cost - (ElectricityPerNight + TimeValuePerNight) * r.nights;
        }

        /// <summary>净赚 − 药片影子价；只用于同一进水量下的药片档比较。</summary>
        private static double ShadowScore(Result r)
        {
            if (r == null) return double.MinValue;
            return Net(r) - TabletShadow * r.tablets;
        }

        private static bool BetterShadow(Result a, Result b)
        {
            if (a == null) return false;
            if (b == null) return true;
            double sa = ShadowScore(a), sb = ShadowScore(b);
            if (sa != sb) return sa > sb;
            if (a.w.v != b.w.v) return a.w.v > b.w.v;
            if (a.nights != b.nights) return a.nights < b.nights;
            return a.tablets < b.tablets;
        }

        /// <summary>最终选择：0 晚硬优先；0 晚内部再按 Net（含电费+时间效率成本）比较。</summary>
        private static bool BetterFinal(Result a, Result b)
        {
            if (a == null || !a.ok) return false;
            if (b == null || !b.ok) return true;
            // 玩家 2026-11-17：只要 0 晚方案达标，就不允许被 1 晚以上的方案顶掉。
            // 旧版先比 Net，净化器多跑一晚带来的水量/毛利会把 0 晚方案压下去，
            // 导致第二天刷新后同一瓶水从「直接 2 药」变成「净化器 + 过夜」。
            if (a.nights == 0 && b.nights > 0) return true;
            if (b.nights == 0 && a.nights > 0) return false;
            double sa = Net(a), sb = Net(b);
            if (sa != sb) return sa > sb;
            if (a.w.v != b.w.v) return a.w.v > b.w.v;
            if (a.nights != b.nights) return a.nights < b.nights;
            return a.tablets < b.tablets;
        }

        /// <summary>
        /// 自动方案入口。nightCap ≤ 0 表示不限制；tabletStock &lt; 0 表示不限；filterDurMax ≤ 0 表示不限。
        /// </summary>
        public static Result Solve(long waterUl, int purityBp, double perf, double quality,
                                   int targetBp, int nightCap, bool allowFilter, bool allowUv,
                                   bool allowTablet, bool allowHydra, int tabletStock, int filterDurMax,
                                   int hydraDuraMax)
        {
            if (waterUl <= 0) return null;
            return SolveFrom(Start(waterUl, purityBp), perf, quality, targetBp, nightCap,
                             allowFilter, allowUv, allowTablet, allowHydra, tabletStock, filterDurMax, hydraDuraMax);
        }

        /// <summary>
        /// 瓶里已经有真实水样：直接拿读到的六项 μL 进搜索，不再用 Build 估。
        /// 这是批次 27 §2 的真实瓶水入口；没有 weights 参数。
        /// </summary>
        public static Result SolveWater(WaterMath.Water water0, double perf, double quality,
                                        int targetBp, int nightCap, bool allowFilter, bool allowUv,
                                        bool allowTablet, bool allowHydra, int tabletStock, int filterDurMax,
                                        int hydraDuraMax)
        {
            if (water0 == null || water0.v <= 0) return null;
            var w = water0.Clone();
            w.cap = 0;
            var start = new Node();
            start.w = w;
            start.p = PurityShown(w);
            return SolveFrom(start, perf, quality, targetBp, nightCap,
                             allowFilter, allowUv, allowTablet, allowHydra, tabletStock, filterDurMax, hydraDuraMax);
        }

        private static Result SolveFrom(Node start, double perf, double quality,
                                        int targetBp, int nightCap, bool allowFilter, bool allowUv,
                                        bool allowTablet, bool allowHydra, int tabletStock, int filterDurMax,
                                        int hydraDuraMax)
        {
            bool pureMode = targetBp >= 9990;             // V1 的 good/pure 分档；99.00 走 good
            double capPct = pureMode ? 100.0 : 99.0;

            if (!pureMode && start.p >= 99.0 && TabletCountOk(start))
            {
                var empty = new Result();
                empty.w = start.w;
                empty.purityShown = start.p;
                empty.grade = GradeName(start.p);
                empty.ok = start.w.Purity >= targetBp;
                empty.feedUl = start.w.v;
                return empty;
            }

            var tools = new List<string>();
            for (int i = 0; i < ToolOrder.Length; i++)
            {
                string t = ToolOrder[i];
                if (t == "uv" && !allowUv) continue;
                if (t == "filter" && !allowFilter) continue;
                if (t == "portable" && !allowHydra) continue;
                if (t == "tablet" && !allowTablet) continue;
                if (t == "purifier" && !AllowMachine) continue;
                tools.Add(t);
            }
            if (tools.Count == 0) return null;

            int maxDepth = pureMode ? 140 : 60;
            int width = 120;
            var beam = new List<Node>();
            beam.Add(start);
            Node best = start;
            Node hit = null;
            Node display = null;
            Node zeroNode = null;
            // 玩家 2026-11-16：0 晚（不过净化器）本身也是候选；
            // 旧搜索按“第一个达标路径”停在 1 晚净化器，2 步的 0 晚药片+滤棉永远轮不到。
            var zeroNightCands = new List<Node>();
            // 玩家 2026-11-15：目标已经由 0 片方案达标时，旧搜索会在 hit 后提前停，
            // 导致主页永远看不到「同进水量、同晚数、加片更赚」的候选；这里单独收集药片档，
            // 只在性价比自动档（TabletShadow>0、库存有限、TabletTarget=-1）参与影子价比较。
            var shadowCands = new List<Node>();
            int hitDepth = -1;
            int stagnant = 0;

            for (int d = 0; d < maxDepth; d++)
            {
                var cands = new List<Node>();
                var seen = new HashSet<string>();
                for (int bi = 0; bi < beam.Count; bi++)
                {
                    var node = beam[bi];
                    for (int ti = 0; ti < tools.Count; ti++)
                    {
                        string tool = tools[ti];
                        if (tool == "purifier" && nightCap > 0 && node.nights >= nightCap) continue;
                        int filterLeft = filterDurMax > 0 ? (filterDurMax - node.filterDura) : int.MaxValue;
                        var r = Apply(node, tool, perf, quality, filterLeft, tabletStock, hydraDuraMax);
                        if (r == null) continue;
                        string key = Key(r);
                        if (seen.Contains(key)) continue;
                        seen.Add(key);
                        cands.Add(r);
                    }
                }
                if (cands.Count == 0) break;
                cands.Sort(delegate(Node a, Node b) { return Cmp(a, b, capPct, pureMode); });

                // 批次 32：方案最后一步如果是药片，主页/步骤/计算页要显示加完药片后的最终纯度，
                // 不能被下面「第一次达标」的 hit 覆盖回净化器刚停时的 99.00 / 99.01。
                for (int i = 0; i < cands.Count; i++)
                {
                    var c = cands[i];
                    if (!TabletCountOk(c)) continue;
                    if (c.tablets == 0 && c.w.Purity >= targetBp
                        && (zeroNode == null || Better(c, zeroNode, capPct, pureMode)))
                        zeroNode = c;
                    // 0 晚候选不要求 0 药片：药片+滤棉也可能是不用过夜的正确解。
                    if (c.nights == 0 && c.w.Purity >= targetBp && TabletCountOk(c)
                        && zeroNightCands.Count < 200)
                        zeroNightCands.Add(c);
                    if (TabletShadow > 0 && TabletTarget < 0 && c.tablets > 0
                        && c.w.Purity >= targetBp && TabletCountOk(c)
                        && shadowCands.Count < 200)
                        shadowCands.Add(c);
                    if (c.p < 99.0) continue;
                    if (c.w.Purity < targetBp) continue;
                    if (display == null
                        || c.p > display.p
                        || (c.p == display.p && c.filterDura < display.filterDura)
                        || (c.p == display.p && c.filterDura == display.filterDura && c.w.WaterPart > display.w.WaterPart))
                        display = c;
                }

                if (!pureMode)
                {
                    var good = new List<Node>();
                    for (int i = 0; i < cands.Count; i++) if (cands[i].p >= 99.0 && TabletCountOk(cands[i])) good.Add(cands[i]);
                    if (good.Count > 0)
                    {
                        if (hit == null)
                        {
                            hit = good[0];
                            hitDepth = d;
                        }
                        else
                        {
                            // 只找“显示纯度一样”的路线里更省耐久/水更多的
                            var same = new List<Node>();
                            for (int i = 0; i < good.Count; i++) if (good[i].p == hit.p) same.Add(good[i]);
                            if (same.Count > 0 && Better(same[0], hit, capPct, pureMode)) hit = same[0];
                        }
                        if (d >= hitDepth + 3)
                        {
                            best = hit;
                            break;
                        }
                    }
                    else if (hit != null && d >= hitDepth + 3)
                    {
                        best = hit;
                        break;
                    }
                }

                beam = new List<Node>();
                for (int i = 0; i < cands.Count && i < width; i++) beam.Add(cands[i]);
                if (beam.Count == 0) break;
                if (Better(beam[0], best, capPct, pureMode))
                {
                    best = beam[0];
                    stagnant = 0;
                }
                else
                {
                    stagnant++;
                    if (stagnant >= 8) break;
                }
            }
            if (hit != null) best = hit;
            // 批次 34：达标即停。普通档（目标 99.00 一类）以第一次达标的 hit 为终点，
            // 不再让 display 追「99.28 → 99.29」这种达标后的 0.01%，否则会白加一片药。
            // 只有 99.90 以上的纯水档仍保留 display，用来显示最高可达纯度。
            Node finalNode = (pureMode && display != null) ? display : best;
            var result = BuildResult(finalNode, targetBp);
            result.feedUl = start.w.v;
            // 玩家 2026-11-15：性价比自动档按库存影子价比较“同进水量、同晚数”的 0 片档。
            // 旧逻辑只在 finalNode 已经带药片时才比较；若 0 片已达标，搜索会提前停在 0 片，
            // 主页因此看不到“加片更赚”的候选。现在把同晚数的药片候选一并纳入影子价比较。
            if (TabletShadow > 0 && TabletTarget < 0 && zeroNode != null)
            {
                var zero = BuildResult(zeroNode, targetBp);
                zero.feedUl = result.feedUl;
                if (result.tablets > 0)
                {
                    bool crossTier0 = result.grade != zero.grade && Net(result) > Net(zero);
                    if (!crossTier0 && BetterShadow(zero, result)) result = zero;
                }
                else if (zeroNode.nights == result.nights || zeroNode.nights == 0)
                {
                    Result bestT = null;
                    for (int i = 0; i < shadowCands.Count; i++)
                    {
                        var c = shadowCands[i];
                        if (c == null || c.tablets <= 0 || c.nights != zeroNode.nights) continue;
                        var t = BuildResult(c, targetBp);
                        t.feedUl = result.feedUl;
                        bool crossTier = t.grade != zero.grade && Net(t) > Net(zero);
                        if (!crossTier && !BetterShadow(t, zero)) continue;
                        if (bestT == null || BetterShadow(t, bestT)
                            || (t.grade != zero.grade && Net(t) > Net(bestT))) bestT = t;
                    }
                    if (bestT != null) result = bestT;
                }
            }
            // 0 晚候选按同一套 Net（含时间效率成本）重新比一次；不能因为净化器路径短就默认选它。
            for (int i = 0; i < zeroNightCands.Count; i++)
            {
                var z = BuildResult(zeroNightCands[i], targetBp);
                z.feedUl = result.feedUl;
                if (z.ok && BetterFinal(z, result)) result = z;
            }
            return result;
        }

        /// <summary>
        /// 批次 28：在 AutoSolve 外面搜接水量。25 点网格 + 最优点左右各一格二分 6 次。
        /// mode 只认 1 效率 / 2 性价比；其它值按性价比处理。forceHydra 只保留 portable 达标方案。
        /// </summary>
        public static Result SearchFeed(long capUl, int purityBp, double perf, double quality,
                                        int targetBp, int nightCap, int mode,
                                        bool allowFilter, bool allowUv, bool allowTablet,
                                        bool allowHydra, bool forceHydra,
                                        int tabletStock, int filterDurMax, int hydraDuraMax)
        {
            if (capUl <= 0) return null;
            var sw = Stopwatch.StartNew();
            if (mode != 1) mode = 2;
            var grid = new List<long>();
            var all = new List<long>();
            Result best = null;
            for (int i = 1; i <= 25; i++)
            {
                long v = (long)Math.Floor(capUl * Math.Pow(i / 25.0, 2.2));
                if (v < 1000) continue;
                all.Add(v);
                var r = Solve(v, purityBp, perf, quality, targetBp, nightCap,
                              allowFilter, allowUv, allowTablet, allowHydra, tabletStock, filterDurMax, hydraDuraMax);
                if (!Accept(r, forceHydra)) continue;
                grid.Add(v);
                if (best == null || Better(r, best, mode, forceHydra))
                    best = r;
            }
            if (best == null) { LastSearchMs = (long)sw.ElapsedMilliseconds; return null; }

            // 右边界取下一个网格点；没有就取容量。目标纯度的可行边界就在 best 和它之间。
            long left = best.feedUl;
            long right = capUl;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] > best.feedUl) { right = all[i]; break; }
            }
            for (int k = 0; k < 6; k++)
            {
                if (right <= left) break;
                long mid = left + (right - left) / 2;
                if (mid < 1000) break;
                var r = Solve(mid, purityBp, perf, quality, targetBp, nightCap,
                              allowFilter, allowUv, allowTablet, allowHydra, tabletStock, filterDurMax, hydraDuraMax);
                if (Accept(r, forceHydra))
                {
                    left = mid;
                    if (best == null || Better(r, best, mode, forceHydra)) best = r;
                }
                else right = mid;
            }
            LastSearchMs = (long)sw.ElapsedMilliseconds;
            return best;
        }

        /// <summary>
        /// 批次 29：多晚开时只取「最小可行晚数」。1..nightCap 依次试，第一个达标即返回；
        /// 空瓶走 SearchFeed（每个 n 只给 n 晚），真实瓶水走 SolveWater，不搜接水量。
        /// </summary>
        public static Result SolveMinNights(long capUl, int purityBp, double perf, double quality,
                                            int targetBp, int nightCap, int mode,
                                            bool allowFilter, bool allowUv, bool allowTablet,
                                            bool allowHydra, bool forceHydra,
                                            int tabletStock, int filterDurMax, int hydraDuraMax)
        {
            if (capUl <= 0) return null;
            if (nightCap <= 0) nightCap = 1;
            mode = (mode == 1 ? 1 : 2);
            for (int n = 1; n <= nightCap; n++)
            {
                var r = SearchFeed(capUl, purityBp, perf, quality, targetBp, n, mode,
                                   allowFilter, allowUv, allowTablet, allowHydra, forceHydra,
                                   tabletStock, filterDurMax, hydraDuraMax);
                if (r != null && r.ok && r.nights <= n) return r;
            }
            return null;
        }

        /// <summary>真实瓶水版 SolveMinNights：水量固定，只加晚数。</summary>
        public static Result SolveMinNights(WaterMath.Water water0, double perf, double quality,
                                            int targetBp, int nightCap, int mode,
                                            bool allowFilter, bool allowUv, bool allowTablet,
                                            bool allowHydra, bool forceHydra,
                                            int tabletStock, int filterDurMax, int hydraDuraMax)
        {
            if (water0 == null || water0.v <= 0) return null;
            if (nightCap <= 0) nightCap = 1;
            mode = (mode == 1 ? 1 : 2);
            for (int n = 1; n <= nightCap; n++)
            {
                var r = SolveWater(water0, perf, quality, targetBp, n,
                                   allowFilter, allowUv, allowTablet, allowHydra,
                                   tabletStock, filterDurMax, hydraDuraMax);
                if (r != null && r.ok && r.nights <= n) return r;
            }
            return null;
        }

        /// <summary>效率档多晚：先用道具把水量尽量拉满；达到容器容量的 99% 即停，
    /// 优先压缩晚数；确实达不到时才取最大出水。</summary>
    public static Result SolveMaxOutput(long capUl, int purityBp, double perf, double quality,
                                        int targetBp, int nightCap, int mode,
                                        bool allowFilter, bool allowUv, bool allowTablet,
                                        bool allowHydra, bool forceHydra,
                                        int tabletStock, int filterDurMax, int hydraDuraMax)
    {
        if (capUl <= 0) return null;
        if (nightCap <= 0) nightCap = 1;
        mode = (mode == 1 ? 1 : 2);
        long nearFull = capUl - capUl / 100;   // 99% 容量
        long minGain = capUl / 100;            // 道具不足时，后续晚数收益不足 1% 容量就停
        Result best = null;
        for (int n = 1; n <= nightCap; n++)
        {
            var r = SearchFeed(capUl, purityBp, perf, quality, targetBp, n, mode,
                               allowFilter, allowUv, allowTablet, allowHydra, forceHydra,
                               tabletStock, filterDurMax, hydraDuraMax);
            if (r != null && r.ok && r.nights <= n)
            {
                if (r.w.v >= nearFull) return r;  // 已接近装满，晚数最少者胜
                if (best == null) best = r;
                else if (r.w.v <= best.w.v + minGain)
                    break;                        // 再跑一晚也装不出 1% 容量，按时间效率停
                else if (r.w.v > best.w.v) best = r;
            }
        }
        return best;
    }

    /// <summary>效率档多晚的真实瓶水版：水量固定，按“先接近装满、再压缩晚数”选择。</summary>
    public static Result SolveMaxOutput(WaterMath.Water water0, double perf, double quality,
                                        int targetBp, int nightCap, int mode,
                                        bool allowFilter, bool allowUv, bool allowTablet,
                                        bool allowHydra, bool forceHydra,
                                        int tabletStock, int filterDurMax, int hydraDuraMax)
    {
        if (water0 == null || water0.v <= 0) return null;
        if (nightCap <= 0) nightCap = 1;
        mode = (mode == 1 ? 1 : 2);
        long nearFull = water0.v - water0.v / 100;   // 99% 容量
        long minGain = water0.v / 100;                // 道具不足时，后续晚数收益不足 1% 就停
        Result best = null;
        for (int n = 1; n <= nightCap; n++)
        {
            var r = SolveWater(water0, perf, quality, targetBp, n,
                               allowFilter, allowUv, allowTablet, allowHydra,
                               tabletStock, filterDurMax, hydraDuraMax);
            if (r != null && r.ok && r.nights <= n)
            {
                if (r.w.v >= nearFull) return r;
                if (best == null) best = r;
                else if (r.w.v <= best.w.v + minGain) break;
                else if (r.w.v > best.w.v) best = r;
            }
        }
        return best;
    }

    private static bool Accept(Result r, bool forceHydra)
        {
            if (r == null || !r.ok) return false;
            if (!forceHydra) return true;
            for (int i = 0; i < r.steps.Count; i++)
                if (r.steps[i].tool == "portable" && r.steps[i].count > 0) return true;
            return false;
        }

        private static bool Better(Result a, Result b, int mode, bool forceHydra)
        {
            if (a == null) return false;
            if (b == null) return true;
            if (mode == 1)
            {
                if (a.w.v != b.w.v) return a.w.v > b.w.v;
                if (a.nights != b.nights) return a.nights < b.nights;
                return a.cost < b.cost;
            }
            // 性价比：0 晚硬优先；同为 0 晚或多晚时才比每升成本、净赚。
            // SearchFeed 会按不同接水量比较候选，这里必须也拦住 1 晚方案，
            // 否则 AutoSolve 内部选了 0 晚，外层搜索仍可能按每升成本换成过夜方案。
            if (a.nights == 0 && b.nights > 0) return true;
            if (b.nights == 0 && a.nights > 0) return false;
            double la = CostPerLiter(a), lb = CostPerLiter(b);
            if (la != lb) return la < lb;
            double pa = a.revenue - a.cost - (ElectricityPerNight + TimeValuePerNight) * a.nights;
            double pb = b.revenue - b.cost - (ElectricityPerNight + TimeValuePerNight) * b.nights;
            if (pa != pb) return pa > pb;
            if (a.w.v != b.w.v) return a.w.v > b.w.v;
            return a.nights < b.nights;
        }

        private static double CostPerLiter(Result r)
        {
            if (r == null || r.w == null || r.w.v <= 0) return double.MaxValue;
            // 玩家要的是「每升成品水的成本」：分母用纯水 WaterPart，不能用进水体积，
            // 否则挤水/杂质多的方案会显得单位成本更低，反而被选成性价比推荐。
            long litersUl = r.w.WaterPart > 0 ? r.w.WaterPart : r.w.v;
            double cost = r.cost + (ElectricityPerNight + TimeValuePerNight) * r.nights;
            return cost / (litersUl / 1000000.0);
        }
    }
}
