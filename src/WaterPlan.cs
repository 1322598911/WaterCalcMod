using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WaterCalcMod
{
    /// <summary>
    /// 「做一次水」的完整规划：整瓶接水拆解（B）+ 逐步纯度模拟（C）。
    /// 所有数字都来自 WaterMath 的真函数调用，不在这里拼百分比。
    /// </summary>
    public static class WaterPlan
    {
        /// <summary>药片溢出用的真实容器上限：容器没装满（capUl &gt; feed）才返回 capUl，
        /// 否则返回 0 让 Build 按「正好装满」处理（与旧行为一致）。</summary>
        private static long CapOf(WaterPlan.Report r, long feed)
        {
            long c = r == null ? 0 : r.capUl;
            return c > feed ? c : 0;
        }

        // 玩家 2026-11-10 定案：原料水是水管水，免费，**不计入成本**；成本只有耗材 + 电。
        public const int TabletCost = 6;
        public const int FilterCost = 25;
        /// <summary>一块过滤棉的耐久点数（**成本折算基准**：一块 400 点 ≈ 25 元）。</summary>
        public const int FilterDurPer = 400;
        /// <summary>规划时可用的过滤棉总耐久：由界面按背包里**实际持有**的滤棉累加后写进来
        /// （玩家 2026-11-10：存档 2 块滤芯 = 800 点，写死 400 导致 20 L 明明能接 16253 只给 11600）。
        /// 界面另有「用几块过滤棉」的选择，玩家可以只挑一部分用。</summary>
        private static int filterDurAvail = FilterDurPer;
        public static int FilterDur
        {
            get { return filterDurAvail; }
            set
            {
                filterDurAvail = value >= 0 ? value : FilterDurPer;   // 0＝当前不用过滤棉
                WaterMath.FilterBudget = filterDurAvail;   // Run() 内部那份预算同步
            }
        }
        /// <summary>海德拉整只的价值（耐久 330 点）：按过滤棉同口径折成每点 ≈0.0758，仅用于「每升」比较。</summary>
        public const int HydraCost = 325;   // 玩家实测：整只 325 元 / 300 耐久
        /// <summary>电：1 能量 = 1 元。每晚耗多少能量从**机器标签**读
        /// （MachineryHelper.GetMachinePowerUsage）；0 效率加成时 4/晚，效率为负会更贵。
        /// 这是 static 而不是 const：每台机器的能耗可能不同，由 PurifierUi.Bonuses 写入。</summary>
        public static int EnergyPerNight = 4;

        public const int MaxTablets = 3;
        /// <summary>「时间也是成本」的旧折算值：已停用（保留常量只为不破坏旧引用）。
        /// 玩家 2026-11-11 定案：一晚只值 4 块电费，两片药 12 块 —— 再给一晚加 15 的「时间折价」，
        /// 会让 6 元一片的药片永远打赢「多跑一晚」，所以排序只用真实耗材成本。</summary>
        public const int NightValue = 15;

        /// <summary>方案排序用的综合成本：耗材价值本身（电费已按真实 EnergyPerNight 计进 c.cost）。</summary>
        private static double RankCost(Combo c)
        {
            if (c == null) return double.MaxValue;
            return c.cost;
        }
        public const int MaxNights = 8;
        /// <summary>最多算到几晚。玩家 2026-11-11 定案：**禁止推荐超过一晚的方案**
        /// （「再多过一晚」会把一瓶水拖成两天，玩家明确不要）。所以这里固定 1。</summary>
        public static int NightCap = 1;

        /// <summary>海德拉开关（玩家 2026-11-10 要求做成设置项）：
        /// 0＝自动（只有不用它就到不了目标时才参战）· 1＝强制可用（装得下就用它装满）· 2＝完全不用。</summary>
        public static int HydraMode = 0;
        /// <summary>最多肯花多少海德拉耐久（1 点 = 100 mL）。0 / ≥330 视为不限；默认 300。</summary>
        public static int HydraDuraCap = 300;

        /// <summary>玩家 2026-11-11 定口径：滑条只有**玩家亲手拖过**才算数（那是他自己声明的可用耐久）；
        /// 没拖过就按背包里真实剩的耐久来算，避免默认 300 把只持有 100 的玩家推荐成 300。</summary>
        public static bool HydraDurUserSet = false;

        /// <summary>这一趟海德拉真正允许用多少耐久。无限口径不按耐久卡；拖过滑条以滑条为准。</summary>
        public static long HydraAllowance(long hiCap, int ownHydraDur)
        {
            if (ownHydraDur == -1)                                   // 无限道具：装得下多少就吃多少
                return hiCap > 0 ? hiCap / WaterMath.HYDRA_UL_PER_DUR + 1 : (long)WaterMath.HYDRA_MAX_DUR;
            if (HydraDurUserSet)                                    // 玩家亲手拖过：以滑条为准（0 = 不限）
                return HydraDuraCap > 0 ? HydraDuraCap : (long)WaterMath.HYDRA_MAX_DUR;
            if (ownHydraDur > 0)                                    // 没拖过：背包真实耐久就是上限
                return HydraDuraCap > 0 ? Math.Min(ownHydraDur, HydraDuraCap) : ownHydraDur;
            return HydraDuraCap > 0 ? HydraDuraCap : (long)WaterMath.HYDRA_MAX_DUR;
        }

        // ---------------- 通用格式 ----------------

        public static string Ml(long ul)
        {
            if (ul % 1000 == 0) return (ul / 1000).ToString(CultureInfo.InvariantCulture);
            return (ul / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
        }

        public static string Ml2(long ul)
        {
            // 两位小数（杂质等小量）
            if (ul % 1000 == 0) return (ul / 1000).ToString(CultureInfo.InvariantCulture);
            return (ul / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>整毫升（四舍五入）：玩家要倒出/保留多少，报「42783 mL」而不是「42782.6 mL」。</summary>
        public static string Ml0(long ul)
        {
            return ((ul + 500L) / 1000L).ToString(CultureInfo.InvariantCulture);
        }

        private static string Circled(int n)
        {
            string s = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳";
            if (n >= 1 && n <= 20) return s.Substring(n - 1, 1);
            return "(" + n + ")";
        }

        public static int MachineCount()
        {
            int n = 0;
            try { n = PurifierHook.Pairs.Count; } catch (Exception) { }
            return n < 1 ? 1 : n;
        }

        // ---------------- B. 容器扫描 / 整瓶拆解 ----------------

        public class Cup { public string name; public long capUl; }
        public class CupGroup { public string name; public long capUl; public int n; }
        public class NightRow { public int n; public long volUl; public int purityBp; }
        public class Step { public string title; public string detail; }

        // 一次整包重算里只扫一遍背包/台面/机器：BuildCombos 会反复调 Plan(feed)，
        // 每调一次就全量扫一遍等于把同一份库存读几十遍。迁到官方 ModReflect 后单次读取变贵
        // （取调用方 mod + 找成员 + 权限校验），实测「打开手册 32 秒」全耗在这个重复扫描上。
        // 只在 Build 期间复用；Build 之外（按钮现算接水量等）一律重新扫，避免拿到旧库存。
        private static List<Cup> scanMemo;
        private static int scanSession;

        public static List<Cup> ScanCups()
        {
            if (scanSession > 0 && scanMemo != null) return scanMemo;
            var res = new List<Cup>();
            var seen = new List<GameItem>();
            AddCups(res, seen, GameBridge.AllItemsDeep());
            AddCups(res, seen, GameBridge.CounterItems());
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var inv = PurifierHook.Pairs[i].BottleInv;
                    if (inv == null) continue;
                    AddCups(res, seen, GameBridge.ItemsOf(inv));
                }
            }
            catch (Exception) { }
            if (scanSession > 0) scanMemo = res;
            return res;
        }

        private static void AddCups(List<Cup> res, List<GameItem> seen, List<GameItem> items)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it == null) continue;
                bool dup = false;
                for (int k = 0; k < seen.Count; k++) if (ReferenceEquals(seen[k], it)) { dup = true; break; }
                if (dup) continue;
                seen.Add(it);
                int capUl = 0;
                try { capUl = WaterHelper.GetTotalCapacity(it); } catch (Exception) { }
                if (capUl <= 0) continue;
                // 3L 水罐是过滤棉容器，只导液体、不进净水器（网页 canReceive）
                if (Math.Abs(capUl - 3000000) <= 1) continue;
                // 按 µL 存容量：1 mL / 10 mL 滴管这种小容器也要能参与凑量（精确到个位 mL）
                string nm = GameBridge.NameOfItem(it);
                if (nm == null || nm.Length == 0) nm = "容器";
                string label = nm;
                string capText = CupCapText(capUl);
                if (nm.IndexOf(capText, StringComparison.Ordinal) < 0) label = nm + " " + capText;
                // 按堆叠数量算：同一种瓶子一格里可能放了 2 个（unitCount=2），
                // 只算 1 个会让整瓶合计偏小（玩家：明明能凑到 1850，却只报 1800）。
                int unitN = 1;
                try
                {
                    var u = GameBridge.Get(it, "unitCount");
                    if (u != null) unitN = Math.Max(1, Convert.ToInt32(u));
                }
                catch (Exception) { unitN = 1; }
                for (int u = 0; u < unitN; u++) res.Add(new Cup { name = label, capUl = capUl });
            }
        }

        /// <summary>容量文案：整毫升写「750 mL」，不足 1 mL 的写「0.5 mL」。</summary>
        public static string CupCapText(long capUl)
        {
            if (capUl % 1000 == 0) return (capUl / 1000L).ToString(CultureInfo.InvariantCulture) + " mL";
            return (capUl / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " mL";
        }

        public static List<CupGroup> Groups(List<Cup> cups)
        {
            var res = new List<CupGroup>();
            for (int i = 0; i < cups.Count; i++)
            {
                CupGroup g = null;
                for (int k = 0; k < res.Count; k++) if (res[k].capUl == cups[i].capUl) { g = res[k]; break; }
                if (g == null) { g = new CupGroup { name = cups[i].name, capUl = cups[i].capUl, n = 0 }; res.Add(g); }
                g.n++;
            }
            res.Sort(delegate(CupGroup a, CupGroup b) { return b.capUl.CompareTo(a.capUl); });
            return res;
        }

        private class Search
        {
            public List<CupGroup> g;
            public long V;
            public long bestUsed;
            public int[] cur, best;
            public int calls;
            public bool over;

            public void Rec(int i, long used)
            {
                if (over) return;
                if (++calls > 400000) { over = true; return; }
                if (used > V) return;
                if (i >= g.Count)
                {
                    if (used > bestUsed) { bestUsed = used; best = (int[])cur.Clone(); }
                    return;
                }
                for (int k = 0; k <= g[i].n; k++)
                {
                    cur[i] = k;
                    Rec(i + 1, used + (long)k * g[i].capUl);
                    if (over) return;
                }
                cur[i] = 0;
            }
        }

        public class Pour
        {
            public long bestUl;
            public long totalUl;
            public long leftUl;
            public bool truncated;
            public bool none;
            public List<CupGroup> groups = new List<CupGroup>();
            public List<int> take = new List<int>();

            public string PartsText()
            {
                var sb = new StringBuilder();
                for (int i = 0; i < groups.Count; i++)
                {
                    if (take[i] <= 0) continue;
                    if (sb.Length > 0) sb.Append(" · ");
                    sb.Append(groups[i].name).Append(" × ").Append(take[i]).
                       Append(" = ").Append(Ml((long)take[i] * groups[i].capUl)).Append(" mL");
                }
                return sb.ToString();
            }

            public string TotalText(bool stillOneNight)
            {
                // 两个数都要给：一个是「一晚净化上限」（受杂质预算限制），
                // 一个是「现有整瓶凑得出来的量」。玩家要按容器来，所以一键值取后者。
                long cap = bestUl / 1000L;
                long whole = totalUl / 1000L;
                string s;
                if (whole > 0 && whole < cap)
                    s = "一键灌水按 " + whole + " mL（整瓶凑得出；接水上限 " + cap + " mL）";
                else
                    s = "一键灌水按 " + cap + " mL（容器直连接水口）";
                if (stillOneNight) s += "，一晚达标";
                return s;
            }
        }

        /// <summary>整瓶装子集和：取「总量 ≤ V 且最大」的组合。</summary>
        public static Pour Plan(long vUl)
        {
            return Plan(vUl, ScanCups());
        }

        /// <summary>同上，但容器清单由调用方给（离线探针用来验证滴管凑量的精确度）。</summary>
        public static Pour Plan(long vUl, List<Cup> cups)
        {
            var p = new Pour();
            p.bestUl = vUl;
            p.groups = Groups(cups);
            if (p.groups.Count == 0) { p.none = true; return p; }

            // 同一个瓶子可以反复用（接一趟倒一趟，跑几趟都行），所以拆解不受「库存件数」限制：
            // 只受体积与瓶口容量约束。这样 750×2 + 250 + 50×2 = 1850 是凑得出来的；
            // 背包里有 1 mL / 10 mL 滴管时，同一套贪心能凑到精确到个位 mL（1857）。
            int[] take = Greedy(p.groups, vUl, out p.totalUl);

            p.take = new List<int>(take);
            p.leftUl = vUl - p.totalUl;
            if (p.leftUl < 0) p.leftUl = 0;
            return p;
        }

        private static int[] Greedy(List<CupGroup> g, long vUl, out long total)
        {
            var take = new int[g.Count];
            total = 0;
            for (int i = 0; i < g.Count; i++)
            {
                long add = g[i].capUl;
                if (add <= 0) continue;
                int guard = 0;
                // 1 mL 滴管要能反复接（1850 → 1857 需要 7 次），上限放到 4096 次
                while (total + add <= vUl && guard++ < 4096)
                {
                    take[i]++; total += add;
                }
            }
            return take;
        }

        // ---------------- C. 逐步模拟 ----------------

        public class Combo
        {
            public string label;
            public bool filter, uv;
            public int tablets;
            public long feedUl, outUl;
            public int filterUsed;   // 这条方案实际扣的过滤棉耐久点数
            public string badge;     // 「效率最高」/「最省」标记（表里用）
            public int purityBp;
            public double cost;
            public bool ok;
            /// <summary>这条方案要跑几晚（只有「按已有水」生成的方案才知道，生水方案恒为 1）。</summary>
            public int nights;
            /// <summary>是否先用海德拉（便携净水器）把水洗成 98% 基础水。</summary>
            public bool hydra;
            /// <summary>海德拉扣的耐久点（1 点 / 100 mL，满 330 点 = 33 L）。</summary>
            public int hydraDur;
        }

        public class Report
        {
            public bool ok;
            /// <summary>玩家要的目标比这台机器上限还高（例：目标纯水 99.90%，本机只有 99.01%）。
            /// 这时不能把 targetBp 悄悄夹回 99.01% 然后报「能到 99.01%」——界面必须说「机器质量不足」。</summary>
            public bool capShort;
            public long capUl, bestUl, useUl, leftUl, needUl;
            public int srcBp, perf, q, targetBp;
            /// <summary>实际拿来判可行性的目标 = min(玩家目标, 本机上限)。方案表/求接水量/挑方案全用它，
            /// 否则目标设 99.90% 时按 99.90 判可达，一条方案都不达标，方案表会空。</summary>
            public int effTargetBp;
            public string srcName = "水";
            public string bottleName = "容器";
            public bool shortOf;
            public Pour pour;
            public List<Step> steps = new List<Step>();
            public List<Combo> combos = new List<Combo>();
            /// <summary>按「模式」选出来的那一条方案（0 一晚就要 / 1 效率 / 2 性价比），界面据此报耗材。</summary>
            public Combo pick;
            /// <summary>按前置/后置筛过之后还站得住的候选。combos 保持全量（方案表要显示 3~5 行），
            /// 只有 pick 从 keep 里挑；keep 为空时才退回全量并置 filterEmpty。</summary>
            public List<Combo> keep = new List<Combo>();
            public int mode;
            /// <summary>前置/后置筛完没有可行方案时置 true（界面提示用），此时 pick 退回全量里挑。</summary>
            public bool filterEmpty;
            public List<NightRow> nights = new List<NightRow>();
            public int tablets, filterUsed, uvUsed, nightsCount, machineCnt;
            public long outUl, contamInUl, contamOutUl, budgetUl;
            public int finalBp;
            public double cost, perL;
            public string tabletsOnly = "";
            /// <summary>true = 这份报告按「容器里已有的真实水团」算（不接新水）；
            /// 卡一/底部状态行据此改口，不再说「接 X mL」。</summary>
            public bool fromWater;
            /// <summary>已有水的起点（含六项真实配比）；fromWater=false 时为 null。</summary>
            public WaterMath.Water water0;
            /// <summary>已有水专用：一晚实际能处理到的体积（µL）。整瓶跑不完时小于 water0.v。</summary>
            public long fitUl;
            /// <summary>已有水专用：要先把多少 mL 倒出去，剩下的才够一晚压到目标。</summary>
            public long dumpUl;
            /// <summary>已有水专用：true = 整瓶压不到目标，只规划了 water0.v 里的一部分。</summary>
            public bool fitPartial;

            /// <summary>
            /// 界面统一口径 = 一晚能接的上限（受容器容量与一晚净化能力限制）。
            /// 不再按「整瓶凑得出多少」压这个数：储水容器是直接从水龙头接的，不经过背包瓶子，
            /// 瓶子只决定「用背包备水」那条路的量（见 pour）。玩家口径：能接到 1850 就报 1850。
            /// </summary>
            public long ShownUl()
            {
                return bestUl / 1000L * 1000L;
            }

            // ---------------- 网页三张卡 / 方案表要的数字（都取当前选中的那条方案） ----------------

            /// <summary>实际要接的量：有选中方案就跟方案走，否则退回主方案。</summary>
            public long ChosenFeedUl() { return pick != null ? pick.feedUl : useUl; }
            /// <summary>一晚出来的量。</summary>
            public long ChosenOutUl() { return pick != null ? pick.outUl : outUl; }
            /// <summary>一晚出来的纯度。</summary>
            public int ChosenOutBp() { return pick != null ? pick.purityBp : finalBp; }

            /// <summary>选中方案一晚的毛收入（元）：纯度档位 × 基础水价 × 出水升数。
            /// 玩家口径（2026-11-11）：实际售价 = 40 元/L × (1+修正)，纯水 ×2、优质 ×1.5、基础 ×1、幽灵 ×0.67、铁锈 ×0.25、沟渠 ×0.10。</summary>
            public double ChosenRevenue()
            {
                try { return WaterEconomy.Revenue(ChosenOutUl(), ChosenOutBp()); }
                catch (Exception) { return 0; }
            }

            /// <summary>选中方案一晚的净利（元）= 毛收入 − 这一轮耗材钱。</summary>
            public double ChosenProfit()
            {
                try { return ChosenRevenue() - (pick != null ? pick.cost : cost); }
                catch (Exception) { return 0; }
            }

            /// <summary>卡一标题：建议接水（容器名）—— 参照网页卡片，量单独放一行大字，标题不塞数字。</summary>
            public string CardFeedTitle()
            {
                return "建议接水（" + bottleName + "）";
            }

            /// <summary>卡一的量：实际能接 X mL（整瓶真倒得进去的量），凑不满时补一句上限与差额。</summary>
            public string CardFeedValue()
            {
                // 玩家要分清「实际能接进去的量」和「理论上限」：整瓶凑不满时两个数不一样。
                // 玩家 2026-11-11：不要再用「·」当分隔，改成空格；毫升一个颜色、标签一个颜色。
                string s = PlanText.Lbl("实际可接 ") + PlanText.Rich(PlanText.C_NUM, PlanText.Ml(ChosenFeedUl()));
                long cap = ShownUl();
                if (cap > ChosenFeedUl())
                    s += "    " + PlanText.Lbl("上限 ") + PlanText.Rich(PlanText.C_NUM, PlanText.Ml(cap))
                       + "    " + PlanText.Lbl("差 ") + PlanText.Rich(PlanText.C_NUM, PlanText.Ml(cap - ChosenFeedUl()));
                return s;
            }

            /// <summary>卡一合并成一行时的文字（ctl / 日志用，界面拆成标题与量两行）。</summary>
            public string CardFeed()
            {
                return CardFeedTitle() + " " + CardFeedValue();
            }

            /// <summary>卡二：出来 X mL   纯度 Y%   每升 Z 元（玩家要求：空格分隔、同类同色）。</summary>
            public string CardOut()
            {
                string s = PlanText.Lbl("出来 ") + PlanText.Rich(PlanText.C_NUM, PlanText.Ml(ChosenOutUl()))
                         + "    " + PlanText.Rich(PlanText.C_PCT, PlanText.Pct(ChosenOutBp()));
                try
                {
                    double c = pick != null ? pick.cost : cost;
                    long outUl2 = ChosenOutUl();
                    if (outUl2 > 0 && c > 0)
                    {
                        double perL = c / (outUl2 / 1000000.0);
                        s += "    " + PlanText.Lbl("每升 ")
                           + PlanText.Rich(PlanText.C_MONEY, perL.ToString("0.00", CultureInfo.InvariantCulture) + " 元");
                    }
                }
                catch (Exception) { }
                // 玩家 2026-11-11：目标设「纯水」但本机质量只到 99.01% 时，主界面也要说清楚。
                if (capShort) s += "    " + PlanText.Rich(PlanText.C_WARN, "本机上限 " + PlanText.Pct(WaterMath.CapOf(q)) + "，到不了 " + PlanText.Pct(targetBp));
                return s;
            }

            /// <summary>
            /// 「出来 …」下面单独一行：卖多少钱、什么档位、扣掉这一轮耗材还剩多少。
            /// 玩家 2026-11-11：档位按等级上色（优质水蓝），钱统一绿色。
            /// </summary>
            public string CardMoney()
            {
                try
                {
                    long outUl2 = ChosenOutUl();
                    if (outUl2 <= 0) return "";
                    double rev = WaterEconomy.Revenue(outUl2, ChosenOutBp());
                    if (rev <= 0) return "";
                    double spent = pick != null ? pick.cost : cost;
                    double profit = rev - spent;
                    string tier = WaterEconomy.TierName(ChosenOutBp());
                    return PlanText.Lbl("卖 ") + PlanText.Rich(PlanText.C_MONEY, Money(rev))
                         + "    " + PlanText.Rich(PlanText.TierHex(tier), tier)
                         + "    " + PlanText.Lbl("赚 ") + PlanText.Rich(PlanText.C_MONEY, Money(profit));
                }
                catch (Exception) { return ""; }
            }

            /// <summary>金额文本：最多两位小数，去掉多余的 0（1028.8 元 / 825.3 元）。</summary>
            private static string Money(double v)
            {
                return v.ToString("0.##", CultureInfo.InvariantCulture) + " 元";
            }

            /// <summary>卡三：杂质变化，进水 X → 出水 Y mL（走选中方案自己的水量与耗材）。</summary>
            public string CardImpurity()
            {
                WaterMath.Water w = ChosenWater();
                if (w == null) return "杂质变化 进水 " + Ml2(contamInUl) + " → 出水 " + Ml2(contamOutUl) + " mL";
                long inUl = pick != null ? pick.feedUl : useUl;
                var w0 = WaterMath.Build(inUl, srcBp, CapOf(this, inUl));
                return "杂质变化 进水 " + Ml2(w0.Impurity) + " → 出水 " + Ml2(w.Impurity) + " mL";
            }

            /// <summary>选中方案按「接水 → 过滤/紫外 → 过一晚 → 药片」走一遍（与 Run 同一顺序）。</summary>
            public WaterMath.Water ChosenWater()
            {
                if (pick == null) return null;
                try
                {
                    var w = water0 != null ? water0.Clone() : WaterMath.Build(pick.feedUl, srcBp, CapOf(this, pick.feedUl));
                    if (pick.hydra) WaterMath.Hydra(w);
                    if (pick.filter) WaterMath.Filter(w, FilterDur);
                    if (pick.uv) WaterMath.Uv(w, Math.Max(1, (int)Math.Ceiling(pick.feedUl / 3000000.0)));
                    int nn = pick.nights > 0 ? pick.nights : 1;
                    for (int i = 0; i < nn; i++) WaterMath.Purify(w, perf, q);
                    if (pick.tablets > 0) WaterMath.Tablet(w, pick.tablets);
                    return w;
                }
                catch (Exception) { return null; }
            }

            /// <summary>选中方案「放药片之前」的状态（接水 → 过滤/紫外 → 过一晚），用来判断药片是否清得动。</summary>
            public WaterMath.Water ChosenWaterBeforeTablet()
            {
                if (pick == null) return null;
                try
                {
                    var w = water0 != null ? water0.Clone() : WaterMath.Build(pick.feedUl, srcBp, CapOf(this, pick.feedUl));
                    if (pick.hydra) WaterMath.Hydra(w);
                    if (pick.filter) WaterMath.Filter(w, FilterDur);
                    if (pick.uv) WaterMath.Uv(w, Math.Max(1, (int)Math.Ceiling(pick.feedUl / 3000000.0)));
                    int nn = pick.nights > 0 ? pick.nights : 1;
                    for (int i = 0; i < nn; i++) WaterMath.Purify(w, perf, q);
                    return w;
                }
                catch (Exception) { return null; }
            }

            /// <summary>
            /// 药片为什么「没反应」：网页 runTablet 里化学污染物只能清到「水量×0.7%」以上，
            /// 微生物又已经被净化器清空，所以这瓶加几片都一样。返回空串表示药片确实起作用。
            /// </summary>
            public string TabletHint()
            {
                if (pick == null || pick.tablets <= 0) return "";
                var w = ChosenWaterBeforeTablet();
                if (w == null || w.v <= 0) return "";
                long chem = w.Part("chemical");
                long floorC = w.v * 7L / 1000L;
                if (chem > floorC) return "";
                return "药片这瓶上清不到东西：化学污染物 " + Ml2(chem) + " mL 低于 0.7% 门限 " + Ml2(floorC) + " mL，加几片都一样";
            }

            /// <summary>
            /// 杂质明细：水 + 六项杂质，逐项按万分比取整，余额全部给水，
            /// 保证界面上一行加起来正好 100.00%（对不齐时调用方改用绝对 mL）。
            /// </summary>
            public string MixLine()
            {
                var w = ChosenWater();
                if (w == null || w.v <= 0) return "";
                string[] keys = { "heavy", "organic", "microbe", "chemical", "physical", "mineral" };
                string[] names = { "重金属", "有机废物", "微生物", "化学污染物", "物理杂质", "矿物" };
                long[] bp = new long[keys.Length];
                long sum = 0;
                for (int i = 0; i < keys.Length; i++)
                {
                    long p = w.Part(keys[i]);
                    bp[i] = p * 10000L / w.v;
                    sum += bp[i];
                }
                long waterBp = 10000L - sum;
                if (waterBp < 0) waterBp = 0;
                var sb = new StringBuilder();
                sb.Append("水 ").Append(PlanText.Pct((int)waterBp));
                for (int i = 0; i < keys.Length; i++)
                    sb.Append(" · ").Append(names[i]).Append(' ').Append(PlanText.Pct((int)bp[i]));
                return sb.ToString();
            }

            /// <summary>杂质明细的兜底版本：百分比对不齐总量时直接报每种杂质多少 mL。</summary>
            public string MixLineMl()
            {
                var w = ChosenWater();
                if (w == null || w.v <= 0) return "";
                string[] keys = { "heavy", "organic", "microbe", "chemical", "physical", "mineral" };
                string[] names = { "重金属", "有机废物", "微生物", "化学污染物", "物理杂质", "矿物" };
                var sb = new StringBuilder();
                sb.Append("水 ").Append(Ml2(w.WaterPart));
                for (int i = 0; i < keys.Length; i++)
                    sb.Append(" · ").Append(names[i]).Append(' ').Append(Ml2(w.Part(keys[i])));
                sb.Append("（mL）");
                return sb.ToString();
            }

            /// <summary>方案表一行：标签 · 接 X · 出 Y · 纯度 · 耗材（药片/过滤棉/紫外）· 每升成本。
            /// 单位写在表头，行里只留数字，避免一行太长被面板宽度折成四行。</summary>
            public string ComboRow(Combo c, bool selected)
            {
                return ComboRowMain(c, selected) + ComboRowSub(c);
            }

            /// <summary>方案表第一行文字：勾 + 标签 + 「接多少→出多少」，这一段绝不拆开（数字不能被折行切断）。</summary>
            /// <summary>等宽补空格：像素字体近似等宽，用空格把列对开（够用且不改控件）。</summary>
            private static string Pad(string s, int w)
            {
                s = s ?? "";
                if (s.Length > w) s = s.Substring(0, w);
                while (s.Length < w) s += ' ';
                return s;
            }

            public string ComboRowMain(Combo c, bool selected)
            {
                var sb = new StringBuilder();
                if (selected) sb.Append("▶ ");
                // 标签压到最短：去掉「（过夜后放）」和「什么都不放 + 」，只留「无前置·药片1」这种能一眼认出的写法
                string lb = c.label.Replace("（过夜后放）", "").Replace("什么都不放", "无前置").Replace(" + ", "·")
                                    .Replace("只放过滤棉", "滤棉").Replace("只放紫外", "紫外");
                // 紧凑单行：像素字体不等宽，补空格对不齐，所以用「·」分隔，避免折行
                sb.Append(lb).Append(" · ").Append(Ml(c.feedUl)).Append('→').Append(Ml(c.outUl));
                if (c.ok && !string.IsNullOrEmpty(c.badge)) sb.Append(" [").Append(c.badge).Append(']');
                return sb.ToString();
            }

            /// <summary>方案表第二行文字：纯度 · 耗材 · 本钱 · 卖价 · 净赚。用空格分隔、不写「·」，
            /// 免得面板宽度不够时在分隔符处被折成第三行。</summary>
            public string ComboRowSub(Combo c)
            {
                var sb = new StringBuilder("   ");
                sb.Append(PlanText.Pct(c.purityBp));
                string cons = "";
                if (c.tablets > 0) cons = "药片" + c.tablets;
                if (c.filter) cons += (cons.Length > 0 ? "+" : "") + "滤棉";
                if (c.hydra) cons += (cons.Length > 0 ? "+" : "") + "海德拉";
                // 玩家：紫外灯是**工具**，不是耗材，所以不计进「耗材」也不计价。
                sb.Append(' ').Append(cons.Length > 0 ? cons : "无耗材");
                // 玩家 2026-11-11：这一行只留「本钱 · 卖多少 · 赚多少」；每升成本在卡二和详情页，
                // 一行塞太多会被面板宽度折成第三行。
                double rev = WaterEconomy.Revenue(c.outUl, c.purityBp);
                sb.Append(" 本 ").Append(c.cost.ToString("0.0", CultureInfo.InvariantCulture))
                  .Append(" 卖 ").Append(WaterEconomy.Money(rev))
                  .Append(" 赚 ").Append(WaterEconomy.Money(rev - c.cost));
                if (c.uv) sb.Append("（紫外灯·工具）");
                return sb.ToString();
            }

            /// <summary>方案表要显示的行：选中那条在最上面，其余按出水量从大到小，最多 max 行。
            /// 前置/后置筛出的候选排在前面，剩下的自动方案补足，保证表里始终是 3~5 行而不是一行。</summary>
            public List<Combo> TableRows(int max)
            {
                var res = new List<Combo>();
                if (max <= 0) return res;
                if (pick != null) res.Add(pick);
                var rest = new List<Combo>();
                for (int i = 0; i < keep.Count; i++)
                {
                    var c = keep[i];
                    if (c == null || !c.ok || c == pick || res.Contains(c)) continue;
                    rest.Add(c);
                }
                rest.Sort(delegate(Combo a, Combo b)
                {
                    int k = b.outUl.CompareTo(a.outUl);
                    if (k != 0) return k;
                    return b.purityBp.CompareTo(a.purityBp);
                });
                for (int i = 0; i < rest.Count && res.Count < max; i++) res.Add(rest[i]);
                // 筛完不足 max 行时，用自动方案补齐（选中行仍然只有一条带 ▶）
                var extra = new List<Combo>();
                for (int i = 0; i < combos.Count; i++)
                {
                    var c = combos[i];
                    if (c == null || !c.ok || c == pick || res.Contains(c)) continue;
                    extra.Add(c);
                }
                extra.Sort(delegate(Combo a, Combo b)
                {
                    int k = b.outUl.CompareTo(a.outUl);
                    if (k != 0) return k;
                    return b.purityBp.CompareTo(a.purityBp);
                });
                for (int i = 0; i < extra.Count && res.Count < max; i++) res.Add(extra[i]);
                return res;
            }

            public string SpareText()
            {
                long spare = capUl - ShownUl();
                if (spare <= 1000) return "正好装满这个容器";
                return "容器还能装 " + (spare / 1000) + " mL，但一晚清不完";
            }

            public string Headline()
            {
                // 玩家 2026-11-11：目标设成纯水（99.90%）而本机上限只有 99.01% 时，
                // 旧代码把目标悄悄夹到 99.01%，于是界面理直气壮地报 99.01%，看不出是机器不行。
                if (capShort)
                    return "机器质量不足：上限 " + PlanText.Pct(WaterMath.CapOf(q)) + "，到不了 " + PlanText.Pct(targetBp)
                         + "（要装质量模组 +100）";
                if (!ok) return "今日水质一晚做不到 " + PlanText.Pct(targetBp);
                // 必须跟选中方案走：此前这里写死 ShownUl()/outUl（自动方案），
                // 玩家选了「过滤+紫外+1片药」后卡一还是「2000→1850」，与方案表的 2000→1904 打架。
                return "建议接水（" + bottleName + "）" + (ChosenFeedUl() / 1000) + " mL → 出 " + (ChosenOutUl() / 1000) + " mL · " + PlanText.Pct(ChosenOutBp());
            }

            public string ImpurityText()
            {
                return "杂质变化：进水 " + Ml2(contamInUl) + " → 出水 " + Ml2(contamOutUl) + " mL";
            }

            public string CostText()
            {
                // 显示「选中方案」真实消耗：玩家反馈过滤棉扣了多少点看不到
                int showTablets = pick != null ? pick.tablets : tablets;
                int showFilter = pick != null ? pick.filterUsed : filterUsed;
                int showUv = pick != null ? (pick.uv ? 1 : 0) : uvUsed;
                var sb = new StringBuilder();
                sb.Append("这一轮花掉的耗材：药片 ").Append(showTablets).Append(" 片");
                if (showFilter > 0) sb.Append(" · 过滤棉 ").Append(showFilter).Append(" 点耐久（400 点里扣）");
                else sb.Append(" · 没用到过滤棉");
                // 玩家：紫外灯是工具（不放耗材、无次数），只注明有没有用。
                if (showUv > 0) sb.Append(" · 用了紫外灯（工具，不消耗）");
                sb.Append(" · 电 ").Append(EnergyPerNight).Append(" 度");
                sb.Append(" · 价值 ").Append(cost.ToString("0.00", CultureInfo.InvariantCulture)).
                   Append(" · 每升 ").Append(perL.ToString("0.00", CultureInfo.InvariantCulture));
                // 玩家 2026-11-11：还要算「这一锅卖多少、净赚多少」。
                try
                {
                    double rev = ChosenRevenue();
                    if (rev > 0)
                        sb.Append(" · 卖 ").Append(WaterEconomy.Money(rev))
                          .Append(" 元（").Append(WaterEconomy.TierName(ChosenOutBp()))
                          .Append(" · 赚 ").Append(WaterEconomy.Money(ChosenProfit())).Append(" 元）");
                }
                catch (Exception) { }
                return sb.ToString();
            }

            public string WhyText()
            {
                var w = WaterMath.Build(capUl, srcBp, CapOf(this, capUl));
                long contam = w.Impurity;
                return "为什么只接这么多：一晚净水器只清 " + Ml2(budgetUl) +
                       " mL 杂质（预算 =（体积×0.3%+25mL）×脏度×1.25），而 " +
                       Ml(capUl) + " mL 里有 " + Ml2(contam) + " mL 杂质，清不够就只能少接";
            }

            public string StepsText()
            {
                var sb = new StringBuilder();
                int idx = 1;
                for (int i = 0; i < steps.Count; i++)
                {
                    sb.Append(Circled(idx++)).Append(" ").Append(steps[i].title);
                    if (!string.IsNullOrEmpty(steps[i].detail)) sb.Append("   （").Append(steps[i].detail).Append("）");
                    sb.Append('\n');
                }
                return sb.ToString();
            }

            public string NightsText()
            {
                var sb = new StringBuilder();
                for (int i = 0; i < nights.Count; i++)
                {
                    sb.Append("第 ").Append(nights[i].n).Append(" 晚 | ").
                       Append(Ml(nights[i].volUl)).Append(" mL | ").
                       Append(PlanText.Pct(nights[i].purityBp));
                    if (nights[i].purityBp >= effTargetBp) sb.Append("  ← 达标");
                    sb.Append('\n');
                }
                return sb.ToString();
            }

            public string Summary()
            {
                var sb = new StringBuilder();
                sb.Append(Headline()).Append("（").Append(SpareText()).Append("）\n");
                sb.Append(ImpurityText()).Append('\n');
                sb.Append(CostText()).Append('\n');
                
                if (!string.IsNullOrEmpty(tabletsOnly)) sb.Append(tabletsOnly).Append('\n');
                if (pour != null && !pour.none && pour.totalUl > 0)
                {
                    sb.Append(pour.PartsText()).Append('\n');
                    sb.Append(pour.TotalText(!shortOf)).Append('\n');
                }
                return sb.ToString();
            }
        }

        private static Report BuildReport(long capUl, int srcBp, int perf, int q, int targetBp,
                                          string srcName, string bottleName, bool full, int mode = 0,
                                          int pre = 0, int post = 0,
                                          int ownTablets = -1, bool ownFilter = true, bool ownUv = true,
                                          int ownHydraDur = -1)
        {
            var r = new Report();
            r.capUl = capUl; r.srcBp = srcBp; r.perf = perf; r.q = q; r.targetBp = targetBp;
            r.srcName = srcName; r.bottleName = bottleName;
            r.machineCnt = MachineCount();
            int cap = WaterMath.CapOf(q);
            // 目标高于本机上限时只夹**计算用的**目标值；r.targetBp 保持玩家的意愿，
            // 并置 capShort 让界面说「质量不足」，而不是把 99.90 的目标悄悄换成 99.01 报出去。
            r.capShort = targetBp > cap;
            if (targetBp > cap) targetBp = cap;
            r.effTargetBp = targetBp;

            r.bestUl = PurifierUi.MaxKeepUl((int)capUl, srcBp, perf, q, targetBp);
            // 容器装得下就显示满：差 ≤1 mL 的零头不当成「还差 1 mL」（玩家 m00160：20 L 显示 19999；游戏本身四舍五入）
            if (r.bestUl > 0 && capUl > 0 && capUl - r.bestUl <= 1000) r.bestUl = capUl;
            if (r.bestUl <= 0) { r.ok = false; return r; }
            r.ok = true;

            r.pour = Plan(r.bestUl);
            r.leftUl = r.pour.leftUl;
            r.useUl = r.pour.totalUl > 0 ? r.pour.totalUl : r.bestUl;
            if (r.useUl > r.bestUl) r.useUl = r.bestUl;

            // 一晚达标的最低接水量（判断「现有容器够不够」）
            r.needUl = MinFeed(r, r.useUl);

            // 选最省的配置（药片最后放，这是网页与游戏的固定顺序）
            bool uf = false, uv = false;
            int tab = 0, nights = 0, fUsed = 0;
            double bestCost = double.MaxValue;
            bool found = false;
            for (int t = 0; t <= MaxTablets; t++)
            {
                for (int f = 0; f < 2; f++)
                {
                    for (int u = 0; u < 2; u++)
                    {
                        var p = WaterMath.Run(r.useUl, srcBp, perf, q, f == 1, u == 1, t, targetBp, NightCap, CapOf(r, r.useUl));
                        if (!p.ok) continue;
                        double c = t * TabletCost + (f == 1 ? (p.filterUsed / (double)FilterDurPer) * FilterCost : 0) +
                                   p.nights * EnergyPerNight;
                        if (!found || c < bestCost - 1e-9 ||
                            (Math.Abs(c - bestCost) <= 1e-9 && p.nights < nights))
                        {
                            found = true; bestCost = c;
                            uf = (f == 1); uv = (u == 1); tab = t;
                            nights = p.nights; fUsed = p.filterUsed;
                        }
                    }
                }
            }
            if (!found)
            {
                // 一晚/八晚都做不到：按「过滤 + 紫外、不放药片」给到最好的一条，标不达标
                var p = WaterMath.Run(r.useUl, srcBp, perf, q, true, true, 0, targetBp, NightCap, CapOf(r, r.useUl));
                uf = true; uv = true; tab = 0; nights = p.nights; fUsed = p.filterUsed;
            }
            r.shortOf = !found && r.useUl < r.bestUl;

            // 逐步模拟：接水 → 过滤 → 紫外 → 每晚 → 药片（最后）
            // 抽成方法：模式选择发生在后面（PickByMode），选中方案变了要按它再算一次，
            // 否则「步骤」页永远显示按最省算出来的那一套（玩家反馈：切效率/性价比步骤没动静）。
            SimulateSteps(r, r.useUl, uf, uv, tab, nights, fUsed);

            r.mode = mode;
            if (full)
            {
                BuildCombos(r, pre, ownTablets, ownFilter, ownUv, ownHydraDur);
                BuildTabletsOnly(r);
                // 前置/后置两个下拉也要真的起作用：先按它们把候选方案筛掉，再按模式挑一条。
                // 效率模式（mode=1）不按前置/后置筛：它就是要「大胆用过滤和药片」把接水量拉满，
                // 优先级高于玩家手动选的后置（否则选了「不放药片」效率就永远上不去）。
                // 前置/后置只要玩家显式选了（pre>0 或 post>0）就必须生效，任何模式都不例外。
                // 以前写成 `if (mode != 1)`：效率模式下玩家选的「过滤棉+紫外」被整条忽略，
                // 方案退回到「什么都不放+1 片药片」，于是步骤里永远是两晚（玩家报的 bug）。
                // 两个都选「自动」时才不筛（让模式自己挑最优）。
                if (pre > 0 || post > 0) FilterByPrePost(r, pre, post);
                PickByMode(r, mode);
#if DEV
                PurifierUi.Log("[pick] mode=" + mode + " eff=" + r.effTargetBp + " capUl=" + r.capUl
                               + " pick=" + (r.pick == null ? "-" : r.pick.label)
                               + " feed=" + (r.pick == null ? 0 : r.pick.feedUl)
                               + " out=" + (r.pick == null ? 0 : r.pick.outUl)
                               + " purity=" + (r.pick == null ? 0 : r.pick.purityBp)
                               + " hydra=" + (r.pick != null && r.pick.hydra)
                               + " keep=" + r.keep.Count);
#endif

                // 模式选中的那条方案才是玩家要照做的：按它的 过滤/紫外/药片/接水量 重算步骤与逐晚报数。
                // 以前步骤在 PickByMode 之前就算完了，所以切「效率/性价比」时步骤页永远不动。
                if (r.pick != null)
                {
                    // 药片是**过夜后放**的：一晚是否达标要按「n 晚 + 药片」判，
                    // 不能只按「过夜前纯度」判 —— 否则方案表说一晚达标、步骤却排两晚（玩家报的 bug）。
                    // 带海德拉的方案，晚数在建组合时已经按「海德拉后 98% 起点」算过，直接用，别拿生水再算。
                    int pNights = r.pick.nights > 0 ? r.pick.nights : 1;
                    if (!r.pick.hydra)
                    {
                        pNights = 1;
                        int capN = NightCap > 0 ? NightCap : 1;
                        for (int n = 1; n <= capN; n++)
                        {
                            var pn = WaterMath.Run(r.pick.feedUl, srcBp, perf, q, r.pick.filter, r.pick.uv,
                                                   r.pick.tablets, targetBp, n, CapOf(r, r.pick.feedUl));
                            if (pn != null && pn.ok) { pNights = n; break; }
                            if (n == capN) pNights = (pn != null && pn.nights > 0) ? pn.nights : 1;
                        }
                    }
                    int pFilterUsed = r.pick.filter ? (int)r.pick.filterUsed : 0;
                    SimulateSteps(r, r.pick.feedUl, r.pick.filter, r.pick.uv, r.pick.tablets, pNights, pFilterUsed, r.pick.hydra);
                }
            }
            return r;
        }

        /// <summary>按给定方案逐步模拟：接水 → 过滤 → 紫外 → 每晚 → 药片（最后）。
        /// 会被调两次：BuildReport 里先按自动方案算一遍，PickByMode 选中模式方案后再按 r.pick 重算，
        /// 保证「步骤」页与卡片/方案表说的是同一套流程。</summary>
        private static void SimulateSteps(Report r, long feedUl, bool uf, bool uv, int tab, int nights, int fUsed, bool hydra = false)
        {
            r.steps.Clear();
            r.nights.Clear();
            var w = WaterMath.Build(feedUl, r.srcBp, CapOf(r, feedUl));
            r.contamInUl = w.Impurity;
            var s0 = new Step();
            s0.title = "接水 " + Ml(feedUl) + " mL · " + r.srcName + " " + PlanText.Pct(r.srcBp);
            s0.detail = "";
            r.steps.Add(s0);

            int hydraDur = 0;
            if (hydra)
            {
                hydraDur = WaterMath.Hydra(w);
                if (hydraDur > 0)
                {
                    var sh = new Step();
                    sh.title = "海德拉 → " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity);
                    sh.detail = "洗成 98% 基础水（化学 1.60% · 重金属 0.40%），耗耐久 " + hydraDur + " 点";
                    r.steps.Add(sh);
                }
            }

            if (uf)
            {
                int used = WaterMath.Filter(w, FilterDur);
                var st = new Step();
                st.title = "过滤棉 → " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity);
                st.detail = "耗耐久 " + used + " 点";
                r.steps.Add(st);
            }
            if (uv)
            {
                // 玩家：紫外灯是**工具**，不是消耗品，没有使用次数；体积不变、一次照过即可。
                WaterMath.Uv(w, 1);
                var st = new Step();
                st.title = "紫外灯 → " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity);
                st.detail = "微生物转纯水，体积不变（工具，不消耗）";
                r.steps.Add(st);
            }
            for (int n = 1; n <= nights; n++)
            {
                var st = new Step();
                st.title = "第 " + n + " 晚 → " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity);
                st.detail = "本晚目标 " + PlanText.Pct(r.effTargetBp);
                WaterMath.Purify(w, r.perf, r.q);
                r.nights.Add(new NightRow { n = n, volUl = w.v, purityBp = w.Purity });
                st.title = "第 " + n + " 晚 → " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity);
                r.steps.Add(st);
                if (w.Purity >= r.effTargetBp) break;
            }
            if (tab > 0)
            {
                long before = w.v;
                WaterMath.Tablet(w, tab);
                var st = new Step();
                st.title = "最后 " + tab + " 片药片 → " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity);
                st.detail = "带走 " + Ml2(before - w.v) + " mL 杂质";
                r.steps.Add(st);
            }
            r.outUl = w.v;
            r.finalBp = w.Purity;
            r.contamOutUl = w.Impurity;
            r.tablets = tab; r.filterUsed = fUsed; r.uvUsed = uv ? 1 : 0;
            r.nightsCount = nights;
            r.budgetUl = WaterMath.Budget(r.capUl, r.srcBp, r.perf);
            r.cost = tab * TabletCost + (fUsed / (double)FilterDurPer) * FilterCost +
                     nights * EnergyPerNight +
                     (hydraDur > 0 ? (hydraDur / (double)WaterMath.HYDRA_MAX_DUR) * HydraCost : 0);
            r.perL = r.outUl > 0 ? r.cost / (r.outUl / 1000000.0) : 0;
        }

        /// <summary>按「前置（0 自动 / 1 不处理 / 2 过滤棉 / 3 紫外）」与「后置（0 自动 / 1 不放药片 / 2-4 = 1-3 片）」筛候选方案。</summary>
        private static void FilterByPrePost(Report r, int pre, int post)
        {
            if (pre <= 0 && post <= 0) return;
            var keep = new System.Collections.Generic.List<Combo>();
            foreach (var c in r.combos)
            {
                if (!c.ok) continue;
                if (pre == 1 && (c.filter || c.uv)) continue;
                if (pre == 2 && !(c.filter && !c.uv)) continue;
                if (pre == 3 && !(c.uv && !c.filter)) continue;
                if (pre == 4 && !(c.filter && c.uv)) continue;   // 玩家新增：过滤棉 + 紫外 一起上
                if (post == 1 && c.tablets != 0) continue;
                if (post >= 2 && c.tablets != post - 1) continue;
                keep.Add(c);
            }
            // 筛空了（比如这台机器根本达不到该配方）就退回全量，但把话留给界面说明，
            // 不能让玩家选了「紫外」却看到一片空白。
            r.keep = keep;
            if (keep.Count == 0) r.filterEmpty = true;
        }

        private static void PickByMode(Report r, int mode)
        {
            r.mode = mode;
            Combo best = null;
            long fillLine = r.capUl > 0 ? (long)(r.capUl * 0.99) : 0;
            // 只在「前置/后置筛过之后」的候选里挑；筛空才退回全量（界面会提示凑不出）。
            var src = r.keep.Count > 0 ? r.keep : r.combos;

            // 档位（玩家 2026-11-10 定案）：
            //   0 默认   = 每升最省；装满方案只有在每升成本 ≤ 最省方案 3 倍时才优先（不为装满堆耗材）
            //   1 效率   = 装满容器优先 → 出水最多 → 便宜（不看性价比）
            //   2 性价比 = 每升最省，但候选已按**实际持知道具**建过（没道具就装不满）
            //   3 质量   = 纯度优先（不进按钮栏）
            // 除质量档外只在达标方案里挑；一条都没有才退回全量（界面照旧标不达标）。
            if (mode != 3)
            {
                var okOnly = new System.Collections.Generic.List<Combo>();
                for (int i = 0; i < src.Count; i++) if (src[i] != null && src[i].ok) okOnly.Add(src[i]);
                if (okOnly.Count == 0)
                {
                    for (int i = 0; i < r.combos.Count; i++) if (r.combos[i] != null && r.combos[i].ok) okOnly.Add(r.combos[i]);
                }
                if (okOnly.Count > 0) src = okOnly;
            }

            // 海德拉门（玩家 2026-11-10）：开关做进设置里，三档默认口径是「自动」——
            //   自动(0)：只要**有任何不带海德拉的达标方案**就剔掉它（海德拉贵、难弄，别动不动就推）；
            //   强制(1)：装得下就用它装满；
            //   不用(2)：任何档位都不让它参战。
            if (HydraMode == 2)
            {
                var noHyd0 = new System.Collections.Generic.List<Combo>();
                for (int i = 0; i < src.Count; i++) if (src[i] != null && !src[i].hydra) noHyd0.Add(src[i]);
                if (noHyd0.Count > 0) src = noHyd0;
            }
            else if (HydraMode == 0 && mode != 3 && mode != 1 && fillLine > 0)
            {
                bool hasNonHydraOk = false;
                for (int i = 0; i < src.Count; i++)
                {
                    var c0 = src[i];
                    if (c0 != null && !c0.hydra && c0.ok) { hasNonHydraOk = true; break; }
                }
                if (hasNonHydraOk)
                {
                    var noHyd = new System.Collections.Generic.List<Combo>();
                    for (int i = 0; i < src.Count; i++) if (src[i] != null && !src[i].hydra) noHyd.Add(src[i]);
                    if (noHyd.Count > 0) src = noHyd;
                }
            }

            // 价格基准（玩家 2026-11-11 定案）：默认/性价比两档一律按「一晚净赚 = 卖水钱 − 本钱」排名。
            // 起因：性价比档曾选海德拉（60 耐久≈65 元，卖 370.16 / 赚 295.16），
            // 而同一瓶水用过滤棉+紫外只花 18 元、卖 360.88 / 赚 342.63 —— 每升便宜不等于赚得多。

            foreach (var c in src)
            {
                if (!c.ok && mode != 3) continue;
                if (best == null) { best = c; continue; }
                if (mode == 1)
                {
                    // 效率（玩家 2026-11-10 定案）：一晚**净出水最多**，不看性价比。
                    // 药片会把水挤少（2 L 容器 + 药片只出 1.94 L，海德拉能出 2 L），
                    // 所以不能用「接满容器」当第一优先，直接比 outUl；平手才比成本、纯度。
                    if (c.outUl > best.outUl) best = c;
                    else if (c.outUl == best.outUl && c.cost < best.cost) best = c;
                    else if (c.outUl == best.outUl && c.cost == best.cost && c.purityBp > best.purityBp) best = c;
                }
                else if (mode == 0 || mode == 2)
                {
                    // 默认 / 性价比（玩家 2026-11-11 定案）：按**一晚净赚**排（卖水钱 − 本钱）。
                    // 平手才比出水多、再比本钱少。每升成本只用来显示，不再参与挑选。
                    double cp = ProfitOf(c);
                    double bp0 = ProfitOf(best);
                    if (cp > bp0 + 1e-9) best = c;
                    else if (Math.Abs(cp - bp0) <= 1e-9 && c.outUl > best.outUl) best = c;
                    else if (Math.Abs(cp - bp0) <= 1e-9 && c.outUl == best.outUl && c.cost < best.cost) best = c;
                }
                else
                {
                    // 质量：纯度优先，其次水量、耗材
                    if (c.purityBp > best.purityBp) best = c;
                    else if (c.purityBp == best.purityBp && c.outUl > best.outUl) best = c;
                    else if (c.purityBp == best.purityBp && c.outUl == best.outUl && RankCost(c) < RankCost(best)) best = c;
                }
            }
            r.pick = best;
            // 选中方案可能比「什么都不放」那晚多接（后置药片能补最后一点脏度）：
            // 卡一与状态行报的是这条方案真能接的量，所以把 bestUl 顶上去，别让 1850 把 2011 压住。
            if (best != null && best.feedUl > r.bestUl) r.bestUl = best.feedUl;
        }

        /// <summary>一晚净赚：卖水钱 − 本钱（玩家 2026-11-11 定案，默认/性价比两档按它排名）。</summary>
        private static double ProfitOf(Combo c)
        {
            if (c == null || c.outUl <= 0) return double.MinValue;
            return WaterEconomy.Revenue(c.outUl, c.purityBp) - c.cost;
        }

        /// <summary>默认档打分：每升综合成本 / 出水升数（现在只用于显示，不参与挑选）。</summary>
        private static double EffPerLitre(Combo c)
        {
            if (c == null || c.outUl <= 0) return double.MaxValue;
            return RankCost(c) / (c.outUl / 1000000.0);
        }

        private static long MinFeed(Report r, long hi)
        {
            if (hi <= 0) return 0;
            var p0 = WaterMath.Run(hi, r.srcBp, r.perf, r.q, false, false, 0, r.effTargetBp, NightCap, CapOf(r, hi));
            var p1 = WaterMath.Run(hi, r.srcBp, r.perf, r.q, false, false, MaxTablets, r.effTargetBp, NightCap, CapOf(r, hi));
            if (!p0.ok && !p1.ok) return 0;
            long lo = 1, best = hi;
            while (lo <= hi)
            {
                long mid = (lo + hi) / 2;
                if (mid < 1) break;
                var pm = WaterMath.Run(mid, r.srcBp, r.perf, r.q, false, false, 0, r.effTargetBp, 1, CapOf(r, mid));
                bool okm = pm.ok;
                if (!okm)
                {
                    var pt = WaterMath.Run(mid, r.srcBp, r.perf, r.q, false, false, MaxTablets, r.effTargetBp, 1, CapOf(r, mid));
                    okm = pt.ok;
                }
                if (okm) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return best;
        }

        /// <summary>显示用的「真实上限」：不受容器容量限制（网页那边也是这样，6L 容器能算出 6665.8）。</summary>
        public static long FeedCeiling(Report r, bool f, bool u, int t)
        {
            if (r == null) return 0;
            long hiCap = r.capUl > 0 ? r.capUl * 2 : 20000000L;
            if (hiCap < 20000000L) hiCap = 20000000L;
            long best = 0, step = hiCap / 200;
            if (step < 1) step = 1;
            for (long v = step; v <= hiCap; v += step)
            {
                var pv = WaterMath.Run(v, r.srcBp, r.perf, r.q, f, u, t, r.effTargetBp, NightCap, CapOf(r, v));
                if (pv.ok) best = v; else if (best > 0) break;
            }
            if (best <= 0) return 0;
            long lo = best, hi = best + step;
            while (lo < hi)
            {
                long mid = (lo + hi + 1) / 2;
                var pm = WaterMath.Run(mid, r.srcBp, r.perf, r.q, f, u, t, r.effTargetBp, NightCap, CapOf(r, mid));
                if (pm.ok) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>这条方案真能接多少：直接取所有**达标候选**里最大的 feedUl（含海德拉/药片组合）。
        /// 以前卡一用不带海德拉的 FeedCeiling 重算，20 L 容器会报「上限 2665」（玩家 2026-11-10 报的错）。
        /// 候选里一条达标的都没有时才退回旧算法。</summary>
        public static long FeedCeilingOf(Report r)
        {
            if (r == null) return 0;
            long best = 0;
            for (int i = 0; i < r.combos.Count; i++)
            {
                var c = r.combos[i];
                if (c != null && c.ok && c.feedUl > best) best = c.feedUl;
            }
            if (best <= 0) best = FeedCeiling(r, false, false, 0);
            return best;
        }

        private static long MaxFeedCombo(Report r, bool f, bool u, int t)
        {
            // 上限用容器容量（2000 / 6000），不是「什么都不放」那晚的上限：
            // 带过滤棉/药片的方案本来就能多接一些，卡在 1850 会像网页那样少报一百多毫升。
            long hiCap = r.capUl > 0 ? r.capUl : r.ShownUl();
            if (hiCap <= 0) return 0;
            // 先 121 点粗扫（网页 solveRun 也是粗扫 + 二分），因为「药片能补多少」随体积不是单调的；
            // 粗扫找不到任何达标点才认为这条配方做不了。
            long best = 0, step = hiCap / 120;
            if (step < 1) step = 1;
            for (long v = step; v <= hiCap; v += step)
            {
                var pv = WaterMath.Run(v, r.srcBp, r.perf, r.q, f, u, t, r.effTargetBp, NightCap, CapOf(r, v));
                if (pv.ok) best = v;
            }
            var pLast = WaterMath.Run(hiCap, r.srcBp, r.perf, r.q, f, u, t, r.effTargetBp, NightCap, CapOf(r, hiCap));
            if (pLast.ok) return hiCap;
            if (best <= 0) return 0;
            // 在最优粗扫点与它的上一格之间二分，把几十毫升误差收掉
            long lo = best, hi = best + step;
            if (hi > hiCap) hi = hiCap;
            while (lo < hi)
            {
                long mid = (lo + hi + 1) / 2;
                var pm = WaterMath.Run(mid, r.srcBp, r.perf, r.q, f, u, t, r.effTargetBp, NightCap, CapOf(r, mid));
                if (pm.ok) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        // ---------------- 海德拉（便携净水器）：空容器接水路径的预处理维 ----------------

        /// <summary>「先海德拉洗成 98% → 过滤/紫外 → N 晚 → 药片」跑一遍的结果。</summary>
        private class HydraRun
        {
            public bool ok;
            public long outUl;
            public int purityBp;
            public int nights;
            public int filterUsed;
            public int hydraDur;
        }

        /// <summary>接 feedUl 生水后先过海德拉，再走过滤/紫外/过夜/药片。海德拉洗不动时 hydraDur=0。</summary>
        private static HydraRun RunHydra(Report r, long feedUl, bool f, bool u, int t)
        {
            var res = new HydraRun();
            if (r == null || feedUl <= 0) return res;
            var w = WaterMath.Build(feedUl, r.srcBp, CapOf(r, feedUl));
            int d = WaterMath.Hydra(w);
            if (d <= 0) return res;                       // >30 L，或这瓶水本来就比 98% 干净
            res.hydraDur = d;
            if (f) res.filterUsed = WaterMath.Filter(w, FilterDur);
            if (u) WaterMath.Uv(w, 1);
            int n = 0;
            // 玩家 2026-11-10 定案：**全 mod 只用一晚模式**，不要多晚方案（多晚看起来跟别的方案不一致）。
            // 这里跟生水路径同一个口径：NightCap 晚（默认 1）。
            while (n < (NightCap > 0 ? NightCap : 1))
            {
                if (w.Purity >= r.effTargetBp) break;
                if (WaterMath.Purify(w, r.perf, r.q) <= 0) break;
                n++;
            }
            if (n < 1) n = 1;                             // 过关那晚总要跑
            if (t > 0) WaterMath.Tablet(w, t);
            res.nights = n; res.outUl = w.v; res.purityBp = w.Purity;
            res.ok = w.Purity >= r.effTargetBp;
            return res;
        }

        /// <summary>带海德拉时最多能接多少：容器容量、设置里的耐久上限、**手上实际剩的耐久**三者取小。</summary>
        private static long MaxFeedComboHydra(Report r, bool f, bool u, int t, int ownHydraDur)
        {
            long hiCap = r.capUl > 0 ? r.capUl : r.ShownUl();
            if (hiCap <= 0) return 0;
            // 玩家口径：一只海德拉真实 300 耐久（1 点 = 100 mL），但可以多买几只叠加。
            // 设置里的「耐久上限」就是这个 allowance：300 / 600 / 900 / 0=不限（不限时按容器算）。
            // 玩家 2026-11-11：无限口径下海德拉不限，拉条只管真实道具的手操耐久，这里直接给理论上限。
            // 玩家 2026-11-11 定口径：拖过滑条以滑条为准；没拖过按背包真实耐久；无限口径不按耐久卡。
            long allowance = HydraAllowance(hiCap, ownHydraDur);
            long hydraMax = allowance * WaterMath.HYDRA_UL_PER_DUR;
            if (hiCap > hydraMax) hiCap = hydraMax;
            long best = 0, step = hiCap / 32;   // 带海德拉每格要跑最多 8 晚，粗扫格数比生水路径少（后面还有二分）
            if (step < 1) step = 1;
            for (long v = step; v <= hiCap; v += step)
            {
                if (RunHydra(r, v, f, u, t).ok) best = v;
            }
            if (best <= 0) return 0;
            long lo = best, hi2 = best + step;
            if (hi2 > hiCap) hi2 = hiCap;
            while (lo < hi2)
            {
                long mid = (lo + hi2 + 1) / 2;
                if (RunHydra(r, mid, f, u, t).ok) lo = mid; else hi2 = mid - 1;
            }
            return lo;
        }

        private static string PreLabel(bool f, bool u)
        {
            if (f && u) return "过滤 + 紫外";
            if (f) return "只放过滤棉";
            if (u) return "只放紫外";
            return "什么都不放";
        }

        private static void BuildCombos(Report r, int pre, int ownTablets, bool ownFilter, bool ownUv, int ownHydraDur)
        {
            // 生水已经比 98% 干净时海德拉洗不动（Hydra 直接返回 0），整维不用建 —— 少一半扫描。
            // 性价比档按「实际持知道具」建：没药片/没滤棉/没紫外/没海德拉耐久就不建对应的组合。
            bool tryHydra = r.srcBp < WaterMath.HYDRA_BASE_BP && ownHydraDur != 0;
            int maxTab = MaxTablets;
            if (ownTablets >= 0 && ownTablets < maxTab) maxTab = ownTablets;
            for (int fi = 0; fi < 2; fi++)
            {
                if (fi == 1 && !ownFilter) continue;
                for (int ui = 0; ui < 2; ui++)
                {
                    if (ui == 1 && !ownUv) continue;
                    // 与网页一致：none / filter / uv / filter+uv 四种全建（网页 pres 里 uv 是独立一项），
                    // 具体选哪条交给 FilterByPrePost；以前 auto 下不建 uv-only，导致「紫外+药」永远出不来。
                    for (int t = 0; t <= maxTab; t++)
                    {                        // 「什么都不放 + 不放药片」这一条必须留着：它就是「原样跑一晚」，
                        // 玩家把前置选成不处理、后置选成不放药片时唯一可行的方案。
                        long feed = MaxFeedCombo(r, fi == 1, ui == 1, t);
                        if (feed > 0)
                        {
                            // 模型上限是 1857 mL，但玩家只能按整瓶倒：实际接得进去的是「整瓶凑得出」的那个量（1850）。
                            // 表里报上限会让玩家照着接却接不满，所以这里先把量收口到可倒出的值，再用它算出来。
                            var pour = Plan(feed);
                            if (pour != null && pour.totalUl > 0 && pour.totalUl < feed) feed = pour.totalUl;
                            var p = WaterMath.Run(feed, r.srcBp, r.perf, r.q, fi == 1, ui == 1, t, r.effTargetBp, 1, CapOf(r, feed));
                            var c = new Combo();
                            c.filter = fi == 1; c.uv = ui == 1; c.tablets = t;
                            // 什么都不放、也不放药片 = 网页里那条「原样跑一晚」，名字直接照网页说
                            string baseLabel = (!c.filter && !c.uv && t == 0) ? "原样跑一晚" : PreLabel(c.filter, c.uv);
                            c.label = baseLabel + (t > 0 ? " + " + t + " 片药片（过夜后放）" : "");
                            c.feedUl = feed; c.outUl = p.output; c.purityBp = p.purity; c.ok = p.ok;
                            c.filterUsed = p.filterUsed;
                            c.cost = t * TabletCost + (c.filter ? (p.filterUsed / (double)FilterDurPer) * FilterCost : 0) +
                                     EnergyPerNight;
                            r.combos.Add(c);
                        }

                        // 先用海德拉把生水洗成 98% 基础水，再走过滤/紫外/过夜/药片。
                        // 空容器接水路径以前完全没有这一维 —— 20 L 这种「必须靠海德拉先洗一遍」的量永远算不出来
                        // （玩家 2026-11-10 用 20 L 容器实测：一条海德拉方案都没有，拉不满容器）。
                        if (!tryHydra) continue;
                        long feedH = MaxFeedComboHydra(r, fi == 1, ui == 1, t, ownHydraDur);
                        if (feedH <= 0) continue;
                        var pourH = Plan(feedH);
                        if (pourH != null && pourH.totalUl > 0 && pourH.totalUl < feedH) feedH = pourH.totalUl;
                        var hr = RunHydra(r, feedH, fi == 1, ui == 1, t);
                        if (!hr.ok) continue;
                        // 设置项「海德拉耐久上限」：超过就不进候选（可买多只叠加：300/600/900/0=不限）。
                        // 玩家 2026-11-11 定口径：滑条 > 0 时**以滑条为准**（那是玩家声明要按多少算），
                        // 不再被背包里的真实耐久 100 压回 100；滑条 = 0（不限）才看背包实际持有。
                        if (ownHydraDur != -1)
                        {
                            long allow = HydraAllowance(r.capUl > 0 ? r.capUl : r.ShownUl(), ownHydraDur);
                            if (hr.hydraDur > allow) continue;   // 超过这一趟允许的耐久就不进候选
                        }
                        var ch = new Combo();
                        ch.filter = fi == 1; ch.uv = ui == 1; ch.tablets = t;
                        ch.hydra = true; ch.hydraDur = hr.hydraDur;
                        string baseH = (!ch.filter && !ch.uv && t == 0) ? "过夜净化" : PreLabel(ch.filter, ch.uv);
                        ch.label = "海德拉(" + hr.hydraDur + "耐久) + " + baseH +
                                   (t > 0 ? " + " + t + " 片药片（过夜后放）" : "") +
                                   (hr.nights > 1 ? "（" + hr.nights + " 晚）" : "");
                        ch.feedUl = feedH; ch.outUl = hr.outUl; ch.purityBp = hr.purityBp; ch.ok = true;
                        ch.nights = hr.nights;
                        ch.filterUsed = hr.filterUsed;
                        ch.cost = t * TabletCost + (ch.filter ? (hr.filterUsed / (double)FilterDurPer) * FilterCost : 0)
                                 + (hr.hydraDur / (double)WaterMath.HYDRA_MAX_DUR) * HydraCost
                                 + EnergyPerNight * hr.nights;
                        r.combos.Add(ch);
                    }
                }
            }

            // 玩家：同一「过滤/紫外 + 同一接水量」下，多加药片却没提升纯度的方案不显示
            // （1 片就到上限时，2、3 片纯亏钱）。只跟**同 feed、少药片**的比，避免误杀不同水量的方案。
            var kept = new List<Combo>();
            for (int i = 0; i < r.combos.Count; i++)
            {
                var c = r.combos[i];
                bool waste = false;
                if (c.tablets > 0)
                {
                    for (int k = 0; k < kept.Count; k++)
                    {
                        var b = kept[k];
                        if (b.filter != c.filter || b.uv != c.uv || b.hydra != c.hydra || b.nights != c.nights) continue;
                        if (b.feedUl != c.feedUl) continue;
                        if (b.tablets >= c.tablets) continue;
                        if (c.purityBp <= b.purityBp) { waste = true; break; }
                    }
                }
                if (!waste) kept.Add(c);
            }
            r.combos = kept;
#if DEV
            // 玩家 2026-11-11 报「效率档不再用海德拉拉满容器」：把候选构成打出来，
            // 分清是「一条海德拉方案都没建出来」还是「建出来了但被判不达标/耐久不够」。
            int nH = 0, nOk = 0, nHOk = 0; long maxHFeed = 0, maxHOut = 0;
            for (int i = 0; i < r.combos.Count; i++)
            {
                var cc = r.combos[i];
                if (cc == null) continue;
                if (cc.ok) nOk++;
                if (!cc.hydra) continue;
                nH++;
                if (cc.ok) nHOk++;
                if (cc.feedUl > maxHFeed) maxHFeed = cc.feedUl;
                if (cc.outUl > maxHOut) maxHOut = cc.outUl;
            }
            PurifierUi.Log("[combos] src=" + r.srcBp + " eff=" + r.effTargetBp + " cap=" + WaterMath.CapOf(r.q)
                           + " ownDur=" + ownHydraDur + " tryHydra=" + tryHydra
                           + " all=" + r.combos.Count + " ok=" + nOk
                           + " hydra=" + nH + " hydraOk=" + nHOk
                           + " maxHFeed=" + maxHFeed + " maxHOut=" + maxHOut);
#endif
        }

        private static void BuildTabletsOnly(Report r)
        {
            var sb = new StringBuilder();
            sb.Append("只放药片、不过净水器：");
            for (int t = 1; t <= MaxTablets; t++)
            {
                var w = WaterMath.Build(r.useUl, r.srcBp, CapOf(r, r.useUl));
                WaterMath.Tablet(w, t);
                if (t > 1) sb.Append(" · ");
                sb.Append(t).Append(" 片 → ").Append(Ml(w.v)).Append(" mL ").Append(PlanText.Pct(w.Purity));
            }
            r.tabletsOnly = sb.ToString();
        }

        // ---------------- 对外入口 ----------------

        /// <summary>
        /// 容器里**已经有水**时（玩家自己调的/混过的液体）：按这瓶水的**真实六项配比**出方案，
        /// 而不是拿「今日水龙头」重建一瓶生水去算「接多少」。
        /// 玩家 2026-11-09 报：容器里 3650 mL·94.79%，卡一/卡二还在按今日铁锈水报「接 1845 → 出 1800」。
        /// 这里枚举 过滤/紫外 × 药片 × 晚数，取「达标」的全集，再按模式挑一条。
        /// </summary>
        public static Report BuildFromWater(WaterMath.Water w0, int perf, int q, int targetBp,
                                            int mode, int pre, int post, string bottleName)
        {
            var full = BuildFromWaterCore(w0, perf, q, targetBp, mode, pre, post, bottleName);
            if (full == null) return null;
            full.fitUl = w0.v; full.dumpUl = 0; full.fitPartial = false;
            if (full.pick != null) return full;
            // ★玩家 2026-11-10：瓶里已有的水按它自己的水质算，但满瓶 6000 mL 不一定一晚压得到目标。
            // 这时不能只说「装满了可以直接跑」，要回答「先倒出多少、留多少刚好够一晚」。
            // 做法：同一六项配比等比缩小水样（缩小不改纯度），二分找「一晚还能压到目标」的最大体积。
            long lo = 0L, hi = w0.v, keepV = 0L;
            Report keepRep = null;
            for (int it = 0; it < 24 && hi - lo > 999L; it++)
            {
                long mid = lo + (hi - lo) / 2;
                var wm = ScaleWater(w0, mid);
                if (wm == null) { hi = mid; continue; }
                var rep = BuildFromWaterCore(wm, perf, q, targetBp, mode, pre, post, bottleName);
                if (rep != null && rep.pick != null) { keepRep = rep; keepV = wm.v; lo = mid; }
                else hi = mid;
            }
            if (keepRep != null && keepV > 0)
            {
                keepRep.fitUl = keepV;
                keepRep.dumpUl = w0.v - keepV;
                keepRep.fitPartial = keepRep.dumpUl > 0;
                if (keepRep.dumpUl > 0)
                {
                    var sd = new Step();
                    sd.title = "先倒出 " + Ml0(keepRep.dumpUl) + " mL，容器里只留 " + Ml0(keepV) + " mL";
                    sd.detail = "整瓶 " + Ml0(w0.v) + " mL 一晚压不到 " + PlanText.Pct(targetBp) +
                                "；留这么多刚好够。倒出来的水先存别的瓶里，别倒了";
                    if (keepRep.steps == null) keepRep.steps = new List<Step>();
                    keepRep.steps.Insert(0, sd);
                }
                return keepRep;
            }
            // 连一口都压不到目标：仍按这瓶水的真实配比回报（pick 留空，界面据此明说做不了）
            full.fitUl = 0L; full.dumpUl = w0.v; full.fitPartial = true;
            if (full.steps != null) full.steps.Clear();
            var s0 = new Step();
            s0.title = "这瓶水一晚压不到 " + PlanText.Pct(targetBp);
            s0.detail = "按这瓶水的水质怎么跑都不达标：兑入更高纯度的水、加药片，或先换紫外灯/过滤棉再算";
            if (full.steps == null) full.steps = new List<Step>();
            full.steps.Add(s0);
            return full;
        }

        /// <summary>按同一六项配比把水样缩到 newV（µL）：只是「少装一点」，纯度不变。</summary>
        private static WaterMath.Water ScaleWater(WaterMath.Water w, long newV)
        {
            if (w == null || w.v <= 0 || newV <= 0) return null;
            if (newV >= w.v) return w.Clone();
            var c = new WaterMath.Water();
            c.v = newV;
            double k = (double)newV / (double)w.v;
            foreach (var kv in w.parts)
            {
                long nv = (long)(kv.Value * k);
                if (nv > 0) c.parts[kv.Key] = nv;
            }
            return c;
        }

        private static Report BuildFromWaterCore(WaterMath.Water w0, int perf, int q, int targetBp,
                                                 int mode, int pre, int post, string bottleName)
        {
            if (w0 == null || w0.v <= 0) return null;
            var r = new Report();
            r.fromWater = true;
            r.water0 = w0.Clone();
            r.mode = mode;
            r.perf = perf; r.q = q;
            r.bottleName = string.IsNullOrEmpty(bottleName) ? "容器" : bottleName;
            r.srcName = "容器里已有的水";
            r.srcBp = w0.Purity;
            r.capUl = w0.v; r.bestUl = w0.v; r.useUl = w0.v;
            int cap = WaterMath.CapOf(q);
            // 玩家 2026-11-11：目标设「纯水」而机器上限只有 99.01% 时，必须说「机器质量不足」，
            // 不能悄悄夹回 cap 还报「能到」。capShort 供 Headline 说明，effTarget 只用于可行性判断。
            r.capShort = targetBp > cap;
            int effTarget = targetBp > cap ? cap : targetBp;
            r.targetBp = targetBp;
            r.effTargetBp = effTarget;
            r.contamInUl = w0.Impurity;

            for (int h = 0; h <= 1; h++)
            {
                for (int f = 0; f <= 1; f++)
                {
                    for (int u = 0; u <= 1; u++)
                    {
                        for (int t = 0; t <= MaxTablets; t++)
                        {
                            var c = ComboFromWater(w0, h == 1, f == 1, u == 1, t, effTarget, perf, q);
                            if (c != null) r.combos.Add(c);
                        }
                    }
                }
            }
            // 同一「耗材组合」下跑更多晚却没更纯的，不显示（和生水方案表同一口径）
            var kept = new List<Combo>();
            for (int i = 0; i < r.combos.Count; i++)
            {
                var c = r.combos[i];
                bool worse = false;
                for (int k = 0; k < kept.Count; k++)
                {
                    var b = kept[k];
                    if (b.filter != c.filter || b.uv != c.uv || b.tablets != c.tablets || b.hydra != c.hydra) continue;
                    if (b.nights <= c.nights && b.purityBp >= c.purityBp && b.outUl >= c.outUl) { worse = true; break; }
                }
                if (!worse) kept.Add(c);
            }
            r.combos = kept;
            // 前置/后置必须同样生效（玩家反复报「选了没反应」）；筛空时保留全量并置 filterEmpty 让界面说明。
            FilterByPrePost(r, pre, post);

            // 海德拉门（玩家口径）：②「完全不用」一律剔除；①「自动」＝只有不用海德拉做不到目标时才允许出现。
            // 以前 BuildFromWater 完全不看 HydraMode，60% 的水也直接推「海德拉 60 耐久」，玩家的设置等于没效。
            if (HydraMode == 2)
            {
                r.combos.RemoveAll(delegate(Combo c) { return c.hydra; });
                r.keep.RemoveAll(delegate(Combo c) { return c.hydra; });
            }
            else if (HydraMode == 0)
            {
                bool noHydraOk = false;
                for (int i = 0; i < r.combos.Count; i++) { if (!r.combos[i].hydra) { noHydraOk = true; break; } }
                if (noHydraOk)
                {
                    r.combos.RemoveAll(delegate(Combo c) { return c.hydra; });
                    r.keep.RemoveAll(delegate(Combo c) { return c.hydra; });
                }
            }

            // 按模式挑：0 一晚就要 / 1 效率（出水最大，同量取省） / 2 性价比（每升最省） / 3 质量（纯度优先）
            Combo best = null;
            var src = r.keep.Count > 0 ? r.keep : r.combos;
            for (int i = 0; i < src.Count; i++)
            {
                var c = src[i];
                if (best == null) { best = c; continue; }
                if (mode == 2)
                {
                    double pl = c.outUl > 0 ? c.cost / (c.outUl / 1000000.0) : double.MaxValue;
                    double bl = best.outUl > 0 ? best.cost / (best.outUl / 1000000.0) : double.MaxValue;
                    if (pl < bl - 1e-9 || (Math.Abs(pl - bl) <= 1e-9 && c.outUl > best.outUl)) best = c;
                }
                else if (mode == 3)
                {
                    if (c.purityBp > best.purityBp ||
                        (c.purityBp == best.purityBp && (c.nights < best.nights || c.outUl > best.outUl))) best = c;
                }
                else if (mode == 0)
                {
                    bool cOne = c.nights <= 1, bOne = best.nights <= 1;
                    if ((cOne && !bOne) || (cOne == bOne && (c.outUl > best.outUl ||
                        (c.outUl == best.outUl && c.purityBp > best.purityBp)))) best = c;
                }
                else
                {
                    if (c.outUl > best.outUl || (c.outUl == best.outUl && c.cost < best.cost)) best = c;
                }
            }
            r.pick = best;
            if (best == null) { r.ok = false; return r; }
            r.ok = true;

            r.outUl = best.outUl;
            r.finalBp = best.purityBp;
            r.tablets = best.tablets;
            r.filterUsed = best.filterUsed;
            r.uvUsed = best.uv ? 1 : 0;
            r.nightsCount = best.nights;
            r.cost = best.cost;
            r.perL = r.outUl > 0 ? r.cost / (r.outUl / 1000000.0) : 0;
            SimulateStepsFromWater(r, best);
            return r;
        }

        /// <summary>从真实水团出发模拟一条方案：海德拉 → 过滤/紫外 → N 晚 → 药片（游戏里药片是过夜后放）。</summary>
        private static Combo ComboFromWater(WaterMath.Water w0, bool h, bool f, bool u, int t,
                                            int targetBp, int perf, int q)
        {
            var w = w0.Clone();
            int hDur = 0;
            if (h)
            {
                hDur = WaterMath.Hydra(w);
                if (hDur <= 0) return null;          // 用不了（>33 L 或本来更干净）
            }
            int fUsed = 0;
            if (f) fUsed = WaterMath.Filter(w, FilterDur);
            if (u) WaterMath.Uv(w, 1);
            int nights = 0;
            // 一晚制：与接水路径同口径，NightCap（默认 1）晚拿不到目标就不算方案。
            while (nights < (NightCap > 0 ? NightCap : 1))
            {
                if (w.Purity >= targetBp) break;
                long cut = WaterMath.Purify(w, perf, q);
                nights++;
                if (cut <= 0) break;
            }
            if (nights < 1) nights = 1;
            // 玩家 2026-11-11 定案：**公式不要动**（网页 V3.6 的算法是对的），别再事后夹纯度。
            // 之前「一晚 99.92%」是接口问题——读进来的水被判成 100%（见 BuildRealWater）。
            if (t > 0) WaterMath.Tablet(w, t);
            if (w.Purity < targetBp) return null;
            var c = new Combo();
            c.filter = f; c.uv = u; c.tablets = t; c.nights = nights; c.hydra = h; c.hydraDur = hDur;
            string baseLabel = (!f && !u && t == 0) ? "原样跑一晚" : PreLabel(f, u);
            c.label = (h ? "海德拉(" + hDur + "耐久) + " : "")
                    + baseLabel + (t > 0 ? " + " + t + " 片药片（过夜后放）" : "")
                    + (nights > 1 ? "（" + nights + " 晚）" : "");
            c.feedUl = w0.v; c.outUl = w.v; c.purityBp = w.Purity; c.ok = true;
            c.filterUsed = fUsed;
            c.cost = t * TabletCost + (f ? (fUsed / (double)FilterDurPer) * FilterCost : 0)
                     + (h ? (hDur / (double)WaterMath.HYDRA_MAX_DUR) * HydraCost : 0)
                     + EnergyPerNight * nights;
            return c;
        }

        /// <summary>「步骤」页：已有水的版本（不写「接水」，改写「已有水」）。</summary>
        private static void SimulateStepsFromWater(Report r, Combo pick)
        {
            r.steps.Clear();
            r.nights.Clear();
            var w = r.water0 != null ? r.water0.Clone() : WaterMath.Build(pick.feedUl, r.srcBp, CapOf(r, pick.feedUl));
            r.contamInUl = w.Impurity;
            var s0 = new Step();
            s0.title = "已有水 " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity) + "（不用再接水）";
            s0.detail = "按这瓶水的真实六项配比算";
            r.steps.Add(s0);
            if (pick.hydra)
            {
                int d = WaterMath.Hydra(w);
                var sh = new Step();
                sh.title = "海德拉 → " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity);
                sh.detail = "洗成 98% 基础水（化学 1.60% · 重金属 0.40%），耗耐久 " + d + " 点";
                r.steps.Add(sh);
            }
            if (pick.filter)
            {
                int used = WaterMath.Filter(w, FilterDur);
                var st = new Step();
                st.title = "过滤棉 → " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity);
                st.detail = "耗耐久 " + used + " 点";
                r.steps.Add(st);
            }
            if (pick.uv)
            {
                WaterMath.Uv(w, 1);
                var st = new Step();
                st.title = "紫外灯 → " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity);
                st.detail = "微生物转纯水，体积不变（工具，不消耗）";
                r.steps.Add(st);
            }
            int total = pick.nights > 0 ? pick.nights : 1;
            for (int n = 1; n <= total; n++)
            {
                WaterMath.Purify(w, r.perf, r.q);
                var st = new Step();
                st.title = "第 " + n + " 晚 → " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity);
                st.detail = "本晚目标 " + PlanText.Pct(r.effTargetBp);
                r.nights.Add(new NightRow { n = n, volUl = w.v, purityBp = w.Purity });
                r.steps.Add(st);
            }
            if (pick.tablets > 0)
            {
                long before = w.v;
                WaterMath.Tablet(w, pick.tablets);
                var st = new Step();
                st.title = "最后 " + pick.tablets + " 片药片 → " + Ml(w.v) + " mL · " + PlanText.Pct(w.Purity);
                st.detail = "带走 " + Ml2(before - w.v) + " mL 杂质";
                r.steps.Add(st);
            }
            r.outUl = w.v;
            r.finalBp = w.Purity;
            r.contamOutUl = w.Impurity;
        }

        public static Report Build(long capUl, int srcBp, int perf, int q, int targetBp,
                                   string srcName, string bottleName, bool full, int mode = 0,
                                   int pre = 0, int post = 0,
                                   int ownTablets = -1, bool ownFilter = true, bool ownUv = true,
                                   int ownHydraDur = -1)
        {
            // 每次整包重算开始都作废上次的库存快照：这期间玩家可能刚接过水、挪过瓶子。
            scanSession++;
            scanMemo = null;
            try { return BuildReport(capUl, srcBp, perf, q, targetBp, srcName, bottleName, full, mode, pre, post, ownTablets, ownFilter, ownUv, ownHydraDur); }
            catch (Exception e)
            {
                PurifierUi.LogQuiet("[plan] ERR " + e.GetType().Name + " " + e.Message);
                return null;
            }
            finally
            {
                scanSession--;
                if (scanSession <= 0) { scanSession = 0; scanMemo = null; }
            }
        }
    }
}
