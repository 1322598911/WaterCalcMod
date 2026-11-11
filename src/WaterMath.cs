using System;
using System.Collections.Generic;

namespace WaterCalcMod
{
    /// <summary>
    /// 水体计算核心：全部单位 μL、整数截断。
    /// 逐行对齐网页版 V3.6（`净水计算器_V3.6_正式版.html` 的 buildWater/purify/runFilter/runTablet/runUv/runOnce/solveRun），
    /// 网页是唯一口径，改动前先改网页再同步这里。
    /// </summary>
    public static class WaterMath
    {
        /// <summary>方案排序偏好：true=省耗材优先（宁愿多倒水），false=少倒水优先（可用药片/耗材）。</summary>
        public static bool PreferNoConsumable = true;

        /// <summary>过滤棉可用总耐久（由 WaterPlan.FilterDur 写入，= 玩家选用的那几块滤棉的实际耐久和）。
        /// 离线对拍脚本自己设它。</summary>
        public static int FilterBudget = 400;

        // 药片真值（玩家实测口径，与网页 V3.6 runTablet 逐位一致）：
        // 每片**一份共用预算** 76,950 μL（15 mL × 5.13）—— 先清化学，化学只能清到总量的 0.7%（floor），
        // 余力再清微生物；清掉的量离体（体积减），水本身不变。
        // 依据：①玩家实测 1 片把 4035.8 mL@98.28% 变成 3995 mL@99.29%（清掉 40.8 mL 化学，正卡在 0.7% 下限）；
        // ②2000 mL 铁锈水 + 1 片 → 1921 mL（−79 mL ≈ 预算上限）；③网页 6000 mL 过滤+紫外+1 晚+1 片 → 99.22%。
        // 曾经的「微生物 75 mL + 化学 10 mL 两份独立预算 / 化学下限 0.05%」来自发行版里**零调用点**的死方法
        // OnWaterPurificationTabletAdded{,2}，把药片收益缩到 1/5（一晚上限只剩 3211 mL），玩家实测直接否掉。
        public const long TAB_BUDGET = 76950;     // 每片共用预算（μL）
        public const double TAB_KEEP = 0.007;     // 化学清到总量的 0.7% 后停手
        // 药片本体的 15 mL 氯：先占体积、反应后整份消耗（净体积不变），但它是**化学污染物**，
        // 所以每片要让化学从「原有+15000」清到 floor —— 玩家 2000 mL 实验正好验证：
        // 化学 39440 → 54440，floor 14000 → 清掉 40440，体积 2000-40.44=1959.56，
        // 化学剩 14000 μL、纯度 1945.56/1959.56=99.2857→99.28，与实测 1959 mL/14000 μL 逐位一致。
        public const long TAB_CHLORINE_UL = 15000;   // 每片自带 15 mL 氯（算进要清的化学里）

        // 去除顺序（网页 KEYS）：粒径大的先被清，mineral/heavy/chemical 排后面
        private static readonly string[] Order = { "physical", "microbe", "organic", "mineral", "heavy", "chemical" };

        // 生成时的分摊顺序（网页 buildWater 的 order）
        private static readonly string[] BuildOrder = { "organic", "microbe", "physical", "chemical", "mineral", "heavy" };

        // 六项默认比例（2:4:3:6:2:3，合计 20 → 0.10/0.20/0.15/0.30/0.10/0.15）
        private static readonly Dictionary<string, double> Dist = new Dictionary<string, double>
        {
            { "heavy", 0.10 }, { "organic", 0.20 }, { "microbe", 0.15 },
            { "chemical", 0.30 }, { "physical", 0.10 }, { "mineral", 0.15 }
        };

        // 过滤棉真值（WaterHelper::HandleFilter + Liquid::InitLiquid/.ctor，玩家实测 3 L → 400→291）：
        // 每次最多过 3 L（超过就按 3 L 一份逐份过，耐久累加）；每份里只动 physical/organic/mineral ——
        //   floor = trunc(amount × filterMinimumPurity)          （三项都是 0.16%）
        //   每次扣 filterDurabilityUsage 耐久、清掉 filterVolumeRemoved 容积，直到 amount ≤ floor
        //   physical: 2 点/1000 μL→ 实际 1500；organic: 1 点/1000 μL；mineral: 1 点/2000 μL
        // 清掉的量离体（体积减），化学/微生物/重金属一概不动。
        // 验算 3 L 铁锈水 91.31%：物理 18×2=36、有机 53×1=53、矿物 20×1=20 → 109 点，剩 2.88 L/95.01%。
        private const long FILTER_BATCH_UL = 3000000;   // 一次最多 3 L
        private static readonly Dictionary<string, long> FilterPer = new Dictionary<string, long>
        {
            { "physical", 1500 }, { "organic", 1000 }, { "mineral", 2000 }
        };
        private static readonly Dictionary<string, int> FilterDurPer = new Dictionary<string, int>
        {
            { "physical", 2 }, { "organic", 1 }, { "mineral", 1 }
        };
        private const double FILTER_MIN_PURITY = 0.0016;

        private static readonly string[] FilterKeys = { "physical", "organic", "mineral" };

        public class Water
        {
            public long v;                                              // 总体积
            public long cap;                                            // 容器容量（0=未知）：药片 15 mL 满瓶会挤出
            public Dictionary<string, long> parts = new Dictionary<string, long>();

            public long WaterPart
            {
                get { long s = 0; foreach (var kv in parts) s += kv.Value; return v - s; }
            }
            public long Impurity
            {
                get { long s = 0; foreach (var kv in parts) s += kv.Value; return s; }
            }
            public int Purity
            {
                get { if (v <= 0) return 0; return (int)(WaterPart * 10000 / v); } // 网页 purityOf：floor
            }
            public Water Clone()
            {
                var c = new Water { v = v, cap = cap };
                foreach (var kv in parts) c.parts[kv.Key] = kv.Value;
                return c;
            }
            public long Part(string k)
            {
                long a;
                parts.TryGetValue(k, out a);
                return a;
            }
        }

        // ================= 海德拉（便携净水器 portable_water_purifier） =================
        // 依据 WaterHelper::PurifyToBaseWater 的 IL：
        //   total = 当前总量；target = ceil(0.98 × total)（水的目标量）
        //   水的量已 ≥ target → 直接返回（洗不了，本来就更干净）
        //   保留 chemical_contaminant / heavy_metal 两项的量，其余四项按比例清掉；
        //   四项不够清时继续按比例清化学与重金属。总量不变 → 纯度正好 98.00%。
        // 耐久：1 点 = 100 mL，满耐久 330 点 = 33 L，超过 33 L 用不了（玩家实测口径）。
        public const long HYDRA_UL_PER_DUR = 100000;   // 每点耐久处理 100 mL
        public const int HYDRA_MAX_DUR = 300;          // 玩家实测：整只 300 耐久、325 元 → 最多 30 L
        public const int HYDRA_BASE_BP = 9800;         // 出水分 98.00%

        private static readonly string[] AllKeys = { "heavy", "organic", "microbe", "chemical", "physical", "mineral" };
        private static readonly string[] HydraKeep = { "chemical", "heavy" };
        private static readonly string[] HydraDrop = { "organic", "microbe", "physical", "mineral" };

        /// <summary>用一次海德拉要扣多少耐久（按总量算，1 点 / 100 mL，向上取整）。</summary>
        public static int HydraDurability(Water w)
        {
            if (w == null || w.v <= 0) return 0;
            long d = (w.v + HYDRA_UL_PER_DUR - 1) / HYDRA_UL_PER_DUR;
            return d > int.MaxValue ? int.MaxValue : (int)d;
        }

        /// <summary>把这瓶水洗成基础水（98%）。返回实际扣的耐久；0 = 用不了/没变化。</summary>
        public static int Hydra(Water w)
        {
            if (w == null || w.v <= 0) return 0;
            int dur = HydraDurability(w);
            if (dur <= 0 || dur > HYDRA_MAX_DUR) return 0;          // 超过 33 L 一次洗不完
            long target = (w.v * HYDRA_BASE_BP + 9999L) / 10000L;    // 水的目标量
            if (w.WaterPart >= target) return 0;                      // 已经比基础水更纯
            long allowedImp = w.v - target;
            long imp = w.Impurity;
            long need = imp - allowedImp;
            if (need <= 0) return 0;
            // 先清「物理/有机/微生物/矿物」四项，不够再清化学与重金属
            long drop = 0;
            for (int i = 0; i < HydraDrop.Length; i++) drop += w.Part(HydraDrop[i]);
            long cutDrop = Math.Min(drop, need);
            ScaleParts(w, HydraDrop, drop, drop - cutDrop);
            long rest = need - cutDrop;
            if (rest > 0)
            {
                long keep = 0;
                for (int i = 0; i < HydraKeep.Length; i++) keep += w.Part(HydraKeep[i]);
                ScaleParts(w, HydraKeep, keep, Math.Max(0L, keep - rest));
            }
            // 取整尾差：把总量杂质精确压到 allowedImp（保证纯度正好 98.00%）
            ScaleParts(w, AllKeys, w.Impurity, allowedImp);
            return dur;
        }

        /// <summary>把 keys 这些杂质从 from 按比例缩到 to（逐项 floor，尾差补在最后一项）。</summary>
        private static void ScaleParts(Water w, string[] keys, long from, long to)
        {
            if (from <= 0 || to < 0) return;
            if (to > from) to = from;
            long acc = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                long old = w.Part(keys[i]);
                long nv = (long)Math.Floor(old * (double)to / from);
                if (nv < 0) nv = 0;
                if (nv > old) nv = old;
                w.parts[keys[i]] = nv;
                acc += nv;
            }
            long diff = to - acc;
            for (int i = 0; diff != 0 && i < keys.Length; i++)
            {
                long old = w.Part(keys[i]);
                if (diff > 0) { w.parts[keys[i]] = old + diff; diff = 0; }
                else { long take = Math.Min(old, -diff); w.parts[keys[i]] = old - take; diff += take; }
            }
        }

        /// <summary>按体积与纯度（万分比）生成六项杂质（网页 buildWater）。</summary>
        public static Water Build(long volume, int purity) { return Build(volume, purity, 0); }

        /// <summary>containerCap &gt; 0 = 真实容器容量（µL）。只有容器装满时药片 15 mL 才挤出水，
        /// 没装满就只是把水位加上去（玩家 m00160：接 2400 进 6 L 瓶，不该按满瓶算溢出）。</summary>
        public static Water Build(long volume, int purity, long containerCap)
        {
            double frac = Math.Max(0.0, Math.Min(1.0, purity / 10000.0));
            var w = new Water { v = Math.Max(0, volume) };
            w.cap = containerCap > 0 ? containerCap : w.v;
            long water = Math.Min(w.v, (long)Math.Floor(w.v * frac));
            long imp = Math.Max(0, w.v - water);
            long used = 0;
            for (int i = 0; i < BuildOrder.Length; i++)
            {
                string k = BuildOrder[i];
                long a = (long)(imp * Dist[k]);
                w.parts[k] = a;
                used += a;
            }
            // 网页：零头全进 organic（第一个分配项）
            w.parts["organic"] = w.parts["organic"] + (imp - used);
            return w;
        }

        /// <summary>脏度曲线：纯度万分比 → 系数。</summary>
        public static double DirtMul(int purity)
        {
            int[] px = { 3750, 6250, 8000, 8850, 9400, 9800 };
            double[] py = { 20, 15, 10, 5, 2, 1 };
            if (purity >= 9800) return 1.0;
            if (purity <= 3750) return 20.0;
            for (int i = 0; i < px.Length - 1; i++)
            {
                if (purity >= px[i] && purity <= px[i + 1])
                {
                    double t = (double)(purity - px[i]) / (px[i + 1] - px[i]);
                    return py[i] + (py[i + 1] - py[i]) * t;
                }
            }
            return 20.0;
        }

        /// <summary>一晚的净化预算（μL）。</summary>
        public static long Budget(long v, int purity, double perf)
        {
            double dirt = Math.Max(0.0, DirtMul(purity) + perf / 100.0) * 1.25;
            long cap = Math.Min(v, 10000000L);
            return (long)((cap * 3.0 / 1000.0 + 25000.0) * dirt);
        }

        /// <summary>质量模组对应的纯度上限（万分比）。</summary>
        public static int CapOf(double quality)
        {
            if (quality >= 200) return 10000;
            if (quality >= 100) return 9990;
            if (quality >= 75) return 9980;
            if (quality >= 50) return 9965;
            if (quality >= 25) return 9936;
            if (quality >= 15) return 9916;
            return 9901;
        }

        /// <summary>质量倍率（网页 qualityMult，仅展示用）。</summary>
        public static double QualityMult(double q)
        {
            if (q >= 200) return 0;
            if (q >= 100) return 0.1;
            if (q >= 75) return 0.2;
            if (q >= 50) return 0.35;
            if (q >= 25) return 0.65;
            if (q >= 15) return 0.85;
            return 1.0;
        }

        /// <summary>跑一遍净水器（一晚一遍，网页 purify）。返回清掉的量。</summary>
        public static long Purify(Water w, double perf, double quality)
        {
            if (w.v <= 0 || w.WaterPart <= 0) return 0;
            long budget = Budget(w.v, w.Purity, perf);
            int cap = CapOf(quality);
            long allowed = w.v * (10000 - cap) / 10000;
            long removable = Math.Min(budget, w.Impurity - allowed);
            if (removable <= 0) return 0;
            long left = removable, removed = 0;
            foreach (string k in Order)
            {
                if (left <= 0) break;
                long a = w.Part(k);
                if (a <= 0) continue;
                long take = Math.Min(a, left);
                w.parts[k] = a - take;
                left -= take;
                removed += take;
            }
            return removed;
        }

        /// <summary>过滤棉：按 3 L 一份逐份过，返回耗掉的耐久点数（WaterHelper::HandleFilter 真值）。</summary>
        public static int Filter(Water w, int durability)
        {
            long totalV = w.v;
            if (totalV <= 0 || durability <= 0) return 0;
            var acc = new Water { v = 0 };
            int used = 0;
            long left = totalV;
            while (left > 0)
            {
                long batchV = Math.Min(FILTER_BATCH_UL, left);
                // 这一份的六项按整体比例切出来（同纯度 → 每份同比例）
                var b = new Water { v = batchV };
                foreach (var kv in w.parts)
                    b.parts[kv.Key] = totalV == 0 ? 0 : kv.Value * batchV / totalV;

                long removed = 0;
                foreach (string k in FilterKeys)
                {
                    long a = b.Part(k);
                    if (a <= 0) continue;
                    long floorA = (long)(a * FILTER_MIN_PURITY);
                    long perVol = FilterPer[k];
                    int durPer = FilterDurPer[k];
                    long cur = a;
                    long iters = 0;
                    while (cur > floorA && iters < 10000)
                    {
                        if (used + durPer > durability) break;   // 耐久不够就停手
                        used += durPer;
                        cur -= perVol;
                        iters++;
                    }
                    if (cur < floorA) cur = floorA;
                    if (cur > a) cur = a;
                    b.parts[k] = cur;
                    removed += a - cur;
                }
                b.v = Math.Max(0, batchV - removed);
                acc.v += b.v;
                foreach (var kv in b.parts) acc.parts[kv.Key] = acc.Part(kv.Key) + kv.Value;

                left -= batchV;
                if (used >= durability && left > 0)
                {
                    // 耐久用完：剩下的水原样计入（不过滤）
                    acc.v += left;
                    foreach (var kv in w.parts) acc.parts[kv.Key] = acc.Part(kv.Key) + kv.Value * left / totalV;
                    break;
                }
            }
            w.v = acc.v;
            w.parts = acc.parts;
            return used;
        }

        /// <summary>药片：每片一份共用预算 76,950 μL，先清化学（停在上限总量的 0.7%），余力清微生物；清掉的量离体。</summary>
        public static long Tablet(Water w, int count) { return Tablet(w, count, 0); }

        /// <summary>capBp = 机器上限（99.01 → 9901）；&gt;0 时不允许把纯度推过上限。</summary>
        public static long Tablet(Water w, int count, int capBp)
        {
            // 每片：15 mL 氯先占体积（满瓶才挤出等量水）→ 化学清到总量 0.7%（floor）→ 余力清微生物
            // → **这一片立刻结算体积**：第二片时容器已因第一片清掉杂质腾出空间，不再挤 15 mL（玩家口径）。
            long removed = 0;
            for (int t = 0; t < count; t++)
            {
                // ★2026-11-10 修正：药片自带的 15 mL 氯**不占清除预算**（预算 76.95 mL 是「能清掉多少」）。
                // 旧实现把 15000 从预算里扣掉，6 L 这种预算受限的场景会少算 0.27%
                // （6000 mL 双前置+1 片：旧 98.95% vs 玩家实测 99.21~99.22%），小体积因为化学先到 floor 不受影响。
                long left = TAB_BUDGET + TAB_CHLORINE_UL;

                w.v += TAB_CHLORINE_UL;
                w.parts["chemical"] = w.Part("chemical") + TAB_CHLORINE_UL;
                if (w.cap > 0 && w.v > w.cap) w.v = w.cap;   // 满瓶：挤出的是水

                long floorC = (long)Math.Floor(w.v * TAB_KEEP);
                long chem = w.Part("chemical");
                long cutC = chem - floorC;
                if (cutC < 0) cutC = 0;
                if (cutC > left) cutC = left;
                if (cutC > 0) { w.parts["chemical"] = chem - cutC; left -= cutC; }

                long mic = w.Part("microbe");
                long cutM = mic < left ? mic : left;
                if (cutM > 0) { w.parts["microbe"] = mic - cutM; left -= cutM; }

                long cut = cutC + cutM;

                if (capBp > 0 && w.v > 0)
                {
                    // 杂质不能少于 v*(10000-cap)/10000，否则纯度就“溢出”机器上限
                    long floorImp = (w.v * (10000L - capBp)) / 10000L;
                    long imp = w.Impurity;
                    if (imp < floorImp)
                    {
                        long giveBack = floorImp - imp;
                        if (giveBack > cut) giveBack = cut;
                        w.parts["chemical"] = w.Part("chemical") + giveBack;
                        cut -= giveBack;
                    }
                }

                w.v = Math.Max(0, w.v - cut);
                removed += cut;
                if (cut <= 0) break;
            }
            return removed;
        }

        /// <summary>紫外线：一次把微生物全清并等量加水（总体积不变）。</summary>
        public static void Uv(Water w, int uses)
        {
            long mic = w.Part("microbe");
            if (uses > 0 && mic > 0) w.parts["microbe"] = 0;
        }

        /// <summary>JS Math.round 语义（四舍五入），C# Math.Round 是银行家舍入，不能用。</summary>
        private static long RoundHalfUp(double x) { return (long)Math.Floor(x + 0.5); }

        public class Plan
        {
            public long feed;       // 建议接水量 μL
            public long output;     // 出水 μL
            public int purity;      // 出水纯度万分比
            public int nights;      // 实际过了几晚
            public int tablets;     // 用几片药片
            public int filterUsed;  // 过滤棉耐久
            public bool ok;         // 能否达标
        }

        /// <summary>走一遍完整流程：接水 → 过滤/紫外 → 过夜净化 N 晚 → 药片（网页 runOnce）。</summary>
        public static Plan Run(long feed, int purity, double perf, double quality,
                               bool useFilter, bool useUv, int tablets, int target, int rounds,
                               long containerCap = 0)
        {
            if (rounds < 1) rounds = 1;
            var w = Build(feed, purity, containerCap);
            var p = new Plan();
            if (useFilter) p.filterUsed = Filter(w, FilterBudget);
            if (useUv) Uv(w, Math.Max(1, (int)Math.Ceiling(feed / 3000000.0)));
            int used = 0;
            for (int rd = 0; rd < rounds; rd++)
            {
                // 已经达标就别再空跑一晚；但带过滤棉/药片时第一晚仍有意义（会继续提纯）
                if (w.Purity >= target && (rd > 0 || !(useFilter || tablets > 0))) break;
                long removed = Purify(w, perf, quality);
                if (removed <= 0) break;
                used = rd + 1;
            }
            if (tablets > 0) Tablet(w, tablets);   // 回退：不再用机器上限截断（玩家：别为了 99 质量降低收益）
            p.feed = feed;
            p.output = w.v;
            p.purity = w.Purity;
            p.nights = used;
            p.tablets = tablets;
            p.ok = p.purity >= target;
            return p;
        }

        /// <summary>从**真实水团**（含六项真实配比）出发的最优方案。</summary>
        public class ExistingPlan
        {
            public int filter, uv, tablets, nights, filterUsed;
            public long outUl;
            public int purityBp;
            public bool ok;
            public int Cost { get { return filter + uv + tablets; } }
        }

        /// <summary>容器里已经有水（可能混过别的液体，配比与水源不同）时：枚举 过滤/紫外 × 药片 × 晚数，
        /// 取「达标且出水量最大、耗材最少」的方案。顺序按游戏：过滤/紫外 → N 晚 → 药片。</summary>
        public static ExistingPlan PlanFromWater(Water w0, double perf, double quality,
                                                 int targetBp, int maxNights, int maxTablets, int durability)
        {
            if (w0 == null || w0.v <= 0) return null;
            int cap = CapOf(quality);
            if (targetBp > cap) targetBp = cap;
            if (maxNights < 1) maxNights = 1;
            if (maxTablets < 0) maxTablets = 0;
            ExistingPlan best = null;
            for (int f = 0; f <= 1; f++)
            {
                for (int u = 0; u <= 1; u++)
                {
                    for (int t = 0; t <= maxTablets; t++)
                    {
                        for (int n = 1; n <= maxNights; n++)
                        {
                            var w = w0.Clone();
                            int fu = 0;
                            if (f == 1) fu = Filter(w, durability);
                            if (u == 1) Uv(w, 1);
                            for (int i = 0; i < n; i++)
                            {
                                long cut = Purify(w, perf, quality);
                                if (cut <= 0) break;
                            }
                            if (t > 0) Tablet(w, t);
                            if (w.Purity < targetBp) continue;
                            int cost = f + u + t;
                            if (best == null || w.v > best.outUl ||
                                (w.v == best.outUl && cost < best.Cost))
                            {
                                var bp = new ExistingPlan();
                                bp.filter = f; bp.uv = u; bp.tablets = t; bp.nights = n;
                                bp.filterUsed = fu; bp.outUl = w.v; bp.purityBp = w.Purity; bp.ok = true;
                                best = bp;
                            }
                        }
                    }
                }
            }
            return best;
        }

        /// <summary>给定容器，找最大可行投料量（网页 solveRun：61 点线性扫描 + 24 次二分细化）。</summary>
        public static Plan Solve(long containerCap, int purity, double perf, double quality,                                 bool useFilter, bool useUv, int tabletStock, int target, int maxNights)
        {
            int cap = CapOf(quality);
            if (target > cap) target = cap;
            if (maxNights < 1) maxNights = 1;
            if (containerCap < 1) containerCap = 1;

            Plan best = null;
            for (int i = 0; i <= 60; i++)
            {
                long V = RoundHalfUp(1.0 + (containerCap - 1.0) * i / 60.0);
                if (V < 1) continue;
                var w = Run(V, purity, perf, quality, useFilter, useUv, tabletStock, target, maxNights, containerCap);
                if (w.purity >= target && (best == null || V > best.feed)) best = w;
            }
            if (best == null)
            {
                // 全不达标：给最接近的一个，标 ok=false
                Plan b2 = null;
                for (int j = 1; j <= 60; j++)
                {
                    long V = RoundHalfUp(containerCap * (double)j / 60.0);
                    var w = Run(V, purity, perf, quality, useFilter, useUv, tabletStock, target, maxNights, containerCap);
                    if (b2 == null || w.purity > b2.purity) b2 = w;
                }
                if (b2 == null) return new Plan();
                b2.ok = false;
                return b2;
            }
            // 在 best 附近二分细化
            long lo2 = best.feed;
            long hi2 = Math.Min(containerCap, best.feed + Math.Max(1L, RoundHalfUp(containerCap / 60.0)));
            for (int s = 0; s < 24; s++)
            {
                long m = RoundHalfUp((lo2 + hi2) / 2.0);
                var wm = Run(m, purity, perf, quality, useFilter, useUv, tabletStock, target, maxNights, containerCap);
                if (wm.purity >= target) { lo2 = m; best = wm; }
                else hi2 = m;
            }
            best.ok = true;
            return best;
        }

        /// <summary>「一晚直出」方案：这瓶现有水，怎样一晚上就到 target。</summary>
        public class OneNight
        {
            public bool possible;    // 一晚能不能达标
            public bool asIs;        // 原样跑（不倒水、不加药片、不用耗材）就行
            public long drainUl;     // 需要先倒掉多少 μL（0=不用倒）
            public long keepUl;      // 倒完后留多少 μL
            public int tablets;      // 需要几片药片
            public bool useFilter;   // 需要过滤棉
            public bool useUv;       // 需要紫外
            public int purity;       // 一晚后的纯度
            public long output;      // 一晚后的体积
        }

        /// <summary>枚举「药片数 × 过滤棉 × 紫外」，找一晚达标的最小代价方案（先看少倒水，再看少用药片）。</summary>
        /// <summary>列出所有「一晚直出」可选方案：不倒水（靠药片/过滤/紫外）＋各种倒水量，去重后按偏好排序。</summary>
        public static System.Collections.Generic.List<OneNight> Plans(long vol, int purity, double perf, double quality, int target)
        {
            var res = new System.Collections.Generic.List<OneNight>();
            var seen = new System.Collections.Generic.HashSet<string>();
            if (vol <= 0) return res;
            int cap = CapOf(quality);
            if (target > cap) target = cap;
            // A) 不倒水：枚举药片 0..8 × 过滤棉 × 紫外
            for (int t = 0; t <= 8; t++)
                for (int f = 0; f < 2; f++)
                    for (int u = 0; u < 2; u++)
                    {
                        if (t == 0 && f == 0 && u == 0) continue;
                        var rr = Run(vol, purity, perf, quality, f == 1, u == 1, t, target, 1);
                        if (rr.purity < target) continue;
                        var one = new OneNight();
                        one.possible = true; one.drainUl = 0; one.keepUl = vol;
                        one.tablets = t; one.useFilter = f == 1; one.useUv = u == 1;
                        one.purity = rr.purity; one.output = rr.output;
                        string k = one.drainUl + "|" + one.tablets + "|" + one.useFilter + "|" + one.useUv;
                        if (seen.Add(k)) res.Add(one);
                    }
            // B) 倒水方案：零耗材 / 允许药片
            for (int m = 0; m <= 8; m++)
            {
                var p = PlanOneNight(vol, purity, perf, quality, target, m);
                if (p == null || !p.possible) continue;
                string k = p.drainUl + "|" + p.tablets + "|" + p.useFilter + "|" + p.useUv;
                if (seen.Add(k)) res.Add(p);
            }
            res.Sort(delegate (OneNight a, OneNight b)
            {
                int ca = (a.useFilter ? 1 : 0) + (a.useUv ? 1 : 0) + a.tablets;
                int cb = (b.useFilter ? 1 : 0) + (b.useUv ? 1 : 0) + b.tablets;
                if (PreferNoConsumable)
                {
                    if (ca != cb) return ca.CompareTo(cb);
                    if (a.drainUl != b.drainUl) return a.drainUl.CompareTo(b.drainUl);
                }
                else
                {
                    if (a.drainUl != b.drainUl) return a.drainUl.CompareTo(b.drainUl);
                    if (ca != cb) return ca.CompareTo(cb);
                }
                return a.tablets.CompareTo(b.tablets);
            });
            return res;
        }

        public static OneNight PlanOneNight(long vol, int purity, double perf, double quality,
                                           int target, int maxTablets, long containerCap = 0)
        {
            var res = new OneNight();
            if (vol <= 0) return res;
            int cap = CapOf(quality);
            if (target > cap) target = cap;

            OneNight best = null;
            for (int t = 0; t <= Math.Max(0, maxTablets); t++)
            {
                for (int f = 0; f < 2; f++)
                {
                    for (int u = 0; u < 2; u++)
                    {
                        bool useFilter = f == 1, useUv = u == 1;
                        // 1) 原样（不倒水）
                        var full = Run(vol, purity, perf, quality, useFilter, useUv, t, target, 1, containerCap);
                        if (full.ok && full.nights <= 1)
                        {
                            var cand = new OneNight
                            {
                                possible = true,
                                asIs = (t == 0 && !useFilter && !useUv),
                                drainUl = 0, keepUl = vol, tablets = t,
                                useFilter = useFilter, useUv = useUv,
                                purity = full.purity, output = full.output
                            };
                            if (best == null || Better(cand, best)) best = cand;
                            continue;
                        }
                        // 2) 倒掉一部分：二分找「一晚还能达标的较大体积」
                        long lo = 1, hi = vol - 1;   // 必须真的倒掉至少 1 μL 才算这一支
                        if (hi >= 1)
                        {
                            Plan bestP = null;
                            long bestV = 0;
                            var wt = Run(hi, purity, perf, quality, useFilter, useUv, t, target, 1, containerCap);
                            if (wt.ok) { bestP = wt; bestV = hi; }
                            while (lo <= hi)
                            {
                                long mid = (lo + hi) / 2;
                                if (mid < 1) break;
                                var wm = Run(mid, purity, perf, quality, useFilter, useUv, t, target, 1, containerCap);
                                if (wm.ok) { bestP = wm; bestV = mid; lo = mid + 1; }
                                else hi = mid - 1;
                            }
                            if (bestP != null && bestV > 0)
                            {
                                var cand = new OneNight
                                {
                                    possible = true, asIs = false,
                                    drainUl = vol - bestV, keepUl = bestV, tablets = t,
                                    useFilter = useFilter, useUv = useUv,
                                    purity = bestP.purity, output = bestP.output
                                };
                                if (best == null || Better(cand, best)) best = cand;
                            }
                        }
                    }
                }
            }
            return best ?? res;
        }

        // 代价排序：先比倒掉的体积（少倒优先），再比药片数，最后比耗材数量。
        private static bool Better(OneNight a, OneNight b)
        {
            int ca = (a.useFilter ? 1 : 0) + (a.useUv ? 1 : 0) + a.tablets;
            int cb = (b.useFilter ? 1 : 0) + (b.useUv ? 1 : 0) + b.tablets;
            if (PreferNoConsumable && ca != cb) return ca < cb;
            if (a.drainUl != b.drainUl) return a.drainUl < b.drainUl;
            if (a.tablets != b.tablets) return a.tablets < b.tablets;
            return ca < cb;
        }
    }
}

