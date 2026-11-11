using System;
using System.Collections.Generic;
using System.Globalization;

namespace WaterCalcMod
{
    /// <summary>纯文案层：只把数字拼成人话，不碰 Unity、不碰游戏对象。原生面板与 IMGUI 面板共用。</summary>
    public static class PlanText
    {
        public static string Pct(int bp) { return (bp / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%"; }
        public static string Ml(long ul) { return (ul / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " mL"; }

        // ---------------- 玩家 2026-11-11：界面按「同类同色」上色 ----------------
        // 要求原话：「不要用·来替代空格…同类属性比如 ml 比如多少元 一个颜色。优质水就蓝色吧」。
        // 约定：标签用暖灰（和游戏原生 tooltip 的说明文字同色系）、毫升数用青、纯度用天蓝、
        //      钱用绿、警告用橙红、水质档位按等级上色（优质水蓝、纯水亮白、铁锈橙、沟渠褐）。
        public const string C_LABEL = "BCA98A";
        public const string C_NUM = "7FD8E8";
        public const string C_PCT = "9CC9FF";
        public const string C_MONEY = "A8DE6E";
        public const string C_WARN = "FF8A73";
        public const string C_STEP = "D8D2C4";

        public static string Rich(string hex, string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return "<color=#" + hex + ">" + s + "</color>";
        }
        public static string Lbl(string s) { return Rich(C_LABEL, s); }
        public static string Num(string s) { return Rich(C_NUM, s); }
        public static string PctColored(int bp) { return Rich(C_PCT, Pct(bp)); }
        public static string MlColored(long ul) { return Rich(C_NUM, Ml(ul)); }
        public static string MoneyColored(double yuan, string suffix)
        {
            string s = (yuan >= 100 ? yuan.ToString("0", CultureInfo.InvariantCulture)
                                    : yuan.ToString("0.0", CultureInfo.InvariantCulture)) + " 元";
            return Rich(C_MONEY, s + (suffix ?? ""));
        }

        /// <summary>水质档位的颜色：优质水蓝、纯水亮白、基础水浅灰、幽灵紫、铁锈橙、沟渠褐。</summary>
        public static string TierHex(string tierName)
        {
            if (tierName == null) return C_LABEL;
            if (tierName.IndexOf("纯水") >= 0) return "C9F2FF";
            if (tierName.IndexOf("优质") >= 0) return "6FA8FF";
            if (tierName.IndexOf("基础") >= 0) return "DCDCDC";
            if (tierName.IndexOf("幽灵") >= 0) return "C39BFF";
            if (tierName.IndexOf("铁锈") >= 0) return "FFA35C";
            if (tierName.IndexOf("沟渠") >= 0) return "A08A70";
            return C_LABEL;
        }

        /// <summary>面板顶部一行总结：机器里这瓶现在怎样、今晚会怎样。</summary>
        public static string Overview(PanelModel m)
        {
            if (m == null || !m.HasBottle) return "机器里没有水瓶 —— 放一瓶水进「储水容器」槽。";
            string s = m.VolumeMl.ToString() + "/" + m.CapacityMl.ToString() + " mL · " + Pct(m.PurityBp);
            if (m.CapBp > 0) s += " · 本机上限 " + Pct(m.CapBp);
            return s;
        }

        /// <summary>选中方案后的一行结论：接多少、几晚、出多少。</summary>
        public static string Verdict(PanelModel m)
        {
            if (m == null || m.Verdict == null || m.Verdict.Length == 0) return "";
            return m.Verdict;
        }

        /// <summary>水商配方小档案（原生滚动窗口用的纯文本，不含任何 UI 代码）。</summary>
        public static string[] Recipes()
        {
            return new string[]
            {
                "【水质等级】纯水 99.90% / 优质 99.00–99.89% / 良好 98.00–98.99% / 基础 <98%",
                "【一晚直出上限】铁锈水 91.0% → 99.01%（2L 瓶留 1650 mL）",
                "沟渠水 91.24% → 99.01%（2L 瓶留 1500 mL 保守 / 1650 mL 上限）",
                "【99.90 纯水】需要质量模组 +100；达不到时本机上限只有档位值",
                "【上限档位】质量 <15→99.01 · 15-24→99.16 · 25-49→99.36 · 50-74→99.65 · 75-99→99.80 · 100-199→99.90 · ≥200→100.00",
                "【顺序】先放药片/过滤棉，开紫外，再让净水器过夜；顺序反了结果会掉",
                "【耗材】过滤棉处理 physical/organic/mineral；药片处理 chemical/microbe；紫外压 heavy",
                "【药片】每片约 75 mL 微生物 + 化学 0.05%，放多了会稀释体积",
                "【水商利润】基础水 2000 mL 买 80 卖 118，含加价小费可到 153",
                "【体积】净水器不改变总体积，只把污染物清掉；留水量决定稀释比",
                "【小瓶更好】体积越大稀释越狠，6L 需 98.11%，50L 需 98.87% 才达标",
            };
        }
        /// <summary>一条方案的人话（用于列表）。</summary>
        public static string OneNightRow(int index, WaterMath.OneNight p, int targetBp)
        {
            if (p == null) return "";
            string head = (index + 1).ToString() + ". ";
            string s = head;
            if (p.asIs) s += "原样跑 1 晚 → " + Pct(p.purity);
            else
            {
                if (p.drainUl > 0) s += "倒掉 " + Ml(p.drainUl) + "（留 " + Ml(p.keepUl) + "）";
                else s += "留 " + Ml(p.keepUl);
                s += " → 跑 1 晚 → " + Pct(p.purity);
            }
            // 耗材只给路线提示，不代劳：把顺序和数量写清楚。
            string pre = "";
            if (p.useFilter) pre += "过滤棉";
            if (p.useUv) pre += (pre.Length > 0 ? " + " : "") + "紫外";
            if (p.tablets > 0) pre += (pre.Length > 0 ? " + " : "") + p.tablets + " 片药片";
            if (pre.Length > 0) s += "（先放 " + pre + "，再让净水器过夜）";
            if (p.purity >= targetBp) s += " ✓";
            return s;
        }

        /// <summary>把方案展开成完整句子（tooltip / 日志用）。</summary>
        public static string Describe(WaterMath.OneNight p)
        {
            if (p == null) return "做不到";
            if (!p.possible) return "一晚最多 " + Pct(p.purity) + "（先兑水或换小瓶）";
            if (p.asIs) return "原样跑 1 晚 → " + Pct(p.purity) + " · " + Ml(p.output);
            return "先倒掉 " + Ml(p.drainUl) + "（留 " + Ml(p.keepUl) + "）" +
                   (p.tablets > 0 ? " + " + p.tablets + " 片药片" : "") +
                   (p.useFilter ? " + 过滤棉" : "") + (p.useUv ? " + 紫外" : "") +
                   " → 跑 1 晚 → " + Ml(p.output) + " · " + Pct(p.purity);
        }

        /// <summary>图标 hover 用的一行结论。</summary>
        public static string Tooltip(PanelModel m)
        {
            if (m == null || !m.HasBottle) return "把水瓶放进储水容器";
            if (m.Plans != null && m.Plans.Count > 0)
            {
                var p = m.Plans[0];
                if (p != null && p.possible)
                {
                    if (p.asIs) return "今晚原样跑 → " + Pct(p.purity);
                    return "今晚 → " + Pct(p.purity) + "（留 " + Ml(p.keepUl) + "）";
                }
                if (p != null) return "一晚最多 " + Pct(p.purity);
            }
            return m.Verdict;
        }

        /// <summary>三档（玩家 2026-11-11 重定）：自定义/性价比＝按一晚净赚排；效率＝装满容器。
        /// 玩家 m00327 定案：把「默认」改名为「自定义」（逻辑不变，仍是「一晚净赚最大」）；
        /// 「质量：纯度优先」整档去掉（玩家 Q7：已去掉，不用在代码里留着）。</summary>
        public static readonly string[] ModeNames = { "自定义：一晚赚最多", "效率：装满容器", "性价比：按现有道具" };
        public static readonly string[] ModeShort = { "自定义", "效率", "性价比" };
        // 前置 5 档：0 自动 / 1 不处理 / 2 过滤棉 / 3 紫外 / 4 过滤棉+紫外
        // ★玩家报的 bug：4 档以前没有名字，Rebuild 用 PreNames[Min(PreMode, len-1)] 把 4 夹回 3，
        //   所以按到「过滤棉+紫外」界面立刻变成「紫外」。文案必须跟 PreUi 一样有 5 项。
        public static readonly string[] PreNames = { "自动", "不处理", "过滤棉", "紫外", "过滤棉+紫外" };
        /// <summary>净化后（对应网页计算器的「净化后」下拉）：0=自动，1=不放药片，2..4=固定 1..3 片。</summary>
        public static readonly string[] PostNames = { "按模式最优", "不放药片", "1 片药片", "2 片药片", "3 片药片" };

        public static string PreName(int i) { return (i >= 0 && i < PreNames.Length) ? PreNames[i] : PreNames[0]; }
        public static string PostName(int i) { return (i >= 0 && i < PostNames.Length) ? PostNames[i] : PostNames[0]; }
        public static readonly string[] TargetNames = { "本机上限", "纯水 99.90%", "优质水 99.00%" };

        /// <summary>目标档只有两个：0 = 本机上限（随质量档变），1 = 99.90% 纯水。</summary>
        public static int TargetBp(int idx, int quality)
        {
            if (idx <= 0) return WaterMath.CapOf(quality);
            if (idx == 2) return 9900;   // 与网页「优质水 99.00%」同档
            return 9990;
        }

        public static string ModeLine(int mode)
        {
            if (mode < 0 || mode >= ModeNames.Length) mode = 0;
            if (mode == 0) return "只跑 1 晚，出多少算多少";
            if (mode == 1) return "达标前提下，单次产量最大";
            return "达标前提下，耗材/倒水代价最小";
        }
    }

    /// <summary>面板只读的数据快照：UI 层不再算数。</summary>
    public class PanelModel
    {
        public bool HasBottle;
        public string BottleName = "";
        public int VolumeMl;
        public int CapacityMl;
        public int PurityBp;
        public int CapBp;
        public int PerfPct;
        public int Quality;
        public int TargetBp;
        public int Mode;
        public int PreMode;     // 0 自动 / 1 不处理 / 2 过滤棉 / 3 紫外
        public int PostMode;    // 同上
        public string Verdict = "";
        public string Hint = "";
        public string SourceNote = "";   // 「今日铁锈水 91.0%」之类
        public List<WaterMath.OneNight> Plans = new List<WaterMath.OneNight>();
        public int Selected;
        public bool MixKnown;            // 六项真实配比是否读到
        public string MixText = "";      // 六项配比摘要（mL）
        public int Grade = -1;           // 0 纯水 1 优质 2 基础 3 幽灵 4 铁锈 5 沟渠
        public int SourcePurBp;          // 今日水源纯度
    }
}
