using System;

namespace WaterCalcMod
{
    /// <summary>
    /// 水价模型（玩家 2026-11-11 口径，实测参数）：
    ///   实际销售价 = 基础水价 × (1 + 修正系数)
    ///   基础水价 = 40 元/升（游戏内 2000 mL 基础水卖 80 元，不含容器本身的价格）。
    /// 阶梯按**纯度**分档，门槛直接取自 WaterFeatureHelper.WATER_PURITIES（反编译实测
    /// 9990 / 9900 / 9800 / 9600 / 9000 bp）：
    ///   纯水   9990+      +100%  → 80   元/L
    ///   优质水 9900–9990  +50%   → 60   元/L
    ///   基础水 9800–9900   0%    → 40   元/L
    ///   幽灵水 9600–9800  −33%   → 26.8 元/L
    ///   铁锈水 9000–9600  −75%   → 10   元/L
    ///   沟渠水 &lt;9000    −90%   → 4    元/L
    /// 注意：**不要**用 GameItem.GetValue / 预估价值估水价——那是按「满耐久」算的物品价值，
    /// 水没有耐久，玩家实测（海德拉 306 / 325）与真实售价无关。
    /// </summary>
    public static class WaterEconomy
    {
        /// <summary>基础水价（元/升）。游戏 WaterFeatureHelper.GetWaterPrice 里硬编码的就是 40。</summary>
        public const double BasePerLitre = 40.0;

        /// <summary>装备/外部改动后的真实基础水价（Harmony 探针观测到就覆盖它）。</summary>
        public static double LiveBasePerLitre = 40.0;

        // 纯度门槛（bp = 万分之一），与 WaterFeatureHelper.WATER_PURITIES 逐位一致。
        public static readonly int[] Thresholds = { 9990, 9900, 9800, 9600, 9000 };
        // 修正系数，整数 ×100 存储（2000 = ×2.00），避免浮点累积误差。
        static readonly int[] MultCenti = { 200, 150, 100, 67, 25, 10 };
        static readonly string[] Names = { "纯水", "优质水", "基础水", "幽灵水", "铁锈水", "沟渠水" };

        /// <summary>纯度 → 档位序号（0 纯水 / 1 优质 / 2 基础 / 3 幽灵 / 4 铁锈 / 5 沟渠）。</summary>
        public static int Index(int purityBp)
        {
            for (int i = 0; i < Thresholds.Length; i++) if (purityBp >= Thresholds[i]) return i;
            return Thresholds.Length;
        }

        public static string TierName(int purityBp) { return Names[Index(purityBp)]; }

        /// <summary>修正系数倍率（2.0 / 1.5 / 1.0 / 0.67 / 0.25 / 0.10）。</summary>
        public static double Mult(int purityBp) { return MultCenti[Index(purityBp)] / 100.0; }

        /// <summary>该纯度水的作价（元/升）。</summary>
        public static double PricePerLitre(int purityBp)
        {
            double b = LiveBasePerLitre > 1 ? LiveBasePerLitre : BasePerLitre;
            return b * Mult(purityBp);
        }

        /// <summary>一批水的毛收入（元）。ul 是微升（μL）。</summary>
        public static double Revenue(long ul, int purityBp)
        {
            if (ul <= 0) return 0;
            return PricePerLitre(purityBp) * (ul / 1000000.0);
        }

        /// <summary>净利（元）= 毛收入 − 耗材成本（成本口径与 Plan 一致：药片/过滤棉/电/海德拉）。</summary>
        public static double Profit(long ul, int purityBp, double cost)
        {
            return Revenue(ul, purityBp) - cost;
        }

        /// <summary>钱数格式化：整数就写整数，带小数最多两位。</summary>
        public static string Money(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 0.05) return Math.Round(v).ToString("0");
            return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>一句话报价：纯水 80 元/L 这类。</summary>
        public static string Quote(int purityBp)
        {
            return TierName(purityBp) + " " + Money(PricePerLitre(purityBp)) + " 元/L";
        }

#if DEV
        /// <summary>DEV 自检：把六个档位的价格打一遍，方便和游戏里看到的钱对拍。</summary>
        public static string SelfTest()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("base=").Append(Money(BasePerLitre)).Append(" 元/L");
            for (int i = 0; i <= Thresholds.Length; i++)
            {
                int bp = i < Thresholds.Length ? Thresholds[i] : 8999;
                sb.Append(" | ").Append(Names[i]).Append("@").Append(bp)
                  .Append("=").Append(Money(PricePerLitre(bp)));
            }
            return sb.ToString();
        }
#endif
    }
}
