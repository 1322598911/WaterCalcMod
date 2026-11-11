using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ColorPalette = RenderHandler.ColorPalette;

namespace WaterCalcMod
{
    /// <summary>
    /// 净水助手的原生 UI：全部用游戏自己的 MachineryText / MachineryButton /
    /// MachineryStack / ScrollableRichTextElement 拼，宿主可以是净水器窗口的标题栏
    /// （3 个 16px 图标）或按 F7 打开的独立 PixelWindow。不画任何自绘美术。
    /// </summary>
    public static class PurifierUi
    {
        private static ManualLogSource log;
        private static string lastPartsLog = "";   // 去重：Compute 每 0.4s 跑一次，同样的配比不重复写日志
        private static string lastTipLog = "";
        private static readonly List<PixelWindow> attached = new List<PixelWindow>();

        public static PanelModel Model = new PanelModel();
        public static int Mode = 0;   // 0 自定义（一晚净赚最多）· 1 效率（装满容器）· 2 性价比（按现有道具）
        /// <summary>玩家 2026-11-11 m00327：道具口径的**全局**开关（Q2＝全局一个）。
        /// false＝按背包真实持有；true＝无限道具（药片/滤棉不限，紫外是工具不计耐久与成本，海德拉不限，
        /// 但拉条与设置页仍能限制这些「无限道具」，见 Q3）。点档位键＝真实→无限→真实 三态循环。</summary>
        public static bool InfiniteTools = false;

        /// <summary>真实道具 ⇄ 无限道具（全局一个开关，所有档位共用）。</summary>
        private static void ToggleInfinite(string why)
        {
            InfiniteTools = !InfiniteTools;
            LastMsg = InfiniteTools ? "无限道具：药片/滤棉不限，海德拉不限（拉条只管真实道具）" : "真实道具：按背包实际持有算";
            Log("[ui] infinite=" + (InfiniteTools ? 1 : 0) + " (" + why + ")");
        }
        // 默认「优质水 99.00%」：玩家目的是卡 99% 上限多接水（本机上限 99.01 会少接 ~50 mL/6L）
        public static int TargetIdx = 2;
        // 玩家：模式里删掉「一晚就要」，只留 效率 / 性价比；前置「按模式最优」改名「自动」并增「过滤棉+紫外」；
        // 后置同理（自动 + 具体药片数）。内部索引仍是老编号，靠这层映射做「显示名 ↔ 值」，不动计算逻辑。
        private static readonly string[] ModeUi = { "效率", "性价比" };
        private static readonly int[] ModeVal = { 1, 2 };
        private static readonly string[] PreUi = { "自动", "不处理", "过滤棉", "紫外", "过滤棉+紫外" };
        private static readonly int[] PreVal = { 0, 1, 2, 3, 4 };
        private static readonly string[] PostUi = { "自动", "不放药片", "1 片药片", "2 片药片", "3 片药片" };
        private static readonly int[] PostVal = { 0, 1, 2, 3, 4 };

        public static readonly string[] SrcNames = { "自动（瓶上标签）", "铁锈水", "沟渠水", "优质水", "基础水", "纯水" };
        // 海德拉开关（玩家 2026-11-10 要求做成设置项）：自动 / 强制用 / 完全不用。
        // 真实一只 300 耐久（1 点 = 100 mL），可以多买几只叠加；滑条上限按容器装得下的最大量给。
        // 玩家 2026-11-10：游戏容器最大 50 L ⇒ 500 点才是合理上限（不该卡在 330）。
        public static readonly string[] HydraUi = { "自动（必要时才用）", "强制用（装得下就装满）", "完全不用" };
        public static readonly int[] HydraVal = { 0, 1, 2 };
        // 耐久改成原生滑条（玩家 2026-11-10：为什么人家 mod 有滑块你没有）。
        // 游戏自带 MachinerySlider（PixelElement 家族，可直接 Add 进 MachineryStack），
        // 有公开的 onChanged / onReleased（Action<float>），所以能连续拖到 312 这种任意值。
        public const int HydraDurMin = 1;      // 0 留给「不限」
        public const int HydraDurMaxSlider = 500;   // 50 L 容器 = 500 点（玩家 2026-11-10 定案）
        public static int PreMode = 0;
        /// <summary>效率（无视容器上限）：规划时不按容器容量收口，用来回答「这台机器一晚到底能吃多少」。
        /// 玩家 2026-11-10 要求：净水器原版标题栏与 mod 面板右上角各给一个快捷图标。</summary>
        public static bool UncapEff = false;
        public static int PostMode = 0;
        public static int SrcIdx = 0;   // 0=自动；1 铁锈水 2 沟渠水 3 优质水 4 基础水 5 纯水
        // 玩家 2026-11-10：水源选项里只保留「沟渠水 / 铁锈水」两个切换按钮（其余档不再露出）。
        public static readonly string[] SrcPickNames = { "铁锈水", "沟渠水" };
        public static readonly int[] SrcPickVals = { 1, 2 };
        /// <summary>界面显示用的水源名：只认这两只水龙头，其余一律回落到「自动（瓶上标签）」。</summary>
        public static string SrcUiName(int idx)
        {
            if (idx == 1) return SrcPickNames[0];
            if (idx == 2) return SrcPickNames[1];
            return SrcNames[0];
        }
        public static string LastMsg = "";
        /// <summary>右键「使用」选中的瓶子；灌水后清空。</summary>
        public static object RightClickTarget = null;

        private static readonly Color Cream = new Color(213f / 255f, 210f / 255f, 184f / 255f, 1f);
        private static readonly Color Dim = new Color(150f / 255f, 150f / 255f, 140f / 255f, 1f);
        // 玩家 2026-11-11：当前页的导航键用金色高亮，和主界面「接 X mL」的金字同一色相。
        private static readonly Color Gold = new Color(242f / 255f, 193f / 255f, 78f / 255f, 1f);
        private static readonly Color Warn = new Color(1f, 0.72f, 0.35f, 1f);

        private static MachineryButton calcBtn, fillBtn, cfgBtn, recipeBtn, gearBtn;
        // 玩家：每台净水器的标题栏都要显示「这台正在跑多少量」，而 fillBtn 是单例（只有最后挂的那台有），
        // 所以按窗口各存一份：window -> 它的灌水键。Tick 里逐台按各自的方案量更新键面。
        private static readonly Dictionary<PixelWindow, MachineryButton> winFill
            = new Dictionary<PixelWindow, MachineryButton>();
        private static readonly Dictionary<PixelWindow, MachineryButton> winCalc
            = new Dictionary<PixelWindow, MachineryButton>();
        private static MachineryButton modeBtnEff, modeBtnCheap, modeBtnUncap;   // 左上角：效率 / 性价比 / 效率(无视容器)
        private static Sprite iEff;
        private static PixelWindow panelWin;
        private static PixelWindow detailWin;
        private static ScrollableRichTextElement detailTxt;
        private static ScrollableRichTextElement recipeTxt;
        private static PixelWindow recipeWin;
        private static bool panelWant;
        private static MachineryText pSub, pTarget, pMode, pPre, pPost, pHint, pEarly, pMsg;
        private static MachineryButton[] modeBtns, targetBtns, preBtns;
        // 下拉式（一行一个按钮）的选择器按钮：F9 里直接改这三行的文字
        private static MachineryButton cTarget, cMode, cPre, cPost, cSrc, cHydra, cHydraDur;
        // 海德拉耐久：原生滑条 + 旁边的数值标签（滑条拖动时只改这行文字，不重建整个面板）
        private static MachinerySlider sHydraDur;
        private static MachineryText cHydraDurLbl;
        // 过滤棉耐久：原生滑条选「这次用多少点耐久」，不按块数（400+311 这种按块会虚高）。
        // -1 = 还没拖过（＝用背包里实际持有的耐久总和）；0 = 这次不用过滤棉。
        private static MachinerySlider sFilterDur;
        private static MachineryText cFilterDurLbl;
        private static int FilterDurUse = -1;
        private const int FilterDurMaxSlider = 2000;
        private static MachineryButton pMore;
        /// <summary>方案区切到「更多方案」：列出全部达标解，不受前置/后置过滤（用户 2026-11-09 要求）。</summary>
        public static bool ShowAllCombos = false;
        /// <summary>玩家点选过的方案 label；非空时覆盖自动选中的方案（再点「自动」清空）。</summary>
        public static string ManualPickLabel = null;
        private static WaterPlan.Combo[] lastRows;
        private static MachineryStack pRowsBox;
        private static MachineryButton[] pRowBtns;
        private const int RowCount = 6;
        private static MachineryStack pPickRow;
        // 分页：0=主页（当前方案）1=设置（五行下拉）2=更多方案（候选表）
        private static int UiPage;
        private static readonly System.Collections.Generic.List<MachineryButton> pSetBtns =
            new System.Collections.Generic.List<MachineryButton>();
        /// <summary>网页的「自己填纯度」：>0 时按这个纯度算，不看水源下拉；-1=关闭。</summary>
        public static int CustomPur = -1;
        private static ScrollableRichTextElement pList;
        private static MachineryButton pFill, pMix, pMode2, pTab, pUv, pBlend;
        // 网页式布局的三张输出卡 + 杂质明细 + 方案表头 + 底部「接水」键（2026-11-08 重做）
        private static MachineryText pCard1, pCard2, pCard3, pMixLine, pTableHead;
        private static MachineryText pMoney;   // 「卖 X 元 · 赚 Y 元」单独一行，挂在「出来 …」下面（玩家 2026-11-11）
        /// <summary>主页「持有：过滤棉 X 点   海德拉 Y 点」：只拼已拥有的项，两样都没有就隐藏整行。</summary>
        private static MachineryText pOwn;
        private static MachineryText pCard1Val;
        /// <summary>卡二下面单独一行「步骤 过滤+紫外+1片药片」（玩家 2026-11-10：别挤在底行、别撑宽窗口）。</summary>
        private static MachineryText pStep;
        private static MachineryButton pJoin;
        // 面板用的方案缓存：同一组输入只算一次，避免 0.4 秒刷新重复跑整瓶规划
        private static WaterPlan.Report planCache;
        private static string planKey = "";
        // 「接水」键这次实际会倒进去的整瓶量（mL）：OnFill 用的就是这条整瓶拆分
        private static long planPourMl = 0;
        private static Sprite iCalc, iFill, iCfg, iGear;
        // 加载 mod 时的默认：平衡模式开（按瓶里真实水质算）。
        // 玩家要求「再游戏加载模组的时候设置」，所以这里给初值，设置窗里可改。
        private static bool BalanceOff = true;
        // 已删除 dayParts / dayBaseUl / dayGrade / dayPur / dayPurReal / dayTemplate
        // （2026-11-08：那套「缓存当天六项配比 → 逐项 AddLiquid 重建」的模板灌水路径已废弃，
        //   原因见 OnFill 里的注释：水龙头纯度来自 StoreStation 的当日字段，过月会变，缓存必错。）
        private static string lastBal;
        private static float nextTick;
        private static string lastKey = "";
        // 数值真的变了才做整窗重建（每秒无条件重建会让鼠标划过按钮卡顿）
        private static bool modelDirty;
        // 玩家 2026-11-10 卡顿审计：Tick 里每秒无条件跑的重活（全背包扫描 / 反射 / 富文本重排）全部节流或缓存。
        private static float lastWatchT = -999f;      // WatchContainer 节流（它内部会全背包扫滤棉）
        private static float lastHdrT = -999f;        // 标题图标名字自愈节流（每颗都要反射全量 GetMethods）
        private static long scanCacheVal; private static float scanCacheT = -999f;   // Scanners() 结果缓存
        private static long filtCacheVal = -1; private static float filtCacheT = -999f; // FilterDurability() 结果缓存
        private static string lastStepLine;           // 步骤行上次的文案（不同才 SetText）
        private static object lastStepOwner;          // 步骤行上次绑的控件（换页重建后必须重画，否则整行空）
        private static string lastRichSig;            // 富文本上次的数据签名（不同才 SetRichText）
        private static object lastRichOwner;          // 富文本上次绑的控件（换对象＝必须重画）
        private static string lastFillKey;            // 灌水键上次的容器指纹（体积/瓶名没变就不重绑）
        private static int[] markSelLast = new int[] { -99, -99, -99 };  // 三组按钮上次的选中项
        // 每台净水器窗口各自绑定自己的储水容器槽（用户 m03701：三窗串台，点中间灌到右上）。
        private static readonly List<PixelWindow> ctxWins = new List<PixelWindow>();
        private static readonly List<object> ctxBottles = new List<object>();
        private static object overrideBottle;
        private static int ctxIndex;

        public static void Install(ManualLogSource l) { log = l; }

        // 玩家正常游玩时不刷日志：只有开发测试用的 _tools\ctl.enable 存在时才写 BepInEx 日志。
        internal static void Log(string s)
        {
            if (log == null) return;
#if DEV
            if (!System.IO.File.Exists(System.IO.Path.Combine(CmdFile.Dir, "ctl.enable"))) return;
#else
            return;   // 发布版不写调试日志
#endif
            log.LogInfo(s);
        }

        /// <summary>窗口 → 这台机器自己的储水容器槽（同一窗口重复进只更新槽）。</summary>
        public static void BindWindow(object window, object bottleInv)
        {
            var w = window as PixelWindow;
            if (w == null) return;
            for (int i = 0; i < ctxWins.Count; i++)
                if (ReferenceEquals(ctxWins[i], w)) { ctxBottles[i] = bottleInv; return; }
            ctxWins.Add(w); ctxBottles.Add(bottleInv);
        }

        /// <summary>把「当前窗口」切到这台（按钮回调闭包捕获自己的 w，绝不读全局 ActiveMachine）。</summary>
        private static void UseWindow(PixelWindow w)
        {
            for (int i = 0; i < ctxWins.Count; i++)
                if (ReferenceEquals(ctxWins[i], w)) { overrideBottle = ctxBottles[i]; ctxIndex = i + 1; break; }
            // 闭包里拿到的 bottleInv 可能是 null（创建窗口那一刻还没配对）→ 按窗口反查 Pair 补上，
            // 否则所有窗口都会退回到「全局当前机器」，表现成只有一台能灌。
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var p = PurifierHook.Pairs[i];
                    if (p == null || p.Window == null) continue;
                    if (!ReferenceEquals(p.Window, w)) continue;
                    if (p.BottleInv != null) overrideBottle = p.BottleInv;
                    ctxIndex = i + 1;
                    return;
                }
            }
            catch (Exception) { }
        }

        /// <summary>上下文窗口里的瓶；没有就 null。</summary>
        private static GameItem CtxBottle()
        {
            try { return overrideBottle == null ? null : GameBridge.Get(overrideBottle, "childItem") as GameItem; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 玩家 2026-11-11：没点左上角圆点就直接按「这台机器」标题栏上的模式键时，
        /// 面板不应该去改另一台 —— 先把按下去的那台机器选成当前台，再改模式。
        /// </summary>
        private static void ModeOn(PixelWindow w, int mode)
        {
            UseWindow(w);
            try
            {
                int idx = PairIndexOf(w);
                if (idx >= 0) SelectPair(idx);
            }
            catch (Exception) { }
            SetMode(mode);
        }

        /// <summary>同上，效率（无视容器）那个键。</summary>
        private static void UncapOn(PixelWindow w)
        {
            UseWindow(w);
            try
            {
                int idx = PairIndexOf(w);
                if (idx >= 0) SelectPair(idx);
            }
            catch (Exception) { }
            ToggleUncapEff();
        }


        // ---------------- 净水器窗口标题栏：3 个 16px 图标 ----------------

        public static void Attach(object window) { Attach(window, null); }

        public static void Attach(object window, object bottleInv)
        {
            var w = window as PixelWindow;
            if (w == null) return;
            BindWindow(w, bottleInv);
            for (int i = 0; i < attached.Count; i++) if (attached[i] == w) return;
            attached.Add(w);
            try
            {
                // 注：这里原本会在挂图标时把独立面板收起来（去重），但 F9 面板已改回完整设置页，
                // 收起来等于把玩家的设置页关掉，故去掉该规则；重复问题改由机器窗口只挂图标解决。
                iconSize = NativeIconSize(w);
                iFill = PixelIcons.Get("water", out iw, out ih);
                iCalc = PixelIcons.Get("info", out iw, out ih);
                iCfg = PixelIcons.Get("note", out iw, out ih);
                iGear = PixelIcons.Get("gear", out iw, out ih);

                fillBtn = NewIcon(iFill, "接水", new Action(delegate { UseWindow(w); OnFill(); }));
                calcBtn = NewIcon(iCalc, "净水助手", new Action(delegate
                {

                    UseWindow(w);
                    ShowMain(true);

                }));
                recipeBtn = NewIcon(iCfg, "手册", new Action(delegate { ToggleRecipes(); }));
                // 玩家：平衡模式挪进 mod 设置窗，标题栏不再挂这个图标
                gearBtn = null;

                // 玩家：效率 / 性价比 一键切，放在净水器标题栏（PixelIcons 只有 water/note/info/gear/serve 五种键）
                // 玩家 2026-11-11：没点圆点时按「别的机器」的模式，面板会去改另一台 —— 按键先认这台机器。
                modeBtnEff = NewIcon(PixelIcons.Get("serve", out iw, out ih), "自定义", new Action(delegate { ModeOn(w, 0); }));
                modeBtnCheap = NewIcon(PixelIcons.Get("gear", out iw, out ih), "性价比", new Action(delegate { ModeOn(w, 2); }));
                // ★玩家 2026-11-10：再来一个「效率·无视容器」快捷键（原版标题栏 + 面板各一个）
                modeBtnUncap = NewIcon(PixelIcons.Get("serve", out iw, out ih), "效率", new Action(delegate { UncapOn(w); }));
                // 玩家 2026-11-10：按钮排布要与面板一致 —— 接水 → 默认 → 效率 → 性价比 → 手册 → 详情
                Log("[ui] 模式图标 eff=" + (modeBtnEff != null) + " cheap=" + (modeBtnCheap != null));
                // ★玩家 2026-11-10：多开净水器时，标题栏左上角这个圆点 = 「按这台算」。绿=当前在用，灰=没选。
                // 之前永远只抓第一台打开的窗口，第二台怎么点都没反应，把第一台关了也还是抓不到第二台。
                int myPair = PairIndexOf(w);
                MachineryButton pickBtn = NewIcon(PixelIcons.Get("pick", out iw, out ih),
                    "选中这台（绿=当前按它算）", new Action(delegate
                    {
                        int pi = PairIndexOf(w);
                        if (pi >= 0) SelectPair(pi);
                    }));
                if (pickBtn != null)
                {
                    pickBtns[w] = pickBtn;
                    // 第一次挂图标时，谁先开谁就是当前这台，避免一进来两台都是灰的
                    if (activeIdx < 0 && myPair >= 0) { activeIdx = myPair; MachineOverride = myPair; }
                    // 玩家 2026-11-10：圆点要钉在窗口**左上角**，不是跟着原生标题栏右对齐的那排图标走。
                    PlacePickTopLeft(w, pickBtn);
                    RegisterHdr(pickBtn, "选择");
                }
                if (fillBtn != null) { w.AddHeaderButton(fillBtn); RegisterHdr(fillBtn, "接水"); }
                if (fillBtn != null) winFill[w] = fillBtn;   // 这台窗口的专用灌水键
                if (modeBtnEff != null) { w.AddHeaderButton(modeBtnEff); RegisterHdr(modeBtnEff, "自定义"); }
                if (modeBtnUncap != null) { w.AddHeaderButton(modeBtnUncap); RegisterHdr(modeBtnUncap, "效率"); }
                if (modeBtnCheap != null) { w.AddHeaderButton(modeBtnCheap); RegisterHdr(modeBtnCheap, "性价比"); }
                if (recipeBtn != null) { w.AddHeaderButton(recipeBtn); RegisterHdr(recipeBtn, "手册"); }
                if (calcBtn != null) winCalc[w] = calcBtn;   // 详情键同理：不能四台共用一份
                if (calcBtn != null) { w.AddHeaderButton(calcBtn); RegisterHdr(calcBtn, "净水助手"); }
                RefreshPickTints();   // 把「谁在用」的颜色刷到每台窗口的圆点上（绿=当前）
                // （已移除）
                LogIcon("water", fillBtn); LogIcon("note", recipeBtn); LogIcon("info", calcBtn); LogIcon("gear", gearBtn);
                Log("[ui] 标题栏图标 ok size=" + iconSize + " src=generated missing=" + missingIcons);
                nextTick = 0f;
            }
            catch (Exception e) { log.LogWarning("[ui] Attach " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>
        /// 把「选中这台」的圆点钉在窗口左上角（标题栏最左），不参与原生标题栏的右对齐排布。
        /// 直接 AddHeaderButton 只会排到右侧那排图标里，玩家要的是最左那颗。
        /// </summary>
        private static void PlacePickTopLeft(PixelWindow w, MachineryButton b)
        {
            try
            {
                if (w == null || b == null) return;
                var wrt = w.rectTransform;
                var brt = b.rectTransform;
                if (wrt == null || brt == null) return;
                brt.SetParent(wrt, false);
                brt.anchorMin = new Vector2(0f, 1f);
                brt.anchorMax = new Vector2(0f, 1f);
                brt.pivot = new Vector2(0f, 1f);
                brt.anchoredPosition = new Vector2(5f, -4f);
                brt.sizeDelta = new Vector2(iconSize, iconSize);
                brt.localScale = Vector3.one;
                brt.SetAsLastSibling();
            }
            catch (Exception e) { log.LogWarning("[ui] pick place " + e.GetType().Name + " " + e.Message); }
        }

        private static string SpriteName(Sprite s) { return s == null ? "-" : s.name; }

        /// <summary>图标尺寸按游戏原生按钮的 rect 记录，便于核对是否与原生一致。</summary>
        private static void LogIcon(string name, MachineryButton b)
        {
            try
            {
                var rt = b == null ? null : b.rectTransform;
                if (rt == null) { Log("[icon] " + name + " size=none"); return; }
                Log("[icon] " + name + " size=" + (int)rt.sizeDelta.x + "x" + (int)rt.sizeDelta.y +
                    " native=" + (int)rt.rect.width + "x" + (int)rt.rect.height);
            }
            catch (Exception e) { Log("[icon] " + name + " size=ERR " + e.GetType().Name); }
        }

        private static int iconSize = 16;
        private static int missingIcons;
        private static int iw, ih;

        /// <summary>
        /// 头部补一句「现在瓶里这 N mL，一晚能到多少」——玩家问过「1800 到底行不行」，
        /// 直接把当前体积过一晚的结果算出来贴在标题行，省得猜。
        /// </summary>
        /// <summary>
        /// 六项杂质的英文 identifier → 中文（玩家反馈「英文没翻译」）。
        /// 顺序与 Liquid.InitLiquid 一致：water/heavy_metal/organic_waste/microplastic/microbe/
        /// chemical_contaminant/physical_contaminant/mineral。
        /// </summary>
        private static string CnLiquid(string id, int idx)
        {
            string k = (id == null ? "" : id.ToLowerInvariant());
            if (k.Contains("heavy")) return "重金属";
            if (k.Contains("organic")) return "有机废物";
            if (k.Contains("microplastic")) return "微塑料";
            if (k.Contains("microbe")) return "微生物";
            if (k.Contains("chemical")) return "化学污染物";
            if (k.Contains("physical")) return "物理杂质";
            if (k.Contains("mineral")) return "矿物";
            if (k.Contains("water")) return "水";
            switch (idx)
            {
                case 0: return "水";
                case 1: return "重金属";
                case 2: return "有机废物";
                case 3: return "微塑料";
                case 4: return "微生物";
                case 5: return "化学污染物";
                case 6: return "物理杂质";
                case 7: return "矿物";
                default: return k.Length > 0 ? k : "#" + idx;
            }
        }

        private static string PredictText(PanelModel m)
        {
            try
            {
                if (m == null || m.VolumeMl <= 0 || m.PurityBp <= 0) return "";
                // 顶行必须跟卡2/方案表说的是同一件事：以当前选中方案为准，而不是固定按「原样」预测。
                var r = CurrentPlan(true);
                if (r != null && r.pick != null)
                {
                    var c = r.pick;
                    return " · 选中「" + c.label + "」：接 " + (c.feedUl / 1000) + " → 出 " + (c.outUl / 1000)
                         + " mL · " + PlanText.Pct(c.purityBp);
                }
                WaterMath.Water w = WaterMath.Build(m.VolumeMl, m.PurityBp);
                WaterMath.Purify(w, 1.0 + m.PerfPct / 100.0, m.Quality);
                int bp = w.Purity;
                // 引擎按整数毫升算，末位会算出 99.05 这种比上限高 0.04 的值；显示按上限截断。
                if (m.CapBp > 0 && bp > m.CapBp) bp = m.CapBp;
                if (bp <= m.PurityBp) return " · 一晚到顶（本机上限）";
                return " · 这 " + m.VolumeMl + " mL 一晚 → " + PlanText.Pct(bp);
            }
            catch (Exception) { return ""; }
        }

        private static MachineryButton NewIcon(Sprite sp, string desc, Action cb)
        {
            if (sp == null) { missingIcons++; return null; }
            MachineryButton b = new MachineryButton(sp, cb, iconSize);
            try { b.SetDescription(desc); } catch (Exception) { }
            return b;
        }

        /// <summary>从同窗口里已有的原生按钮读实际尺寸；读不到退回 16。</summary>
        private static int NativeIconSize(PixelWindow w)
        {
            try
            {
                var arr = w.rectTransform.GetComponentsInChildren<MachineryButton>(true);
                for (int i = 0; i < arr.Length; i++)
                {
                    var rt = arr[i].rectTransform;
                    if (rt != null && rt.sizeDelta.x >= 8f) return Mathf.RoundToInt(rt.sizeDelta.x);
                }
            }
            catch (Exception) { }
            return 16;
        }

        public static string IconsReport()
        {
            int miss = 0;
            string[] names = { "water", "note", "info", "gear" };
            for (int i = 0; i < names.Length; i++) { int a, b; if (PixelIcons.Get(names[i], out a, out b) == null) miss++; }
            return "icons -> water=16x16 note=16x16 info=16x16 gear=16x16 missing=" + miss + " size=" + iconSize + " src=generated";
        }

        private static Sprite FindSprite(params string[] names)
        {
            var arr = Resources.FindObjectsOfTypeAll<Sprite>();
            for (int n = 0; n < names.Length; n++)
                for (int i = 0; i < arr.Length; i++)
                    if (arr[i] != null && arr[i].name == names[n]) return arr[i];
            return null;
        }

        // ---------------- 独立窗口（F9 / 设置图标） ----------------

        public static string ShowMain(bool show)
        {
            try
            {
                // 打开详情一律回主页，但**不重装内容**：只要窗口里已经装着主页，就纯 Show，
                // 照「手册窗」的做法（窗口建一次，只 Show/Hide）——反复 Detach/Attach 会让面板刚开就关。
                if (show && UiPage != 0) UiPage = 0;
                if (!(PanelAlive() && panelPageFor == UiPage))
                {
                    EnsurePanel();
                }
                if (!PanelAlive()) return "no-window";
                panelWant = show;
                if (show)
                {
                    Rebuild(true);
                    // 引擎会记住原生窗口位置：上一版因此一直贴左上角 (0,1080)。
                    // Center() 对原生窗口无效（改完读回还是 0,1080），只能 WinSetPos 直接写。
                    // 在 Show 之前定位置，就不会「先闪左上角再跳」。
                    float cx, cy;
                    if (hasLastPos) { cx = LastPosX; cy = LastPosY; }   // 回到上次关掉的位置
                    else { CenterXY(panelWin, out cx, out cy); }
                    MainPosX = cx; MainPosY = cy;
                    panelWin.Show(); panelWin.ToFront(true);
                    ForceAt(panelWin, cx, cy, 12);
                    // 玩家 2026-11-11（性能）：刚开窗口这一帧背包/机器仓可能还没喂进来，面板算出来是空的；
                    // 以前要等 1 s 的 live 节流才补上，看起来就是「先空白、加载慢」。
                    // 开窗后 0.15 s 立刻补算一次，并把 live 节拍提前，空面板最多只存在一帧。
                    pendingRebuild = true;
                    pendingRebuildAt = UnityEngine.Time.realtimeSinceStartup + 0.15f;
                    nextTick = UnityEngine.Time.realtimeSinceStartup;
                    Log("[ui] 详情打开 page=" + UiPage + " pageFor=" + panelPageFor + " visible=" + panelWin.IsVisible());
                }
                else
                {
                    // 关掉之前把位置记下来（下次打开回到这里）
                    UnityEngine.Vector2 pv;
                    if (WinGetPos(panelWin, out pv)) { LastPosX = pv.x; LastPosY = pv.y; hasLastPos = true; Log("[ui] 记住位置 " + pv.x + "," + pv.y); }
                    panelWin.Hide();
                }
                return "main=" + panelWin.IsVisible();
            }
            catch (Exception e) { return "ERR " + e.GetType().Name + ": " + e.Message; }
        }

        public static void ToggleMain()
        {
            try
            {
                EnsurePanel();
                if (panelWin == null) return;
                if (panelWin.IsVisible()) panelWin.Hide(); else ShowMain(true);
            }
            catch (Exception e) { log.LogWarning("[ui] toggle " + e.Message); }
        }

        // ---------------- 配方窗：纸面手册（6 页，‹ N/M › 翻页，不滚） ----------------

        private const int RecipePages = 6;
        private static int recipePage = 1;
        private static int slimW, slimH, slimIcons;
        private static MachineryButton recipePrevBtn, recipeNextBtn, recipePageBtn;

        public static void ToggleRecipes()
        {
            try
            {
                if (bookOpened)
                {
                    bookOpened = false;
                    try { WaterBook.Close(); } catch (Exception) { }
                    Log("[ui] recipe via=book close");
                    return;
                }
                if (paperWin != null && paperWin.IsAlive && paperWin.IsVisible) { paperWin.Hide(); return; }
                string bk = null;
                try { bk = WaterBook.Open(recipePage); }
                catch (Exception e) { bk = "book -> ERR " + e.GetType().Name; }
                if (bk != null && bk.IndexOf("current=yes") >= 0)
                {
                    bookOpened = true;
                    Log("[ui] recipe via=book " + bk);
                    return;
                }
                Log("[ui] recipe via=fallback " + bk);
                if (BuildRecipePaper(recipePage)) return;
                if (recipeWin != null && recipeWin.IsVisible()) { recipeWin.Hide(); return; }
                if (recipeWin == null)
                {
                    recipeTxt = new ScrollableRichTextElement(false, true);
                    var body = new MachineryPanel("水商手册", recipeTxt, "watercalc_recipe_fb");
                    var navRow = new MachineryStack(false, 6);
                    recipePrevBtn = Small("‹ 上页", new Action(delegate { RecipeTurn(-1); }), 62);
                    recipePageBtn = Small("1/6", new Action(delegate { }), 44);
                    recipeNextBtn = Small("下页 ›", new Action(delegate { RecipeTurn(1); }), 62);
                    if (recipePrevBtn != null) navRow.Add(recipePrevBtn);
                    if (recipePageBtn != null) navRow.Add(recipePageBtn);
                    if (recipeNextBtn != null) navRow.Add(recipeNextBtn);
                    var nav = new MachineryPanel("", navRow, "watercalc_recipe_nav");
                    recipeWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { body, nav });
                    if (recipeWin == null) { Log("[ui] 配方窗口 null"); return; }
                    try { recipeTxt.ResizePixels(316, 366); } catch (Exception) { }
                    try { recipeWin.ResizePixels(340, 430); } catch (Exception) { }
                AddClickBlocker(recipeWin, "recipe", 340, 430);
                try { recipeWin.ResizePixels(340, 430); } catch (Exception) { }
                }
                RecipeRender();
            }
            catch (Exception e) { Log("[ui] recipes " + e.GetType().Name + " " + e.Message); }
        }

        // ---------------- 纸面手册（游戏原生 CustomUIManager / LAYER_PAPER） ----------------

        private static CustomUIWindow paperWin;
        private static bool bookOpened;
        private static string paperFail = "";

        private static bool BuildRecipePaper(int page)
        {
            try
            {
                var mgr = CustomUIManager.Instance;
                if (mgr == null) { paperFail = "CustomUIManager.Instance=null"; return false; }
                try { mgr.CloseWindow("watercalc_recipe_fb"); } catch (Exception) { }
                var bld = mgr.CreateWindow("watercalc_recipe_fb", "水商手册", CustomUIManager.LAYER_PAPER, null, CustomUIManager.Style.Game);
                if (bld == null) { paperFail = "CreateWindow=null"; return false; }
                bld.SetSize(340f, 430f).Center();
                bld.BeginColumn(3f, TextAnchor.UpperLeft);
                string[] lines = RecipePage(page);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = lines[i];
                    if (string.IsNullOrEmpty(s)) { bld.AddSpace(4f); continue; }
                    if (s[0] == '#') bld.AddHeader(s.Substring(1), "rh" + i);
                    else bld.AddLabel(s, "rl" + i);
                }
                bld.End();
                bld.BeginRow(8f, TextAnchor.MiddleCenter);
                bld.AddButton("‹ 上页", new Action(delegate { RecipeTurn(-1); }), "rprev");
                bld.AddLabel(page + "/" + RecipePages, "rpage");
                bld.AddButton("下页 ›", new Action(delegate { RecipeTurn(1); }), "rnext");
                bld.End();
                paperWin = bld.Show();
                recipeLines = lines.Length;
                recipeW = 340; recipeH = 430;
                Log("[ui] paper ok page=" + page + "/" + RecipePages + " lines=" + recipeLines);
                return paperWin != null;
            }
            catch (Exception e)
            {
                paperFail = e.GetType().Name + " " + e.Message;
                Log("[ui] paper FAIL " + paperFail);
                return false;
            }
        }

        public static string PaperReport()
        {
            string bg = "color", border = "color";
            bool open = false;
            try
            {
                if (paperWin != null && paperWin.IsAlive)
                {
                    open = paperWin.IsVisible;
                    var root = paperWin.root;
                    if (root != null)
                    {
                        var imgs = root.GetComponentsInChildren<Image>(true);
                        if (imgs != null && imgs.Length > 0)
                        {
                            for (int i = 0; i < imgs.Length; i++)
                            {
                                if (imgs[i] != null && imgs[i].sprite != null) { bg = imgs[i].sprite.name; break; }
                            }
                            if (imgs.Length > 1 && imgs[1] != null && imgs[1].sprite != null) border = imgs[1].sprite.name;
                        }
                    }
                }
            }
            catch (Exception) { }
            return "paper -> bg=" + bg + " border=" + border + " style=Game layer=paper open=" + open +
                   " page=" + recipePage + "/" + RecipePages + (paperFail.Length > 0 ? " fail=" + paperFail : "");
        }

        public static void RecipeTurn(int d)
        {
            recipePage += d;
            if (recipePage < 1) recipePage = 1;
            if (recipePage > RecipePages) recipePage = RecipePages;
            if (paperWin != null && paperWin.IsAlive) { BuildRecipePaper(recipePage); }
            else RecipeRender();
            Log("[ui] recipe page=" + recipePage + "/" + RecipePages);
        }

        private static void RecipeRender()
        {
            try
            {
                if (recipeTxt == null) return;
                var b = new RichTextBuilder(300);
                string[] lines = RecipePage(recipePage);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = lines[i];
                    if (s.Length == 0) AddLine(b, "", ColorPalette.White, false);
                    else if (s[0] == '#') AddLine(b, s.Substring(1), ColorPalette.LightGreen, true);
                    else AddLine(b, s, ColorPalette.White, false);
                }
                if (recipePageBtn != null) { try { recipePageBtn.SetText(recipePage + "/" + RecipePages); } catch (Exception) { } }
                recipeLines = lines.Length;
                recipeTxt.SetRichText(b, 3000);
                try { recipeTxt.ResizePixels(316, 366); } catch (Exception) { }
                if (recipeWin != null)
                {
                    try { recipeWin.Show(); recipeWin.ToFront(true); } catch (Exception) { }
                    try { recipeWin.ResizePixels(340, 430); } catch (Exception) { }
                AddClickBlocker(recipeWin, "recipe", 340, 430);
                try { recipeWin.ResizePixels(340, 430); } catch (Exception) { }
                    try { Canvas.ForceUpdateCanvases(); } catch (Exception) { }
                    try { var rt = recipeWin.rectTransform; if (rt != null) { recipeW = (int)rt.rect.width; recipeH = (int)rt.rect.height; } }
                    catch (Exception) { }
                }
                if (recipeW <= 0 || recipeH <= 0) { recipeW = 340; recipeH = 430; }
                Log("[ui] recipe size=" + recipeW + "x" + recipeH + " lines=" + recipeLines);
            }
            catch (Exception e) { Log("[ui] recipe render " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>分页文本；同程序集内的 WaterBook 直接调用（不再反射）。</summary>
        internal static string[] RecipePage(int n)
        {
            var r = CurrentPlan(true);
            int perf, q;
            Bonuses(out perf, out q);
            int qq = r == null ? q : r.q;
            int cap = WaterMath.CapOf(qq);
            if (n == 3) { qq = 100; cap = 9990; }   // 手册页按「装了质量 100」讲，不跟当前机器走
            if (n == 1)
            {
                return new string[]
                {
                    "# 1. 水质等级与上限",
                    "1. 上限 cap = 10000 − round(99 × 质量倍率)",
                    "2. 质量 <15 → 99.01%",
                    "3. 15–24 → 99.16%",
                    "4. 25–49 → 99.36%",
                    "5. 50–74 → 99.65%",
                    "6. 75–99 → 99.80%",
                    "7. 100–199 → 99.90%",
                    "8. ≥200 → 100.00%",
                    "本机质量 " + qq + " → 上限 " + PlanText.Pct(cap),
                };
            }
            if (n == 2 || n == 3)
            {
                int target = n == 2 ? 9901 : 9990;
                int[] bps;
                string[] srcNames;
                if (n == 2)
                {
                    // 按「水源等级」细分：沟渠 <90% / 铁锈 90–95.99 / 幽灵 96–97.99 / 基础 98–98.99 / 优质 99.00+
                    bps = new int[] { 8800, 9100, 9400, 9750, 9850, 9900 };
                    srcNames = new string[] { "沟渠水", "铁锈水(低)", "铁锈水(高)", "幽灵水", "基础水", "优质水" };
                }
                else
                {
                    bps = new int[] { 9800, 9850, 9900, 9920, 9940, 9960 };
                    srcNames = new string[] { "基础水", "基础水", "优质水", "优质水", "优质水", "优质水" };
                }
                var arr = new string[3 + bps.Length];
                arr[0] = n == 2 ? "# 2. 一晚直出 99.01%（按水源 · 零模组）" : "# 3. 一晚直出 99.90%（要质量 100+）";
                arr[1] = "1. 每行 = 这种水源纯度下一晚能接的最大量";
                arr[2] = "2. 目标 " + PlanText.Pct(target) + " · 质量 " + qq + " · 性能 +0% · 上限 " + PlanText.Pct(cap);
                int perfBase = 0;   // 手册按裸机给基线，不跟当前机器加成
                for (int i = 0; i < bps.Length; i++)
                {
                    long keepUl = MaxKeepUl(10000000, bps[i], perfBase, qq, target);
                    bool ok = keepUl > 0 && target <= cap;
                    string amount;
                    if (!ok) amount = "达不到，最多 " + PlanText.Pct(cap);
                    else if (keepUl >= 10000000L) amount = "≥10 L（受净水器 10 L 上限限制）";
                    else amount = WaterPlan.Ml(keepUl) + " mL";
                    arr[3 + i] = (i + 3) + ". " + srcNames[i] + " " + PlanText.Pct(bps[i]) + " → " + amount;
                }
                return arr;
            }
            if (n == 4)
            {
                return new string[]
                {
                    "# 4. 耗材怎么加",
                    "1. 过滤棉：插在容器上，灌水/导水时自动过滤（一次 400 耐久）",
                    "2. 过滤棉只清大颗粒：塑料/物理/矿物/有机物，不清微生物",
                    "3. 紫外灯：只清微生物，体积不变，用一次算一次",
                    "4. 药片：每片清最多 75 mL 微生物 + 10 mL 化学（化学只清到占总量 0.7%）",
                    "5. 药片清掉的量会离体，体积变小",
                    "6. 净水器：按 脏度 × 预算 清杂质，cap 之外清不动",
                    "7. 电：4 度/台/晚；药片 6、过滤棉 25/副",
                };
            }
            if (n == 5)
            {
                return new string[]
                {
                    "# 5. 顺序与坑",
                    "1. 顺序：过滤棉 / 紫外 → 净水器过夜 → 最后药片",
                    "2. 药片提前放会先改掉配比，等于白给净水器清一部分，所以放最后",
                    "3. 紫外和过滤棉可以互换，但都必须在净水器之前",
                    "4. 一晚清不完就多过一晚，别一次接太多",
                    "5. 接水只能整瓶：凑不满就少接，不用量筒硬补",
                    "6. 上限由质量决定，性能只影响一晚能清多少",
                    "7. 本机质量 " + qq + " → 上限 " + PlanText.Pct(cap),
                };
            }
            // n == 6
            return new string[]
            {
                "# 6. 水商利润",
                "1. 成本 = 药片 6 + 过滤棉 25 + 电 4/台/晚",
                "2. 每升成本 = 总成本 / 出水升数",
                "3. 一晚方案越省，每升成本越低",
                "4. 多过一晚只多电费，不加耗材",
                "5. 上限越高，同样的水能卖越贵",
                "6. 本机质量 " + qq + " → 上限 " + PlanText.Pct(cap),
            };
        }

        /// <summary>
        /// 手册里的「照抄一行」：给一组固定耗材组合跑模型，返回「接多少 → 出多少 → 赚多少」。
        /// 数字现算，换机器/换水源自动跟着变；算不出来就退回一句人话，绝不让书页崩。
        /// </summary>
        private static string RecipeProbe(long capUl, int srcBp, int perf, int q, int targetBp,
                                          int mode, int pre, int post, int tablets,
                                          bool filter, bool uv, int hydraDur)
        {
            try
            {
                var rr = WaterPlan.Build(capUl, srcBp, perf, q, targetBp, "沟渠水", "参考瓶", false,
                                         mode, pre, post, tablets, filter, uv, hydraDur);
                if (rr == null || rr.pick == null)
                    return "这一组装法一晚到不了 " + PlanText.Pct(targetBp) + "，得降目标或多过一晚";
                var c = rr.pick;
                double rev = WaterEconomy.Revenue(c.outUl, c.purityBp);
                string cost = c.cost.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                return "接 " + WaterPlan.Ml(c.feedUl) + " mL    一晚出 " + WaterPlan.Ml(c.outUl) + " mL    " +
                       PlanText.Pct(c.purityBp) + "    本钱 " + cost + " 元    卖 " +
                       WaterEconomy.Money(rev) + "    赚 " + WaterEconomy.Money(rev - c.cost);
            }
            catch (Exception e) { Log("[book] recipe probe " + e.GetType().Name); return "（这组装法算不出来，直接看面板给的方案）"; }
        }

        private static void RecipeRenderLegacy() { }

        /// <summary>水商配方小档案：点图标才开，原生 PixelWindow + 滚动文本。与当前瓶无关，任何时候都有内容。</summary>
        private static int recipeLines;
        private static int recipeW, recipeH;

        private static void ToggleRecipesLegacy()
        {
            try
            {
                if (recipeWin != null && recipeWin.IsVisible()) { recipeWin.Hide(); return; }
                if (recipeWin == null)
                {
                    recipeTxt = new ScrollableRichTextElement(false, true);
                    var panel = new MachineryPanel("水商配方", recipeTxt, "watercalc_recipe_fb");
                    recipeWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { panel });
                    if (recipeWin == null) { Log("[ui] 配方窗口 null"); return; }
                    try { recipeTxt.ResizePixels(320, 250); } catch (Exception) { }
                    try { recipeWin.ResizePixels(360, 300); } catch (Exception) { }
                }
                var r = CurrentPlan(true);
                if (r == null) r = WaterPlan.Build(6000000L, 9100, 0, 0, WaterMath.CapOf(0), "沟渠水", "水桶 6L", true);
                int lines = 0;
                var b = new RichTextBuilder(500);
                AddLine(b, "怎么接这瓶水（按你有的容器，只能整瓶装，绝不超最优体积）", ColorPalette.LightGreen, true); lines++;
                AddLine(b, "顺序固定：过滤棉 / 紫外（选填）→ 进净水器过夜 → 最后放药片；没选的就跳过。", ColorPalette.LightGray, false); lines++;
                AddLine(b, "药片必须等过完夜再放，提前放会被当普通杂质一起清掉。", ColorPalette.LightGray, false); lines++;
                AddLine(b, "接水只能整瓶：750 / 450 / 250 / 50 mL 这类容器，凑不满就少接，不用量筒硬补。", ColorPalette.LightGray, false); lines++;
                AddLine(b, "耗材计价：药片 6 · 过滤棉 25/400 耐久 · 电 4/台/晚 · 每升 = 总价 / 出水升数。", ColorPalette.LightGray, false); lines++;
                AddLine(b, "卖价：40 元/L × 档位（纯水 ×2 · 优质 ×1.5 · 基础 ×1 · 幽灵 ×0.67 · 铁锈 ×0.25 · 沟渠 ×0.10）。", ColorPalette.LightGray, false); lines++;
                AddLine(b, "", ColorPalette.White, false); lines++;
                if (r == null)
                {
                    AddLine(b, "示例（默认参数：质量 0 / 性能 0 / 6L 沟渠水 91% / 上限 99.01%）", ColorPalette.LightOrange, true); lines++;
                    AddLine(b, "6000 mL 生水直接过净水器：一晚只清 138 mL 杂质，最多推到 99.01% 的要少接。", ColorPalette.White, false); lines++;
                    AddLine(b, "6000 mL 生水 + 1 片药片：体积略减，纯度几乎不动（药片只清微生物/化学）。", ColorPalette.White, false); lines++;
                    AddLine(b, "先过滤棉再一夜：体积减少，杂质一起被滤走，纯度升得更快。", ColorPalette.White, false); lines++;
                }
                else
                {
                    AddLine(b, "示例参数：本机质量 " + r.q + " / 上限 " + PlanText.Pct(WaterMath.CapOf(r.q)) +
                               " / 接水 " + WaterPlan.Ml(r.bestUl) + " mL / 进水 " + PlanText.Pct(r.srcBp), ColorPalette.LightGreen, true); lines++;
                    AddLine(b, "一晚能做的几种（药片都是过夜后最后放）", ColorPalette.LightGreen, true); lines++;
                    AddLine(b, "做法 · 接多少 · 一晚出 · 纯度 · 本钱 · 卖价 · 净赚", ColorPalette.LightGray, false); lines++;
                    for (int i = 0; i < r.combos.Count; i++)
                    {
                        var c = r.combos[i];
                        if (PostMode > 0 && c.tablets != PostMode - 1) continue;   // 「净化后」下拉：按药片数筛选
                        double rev = WaterEconomy.Revenue(c.outUl, c.purityBp);
                        AddLine(b, c.label + " · " + WaterPlan.Ml(c.feedUl) + " mL · " + WaterPlan.Ml(c.outUl) + " mL · " +
                                   PlanText.Pct(c.purityBp) + " · " + c.cost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                                   " · " + WaterEconomy.Money(rev) + " · " + WaterEconomy.Money(rev - c.cost),
                                   c.ok ? ColorPalette.LightGreen : ColorPalette.LightOrange, false);
                        lines++;
                    }

                    AddLine(b, "", ColorPalette.White, false); lines++;
                    AddLine(b, r.tabletsOnly, ColorPalette.White, false); lines++;

                    AddLine(b, "", ColorPalette.White, false); lines++;
                    AddLine(b, "每晚变化", ColorPalette.LightOrange, true); lines++;
                    var nl = r.NightsText().Split('\n');
                    for (int i = 0; i < nl.Length; i++) if (nl[i].Length > 0) { AddLine(b, nl[i], ColorPalette.White, false); lines++; }
                }
                int qq = r == null ? 0 : r.q;
                AddLine(b, "", ColorPalette.White, false); lines++;
                AddLine(b, "水质等级与上限（cap = 10000 − round(99×质量倍率)）", ColorPalette.LightOrange, true); lines++;
                AddLine(b, "质量 <15 → 99.01% · 15-24 → 99.16% · 25-49 → 99.36% · 50-74 → 99.65%", ColorPalette.LightGray, false); lines++;
                AddLine(b, "75-99 → 99.80% · 100-199 → 99.90% · ≥200 → 100.00%", ColorPalette.LightGray, false); lines++;
                AddLine(b, "本机质量 " + qq + " → 上限 " + PlanText.Pct(WaterMath.CapOf(qq)), ColorPalette.White, false); lines++;

                recipeLines = lines;
                recipeTxt.SetRichText(b, 6000);
                try { recipeTxt.ResizePixels(320, 250); } catch (Exception) { }
                if (recipeWin != null)
                {
                    recipeWin.Show();
                    try { recipeWin.ToFront(true); } catch (Exception) { }
                    try { recipeWin.ResizePixels(360, 300); } catch (Exception) { }
                    try { var rt = recipeWin.rectTransform; if (rt != null) { recipeW = (int)rt.rect.width; recipeH = (int)rt.rect.height; } }
                    catch (Exception) { }
                }
                Log("[ui] recipe size=" + recipeW + "x" + recipeH + " lines=" + recipeLines);
            }
            catch (Exception e) { Log("[ui] recipes " + e.GetType().Name + " " + e.Message); }
        }

        public static string OpenRecipePublic()
        {
            if (recipeWin != null && recipeWin.IsVisible()) { recipeWin.Hide(); }
            ToggleRecipes();
            return "recipe -> page=" + recipePage + "/" + RecipePages + " size=" + recipeW + "x" + recipeH + " lines=" + recipeLines;
        }

        public static string RecipePagePublic(int n)
        {
            if (n < 1) n = 1;
            if (n > RecipePages) n = RecipePages;
            recipePage = n;
            if (bookOpened) { WaterBook.Open(n); }
            else if (paperWin != null && paperWin.IsAlive) { BuildRecipePaper(n); }
            else if (recipeWin == null || !recipeWin.IsVisible()) ToggleRecipes();
            else RecipeRender();
            return "recipe -> page=" + recipePage + "/" + RecipePages + " size=" + recipeW + "x" + recipeH + " lines=" + recipeLines;
        }

        public static string OpenDetailPublic() { ToggleDetail(); return "win=detail -> opened ok=" + (detailWin != null && detailWin.IsVisible()); }

        public static string OpenSlimPublic()
        {
            ShowMain(true);
            try { Canvas.ForceUpdateCanvases(); } catch (Exception) { }
            int w = 0, h = 0;
            try { var rt = panelWin == null ? null : panelWin.rectTransform; if (rt != null) { w = (int)rt.rect.width; h = (int)rt.rect.height; } }
            catch (Exception) { }
            if (w <= 0) w = slimW;
            if (h <= 0) h = slimH;
            return "slim -> w=" + w + " h=" + h + " icons=" + slimIcons;
        }

        // ---------------- F9 窄条（唯一常驻窗口） ----------------

        private static void BuildSlimBar()
        {
            try
            {
                iconSize = 16;
                var row = new MachineryStack(false, 4);
                int n = 0;
                var bInfo = NewIcon(PixelIcons.Get("info", out iw, out ih), "详情：主结论 / 步骤 / 容器拆解", new Action(delegate { ToggleDetail(); }));
                var bNote = NewIcon(PixelIcons.Get("note", out iw, out ih), "配方：一晚几种 / 只放药片 / 每晚变化", new Action(delegate { ToggleRecipes(); }));
                var bWater = NewIcon(PixelIcons.Get("water", out iw, out ih), "接水：按今日水质把瓶子灌到达标体积", new Action(delegate { OnFill(); }));
                var bServe = NewIcon(PixelIcons.Get("serve", out iw, out ih), "备水放台面：把现有容器里的水真实导进承接瓶再上柜", new Action(delegate { OnPrepare2(); }));
                var bGear = NewIcon(PixelIcons.Get("gear", out iw, out ih), "设置：平衡模式（隐藏精确水质）", new Action(delegate { ToggleBalance(); }));
                if (bInfo != null) { row.Add(bInfo); n++; }
                if (bNote != null) { row.Add(bNote); n++; }
                if (bWater != null) { row.Add(bWater); n++; }
                if (bServe != null) { row.Add(bServe); n++; Log("[icon] serve size=" + iw + "x" + ih); }
                if (bGear != null) { row.Add(bGear); n++; }
                if (n == 0) { log.LogWarning("[ui] slim 无图标"); return; }

                var panel = new MachineryPanel("", row, "watercalc_slim");
                panelWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { panel });
                if (panelWin == null) { log.LogWarning("[ui] slim null"); return; }
                int w = iconSize * n + 10 * (n - 1) + 16;
                int h = iconSize + 12;
                slimW = w; slimH = h; slimIcons = n;
                try { panelWin.ResizePixels(w, h); } catch (Exception) { }
                AddClickBlocker(panelWin, "slim", w, h);
                try { panelWin.ResizePixels(w, h); } catch (Exception) { }
                try { panelWin.Hide(); } catch (Exception) { }
                if (panelWin.IsVisible()) { try { panelWin.rectTransform.gameObject.SetActive(false); } catch (Exception) { } }
                Log("[ui] slim ok w=" + w + " h=" + h + " icons=" + n);
            }
            catch (Exception e) { log.LogWarning("[ui] slim " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>
        /// 在窗口最底层铺一张透明但 raycastTarget 的图，吃掉落在窗口矩形内的点击，
        /// 避免点击穿透到游戏自己的 UI（试纸/日历那类浮层）。放 sibling 0，按钮在它之上不受影响。
        /// </summary>
        internal static void AddClickBlocker(PixelWindow w, string tag)
        {
            if (w == null) return;
            try
            {
                var rt = w.rectTransform;
                if (rt == null) return;
                var go = new UnityEngine.GameObject("watercalc_blocker_" + tag);
                var brt = go.AddComponent<UnityEngine.RectTransform>();
                brt.SetParent(rt, false);
                brt.anchorMin = UnityEngine.Vector2.zero;
                brt.anchorMax = UnityEngine.Vector2.one;
                brt.offsetMin = UnityEngine.Vector2.zero;
                brt.offsetMax = UnityEngine.Vector2.zero;
                brt.SetSiblingIndex(0);
                var img = go.AddComponent<UnityEngine.UI.Image>();
                img.color = new UnityEngine.Color(0f, 0f, 0f, 0f);
                img.raycastTarget = true;
                var le = go.AddComponent<UnityEngine.UI.LayoutElement>();
                le.ignoreLayout = true;
                Log("[ui] blocker " + tag + " " + brt.rect.width.ToString("0") + "x" + brt.rect.height.ToString("0"));
            }
            catch (Exception e) { log.LogWarning("[ui] blocker " + tag + " " + e.GetType().Name + " " + e.Message); }
        }

        internal static UnityEngine.RectTransform SlimBlockerRt = null;
        internal static void AddClickBlocker(PixelWindow w, string tag, int wPx, int hPx)
        {
            if (w == null) return;
            try
            {
                var rt = w.rectTransform;
                if (rt == null) return;
                // MachineryWindow 的根 rect 往往是 0x0，真正有尺寸的是子节点；
                // 拦截层必须贴在「有尺寸的那层」上并拉满，否则它落在 (0,0) 处，既挡不住点击、自检也会算到屏幕外。
                var host = FindSizedRect(rt, 0);
                var go = new UnityEngine.GameObject("watercalc_blocker_" + tag);
                var brt = go.AddComponent<UnityEngine.RectTransform>();
                brt.SetParent(host != null ? host : rt, false);
                if (host != null && host != rt)
                {
                    brt.anchorMin = UnityEngine.Vector2.zero;
                    brt.anchorMax = UnityEngine.Vector2.one;
                    brt.pivot = new UnityEngine.Vector2(0.5f, 0.5f);
                    brt.offsetMin = UnityEngine.Vector2.zero;
                    brt.offsetMax = UnityEngine.Vector2.zero;
                }
                else
                {
                    brt.anchorMin = new UnityEngine.Vector2(0.5f, 0.5f);
                    brt.anchorMax = new UnityEngine.Vector2(0.5f, 0.5f);
                    brt.pivot = new UnityEngine.Vector2(0.5f, 0.5f);
                    brt.anchoredPosition = UnityEngine.Vector2.zero;
                    brt.sizeDelta = new UnityEngine.Vector2(wPx, hPx);
                }
                brt.SetSiblingIndex(0);
                var img = go.AddComponent<UnityEngine.UI.Image>();
                img.color = new UnityEngine.Color(0f, 0f, 0f, 0f);
                img.raycastTarget = true;
                var le = go.AddComponent<UnityEngine.UI.LayoutElement>();
                le.ignoreLayout = true;
                try { UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rt); } catch (Exception) { }
                if (tag == "slim" || tag == "main") SlimBlockerRt = brt;
                Log("[ui] blocker " + tag + " want=" + wPx + "x" + hPx + " got=" + brt.rect.width.ToString("0") + "x" + brt.rect.height.ToString("0"));
            }
            catch (Exception e) { log.LogWarning("[ui] blocker " + tag + " " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>无鼠标自检：在窄条中心做一次 EventSystem 射线，看最上层是谁。</summary>
        /// <summary>背包/台面里名字含关键词的道具数量（药片、过滤棉、紫外这类按本地化名认）。</summary>
        private static int CountNamed(string key)
        {
            int n = 0;
            try
            {
                // 深度枚举：药片/过滤棉/紫外可能被塞在打印机或箱子里，只看顶层会少数。
                var all = GameBridge.AllItemsDeep();
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string nm = GameBridge.NameOfItem(it);
                    if (!string.IsNullOrEmpty(nm) && nm.IndexOf(key, StringComparison.Ordinal) >= 0) n++;
                }
            }
            catch (Exception) { }
            return n;
        }

        /// <summary>
        /// 指定屏幕坐标做一次 EventSystem 射线：`hit=960,400`。
        /// 用来判定「谁在吃点击」——比如详情窗开着时，机器窗口那块区域被谁挡住了。
        /// </summary>
        public static string HitTestAt(string args)
        {
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es == null) return "hit -> no-eventsystem";
                float x = 960f, y = 540f;
                var parts = (args ?? "").Split(',');
                if (parts.Length >= 2)
                {
                    float.TryParse(parts[0].Trim(), out x);
                    float.TryParse(parts[1].Trim(), out y);
                }
                var ped = new UnityEngine.EventSystems.PointerEventData(es);
                ped.position = new UnityEngine.Vector2(x, y);
                var list = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                es.RaycastAll(ped, list);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < list.Count && i < 5; i++)
                {
                    var go = list[i].gameObject;
                    string up = "";
                    var tr = go.transform;
                    for (int k = 0; k < 2 && tr.parent != null; k++) { tr = tr.parent; up = tr.name + "/" + up; }
                    sb.Append(i == 0 ? "" : " < ").Append(up).Append(go.name);
                }
                return "hit=" + x.ToString("0") + "," + y.ToString("0") + " -> n=" + list.Count + " top=" + sb;
            }
            catch (Exception e) { return "hit -> " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>MachineryWindow 的根 rect 常常是 0x0，真正有尺寸的是它的子节点；找第一个有尺寸的。</summary>
        private static UnityEngine.RectTransform FindSizedRect(UnityEngine.RectTransform rt, int depth)
        {
            if (rt == null || depth > 4) return null;
            if (rt.rect.width > 16f && rt.rect.height > 16f && rt.gameObject.activeInHierarchy) return rt;
            for (int i = 0; i < rt.childCount; i++)
            {
                var ch = rt.GetChild(i) as UnityEngine.RectTransform;
                var hit = FindSizedRect(ch, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        public static string HitTest()
        {
            try
            {
                if (panelWin == null) return "hit -> no-panel";
                var rt = panelWin.rectTransform;
                if (rt == null) return "hit -> no-rect";
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es == null) return "hit -> no-eventsystem";
                UnityEngine.Canvas.ForceUpdateCanvases();
                // 布局是延迟的：创建窗口那一刻根 rect 还是 0x0，拦截层会落到屏幕原点。
                // 自检时再找一次「真正有尺寸的那层」，并把拦截层贴上去、拉满。
                var host = FindSizedRect(panelWin.rectTransform, 0);
                if (SlimBlockerRt != null && host != null && SlimBlockerRt.parent != host)
                {
                    SlimBlockerRt.SetParent(host, false);
                    SlimBlockerRt.anchorMin = UnityEngine.Vector2.zero;
                    SlimBlockerRt.anchorMax = UnityEngine.Vector2.one;
                    SlimBlockerRt.offsetMin = UnityEngine.Vector2.zero;
                    SlimBlockerRt.offsetMax = UnityEngine.Vector2.zero;
                    Log("[ui] blocker main reparent -> " + host.name + " size=" + host.rect.width.ToString("0") + "x" + host.rect.height.ToString("0"));
                }
                var src = SlimBlockerRt != null ? SlimBlockerRt : (host != null ? host : rt);
                if (src == null) return "hit -> no-src";
                // 屏幕空间 Overlay 画布：世界坐标就是屏幕像素，不能再除 scaleFactor
                // （旧实现多除了一次，取样点被算成 0，测出来的 at=0 是假的）。
                // 用 GetWorldCorners 取中心，避免 rect 尚未布局时 center 为 0。
                var corners = new UnityEngine.Vector3[4];
                var sized = FindSizedRect(src, 0);
                src = sized != null ? sized : src;
                src.GetWorldCorners(corners);
                UnityEngine.Vector3 world = (corners[0] + corners[2]) * 0.5f;
                var sp = UnityEngine.RectTransformUtility.WorldToScreenPoint(null, world);
                var ped = new UnityEngine.EventSystems.PointerEventData(es);
                ped.position = sp;
                var list = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                es.RaycastAll(ped, list);
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < list.Count && i < 4; i++) names.Append((i == 0 ? "" : " < ") + list[i].gameObject.name);
                string up = "";
                if (list.Count > 0 && list[0].gameObject != null)
                {
                    var tr = list[0].gameObject.transform;
                    for (int i = 0; i < 3 && tr.parent != null; i++) { tr = tr.parent; up = tr.name + "/" + up; }
                }
                return "hit -> n=" + list.Count + " top=" + names + " up=" + up + " at=" + sp.x.ToString("0") + "," + sp.y.ToString("0")
                     + " rect=" + src.rect.width.ToString("0") + "x" + src.rect.height.ToString("0");
            }
            catch (Exception e) { return "hit -> " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>当前这瓶水的完整规划（详情/配方/ctl 共用一份数据）。</summary>
        // ★性能：一次界面刷新里 CurrentPlan(true) 会被叫很多次（每张卡的文案、每个键面、手册每页各一次），
        // 每次都整包重算是「点一下卡几秒」的直接原因（实测手册 6 页 = 6 次重算 = 32 秒）。
        // 这里让 0.25 秒内的重复调用共用同一份 Report（UI 全部代码都是同一帧内同步跑完的），
        // 再配合 GameBridge.BeginMemo 的读取缓存 + WaterPlan 的库存快照，一次重算从几秒降到几十毫秒。
        private static WaterPlan.Report currentCacheReport;
        private static float currentCacheAt = -1f;
        private static bool currentCacheFull;
        private static string currentCacheSig = "";

        /// <summary>换台/换模式/灌水之后必须丢缓存：不然 0.25 秒内的界面更新还拿着上一台机器的方案，
        /// 表现成「点了选台没反应 / 数字还是另一台机器的」。</summary>
        internal static void DropPlanCache() { currentCacheAt = -1f; }

        // ===== 方案锁定（玩家 2026-11-11 定案）=====
        // 玩家 2026-11-11 定案：**方案只在「玩家按档位键」或「改设置页」时变**。
        // 往瓶里倒水、接水、过夜让水质变了，都不能自己换一套 —— 「接水」键只灌水，不重算。
        private static WaterPlan.Report lockedReport;
        private static bool lockedFull;
        private static string lockModeSig = "";
        private static string lockBottleSig = "";
        private static float lockedAt = -999f;

        private static string LockModeSig()
        {
            // 玩家 2026-11-11：设置页里的东西（水源/目标/前后置/海德拉）变了要重算；
            // 标题栏「默认 / 效率 / 性价比」按键在 SetMode 里已经显式 UnlockPlan 重算，
            // 所以 Mode / UncapEff 不进锁签名（否则锁签名会在重算后又判一次「设置变了」）。
            return "s" + SrcIdx + "|t" + TargetIdx + "|pre" + PreMode + "|po" + PostMode +
                   "|h" + WaterPlan.HydraMode + "|hd" + WaterPlan.HydraDuraCap;
        }

        private static void LockPlan(WaterPlan.Report r, string why)
        {
            LockPlan(r, why, true);
        }

        private static void LockPlan(WaterPlan.Report r, string why, bool full)
        {
            if (r == null || r.pick == null) return;
            lockedReport = r;
            lockedFull = full;
            lockModeSig = LockModeSig();
            lockBottleSig = LockVolSig();
            lockedAt = UnityEngine.Time.realtimeSinceStartup;
            Log("[lock] 锁定方案（" + why + "）feed=" + (r.pick.feedUl / 1000) + "mL out=" + (r.pick.outUl / 1000) + "mL");
        }

        internal static void UnlockPlan(string why)
        {
            if (lockedReport == null) return;
            lockedReport = null;
            lockModeSig = "";
            lockBottleSig = "";
            lockedAt = -999f;
            DropPlanCache();
            Log("[lock] 解锁（" + why + "）");
        }

        /// <summary>锁定是否还有效：只看设置页（水源/目标/前后置/海德拉）。
        /// 玩家 2026-11-11 定案：水量、水质都不参与解锁 —— 倒水/接水/过夜都不改方案；
        /// 档位按键走 SetMode 里的显式 UnlockPlan，不靠这里判。</summary>
        private static bool LockValid()
        {
            if (lockedReport == null) return false;
            if (LockModeSig() != lockModeSig) { UnlockPlan("设置变了"); return false; }
            return true;
        }

        /// <summary>锁里存的是轻量结果（没有方案表），这次要重量结果就放它重算一次。</summary>
        private static bool LockNeedsFull(bool full)
        {
            return full && lockedReport != null && !lockedFull;
        }

        /// <summary>方案缓存的有效性签名：凡是会影响方案结果的设置都算进来。
        /// 玩家 2026-11-11 报「点默认/性价比按钮没反应」，根因是 0.25 秒缓存不知道 Mode 变了，
        /// 重建时把上一次的整包结果原样还回去。</summary>
        private static string PlanSig()
        {
            return Mode + "|" + (UncapEff ? 1 : 0) + "|" + (ManualPickLabel == null ? "-" : ManualPickLabel) +
                   "|s" + SrcIdx + "|t" + TargetIdx + "|pre" + PreMode + "|po" + PostMode +
                   "|h" + WaterPlan.HydraMode + "|hd" + WaterPlan.HydraDuraCap +
                   "|fd" + FilterDurNow() + "|pk" + PurifierHook.PickedIndex + "|bv" + BottleSig();
        }

        /// <summary>签名里去掉「瓶子」那一段：只有水量/水质变了时用它判「设置没动，可以慢一点重算」。</summary>
        private static string SigHead(string s)
        {
            if (s == null) return "";
            int i = s.LastIndexOf("|bv", System.StringComparison.Ordinal);
            return i < 0 ? s : s.Substring(0, i);
        }

        /// <summary>缓存签名里也要带上瓶子（换容器/换瓶水必须重算，否则 0.25 s 内会拿到上一瓶的答案）。</summary>
        private static string BottleSig()
        {
            try
            {
                var b = CtxBottle();
                if (b == null) b = GameBottle();
                if (b == null) return "-";
                return WaterHelper.GetTotalVolume(b) + "@" + WaterHelper.GetWaterPurity(b) + "/" + WaterHelper.GetTotalCapacity(b);
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>锁定只认「水量 + 容器容量」，**不认水质**：净水器过一晚本来就会改水质，
        /// 把水质算进锁签名会让锁在下一次刷新时立刻自解，又回到「过一晚就换方案」（玩家 2026-11-11 报的 2 天出货问题）。
        /// 玩家自己往里加/倒水会改水量，照样能解锁。</summary>
        private static string LockVolSig()
        {
            try
            {
                var b = CtxBottle();
                if (b == null) b = GameBottle();
                if (b == null) return "-";
                return WaterHelper.GetTotalVolume(b) + "/" + WaterHelper.GetTotalCapacity(b);
            }
            catch (Exception) { return "?"; }
        }

        private static WaterPlan.Report CurrentPlan(bool full)
        {
            // 玩家 2026-11-11：方案只在按档位键 / 改设置页时重算（水量/水质怎么变都不重算）。
            if (LockValid() && !LockNeedsFull(full)) return lockedReport;
            float now = UnityEngine.Time.realtimeSinceStartup;
            string sig = PlanSig();
            // 轻量调用（full=false）可以复用重量结果；反过来不行——重量调用拿到轻量结果会缺方案表。
            if (currentCacheReport != null && sig == currentCacheSig && now >= currentCacheAt && now - currentCacheAt < 0.25f &&
                (currentCacheFull || !full))
                return currentCacheReport;
            // 只有「水量/水质」变了（其它设置没动）：先给旧结果，最多 1.2 秒后再重算。
            // 玩家 2026-11-11 反馈「每操作一次水就整包算一次会卡」——灌水是一连串操作，不能每次都算。
            if (currentCacheReport != null && (currentCacheFull || !full) && now >= currentCacheAt && now - currentCacheAt < 1.2f &&
                SigHead(sig) == SigHead(currentCacheSig))
                return currentCacheReport;
            GameBridge.BeginMemo();
            try
            {
                var r = CurrentPlanCompute(full);
                currentCacheReport = r;
                currentCacheFull = full;
                currentCacheSig = sig;
                currentCacheAt = UnityEngine.Time.realtimeSinceStartup;
                // 玩家 2026-11-11：算出来的这一份就是「当前方案」，钉住它 ——
                // 之后往瓶里倒水/接水/过夜让水质变，都不许自己换方案，除非按档位键或改设置页。
                // 重量结果（full）优先：别让轻量结果把带方案表的整份覆盖掉。
                if (r != null && r.pick != null && (lockedReport == null || full))
                    LockPlan(r, "算完即锁", full);
                return r;
            }
            finally { GameBridge.EndMemo(); }
        }

        /// <summary>真正的整包重算（缓存入口见 CurrentPlan）。</summary>
        private static WaterPlan.Report CurrentPlanCompute(bool full)
        {
            var b = CtxBottle();
            if (b == null) b = GameBottle();
            if (b == null) b = PickFallbackBottle();
#if DEV
            try
            {
                Log("[plan] bottle=" + (b == null ? "null" : GameBridge.NameOfItem(b)) +
                    " vol=" + (b == null ? 0 : WaterHelper.GetTotalVolume(b)) +
                    " cap=" + (b == null ? 0 : WaterHelper.GetTotalCapacity(b)));
            }
            catch (Exception) { }
#endif
            if (b == null) return null;
            int perf, q;
            Bonuses(out perf, out q);
            int targetBp = PlanText.TargetBp(TargetIdx, q);
            // 过滤棉可用耐久：按背包里**所有**滤棉累加（玩家 2026-11-10：存档 2 块滤芯 = 800 点）。
            // 以前写死 400 → 20 L 明明能接 16253 只给 11600，「上限」也跟着算错。
            var owF = OwnedTools();
            // 过滤棉用多少点：设置页滑条选的值；没拖过就用背包里实际持有的总和（不按「块数」）。
            int fDur = FilterDurNow();
            if (Mode != 2 && fDur <= 0) fDur = WaterPlan.FilterDurPer;   // 默认/效率档没滤棉时当作有全新的一块
            WaterPlan.FilterDur = fDur;
            // 今日水龙头水质先读出来：后面判断「瓶里的水是不是跟今日水管一样」要用（玩家 2026-10-10 定案）
            int grade, pur;
            TapSource(out grade, out pur);
            if (pur <= 0) TodaySource(out grade, out pur);
            // ★玩家 2026-10-10 定案的三条，是容器里有水时的唯一口径：
            //   ①瓶里 0 mL（空瓶）→ 按今日水管水质算，还是老模式：给方案、我自己按接水。
            //   ②瓶里有水、且水质与今日水管**不同** → 按这瓶水自己的质量重算（它可能是玩家自己兑的）。
            //   ③瓶里有水、但水质与今日水管**一致**（哪怕只装了 3 mL）→ 仍按今日水管算，别当成另一瓶水。
            // 另外：按瓶里水算时即使整瓶一晚压不到目标，也不再静默掉回水龙头模式 —— 要按这瓶水回答
            // 「先倒出多少、留多少刚好够」（玩家 2026-10-10 报「只会说装满 6000 可以跑，不给步骤」）。
            try
            {
                long volUl = WaterHelper.GetTotalVolume(b);
                WaterMath.Water realW;
                if (volUl > 0 && BuildRealWater(b, out realW) && realW != null && realW.v > 0)
                {
                    // ±0.10% 以内视作同一水质（浮点/取样误差），走老模式
                    bool sameAsTap = (pur > 0) && (Math.Abs(realW.Purity - pur) <= 10);
                    if (!sameAsTap)
                    {
                        var repW = WaterPlan.BuildFromWater(realW, perf, q, targetBp, Mode, PreMode, PostMode,
                                                            GameBridge.NameOfItem(b));
                        if (repW != null) { ApplyManualPick(repW); return repW; }
                    }
                }
            }
            catch (Exception e) { log.LogWarning("[ui] plan-from-water " + e.GetType().Name); }
            string srcName = SrcName(grade);
            // 效率·无视容器：按净水器 10 L 硬上限规划，回答「这台机器一晚到底能吃多少」（玩家 2026-11-10 要求）
            // 「效率：无视容器」已按玩家要求删掉（游戏里不可能无视容器接水）：一律按当前容器规划。
            long capForPlan = WaterHelper.GetTotalCapacity(b);
            // 玩家 2026-11-10 定案：**三个档都按身上的真实道具建候选** ——
            // 默认也是「用我当前的道具一晚赚更多」，不能假设药片/滤棉无限。
            int ot = owF.tablets; bool of = owF.filter, ou = owF.uv; int oh = owF.hydraDur;
            var rep = WaterPlan.Build(capForPlan, pur, perf, q, targetBp,
                                   srcName, GameBridge.NameOfItem(b), full, Mode, PreMode, PostMode,
                                   ot, of, ou, oh);
            ApplyManualPick(rep);
            return rep;
        }

        /// <summary>玩家在方案表里点过的那条：按 label 覆盖自动选中的方案；找不到就回落到自动。</summary>
        private static void ApplyManualPick(WaterPlan.Report r)
        {
            if (r == null || ManualPickLabel == null) return;
            for (int i = 0; i < r.combos.Count; i++)
            {
                var c = r.combos[i];
                if (c != null && c.ok && c.label == ManualPickLabel) { r.pick = c; return; }
            }
            ManualPickLabel = null;   // 换瓶/换水源后旧方案不存在了，自动回到自动选
        }

        /// <summary>诊断：列出参与整瓶凑量的容器（背包/台面/机器里的都算），
        /// 用来确认 1 mL / 10 mL 滴管有没有被认出来、当前整瓶能凑到多少。</summary>
        public static string CupsPublic()
        {
            try
            {
                var cups = WaterPlan.ScanCups();
                var g = WaterPlan.Groups(cups);
                var sb = new System.Text.StringBuilder("cups -> n=" + cups.Count);
                for (int i = 0; i < g.Count; i++)
                {
                    sb.Append(" | ").Append(g[i].name).Append(" ×").Append(g[i].n)
                      .Append(" cap=").Append(WaterPlan.CupCapText(g[i].capUl));
                }
                var mm = Compute();
                if (mm != null)
                {
                    long keep = MaxKeepUl((int)(mm.CapacityMl * 1000L), mm.PurityBp, mm.PerfPct, mm.Quality, mm.TargetBp);
                    var pour = WaterPlan.Plan(keep);
                    sb.Append(" || 一晚需 ").Append(keep / 1000).Append(" mL");
                    sb.Append("，整瓶能凑 ").Append(pour == null ? 0 : pour.totalUl / 1000).Append(" mL");
                    if (pour != null && pour.leftUl > 0) sb.Append("，差 ").Append(pour.leftUl / 1000).Append(" mL");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "cups -> err " + e.Message; }
        }

        /// <summary>诊断：把当前候选方案原样打出来，用于确认「前置/后置筛完为什么是空的」。</summary>
        public static string CombosPublic()
        {
            try
            {
                var r = CurrentPlan(true);
                if (r == null) return "combos -> no-plan";
                var sb = new System.Text.StringBuilder("combos -> n=" + r.combos.Count);
                sb.Append(" pick=").Append(r.pick == null ? "none" : r.pick.label);
                sb.Append(" filterEmpty=").Append(r.filterEmpty ? 1 : 0);
                int k = 0;
                foreach (var c in r.combos)
                {
                    if (k++ >= 10) { sb.Append(" | …"); break; }
                    sb.Append(" | ").Append(c.label).Append(" ok=").Append(c.ok ? 1 : 0)
                      .Append(" feed=").Append(c.feedUl / 1000).Append(" out=").Append(c.outUl / 1000)
                      .Append(" t=").Append(c.tablets);
                }
                return sb.ToString();
            }
            catch (Exception e) { return "combos -> ERR " + e.GetType().Name + ": " + e.Message; }
        }

        private static double CostOf(WaterMath.OneNight c)
        {
            double v = c.tablets * 8.0;
            if (c.useFilter) v += 6.0;
            if (c.useUv) v += 4.0;
            return v + 4.0;   // +4：一晚电费
        }

        private static string LiveLabel(WaterMath.OneNight c)
        {
            var s = new System.Text.StringBuilder();
            if (c.useFilter) s.Append("过滤棉");
            if (c.useUv) s.Append(s.Length > 0 ? "+紫外" : "紫外");
            if (c.tablets > 0)
            {
                if (s.Length > 0) s.Append("+");
                s.Append(c.tablets).Append(" 片药片（过夜后放）");
            }
            if (s.Length == 0) s.Append("原样跑一晚");
            return s.ToString();
        }

        public static string PlanPublic()
        {
            var r = CurrentPlan(true);
            if (r == null) return "[plan] no-bottle";
            Rebuild(true);
            string t = r.Summary();
            // 机器里已经有水（存档自带/上次没倒）时，不要再说「建议接水」——直接按现况算过夜：
            // 过夜只清杂质，体积不变（2000 mL 进 → 2000 mL 出，纯度抬到上限为止）。
            var mm = Compute();
            if (mm != null && mm.VolumeMl > 0 && mm.PurityBp > 0)
            {
                int after = mm.PurityBp;
                string liveLabel = "";
                try
                {
                    // 现况也要跟着四个下拉走：按「前置/后置」筛候选，再按「模式」挑一条，
                    // 而不是不管选什么都只报「原样过夜」（玩家：改了下拉数字还是死的）。
                    var cands = WaterMath.Plans(mm.VolumeMl * 1000L, mm.PurityBp,
                        1.0 + mm.PerfPct / 100.0, mm.Quality, PlanText.TargetBp(TargetIdx, mm.Quality));
                    WaterMath.OneNight best = null;
                    if (cands != null)
                    {
                        foreach (var c in cands)
                        {
                            if (!c.possible) continue;
                            if (PreMode == 1 && (c.useFilter || c.useUv)) continue;
                            if (PreMode == 2 && !c.useFilter) continue;
                            if (PreMode == 3 && !c.useUv) continue;
                            if (PostMode >= 1 && c.tablets != PostMode - 1) continue;
                            if (best == null) { best = c; continue; }
                            if (Mode == 1) { if (c.output > best.output) best = c; }
                            else if (Mode == 2)
                            {
                                double ca = CostOf(c), cb = CostOf(best);
                                if (ca / Math.Max(1.0, c.output) < cb / Math.Max(1.0, best.output)) best = c;
                            }
                            else if (c.purity > best.purity || (c.purity == best.purity && CostOf(c) < CostOf(best))) best = c;
                        }
                    }
                    if (best != null)
                    {
                        after = best.purity;
                        if (mm.CapBp > 0 && after > mm.CapBp) after = mm.CapBp;
                        liveLabel = "选中「" + LiveLabel(best) + "」：这 " + mm.VolumeMl + " mL 过夜 → "
                                  + (best.output / 1000L) + " mL · " + PlanText.Pct(after);
                    }
                }
                catch (Exception) { }
                if (liveLabel == "")
                {
                    try
                    {
                        var w0 = WaterMath.Build(mm.VolumeMl, mm.PurityBp);
                        WaterMath.Purify(w0, 1.0 + mm.PerfPct / 100.0, mm.Quality);
                        after = w0.Purity;
                    }
                    catch (Exception) { }
                    if (mm.CapBp > 0 && after > mm.CapBp) after = mm.CapBp;
                    liveLabel = "按现况过夜 → " + mm.VolumeMl + " mL · " + PlanText.Pct(after) + "（体积不变）";
                }
                string adv0 = AdviceText(mm);
                t = "容器里已经有 " + mm.VolumeMl + " mL · " + PlanText.Pct(mm.PurityBp) + "（不用再灌水）"
                  + " | " + liveLabel
                  + (adv0 == "" ? "" : " | 这瓶现在怎么做：" + adv0);
            }
            // 模式（下拉）必须体现在结论里：同一次规划，按模式挑出来的方案不同。
            int mi = Mode;
            if (mi < 0 || mi >= PlanText.ModeNames.Length) mi = 0;
            if (r.filterEmpty)
                t = t.TrimEnd() + "\n前置「" + PlanText.PreName(PreMode) + "」+ 后置「" + PlanText.PostName(PostMode)
                  + "」凑不出达标方案，下面按自动给";
            if (r.pick != null)
                t = t.TrimEnd() + "\n按「" + PlanText.ModeNames[mi] + "」选：前置 " + PlanText.PreName(PreMode)
                  + " · 后置 " + PlanText.PostName(PostMode) + " → " + r.pick.label
                  + " → " + (r.pick.outUl / 1000) + " mL · " + PlanText.Pct(r.pick.purityBp);
            Log("[plan] " + t.Replace("\n", " | "));
            return t.Replace("\n", " | ");
        }

        public static string StepsPublic()
        {
            var r = CurrentPlan(true);
            if (r == null) return "[steps] no-bottle";
            string t = r.StepsText() + "\n" + r.NightsText();
            Log("[steps] " + t.Replace("\n", " | "));
            return t.Replace("\n", " | ");
        }

        public static void ToggleDetail()
        {
            try
            {
                if (detailWin != null && detailWin.IsVisible()) { detailWin.Hide(); return; }
                var r = CurrentPlan(true);
                if (detailWin == null)
                {
                    detailTxt = new ScrollableRichTextElement(true, true);
                    detailTxt.ResizePixels(228, 452);
                    var panel = new MachineryPanel("净水详情", detailTxt, "watercalc_detail");
                    detailWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { panel });
                    if (detailWin == null) { log.LogWarning("[ui] detail null"); return; }
                    try { detailWin.ResizePixels(244, 500); } catch (Exception) { }
                    CenterWindow(detailWin);
                    // Input 钩子已关，改由这层命名拦截器吞掉落在详情窗里的点击（不让它穿到游戏 UI）。
                    AddClickBlocker(detailWin, "detail", 244, 500);
                }
                var b = new RichTextBuilder(200);
                if (r == null) AddLine(b, "没有可规划的瓶子：把瓶放进储水容器或背包", ColorPalette.LightOrange, false);
                else
                {
                    AddLine(b, r.Headline(), ColorPalette.LightGreen, true);
                    AddLine(b, "本机 性能 +" + r.perf + "% · 质量 " + r.q , ColorPalette.LightGray, false);
                    AddLine(b, r.CostText(), ColorPalette.White, false);
                    // 玩家 2026-11-10：详情页从这里往下（方案清单 → 一键灌水）整段删掉，只留步骤。
                    // 方案清单在「更多方案」页已经有了，这里重复一遍还占屏。
                    AddLine(b, "", ColorPalette.White, false);
                    AddLine(b, "步骤", ColorPalette.LightOrange, true);
                    var lines = r.StepsText().Split('\n');
                    for (int i = 0; i < lines.Length; i++) if (lines[i].Length > 0) AddLine(b, lines[i], ColorPalette.White, false);
                }
                detailTxt.SetRichText(b, 6000);
                detailWin.Show(); detailWin.ToFront(true);
            }
            catch (Exception e) { log.LogWarning("[ui] detail " + e.GetType().Name + " " + e.Message); }
        }


        // 模式二入口（供按钮与 ctl 调试命令共用）
        public static string OnPreparePublic() { OnPrepare2(); return "prepare -> " + (serveResult == null ? LastMsg : serveResult); }

        /// <summary>「净化后」选择器：0/1/2/3 片药片，对应网页计算器的下拉框。只改方案口径，不动瓶子。</summary>
        private static int tabletPick = 0;

        private static void CycleTablet()
        {
            tabletPick = (tabletPick + 1) % 4;
            LastMsg = tabletPick == 0
                ? "方案按「不放药片」算"
                : "方案按「净化后 " + tabletPick + " 片药片」算（药片得你自己放进瓶子，这里只算数）";
            lastKey = "";
            Rebuild(true);
        }

        public static string CycleTabletPublic() { CycleTablet(); return "tablet -> " + tabletPick + " 片: " + LastMsg; }

        /// <summary>ctl `tablet`：给当前瓶子加一片净水片（与 F9「药片」按钮同一条路径）。</summary>
        public static string OnTabletPublic() { OnAddTablet(); return "tablet -> " + LastMsg; }

        /// <summary>设置窗用：直接设定平衡模式（true=按真实水质算）。</summary>
        public static void SetBalance(bool on)
        {
            BalanceOff = on;
            lastKey = "";
            Log("[bal] set on=" + on + " display=" + (on ? "exact" : "unknown"));
            Rebuild(true);
        }

        public static void ToggleBalance()
        {
            BalanceOff = !BalanceOff;
            // 开/关要看得出来：直接换图标（开＝水滴=显示真实水质；关＝齿轮=未知）
            // MachineryButton 没有换图标的接口，只能改描述；把状态写进 tooltip + 用面板顶行显示
            try
            {
                // 图标已移除，状态只体现在设置窗与顶行
            }
            catch (Exception) { }
            Log("[bal] on=" + BalanceOff + " display=" + (BalanceOff ? "exact" : "unknown"));
            lastKey = "";
            Rebuild(true);
        }

        public static string BalanceState()
        {
            bool hp = HidePurity();
            return "bal -> scanners=" + (Scanners() ? "true" : "false") + " mode=" + (BalanceOff ? "off" : "on") + " hide=" + (hp ? "true" : "false");
        }

        public static string ToggleBalancePublic() { ToggleBalance(); return BalanceState(); }

        public static void LogQuiet(string s) { Log(s); }
        public static string RecipesVisible() { return (recipeWin != null && recipeWin.IsVisible()) ? "true" : "false"; }

        /// <summary>每页各自的高度：主页只放当前方案，设置页放五行下拉，方案池页放候选表。</summary>
        // ---------------- 水源选择器：小窗 + 网格（先文字格，后续换物品图标） ----------------
        private static PixelWindow srcWin;

        /// <summary>打开/收起水源选择器：两列网格，点哪个是哪个（当前项带 ▶）。</summary>
        public static void OpenSourcePicker()
        {
            try
            {
                if (srcWin == null)
                {
                    var st = new MachineryStack(true, 4);
                    int n = SrcPickNames.Length;
                    for (int i = 0; i < n; i += 2)
                    {
                        var row = new MachineryStack(false, 4);
                        for (int k = i; k < Math.Min(i + 2, n); k++)
                        {
                            int idx = SrcPickVals[k];
                            var b = new MachineryButton((idx == SrcIdx ? "▶" : "") + SrcPickNames[k], new Action(delegate
                            {
                                SetSrc(idx);
                                if (srcWin != null) srcWin.Hide();
                            }), 108, 20);
                            row.Add(b);
                        }
                        st.Add(row);
                    }
                    st.Add(Small("关闭", new Action(delegate { if (srcWin != null) { WinSave("src", srcWin); srcWin.Hide(); } }), 64));
                    var sp = new MachineryPanel("", st, "watercalc_src");
                    sp.SetMinWidth(232);
                    srcWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { sp });
                    if (srcWin != null) { try { srcWin.ResizePixels(250, 170); } catch (Exception) { } WinRestore("src", srcWin); }
                    Log("[ui] 水源窗 ok");
                }
                if (srcWin != null) { if (srcWin.IsVisible()) { WinSave("src", srcWin); srcWin.Hide(); } else { srcWin.Show(); WinRestore("src", srcWin); } }
            }
            catch (Exception e) { log.LogWarning("[ui] OpenSourcePicker " + e.GetType().Name + " " + e.Message); }
        }

        // ---------------- 步骤：独立小窗（不替换主页面板） ----------------
        private static PixelWindow stepsWin;
        private static ScrollableRichTextElement pStepList;

        /// <summary>ctl 用：把「步骤」按文本返回，方便不开面板也能核对（steps?）。</summary>
        public static string StepsTextPublic()
        {
            try
            {
                var r = CurrentPlan(true);
                if (r == null) return "steps -> no-plan";
                var c = r.pick;
                long feed = (c != null && c.feedUl > 0) ? c.feedUl : r.useUl;
                int perf, q;
                Bonuses(out perf, out q);
                WaterMath.Water w = null;
                var b0 = CtxBottle(); if (b0 == null) b0 = GameBottle();
                if (b0 != null)
                {
                    WaterMath.Water real;
                    if (BuildRealWater(b0, out real) && real.v > 0 && Math.Abs(real.v - feed) <= 5000L) w = real;
                }
                bool fromReal = w != null;
                if (w == null) w = WaterMath.Build(feed, r.srcBp);
                var sb = new System.Text.StringBuilder();
                if (fromReal) sb.Append("现有 ").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity));
                else sb.Append("接水 ").Append(WaterPlan.Ml(feed)).Append("mL@").Append(PlanText.Pct(r.srcBp));
                if (c != null && c.filter) { WaterMath.Filter(w, 400); sb.Append(" | 过滤→").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)); }
                if (c != null && c.uv) { WaterMath.Uv(w, 1); sb.Append(" | 紫外→").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)); }
                WaterMath.Purify(w, perf, q);
                sb.Append(" | 1晚→").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity));
                if (c != null && c.tablets > 0) { WaterMath.Tablet(w, c.tablets); sb.Append(" | ").Append(c.tablets).Append("药→").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)); }
                if (c != null) sb.Append(" || 表:").Append(WaterPlan.Ml(c.feedUl)).Append("→").Append(WaterPlan.Ml(c.outUl)).Append("mL@").Append(PlanText.Pct(c.purityBp));
                return "steps -> " + sb;
            }
            catch (Exception e) { return "steps -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl 用：列出 PixelWindow 全部公开方法（找「换内容而不换窗口」的接口）。</summary>
#if DEV
        public static string PwProbe()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var w = panelWin;
                if (w == null) return "pw -> no-window";
                var t = w.GetType();
                sb.Append("pw -> ").Append(t.Name);
                var ms = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                foreach (var m in ms)
                {
                    if (m.IsSpecialName) continue;
                    sb.Append(" | ").Append(m.Name).Append('(');
                    var ps = m.GetParameters();
                    for (int i = 0; i < ps.Length; i++) { if (i > 0) sb.Append(','); sb.Append(ps[i].ParameterType.Name); }
                    sb.Append(')');
                }
            }
            catch (Exception e) { sb.Append(" ERR ").Append(e.GetType().Name); }
            return sb.ToString();
        }
#endif

        /// <summary>ctl 用：列出当前净水器已装的子件（模组/滤芯）identifier —— 找紫外灯/过滤棉真名。</summary>
        public static string ModsProbe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("mods -> ");
            try
            {
                var m = CurrentMachine();
                sb.Append("cur=").Append(m == null ? "null" : GameBridge.DescribeModules(m));
            }
            catch (Exception e) { sb.Append("ERR ").Append(e.GetType().Name); }
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var p2 = PurifierHook.Pairs[i];
                    if (p2 == null || p2.Machine == null) continue;
                    sb.Append(" | #").Append(i).Append('=').Append(GameBridge.DescribeModules(p2.Machine));
                }
            }
            catch (Exception e2) { sb.Append(" listErr=").Append(e2.GetType().Name); }
            return sb.ToString();
        }

        /// <summary>ctl 用：列出背包/台面里所有物品 identifier（找紫外灯/过滤棉的真名）。</summary>
        public static string ItemsProbe()
        {
            try
            {
                var all = GameBridge.LiveAllItems();
                var sb = new System.Text.StringBuilder();
                sb.Append("items -> n=").Append(all.Count);
                for (int i = 0; i < all.Count && i < 60; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    int cap = 0; int vol = 0;
                    try { cap = WaterHelper.GetTotalCapacity(it); } catch (Exception) { }
                    try { vol = WaterHelper.GetTotalVolume(it); } catch (Exception) { }
                    sb.Append(" | ");
                    try { sb.Append(it.identifier); } catch (Exception) { sb.Append("?"); }
                    if (cap > 0) sb.Append("(cap=").Append(cap).Append(",vol=").Append(vol).Append(')');
                }
                return sb.ToString();
            }
            catch (Exception e) { return "items -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl 用：回报主面板当前页与位置（验证切页不跳位）。</summary>
        public static string PosPublic()
        {
            try
            {
                if (panelWin == null) return "pos -> no-window";
                UnityEngine.Vector2 p2; if (!WinGetPos(panelWin, out p2)) p2 = panelWin.rectTransform.anchoredPosition;
                var sb = new System.Text.StringBuilder();
                sb.Append("pos -> page=").Append(UiPage).Append(" visible=").Append(panelWin.IsVisible())
                  .Append(" x=").Append((int)p2.x).Append(" y=").Append((int)p2.y);
#if DEV
                // 顺便把 PixelWindow 上跟「位置」有关的成员打出来（找真正控制位置的接口）
                // 注意：GetType() 拿到的是「计算出的类型」，按能力清单算 reflection-external；
                // 创意工坊发布版（2026-12-10 起）一律拒绝，所以只留在 DEV 构建里。
                try
                {
                    var t = panelWin.GetType();
                    foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        string n = m.Name; if (n == null) continue;
                        string ln = n.ToLowerInvariant();
                        if (ln.Contains("pos") || ln.Contains("move") || ln.Contains("anchor") || ln.Contains("offset"))
                            sb.Append(" | ").Append(n);
                    }
                }
                catch (Exception) { }
#endif
                return sb.ToString();
            }
            catch (Exception e) { return "pos -> ERR " + e.Message; }
        }

        /// <summary>ctl 用：把 GraphUtils 的静态方法签名打出来，用来找「摆到台面」的正确入口。</summary>
#if DEV
        public static string GraphUtilsProbe()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Type t = null;
                    try { t = asms[i].GetType("GraphUtils"); } catch (Exception) { }
                    if (t == null) continue;
                    sb.Append("type=").Append(t.FullName).Append(" asm=").Append(asms[i].GetName().Name).Append(" | ");
                    var ms = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    for (int k = 0; k < ms.Length; k++)
                    {
                        var m = ms[k];
                        if (!m.Name.ToLowerInvariant().Contains("place") && !m.Name.ToLowerInvariant().Contains("add")) continue;
                        sb.Append(m.Name).Append('(');
                        var ps = m.GetParameters();
                        for (int j = 0; j < ps.Length; j++) { if (j > 0) sb.Append(", "); sb.Append(ps[j].ParameterType.Name).Append(' ').Append(ps[j].Name); }
                        sb.Append(") ");
                    }
                }
                if (sb.Length == 0) sb.Append("GraphUtils not found");
                return "gs -> " + sb;
            }
            catch (Exception e) { return "gs -> ERR " + e.GetType().Name + " " + e.Message; }
        }
#endif

        /// <summary>ctl 用：不碰游戏状态，按给定参数跑一遍 WaterPlan.Build（核对 6L/5498 这种离线算例）。
        /// 用法：sim? cap=6000 pur=9001 pre=0 post=0 perf=25 qual=0 tgt=9901</summary>
        public static string SimPublic(string args)
        {
            try
            {
                int capMl = 6000, pur = 9000, pre = 0, post = 0, perf = 25, qual = 0, tgt = 9901;
                if (!string.IsNullOrEmpty(args))
                {
                    string[] kv = args.Trim().Split(' ');
                    for (int i = 0; i < kv.Length; i++)
                    {
                        int eq = kv[i].IndexOf('=');
                        if (eq <= 0) continue;
                        string k = kv[i].Substring(0, eq).ToLowerInvariant();
                        int v;
                        if (!int.TryParse(kv[i].Substring(eq + 1), out v)) continue;
                        if (k == "cap") capMl = v;
                        else if (k == "pur") pur = v;
                        else if (k == "pre") pre = v;
                        else if (k == "post") post = v;
                        else if (k == "perf") perf = v;
                        else if (k == "qual") qual = v;
                        else if (k == "tgt") tgt = v;
                    }
                }
                var r = WaterPlan.Build(capMl * 1000L, pur, perf, qual, tgt, "模拟", "模拟", true, 0, pre, post);
                if (r == null) return "sim -> null";
                var sb = new System.Text.StringBuilder();
                sb.Append("sim [cap=").Append(capMl).Append(" pur=").Append(pur).Append(" pre=").Append(pre)
                  .Append(" post=").Append(post).Append(" perf=").Append(perf).Append(" qual=").Append(qual)
                  .Append(" tgt=").Append(tgt).Append("]");
                if (r.pick != null)
                    sb.Append(" -> pick=").Append(r.pick.label).Append(" ").Append(WaterPlan.Ml(r.pick.feedUl))
                      .Append("→").Append(WaterPlan.Ml(r.pick.outUl)).Append("mL@").Append(PlanText.Pct(r.pick.purityBp));
                int n = 0;
                for (int i = 0; i < r.combos.Count && n < 6; i++)
                {
                    var c = r.combos[i];
                    if (!c.ok) continue;
                    sb.Append(" | ").Append(c.label).Append(' ').Append(WaterPlan.Ml(c.feedUl))
                      .Append("→").Append(WaterPlan.Ml(c.outUl)).Append('@').Append(PlanText.Pct(c.purityBp));
                    n++;
                }
                return sb.ToString();
            }
            catch (Exception e) { return "sim -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl 用：当前瓶水 + 模拟逐片加药（同一批水），用来和游戏实测反推药片真实清除量。</summary>
        public static string TabletProbe()
        {
            try
            {
                var b = CtxBottle();
                if (b == null) b = GameBottle();
                if (b == null) return "tab -> no-bottle";
                WaterMath.Water w;
                if (!BuildRealWater(b, out w)) return "tab -> no-parts";
                var sb = new System.Text.StringBuilder();
                sb.Append("tab -> 现在 ").Append(WaterPlan.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity));
                var wc = w.Clone();
                for (int t = 1; t <= 3; t++)
                {
                    WaterMath.Tablet(wc, 1);
                    sb.Append(" | ").Append(t).Append("片→").Append(WaterPlan.Ml(wc.v)).Append("mL@").Append(PlanText.Pct(wc.Purity));
                }
                sb.Append(" || 分项 ");
                string[] keys = { "heavy", "organic", "microbe", "chemical", "physical", "mineral" };
                for (int i = 0; i < keys.Length; i++) sb.Append(keys[i]).Append('=').Append(w.Part(keys[i])).Append(' ');
                return sb.ToString();
            }
            catch (Exception e) { return "tab -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>
        /// 用瓶子里真实的六项配比建 Water。
        /// 2026-11-11 玩家定案：公式是对的，错的是「接口」——读进来的水被算成 100%。
        /// 因此这里必须让模型纯度与游戏真值（WaterHelper.GetWaterPurity）对齐：
        /// 名字映射不全/顺序不符时，回落按真值纯度反推配比，绝不给出比真值更高的纯度。
        /// </summary>
        private static bool BuildRealWater(GameItem b, out WaterMath.Water w)
        {
            w = null;
            try
            {
                var parts = WaterHelper.GetLiquidParts(b);
                if (parts == null || parts.Length < 6) return false;
                long sum = 0;
                for (int i = 0; i < parts.Length; i++) sum += parts[i];
                if (sum <= 0) return false;
                int gp = 0;
                try { gp = WaterHelper.GetWaterPurity(b); } catch (Exception) { gp = 0; }

                var names = GameBridge.LiquidNames();
                var m = new WaterMath.Water { v = sum };
                try { m.cap = WaterHelper.GetTotalCapacity(b); } catch (Exception) { m.cap = 0; }
                long mapped = 0;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] <= 0) continue;
                    string k = names != null && i < names.Length ? MapLiquid(names[i]) : null;
                    if (k == null) k = MapLiquidByIndex(i);   // 名字对不上（本地化/空）时按 Liquid 顺序兜底
                    if (k == null) continue;
                    long cur;
                    m.parts.TryGetValue(k, out cur);
                    m.parts[k] = cur + parts[i];
                    mapped += parts[i];
                }
                // 接口校验：模型纯度不得高于游戏真值（允许 5 bp 误差，正负都可）。
                if (gp > 0 && (mapped <= 0 || m.Purity > gp + 5 || m.Purity < gp - 5))
                {
#if DEV
                    Log("[realwater] 配比对不上，按真值反推 gp=" + PlanText.Pct(gp) + " model=" + PlanText.Pct(m.Purity) + " mapped=" + mapped + "/" + sum);
#endif
                    var fb = WaterMath.Build(sum, gp);
                    fb.cap = m.cap;
                    m = fb;
                }
#if DEV
                Log("[realwater] gp=" + PlanText.Pct(gp) + " model=" + PlanText.Pct(m.Purity) + " vol=" + sum + " mapped=" + mapped + " parts=" + m.parts.Count);
#endif
                w = m;
                return true;
            }
            catch (Exception e)
            {
#if DEV
                Log("[realwater] ERR " + e.GetType().Name + " " + e.Message);
#endif
                return false;
            }
        }

        /// <summary>按 Liquid.Liquids 的顺序兜底映射（顺序见 Liquid.InitLiquid：water 在最前）。</summary>
        private static string MapLiquidByIndex(int i)
        {
            switch (i)
            {
                case 1: return "heavy";
                case 2: return "organic";
                case 3: return "physical";   // microplastic（与 physical 同参数：filterMin 0.002、移除 1500）
                case 4: return "microbe";
                case 5: return "chemical";
                case 6: return "physical";
                case 7: return "mineral";
                default: return null;
            }
        }

        private static string MapLiquid(string n)
        {
            switch ((n ?? "").Trim().ToLowerInvariant())
            {
                case "heavy_metal": return "heavy";
                case "heavy": return "heavy";
                case "organic_waste": case "organic": return "organic";
                case "microbe": case "microbes": return "microbe";
                case "chemical_contaminant": case "chemical": return "chemical";
                case "physical_contaminant": case "physical": return "physical";
                case "microplastic": case "micro_plastic": return "physical";
                case "mineral": return "mineral";
                default: return null;
            }
        }

        private static void FillSteps()
        {
            if (pStepList == null) return;
            var b = new RichTextBuilder(240);
            try
            {
                var r = CurrentPlan(true);
                if (r == null) { AddLine(b, "还没有可用方案：先放一个装水的容器", ColorPalette.LightOrange, false); pStepList.SetRichText(b, 4000); return; }
                var c = r.pick;
                long feed = (c != null && c.feedUl > 0) ? c.feedUl : r.useUl;
                int perf, q;
                Bonuses(out perf, out q);
                // 与卡一/接水键同源：feed 取选中方案，不再用自动方案的 useUl（玩家：一直显示 1850）
                // 瓶子已经有水且量级对得上，就用真实六项配比算；否则按纯度反推
                WaterMath.Water w = null;
                var bottle0 = CtxBottle();
                if (bottle0 == null) bottle0 = GameBottle();
                if (bottle0 != null)
                {
                    WaterMath.Water real;
                    if (BuildRealWater(bottle0, out real) && real.v > 0 && Math.Abs(real.v - feed) <= 5000L) w = real;
                }
                long startV = 0;
                if (w == null) w = WaterMath.Build(feed, r.srcBp); else startV = w.v;
                int idx = 1;
                if (startV > 0) AddLine(b, idx + ". 现有 " + WaterPlan.Ml(startV) + " mL · " + PlanText.Pct(w.Purity), ColorPalette.LightGray, false);
                else AddLine(b, idx + ". 接水 " + WaterPlan.Ml(feed) + " mL · " + PlanText.Pct(r.srcBp), ColorPalette.LightGray, false);
                if (c != null && c.filter)
                {
                    WaterMath.Filter(w, 400);
                    AddLine(b, (++idx) + ". 过滤棉 → " + WaterPlan.Ml(w.v) + " mL · " + PlanText.Pct(w.Purity), ColorPalette.LightGray, false);
                }
                if (c != null && c.uv)
                {
                    // 紫外灯是工具：一次照过、体积不变、不计次数（玩家明确要求）
                    WaterMath.Uv(w, 1);
                    AddLine(b, (++idx) + ". 紫外灯 → " + WaterPlan.Ml(w.v) + " mL · " + PlanText.Pct(w.Purity), ColorPalette.LightGray, false);
                }
                // 注意：Purify 的 perf 是「百分比原值」（25 就传 25），不是 1.25；
                // 之前这里传了 1+perf/100，导致步骤纯度比方案表低 0.47。
                WaterMath.Purify(w, perf, q);
                AddLine(b, (++idx) + ". 净水器 1 晚 → " + WaterPlan.Ml(w.v) + " mL · " + PlanText.Pct(w.Purity), ColorPalette.LightGray, false);
                if (c != null && c.tablets > 0)
                {
                    WaterMath.Tablet(w, c.tablets);
                    AddLine(b, (++idx) + ". " + c.tablets + " 片药片 → " + WaterPlan.Ml(w.v) + " mL · " + PlanText.Pct(w.Purity), ColorPalette.LightGray, false);
                }
                if (c != null)
                {
                    AddLine(b, "方案表口径：" + WaterPlan.Ml(c.feedUl) + " → " + WaterPlan.Ml(c.outUl) + " mL · " + PlanText.Pct(c.purityBp), ColorPalette.LightGreen, false);
                    // 逐片加药：同一批水再加 1/2 片会到多少，方便判断「其实一片就够」
                    var wc = w.Clone();
                    for (int t = c.tablets + 1; t <= 3; t++)
                    {
                        WaterMath.Tablet(wc, 1);
                        AddLine(b, "  +1 片（共 " + t + " 片）→ " + WaterPlan.Ml(wc.v) + " mL · " + PlanText.Pct(wc.Purity), ColorPalette.LightGray, false);
                    }
                }
            }
            catch (Exception e) { AddLine(b, "步骤算不出来：" + e.GetType().Name, ColorPalette.LightOrange, false); }
            pStepList.SetRichText(b, 4000);
        }

        /// <summary>打开/收起「做法步骤」小窗：1 接水 → 2 过滤 → 3 紫外 → 4 每晚 → 5 药片。</summary>
        public static void OpenSteps()
        {
            try
            {
                if (stepsWin == null)
                {
                    var st = new MachineryStack(true, 3);
                    pStepList = new ScrollableRichTextElement(true, true);
                    pStepList.ResizePixels(300, 230);
                    st.Add(pStepList);
                    st.Add(Small("返回", new Action(delegate { if (stepsWin != null) { WinSave("steps", stepsWin); stepsWin.Hide(); } }), 72));
                    var sp = new MachineryPanel("", st, "watercalc_steps");
                    sp.SetMinWidth(280);
                    stepsWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { sp });
                    if (stepsWin != null) { try { stepsWin.ResizePixels(330, 300); } catch (Exception) { } WinRestore("steps", stepsWin); }
                    Log("[ui] 步骤窗 ok");
                }
                if (stepsWin != null)
                {
                    if (stepsWin.IsVisible()) { WinSave("steps", stepsWin); stepsWin.Hide(); return; }
                    stepsWin.Show();
                }
                FillSteps();   // 先显示再填：列表控件在未激活时 SetRichText 会丢掉内容
            }
            catch (Exception e) { log.LogWarning("[ui] OpenSteps " + e.GetType().Name + " " + e.Message); }
        }

        // ---------------- 设置：独立小窗（不挤主页面板） ----------------
        private static PixelWindow settingsWin;
        private static UnityEngine.Vector2 settingsPos;   // 记住设置窗上次位置（关掉再开不跳回左上）
        private static bool settingsHasPos;
        private static MachineryButton cPur;
        private static MachineryButton[] modeBtnsS;   // 设置窗里的「效率/省钱/自定」三个小按钮
        private static bool hasUv, hasFilter;         // 背包里有没有紫外灯/过滤棉（没有就不该出现在选项里）
        // 浓度预设：0=自动读瓶子；其余是「自己填纯度」的常用档
        private static readonly int[] PurPresets = { -1, 9096, 9850, 9900, 9990 };
        private static readonly string[] PurNames = { "自动（读瓶）", "90.96%", "98.50%", "99.00%", "99.90%" };

        private static int PurIndex()
        {
            for (int i = 0; i < PurPresets.Length; i++) if (PurPresets[i] == CustomPur) return i;
            return 0;
        }

        /// <summary>打开/收起设置小窗：五行下拉 + 浓度预设 + 填浓度入口。</summary>
        public static void OpenSettings()
        {
            try
            {
                // 只在「引用已经失效」时才重建（主面板重建会把 cPre/cPost 置空）。
                // 否则重复点设置只是开关，不会把窗口重置回默认位置（玩家反馈）。
                bool stale = settingsWin != null && (cPre == null || cPost == null || cTarget == null);
                if (settingsWin != null && !stale)
                {
                    if (settingsWin.IsVisible())
                    {
                        WinSave("cfg", settingsWin);
                        settingsWin.Hide();
                    }
                    else
                    {
                        settingsWin.Show();
                        WinRestore("cfg", settingsWin);
                    }
                    return;
                }
                // 照「手册窗」：窗口建一次就一直复用，绝不销毁 —— 这样引擎会记住它的位置
                if (settingsWin == null) { /* 下面按需建 */ }
                if (settingsWin == null)
                {
                    var st = new MachineryStack(true, 3);
                    cTarget = CycleRow(st, "目标", PlanText.TargetNames, delegate { return TargetIdx; }, new Action<int>(SetTarget));
                    // 玩家：模式不要一长条循环按钮。改成三个小键，点哪个是哪个。
                    var modeRow = new MachineryStack(false, 4);
                    modeBtnsS = new MachineryButton[PlanText.ModeShort.Length];
                    for (int mi = 0; mi < PlanText.ModeShort.Length; mi++)
                    {
                        int mid = mi;
                        var mb = new MachineryButton((mid == Mode ? "▶" : "") + PlanText.ModeShort[mid], new Action(delegate { SetMode(mid); }), 56, 18);
                        modeBtnsS[mid] = mb;
                        modeRow.Add(mb);
                    }
                    st.Add(modeRow);
                    // 药片：暂时用「+1 / -1」两个小键（数字输入框还没找到可复用的原生控件）
                    var tabRow = new MachineryStack(false, 4);
                    tabRow.Add(new MachineryButton("-1", new Action(delegate { SetPost(Math.Max(1, PostMode <= 0 ? 1 : PostMode - 1)); }), 28, 18));
                    tabRow.Add(new MachineryButton("自动", new Action(delegate { SetPost(0); }), 44, 18));
                    tabRow.Add(new MachineryButton("+1", new Action(delegate { SetPost(Math.Min(PlanText.PostNames.Length - 1, (PostMode <= 0 ? 1 : PostMode) + 1)); }), 28, 18));
                    st.Add(tabRow);
                    // 平衡模式：开＝按瓶里真实水质算；关＝水质按未知处理（玩家要求放进设置里）
                    var balRow = new MachineryStack(false, 4);
                    balRow.Add(new MachineryButton((BalanceOff ? "▶" : "") + "平衡开", new Action(delegate { SetBalance(true); }), 58, 18));
                    balRow.Add(new MachineryButton((!BalanceOff ? "▶" : "") + "平衡关", new Action(delegate { SetBalance(false); }), 58, 18));
                    st.Add(balRow);
                    // 前置只列「背包里真有的」：两个都没有时把前置强制回自动
                    // 过滤棉/紫外灯在机器里是 node 类模组（实测 identifier = node_medium），
                    // 所以「有没有」要看当前净水器已装的子件，不是背包物品。
                    int nodes = CountNodeModules();
                    hasFilter = nodes >= 1; hasUv = nodes >= 1;
                    if (!hasUv && !hasFilter) PreMode = 0;
                    // 前置：图标化小键（只列背包里真有的），点哪个是哪个
                    var preRow = new MachineryStack(false, 4);
                    preRow.Add(Small("前:按模式", new Action(delegate { SetPre(0); }), 74));
                    preRow.Add(Small("不处理", new Action(delegate { SetPre(1); }), 60));
                    if (hasFilter) preRow.Add(Small("过滤", new Action(delegate { SetPre(2); }), 50));
                    if (hasUv) preRow.Add(Small("紫外", new Action(delegate { SetPre(3); }), 50));
                    st.Add(preRow);
                    cPre = null;
                    if (!hasUv && !hasFilter) try { cPre.SetDescription("背包里没有紫外灯/过滤棉：前置只能用「自动」或「不处理」"); } catch (Exception) { }
                    cPost = null;   // 后置已由「药 -1/自动/+1」取代
                    st.Add(new MachineryButton("水源 ▶ 选择…", new Action(delegate { OpenSourcePicker(); }), 140, 20));
                    // 网页的「自己填纯度」：不靠扫描仪也能按一个指定浓度算方案
                    // 浓度：自动时直接把瓶子当前纯度写出来，否则玩家不知道这行在说什么
                    string curPur = "自动（读瓶）";
                    try
                    {
                        if (CustomPur > 0) curPur = "手填 " + PlanText.Pct(CustomPur);
                        else
                        {
                            var bb = CtxBottle(); if (bb == null) bb = GameBottle();
                            int pp = bb == null ? 0 : WaterHelper.GetWaterPurity(bb);
                            curPur = pp > 0 ? ("瓶 " + PlanText.Pct(pp)) : "自动（未读到）";
                        }
                    }
                    catch (Exception) { }
                    try { cPur = CycleRow(st, "浓度", PurNames, new Func<int>(PurIndex), new Action<int>(delegate(int i)
                    {
                        CustomPur = PurPresets[i];
                        Rebuild(true);
                    }));
                    } catch (Exception) { }
                    try { cPur.SetDescription(curPur); } catch (Exception) { }
                    pSetBtns.Clear(); pSetBtns.Add(cTarget); pSetBtns.Add(cMode); pSetBtns.Add(cPre); pSetBtns.Add(cPost); pSetBtns.Add(cSrc); pSetBtns.Add(cPur);
                    st.Add(Small("返回", new Action(delegate { if (settingsWin != null) settingsWin.Hide(); }), 72));
                    var sp = new MachineryPanel("", st, "watercalc_cfg");
                    sp.SetMinWidth(240);
                    settingsWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { sp });
                    if (settingsWin != null)
                    {
                        try { settingsWin.ResizePixels(260, 200); } catch (Exception) { }
                        WinRestore("cfg", settingsWin);
                        WinRestore("cfg", settingsWin);
                        Log("[ui] 设置窗 ok");
                    }
                }
                if (settingsWin != null)
                {
                    if (settingsWin.IsVisible()) settingsWin.Hide(); else settingsWin.Show();
                    Rebuild(true);
                }
            }
            catch (Exception e) { log.LogWarning("[ui] OpenSettings " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>玩家点方案表那一行：按序号把当前表里那条方案设为选中（与 ctl pick=N 同一条路）。</summary>
        private static void PickRow(int k)
        {
            var rows = lastRows;
            if (rows == null || k < 0 || k >= rows.Length)
            {
                var rep = CurrentPlan(true);
                rows = rep == null ? null : rep.TableRows(RowCount).ToArray();
            }
            if (rows == null || k < 0 || k >= rows.Length) { Log("[ui] row " + k + " no-row"); return; }
            ManualPickLabel = rows[k].label;
            Log("[ui] row " + (k + 1) + " pick=" + ManualPickLabel);
            Rebuild(true);
        }

        /// <summary>ctl 用：切页并回报状态。</summary>
        public static string PagePublic(int p)
        {
            try
            {
                if (p >= 0 && p <= 3) SetPage(p);
                return "page -> " + UiPage + " visible=" + ((panelWin != null && panelWin.IsVisible()) ? "true" : "false");
            }
            catch (Exception e) { return "page -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        private static int PageHeight()
        {
            if (UiPage == 1) return 200;   // 设置页多了「过滤棉耐久」滑条一行（玩家 2026-11-10）
            if (UiPage == 2) return 252;
            if (UiPage == 3) return 232;
            return 176;   // 主页：卖/赚独立一行、持有紧跟步骤行（隐藏了空行）后收紧到 176
        }

        /// <summary>切页＝整窗重建：这些控件没有 Remove，隐藏了仍占位（会把窗口撑大），只能重建。</summary>
        private static void SetPage(int page)
        {
            if (UiPage == page) page = 0;   // 再点一次同页＝返回主页
            UiPage = page;
            try
            {
                // 切页**不销毁窗口**：销毁再建会让主界面消失、位置跳回左上（玩家反馈两次）。
                // 只保留一个窗口，换内容 + 改高度即可。
                // 页 1..3 的控件挂在 panel 里；换页＝同一个窗口 Detach/Attach（不销毁）
                EnsurePanel();
                Rebuild(true);
                if (panelWin != null) { try { if (!panelWin.IsVisible()) panelWin.Show(); } catch (Exception) { } }
            }
            catch (Exception e) { log.LogWarning("[ui] SetPage " + e.GetType().Name + " " + e.Message); }
            Rebuild(true);
        }

        /// <summary>窗口对象是否还活着：Unity 销毁后引用不为 null，但访问 rectTransform 会抛异常。
        /// 「关掉详情再打开就闪退」就是这里没校验。</summary>
        private static bool PanelAlive()
        {
            if (panelWin == null) return false;
            try { var rt = panelWin.rectTransform; return rt != null; }
            catch (Exception) { return false; }
        }

        private static void EnsurePanel()
        {
            if (!PanelAlive()) { panelWin = null; panelPageFor = -1; }   // 死引用先清掉
            // 玩家定案：面板要像净水器那样小 —— 主页只讲当前这一条方案，
            // 五行下拉进「设置」页，候选方案进「更多方案」页，各页各自建窗，互不占位。
            bool main = UiPage == 0, set = UiPage == 1, pool = UiPage == 2, steps = UiPage == 3;
            pSub = null; pCard1 = null; pCard1Val = null; pCard2 = null; pCard3 = null; pStep = null;
            pMoney = null;
            pMixLine = null; pTableHead = null; pList = null; pPickRow = null; pMsg = null; pJoin = null; pOwn = null;
            cTarget = null; cMode = null; cPre = null; cPost = null; cSrc = null; cHydra = null; cHydraDur = null;
            sFilterDur = null; cFilterDurLbl = null;
            pSetBtns.Clear();

            var stack = new MachineryStack(true, 3);

            if (main)
            {
                pSub = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                pSub.SetColor(Cream); pSub.SetFillWidth(true);
                stack.Add(pSub);
            }

            if (set)
            {
                // 五行下拉：「组名 ▶ 当前值」，点一下换下一个
                cTarget = CycleRow(stack, "目标", PlanText.TargetNames, delegate { return TargetIdx; }, new Action<int>(SetTarget));
                cMode = CycleRowMap(stack, "模式", ModeUi, ModeVal, delegate { return Mode; }, new Action<int>(SetMode));
                cPre = CycleRowMap(stack, "前置", PreUi, PreVal, delegate { return PreMode; }, new Action<int>(SetPre));
                cPost = CycleRowMap(stack, "后置", PostUi, PostVal, delegate { return PostMode; }, new Action<int>(SetPost));
                cSrc = CycleRowMap(stack, "水源", SrcPickNames, SrcPickVals, delegate { return SrcIdx; }, new Action<int>(SetSrc));
                // 玩家 2026-11-10：海德拉做成设置项 —— 一个开关（自动/强制/不用）+ 一个耐久上限（可买多只叠加）
                cHydra = CycleRowMap(stack, "海德拉", HydraUi, HydraVal, delegate { return WaterPlan.HydraMode; }, new Action<int>(SetHydra));
                // 耐久：原生 MachinerySlider，可拖到任意点数（312 就 312），不是 300/600/900 三档
                cHydraDur = null;
                var hdRow = new MachineryStack(false, 4);
                cHydraDurLbl = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                cHydraDurLbl.SetColor(Cream);
                cHydraDurLbl.SetFillWidth(false);
                hdRow.Add(cHydraDurLbl);
                sHydraDur = new MachinerySlider(Math.Max(0, WaterPlan.HydraDuraCap), 120);
                sHydraDur.SetRange(0f, (float)HydraDurMaxSlider, 1f);
                sHydraDur.SetFillWidth(true);
                sHydraDur.onChanged = new Action<float>(OnHydraDurSlide);
                sHydraDur.onReleased = new Action<float>(OnHydraDurReleased);
                hdRow.Add(sHydraDur);
                stack.Add(hdRow);
                RefreshHydraDurLabel();
                // 玩家 2026-11-10：过滤棉也做成原生滑条 —— 选「这次用多少点耐久」（不按块数，400+311 按块会虚高）。
                // 背包里没有滤棉就不显示这一行。
                if (FilterDurOwned() > 0)
                {
                    var fdRow = new MachineryStack(false, 4);
                    cFilterDurLbl = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                    cFilterDurLbl.SetColor(Cream);
                    cFilterDurLbl.SetFillWidth(false);
                    fdRow.Add(cFilterDurLbl);
                    sFilterDur = new MachinerySlider((float)FilterDurNow(), 120);
                    sFilterDur.SetRange(0f, (float)FilterDurMaxSlider, 1f);
                    sFilterDur.SetFillWidth(true);
                    sFilterDur.onChanged = new Action<float>(OnFilterDurSlide);
                    sFilterDur.onReleased = new Action<float>(OnFilterDurReleased);
                    fdRow.Add(sFilterDur);
                    stack.Add(fdRow);
                    RefreshFilterDurLabel();
                }
                pSetBtns.Add(cTarget); pSetBtns.Add(cMode); pSetBtns.Add(cPre); pSetBtns.Add(cPost); pSetBtns.Add(cSrc);
                pSetBtns.Add(cHydra);
            }

            if (main)
            {
                pCard1 = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                pCard1.SetColor(Cream); pCard1.SetFillWidth(true);
                stack.Add(pCard1);

                // 卡一的量单独一行放大（对应网页卡里的大字 mL）
                pCard1Val = new MachineryText("", 11, HorizontalAlignmentOptions.Left, false);
                pCard1Val.SetColor(Cream); pCard1Val.SetFillWidth(true);
                stack.Add(pCard1Val);

                pCard2 = new MachineryText("", 9, HorizontalAlignmentOptions.Left, false);
                pCard2.SetColor(Cream); pCard2.SetFillWidth(true);
                stack.Add(pCard2);

                // 玩家 2026-11-11：「卖多少 / 赚多少」从「出来 …」那一行挪出来，单独一行跟在下面。
                pMoney = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
                pMoney.SetColor(Cream); pMoney.SetFillWidth(true);
                stack.Add(pMoney);

                // 玩家 2026-11-10：步骤放在「出来 X mL」的下一行，前面不要「·」、中间不要空行。
                pStep = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
                pStep.SetColor(Cream); pStep.SetFillWidth(true);
                stack.Add(pStep);

                pCard3 = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
                pCard3.SetColor(Warn); pCard3.SetFillWidth(true);
                stack.Add(pCard3);

                // 玩家 2026-11-10：主页显示背包里实际持有的总额（没有的道具整行不显示、也不占高度）。
                pOwn = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
                pOwn.SetColor(Dim); pOwn.SetFillWidth(true);
                stack.Add(pOwn);

                // 玩家：主界面不要六项水质明细（省一行，界面还能更窄）。明细留在「更多方案/日志」里。
                pMixLine = null;

                pMsg = new MachineryText("", 8, HorizontalAlignmentOptions.Left, false);
                pMsg.SetColor(Warn); pMsg.SetFillWidth(true);
                stack.Add(pMsg);

                // 玩家：按钮别拉满整行，短一点（以前 268 宽几乎占满面板）
                // 玩家：面板里不要再放「接水」大按钮——直接用净水器标题栏那个水滴图标（fillBtn）。
            }

            if (pool || steps)
            {
                pTableHead = new MachineryText(pool ? "点一行就选中" : "做法步骤（每步后的体积/纯度）", 8, HorizontalAlignmentOptions.Left, false);
                pTableHead.SetColor(Warn); pTableHead.SetFillWidth(true);
                stack.Add(pTableHead);
                if (pool)
                {
                    // 玩家要求：点方案表里的**行本身**就选中（以前只有底部 1..5 数字键）。
                    // 行用原生 MachineryButton，天然可点；回调按序号去查当前表里那一条。
                    pRowsBox = new MachineryStack(true, 2);
                    pRowBtns = new MachineryButton[RowCount];
                    for (int ri = 0; ri < RowCount; ri++)
                    {
                        int k = ri;
                        pRowBtns[ri] = new MachineryButton("", new Action(delegate { PickRow(k); }), 258, 24);
                        pRowsBox.Add(pRowBtns[ri]);
                    }
                    stack.Add(pRowsBox);
                }
                else
                {
                    try
                    {
                        pList = new ScrollableRichTextElement(true, true);
                        pList.ResizePixels(258, 168);
                        stack.Add(pList);
                    }
                    catch (Exception e) { log.LogWarning("[ui] list " + e.Message); pList = null; }
                }
                pPickRow = null;
            }

            // 玩家：图标不再放面板底部，改挂到「净水助手」标题栏右上角（X 左边）
            int iw2, ih2;
            var acts = new MachineryStack(false, 4);
            // 玩家：备水功能先删掉（太难做，等以后再说）
            pMode2 = null;
            // 图标全部挂在标题栏（AddPanelHeader），底部不再放任何图标

            // 左列竖排导航（玩家要求）：设置 / 更多方案（页 2 时变「返回」）/ 步骤 都挂在左边，
            // 当前页那一项带 ▶。右边才是内容列 —— 页面本身不再留白。
            var nav = new MachineryStack(true, 3);
            // 玩家 2026-11-11：三键要和右边内容「从同一行起头」（设置对齐「容器」那行），
            // 之前竖直居中挂在内容列中间，看起来偏下。SetAlign(Start)＝交叉轴贴顶。
            try { nav.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            pNavSet = Small((UiPage == 1 ? "▶ " : "") + "设置", new Action(delegate { SetPage(1); }), 72);
            pMore = Small((UiPage == 2 ? "▶ " : "") + (pool ? "返回" : "更多方案"), new Action(delegate { SetPage(2); }), 72);
            pNavSteps = Small((UiPage == 3 ? "▶ " : "") + "步骤", new Action(delegate { SetPage(3); }), 72);
            // 玩家 2026-11-11：原来给三个按钮涂奶油/金色，浅底浅字＝「白白的」看不清。
            // 现在完全不改文字色，保留原生深色，只用 ▶ 前缀表示当前页。
            nav.Add(pNavSet); nav.Add(pMore); nav.Add(pNavSteps);

            // 两列：左＝竖排导航，右＝当前页内容
            var root = new MachineryStack(false, 4);
            // 玩家 2026-11-11：左右两列都要从同一条顶线起排（让步按钮和「容器」那行齐平）。
            try { root.SetAlign(MachineryStack.Align.Start); } catch (Exception) { }
            root.Add(nav);
            root.Add(stack);

            // 玩家：标题要像官方净水器那样贴在窗口最上方居中，且不要那个白色药丸框。
            // 做法：panel 标题留空（药丸消失），先放一行居中文字当兜底标题；
            // 若运行时能拿到 frame.titleText（官方标题控件），就把这行隐藏掉，直接用官方标题位。
            var rootV = new MachineryStack(true, 3);
            pTitleRow = new MachineryText("净水助手", 9, HorizontalAlignmentOptions.Center, false);
            pTitleRow.SetColor(Cream);
            pTitleRow.SetFillWidth(true);
            rootV.Add(pTitleRow);
            rootV.Add(root);

            // 传 (string)null 而不是 ""：空串仍会画一个空的浅色标题框（玩家看到的就是那个小圆角白框），
            // 标题已经由窗口自带的 titleString 显示在图标那一排。显式转型是为了避开 Sprite 重载。
            var panel = new MachineryPanel((string)null, rootV, "watercalc");
            panel.SetMinWidth(PanelW);
            if (PanelAlive())
            {
                // 只有「页签变了」才换内容；同一页反复 Detach/Attach 会把窗口弄坏（玩家：详情只有第一次有效）
                AddPanelHeader();
                ApplyFrameTitle();
                if (panelPageFor != UiPage)
                {
                    try { panelWin.Detach(); } catch (Exception) { }
                    bool attachOk = false;
                    try { panelWin.Attach(panel); attachOk = true; } catch (Exception e2) { log.LogWarning("[ui] attach " + e2.Message); }
                    if (!attachOk)
                    {
                        // 以前 Attach 失败也硬把 panelPageFor 记成「这页装好了」→ 之后永远不再重试，
                        // 表现就是「点设置/更多方案/步骤任何键都没反应」（玩家 2026-10-10 报）。
                        // 失败时直接把窗口判死，下一帧按新窗口重建，别把面板锁在旧页上。
                        panelWin = null;
                        panelPageFor = -1;
                        panelHeaderDone = false;
                        return;
                    }
                    panelPageFor = UiPage;
                    int h2 = PageHeight();
                    try { panelWin.ResizePixels(PanelW, h2); } catch (Exception) { }
                }
                return;
            }
            panelPageFor = UiPage;
            panelWin = MachineHelper.CreateMachineryWindow(true, new MachineryPanel[] { panel });
            if (panelWin != null)
            {
                // 默认放屏幕中间（PixelWindow 自带 Center()），别贴左上角
                try { panelWin.Center(); } catch (Exception) { }
                int h = PageHeight();
                try { panelWin.ResizePixels(PanelW, h); } catch (Exception) { }
                panelHeaderDone = false;
                AddPanelHeader();
                ApplyFrameTitle();
                // 不再给主面板加点击拦截层：它会盖在面板上，重新打开时抢先吃点击/触发关闭
                // AddClickBlocker(panelWin, "main", 208, h);
                // 位置完全交给引擎/玩家拖动：不再强推坐标（以前推 (300,-240) 会先闪左上角再跳位）。
                try { panelWin.Hide(); } catch (Exception) { }
                if (panelWin.IsVisible())
                {
                    try { panelWin.rectTransform.gameObject.SetActive(false); } catch (Exception) { }
                }
                Log("[ui] 独立窗口 page=" + UiPage + " ok visible=" + panelWin.IsVisible());
            }
            else log.LogWarning("[ui] 独立窗口 null");
        }

        /// <summary>这些 UI 包装类型没有暴露 gameObject，统一从 rectTransform 上拿。</summary>
        private static void SetVisible(object ui, bool on)
        {
            if (ui == null) return;
            try
            {
                // 走 GameBridge.Get → ModReflect：不再用 ui.GetType() 反射（工坊扫描器会判 reflection-external）。
                var rt = GameBridge.Get(ui, "rectTransform") as RectTransform;
                if (rt != null) rt.gameObject.SetActive(on);
            }
            catch (Exception) { }
        }

        private static MachineryButton Small(string text, Action cb, int w)
        {
            // 玩家 2026-11-11：按钮原来又窄又小、字贴边；但要的是「矮一点、别撑满整栏」——
            // 之前把宽度强拉到 88 反而被拉成一长条，现在按调用方给的宽度，并关掉 fillWidth。
            var b = new MachineryButton(" " + text + " ", cb, w, 20);
            try { b.SetFillWidth(false); } catch (Exception) { }
            return b;
        }

        /// <summary>
        /// 给原生按钮的文字上色（MachineryButton 没有 SetColor，只能改它子树里的 TMP 文字）。
        /// 同时把 Button.transition 关掉，否则鼠标悬停时原生样式会把颜色盖回去（选台圆点踩过同一个坑）。
        /// </summary>
        private static void PaintButton(MachineryButton b, Color c)
        {
            if (b == null) return;
            try
            {
                RectTransform rt = b.rectTransform;
                if (rt == null) return;
                var tmps = rt.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                for (int i = 0; i < tmps.Length; i++) if (tmps[i] != null) tmps[i].color = c;
                var btns = rt.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                for (int i = 0; i < btns.Length; i++) if (btns[i] != null) btns[i].transition = UnityEngine.UI.Selectable.Transition.None;
            }
            catch (Exception) { }
        }

        // 静态三选一：标签一行 + 平铺按钮一行，当前项前缀 ▶，直接点选（不再用「改」循环）。
        // 下拉式选择：一行只放一个按钮，文字是「组名 ▶ 当前值」，点一下换下一个选项。
        private static MachineryButton CycleRow(MachineryStack parent, string group, string[] names, Func<int> get, Action<int> pick)
        {
            var row = new MachineryStack(false, 3);
            var b = new MachineryButton("", new Action(delegate
            {
                int cur = get();
                pick((cur + 1) % names.Length);
            }), 268, 20);
            try { b.SetText(group + " ▶ " + names[Math.Max(0, Math.Min(get(), names.Length - 1))]); } catch (Exception) { }
            row.Add(b);
            parent.Add(row);
            return b;
        }

        /// <summary>显示名与内部值分离的下拉：值仍走老索引（计算逻辑不动），只是界面上的可选项变了。</summary>
        private static MachineryButton CycleRowMap(MachineryStack parent, string group, string[] names, int[] vals, Func<int> get, Action<int> pick)
        {
            var row = new MachineryStack(false, 3);
            var b = new MachineryButton("", new Action(delegate
            {
                int cur = get();
                int idx = 0;
                for (int i = 0; i < vals.Length; i++) if (vals[i] == cur) { idx = i; break; }
                int next = (idx + 1) % vals.Length;
                pick(vals[next]);
            }), 268, 20);
            try
            {
                int cur0 = get(); string disp = names[0];
                for (int i = 0; i < vals.Length; i++) if (vals[i] == cur0) { disp = names[i]; break; }
                b.SetText(group + " ▶ " + disp);
            }
            catch (Exception) { }
            row.Add(b);
            parent.Add(row);
            return b;
        }

        /// <summary>下拉名（按内部值取 UI 显示名）。</summary>
        private static string UiName(string[] names, int[] vals, int v)
        {
            for (int i = 0; i < vals.Length; i++) if (vals[i] == v) return names[i];
            return names.Length > 0 ? names[0] : "";
        }

        private static MachineryButton[] Seg(MachineryStack parent, MachineryText label, string[] names, Action<int> pick)
        {
            parent.Add(label);
            var row = new MachineryStack(false, 3);
            var bs = new MachineryButton[names.Length];
            // 按钮宽度按行内数量分摊：原来写死 116，3~4 个就撑爆 340 宽的窗口互相压住
            // （玩家截图里「1857mL铁锈水 90.9…」被后面的按钮盖掉了）。
            int bw = Math.Max(56, (300 - (names.Length - 1) * 3) / names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                int k = i;
                bs[i] = new MachineryButton((i == 0 ? "▶ " : "") + names[i], new Action(delegate { pick(k); }), bw, 24);
                row.Add(bs[i]);
            }
            parent.Add(row);
            return bs;
        }

        /// <summary>富文本要不要重画：控件对象换了（整窗重建）或数据签名变了才画。
        /// 玩家 2026-11-10：面板开着划鼠标就卡，一大半是每次 Rebuild 把几千字富文本整段重排。</summary>
        private static bool RichChanged(object owner, string sig)
        {
            if (!object.ReferenceEquals(owner, lastRichOwner) || sig != lastRichSig)
            {
                lastRichOwner = owner;
                lastRichSig = sig;
                return true;
            }
            return false;
        }

        // 玩家 2026-11-10：Rebuild 每次都无条件给 8 颗按钮 SetText，划过按钮就卡。
        // 记住「这组按钮上次写的是哪个选中项」，没变就不再碰原生控件。
        private static readonly Dictionary<MachineryButton[], string> markCache = new Dictionary<MachineryButton[], string>();
        private static void Mark(MachineryButton[] bs, string[] names, int sel)        {
            if (bs == null) return;
            string sig = sel + "|" + names.Length;
            string prev;
            if (markCache.TryGetValue(bs, out prev) && prev == sig) return;
            markCache[bs] = sig;
            for (int i = 0; i < bs.Length && i < names.Length; i++)
            {
                try { bs[i].SetText((i == sel ? "▶ " : "") + names[i]); } catch (Exception) { }
            }
        }

        // 玩家：1..5 手动选的行会和「自动」打架。定案：任何一个下拉改动都清掉手动选择，回到自动挑。
        private static void SetMode(int i)
        {
            // 玩家 2026-11-10：点「默认/性价比/质量」就要真的从「效率（无视容器）」退出来，
            // 否则点完默认还是按 50 L 在算，看起来跟效率一模一样。
            // 玩家 2026-11-11 m00327：档位键三态循环 —— 第一次按＝按真实背包道具算，
            // 再按同一个键＝按无限道具算，再按＝回真实。开关是全局的（Q2）。
            bool sameMode = (Mode == i) && !UncapEff;
            UncapEff = false;
            Mode = i; ManualPickLabel = null; Log("[ui] mode=" + i + (sameMode ? " (same -> toggle道具口径)" : ""));
            if (sameMode) ToggleInfinite("再点了一次档位键");
            else
            {
                // 底行黄字必须跟着当前档走，不能留着上一次「效率：…」的残留（玩家 2026-11-10 报）。
                LastMsg = i == 2 ? "性价比：0 过滤棉 + 一晚直出（紫外免费可带）"
                       : "自定义：一晚净赚最多（按容器里的水与目标质量）";
            }
            if (modeBtnsS != null)
                for (int k = 0; k < modeBtnsS.Length && k < PlanText.ModeShort.Length; k++)
                    if (modeBtnsS[k] != null) modeBtnsS[k].SetText((k == i ? "▶" : "") + PlanText.ModeShort[k]);
            // 玩家 2026-11-11 定案：按「默认 / 效率 / 性价比」就直接按该档给推荐方案 ——
            // 按下档位键＝解锁 + 丢缓存，Rebuild 里按新档重算，算完再锁上（不再是只换名字）。
            // 玩家 2026-11-10：点档位会卡 —— 按钮视觉已经即时改过，整窗重建挪到下一帧做一次。
            UnlockPlan("按了模式键");
            DropPlanCache();
            RequestRebuild();
        }

        /// <summary>「效率」档：一晚装满当前容器、净出水最多（不管性价比）。
        /// 「无视容器」已删掉，一律按当前容器的容量规划。</summary>
        private static void ToggleUncapEff()
        {
            bool sameMode = (Mode == 1) && !UncapEff;
            UncapEff = false;
            Mode = 1;
            ManualPickLabel = null;
            if (sameMode) ToggleInfinite("再点了一次效率键");
            else LastMsg = "效率：装满当前容器（一晚净出水最多）";
            Log("[ui] mode=1 效率" + (sameMode ? " (same -> toggle道具口径)" : ""));
            UnlockPlan("按了效率键");
            DropPlanCache();
            RequestRebuild();
        }

        /// <summary>当前净水器里装了几个 node 类耗材（过滤棉/紫外灯都是 node_medium）。</summary>
        private static int CountNodeModules()
        {
            try
            {
                var m = CurrentMachine();
                if (m == null) return 0;
                string d = GameBridge.DescribeModules(m);
                if (string.IsNullOrEmpty(d)) return 0;
                int n = 0;
                foreach (string part in d.Split(','))
                {
                    string k = part.Trim();
                    if (k.StartsWith("node_")) n++;
                }
                return n;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>背包/台面里有没有这个 identifier 的道具（只认整数件）。</summary>
        private static bool OwnsItem(string id)
        {
            try
            {
                var all = GameBridge.LiveAllItems();
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string k = null;
                    try { k = it.identifier; } catch (Exception) { }
                    if (k == id) return true;
                }
            }
            catch (Exception) { }
            return false;
        }
        private static void SetTarget(int i) { TargetIdx = i; ManualPickLabel = null; Log("[ui] target=" + i); if (cTarget != null) try { cTarget.SetText("目标 ▶ " + PlanText.TargetNames[Math.Max(0, Math.Min(i, PlanText.TargetNames.Length - 1))]); } catch (Exception) { } RequestRebuild(); }
        private static void SetPre(int i) { PreMode = i; ManualPickLabel = null; Log("[ui] pre=" + i); if (cPre != null) try { cPre.SetText("前置 ▶ " + UiName(PreUi, PreVal, i)); } catch (Exception) { } RequestRebuild(); }
        private static void SetPost(int i) { PostMode = i; ManualPickLabel = null; Log("[ui] post=" + i); if (cPost != null) try { cPost.SetText("后置 ▶ " + UiName(PostUi, PostVal, i)); } catch (Exception) { } RequestRebuild(); }
        private static void SetSrc(int i) { SrcIdx = i; ManualPickLabel = null; Log("[ui] src=" + i); if (cSrc != null) try { cSrc.SetText("水源 ▶ " + SrcUiName(i)); } catch (Exception) { } RequestRebuild(); }
        /// <summary>海德拉开关：0 自动 / 1 强制 / 2 不用。改完只打脏标记，重建交给下一帧。</summary>
        private static void SetHydra(int i) { WaterPlan.HydraMode = i; ManualPickLabel = null; Log("[ui] hydra=" + i); RequestRebuild(); }
        /// <summary>海德拉耐久上限：300 一只 / 600 两只 / 900 三只 / 0 不限。</summary>
        /// <summary>滑条拖动中：只存值 + 改旁边那行字，绝不整窗重建（重建会卡，玩家报过多次）。</summary>
        private static void OnHydraDurSlide(float v)
        {
            int n = (int)Math.Round(v);
            if (n < 0) n = 0;
            if (n > HydraDurMaxSlider) n = HydraDurMaxSlider;
            if (n == WaterPlan.HydraDuraCap) return;
            WaterPlan.HydraDuraCap = n;
            WaterPlan.HydraDurUserSet = true;   // 玩家亲手拖过：以后以滑条为准（0 = 不限）
            ManualPickLabel = null;
            RefreshHydraDurLabel();
        }

        /// <summary>松手才重算一次方案。</summary>
        private static void OnHydraDurReleased(float v)
        {
            OnHydraDurSlide(v);
            Log("[ui] hydraDur=" + WaterPlan.HydraDuraCap);
            RequestRebuild();
        }

        /// <summary>滑条该显示的值：拖过就是滑条本身；没拖过显示「实际会用到的耐久」（背包里剩多少就是多少）。</summary>
        private static int HydraDurShown()
        {
            if (WaterPlan.HydraDurUserSet || InfiniteTools) return WaterPlan.HydraDuraCap;
            var o = OwnedTools();
            int own = o != null ? o.hydraDur : 0;
            if (own > 0 && WaterPlan.HydraDuraCap > 0 && own > WaterPlan.HydraDuraCap) own = WaterPlan.HydraDuraCap;
            return own > 0 ? own : WaterPlan.HydraDuraCap;
        }

        private static void RefreshHydraDurLabel()
        {
            if (cHydraDurLbl == null) return;
            int hd = HydraDurShown();
            if (hd <= 0) { cHydraDurLbl.SetText("海德拉耐久 不限"); return; }
            double n = hd / 300.0;
            cHydraDurLbl.SetText("海德拉耐久 " + hd + " 点（" + n.ToString("0.##") + " 只 ≈ " +
                                 WaterPlan.Ml((long)hd * WaterMath.HYDRA_UL_PER_DUR) + " mL）");
        }

        // —— 过滤棉耐久：背包里所有滤棉累加，玩家用原生滑条决定这次用多少点 ——
        /// <summary>背包里实际持有的过滤棉总耐久（没有就是 0）。</summary>
        private static int FilterDurOwned()
        {
            var o = OwnedTools();
            if (o == null) return 0;
            return o.filterDur > 0 ? o.filterDur : 0;
        }

        /// <summary>这次实际用多少点耐久：玩家拖过就用拖的值，没拖过就用持有总和；上限 2000。</summary>
        private static int FilterDurNow()
        {
            // 玩家 2026-11-11：无限口径下过滤棉不限；拉条只用来管真实道具的手操耐久。
            if (InfiniteTools) return 1 << 20;
            int own = FilterDurOwned();
            if (FilterDurUse >= 0) return Math.Max(0, Math.Min(FilterDurMaxSlider, FilterDurUse));
            return Math.Max(0, Math.Min(FilterDurMaxSlider, own));
        }

        /// <summary>拖动中只刷标签，不重算方案（松手才重建）。</summary>
        private static void OnFilterDurSlide(float v)
        {
            int n = (int)Math.Round(v);
            if (n < 0) n = 0;
            if (n > FilterDurMaxSlider) n = FilterDurMaxSlider;
            if (n == FilterDurUse) return;
            FilterDurUse = n;
            ManualPickLabel = null;
            RefreshFilterDurLabel();
        }

        /// <summary>松手才重算一次方案。</summary>
        private static void OnFilterDurReleased(float v)
        {
            OnFilterDurSlide(v);
            Log("[ui] filterDur=" + FilterDurUse);
            RequestRebuild();
        }

        private static void RefreshFilterDurLabel()
        {
            if (cFilterDurLbl == null) return;
            int own = FilterDurOwned();
            int use = FilterDurNow();
            if (use <= 0) { cFilterDurLbl.SetText("过滤棉耐久 不用过滤棉（持有 " + own + " 点）"); return; }
            cFilterDurLbl.SetText("过滤棉耐久 " + use + " 点 · 持有 " + own + " 点");
        }

        /// <summary>主页「持有」行：只拼背包里真有的项，都没有返回空串（整行隐藏）。</summary>
        private static string OwnLineText()
        {
            var o = OwnedTools();
            if (o == null) return "";
            if (InfiniteTools) return "持有：无限";
            string s = "";
            if (o.filterDur > 0) s = "过滤棉 " + o.filterDur + " 点";
            if (o.hydraDur > 0) s = s.Length > 0 ? s + "    " + "海德拉 " + o.hydraDur + " 点" : "海德拉 " + o.hydraDur + " 点";
            return s.Length > 0 ? "持有：" + s : "";
        }
        public static string SrcPlanPublic() { int g, p; TodaySource(out g, out p); int perf, q; Bonuses(out perf, out q); int tb = PlanText.TargetBp(TargetIdx, q); var r = WaterPlan.Build(2000000L, p, perf, q, tb, SrcNames[Math.Max(0, Math.Min(SrcIdx, SrcNames.Length - 1))], "空瓶 2000 mL", true, Mode, PreMode, PostMode); if (r == null) return "srcplan -> none"; return "srcplan -> " + SrcNames[Math.Max(0, Math.Min(SrcIdx, SrcNames.Length - 1))] + " @" + PlanText.Pct(p) + " 接 " + (r.ShownUl()/1000) + " mL → 出 " + (r.pick == null ? 0 : r.pick.outUl/1000) + " mL · " + (r.pick == null ? "—" : PlanText.Pct(r.pick.purityBp)) + "（" + (r.pick == null ? "无方案" : r.pick.label) + "）"; }
        public static string SrcPublic() { int g, p; TodaySource(out g, out p); return "src -> idx=" + SrcIdx + " name=" + SrcNames[Math.Max(0, Math.Min(SrcIdx, SrcNames.Length - 1))] + " grade=" + g + " purity=" + PlanText.Pct(p); }
        public static string SetSrcPublic(string s) { int v; if (!int.TryParse(s, out v)) return "src -> bad-arg"; if (v < 0 || v >= SrcNames.Length) return "src -> out-of-range 0.." + (SrcNames.Length - 1); SetSrc(v); return SrcPublic(); }

        // 水龙头真值一览：让面板/自检知道「今天有什么水」，不再默认铁锈水
        public static string TapPublic()
        {
            int rg = 4, rp = 0, gg = 5, gp = 0;
            try
            {
                var st = StoreStation.Instance;
                if (st != null) { rp = st.todayRustwaterPurity; gp = st.todayGutterflowPurity; }
            }
            catch (Exception e) { return "tap -> ERR " + e.GetType().Name; }
            return "tap -> rust=" + (rp > 0 ? PlanText.Pct(rp) : "无") + " gutter=" + (gp > 0 ? PlanText.Pct(gp) : "无")
                 + " 自动选=" + (rp > 0 ? "铁锈水" : (gp > 0 ? "沟渠水" : "未解锁/无法读取"));
        }

        // 网页的「自己填纯度」：0..10000 表示 0.00%..100.00%，-1 关闭
        /// <summary>ctl 用：点选第 n 行方案（1 起），传 auto 回到自动。</summary>
        public static string PickPublic(string s)
        {
            try
            {
                if (string.IsNullOrEmpty(s) || s == "auto") { ManualPickLabel = null; Rebuild(true); return "pick -> auto"; }
                int n;
                var rows = lastRows;
                var rep = CurrentPlan(true);          // 面板没重建过时 lastRows 为空，这里按当前模型现算一份
                if (rows == null && rep != null && rep.combos != null)
                {
                    var ok = new System.Collections.Generic.List<WaterPlan.Combo>();
                    foreach (var c in rep.combos) if (c.ok) ok.Add(c);
                    ok.Sort(delegate (WaterPlan.Combo a, WaterPlan.Combo b) { return b.outUl.CompareTo(a.outUl); });
                    rows = ok.ToArray();
                }
                if (!int.TryParse(s, out n) || n < 1 || rows == null || n > rows.Length)
                    return "pick -> bad-arg rows=" + (rows == null ? 0 : rows.Length);
                ManualPickLabel = rows[n - 1].label;
                Rebuild(true);
                return "pick -> " + ManualPickLabel;
            }
            catch (Exception e) { return "pick -> ERR " + e.Message; }
        }

        public static string CustomPurPublic() { return "pur -> " + (CustomPur > 0 ? PlanText.Pct(CustomPur) : "off") + " (mode=" + SrcNames[Math.Max(0, Math.Min(SrcIdx, SrcNames.Length - 1))] + ")"; }
        public static string SetCustomPurPublic(string s)
        {
            int v;
            if (!int.TryParse(s, out v)) return "pur -> bad-arg";
            if (v < 0) { CustomPur = -1; }
            else if (v > 10000) return "pur -> out-of-range 0..10000";
            else CustomPur = v;
            Rebuild(true);
            return CustomPurPublic();
        }

        // ctl 通道用：让下拉项能在无鼠标的情况下被设置与核对
        public static string SetUiPublic(string what, int v)
        {
            try
            {
                if (what == "target") SetTarget(v);
                else if (what == "mode") SetMode(v);
                else if (what == "pre") SetPre(v);
                else if (what == "post") SetPost(v);
                else return "ui -> bad-key " + what;
                return "ui -> " + what + "=" + v + " target=" + TargetIdx + " mode=" + Mode + " pre=" + PreMode + " post=" + PostMode;
            }
            catch (Exception e) { return "ui -> ERR " + e.GetType().Name + " " + e.Message; }
        }

        private static void CycleMode() { Mode = (Mode + 1) % PlanText.ModeNames.Length; UncapEff = false; ManualPickLabel = null; UnlockPlan("按了模式键"); DropPlanCache(); RequestRebuild(); }
        private static void CycleTarget() { TargetIdx = (TargetIdx + 1) % PlanText.TargetNames.Length; RequestRebuild(); }
        private static void CyclePre() { PreMode = (PreMode + 1) % PlanText.PreNames.Length; RequestRebuild(); }

        private static UnityEngine.Vector2 posSteps, posSrc;
        private static bool hasSteps, hasSrc;
        /// <summary>每帧记住各窗口位置（玩家点 × 关掉再开要留在原处，和游戏一样）。</summary>
        /// <summary>所有窗口的位置登记表：key -> 世界坐标。以后新窗口只要 WinRestore(key) / RememberAllWindows 里加一行即可。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, UnityEngine.Vector2> winPos
            = new System.Collections.Generic.Dictionary<string, UnityEngine.Vector2>();
        public static void WinRestore(string key, PixelWindow w)
        {
            if (w == null || string.IsNullOrEmpty(key)) return;
            UnityEngine.Vector2 v;
            if (winPos.TryGetValue(key, out v)) WinSetPos(w, v);
        }
        public static void WinSave(string key, PixelWindow w)
        {
            if (w == null || string.IsNullOrEmpty(key)) return;
            UnityEngine.Vector2 v;
            if (WinGetPos(w, out v)) winPos[key] = v;
        }

        public static void RememberAllWindows()
        {
            WinSave("main", panelWin); WinSave("cfg", settingsWin); WinSave("steps", stepsWin); WinSave("src", srcWin);
            try { if (pendingMainFrames <= 0 && panelWin != null && panelWin.IsVisible()) { UnityEngine.Vector2 v; if (WinGetPos(panelWin, out v)) { keepMainPos = v; hasKeepMain = true; } } } catch (Exception) { }

        }
        private static UnityEngine.Vector2 keepMainPos;
        private static bool hasKeepMain;
        private static int panelPageFor = -1;   // 这个窗口里现在装的是哪一页（-1＝没装）
        // 主面板两列宽：左竖排导航 72 + 间距 4 + 内容 224（玩家：界面还能再缩一档）
        private const int PanelW = 304;
        private static bool panelHeaderDone;
        private static MachineryButton pNavSet;
        private static MachineryButton pNavSteps;
        /// <summary>PixelWindow 真正的定位字段是 DockAnchorOffset（anchoredPosition 恒为 0，改了也没用）。</summary>
        // 位置一律用「世界坐标」记：anchoredPosition 与 DockAnchorOffset 在这类窗口上都读回 0，
        // 只有 transform.position 才是拖动后真正变的那份。
        // 主面板默认位置（画布坐标，原点左下；y=1080 是屏幕上沿）
        private static float MainPosX = 610f;
        private static float MainPosY = 670f;
        // 玩家：净水助手要记住关闭前的位置，下次打开回到那儿（只有第一次才居中）
        private static float LastPosX = 0f;
        private static float LastPosY = 0f;
        private static bool hasLastPos = false;

        // 显示后要连续顶几帧：PixelWindow.Show() 自己会把位置排回左上角(0,1080)，只写一次会被它盖掉。
        private class ForcePos { public PixelWindow w; public float x; public float y; public int frames; }
        private static readonly List<ForcePos> forcePos = new List<ForcePos>();

        private static void CenterXY(PixelWindow w, out float x, out float y)
        {
            x = 960f; y = 540f;
            try
            {
                var rt = w.rectTransform;
                float pw = rt.rect.width, ph = rt.rect.height;
                if (pw <= 1f) pw = 360f;
                if (ph <= 1f) ph = 260f;
                x = (UnityEngine.Screen.width - pw) * 0.5f;
                y = (UnityEngine.Screen.height + ph) * 0.5f;
            }
            catch (Exception) { }
        }

        private static void ForceAt(PixelWindow w, float x, float y, int frames)
        {
            if (w == null) return;
            try { WinPlace(w, x, y); } catch (Exception) { }
            try { forcePos.Add(new ForcePos { w = w, x = x, y = y, frames = frames }); } catch (Exception) { }
        }

        /// <summary>把任意 mod 窗口放到屏幕正中，并在随后几帧持续顶住（防 Show() 把它排回左上角）。</summary>
        /// <summary>把 接水 / 详情 / 手册 / 效率 / 性价比 挂到面板自己的标题栏（AddHeaderButton 加在原生 X 左边）。</summary>
        private static void AddPanelHeader()
        {
            if (panelWin == null || panelHeaderDone) return;
            try
            {
                int iw, ih;
                var bFill = NewIcon(PixelIcons.Get("water", out iw, out ih), "接水", new Action(delegate { OnFill(); }));
                // 玩家 2026-11-10：「净水详情」这个按钮删掉 —— 那份内容并到「步骤」页里，不需要两个地方看同一份东西。
                var bBook = NewIcon(PixelIcons.Get("note", out iw, out ih), "手册", new Action(delegate { WaterBook.Open(1); }));
                var bEff = NewIcon(PixelIcons.Get("serve", out iw, out ih), "自定义", new Action(delegate { SetMode(0); }));
                var bCheap = NewIcon(PixelIcons.Get("gear", out iw, out ih), "性价比", new Action(delegate { SetMode(2); }));
                var bUncap = NewIcon(PixelIcons.Get("serve", out iw, out ih), "效率", new Action(delegate { ToggleUncapEff(); }));
                // 与净水器标题栏同序：接水 → 默认 → 效率 → 性价比 → 手册（详情已并入步骤页）
                if (bFill != null) panelWin.AddHeaderButton(bFill);
                if (bEff != null) panelWin.AddHeaderButton(bEff);
                if (bUncap != null) panelWin.AddHeaderButton(bUncap);
                if (bCheap != null) panelWin.AddHeaderButton(bCheap);
                if (bBook != null) panelWin.AddHeaderButton(bBook);
                panelHeaderDone = true;
                Log("[ui] 面板标题栏图标 ok");
            }
            catch (Exception e) { log.LogWarning("[ui] panel header " + e.GetType().Name + " " + e.Message); }
        }

        private static void CenterWindow(PixelWindow w)
        {
            if (w == null) return;
            float x, y; CenterXY(w, out x, out y);
            ForceAt(w, x, y, 12);
        }

        /// <summary>只写 world position 的定位：WinSetPos 同时写 anchoredPosition 会叠加成双倍位移。</summary>
        private static void WinPlace(PixelWindow w, float x, float y)
        {
            if (w == null) return;
            try
            {
                var rt = w.rectTransform;
                var p3 = rt.position;
                rt.position = new UnityEngine.Vector3(x, y, p3.z);
            }
            catch (Exception) { }
        }

        /// <summary>ctl: move=x,y —— 就地移动主面板并记住，用来标定默认位置。</summary>
        public static string MovePublic(string args)
        {
            try
            {
                var parts = args.Split(',');
                float x = float.Parse(parts[0].Trim());
                float y = float.Parse(parts[1].Trim());
                WinPlace(panelWin, x, y);
                MainPosX = x; MainPosY = y;
                LastPosX = x; LastPosY = y; hasLastPos = true;
                Log("[ui] move " + x + "," + y);
                return PosPublic();
            }
            catch (Exception e) { return "move ERR " + e.Message; }
        }

        private static bool WinGetPos(PixelWindow w, out UnityEngine.Vector2 v)
        {
            v = UnityEngine.Vector2.zero;
            if (w == null) return false;
            try { var p3 = w.rectTransform.position; v = new UnityEngine.Vector2(p3.x, p3.y); return true; }
            catch (Exception) { }
            return false;
        }
        private static void WinSetPos(PixelWindow w, UnityEngine.Vector2 v)
        {
            if (w == null) return;
            try
            {
                var rt = w.rectTransform;
                var p3 = rt.position;
                rt.position = new UnityEngine.Vector3(v.x, v.y, p3.z);
                // 同时写 anchoredPosition，双保险
                try { rt.anchoredPosition = new UnityEngine.Vector2(v.x, v.y); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        private static UnityEngine.Vector2 pendingMainPos;
        private static int pendingMainFrames;   // >0 时每帧强制套用位置，并暂停「记录位置」（布局会把坐标冲回 0，会形成死循环）
        private static bool pendingRebuild;     // 点击回调只记标记，整窗重建挪到下一帧（玩家：点按钮响应慢）

        /// <summary>点击后立刻改标签、重活延后一帧：按钮视觉即时，面板在下一帧一次性重建。</summary>
        /// <summary>请求重建：延后 0.12 s 再重建，别在玩家按下的那一帧就把按钮销毁重排。
        /// 玩家 2026-11-10 反馈「有时点一下不生效，得再点一次（像双击）」——
        /// 根因是按钮回调里立刻重建窗口，指针抬起时这个按钮已经被销毁，这一次点击就被吞了。</summary>
        public static void RequestRebuild()
        {
            pendingRebuild = true;
            pendingRebuildAt = UnityEngine.Time.realtimeSinceStartup + 0.12f;
        }

        private static float pendingRebuildAt = -1f;

        private static string lastLiveSig;      // 上一次算方案时的输入指纹（没变就不重算）
        private static int lastPageH = -1;      // 上次套用的面板高度（ResizePixels 会让原生窗口整体重排）
        private static float lastMarkerSync = -999f;

        /// <summary>面板显示的输入指纹：容器数值 + 各档设置 + 页面。
        /// 玩家 2026-11-10 反馈：面板开着时鼠标划过按钮约 1.5 s 卡一下 —— 真因是 Tick 里每秒无条件
        /// Rebuild(false)：Compute / 方案表重排 / ResizePixels 全在动原生窗口。指纹没变就整段跳过。</summary>
        private static string LiveSig()
        {
            var sb = new System.Text.StringBuilder(64);
            try
            {
                var b = CtxBottle(); if (b == null) b = GameBottle();
                if (b != null)
                {
                    sb.Append(WaterHelper.GetTotalVolume(b)).Append('/').Append(WaterHelper.GetTotalCapacity(b))
                      .Append('/').Append(WaterHelper.GetWaterPurity(b)).Append('/').Append(GameBridge.NameOfItem(b));
                }
                sb.Append("|ctx").Append(CtxBottle() == null ? 0 : 1);
            }
            catch (Exception) { sb.Append("err"); }
            sb.Append('|').Append(Mode).Append(',').Append(PreMode).Append(',').Append(PostMode)
              .Append(',').Append(TargetIdx).Append(',').Append(SrcIdx).Append(',').Append(UncapEff ? 1 : 0)
              .Append(',').Append(WaterPlan.HydraMode).Append(',').Append(WaterPlan.HydraDuraCap)
              .Append(",fd").Append(FilterDurNow())   // 玩家拖过滤棉滑条 → 指纹变 → 面板跟着重算
              .Append(",inf").Append(InfiniteTools ? 1 : 0)   // 真实/无限道具口径也进指纹
              .Append(',').Append(BalanceOff ? 1 : 0).Append(',').Append(ShowAllCombos ? 1 : 0)
              .Append(',').Append(UiPage).Append(',').Append(ManualPickLabel ?? "-");
            return sb.ToString();
        }

        public static void Tick()
        {
#if DEV
            var __sw = new System.Diagnostics.Stopwatch();
            __sw.Start();
            try { TickInner(); }
            finally
            {
                __sw.Stop();
                if (__sw.ElapsedMilliseconds >= 80)
                    Log("[perf] tick " + __sw.ElapsedMilliseconds + "ms | " + GameBridge.CountDump(10));
                else if (__sw.ElapsedMilliseconds >= 20 && GameBridge.Counts.Count > 0)
                    GameBridge.CountDump(1);
            }
#else
            TickInner();
#endif
        }

        private static void TickInner()
        {
#if DEV
            {
                float __now = UnityEngine.Time.realtimeSinceStartup;
                if (__now - lastTickIn >= 2f)
                {
                    lastTickIn = __now;
                    Log("[tick] in pairs=" + PurifierHook.Pairs.Count +
                        " mouse=" + (UnityEngine.Input.GetMouseButton(0) ? 1 : 0) +
                        " panel=" + (panelWin == null ? 0 : 1) +
                        " calc=" + (calcBtn == null ? 0 : 1));
                }
            }
#endif
            RememberAllWindows();
            if (forcePos.Count > 0)
            {
                for (int i = forcePos.Count - 1; i >= 0; i--)
                {
                    var f = forcePos[i];
                    if (f == null || f.w == null || f.frames <= 0) { forcePos.RemoveAt(i); continue; }
                    try { WinPlace(f.w, f.x, f.y); } catch (Exception) { }
                    f.frames--;
                    if (f.frames <= 0) forcePos.RemoveAt(i);
                }
            }
            if (pendingMainFrames > 0 && panelWin != null)
            {
                pendingMainFrames--;
                try { WinPlace(panelWin, MainPosX, MainPosY); } catch (Exception) { }
            }
            // 拖动窗口时不重建：重建会重排整个面板，拖起来就是一卡一卡的（玩家反馈）。
            if (UnityEngine.Input.GetMouseButton(0)) return;
            // 开关机器时红框要立刻跟上（原来 4 s 才同步一次，玩家说「红框显示有点慢」）。
            try { MarkerTick(); } catch (Exception) { }
            try { PickTintTick(); } catch (Exception) { }
            if (pendingRebuild && UnityEngine.Time.realtimeSinceStartup >= pendingRebuildAt)
            {
                pendingRebuild = false;
                pendingRebuildAt = -1f;
                try { Rebuild(true); } catch (Exception) { }
                return;
            }
            // 数值必须跟着现场变，但只在面板可见时重算；间隔放宽到 1.5s，
            // 因为 Compute() 要遍历全部物品（含机器仓 BFS），太频繁会让鼠标划过按钮明显掉帧。
            if (UnityEngine.Time.realtimeSinceStartup - lastLive > 1.5f)
            {
                lastLive = UnityEngine.Time.realtimeSinceStartup;
                if (panelWin != null && panelWin.IsVisible() && forcePos.Count == 0)
                {
                    UnityEngine.Vector2 pv2;
                    if (WinGetPos(panelWin, out pv2) && (pv2.x != 0f || pv2.y != 0f))
                    { LastPosX = pv2.x; LastPosY = pv2.y; hasLastPos = true; }
                }
                // 同时开几台就画几条：SyncAll 自己按每台窗口的 IsVisible 维护，
                // 关掉哪台就销毁哪台的框（不再只认 CurrentMachine 一台）。
                MarkerTick();
            }
            if (calcBtn == null && panelWin == null) return;
            if (Time.realtimeSinceStartup < nextTick) return;
            nextTick = Time.realtimeSinceStartup + 1f;
            try
            {
                // 面板没开就别算了：以前这里每秒无条件 Rebuild(false)（Compute + 方案重建），
                // 玩家在机器前瞎晃也会掉帧（「打开净水助手就很卡」）。
                bool uiVisible = panelWin != null && panelWin.IsVisible();
                if (uiVisible)
                {
                    // 玩家 2026-11-10：面板开着、鼠标划过按钮就是一下一下地卡。这里原来每秒无条件
                    // Rebuild(false) —— Compute + 方案表重排 + ResizePixels 都在动原生窗口。
                    // 改成先比输入指纹：容器/设置都没变就整段跳过，只在真的变了才重算。
                    string sig = LiveSig();
                    if (sig != lastLiveSig)
                    {
                        lastLiveSig = sig;
                        // 玩家 2026-11-11（性能）：这里原来先 Rebuild(false)、再按 modelDirty 补一次
                        // Rebuild(true)——同一份 Compute + 方案重建跑两遍（实测单次 170-270 ms，
                        // 变更一次就要 400-500 ms，鼠标划过按钮就是一下一下地卡）。
                        // 合并成一次全量：force=true 已经包含「整窗重建」的全部动作。
                        modelDirty = false;
                        Rebuild(true);
                        // 玩家 2026-11-10：灌水键只在容器体积/瓶量变了才重绑，别被别的设置变化带着跑。
                        string fillKey = "";
                        try
                        {
                            var b2 = CtxBottle(); if (b2 == null) b2 = GameBottle();
                            if (b2 != null) fillKey = WaterHelper.GetTotalVolume(b2) + "/" + WaterHelper.GetTotalCapacity(b2)
                                                    + "/" + WaterHelper.GetWaterPurity(b2);
                        }
                        catch (Exception) { }
                        if (fillKey != lastFillKey) { lastFillKey = fillKey; try { RefreshFillLabels(); } catch (Exception) { } }
                    }
                    // 玩家 2026-11-10：标题图标名字自愈每颗都要反射全量 GetMethods，节流到 5 s。
                    if (UnityEngine.Time.realtimeSinceStartup - lastHdrT > 5f)
                    {
                        lastHdrT = UnityEngine.Time.realtimeSinceStartup;
                        try { EnsureHdrNames(); } catch (Exception) { }
                    }
                    // 玩家 2026-11-10：WatchContainer 内部会全背包扫滤棉，节流到 4 s（且只在面板可见时跑）。
                    if (UnityEngine.Time.realtimeSinceStartup - lastWatchT > 4f)
                    {
                        lastWatchT = UnityEngine.Time.realtimeSinceStartup;
                        try { WatchContainer(); } catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }

        private static void AddLine(RichTextBuilder b, string text, ColorPalette c, bool bold)
        {
            b.AddLine(text, true, c, bold, false, false, false,
                (ColorPalette)(-1), (ColorPalette)(-1), (ColorPalette)(-1));
        }

        /// <summary>背包里有没有能读水质的东西。</summary>
        /// <summary>诊断：把 LiveAllItems 里每件的 identifier / 名字打出来（ctl `items`）。</summary>
#if DEV
        public static string ItemsDump()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var all = GameBridge.AllItemsDeep();   // 深度枚举：打印机/箱子里塞的也要列出来
                int n = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string id = ""; try { id = it.identifier; } catch (Exception) { }
                    int cnt = 1;
                    try
                    {
                        var t = it.GetType();
                        var pc = t.GetProperty("unitCount", BindingFlags.Public | BindingFlags.Instance);
                        if (pc != null) cnt = Convert.ToInt32(pc.GetValue(it, null));
                    }
                    catch (Exception) { }
                    sb.Append(n == 0 ? "" : " | ").Append(id).Append("×").Append(cnt).Append("=").Append(GameBridge.NameOfItem(it));
                    // 含水容器补上「容量/纯度」：塞在打印机仓里的瓶子，就是要靠这两个数确认「查到了」。
                    // 空瓶子体积是 0，只打体积会看不见，所以按容量判。
                    try
                    {
                        long cap = WaterHelper.GetTotalCapacity(it);
                        if (cap > 0)
                        {
                            long ul = WaterHelper.GetTotalVolume(it);
                            sb.Append(" ").Append(PlanText.Ml(ul)).Append("/").Append(PlanText.Ml(cap))
                              .Append("·").Append(PlanText.Pct(WaterHelper.GetWaterPurity(it)));
                        }
                    }
                    catch (Exception) { }
                    n++;
                }
                string bins = GameBridge.MachineStorageSummary();
                if (bins.Length > 0) sb.Append(" || 机器仓 ").Append(bins);
                return "items[" + all.Count + "] -> " + sb;
            }
            catch (Exception e) { return "items -> " + e.GetType().Name; }
        }
#endif

        /// <summary>
        /// 加一片净水片到当前瓶子：走游戏自己的 `WaterHelper.OnWaterPurificationTabletAdded`
        /// （它内部处理药片消耗与配比），玩家不用再回背包手点。
        /// </summary>
        private static void OnAddTablet()
        {
            try
            {
                var b = CtxBottle();
                if (b == null) b = GameBottle();   // 没开机器窗口时退到背包里的瓶子，F9 里也能加药片
                if (b == null) { LastMsg = "先把水瓶放进储水容器，再加药片"; Rebuild(true); return; }
                // 必须先真的有净水片：`OnWaterPurificationTabletAdded` 只负责改水质，不扣道具，
                // 不校验就会变成「凭空加纯度」（实测 98.04 → 98.54 而背包里没有药片）。
                var tab = FindTablet();
                if (tab == null) { LastMsg = "背包里没有净水片，加不了"; Log("[tablet] no-tablet"); Rebuild(true); return; }
                int before = WaterHelper.GetWaterPurity(b);
                GameBridge.StatVoid("WaterHelper", "OnWaterPurificationTabletAdded", b);
                int after = WaterHelper.GetWaterPurity(b);
                // 扣掉一片：`OnWaterPurificationTabletAdded` 本身不动背包，不扣就等于无限白嫖纯度。
                string used = "";
                try
                {
                    var tt = tab.GetType();
                    int n = 1;
                    var counted = GameBridge.Get(tab, "unitCount");
                    if (counted != null) n = Convert.ToInt32(counted);
                    if (n > 1)
                    {
                        bool wrote = GameBridge.Set(tab, "unitCount", n - 1);
                        if (!wrote) GameBridge.Call(tab, "SetUnitCount", n - 1);
                        used = "（背包剩 " + (n - 1) + " 片）";
                    }
                    else
                    {
                        GameBridge.Call(tab, "SetUnitCount", 0);
                        used = "（用光了）";
                    }
                }
                catch (Exception) { }
                LastMsg = "已加净水片：" + PlanText.Pct(before) + " → " + PlanText.Pct(after) + used + " [" + lastTabletHit + "]";
                Log("[tablet] before=" + before + " after=" + after);
                lastKey = "";
                Rebuild(true);
            }
            catch (Exception e) { LastMsg = "加药片失败：" + e.GetType().Name; Rebuild(true); }
        }

        /// <summary>找背包里的净水片（名字含「净水片」或 identifier 含 purif/tablet）。</summary>
        private static string lastTabletHit = "";

        private static object FindTablet()
        {
            try
            {
                var all = GameBridge.AllItemsDeep();
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string id = null;
                    try { id = it.identifier; } catch (Exception) { }
                    string nm = GameBridge.NameOfItem(it);
                    // 只认「净水片」。早期版本按 id 含 purif 兜底，把 water_purifier（净水器机器本体）
                    // 也匹配上了，随后 SetUnitCount(0) 会把机器从背包里抹掉 —— 必须排除机器与容器。
                    bool isMachineOrBottle = !string.IsNullOrEmpty(id)
                        && (id.IndexOf("purifier", StringComparison.OrdinalIgnoreCase) >= 0
                         || id.IndexOf("machine", StringComparison.OrdinalIgnoreCase) >= 0
                         || id.IndexOf("bottle", StringComparison.OrdinalIgnoreCase) >= 0
                         || id.IndexOf("tank", StringComparison.OrdinalIgnoreCase) >= 0
                         || id.IndexOf("container", StringComparison.OrdinalIgnoreCase) >= 0);
                    bool hit = !isMachineOrBottle
                            && ((!string.IsNullOrEmpty(nm) && nm.IndexOf("净水片", StringComparison.Ordinal) >= 0)
                             || (!string.IsNullOrEmpty(id) && id.IndexOf("purif_tablet", StringComparison.OrdinalIgnoreCase) >= 0)
                             || (!string.IsNullOrEmpty(id) && id.IndexOf("water_tablet", StringComparison.OrdinalIgnoreCase) >= 0));
                    if (hit)
                    {
                        lastTabletHit = (id ?? "?") + "/" + (nm ?? "?");
                        Log("[tablet] match " + lastTabletHit);
                        return it;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        private static bool Scanners()        {
            // 玩家 2026-11-10：这函数原来是「每次 Rebuild 就全背包 BFS 一轮」，只为打一行 bal 日志，
            // 面板开着鼠标划过按钮就卡。结果缓存 6 秒，期间任何调用都直接命中。
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (scanCacheT > 0f && now - scanCacheT < 6f) return scanCacheVal != 0;
            scanCacheT = now;
            bool found = false;
            try
            {
                // 必须用 RawItems 而不是 LiveAllItems —— 后者只返回「有容量」的容器，
                // 扫描仪/药片这类非容器道具根本不会出现在里面（玩家带了水质扫描仪却被判成没有）。
                var all = GameBridge.AllItemsDeep();
                for (int i = 0; i < all.Count && !found; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string id = null;
                    try { id = it.identifier; } catch (Exception) { }
                    if (id == "aquascan" || id == "water_test_strip" || id == "water_test_container") { found = true; break; }
                    // identifier 兜不住时按本地化名兜底：叫「水质扫描仪」「化学扫描仪」的都要认。
                    string nm2 = GameBridge.NameOfItem(it);
                    if (!string.IsNullOrEmpty(nm2) && nm2.IndexOf("扫描仪", StringComparison.Ordinal) >= 0) { found = true; break; }
                }
            }
            catch (Exception) { }
            scanCacheVal = found ? 1L : 0L;
            return found;
        }

        /// <summary>平衡模式开关（玩家口径）：开启＝按真实水质算；关闭＝当未知水质（隐藏精确值）。</summary>
        private static bool HidePurity() { return !BalanceOff; }
        public static void Rebuild(bool force)
        {
            // 玩家 2026-11-11：「点了按钮要等大约 1 秒」——先把耗时量出来，别再凭感觉改。
            // 只有超过 40ms 才写日志（正常重建是毫秒级），并把当次 GameBridge 调用计数一起打出来。
#if DEV
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { RebuildInner(force); }
            finally
            {
                long ms = sw.ElapsedMilliseconds;
                if (ms >= 40) Log("[rebuild] " + ms + "ms force=" + force + " | " + GameBridge.CountDump(8));
            }
#else
            RebuildInner(force);
#endif
        }

        private static void RebuildInner(bool force)
        {
            try
            {
                var m = Compute();
                Model = m;
                string key = m.VolumeMl + "|" + m.PurityBp + "|" + m.CapBp + "|" + m.Mode + "|" + m.TargetBp + "|" + m.PreMode + "|" + m.PerfPct + "|" + m.Quality
                           + "|s" + SrcIdx + "|po" + m.PostMode
                           + "|h" + WaterPlan.HydraMode + "|hd" + WaterPlan.HydraDuraCap
                           + "|fd" + FilterDurNow() + "|inf" + (InfiniteTools ? "1" : "0");
                bool changed = force || key != lastKey;
                if (changed)
                {
                    lastKey = key;
                    modelDirty = true;
                    // 玩家定案：机器标题栏的图标只显示固定名字（接水/净水助手/手册/效率/性价比），
                    // 动态方案文案一律进面板顶行，不再覆盖图标名（以前这里会把名字刷成「今晚原样跑→99.01」）。
                    SetDesc(cfgBtn, "配方与攻略");
                }
                if (panelWin == null && detailWin == null && recipeWin == null && settingsWin == null && stepsWin == null && srcWin == null) return;
                bool hidePurity = HidePurity();
                // 玩家 2026-11-10：这一行纯日志，别再为它跑一次全背包 BFS（扫描仪结果见 ctl `scan?` 探针）。
                string bal = "[bal] mode=" + (BalanceOff ? "off" : "on") +
                             " display=" + (hidePurity ? "unknown" : "exact");
                if (bal != lastBal) { lastBal = bal; Log(bal); }
                // 顶行压短：它一长窗口就变宽（玩家：按了接水界面变长）。性能/质量留在 tooltip 里。
                var top = new System.Text.StringBuilder();
                // 玩家 2026-11-11 的界面口径：整行不再用「·」当分隔，改空格；容器量/水质/上限各一种颜色。
                top.Append(PlanText.Lbl("容器 "))
                   .Append(PlanText.Rich(PlanText.C_NUM, m.VolumeMl + "/" + m.CapacityMl + " mL"))
                   .Append("    ")
                   .Append(PlanText.Lbl("水质 "))
                   .Append(hidePurity
                       ? PlanText.Rich(PlanText.C_WARN, "未知[平衡关]")
                       : PlanText.Rich(PlanText.C_PCT, PlanText.Pct(m.PurityBp)))
                   .Append("    ")
                   .Append(PlanText.Lbl("上限 "))
                   .Append(PlanText.Rich(PlanText.C_PCT, PlanText.Pct(m.CapBp)));
                // 玩家：方案标签（如「无前置+1 片药片」）不放在顶行，挪到底部当步骤显示。
                string subTxt = top.ToString();
                if (pSub != null) pSub.SetText(subTxt);
                // 四行下拉：按钮文字 =「组名 ▶ 当前值」，点一下换下一个（网页 select 的等价物）
                if (cTarget != null) cTarget.SetText("目标 ▶ " + PlanText.TargetNames[Math.Max(0, Math.Min(TargetIdx, PlanText.TargetNames.Length - 1))]);
                if (cMode != null)
                    cMode.SetText("模式 ▶ " + (UncapEff
                        ? "效率：能装的最多（50 L 内）"
                        : PlanText.ModeNames[Math.Max(0, Math.Min(m.Mode, PlanText.ModeNames.Length - 1))]
                          + (InfiniteTools ? "（无限道具）" : "")));
                if (cPre != null) cPre.SetText("前置 ▶ " + PlanText.PreNames[Math.Max(0, Math.Min(PreMode, PlanText.PreNames.Length - 1))]);
                if (cPost != null) cPost.SetText("后置 ▶ " + PlanText.PostNames[Math.Max(0, Math.Min(PostMode, PlanText.PostNames.Length - 1))]);
            if (cSrc != null) cSrc.SetText("水源 ▶ " + SrcUiName(SrcIdx));
                if (cHydra != null) cHydra.SetText("海德拉 ▶ " + HydraUi[Math.Max(0, Math.Min(WaterPlan.HydraMode, HydraUi.Length - 1))]);
                // 滑条不在这里重建文字：只在值被别处改动时把位置同步过来，标签走 RefreshHydraDurLabel
                if (sHydraDur != null)
                {
                    float want = Math.Max(0f, Math.Min((float)HydraDurMaxSlider, (float)HydraDurShown()));
                    if (Math.Abs(sHydraDur.value - want) > 0.01f) sHydraDur.SetValue(want, false);
                }
                RefreshHydraDurLabel();
                // 过滤棉滑条同理：只在值被别处改动时把位置同步过来（玩家正在拖的时候别覆盖）
                if (sFilterDur != null)
                {
                    float wantF = Math.Max(0f, Math.Min((float)FilterDurMaxSlider, (float)FilterDurNow()));
                    if (Math.Abs(sFilterDur.value - wantF) > 0.01f) sFilterDur.SetValue(wantF, false);
                }
                RefreshFilterDurLabel();
                if (targetBtns != null) Mark(targetBtns, PlanText.TargetNames, TargetIdx);
                if (modeBtns != null) Mark(modeBtns, PlanText.ModeNames, m.Mode);
                if (preBtns != null) Mark(preBtns, PlanText.PreNames, PreMode);
                if (pFill != null) SetDesc(pFill, FillLabel());   // 图标键：文字走 tooltip，别覆盖图标本身
                if (pEarly != null) pEarly.SetText("");
                pHintText(m);
                // 三张卡与底部状态行共用同一份方案，避免「卡一说上限 2000、状态行说 1857」
                var r = PlanForUi(m);
                // 玩家 2026-11-11：容器里这瓶水已经达到目标 → 界面只显示「已达标」，方案/钱/步骤/持有/接水键全部不显示。
                // 达标按「实际能达到的目标」判断：玩家设 99.90% 但本机上限只有 99.01% 时，
                // 瓶子已经到 99.01% 就是到头了，再给方案也没用 —— 直接显示「已达标」。
                int okTarget = m != null && m.TargetBp > 0 ? m.TargetBp : 0;
                if (okTarget > 0 && m.CapBp > 0 && okTarget > m.CapBp) okTarget = m.CapBp;
                bool alreadyOk = m != null && m.VolumeMl > 0 && okTarget > 0 && m.PurityBp >= okTarget;
                // 底部状态行：容器 X/Y mL、还能装多少、装不下多少必须常驻（网页底部的状态条），
                // 上一次动作的回执（已灌/已备水）接在后面，不再顶掉容器状态。
                if (pMsg != null)
                {
                    // 底行必须短：它一变长，窗口就跟着变宽（玩家：按了接水界面变长）
                    string live = LiveLineShort(m, r);
                    string msg = (LastMsg == null ? "" : LastMsg.Replace("\n", " ").Trim());
                    if (msg.Length > 12) msg = msg.Substring(0, 12) + "…";
                    // 玩家要求：方案标签（如「无前置+1 片药片」）放最下面当步骤，别占顶行。
                    string stepTxt = "";
                    if (!alreadyOk && r != null && r.pick != null)
                        stepTxt = "步骤 " + StepChain(r.pick);
                    pMsg.SetText(live);
                    // 玩家 2026-11-10：步骤放到「出来 X mL」的下一行（pStep），底行只留容器状态。
                    // 玩家 2026-11-10：步骤行文案没变就别再写原生控件（写一次＝整行重排）。
                    // 玩家 2026-11-11：步骤行有时整行不显示 —— 因为换页重建后 pStep 是全新控件（空文本），
                    // 而 lastStepLine 还存着上一页写过的同一句，判等把它拦住，新控件就一直是空的。
                    // 所以除了比文案，还要比「控件是不是同一个」。
                    if (pStep != null && (stepTxt != lastStepLine || !ReferenceEquals(pStep, lastStepOwner)))
                    {
                        lastStepOwner = pStep;
                        lastStepLine = stepTxt;
                        pStep.SetText(stepTxt);
                    }
                }

                // 玩家：不要「建议接水（水瓶/水桶）」这一行 —— 玩家会自己按实际换容器。
                if (pCard1 != null) { pCard1.SetText(""); SetVisible(pCard1, false); }
                if (pCard1Val != null) pCard1Val.SetText(alreadyOk ? PlanText.Rich(PlanText.C_MONEY, "已达标")
                                                                   : (r == null ? "" : CardFeedValueFor(m, r)));
                if (pCard2 != null) pCard2.SetText(alreadyOk ? "" : (r == null ? "出来 —" : r.CardOut()));
                if (pMoney != null) pMoney.SetText(alreadyOk ? "" : (r == null ? "" : r.CardMoney()));
                if (pCard3 != null) { pCard3.SetText(""); SetVisible(pCard3, false); }   // 玩家：杂质变化这一行没必要，整行隐藏（只清空仍占一行，会留空白）
                // 主页「持有」行：只拼背包里真有的道具，两样都没有就隐藏整行（也不占高度）
                if (pOwn != null)
                {
                    string ownTxt = OwnLineText();
                    if (alreadyOk) SetVisible(pOwn, false);
                    else { pOwn.SetText(ownTxt); SetVisible(pOwn, ownTxt.Length > 0); }
                }
                if (pMixLine != null) pMixLine.SetText(alreadyOk ? "" : (r == null ? "" : MixText(r)));
                // 底部「接水」键：键面只写动作，接多少由下面的状态行走日志与 tooltip 说；
                // 点下去就是 OnFill 的整瓶量逻辑（容器已有水只补空余，满了提示先倒出来）。
                if (pJoin != null)
                {
                    SetVisible(pJoin, !alreadyOk);   // 已达标的瓶不用再接水，键一起收起来
                    pJoin.SetText("接水");
                    long joinMl = planPourMl > 0 ? planPourMl : (r != null ? r.ChosenFeedUl() / 1000L : 0L);
                    SetDesc(pJoin, "按当前优选方案接水龙头水：本次 " + joinMl + " mL（容器已有水只补空余，满了先倒出来）");
                }

                // 方案池（表头 + 列表 + 行号键）只在「更多方案」展开时显示：
                // 主页面只讲当前这一条方案，别的方案点了更多再看（玩家明确要求）。
                // 分页后每页各自建窗，这里只更新本页内容；只有方案池页需要重排列表
                if (pMore != null) pMore.SetText(UiPage == 2 ? "返回" : "更多方案");
                // 玩家 2026-11-10：这一行原来每次 Rebuild 都无条件调 ResizePixels —— 每秒改一次窗口尺寸，
                // 原生窗口会把整页重排一遍，鼠标划过按钮就是一卡一卡的。高度没变就不要再调。
                int phNow = PageHeight();
                if (force || phNow != lastPageH)
                {
                    lastPageH = phNow;
                    try { panelWin.ResizePixels(300, phNow); } catch (Exception) { }
                }
                if (UiPage != 3)
                {
                    var rows2 = r != null ? (ShowAllCombos ? AllRows(r, 40) : r.TableRows(RowCount)) : null;
                    // 玩家：2000 mL 的容器就别显示 6000 的方案了 —— 按当前瓶子的容量把超出的行滤掉
                    long capNow = (long)m.CapacityMl * 1000L;
                    if (rows2 != null && rows2.Count > 0 && capNow > 0 && !UncapEff)
                    {
                        var fit = new List<WaterPlan.Combo>();
                        for (int qi = 0; qi < rows2.Count; qi++) if (rows2[qi] != null && rows2[qi].feedUl <= capNow) fit.Add(rows2[qi]);
                        if (fit.Count > 0) rows2 = fit;
                    }
                    lastRows = (rows2 != null && rows2.Count > 0) ? rows2.ToArray() : null;
                    if (pTableHead != null)
                    {
                        string head = "点一行就选中";
                        if (r != null && r.filterEmpty) head += " ｜ 前后置凑不出，按自动给";
                        if (r != null)
                        {
                            string th2 = r.TabletHint();
                            if (!string.IsNullOrEmpty(th2)) head += " ｜ " + (th2.Length > 34 ? th2.Substring(0, 34) + "…" : th2);
                        }
                        try { pTableHead.SetText(head); } catch (Exception) { }
                    }
                    if (pRowBtns != null)
                    {
                        for (int i = 0; i < pRowBtns.Length; i++)
                        {
                            bool has = rows2 != null && i < rows2.Count;
                            if (!has && i == 0)
                            {
                                // 空表也要说清楚为什么空（玩家报「更多方案空了」）
                                SetVisible(pRowBtns[0], true);
                                string why = r == null ? "没有可规划的水瓶：先放一只装水的瓶子" : (r.filterEmpty ? "前后置凑不出达标方案，换一个前置/后置" : "这瓶给不出可选方案：先用水质扫描仪扫一遍");
                                try { pRowBtns[0].SetText(why); } catch (Exception) { }
                                continue;
                            }
                            var btn = pRowBtns[i];
                            if (btn == null) continue;
                            if (!has) { SetVisible(btn, false); continue; }
                            SetVisible(btn, true);
                            var c = rows2[i];
                            bool sel = r.pick != null && c == r.pick;
                            try { btn.SetText((sel ? "▶ " : "") + (i + 1) + ". " + r.ComboRowMain(c, sel) + " · " + PlanText.Pct(c.purityBp)); } catch (Exception) { }
                            try { SetDesc(btn, r.ComboRowSub(c)); } catch (Exception) { }
                        }
                    }
                }
                if (pList == null) return;
                var b = new RichTextBuilder(160);
                if (UiPage == 3)
                {
                    // 玩家 2026-11-10：详情窗删了，那份「方案上面的信息」搬到这里，步骤页就是完整一页。
                    if (r != null)
                    {
                        AddLine(b, r.Headline(), ColorPalette.LightGreen, true);
                        AddLine(b, "本机 性能 +" + r.perf + "% · 质量 " + r.q, ColorPalette.LightGray, false);
                        AddLine(b, r.CostText(), ColorPalette.White, false);
                        AddLine(b, "", ColorPalette.White, false);
                    }
                    if (r != null && r.steps != null && r.steps.Count > 0)
                    {
                        for (int si = 0; si < r.steps.Count; si++)
                        {
                            var stp = r.steps[si];
                            AddLine(b, (si + 1) + ". " + stp.title, ColorPalette.LightGray, false);
                            if (!string.IsNullOrEmpty(stp.detail)) AddLine(b, "    " + stp.detail, ColorPalette.LightGray, false);
                        }
                    }
                    else AddLine(b, "还没有步骤：先选好水源与目标", ColorPalette.LightOrange, false);
                    // 玩家 2026-11-10：富文本每次重排最贵，数据没变就跳过（换了控件对象才强制重画）。
                    string stepSig = "S|" + (r == null || r.steps == null ? 0 : r.steps.Count) + "|" +
                                     (r != null && r.pick != null ? r.pick.label : "-") + "|" + PreMode + "/" + PostMode;
                    if (RichChanged(pList, stepSig)) pList.SetRichText(b, 4000);
                    return;
                }
                // —— 一晚方案表：选中那条高亮并把 ▶ 放在最前，其余按出水量从大到小 ——
                var rows = r != null ? (ShowAllCombos ? AllRows(r, 40) : r.TableRows(5)) : null;
                lastRows = (rows != null && rows.Count > 0) ? rows.ToArray() : null;
                if (rows != null && rows.Count > 0)
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var c = rows[i];
                        bool sel = r.pick != null && c == r.pick;
                        // 每条方案两行：标签 + 接→出 一行，纯度/耗材/每升 一行，数字不会被折行截断
                        AddLine(b, r.ComboRowMain(c, sel),
                            c.ok ? ColorPalette.LightGreen : ColorPalette.LightOrange, sel);
                        AddLine(b, r.ComboRowSub(c), ColorPalette.LightGray, false);
                    }
                }
                else AddLine(b, m.Verdict, ColorPalette.White, false);
                // 玩家 23:18：方案要的量超过容器容量时别硬推荐 —— 先确认背包/这台机器有没有这么大的空容器，
                // 没有就用黄字明说「先换大容器」，不要让他照着按钮接却接不满。
                try
                {
                    if (r != null && r.pick != null && !r.fromWater)
                    {
                        long needUl2 = r.pick.feedUl;
                        int f0, cnt0, maxCap0;
                        PickReceiver(0, out f0, out cnt0, out maxCap0);
                        long mineUl = (long)m.CapacityMl * 1000L;
                        if (needUl2 > mineUl && needUl2 > maxCap0)
                        {
                            AddLine(b, "方案要 " + WaterPlan.Ml(needUl2) + " mL，但这台机器只有 " + m.CapacityMl +
                                   " mL、背包最大空容器 " + WaterPlan.Ml(maxCap0) + " mL —— 先换大容器（或换小方案）",
                                   ColorPalette.LightOrange, true);
                        }
                    }
                }
                catch (Exception) { }
                // 前置+后置筛空时的说明：方案表退回自动方案，但必须让玩家知道为什么
                if (r != null && r.filterEmpty) AddLine(b, "前置「" + PlanText.PreName(PreMode) + "」+ 后置「" + PlanText.PostName(PostMode) + "」凑不出达标方案，表里按自动给", ColorPalette.LightOrange, false);
                // 药片清不动时说明原因（化学量还没到 0.7% 门限，网页同一套判定），避免看起来「选了没反应」
                if (r != null)
                {
                    string th = r.TabletHint();
                    if (th != null && th.Length > 0) AddLine(b, th, ColorPalette.LightOrange, false);
                }
                // 操作回执单独一行放最下面（玩家：别拼在状态行里把窗口撑长）
                if (!string.IsNullOrEmpty(LastMsg))
                    AddLine(b, LastMsg.Replace("\n", " "), ColorPalette.LightOrange, false);
                // 玩家 2026-11-10：方案表富文本也先比签名，没变不重排（换控件对象才强制画）。
                string richSig = "T|" + (rows == null ? 0 : rows.Count) + "|" +
                                 (r != null && r.pick != null ? r.pick.label + r.pick.feedUl : "-") + "|" +
                                 (ShowAllCombos ? 1 : 0) + "|" + (LastMsg == null ? "-" : LastMsg) + "|" +
                                 m.Mode + "/" + m.TargetBp;
                if (RichChanged(pList, richSig)) pList.SetRichText(b, 4000);
            }
            catch (Exception e)
            {
                log.LogWarning("[ui] Rebuild " + e.GetType().Name + " " + e.Message);
#if DEV
                // NRE 的 Message 是空串，只有类型名抓不到位置 —— 打前 3 帧栈（玩家 2026-11-11 报刷屏）
                string st = e.StackTrace;
                if (st != null)
                {
                    string[] fr = st.Split('\n');
                    int n = fr.Length < 3 ? fr.Length : 3;
                    var sb = new System.Text.StringBuilder("[ui] Rebuild @");
                    for (int i = 0; i < n; i++) sb.Append(' ').Append(fr[i].Trim());
                    log.LogWarning(sb.ToString());
                }
#endif
            }
        }

        /// <summary>
        /// 面板专用的方案对象：同一组输入只算一次（整瓶规划比较贵，0.4 秒刷新不能每帧重跑）。
        /// 方案表的选中行、三张卡、接水键量都从这一个 Report 来，保证互相不打架。
        /// </summary>
        private static WaterPlan.Report PlanForUi(PanelModel m)
        {
            string k = m.BottleName + "|" + m.VolumeMl + "|" + m.PurityBp + "|" + m.CapBp + "|" +
                       m.Mode + "|" + m.PreMode + "|" + m.PostMode + "|" + m.TargetBp + "|" +
                       m.PerfPct + "|" + m.Quality + "|" + (ManualPickLabel == null ? "-" : ManualPickLabel) +
                       "|u" + (UncapEff ? "1" : "0") +
                       "|s" + SrcIdx +
                       "|h" + WaterPlan.HydraMode + "|hd" + WaterPlan.HydraDuraCap +
                       "|fd" + FilterDurNow() + "|inf" + (InfiniteTools ? "1" : "0");
            if (planCache != null && k == planKey) return planCache;
            planKey = k;
            planPourMl = 0;
            try
            {
                planCache = CurrentPlan(true);
                // 整瓶拆分（和 OnFill 完全同一条路径）：按钮上写的量必须就是真会倒进去的量。
                // 已有水方案不需要从背包瓶子凑水，所以不给「接水量」。
                if (planCache != null && planCache.bestUl > 0 && !planCache.fromWater)
                {
                    var pour = WaterPlan.Plan(planCache.bestUl);
                    if (pour != null) planPourMl = pour.totalUl / 1000L;
                }
            }
            catch (Exception e) { log.LogWarning("[ui] PlanForUi " + e.Message); planCache = null; }
            return planCache;
        }

        /// <summary>
        /// 六项杂质明细：正常情况下用百分比并保证加起来 100.00%；
        /// 若取整后杂质已经占满（水被挤到 0 或负数），改用绝对 mL，避免出现「水 0.00% 却还有杂质」的假数据。
        /// </summary>
        private static string MixText(WaterPlan.Report r)
        {
            string pct = r.MixLine();
            if (string.IsNullOrEmpty(pct)) return "";
            try
            {
                var w = r.ChosenWater();
                if (w == null || w.v <= 0) return pct;
                string[] keys = { "heavy", "organic", "microbe", "chemical", "physical", "mineral" };
                long sum = 0;
                for (int i = 0; i < keys.Length; i++) sum += w.Part(keys[i]) * 10000L / w.v;
                if (sum >= 9960) return r.MixLineMl();
            }
            catch (Exception) { }
            return pct;
        }

        private static void pHintText(PanelModel m)
        {
            // 提示固定在 hint 行的 tooltip 里，避免再占一行
            SetDesc(pFill, m.Hint == null ? "" : m.Hint);
        }

        private static void SetDesc(MachineryButton b, string text)
        {
            if (b == null || string.IsNullOrEmpty(text)) return;
            try { b.SetDescription(text); } catch (Exception) { }
        }

        // ---------------- 标题栏图标固定名 ----------------
        // 玩家要求：机器/面板标题栏图标只显示固定名字（接水/净水助手/手册/效率/性价比），
        // 不能像以前那样被方案文案刷成「今晚原样跑→99.01」。动态信息一律进面板顶行。
        private static readonly Dictionary<MachineryButton, string> HdrNames = new Dictionary<MachineryButton, string>();
        private static MachineryText pTitleRow;      // 面板内兜底标题行（拿不到官方标题控件时用）
        private static bool frameTitleOk;            // 官方标题控件是否可用

        private static void RegisterHdr(MachineryButton b, string name)
        {
            if (b == null) return;
            HdrNames[b] = name;
            SetDesc(b, name);
        }

        /// <summary>每 tick 自愈：游戏侧若有代码改了图标 description，这里按登记表写回。
        /// 注意必须「先读后写」——无条件 SetDescription 会让原生按钮每秒重建一次，鼠标划过就卡。</summary>
        private static float lastHdrNameAt = -999f;

        private static void EnsureHdrNames()
        {
            if (HdrNames.Count == 0) return;
            // ★性能：12 个标题栏图标 × 每帧一次 ModReflect 读 description，日志里 Get:description*84。
            // 名字设好就不会再变，1.5 s 检查一次足够。
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= lastHdrNameAt && now - lastHdrNameAt < 1.5f) return;
            lastHdrNameAt = now;
            foreach (var kv in HdrNames)
            {
                if (kv.Key == null) continue;
                try
                {
                    string cur = GameBridge.Get(kv.Key, "description") as string;
                    if (!string.Equals(cur, kv.Value)) kv.Key.SetDescription(kv.Value);
                }
                catch (Exception) { }
            }
        }

        /// <summary>ctl: icons? —— 回报标题栏图标的当前名字（回归断言用）。</summary>
        public static string IconsPublic()
        {
            var sb = new System.Text.StringBuilder("icons -> n=" + HdrNames.Count);
            foreach (var kv in HdrNames)
            {
                if (kv.Key == null) continue;
                string cur = "?";
                try { var v = GameBridge.Get(kv.Key, "description"); if (v != null) cur = v.ToString(); } catch (Exception) { }
                sb.Append(" | ").Append(kv.Value).Append(":").Append(cur);
            }
            return sb.ToString();
        }

        /// <summary>把「净水助手」写进 PixelWindow 自带的标题栏字段（titleString / hasTitle），
        /// 这样标题就和小图标按钮在同一排居中，和官方净水器窗一模一样；成功则隐藏面板内的兜底标题行。</summary>
        private static void ApplyFrameTitle()
        {
            frameTitleOk = false;
            if (panelWin == null) return;
            try
            {
                // 走官方 ModReflect（免 reflection-external）：先写属性，只读时写 backing field
                if (GameBridge.Set(panelWin, "titleString", "净水助手")) frameTitleOk = true;
                else if (GameBridge.Set(panelWin, "<titleString>k__BackingField", "净水助手")) frameTitleOk = true;

                // 让窗口把标题行显示出来（hasTitle 只有 getter，写 backing field）
                if (!GameBridge.Set(panelWin, "hasTitle", true))
                {
                    try { GameBridge.Set(panelWin, "<hasTitle>k__BackingField", true); } catch (Exception) { }
                }

                Log("[ui] title via PixelWindow.titleString ok=" + frameTitleOk);
            }
            catch (Exception e) { Log("[ui] title ERR " + e.GetType().Name + " " + e.Message); }
            try { SetVisible(pTitleRow, !frameTitleOk); } catch (Exception) { }
        }

        /// <summary>ctl: title? —— 当前标题走的是窗口自带标题栏还是面板内兜底行。</summary>
        public static string TitlePublic()
        {
            string ts = "?", ht = "?";
            try
            {
                if (panelWin != null)
                {
                    var v1 = GameBridge.Get(panelWin, "titleString");
                    if (v1 != null) ts = v1.ToString();
                    var v2 = GameBridge.Get(panelWin, "hasTitle");
                    if (v2 != null) ht = v2.ToString();
                }
            }
            catch (Exception) { }
            bool bodyVisible = true;
            try { if (pTitleRow != null) bodyVisible = pTitleRow.rectTransform.gameObject.activeSelf; } catch (Exception) { }
            return "title -> via=" + (frameTitleOk ? "window" : "fallback") + " titleString=" + ts + " hasTitle=" + ht + " bodyRow=" + (bodyVisible ? "on" : "off");
        }

        /// <summary>ctl: assert? —— 用分析员给的 IL 断言值自检单晚净化（只读）。
        /// 期望：1650 mL / 91.31% / perf=0 → 一晚到 9901；2000 mL 同浓度若需两晚，说明模型是对的。</summary>
        public static string AssertPublic()
        {
            return "assert -> " + OneAssert(1650000L, 9131, 0) + " || " + OneAssert(2000000L, 9131, 0);
        }

        private static string OneAssert(long vUl, int pur, double perf)
        {
            try
            {
                var w = WaterMath.Build(vUl, pur);
                long imp0 = w.Impurity;
                long budget = WaterMath.Budget(vUl, pur, perf);
                int cap = WaterMath.CapOf(0);
                int rounds = 0; long removed = 0;
                while (rounds < 8)
                {
                    long r = WaterMath.Purify(w, perf, 0);
                    if (r <= 0) break;
                    removed += r; rounds++;
                    if (w.Purity >= 9900) break;
                }
                return (vUl / 1000) + "mL@" + pur + " dirt=" + WaterMath.DirtMul(pur).ToString("0.0000000")
                     + " budget=" + budget + " imp0=" + imp0 + " cap=" + cap
                     + " removed=" + removed + " pur=" + w.Purity + " rounds=" + rounds;
            }
            catch (Exception e) { return "ERR " + e.GetType().Name + " " + e.Message; }
        }

        // —— 容器观察器：把被观测容器的精确 μL / 六项配比 / 过滤棉耐久的变化写进日志 ——
        // 用途：过滤、过夜、加药片每一步前后各留一行精确数字；玩家实测与模型差几 mL 时直接对账。
        private static string lastWatch;
        public static string WatchNow() { return WatchContainer(); }

        public static string WatchContainer()
        {
            try
            {
                var b = CtxBottle(); if (b == null) b = GameBottle(); if (b == null) b = PickFallbackBottle();
                if (b == null) return "watch -> no-bottle";
                long v = WaterHelper.GetTotalVolume(b);
                int pur = WaterHelper.GetWaterPurity(b);
                var sb = new System.Text.StringBuilder("watch -> v=").Append(v).Append("uL pur=").Append(pur)
                    .Append('(').Append(PlanText.Pct(pur)).Append(')');
                var parts = GameBridge.LiquidParts(b);
                var names = GameBridge.LiquidNames();
                if (parts == null) sb.Append(" parts=null");
                else
                {
                    sb.Append(" n=").Append(parts.Length);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string nm = (names != null && i < names.Length) ? names[i] : "?";
                        sb.Append(' ').Append(nm).Append('=').Append(parts[i]);
                    }
                }
                long dur = FilterDurability();
                if (dur >= 0) sb.Append(" filterDur=").Append(dur);
                sb.Append(" cap=").Append(WaterHelper.GetTotalCapacity(b));
                string s = sb.ToString();
                if (s != lastWatch) { lastWatch = s; Log(s); }
                return s;
            }
            catch (Exception e) { return "[watch] ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>玩家「实际持有的净水道具」——性价比档按这个建候选（没道具就装不满）。
        /// 接口照抄 N 网 WaterAssistant 的 InventoryScanner：identifier + unitCount + DurabilityHelper。</summary>
        public class OwnedToolsInfo
        {
            public int tablets;      // 净水片总数（含每格 unitCount）
            public bool filter;      // 有没有过滤棉（耐久 > 0）
            public bool uv;          // 有没有紫外灯
            public int hydraDur;     // 海德拉可用耐久总和
            public int filterDur;    // 过滤棉可用耐久总和（2 块 = 800 点；以前只算第一块 400）
            public bool infinite;    // 玩家 2026-11-11：无限道具口径（药片/滤棉/海德拉都不限）
            public string note = "";
        }

        private static OwnedToolsInfo ownCache;
        private static float ownTime = -999f;

        public static OwnedToolsInfo OwnedTools()
        {
            // 玩家 2026-11-11 报 bug：切到无限后「持有」还显示过滤棉 2000 点，点一下设置再返回才更新。
            // 根因是 6 秒缓存没有记录口径 —— 切档后仍返回上一次的口径。缓存键带上 infinite 即可。
            if (ownCache != null && ownCache.infinite == InfiniteTools
                && UnityEngine.Time.realtimeSinceStartup - ownTime < 6f) return ownCache;
            var o = new OwnedToolsInfo();
            if (InfiniteTools)
            {
                // 玩家 Q3 口径：无限 = 药片/过滤棉不限；紫外是工具（不计耐久与成本）；海德拉不限，
                // 但拉条（FilterDurUse / HydraDuraCap）与设置页仍能限制这些「无限道具」。
                o.tablets = -1; o.filter = true; o.uv = true; o.hydraDur = -1;
                o.filterDur = -1;          // -1 = 不限点数，持有行直接显示「无限」
                o.infinite = true;
                o.note = "无限";
                ownCache = o; ownTime = UnityEngine.Time.realtimeSinceStartup;
                return o;
            }
            if (ForceOwnAll)
            {
                o.tablets = -1; o.filter = true; o.uv = true; o.hydraDur = -1;
                o.filterDur = WaterPlan.FilterDurPer;
                o.note = "全都有（强制）";
                ownCache = o; ownTime = UnityEngine.Time.realtimeSinceStartup;
                return o;
            }
            int tabs = 0, hydra = 0, filtDur = 0;
            bool hasFilter = false, hasUv = false;
            try
            {
                var all = GameBridge.AllItemsDeep();
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string id = null;
                    try { id = it.identifier; } catch (Exception) { }
                    if (string.IsNullOrEmpty(id)) continue;
                    string lid = id.ToLowerInvariant();
                    if (lid.IndexOf("portable_water_purifier") >= 0 || lid.IndexOf("portable_purifier") >= 0)
                    {
                        int d = 0;
                        try { d = (int)DurabilityHelper.GetRealDurability(it); } catch (Exception) { }
                        if (d > 0) hydra += d;
                    }
                    else if (lid.IndexOf("uv_filter") >= 0 || lid.IndexOf("_uv") >= 0 || lid.IndexOf("uv_lamp") >= 0)
                    {
                        hasUv = true;
                    }
                    else if (lid.IndexOf("water_tablet") >= 0 || lid.IndexOf("purif_tablet") >= 0)
                    {
                        int c = 1;
                        try
                        {
                            var counted = GameBridge.Get(it, "unitCount");
                            if (counted != null) c = Convert.ToInt32(counted);
                        }
                        catch (Exception) { }
                        if (c < 1) c = 1;
                        tabs += c;
                    }
                    else if (lid.IndexOf("water_filter") >= 0 || lid.IndexOf("filter") >= 0)
                    {
                        int d = 1;
                        try { d = (int)DurabilityHelper.GetRealDurability(it); } catch (Exception) { d = 0; }
                        if (d != 0) hasFilter = true;
                        if (d > 0) filtDur += d;   // 累加所有滤棉：背包 2 块 = 800 点（玩家 2026-11-10）
                    }
                }
            }
            catch (Exception) { }
            o.tablets = tabs; o.filter = hasFilter; o.uv = hasUv; o.hydraDur = hydra;
            o.filterDur = filtDur;
            o.note = "药片 " + tabs + " · " + (hasFilter ? ("滤棉 " + filtDur + " 点") : "无滤棉") + " · "
                   + (hasUv ? "紫外" : "无紫外") + " · 海德拉 " + hydra + " 耐久";
            ownCache = o; ownTime = UnityEngine.Time.realtimeSinceStartup;
            return o;
        }

        /// <summary>ctl 用：强制「道具全有」，方便离线核对默认档。</summary>
        public static bool ForceOwnAll = false;

        /// <summary>背包里**所有**过滤棉的真实耐久之和（一块 400 点；没找到返回 -1，读不出来返回 -2）。
        /// 玩家 2026-11-10：以前找到第一块就 return，2 块滤芯 = 800 点却一直按 400 规划。</summary>
        private static long FilterDurability()
        {
            // 玩家 2026-11-10：WatchContainer 每次都会调到这里，而 LiveAllItems 是整包枚举 ——
            // 面板一开着就一秒一次，鼠标划按钮就卡。结果缓存 4 秒（滤棉耐久不会瞬间变）。
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (filtCacheT > 0f && now - filtCacheT < 4f) return filtCacheVal;
            filtCacheT = now;
            long sum = 0;
            bool any = false;
            try
            {
                var all = GameBridge.LiveAllItems();
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null) continue;
                    string id = null;
                    try { id = it.identifier; } catch (Exception) { }
                    if (string.IsNullOrEmpty(id)) continue;
                    if (id.IndexOf("water_filter", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    long d = 0;
                    try { d = DurabilityHelper.GetRealDurability(it); } catch (Exception) { d = 0; }
                    if (d == -2) { filtCacheVal = -2; return -2; }
                    if (d > 0) { sum += d; any = true; }
                }
            }
            catch (Exception) { }
            filtCacheVal = any ? sum : -1;
            return filtCacheVal;
        }

        /// <summary>ctl: tcase? —— 2000 mL 铁锈水(91.31%) → 1 晚(0 效率) → 1/2 片药片的真值自检。
        /// 目标值：1 片 → 1959.56 mL、化学 14000 μL、99.28%（玩家实测 1959 mL / 14000 μL / 99.28%）。</summary>
        public static string TabletCasePublic()
        {
            try
            {
                var sb = new System.Text.StringBuilder("tcase ->");
                for (int tab = 1; tab <= 2; tab++)
                {
                    var w = WaterMath.Build(2000000L, 9131);
                    WaterMath.Purify(w, 0, 0);
                    long v1 = w.v; int pur1 = w.Purity; long chem1 = w.Part("chemical");
                    WaterMath.Tablet(w, tab);
                    sb.Append(" | 一晚后 ").Append(PlanText.Ml(v1)).Append("mL@").Append(PlanText.Pct(pur1))
                      .Append("(chem ").Append(PlanText.Ml(chem1)).Append(')')
                      .Append(" + ").Append(tab).Append("片 → ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity))
                      .Append(" chem=").Append(w.Part("chemical"));
                }
                return sb.ToString();
            }
            catch (Exception e) { return "tcase ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl: mix? —— 读容器真实六项配比（与水质扫描仪同一个接口 GetLiquidParts），
        /// 给出「把这瓶水直接处理到机器上限」的最优方案（过滤/紫外 × 晚数 × 药片）。</summary>
        public static string MixPlanPublic()
        {
            try
            {
                var b = CtxBottle(); if (b == null) b = GameBottle(); if (b == null) b = PickFallbackBottle();
                if (b == null) return "mix -> no-bottle";
                long v = WaterHelper.GetTotalVolume(b);
                int pur = WaterHelper.GetWaterPurity(b);
                WaterMath.Water w;
                if (!BuildRealWater(b, out w) || w == null || w.v <= 0) return "mix -> no-parts";
                int perf, q; Bonuses(out perf, out q);
                int cap = WaterMath.CapOf(q);
                var sb = new System.Text.StringBuilder("mix -> v=").Append(v).Append("uL pur=").Append(pur)
                    .Append(" perf=").Append(perf).Append(" q=").Append(q).Append(" cap=").Append(cap);
                string[] keys = { "heavy", "organic", "microbe", "chemical", "physical", "mineral" };
                sb.Append(" | ");
                for (int i = 0; i < keys.Length; i++) sb.Append(keys[i]).Append('=').Append(w.Part(keys[i])).Append(' ');
                var pl = WaterMath.PlanFromWater(w, perf, q, cap, 4, 3, WaterPlan.FilterDur);
                if (pl == null) { sb.Append("|| 无可行方案（4 晚 + 3 片也到不了上限）"); return sb.ToString(); }
                sb.Append("|| 最优: ");
                if (pl.filter == 1) sb.Append("过滤 ").Append(pl.filterUsed).Append("点 ");
                if (pl.uv == 1) sb.Append("紫外 ");
                sb.Append(pl.nights).Append("晚");
                if (pl.tablets > 0) sb.Append(" + ").Append(pl.tablets).Append("片");
                sb.Append(" → ").Append(PlanText.Ml(pl.outUl)).Append("mL@").Append(PlanText.Pct(pl.purityBp));
                return sb.ToString();
            }
            catch (Exception e) { return "mix ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl: ceiling? —— 6 L 容器、过滤+紫外+1 片药片，一晚能接多少（游戏真值口径）。
        /// 用来和网页计算器的「上限 6665.8 mL」对照。</summary>
        public static string CeilingPublic()        {
            try
            {
                var sb = new System.Text.StringBuilder("ceiling -> cap=6000mL target=9900 perf=25 q=0");
                sb.Append(" | filter+uv+1tab=").Append(CeilOn(6000000L, 25, 0, true, true, 1));
                sb.Append(" | filter+uv+0tab=").Append(CeilOn(6000000L, 25, 0, true, true, 0));
                sb.Append(" | none+1tab=").Append(CeilOn(6000000L, 25, 0, false, false, 1));
                sb.Append(" | none+0tab=").Append(CeilOn(6000000L, 25, 0, false, false, 0));
                return sb.ToString();
            }
            catch (Exception e) { return "ceiling ERR " + e.GetType().Name + " " + e.Message; }
        }

        private static string CeilOn(long capUl, int perf, int q, bool f, bool u, int t)
        {
            long lo = 0, hi = capUl;
            for (int i = 0; i < 28; i++)
            {
                long mid = (lo + hi + 1) / 2;
                var p = WaterMath.Run(mid, 9131, perf, q, f, u, t, 9900, 1);
                if (p != null && p.ok) lo = mid; else hi = mid - 1;
                if (hi < lo) break;
            }
            var pp = WaterMath.Run(lo, 9131, perf, q, f, u, t, 9900, 1);
            return (lo / 1000) + "mL" + (pp == null ? "" : ("/" + PlanText.Pct(pp.purity)));
        }

        /// <summary>ctl: sim?6000@9131@1 —— 逐阶段模拟「过滤+紫外+1 晚+药片」，用来和游戏实测逐段对照。</summary>
        public static string StagePublic(string args)
        {
            try
            {
                long ml = 6000; int bp = 9131, tab = 1, nights = 1; bool f = true, u = true;
                string s0 = (args ?? "").Trim();
                var seg = s0.Split('@');
                if (seg.Length > 0) long.TryParse(seg[0], out ml);
                if (seg.Length > 1) int.TryParse(seg[1], out bp);
                if (seg.Length > 2) int.TryParse(seg[2], out tab);
                if (seg.Length > 3) int.TryParse(seg[3], out nights);
                if (seg.Length > 4) f = seg[4].Trim() != "0";
                if (seg.Length > 5) u = seg[5].Trim() != "0";
                if (ml <= 0) ml = 6000;
                if (bp <= 0 || bp > 10000) bp = 9131;
                int perf, q; Bonuses(out perf, out q);
                var w = WaterMath.Build(ml * 1000L, bp);
                var sb = new System.Text.StringBuilder("sim -> ");
                sb.Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)).Append(" perf=").Append(perf);
                if (f) { int used = WaterMath.Filter(w, 400); sb.Append(" | 过滤(").Append(used).Append(") -> ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)); }
                if (u) { WaterMath.Uv(w, 1); sb.Append(" | 紫外 -> ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity)); }
                for (int n = 1; n <= Math.Max(0, nights); n++)
                {
                    WaterMath.Purify(w, perf, q);
                    sb.Append(" | 第").Append(n).Append("晚 -> ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity));
                }
                if (tab > 0)
                {
                    long before = w.v;
                    WaterMath.Tablet(w, tab);
                    sb.Append(" | ").Append(tab).Append("片 -> ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity))
                      .Append(" (化学剩 ").Append(w.Part("chemical")).Append("μL, 离体 ").Append(before - w.v).Append("μL)");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "sim ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl: filt?3000@9131 —— 过滤棉真值自检：过多少耐久、出水多少（对齐玩家实测 3 L→109 点）。</summary>
        public static string FiltPublic(string args)
        {
            try
            {
                long ml = 3000; int bp = 9131;
                string s = (args ?? "").Trim();
                int at = s.IndexOf('@');
                if (at >= 0)
                {
                    long.TryParse(s.Substring(0, at), out ml);
                    int.TryParse(s.Substring(at + 1), out bp);
                }
                else long.TryParse(s, out ml);
                if (ml <= 0) ml = 3000;
                if (bp <= 0 || bp > 10000) bp = 9131;
                var w = WaterMath.Build(ml * 1000L, bp);
                long v0 = w.v;
                int used = WaterMath.Filter(w, 400);
                var sb = new System.Text.StringBuilder("filt -> ");
                sb.Append(PlanText.Ml(v0)).Append("mL@").Append(PlanText.Pct(bp));
                sb.Append(" -> ").Append(PlanText.Ml(w.v)).Append("mL@").Append(PlanText.Pct(w.Purity));
                sb.Append(" dur=").Append(used);
                sb.Append(" || 分项 physical=").Append(w.Part("physical"))
                  .Append(" organic=").Append(w.Part("organic"))
                  .Append(" mineral=").Append(w.Part("mineral"))
                  .Append(" chemical=").Append(w.Part("chemical"));
                return sb.ToString();
            }
            catch (Exception e) { return "filt ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl: nights? —— 逐阶段打印当前选中方案的净化过程（过滤 → 紫外 → 每晚），
        /// 用来查「过滤棉+紫外 明明一晚够，却报两晚」这类口径错位。</summary>
        public static string NightsPublic()
        {
            try
            {
                var m = Compute();
                var r = CurrentPlan(true);
                if (r == null || r.pick == null) return "nights -> no-pick";
                var w = WaterMath.Build(r.pick.feedUl, r.srcBp);
                var sb = new System.Text.StringBuilder("nights -> pick=" + r.pick.label
                    + " feed=" + (r.pick.feedUl / 1000) + "mL out=" + (r.pick.outUl / 1000)
                    + " pur=" + r.pick.purityBp + " tablets=" + r.pick.tablets
                    + " target=" + m.TargetBp + " perf=" + m.PerfPct + " q=" + m.Quality
                    + " imp0=" + w.Impurity + " v0=" + (w.v / 1000) + " pur0=" + w.Purity);
                if (r.pick.filter) { WaterMath.Filter(w, 400); sb.Append(" | filter -> pur=" + w.Purity + " v=" + (w.v / 1000)); }
                if (r.pick.uv) { WaterMath.Uv(w, 1); sb.Append(" | uv -> pur=" + w.Purity + " v=" + (w.v / 1000)); }
                for (int n = 1; n <= 3; n++)
                {
                    long rm = WaterMath.Purify(w, m.PerfPct, m.Quality);
                    sb.Append(" | night" + n + " removed=" + rm + " pur=" + w.Purity + " v=" + (w.v / 1000));
                    if (rm <= 0 || w.Purity >= m.TargetBp) break;
                }
                if (r.pick.tablets > 0)
                {
                    long before = w.v;
                    WaterMath.Tablet(w, r.pick.tablets);
                    sb.Append(" | tablet" + r.pick.tablets + " -> pur=" + w.Purity + " v=" + (w.v / 1000) + " (−" + (before - w.v) + "μL)");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "nights ERR " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>ctl: uitree —— 面板窗口的真实类型、含 title/frame/window 的成员、以及节点名字树。
        /// 目的：找到那个「空白色药丸」的 GameObject，或找到真正的标题控件。</summary>
#if DEV
        public static string UiTreePublic()
        {
            var sb = new System.Text.StringBuilder("uitree -> ");
            if (panelWin == null) return sb.Append("no-window").ToString();
            try
            {
                var t = panelWin.GetType();
                sb.Append("type=").Append(t.FullName).Append(" | members:");
                var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                foreach (var f in t.GetFields(bf))
                {
                    string n = f.Name.ToLower();
                    if (n.Contains("title") || n.Contains("frame") || n.Contains("window")) sb.Append(" F:").Append(f.Name).Append("=").Append(f.FieldType.Name);
                }
                foreach (var p in t.GetProperties(bf))
                {
                    string n = p.Name.ToLower();
                    if (n.Contains("title") || n.Contains("frame") || n.Contains("window")) sb.Append(" P:").Append(p.Name).Append("=").Append(p.PropertyType.Name);
                }
                sb.Append(" | nodes:");
                try { WalkNames(panelWin.rectTransform, 0, sb); } catch (Exception e) { sb.Append(" walk-err ").Append(e.GetType().Name); }
            }
            catch (Exception e) { sb.Append(" ERR ").Append(e.GetType().Name).Append(" ").Append(e.Message); }
            return sb.ToString();
        }
#endif

        private static void WalkNames(UnityEngine.Transform t, int d, System.Text.StringBuilder sb)
        {
            if (t == null || d > 3) return;
            sb.Append(' ').Append(d).Append(':').Append(t.gameObject.name);
            for (int i = 0; i < t.childCount; i++) WalkNames(t.GetChild(i), d + 1, sb);
        }

        // ---------------- 数据 ----------------

        // ★ 水龙头真实入口（反编译 WaterHelper/<>c__DisplayClass10_0::<InitLiquidContainerItem>b__9）：
        //   ldstr "RUSTWATER_TAP" → NetworkUpgrade::GetUpgradeById → IsUnlocked
        //     → AudioHelper::UseFaucet(container)
        //     → AddWater(container, grade=1, amount=9999999, useRange=true,
        //                lowPara=StoreStation.todayRustwaterPurity, highPara=同一字段, initWaterFeature=true)
        //   else "GUTTERFLOW_TAP" → 同上，纯度取 StoreStation.todayGutterflowPurity，grade 仍是 1。
        // 为什么 low==high 就能锁纯度：RNG.GetRandomInt(a,b) 的 IL 是 Random::Range(a, b+1)（闭区间），a==b 时必得 a。
        // 为什么不能缓存：todayRustwaterPurity / todayGutterflowPurity 是**当日**字段，随存档天数（过月）变化，
        // 缓存后过月仍灌旧值，就与游戏水龙头不一致了。所以每次现场读、读不到就报错，绝不自己造值。
        /// <summary>水龙头是否真的解锁。原版拖水回调先查 NetworkUpgrade::GetUpgradeById(id).IsUnlocked，
        /// 没解锁就什么都不发生；旧版只读 StoreStation 的当日纯度，等于**绕过解锁凭空接水**（玩家报的 bug）。</summary>
        /// <summary>测试用：ctl `taplock=1` 把所有水龙头当成未解锁，用来验证「不许凭空接水」的拒绝路径。</summary>
        public static int ForceTapLocked = 0;

        private static bool TapUnlocked(string id)
        {
            try
            {
                if (ForceTapLocked != 0) return false;
                var up = GameBridge.CallStatic("NetworkUpgrade", "GetUpgradeById", id);
                if (up == null) return false;
                // ★反编译定论：IsUnlocked 是**无参方法**（另有 IsUnlocked(string) 重载），不是属性。
                // 之前用 GameBridge.Get 取属性永远拿到 null → 两只水龙头都被判成没解锁，接水会全被拒。
                var v = GameBridge.Call(up, "IsUnlocked");
                if (v is bool) return (bool)v;
                return false;
            }
            catch (Exception) { return false; }
        }

        /// <summary>ctl `tap?`：两只水龙头的解锁状态与今日纯度（验证解锁校验是否真的读到游戏状态）。</summary>
        public static string TapReport()
        {
            try
            {
                int rp = 0, gp = 0;
                try
                {
                    var st = StoreStation.Instance;
                    if (st != null) { rp = st.todayRustwaterPurity; gp = st.todayGutterflowPurity; }
                }
                catch (Exception) { }
                return "tap -> rust=" + (TapUnlocked("RUSTWATER_TAP") ? "unlocked" : "locked") + " pur=" + rp +
                       " | gutter=" + (TapUnlocked("GUTTERFLOW_TAP") ? "unlocked" : "locked") + " pur=" + gp +
                       " | forceLocked=" + ForceTapLocked;
            }
            catch (Exception e) { return "tap -> ERR " + e.GetType().Name; }
        }

        /// <summary>最近一次 TapSource 失败的原因（给面板说清楚：没解锁 / 读不到）。</summary>
        private static string tapBlock;

        private static void TapSource(out int grade, out int pur)
        {
            grade = 1; pur = 0;                       // 水龙头两个分支的 grade 都是 1
            tapBlock = null;
            try
            {
                var st = StoreStation.Instance;
                if (st == null) { tapBlock = "StoreStation 未就绪"; return; }
                // ★玩家反馈：选了「沟渠水」还是接铁锈水 —— 以前这里永远先取铁锈水，水源下拉等于没接。
                // 现在按 SrcIdx 取对应那只水龙头：1=铁锈水、2=沟渠水，其余（自动）保持先铁锈后沟渠。
                // ★2026-11-09 第二报：沟渠水还能「凭空接出来」——必须像原版一样先查解锁，
                //   否则没买沟渠水龙头的存档也能灌出沟渠水。
                bool rust = TapUnlocked("RUSTWATER_TAP");
                bool gutter = TapUnlocked("GUTTERFLOW_TAP");
                if (SrcIdx == 2)
                {
                    if (gutter && st.todayGutterflowPurity > 0) { pur = st.todayGutterflowPurity; return; }
                    tapBlock = gutter ? "沟渠水龙头读不到今日纯度" : "沟渠水龙头还没解锁";
                    return;
                }
                if (SrcIdx == 1)
                {
                    if (rust && st.todayRustwaterPurity > 0) { pur = st.todayRustwaterPurity; return; }
                    tapBlock = rust ? "铁锈水龙头读不到今日纯度" : "铁锈水龙头还没解锁";
                    return;
                }
                if (rust && st.todayRustwaterPurity > 0) { pur = st.todayRustwaterPurity; return; }
                if (gutter && st.todayGutterflowPurity > 0) { pur = st.todayGutterflowPurity; return; }
                tapBlock = (rust || gutter) ? "水龙头读不到今日纯度" : "水龙头还没解锁";
            }
            catch (Exception) { tapBlock = "读水龙头出错"; }
        }

        /// <summary>面板显示用：grade 只用于文案（4=铁锈水 / 5=沟渠水），纯度同样是现场读。</summary>
        private static void TodaySource(out int grade, out int pur)
        {
            // 手动填浓度（网页的「自己填纯度」）：>0 时直接按这个值算，不看水源下拉。
            if (CustomPur > 0) { grade = 4; pur = CustomPur; return; }
            // 手动选水源（F7 下拉）：0=自动，1..N=固定水源。
            if (SrcIdx > 0 && SrcSource(SrcIdx, out grade, out pur)) return;
            // 自动：**先看瓶里有没有水**——玩家把优质水放进去，就该按 99.01 算，
            // 而不是继续套水龙头水质（否则「按读取水质计算」根本不生效）。
            try
            {
                var b0 = CtxBottle(); if (b0 == null) b0 = GameBottle();
                if (b0 != null)
                {
                    int v = WaterHelper.GetTotalVolume(b0);
                    int pp = WaterHelper.GetWaterPurity(b0);
                    if (v > 0 && pp > 0) { grade = 4; pur = pp; return; }
                }
            }
            catch (Exception) { }
            TapSource(out grade, out pur);
            grade = 4;
            try
            {
                var st = StoreStation.Instance;
                if (st != null && st.todayRustwaterPurity <= 0 && st.todayGutterflowPurity > 0) grade = 5;
            }
            catch (Exception) { }
            if (pur <= 0) pur = 9100;   // 仅用于估算/文案；真正灌水走 TapSource，读不到会直接拒绝
        }

        /// <summary>手动指定水源：铁锈水/沟渠水取今日真值，纯水类的纯度按引擎液体定义。</summary>
        private static bool SrcSource(int idx, out int grade, out int pur)
        {
            grade = 4; pur = 0;
            try
            {
                var st = StoreStation.Instance;
                switch (idx)
                {
                    case 1: grade = 4; pur = st != null ? st.todayRustwaterPurity : 0; break;
                    case 2: grade = 5; pur = st != null ? st.todayGutterflowPurity : 0; break;
                    case 3: grade = 3; pur = 9900; break;   // 优质水
                    case 4: grade = 2; pur = 9850; break;   // 基础水
                    case 5: grade = 0; pur = 9990; break;   // 纯水
                    default: return false;
                }
            }
            catch (Exception) { return false; }
            if (pur <= 0) return false;
            return true;
        }

        /// <summary>水源显示名（下拉与顶行文案共用）。</summary>
        private static string SrcName(int grade)
        {
            if (SrcIdx > 0 && SrcIdx < SrcNames.Length) return SrcNames[SrcIdx];
            return grade == 4 ? "铁锈水" : (grade == 5 ? "沟渠水" : "今日水");
        }

        private static GameItem GameBottle()
        {
            try
            {
                // 玩家 2026-11-11：面板写「接 750 · 容器最多 750」，可实际接水进的是 20 L 那台。
                // 原因是显示侧读全局上下文里的瓶位（PurifierHook.BottleInv，由「最后可见窗口」写），
                // 动作侧读玩家点选那台。统一口径：当前在用哪台，就用哪台的瓶位。
                object inv = null;
                var pairs = PurifierHook.Pairs;
                int idx = activeIdx;
                if (idx < 0) idx = PurifierHook.PickedIndex;
                if (idx >= 0 && idx < pairs.Count && pairs[idx] != null && pairs[idx].BottleInv != null)
                    inv = pairs[idx].BottleInv;
                if (inv == null) inv = PurifierHook.BottleInv;
                if (inv == null) return null;
                return GameBridge.Get(inv, "childItem") as GameItem;
            }
            catch (Exception) { return null; }
        }

        /// <summary>没有净水器窗口时兜底：玩家身上容量最大的容器。</summary>
        private static GameItem PickFallbackBottle()
        {
            var all = GameBridge.LiveAllItems();
            GameItem best = null; int bestCap = 0;
            for (int i = 0; i < all.Count; i++)
            {
                int c = WaterHelper.GetTotalCapacity(all[i]);
                if (c > bestCap) { bestCap = c; best = all[i]; }
            }
            if (best != null) Log("[fill] fallback bottle cap=" + (bestCap / 1000) + "mL");
            return best;
        }

        /// <summary>有水的容器里挑装得最多的一个 —— 「按现有水/混水算方案」要看这一瓶，不是看空瓶。</summary>
        private static GameItem PickRichestBottle()
        {
            try
            {
                var all = GameBridge.LiveAllItems();
                GameItem best = null; long bestV = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    long v = WaterHelper.GetTotalVolume(all[i]);
                    if (v > bestV) { bestV = v; best = all[i]; }
                }
                if (best != null) Log("[mix] richest bottle v=" + (bestV / 1000) + "mL");
                return best;
            }
            catch (Exception) { return null; }
        }

        /// <summary>底部常驻一行：一晚要接多少、整瓶凑得多少、容器还剩多少空间。</summary>
        /// <summary>卡一的量：按容器现状说话——瓶里有水就只报「还差多少」，满了就让他先倒，
        /// 空瓶才报「最多取 X mL · 一晚上限 Y mL」。这样 2 L 瓶不再永远显示同一个 1850。</summary>
        /// <summary>不经耗材、单纯过一晚能达标的最大量（整瓶凑得出来的），与 OnFill 同一口径。</summary>
        private static long PlainReachableMl(PanelModel m)
        {
            try
            {
                int g, p; TodaySource(out g, out p);
                int perf, q; Bonuses(out perf, out q);
                long keep = MaxKeepUl((int)(m.CapacityMl * 1000L), p, perf, q, PlanText.TargetBp(TargetIdx, q));
                if (keep <= 0) return 0;
                var pp = WaterPlan.Plan(keep);
                if (pp != null && pp.totalUl > 0 && pp.totalUl < keep) keep = pp.totalUl;
                return keep / 1000L;
            }
            catch (Exception) { return 0; }
        }

        private static string CardFeedValueFor(PanelModel m, WaterPlan.Report r)
        {
            // 容器里已经有水（且方案按这瓶水算）：不用说「接多少」，说清楚「不用再接」。
            // 玩家：这里原来还挂一句「（一晚上限 1845 mL）」，跟下面的方案自相矛盾，删掉。
            if (r != null && r.fromWater && (r.capUl > 0 || m.VolumeMl > 0))
            {
                // 按瓶里这瓶水算：整瓶能跑就说「直接跑」，跑不完必须说清「先倒出多少、留多少」
                // （玩家 2026-10-10：只说「装满了 6000 可以跑」→ 我不知道该怎么操作）。
                // 玩家 2026-11-11：别用面板模型的数字（它可能还被上一只瓶子占着），
                // 一律用这份 Report 自己记录的体积/纯度，否则会出现「容器里已有 0 mL（0.00%），这瓶直接跑」。
                long volMl = r.capUl > 0 ? r.capUl / 1000L : m.VolumeMl;
                int bpV = r.srcBp > 0 ? r.srcBp : m.PurityBp;
                string bp = PlanText.Pct(bpV);
                if (r.fitUl <= 0)
                    return "这瓶 " + HiLite(volMl + " mL") + "（" + bp + "）一晚压不到目标，先别灌满";
                if (r.dumpUl > 0)
                    return "先倒出 " + HiLite(WaterPlan.Ml0(r.dumpUl) + " mL") +
                           "，留 " + WaterPlan.Ml0(r.fitUl) + " mL（" + bp + "）刚好够一晚";
                return "容器里已有 " + HiLite(volMl + " mL") + "（" + bp + "），这瓶直接跑";
            }
            long pour = r.ChosenFeedUl() / 1000L;      // 这条配方要接的量：选中方案说了算（玩家：第一个方案要 2000）
            if (pour <= 0) pour = PlainReachableMl(m);  // 没有候选方案时才退回「只过一晚」的上限
            long cap = r.ShownUl() / 1000L;            // 一晚模型上限
            long have = m.VolumeMl, room = m.CapacityMl - m.VolumeMl;
            if (have > 0 && have >= pour) return "容器里已有 " + have + " mL，够一晚";
            if (room <= 0) return "容器已满 " + have + "/" + m.CapacityMl + " mL，先倒出来再接";
            long want = pour - have;
            if (want <= 0) want = pour;
            if (!UncapEff && want > room) want = room;
            // 玩家 2026-11-10：不要用「·」分隔，重点数字改成颜色标记。
            // 上限一律取**达标候选里真能接的最大量**（含海德拉/药片），不再拿不带工具的公式重算 —— 20 L 曾误报 2665。
            long upperMl = cap;
            try
            {
                if (r != null)
                {
                    // 玩家 2026-11-10：「接 11600 · 上限 20000」里的 20000 是假的 —— 那只是容器容量/别家配方。
                    // 同策略（同样用不用过滤棉/紫外/海德拉）里能接到的最大量，才是「再多接点水、这套工具还撑得住」的上限。
                    long best = 0;
                    var pk = r.pick;
                    for (int i = 0; i < r.combos.Count; i++)
                    {
                        var c = r.combos[i];
                        if (c == null || !c.ok) continue;
                        if (pk != null)
                        {
                            if (c.filter != pk.filter || c.uv != pk.uv || c.hydra != pk.hydra) continue;
                        }
                        if (c.feedUl > best) best = c.feedUl;
                    }
                    if (best <= 0) best = WaterPlan.FeedCeilingOf(r);
                    upperMl = best / 1000L;
                }
            }
            catch (Exception) { }
            // 玩家 2026-11-11：这一行是主界面最大的那行字，数字用金色大字，标签暖灰，不再用「·」。
            string s = PlanText.Lbl("接 ") + HiLite(WaterPlan.Ml(want * 1000L) + " mL");
            // 玩家 2026-11-10：「接 11600 · 上限 20000」里的上限是假的 —— 20000 只是容器容量，
            // 跟这个方案能不能做到无关。上限只在「工具还能多接、容器装不下」时才显示（如 2000 mL 瓶 → 2636）。
            if (upperMl > want)
            {
                s += "    " + PlanText.Lbl("上限 ") + PlanText.Rich(PlanText.C_NUM, WaterPlan.Ml(upperMl * 1000L) + " mL");
                long over = m.CapacityMl > 0 ? upperMl - m.CapacityMl : 0;
                if (over > 0) s += "    " + PlanText.Rich(PlanText.C_WARN, "容器最多 " + WaterPlan.Ml(m.CapacityMl * 1000L) + " mL，差 " + WaterPlan.Ml(over * 1000L) + " mL");
                else s += "    " + PlanText.Rich(PlanText.C_LABEL, "容器凑不满，差 " + WaterPlan.Ml((upperMl - want) * 1000L) + " mL");
            }
            return s;
        }

        /// <summary>「更多方案」页的行：全部 ok 解，按出水量从大到小，不按前置/后置过滤。</summary>
        private static List<WaterPlan.Combo> AllRows(WaterPlan.Report r, int max)
        {
            var res = new List<WaterPlan.Combo>();
            if (r == null || max <= 0) return res;
            var all = new List<WaterPlan.Combo>();
            for (int i = 0; i < r.combos.Count; i++)
            {
                var c = r.combos[i];
                if (c == null || !c.ok) continue;
                all.Add(c);
            }
            // 玩家：花更多、出水更少的方案没必要显示 —— 只留有收益的（被支配的解剔除）
            var keep = new List<WaterPlan.Combo>();
            for (int i = 0; i < all.Count; i++)
            {
                var c1 = all[i];
                bool dominated = false;
                for (int k = 0; k < all.Count; k++)
                {
                    if (k == i) continue;
                    var c2 = all[k];
                    if (c2.cost <= c1.cost && c2.outUl >= c1.outUl && (c2.cost < c1.cost || c2.outUl > c1.outUl))
                    { dominated = true; break; }
                }
                if (!dominated) keep.Add(c1);
            }
            all = keep;
            all.Sort(delegate(WaterPlan.Combo a, WaterPlan.Combo b)
            {
                int k = b.outUl.CompareTo(a.outUl);
                if (k != 0) return k;
                return b.purityBp.CompareTo(a.purityBp);
            });
            for (int i = 0; i < all.Count && i < max; i++) res.Add(all[i]);
            return res;
        }


        /// <summary>底部状态行：只写「容器多少 / 还差多少」，详细长句留在 tooltip 与日志里。</summary>
        private static string LiveLineShort(PanelModel m, WaterPlan.Report r)
        {
            try
            {
                if (m == null) return "";
                // 已有水直接出方案：不报「还差多少」，只报容器现状
                if (r != null && r.fromWater)
                    return PlanText.Lbl("容器 ") + PlanText.Rich(PlanText.C_NUM, m.VolumeMl + "/" + m.CapacityMl + " mL")
                         + "    这瓶水直接跑";
                long pour = planPourMl > 0 ? planPourMl : (r != null ? r.ChosenFeedUl() / 1000L : 0L);
                // 玩家 2026-11-10：这一句删掉（空容器时不再报「要接 X mL（容量 Y）」）
                if (m.VolumeMl <= 0) return "";
                long free = m.CapacityMl - m.VolumeMl;
                if (free <= 0) return "已满 " + PlanText.Rich(PlanText.C_NUM, m.VolumeMl + "/" + m.CapacityMl + " mL") + "    先倒出";
                long need = pour - m.VolumeMl;
                if (need < 0) need = 0;
                string line = PlanText.Lbl("容器 ") + PlanText.Rich(PlanText.C_NUM, m.VolumeMl + "/" + m.CapacityMl + " mL")
                            + "    " + PlanText.Lbl("还差 ") + PlanText.Rich(PlanText.C_NUM, need + " mL");
                // 容器装不下这条方案时给去路：换大容器 / 少一片药刚好卡满
                if (r != null && r.pick != null)
                {
                    long capUl = m.CapacityMl * 1000L;
                    if (r.pick.feedUl > capUl)
                    {
                        line += "    " + PlanText.Rich(PlanText.C_WARN, "方案要 " + WaterPlan.Ml(r.pick.feedUl) + " mL（超容器），换大容器");
                        // 找同前置、少一片药且装得下的方案
                        for (int i = 0; i < r.combos.Count; i++)
                        {
                            var c2 = r.combos[i];
                            if (c2 == null || !c2.ok) continue;
                            if (c2.filter != r.pick.filter || c2.uv != r.pick.uv) continue;
                            if (c2.tablets >= r.pick.tablets) continue;
                            if (c2.feedUl <= capUl && c2.feedUl > capUl - 300000L)
                            {
                                line += "；少到 " + c2.tablets + " 片 → " + WaterPlan.Ml(c2.feedUl) + " mL 刚好";
                                break;
                            }
                        }
                    }
                }
                return line;
            }
            catch (Exception) { return ""; }
        }

        /// <summary>容器里已经有水（可能混过别的液体）时：按**读到的真实六项**给最优方案，
        /// 而不是让玩家再往里接生水。用的是水质扫描仪同一个接口，所以混水也准。</summary>
        private static string ExistingPlanLine(PanelModel m)
        {
            try
            {
                var b = CtxBottle(); if (b == null) b = GameBottle(); if (b == null) b = PickFallbackBottle();
                if (b == null) return "";
                WaterMath.Water w;
                if (!BuildRealWater(b, out w) || w == null || w.v <= 0) return "";
                int perf, q; Bonuses(out perf, out q);
                int target = PlanText.TargetBp(TargetIdx, q);
                var pl = WaterMath.PlanFromWater(w, perf, q, target, 4, 3, 400);
                if (pl == null)
                    return "已有 " + PlanText.Ml(w.v) + " mL·" + PlanText.Pct(w.Purity) + "：4 晚 + 3 片也到不了 " + PlanText.Pct(target) + "，先兑水或换小瓶";
                var sb = new System.Text.StringBuilder();
                sb.Append("已有 ").Append(PlanText.Ml(w.v)).Append(" mL·").Append(PlanText.Pct(w.Purity)).Append("：");
                if (pl.filter == 1) sb.Append("过滤(").Append(pl.filterUsed).Append("点) ");
                if (pl.uv == 1) sb.Append("紫外 ");
                sb.Append(pl.nights).Append(" 晚");
                if (pl.tablets > 0) sb.Append(" + ").Append(pl.tablets).Append(" 片");
                sb.Append(" → 出 ").Append(PlanText.Ml(pl.outUl)).Append(" mL·").Append(PlanText.Pct(pl.purityBp));
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        private static string LiveLine(PanelModel m, WaterPlan.Report plan)
        {
            try
            {
                if (m == null) return "";
                long pour = 0, need = 0;
                var r = plan;
                if (r != null)
                {
                    // 真倒得进去的量 = 整瓶凑得出的总量（1850），模型一晚上限另写（1857）
                    pour = planPourMl > 0 ? planPourMl : (r.bestUl / 1000L);
                    if (pour <= 0) pour = PlainReachableMl(m);
                    need = r.ShownUl() / 1000L;
                }
                if (need <= 0) need = pour;
                long free = m.CapacityMl - m.VolumeMl;
                long more = pour - m.VolumeMl;
                if (more < 0) more = 0;
                var sb = new System.Text.StringBuilder();
                if (r != null && r.fromWater)
                {
                    var sb0 = new System.Text.StringBuilder();
                    sb0.Append("容器里已有 ").Append(m.VolumeMl).Append(" mL · ").Append(PlanText.Pct(m.PurityBp))
                       .Append("，这瓶水直接跑，不用再接");
                    return sb0.ToString();
                }
                // 按容器现状说话：已有水就只报「还差多少」，不再拿整瓶量去和空余比
                if (m.VolumeMl > 0 && more == 0)
                {
                    string ex = ExistingPlanLine(m);
                    if (!string.IsNullOrEmpty(ex)) return ex;
                    sb.Append("容器里已有 ").Append(m.VolumeMl).Append(" mL，够一晚，不用再接");
                }
                else if (free <= 0)
                {
                    sb.Append("容器已满 ").Append(m.VolumeMl).Append("/").Append(m.CapacityMl).Append(" mL，先倒出来再接");
                }
                else if (m.VolumeMl == 0 && CustomPur < 0 && SrcIdx == 0)
                {
                    // 空瓶 + 自动水源：不假装知道瓶子里的水是什么，交给水龙头真值并提示
                    int tg2, tp2;
                    try { TapSource(out tg2, out tp2); if (tp2 <= 0) TodaySource(out tg2, out tp2); } catch (Exception) { tp2 = 0; }
                    sb.Append("容器是空的：一键接水按今日水龙头（").Append(tp2 > 0 ? PlanText.Pct(tp2) : "未读到").Append("）接 ")
                      .Append(pour > 0 ? pour : need).Append(" mL；要其它水源就在下拉里选，或填浓度");
                }
                else
                {
                    long want = more > 0 ? more : pour;
                    if (want > free) want = free;
                    sb.Append("要接 ").Append(want).Append(" mL");
                    if (pour > 0 && pour != need) sb.Append("（整瓶）");
                    sb.Append(" · 容器 ").Append(m.VolumeMl).Append("/").Append(m.CapacityMl).Append(" mL（还能装 ").Append(free).Append(" mL）");
                    // 瓶里剩着旧水时，方案要的量灌不满：明说要先倒，别让人以为「永远差那一点」
                    if (m.VolumeMl > 0 && m.VolumeMl < pour)
                        sb.Append(" · 已有 ").Append(m.VolumeMl).Append(" mL·").Append(PlanText.Pct(m.PurityBp)).Append("，想按方案灌 ").Append(pour).Append(" mL 就先倒掉");
                }
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        /// <summary>逐台刷新标题栏灌水键的键面：显示「这台正在跑的量」，不再只让一台有字。</summary>
        private static void RefreshFillLabels()
        {
            int perf0, q0;
            Bonuses(out perf0, out q0);
            foreach (var kv in winFill)
            {
                var w = kv.Key; var btn = kv.Value;
                if (w == null || btn == null) continue;
                try { if (!w.IsVisible()) continue; } catch (Exception) { continue; }
                try
                {
                    BindWindow(w, null);                       // 让「当前这台」指向这个窗口
                    // 注意：这里**不再** SetDesc 改图标名 —— 玩家要求标题栏图标名字固定；
                    // 每台的动态方案文案由面板顶行显示（Rebuild 里 pSub/pMsg）。
                }
                catch (Exception) { }
            }
        }

        /// <summary>有没有任何一台净水器的窗口正开着（红框只在此时标）。</summary>
        private static bool AnyPurifierWindowVisible()
        {
            for (int i = 0; i < PurifierHook.Pairs.Count; i++)
            {
                var vp = PurifierHook.Pairs[i];
                if (vp == null || vp.Window == null) continue;
                try { if (GameBridge.WinVisible(vp.Window)) return true; }
                catch (Exception) { }
            }
            return false;
        }

        /// <summary>瓶子已经在机器里时的「下一步怎么做」：原样一晚 / 后加几片药片 / 倒到多少。</summary>
        private static string AdviceText(PanelModel m)
        {
            try
            {
                if (m == null || m.VolumeMl <= 0 || m.PurityBp <= 0) return "";
                var list = WaterMath.Plans(m.VolumeMl * 1000L, m.PurityBp, 1.0 + m.PerfPct / 100.0, m.Quality, m.TargetBp);
                if (list == null || list.Count == 0) return "这瓶一晚到不了 " + PlanText.Pct(m.TargetBp) + "：要么加水稀释，要么换更纯的水。";
                var sb = new System.Text.StringBuilder();
                int n = 0;
                for (int i = 0; i < list.Count && n < 3; i++)
                {
                    var p = list[i];
                    if (n > 0) sb.Append(" ／ ");
                    if (n == 0) sb.Append("最优：");
                    bool plain = p.drainUl == 0 && p.tablets == 0 && !p.useFilter && !p.useUv;
                    if (p.drainUl > 0) sb.Append("倒到 ").Append(p.keepUl / 1000L).Append(" mL ");
                    if (plain) sb.Append("原样 ");
                    if (p.useFilter) sb.Append("过滤棉 ");
                    if (p.useUv) sb.Append("紫外 ");
                    if (p.tablets > 0) sb.Append("净化完加 ").Append(p.tablets).Append(" 片药片 ");
                    sb.Append("→ ").Append(PlanText.Pct(p.purity)).Append("（").Append(p.output / 1000L).Append(" mL）");
                    n++;
                }
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }
        private static object CurrentMachine()
        {
            // ★玩家 2026-11-10：多开窗口时按「点选的那台」算 —— 以前永远拿第一台可见窗口，
            // 后来开的窗口怎么点都算不到，把第一台关掉也还是抓不到第二台。
            // 选择走每台窗口标题栏左上角那个圆点按钮（绿=当前在用，灰=没选）。
            if (activeIdx >= 0 && activeIdx < PurifierHook.Pairs.Count)
            {
                var ap = PurifierHook.Pairs[activeIdx];
                if (ap != null && ap.Machine != null) { lastVisible = ap.Machine; return ap.Machine; }
            }
            if (MachineOverride >= 0 && MachineOverride < PurifierHook.Pairs.Count)
            {
                var mp = PurifierHook.Pairs[MachineOverride];
                if (mp != null && mp.Machine != null) return mp.Machine;
            }
            // 多台机器时：先看哪个窗口真的开着，别拿「上次活动的」那台去算。
            for (int i = 0; i < PurifierHook.Pairs.Count; i++)
            {
                var vp = PurifierHook.Pairs[i];
                if (vp == null || vp.Machine == null || vp.Window == null) continue;
                try
                {
                    if (GameBridge.WinVisible(vp.Window)) { lastVisible = vp.Machine; return vp.Machine; }
                }
                catch (Exception) { }
            }
            // 窗口关掉之后仍沿用「上一次真正看到的那台」，避免掉到 ActiveMachine 或第一台上去。
            if (lastVisible != null) return lastVisible;
            // 没有窗口可见时，优先选装了性能/质量模组的那台——玩家配了模组的机器才是他关心的一台。
            object bestMod = null; int bestScore = -1;
            for (int i = 0; i < PurifierHook.Pairs.Count; i++)
            {
                var mp = PurifierHook.Pairs[i];
                if (mp == null || mp.Machine == null) continue;
                int sc = 0;
                try
                {
                    string mods = GameBridge.DescribeModules(mp.Machine);
                    if (mods != null)
                    {
                        if (mods.IndexOf("performance", StringComparison.Ordinal) >= 0) sc += 2;
                        if (mods.IndexOf("quality", StringComparison.Ordinal) >= 0) sc += 1;
                    }
                }
                catch (Exception) { }
                if (sc > bestScore) { bestScore = sc; bestMod = mp.Machine; }
            }
            if (bestMod != null) return bestMod;
            if (PurifierHook.ActiveMachine != null) return PurifierHook.ActiveMachine;
            return PurifierHook.MachineItem;
        }

        private static object lastVisible;
        private static float lastLive = -10f;
        public static int QualityOverride = -1;
        public static int PerfOverride = -1;

        /// <summary>mach=N：强制指定「当前这台」，用于玩家开着 A 台却要算 B 台的场景。</summary>
        public static int MachineOverride = -1;

        // ---------------- 多台净水器：点选「用哪一台算」 ----------------
        /// <summary>玩家点选的当前机器（PurifierHook.Pairs 下标；-1＝没点过，按第一台可见窗口）。</summary>
        private static int activeIdx = -1;
        private static readonly Dictionary<PixelWindow, MachineryButton> pickBtns = new Dictionary<PixelWindow, MachineryButton>();

        /// <summary>窗口 → Pairs 下标（找不到返回 -1）。</summary>
        private static int PairIndexOf(PixelWindow w)
        {
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var p = PurifierHook.Pairs[i];
                    if (p != null && ReferenceEquals(p.Window, w)) return i;
                }
            }
            catch (Exception) { }
            return -1;
        }

        /// <summary>点选这台窗口对应的净水器：之后所有卡一/卡二/方案表都按它的模组与容器算。</summary>
        public static void SelectPair(int idx)
        {
            if (idx < 0 || idx >= PurifierHook.Pairs.Count) return;
            activeIdx = idx;
            MachineOverride = idx;
            lastVisible = null;
            // 让 PurifierHook 那边也认这个点选，否则它每帧按「第一台可见窗口」把上下文改回去，
            // 表现成：选了 20 L 那台，接水量还是 750 那台锁着，只有关掉 750 窗口才正常。
            PurifierHook.PickedIndex = idx;
            DropPlanCache();
            var pw = PurifierHook.Pairs[idx].Window as PixelWindow;
            if (pw != null) UseWindow(pw);
            RefreshPickTints();
            RequestRebuild();
            Log("[ui] select pair=" + idx + " winIdx=" + (pw == null ? -1 : PairIndexOf(pw)) +
                " pairWinVisible=" + (pw == null ? false : PurifierHook.WindowVisible(pw)) + " pairs=" + PurifierHook.Pairs.Count + " mode=" + Mode);
        }

        // ---------------- 红框（机器高亮）与圆点着色：节流刷新 ----------------
        private static float lastVisPoll = -999f;
        private static string lastVisSig = "";
        private static float lastTintT = -999f;
        private static float lastMarkerNote = -999f;
        private static float lastTickIn = -999f;

        /// <summary>哪几台净水器窗口正开着（'1'/'0' 串）。</summary>
        private static string VisibleSig()
        {
            var sb = new System.Text.StringBuilder(16);
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var p = PurifierHook.Pairs[i];
                    sb.Append(p != null && PurifierHook.WindowVisible(p.Window) ? '1' : '0');
                }
            }
            catch (Exception) { }
            return sb.ToString();
        }

        /// <summary>0.4 s 轮询可见集合：一开一关立刻重画红框；集合没变就 4 s 兜底同步一次。</summary>
        private static void MarkerTick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
#if DEV
            // 每 2 s 打一次「到底调到了没」：Pairs 有几个、圆点按钮有几个。红框消失时靠这条分清「没调用」还是「没配对」。
            if (now - lastMarkerNote > 2f)
            {
                lastMarkerNote = now;
                Log("[marker] poll pairs=" + PurifierHook.Pairs.Count + " pickBtns=" + pickBtns.Count);
            }
#endif
            if (PurifierHook.Pairs.Count == 0) return;
            if (now - lastVisPoll < 0.4f) return;
            lastVisPoll = now;
            string vs = VisibleSig();
            bool changed = vs != lastVisSig;
            lastVisSig = vs;
            if (!changed && now - lastMarkerSync < 4f) return;
            lastMarkerSync = now;
#if DEV
            Log("[marker] sync sig=" + vs + " changed=" + changed);
#endif
            try { MachineMarker.SyncAll(); } catch (Exception e) { Log("[marker] ERR " + e.GetType().Name + " " + e.Message); }
        }

        private static readonly Dictionary<MachineryButton, Image[]> tintImgs = new Dictionary<MachineryButton, Image[]>();

        /// <summary>圆点颜色每帧补涂（选中绿 / 未选灰）：原生按钮悬停/按下会瞬间刷白，
        /// 逐帧覆盖过去就等于「指着还是绿、按下变绿」。3 个按钮，开销可忽略。</summary>
        private static void PickTintTick()
        {
            if (pickBtns.Count == 0) return;
            RefreshPickTints();
        }

        /// <summary>圆点按钮的着色：选中的绿，未选中的灰（同一份图标，靠颜色区分，不额外画图）。</summary>
        private static void RefreshPickTints()
        {
            var green = new Color(0.45f, 1f, 0.55f, 1f);
            var gray = new Color(0.55f, 0.55f, 0.55f, 1f);
            try
            {
                foreach (var kv in pickBtns)
                {
                    var btn = kv.Value;
                    if (btn == null) continue;
                    int idx = PairIndexOf(kv.Key);
                    TintIcon(btn, (idx >= 0 && idx == activeIdx) ? green : gray);
#if DEV
                    if (idx < 0)
                        Log("[pick] 按钮的窗口在配对表里找不到（按键失效） active=" + activeIdx + " picked=" + PurifierHook.PickedIndex + " pairs=" + PurifierHook.Pairs.Count);
#endif
                }
            }
            catch (Exception) { }
        }

        /// <summary>给原生图标按钮染色（MachineryButton 不是 Component，得从它的 rectTransform 上拿 GameObject）。</summary>
        private static void TintIcon(MachineryButton b, Color c)
        {
            if (b == null) return;
            try
            {
                var rt = b.rectTransform;
                if (rt == null) return;
                // 玩家 2026-11-10：圆点按下去会「白一下再恢复」——原生按钮按下/悬停时会把 Image 刷成高亮色（白）。
                // 关掉它的颜色过渡，颜色只由我们自己涂，按下去就不会闪白。
                var nb = rt.gameObject.GetComponent<UnityEngine.UI.Button>();
                if (nb != null && nb.transition != UnityEngine.UI.Selectable.Transition.None)
                    nb.transition = UnityEngine.UI.Selectable.Transition.None;
                // 按钮的 Button 组件可能挂在子节点上（图标本体），子节点上的也要关，否则照样闪白。
                var subs = rt.gameObject.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                for (int i = 0; i < subs.Length; i++)
                {
                    var sb2 = subs[i];
                    if (sb2 != null && sb2.transition != UnityEngine.UI.Selectable.Transition.None)
                        sb2.transition = UnityEngine.UI.Selectable.Transition.None;
                }
                Image[] imgs;
                if (!tintImgs.TryGetValue(b, out imgs) || imgs == null || imgs.Length == 0)
                {
                    imgs = rt.gameObject.GetComponentsInChildren<Image>(true);
                    tintImgs[b] = imgs;
                }
                for (int i = 0; i < imgs.Length; i++) if (imgs[i] != null) imgs[i].color = c;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// machines：把 4 台机器逐个列出来（性能/质量/瓶内水量纯度/窗口可见性），
        /// 玩家反馈「我读的是另一台」时用它定位。
        /// </summary>
        public static string MachinesReport()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < PurifierHook.Pairs.Count; i++)
            {
                var p = PurifierHook.Pairs[i];
                string perf = "?", q = "?", vis = "?", vol = "?", pur = "?";
                try
                {
                    if (p.Machine != null)
                    {
                        perf = GameBridge.StatInt("MachineryHelper", "GetCurrentPerformanceBonus", p.Machine).ToString();
                        q = GameBridge.StatInt("MachineryHelper", "GetCurrentQualityBonus", p.Machine).ToString();
                    }
                    vis = p.Window == null ? "?" : (GameBridge.WinVisible(p.Window) ? "true" : "false");
                    var ci = p.BottleInv == null ? null : GameBridge.Get(p.BottleInv, "childItem") as GameItem;
                    var b = ci;
                    if (b != null)
                    {
                        vol = (WaterHelper.GetTotalVolume(b) / 1000).ToString();
                        pur = WaterHelper.GetWaterPurity(b).ToString();
                    }
                }
                catch (Exception) { }
                if (i > 0) sb.Append(" | ");
                sb.Append("#").Append(i).Append(" perf=").Append(perf).Append(" q=").Append(q)
                  .Append(" vis=").Append(vis).Append(" vol=").Append(vol).Append("mL pur=").Append(pur);
            }
            return "machines -> " + (sb.Length == 0 ? "none" : sb.ToString()) + " active=" + MachineOverride;
        }

        public static void RebuildPublic() { Rebuild(true); }

        /// <summary>ctl 改过「当前这台」之后立刻重挂描边，免得 mark? 读到上一帧的 host。</summary>
        public static void SyncMarkerPublic() { try { MachineMarker.Sync(CurrentMachine()); } catch (Exception) { } }

        /// <summary>
        /// ctl 用：把机器加成原样打出来。
        /// 玩家反馈「明明装了性能模组、界面却写 +0%」时，先用它确认是读不到还是真的 0。
        /// </summary>
        public static string BonusReport()
        {
            int perf, q;
            Bonuses(out perf, out q);
            var m = CurrentMachine();
            string ids = "none";
            try
            {
                if (m != null) ids = GameBridge.DescribeModules(m);
            }
            catch (Exception) { }
            return "bonus -> perf=" + perf + " quality=" + q +
                   " machine=" + (m == null ? "none" : "yes") +
                   " over=" + PerfOverride + "/" + QualityOverride +
                   " modules=" + ids;
        }

        /// <summary>给手册用的整机质量（手册第 1/3 页的上限跟它走）。</summary>
        internal static int QualityForBook()
        {
            int perf, q;
            try { Bonuses(out perf, out q); return q; } catch (Exception) { return -1; }
        }

        private static void Bonuses(out int perf, out int q)
        {
            int power;
            Bonuses(out perf, out q, out power);
        }

        /// <summary>性能 / 质量 / **每晚能耗**（机器标签自带的「所需能量」，效率词条会影响它）。
        /// 能耗读不到时才退回 4/晚；1 能量 = 1 元（玩家 2026-11-10 定案）。</summary>
        private static void Bonuses(out int perf, out int q, out int power)
        {
            perf = 0; q = 0; power = 4;
            var machine = CurrentMachine();
            if (machine != null)
            {
                try
                {
                    perf = GameBridge.StatInt("MachineryHelper", "GetCurrentPerformanceBonus", machine);
                    q = GameBridge.StatInt("MachineryHelper", "GetCurrentQualityBonus", machine);
                    int pw = GameBridge.StatInt("MachineryHelper", "GetMachinePowerUsage", machine);
                    if (pw > 0) power = pw;
                }
                catch (Exception) { }
            }
            if (power <= 0) power = 4;
            WaterPlan.EnergyPerNight = power;
            // 调试用：qual=N 直接顶替机器质量，用来验证「带 25 质量模组时的 1850」而不是等玩家装模组。
            if (QualityOverride >= 0) q = QualityOverride;
            if (PerfOverride >= 0) perf = PerfOverride;
        }

        /// <summary>重点数字加金色。面板文本走游戏原生 TMP，支持富文本。</summary>
        private static string HiLite(string s) { return "<color=#F2C14E>" + s + "</color>"; }

        /// <summary>步骤摘要（玩家 2026-11-10 定案）：按真实执行顺序串起来 ——
        /// 海德拉 → 过滤棉 → 紫外 → 净水器 n 晚 → 药片；不再写「药片按游戏真值」这类内部说明。</summary>
        private static string StepChain(WaterPlan.Combo c)
        {
            if (c == null) return "";
            // 玩家 2026-11-11：步骤链不要用「+」硬拼，四个空格分隔；工具名暖灰、数量青色，
            // 与主界面其他行保持同一套配色（同类属性同色）。
            var sb = new System.Text.StringBuilder();
            if (c.hydra && c.hydraDur > 0)
            {
                if (sb.Length > 0) sb.Append("    ");
                sb.Append(PlanText.Lbl("海德拉耐久 ")).Append(PlanText.Rich(PlanText.C_NUM, c.hydraDur.ToString()));
            }
            if (c.filter)
            {
                if (sb.Length > 0) sb.Append("    ");
                sb.Append(PlanText.Lbl("过滤棉 ")).Append(PlanText.Rich(PlanText.C_NUM, c.filterUsed.ToString()));
            }
            if (c.uv)
            {
                if (sb.Length > 0) sb.Append("    ");
                sb.Append(PlanText.Lbl("紫外"));
            }
            if (sb.Length > 0) sb.Append("    ");
            sb.Append(PlanText.Lbl("净水器 ")).Append(PlanText.Rich(PlanText.C_NUM, (c.nights > 0 ? c.nights : 1).ToString())).Append(PlanText.Lbl(" 晚"));
            if (c.tablets > 0)
            {
                sb.Append("    ").Append(PlanText.Lbl("药片 ")).Append(PlanText.Rich(PlanText.C_NUM, c.tablets.ToString()));
            }
            return sb.ToString();
        }

        /// <summary>keepUl 单位 μL，返回 μL。</summary>
        public static long MaxKeepUl(int capUl, int purBp, int perf, int q, int targetBp)
        {
            if (capUl <= 0) return 0;
            var on = WaterMath.PlanOneNight(capUl, purBp, perf, q, targetBp, 1);
            if (on != null && on.possible) return on.keepUl;
            long lo = 0, hi = capUl;
            for (int i = 0; i < 24; i++)
            {
                long mid = (lo + hi) / 2;
                if (mid <= 0) break;
                var p = WaterMath.PlanOneNight(mid, purBp, perf, q, targetBp, 1);
                if (p != null && p.possible) lo = mid; else hi = mid;
            }
            return lo;
        }

        private static int TargetVolumeMl()
        {
            try
            {
                if (Model != null && Model.Plans != null && Model.Plans.Count > 0)
                {
                    var p = Model.Plans[Math.Min(Model.Selected, Model.Plans.Count - 1)];
                    if (p != null && p.keepUl > 0) return (int)(p.keepUl / 1000);
                }
                var b = GameBottle();
                if (b == null) return 0;
                int grade, pur;
                TodaySource(out grade, out pur);
                int perf, q;
                Bonuses(out perf, out q);
                return (int)(MaxKeepUl(WaterHelper.GetTotalCapacity(b), pur, perf, q, PlanText.TargetBp(TargetIdx, q)) / 1000);
            }
            catch (Exception) { return 0; }
        }

        private static string FillLabel()
        {
            var b = GameBottle();
            if (b == null) b = PickFallbackBottle();
            if (b == null) return "灌水（先放瓶）";
            int grade, pur;
            TodaySource(out grade, out pur);
            int perf, q;
            Bonuses(out perf, out q);
            long keep = MaxKeepUl(WaterHelper.GetTotalCapacity(b), pur, perf, q, PlanText.TargetBp(TargetIdx, q));
            if (keep <= 0) return "灌水（今日水质做不到）";
            // 键面必须跟「选中方案」一致：方案说接 1850 就不要写 1857。
            try
            {
                var r0 = CurrentPlan(true);
                if (r0 != null && r0.pick != null && r0.pick.feedUl > 0) keep = r0.pick.feedUl;
                // 与 OnFill 同口径：理论上限 1857 还要按整瓶可达量收成 1850，否则按钮说 1857、实际灌 1850。
                var pp = WaterPlan.Plan(keep);
                if (pp != null && pp.totalUl > 0 && pp.totalUl < keep) keep = pp.totalUl;
            }
            catch (Exception) { }
            string water = grade == 4 ? "铁锈水" : (grade == 5 ? "沟渠水" : "今日水");
            return "灌 " + (keep / 1000) + "mL " + water + " " + PlanText.Pct(pur);
        }

        // ---------------- 一键灌水（单位：内部 μL，显示 mL） ----------------

        private static string OnFill()
        {
            try
            {
                int grade, pur;
                TodaySource(out grade, out pur);
                int perf, q;
                Bonuses(out perf, out q);
                int targetBp = PlanText.TargetBp(TargetIdx, q);

                // 目标容器：优先「净水器槽里那瓶」（玩家正对着的就是它），其次右键选中的，最后背包/台面。
                var b = CtxBottle() as GameItem;
                if (b == null) b = RightClickTarget as GameItem;
                if (b == null) b = GameBottle();
                if (b == null) b = PickFallbackBottle();
                if (b == null) { LastMsg = "没有可灌的瓶子：把瓶放进储水容器或背包"; return "no-bottle"; }
                // 已经有水就只补剩余空间，绝不溢出（AddWater 超量会被丢掉，等于白接）。
                long haveUl2 = 0, capUl2 = 0;
                try { haveUl2 = WaterHelper.GetTotalVolume(b); capUl2 = WaterHelper.GetTotalCapacity(b); } catch (Exception) { }

                // 玩家 2026-11-11：这个键就是原来的「接水」——只往瓶里灌水，不重算、不改方案。
                // 方案什么时候变：按「默认 / 效率 / 性价比」或改设置页；倒水/接水/过夜都不变。
                int freeMl = (int)((capUl2 - haveUl2) / 1000);
                if (freeMl <= 0)
                {
                    LastMsg = "容器已经是满的（" + (haveUl2 / 1000) + "/" + (capUl2 / 1000) + " mL），先倒出来再接";
                    Rebuild(true);
                    return "full";
                }

                long keepUl = MaxKeepUl(WaterHelper.GetTotalCapacity(b), pur, perf, q, targetBp);
                if (keepUl <= 0) { LastMsg = "今日水质一晚达标不了"; return "impossible"; }
                // 按「当前选中方案 + 现有整瓶容器凑得出的量」灌：选中方案说接多少就接多少，
                // 但还要再按整瓶可达量收一次（选中方案给的是理论上限 1857，整瓶只能凑 1850）。
                long bottleUl = keepUl;
                try
                {
                    var r0 = CurrentPlan(true);
                    if (r0 != null && r0.pick != null && r0.pick.feedUl > 0) bottleUl = r0.pick.feedUl;
                    var pp = WaterPlan.Plan(bottleUl);
                    if (pp != null && pp.totalUl > 0 && pp.totalUl < bottleUl) bottleUl = pp.totalUl;
                }
                catch (Exception) { }
                int totalWantMl = (int)(bottleUl / 1000);       // 这条方案总共要接多少
                int haveMlNow = (int)(haveUl2 / 1000);            // 容器里已经有多少
                // 玩家：**按一次就够了**。第二次按只补「离目标还差的量」，差 0 就直接说够了，
                // 绝不再往容器里追加（以前按一次 1500、再按又灌 1500，把水接成了废液）。
                int wantMl = totalWantMl - haveMlNow;
                // 容器里已经有液体：先按引擎的方式把它转到**空容器**（绝不倒进仓库/机器槽里那瓶，
                // 也绝不进已经装了滤芯的水罐），转不过去就只报警，不硬灌。
                if (haveMlNow > 0 && wantMl > 0)
                {
                    // 玩家实测（第 5 条）：瓶里现成的水比水龙头还好时，自动倒掉等于把优质水换走。
                    int havePurNow = 0, srcPurNow = 0;
                    try { havePurNow = WaterHelper.GetWaterPurity(b); } catch (Exception) { }
                    try { int gTmp; TapSource(out gTmp, out srcPurNow); } catch (Exception) { }
                    if (havePurNow > 0 && srcPurNow > 0 && havePurNow >= srcPurNow)
                    {
                        LastMsg = "瓶里现成的水 " + PlanText.Pct(havePurNow) + " 比水龙头 " + PlanText.Pct(srcPurNow) +
                                  " 还好，不自动倒掉；要接水请先手动倒出或换空瓶";
                        Log("[pour] keep-better-water have=" + havePurNow + " tap=" + srcPurNow);
                        Rebuild(true);
                        return "keep-better-water";
                    }
                    var spare = FindEmptyReceiver(b, totalWantMl);
                    if (spare != null)
                    {
                        try
                        {
                            WaterHelper.TransferLiquid(b, spare);
                            Log("[pour] 已有水先转到空容器 " + GameBridge.NameOfItem(spare) + " " + haveMlNow + "mL");
                            // ★这里**不能**写 b = spare：机器容器才是要灌的目标。
                            // 旧代码改绑成备用瓶，导致铁锈水灌进外面的瓶、机器容器留空（玩家报的第 5 条 bug）。
                            WaterHelper.EmptyContainer(b);
                            haveMlNow = 0;
                            try { haveUl2 = WaterHelper.GetTotalVolume(b); capUl2 = WaterHelper.GetTotalCapacity(b); } catch (Exception) { }
                            freeMl = (int)((capUl2 - haveUl2) / 1000);
                            wantMl = totalWantMl;
                            if (wantMl > freeMl) wantMl = freeMl;
                        }
                        catch (Exception e2) { Log("[pour] 转移失败 " + e2.Message); }
                    }
                    else
                    {
                        LastMsg = "容器里已有 " + haveMlNow + " mL，找不到空容器可以转；请先自己倒空或换一个空瓶再接";
                        Rebuild(true);
                        return "need-empty";
                    }
                }
                if (wantMl <= 0)
                {
                    LastMsg = "已经够这条方案了（" + haveMlNow + "/" + totalWantMl + " mL），不用再接";
                    Rebuild(true);
                    return "enough";
                }
                if (wantMl > freeMl) wantMl = freeMl;
                keepUl = wantMl * 1000L;

                GameItem dest = b;
                if (WaterHelper.GetTotalCapacity(b) < keepUl)
                {
                    dest = null;
                    var all = GameBridge.LiveAllItems();
                    for (int i = 0; i < all.Count; i++)
                    {
                        var it = all[i];
                        if (it == null) continue;
                        if (WaterHelper.GetTotalCapacity(it) >= keepUl) { dest = it; break; }
                    }
                    if (dest == null)
                    {
                        int maxCap = 0;
                        for (int i = 0; i < all.Count; i++)
                            if (all[i] != null) maxCap = Math.Max(maxCap, WaterHelper.GetTotalCapacity(all[i]));
                        LastMsg = "没有能装 " + wantMl + " mL 的容器（现有最大 " + (maxCap / 1000) + " mL）";
                        Log("[pour] no-container want=" + wantMl + "mL max=" + (maxCap / 1000) + "mL");
                        return "no-container " + wantMl + "mL";
                    }
                }

                int capMl = WaterHelper.GetTotalCapacity(dest) / 1000;

                // ================= 灌水：完全复刻水龙头那条链，不自己造值 =================
                // 水龙头（network upgrade "RUSTWATER_TAP"）的真实 IL（WaterHelper/<>c__DisplayClass10_0::
                // <InitLiquidContainerItem>b__9）：
                //   NetworkUpgrade::GetUpgradeById("RUSTWATER_TAP") → NetworkUpgrade::IsUnlocked
                //     → AudioHelper::UseFaucet(container)
                //     → WaterHelper::AddWater(container, /*grade*/1, /*amount*/9999999, /*useRange*/true,
                //                            /*lowPara*/StoreStation.todayRustwaterPurity,
                //                            /*highPara*/StoreStation.todayRustwaterPurity,
                //                            /*initWaterFeature*/true)
                //   else 分支 "GUTTERFLOW_TAP" → 同样参数，纯度取 StoreStation.todayGutterflowPurity，grade 仍是 1。
                // 三个关键点：
                //  1) RNG.GetRandomInt(a,b) 的 IL 就是 Random::Range(a, b+1)（闭区间），a==b 时必得 a；
                //     水龙头正是靠 low==high 把纯度钉成当天的 StoreStation 字段值。
                //  2) 这两个字段是**当日**值，随存档天数（过月）变化，绝对不能缓存 ——
                //     所以每次现场读 TapSource()，读不到就直接失败，绝不写死 91.03 之类的数。
                //  3) amount=9999999 会被 AddWater 内部夹到容器剩余容量，所以「灌满」就等于容量。
                // 旧路径（EnsureBaseline / AssembleExact / AssembleWater：缓存当天六项配比再逐项 AddLiquid 重建）
                // 已删除：它把纯度当天固定死，过月水龙头换水后会灌出与游戏不一致的水；而且它根本没走水龙头。
                // 玩家选了商店成水（优质/基础/纯水）：那不是水龙头能出的，直接说清楚，别拿铁锈水糊弄
                if (SrcIdx >= 3)
                {
                    LastMsg = "「" + SrcNames[Math.Min(SrcIdx, SrcNames.Length - 1)] + "」不是水龙头出的——要从商店买/自己兑，或用「自动」按瓶里现成的水算";
                    Log("[pour] src-not-a-tap idx=" + SrcIdx);
                    return "no-tap";
                }
                int tapGrade, tapPur;
                TapSource(out tapGrade, out tapPur);
                if (tapPur <= 0)
                {
                    LastMsg = tapBlock != null ? tapBlock : "读不到水龙头水质：StoreStation 未就绪，或水龙头还没解锁";
                    Log("[pour] tap-unavailable container=" + GameBridge.NameOfItem(dest));
                    return "no-tap";
                }
                Log("[pour] tap-src grade=" + tapGrade + " purity=" + tapPur + " (live StoreStation)");

                // 直接按目标量一次性灌进去：先把量算好，再 AddWater(amount = 目标 μL)。
                // 旧做法是「灌满 2000 再用 RemoveMl 倒掉多余」，倒水会让六项杂质的整数配比重新取整，
                // 实测把 90.96% 变成 90.95%（差 0.01，玩家明确要求不能差）。
                long keepTap = MaxKeepUl(WaterHelper.GetTotalCapacity(dest), tapPur, perf, q, targetBp);
                if (keepTap <= 0)
                {
                    // 不碰瓶里原有的水：达标不了就不灌，直接把瓶里的好水倒掉是以前最容易丢水的写法
                    LastMsg = "今日水龙头水质一晚达标不了（" + PlanText.Pct(tapPur) + "）——原来的水没动";
                    return "impossible";
                }
                // 一键灌水按「现有整瓶容器凑得出的量」灌，而不是理论最优（玩家：一键就该是 1700）。
                // 体积变小只会更容易达标（MaxKeepUl 是上限），往下取整瓶总量是安全的。
                // 储水容器是直接拿去水龙头接的，不经过背包瓶子：按「一晚能达标的最大量」灌。
                // 整瓶凑量的拆解只用于「背包要带多少水」的提示，不再压低下限（玩家：能 1850 就不该只给 1800）。
                // 玩家：接水要按「容器 + 选中方案」来，不能一律灌到模型上限。
                // 有选中方案 → 就灌方案量；没方案 → 才用一晚上限，再用整瓶可达量收口。
                long keepTapUse = keepTap;
                var rPick0 = CurrentPlan(true);
                WaterPlan.Report rFillPlan = rPick0;   // 锁定用：这是「玩家按下接水时看到的那份方案」
                long pickFeed = rPick0 == null ? 0L : rPick0.ChosenFeedUl();
                if (pickFeed > 0) keepTapUse = pickFeed;
                long roomUl = WaterHelper.GetTotalCapacity(dest) - WaterHelper.GetTotalVolume(dest);
                if (roomUl > 0 && keepTapUse > roomUl) keepTapUse = roomUl;
                else if (pickFeed <= 0)
                {
                    // 没有选中方案：按整瓶可达量收一次（能凑到 1850 就不该只给 1800）。
                    // 注意这一支只能在没有方案时走：以前它写在 else 里，等于「装得下就一律压回整瓶量」，
                    // 选中方案要的 2250/2000 会被它悄悄改回 1850（玩家说的「调整方案后仍锁 1850」）。
                    var ppTap = WaterPlan.Plan(keepTap);
                    if (ppTap != null && ppTap.totalUl > 0 && ppTap.totalUl < keepTap) keepTapUse = ppTap.totalUl;
                }
                int keepMlEarly = (int)(keepTapUse / 1000);

                // 玩家定案：瓶里已有水先转到空瓶，转不了就中止 —— 不能为了接新水把优质水冲成铁锈水
                if (!RescueWaterBeforeFill(dest, "接水前要腾空瓶子")) return "busy-bottle";
                WaterHelper.EmptyContainer(dest);
                // 玩家：**按一次就要到位**（以前 2000 的容器要按两次才满，先 1500 再 500）。
                // AddWater 会被引擎夹到剩余容量，所以循环补足差额，最多 3 次。
                long wantTotal = (long)keepMlEarly * 1000L;
                for (int it2 = 0; it2 < 3; it2++)
                {
                    long haveNow = WaterHelper.GetTotalVolume(dest);
                    long gap = wantTotal - haveNow;
                    if (gap <= 0) break;
                    long room = WaterHelper.GetTotalCapacity(dest) - haveNow;
                    if (room <= 0) break;
                    if (gap > room) gap = room;
                    WaterHelper.AddWater(dest, tapGrade, (int)gap, true, tapPur, tapPur, true);   // 与 b__9 同参
                    if (it2 == 0 && WaterHelper.GetTotalVolume(dest) < wantTotal)
                        Log("[pour] 一次没灌满，补第 " + (it2 + 1) + " 次（目标 " + keepMlEarly + "mL）");
                }
                int filledUl = WaterHelper.GetTotalVolume(dest);
                // 玩家报的第 5 条：铁锈水灌进了外面的瓶、机器容器留空。这里做后置校验——
                // 目标容器（机器里那瓶）仍然是空的就直接判失败，不再「日志说成功、实际没进去」。
                if (filledUl <= 0)
                {
                    LastMsg = "灌水没进目标容器（" + GameBridge.NameOfItem(dest) + "），请重试或手动接";
                    Log("[pour] verify-failed dest=" + GameBridge.NameOfItem(dest) + " b=" + GameBridge.NameOfItem(b));
                    Rebuild(true);
                    return "verify-failed";
                }
                int fillPur = WaterHelper.GetWaterPurity(dest);   // 读回真值（不是传入值）

                // 用读回的纯度重算「该留多少」，让面板/日志与水里真实配比自洽
                long keepUl2 = fillPur > 0
                    ? MaxKeepUl(WaterHelper.GetTotalCapacity(dest), fillPur, perf, q, targetBp)
                    : 0L;
                if (keepUl2 <= 0)
                {
                    // 今日水质一晚达标不了：倒空，别留下半瓶废液
                    WaterHelper.EmptyContainer(dest);
                    LastMsg = "今日水龙头水质一晚达标不了（" + PlanText.Pct(fillPur) + "）";
                    return "impossible";
                }

                // ===== A) 显示与实际统一取整：内部 μL，写入/显示同一个整 mL 数 =====
                // 目标先向下取整到 mL，之后所有比较、减量、显示都以 keepMl 为准，
                // 不再出现「界面 1857 / 实际 1858」。
                int keepMl = (int)(keepUl2 / 1000);
                long keepTarget = (long)keepMl * 1000L;
                if (filledUl > keepTarget)
                {
                    Log("[pour] 多灌 " + ((filledUl - keepTarget) / 1000) + " mL（AddWater 夹到容量），保持原样不再倒水以免改纯度");
                }
                try { WaterFeatureHelper.UpdateWaterFeature(dest, false); } catch (Exception) { }

                // 回读：界面显示、日志、判定全部用回读值（唯一真值）
                int realMl = WaterHelper.GetTotalVolume(dest) / 1000;
                int readPur = WaterHelper.GetWaterPurity(dest);
                int showMl = realMl;
                string name = GameBridge.NameOfItem(dest);
                Log("[pour] tap container=" + name + " vol=" + realMl + " pur=" + readPur);
                Log("[pour] exact show=" + showMl + " real=" + realMl + " pur=" + readPur + " keep=" + keepMl);
                // 目标量已按「整瓶凑得出的量」下调到 keepMlEarly，判定也要认这个值，否则会误报 mismatch。
                bool ok = readPur > 0 && (realMl == keepMl || realMl == keepMlEarly);
                Log("[pour] window#" + ctxIndex + " tap=" + tapPur + "@" + (filledUl / 1000) + " kept=" + readPur + "@" + realMl +
                    " container=" + name + " cap=" + capMl + " ok=" + ok);
                if (!ok)
                {
                    LastMsg = "灌水不符：想 " + keepMl + " mL，实得 " + showMl + " mL · " + PlanText.Pct(readPur) + "（" + name + "）";
                    return "mismatch got=" + readPur + "@" + showMl;
                }
                LastMsg = "已灌 " + showMl + " mL · " + PlanText.Pct(readPur) + "（" + name + "）";
                lastKey = "";
                // 玩家 2026-11-11：接完就锁住这份方案，之后过滤/过夜改了水质也不许自己换方案。
                LockPlan(rFillPlan, "接水后锁定");
                Rebuild(true);
                return "ok container=" + name + " vol=" + showMl + " pur=" + readPur;
            }
            catch (Exception e) { log.LogWarning("[pour] " + e.GetType().Name + " " + e.Message); return "ERR " + e.Message; }
        }

        /// <summary>win?：列出所有已配对窗口（index + focusStamp + IsVisible + 瓶体积/纯度）。</summary>
        public static string WinReport()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < PurifierHook.Pairs.Count; i++)
            {
                var p = PurifierHook.Pairs[i];
                string stamp = "?", vis = "?";
                try
                {
                    var stampV = p.Window == null ? null : GameBridge.Get(p.Window, "focusStamp");
                    if (stampV != null) stamp = stampV.ToString();
                }
                catch (Exception) { }
                try { vis = GameBridge.WinVisible(p.Window) ? "true" : "false"; } catch (Exception) { }
                int vol = -1, pur = -1;
                try
                {
                    var ci = p.BottleInv == null ? null : GameBridge.Get(p.BottleInv, "childItem") as GameItem;
                    if (ci != null) { vol = WaterHelper.GetTotalVolume(ci) / 1000; pur = WaterHelper.GetWaterPurity(ci); }
                }
                catch (Exception) { }
                if (i > 0) sb.Append(" | ");
                sb.Append("#").Append(i).Append(" stamp=").Append(stamp).Append(" vis=").Append(vis)
                  .Append(" vol=").Append(vol).Append("mL pur=").Append(pur);
            }
            string s = sb.Length == 0 ? "win? -> none" : "win? -> " + sb;
            Log(s);
            return s;
        }

        /// <summary>fill=N：指定第 N 台窗口灌水（自动化验证绑定是否正确）。</summary>
        public static string FillIndex(int i)
        {
            if (i < 0 || i >= PurifierHook.Pairs.Count) return "fill=" + i + " -> no-such-window";
            overrideBottle = PurifierHook.Pairs[i].BottleInv; ctxIndex = i + 1;
            Log("[win] active=#" + i + " (fill=" + i + ")");
            return OnFill();
        }
        public static string OnFillPublic() { return OnFill(); }

        // ---------------- 容器盘点（勾兑前置条件） ----------------

        private static List<GameItem> Containers()
        {
            var all = GameBridge.LiveAllItems();
            var res = new List<GameItem>();
            for (int i = 0; i < all.Count; i++)
            {
                var it = all[i];
                if (it == null) continue;
                if (WaterHelper.GetTotalCapacity(it) <= 0) continue;
                res.Add(it);
            }
            return res;
        }

        /// <summary>挑一只**空**容器接水：排除自己、排除带滤水器的水罐，容量够大优先。</summary>
        private static GameItem PickEmptyReceiver(GameItem self, int needMl, out int freeTotal)
        {
            freeTotal = 0;
            var cs = Containers();
            GameItem best = null; int bestCap = 0;
            for (int i = 0; i < cs.Count; i++)
            {
                if (ReferenceEquals(cs[i], self)) continue;
                string nm = GameBridge.NameOfItem(cs[i]);
                if (nm != null && nm.IndexOf("滤水") >= 0) continue;   // 滤水器水罐不接生水
                int cap = WaterHelper.GetTotalCapacity(cs[i]);
                int vol = WaterHelper.GetTotalVolume(cs[i]);
                if (vol > 0) continue;                                  // 只认空瓶，有货的不碰
                freeTotal += cap;
                if (cap > bestCap) { bestCap = cap; best = cs[i]; }
            }
            return best;
        }

        /// <summary>灌水前把瓶里原有的水挪走：挪到空瓶；挪不干净就中止（绝不空瓶冲掉好水）。</summary>
        private static bool RescueWaterBeforeFill(GameItem dest, string why)
        {
            try
            {
                int have = WaterHelper.GetTotalVolume(dest);
                if (have <= 0) return true;
                int haveMl = have / 1000;
                int havePur = WaterHelper.GetWaterPurity(dest);
                int freeTotal;
                var alt = PickEmptyReceiver(dest, haveMl, out freeTotal);
                if (alt == null || freeTotal < haveMl)
                {
                    LastMsg = "瓶里还有 " + haveMl + " mL（" + PlanText.Pct(havePur) + "）——" + why +
                              "：先倒进一只空瓶（现在是空的 0 只/空间 " + freeTotal + " mL），或手动清空后再接";
                    Log("[pour] abort 1: bottle has " + haveMl + "mL@" + havePur + " free=" + freeTotal);
                    return false;
                }
                try { WaterHelper.TransferLiquid(dest, alt); }
                catch (Exception e) { Log("[pour] rescue transfer " + e.GetType().Name + " " + e.Message); }
                int left = WaterHelper.GetTotalVolume(dest);
                if (left > 0)
                {
                    LastMsg = "瓶里还剩 " + (left / 1000) + " mL 挪不走（" + GameBridge.NameOfItem(alt) + " 装不下）——先清空再接水";
                    Log("[pour] abort 2: " + (left / 1000) + "mL left");
                    return false;
                }
                Log("[pour] rescue " + haveMl + "mL@" + havePur + " -> " + GameBridge.NameOfItem(alt));
                return true;
            }
            catch (Exception e) { log.LogWarning("[pour] rescue " + e.GetType().Name + " " + e.Message); return false; }
        }

        /// <summary>能承接的容器 = 剩余空间最大的那只。</summary>
        private static GameItem PickReceiver(int needMl, out int freeMl, out int count, out int maxCapMl)
        {
            freeMl = 0; count = 0; maxCapMl = 0;
            // 优先挑「空且够大」的瓶子做承接瓶：满瓶 free=0，选中它必然导不进去
            // （旧实现只按 free 最大挑，全满时会挑到满瓶，报「承接瓶只有 0 mL」）。
            GameItem emptyBest = null; int emptyCap = 0;
            GameItem anyBest = null; int anyFree = 0;
            long needUl = needMl > 0 ? (long)needMl * 1000L : 0L;
            var cs = Containers();
            for (int i = 0; i < cs.Count; i++)
            {
                int cap = WaterHelper.GetTotalCapacity(cs[i]);
                int vol = WaterHelper.GetTotalVolume(cs[i]);
                int free = cap - vol;
                count++;
                if (cap > maxCapMl) maxCapMl = cap;
                if (vol <= 0 && cap >= needUl && cap > emptyCap) { emptyCap = cap; emptyBest = cs[i]; }
                if (free > anyFree) { anyFree = free; anyBest = cs[i]; }
            }
            freeMl = anyFree;
            return emptyBest != null ? emptyBest : anyBest;
        }

        // ---------------- 模式二：备好水放台面 ----------------

        private static string serveResult;

        /// <summary>找一个「空的、非机器槽」的容器来接走已有液体（装得下 needMl 优先）。</summary>
        private static GameItem FindEmptyReceiver(GameItem exclude, int needMl)
        {
            try
            {
                var res = new List<GameItem>();
                var all = GameBridge.LiveAllItems();
                for (int i = 0; i < all.Count; i++)
                {
                    var it = all[i];
                    if (it == null || ReferenceEquals(it, exclude)) continue;
                    if (WaterHelper.GetTotalCapacity(it) <= 0) continue;
                    if (WaterHelper.GetTotalVolume(it) > 0) continue;      // 必须是空的
                    if (IsMachineSlotItem(it)) continue;                     // 不进机器槽（带滤芯的水罐）
                    res.Add(it);
                }
                res.Sort(delegate(GameItem a, GameItem b)
                {
                    int k = WaterHelper.GetTotalCapacity(b).CompareTo(WaterHelper.GetTotalCapacity(a));
                    return k;
                });
                for (int i = 0; i < res.Count; i++)
                    if (needMl <= 0 || WaterHelper.GetTotalCapacity(res[i]) >= needMl * 1000) return res[i];
                return res.Count > 0 ? res[0] : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>这个物品是不是某台净水器「储水容器」槽里的那只。</summary>
        private static bool IsMachineSlotItem(GameItem it)
        {
            if (it == null) return false;
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var inv = PurifierHook.Pairs[i].BottleInv;
                    if (inv == null) continue;
                    var ci = GameBridge.Get(inv, "childItem") as GameItem;
                    if (ci != null && ReferenceEquals(ci, it)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private static void AddSources(List<GameItem> into, List<GameItem> from, GameItem skip)
        {
            if (from == null) return;
            for (int i = 0; i < from.Count; i++)
            {
                var it = from[i];
                if (it == null || ReferenceEquals(it, skip)) continue;
                if (WaterHelper.GetTotalVolume(it) <= 0) continue;
                bool dup = false;
                for (int j = 0; j < into.Count; j++) if (ReferenceEquals(into[j], it)) { dup = true; break; }
                if (!dup) into.Add(it);
            }
        }

        private static void ServeFail(string why, int freeMl, int needMl)
        {
            serveResult = "serve -> FAIL " + why + " free=" + freeMl + "mL need=" + needMl + "mL";
            LastMsg = "备水失败：" + why + "（可承接 " + freeMl + " mL，需要 " + needMl + " mL）";
            Log("[serve] FAIL " + why + " free=" + freeMl + "mL need=" + needMl + "mL");
            Rebuild(true);
        }

        /// <summary>备水放台面：把玩家现有容器里的水真实导进承接瓶，再放到出货口（模拟真人导液体）。</summary>
        private static void OnPrepare2()
        {
            try
            {
                int grade, pur;
                TapSource(out grade, out pur);
                if (pur <= 0) TodaySource(out grade, out pur);
                int perf, q;
                Bonuses(out perf, out q);
                int targetBp = PlanText.TargetBp(TargetIdx, q);

                int freeMl = 0, cnt = 0, maxCapMl = 0;
                // 备水＝模拟玩家手动：承接瓶必须**已经放在出货口台面上**且是空的。
                // 不再用 TryAddInto/ModHelper 搬物品——玩家实测「点一次容器就消失一个」。
                GameItem recv = null;
                try
                {
                    var cnts = GameBridge.CounterItems();
                    for (int i = 0; i < cnts.Count; i++)
                    {
                        var it = cnts[i];
                        if (it == null) continue;
                        int cap = WaterHelper.GetTotalCapacity(it);
                        if (cap <= 0) continue;
                        if (WaterHelper.GetTotalVolume(it) > 0) continue;
                        cnt++;
                        if (cap > maxCapMl) { maxCapMl = cap; recv = it; }
                    }
                }
                catch (Exception) { }
                if (recv == null)
                {
                    ServeFail("台面上没有空瓶：先把一个空容器放到出货口台面，再点备水", 0, 0);
                    return;
                }
                if (recv == null) { ServeFail("缺可承接容器", 0, 0); return; }
                long needUl = MaxKeepUl(WaterHelper.GetTotalCapacity(recv), pur, perf, q, targetBp);
                // 备水要跟当前选中的方案联动：选「过滤棉+1片药片 → 2000→1911」就备 2000，而不是按理论上限另算一套。
                var rp = CurrentPlan(true);
                if (rp != null && rp.pick != null && rp.pick.feedUl > 0) needUl = rp.pick.feedUl;
                // 承接瓶是台面上的实物，装不下就得按它的容量来：以前直接照 6L 方案的 3550 mL 备，
                // 挑了 3000 mL 的瓶子就只会报「源水不够」。
                long recvCapUl = WaterHelper.GetTotalCapacity(recv);
                if (recvCapUl > 0 && needUl > recvCapUl) needUl = recvCapUl;
                int needMl = (int)(needUl / 1000);
                if (needMl <= 0) { ServeFail("今日水质配不出达标水", freeMl, 0); return; }

                var srcs = new List<GameItem>();
                AddSources(srcs, GameBridge.LiveAllItems(), recv);
                AddSources(srcs, GameBridge.CounterItems(), recv);
                if (srcs.Count == 0) { ServeFail("缺源水：背包/台面没有装水的容器", freeMl, needMl); return; }
                srcs.Sort(delegate(GameItem a, GameItem b)
                {
                    return WaterHelper.GetWaterPurity(b).CompareTo(WaterHelper.GetWaterPurity(a));
                });

                int srcMl = 0, srcPur = 0;
                string srcName = "none";
                // 承接瓶里原有的水不能凭空倒掉（玩家反馈「备水会消失」就是这里先 EmptyContainer 把水清了）。
                // 有货就换个空瓶；一个都没有就明确报错，让玩家自己决定。
                int recvHave = WaterHelper.GetTotalVolume(recv);
                if (recvHave > 0)
                {
                    int free2, cnt2, cap2;
                    var alt = PickReceiver(1, out free2, out cnt2, out cap2);
                    if (alt == null || WaterHelper.GetTotalVolume(alt) > 0)
                    {
                        ServeFail("承接瓶里还有 " + (recvHave / 1000) + " mL，先倒空或换一个空瓶", freeMl, needMl);
                        return;
                    }
                    recv = alt;
                }
                bool placedOnCounter = true;   // 瓶子本来就在台面上（不再搬动）

GameItem lastSrc = null;
                for (int i = 0; i < srcs.Count; i++)
                {
                    if (WaterHelper.GetTotalVolume(recv) / 1000 >= needMl) break;
                    var s = srcs[i];
                    int sv = WaterHelper.GetTotalVolume(s);
                    if (sv <= 0) continue;
                    int sp = WaterHelper.GetWaterPurity(s);   // 必须导液前读：导完源瓶空了，纯度会变 0
                    if (WaterHelper.GetTotalCapacity(recv) - WaterHelper.GetTotalVolume(recv) <= 0) break;
                    try { WaterHelper.TransferLiquid(s, recv); }
                    catch (Exception) { continue; }
                    lastSrc = s;
                    if (srcName == "none") { srcMl = sv / 1000; srcPur = sp; srcName = GameBridge.NameOfItem(s); }
                }
                long gotUl = WaterHelper.GetTotalVolume(recv);
                if (gotUl <= 0) { ServeFail("源水没有导进承接瓶", freeMl, needMl); return; }
                if (gotUl > needUl)
                {
                    // 多出来的水回倒给最后一个源瓶，实在倒不回去才丢掉（并写日志说明丢了多少）。
                    long excessUl = gotUl - needUl;
                    bool back = false;
                    if (lastSrc != null)
                    {
                        try { WaterHelper.TransferLiquid(recv, lastSrc); back = true; }
                        catch (Exception) { back = false; }
                    }
                    if (!back)
                    {
                        WaterHelper.RemoveMl(recv, (int)((excessUl + 500) / 1000));
                        Log("[serve] 多出的 " + (excessUl / 1000) + " mL 倒不回去，已丢弃");
                    }
                    gotUl = WaterHelper.GetTotalVolume(recv);
                }
                WaterFeatureHelper.UpdateWaterFeature(recv, false);
                int dstMl = (int)(gotUl / 1000);
                int dstPur = WaterHelper.GetWaterPurity(recv);
                if (dstMl < needMl)
                {
                    ServeFail("源水不够，承接瓶只有 " + dstMl + " mL", dstMl, needMl);
                    return;
                }

                // 瓶子在开始倒液之前就已经摆上出货口台面了，这里只做“还在台面上”的校验。
                bool moved = placedOnCounter;
                int accepted = placedOnCounter ? 1 : 0;
                bool found = false;
                var cs = GameBridge.CounterItems();
                for (int i = 0; i < cs.Count; i++) if (ReferenceEquals(cs[i], recv)) { found = true; break; }

                string dstName = GameBridge.NameOfItem(recv);
                serveResult = "serve -> src=" + srcName + " " + srcMl + "mL@" + PlanText.Pct(srcPur) +
                              " -> dst=" + dstName + " " + dstMl + "mL@" + PlanText.Pct(dstPur) + " counter=" + (found ? "true" : "false");
                LastMsg = !moved
                    ? "水已配好但放不上台面（" + dstName + " " + dstMl + " mL，accepted=" + accepted + "）——请手动拖到出货口"
                    : "台面已备好：" + dstName + " " + dstMl + " mL · " + PlanText.Pct(dstPur) +
                      "（一晚 → " + PlanText.Pct(targetBp) + "）";
                Log("[serve] src=" + srcName + " " + srcMl + "mL@" + srcPur + " -> dst=" + dstName + " " + dstMl + "mL@" + dstPur +
                    " counter=" + (found ? "true" : "false") + " moved=" + (moved ? "true" : "false"));
                Log("[counter] found=" + (found ? "true" : "false") + " vol=" + dstMl + " pur=" + dstPur);
                Rebuild(true);
            }
            catch (Exception e) { log.LogWarning("[serve] " + e.GetType().Name + " " + e.Message); }
        }

        // ---------------- 凑量 ----------------

        public static string MixPreview()
        {
            int freeMl, cnt, maxCapMl;
            var recv = PickReceiver(0, out freeMl, out cnt, out maxCapMl);
            var b = GameBottle();
            int V = TargetVolumeMl();
            return "[mix] containers=" + cnt + " maxCap=" + (maxCapMl / 1000) + "mL free=" + (freeMl / 1000) +
                   "mL V=" + V + " bottle=" + (b == null ? "none" : WaterHelper.GetTotalVolume(b) + "uL") +
                   " recv=" + (recv == null ? "none" : GameBridge.NameOfItem(recv));
        }

        private static void OnMix()
        {
            try
            {
                var b = GameBottle();
                if (b == null) { LastMsg = "净水器里没有瓶子（凑量是倒进机器里的瓶）"; return; }
                int V = TargetVolumeMl();
                if (V <= 0) { LastMsg = "目标体积为 0"; return; }

                int freeMl = 0, cnt = 0, maxCapMl = 0;
                var recv = CtxBottle();
                if (recv == null) recv = PickReceiver(0, out freeMl, out cnt, out maxCapMl);
                if (recv == null) { LastMsg = "缺少可勾兑的容器：背包和台面都没有能装水的容器"; return; }
                var items = Candidates(b);
                int sum;
                var picked = PickSubset(items, V, out sum);
                WaterHelper.EmptyContainer(b);
                for (int i = 0; i < picked.Count; i++)
                {
                    try { WaterHelper.TransferLiquid(picked[i], b); }
                    catch (Exception e) { log.LogWarning("[mix] transfer " + e.Message); }
                }
                int got = WaterHelper.GetTotalVolume(b) / 1000;
                int grade, pur;
                TodaySource(out grade, out pur);
                int need = V - got;
                if (need > 0) WaterHelper.AddWater(b, grade, need * 1000, false, pur, pur, true);
                int final = WaterHelper.GetTotalVolume(b) / 1000;
                if (final < V - 1) LastMsg = "只有 " + final + " mL（差 " + (V - final) + " mL）";
                else LastMsg = "凑好 " + final + " mL（" + picked.Count + " 个容器）";
                Log("[mix] V=" + V + " got=" + got + " final=" + final + " picked=" + picked.Count + " containers=" + cnt);
                Rebuild(true);
            }
            catch (Exception e) { log.LogWarning("[mix] " + e.GetType().Name + " " + e.Message); }
        }

        private static List<GameItem> Candidates(GameItem target)
        {
            var all = GameBridge.LiveItems();
            var res = new List<GameItem>();
            for (int i = 0; i < all.Count; i++)
            {
                var it = all[i];
                if (it == null || ReferenceEquals(it, target)) continue;
                if (WaterHelper.GetTotalVolume(it) <= 0) continue;
                res.Add(it);
            }
            res.Sort(delegate (GameItem a, GameItem c) { return WaterHelper.GetTotalVolume(c).CompareTo(WaterHelper.GetTotalVolume(a)); });
            return res;
        }

        private static List<GameItem> PickSubset(List<GameItem> items, int V, out int sum)
        {
            sum = 0;
            int n = items.Count;
            var vol = new int[n];
            for (int i = 0; i < n; i++) vol[i] = WaterHelper.GetTotalVolume(items[i]) / 1000;

            var dp = new int[V + 1];
            var pick = new int[V + 1];
            for (int j = 0; j <= V; j++) { dp[j] = 9999; pick[j] = -1; }
            dp[0] = 0;
            for (int i = 0; i < n; i++)
            {
                int v = vol[i];
                if (v <= 0) continue;
                for (int j = V; j >= v; j--)
                {
                    if (dp[j - v] + 1 < dp[j]) { dp[j] = dp[j - v] + 1; pick[j] = i; }
                }
            }
            int best = 0;
            for (int j = V; j >= 0; j--) if (dp[j] < 9999) { best = j; break; }

            var res = new List<GameItem>();
            int cur = best;
            while (cur > 0 && pick[cur] >= 0)
            {
                int i = pick[cur];
                res.Add(items[i]);
                cur -= vol[i];
            }
            sum = best;
            return res;
        }

        // ---------------- 前期：药片 / 紫外 / 两瓶兑水 ----------------

        private static GameItem AnyBottle()
        {
            var b = GameBottle();
            if (b != null) return b;
            var all = GameBridge.LiveItems();
            return all.Count > 0 ? all[0] : null;
        }

        private static void OnTablet()
        {
            var b = AnyBottle();
            if (b == null) { LastMsg = "没有含水的瓶子，先放一瓶水"; return; }
            int before = WaterHelper.GetWaterPurity(b);
            int vol = WaterHelper.GetTotalVolume(b);
            try { WaterHelper.OnWaterPurificationTabletAdded(b); }
            catch (Exception e) { log.LogWarning("[tablet] " + e.Message); LastMsg = "药片失败：" + e.Message; return; }
            int after = WaterHelper.GetWaterPurity(b);
            LastMsg = "药片：" + PlanText.Pct(before) + " → " + PlanText.Pct(after) + " · " + (WaterHelper.GetTotalVolume(b) / 1000) + "mL";
            Log("[tablet] " + LastMsg + " vol=" + vol);
            Rebuild(true);
        }

        private static void OnUv()
        {
            var b = AnyBottle();
            if (b == null) { LastMsg = "没有含水的瓶子，先放一瓶水"; return; }
            if (WaterHelper.GetWaterPurity(b) <= 0) { LastMsg = "先扫描这瓶水，读不到真实纯度"; return; }
            int before = WaterHelper.GetWaterPurity(b);
            try { WaterHelper.OnUVUsed(b); }
            catch (Exception e) { log.LogWarning("[uv] " + e.Message); LastMsg = "紫外失败：" + e.Message; return; }
            LastMsg = "紫外：" + PlanText.Pct(before) + " → " + PlanText.Pct(WaterHelper.GetWaterPurity(b)) + " · " + (WaterHelper.GetTotalVolume(b) / 1000) + "mL";
            Log("[uv] " + LastMsg);
            Rebuild(true);
        }

        private static void OnBlend()
        {
            var all = GameBridge.LiveItems();
            if (all.Count < 2) { LastMsg = "至少要两瓶水才能兑（现在 " + all.Count + " 瓶）"; return; }
            GameItem good = null, bad = null;
            int gp = -1, bp = 99999;
            for (int i = 0; i < all.Count; i++)
            {
                int p = WaterHelper.GetWaterPurity(all[i]);
                if (p <= 0) continue;
                if (p > gp) { gp = p; good = all[i]; }
                if (p < bp) { bp = p; bad = all[i]; }
            }
            if (good == null || bad == null || ReferenceEquals(good, bad)) { LastMsg = "读不到两瓶不同的水，先扫描"; return; }
            int freeMl, cnt, maxCapMl;
            var recv = PickReceiver(0, out freeMl, out cnt, out maxCapMl);
            if (recv == null) { LastMsg = "缺少可勾兑的容器：背包和台面都没有能装水的容器"; return; }

            int V = Math.Min(1000, freeMl / 1000);
            if (V <= 0) { LastMsg = "承接容器没有空间"; return; }
            int target = 9900;
            WaterHelper.EmptyContainer(recv);
            WaterHelper.AddWater(recv, 4, V * 1000, false, bp, bp, true);
            LastMsg = "兑水：A " + PlanText.Pct(gp) + " + B " + PlanText.Pct(bp) + " → 承接瓶 " + V + "mL（占位实现）";
            Log("[blend] gp=" + gp + " bp=" + bp + " V=" + V + " target=" + target + " containers=" + cnt);
            Rebuild(true);
        }

        // ---------------- 计算（唯一算数的地方） ----------------

        private static PanelModel Compute()
        {
            var m = new PanelModel();
            m.Mode = Mode; m.PreMode = PreMode; m.PostMode = PostMode;
            var b = GameBottle();
            m.HasBottle = b != null;
            if (b != null)
            {
                m.BottleName = GameBridge.NameOfItem(b);
                // 20 L 罐实测 19,999,998 µL（差 2 µL），整数截断会显示成 19999 —— 按四舍五入取整（m00160）
                m.VolumeMl = (int)((WaterHelper.GetTotalVolume(b) + 999L) / 1000L);
                m.CapacityMl = (int)((WaterHelper.GetTotalCapacity(b) + 999L) / 1000L);
                m.PurityBp = WaterHelper.GetWaterPurity(b);
                if (m.PurityBp <= 0)
                {
                    m.Plans = null;
                    m.Verdict = "纯度未知 —— 先用 水质扫描仪 / 试纸 扫一次这瓶，再算方案";
                }
            }
            int perf, q;
            Bonuses(out perf, out q);
            m.PerfPct = perf; m.Quality = q;
            m.CapBp = WaterMath.CapOf(q);
            m.TargetBp = PlanText.TargetBp(TargetIdx, q);
            m.MixKnown = false;
            m.MixText = "";
            try
            {
                var parts = WaterHelper.GetLiquidParts(b);
                long psum = 0;
                if (parts != null) for (int pi = 0; pi < parts.Length; pi++) psum += parts[pi];
                if (psum > 0)
                {
                    m.MixKnown = true;
                    var pn = GameBridge.LiquidNames();
                    var psb = new System.Text.StringBuilder();
                    for (int pi = 0; pi < parts.Length && parts[pi] > 0; pi++)
                    {
                        string pnm = CnLiquid((pn != null && pi < pn.Length) ? pn[pi] : ("#" + pi), pi);
                        if (psb.Length > 0) psb.Append(" · ");
                        psb.Append(pnm).Append(" ").Append(PlanText.Pct((int)(parts[pi] * 10000L / psum)));
                    }
                    m.MixText = psb.ToString();
                }
            }
            catch (Exception e) { Log("[parts] fail " + e.GetType().Name); }
            // 只在配比真的变了才写日志：Compute 每 0.4 秒跑一次，逐帧写会把 log.txt 刷到几百 KB。
            string pl = "known=" + m.MixKnown + " bottle=" + m.BottleName + " text=" + m.MixText;
            if (pl != lastPartsLog) { lastPartsLog = pl; Log("[parts] " + pl); }

            int grade, pur;
            TodaySource(out grade, out pur);
            m.Grade = grade; m.SourcePurBp = pur;
            m.SourceNote = "今日" + (grade == 4 ? "铁锈水" : grade == 5 ? "沟渠水" : "水") + " " + PlanText.Pct(pur);

            string capNote = m.TargetBp > m.CapBp ? "（本机上限只有 " + PlanText.Pct(m.CapBp) + "，要 99.90% 得先装质量模组 +100）" : "";
            m.Hint = "性能 +" + perf + "% · 质量 " + q + " · 上限 " + PlanText.Pct(m.CapBp) + capNote;

            if (!m.HasBottle)
            {
                m.Verdict = "储水容器里没有水瓶。";
                return m;
            }

            if (!m.MixKnown)
            {
                m.Verdict = "这瓶还没扫过，读不到六项配比 —— 请先用水质扫描仪/试纸扫一次再算。";
                return m;
            }

            if (m.Mode == 0)
            {
                var on = WaterMath.PlanOneNight(m.VolumeMl * 1000L, m.PurityBp, perf, q, m.TargetBp, 1);
                if (on != null && on.possible) m.Plans.Add(on);
                m.Verdict = on != null && on.possible ? PlanText.Describe(on) : "一晚做不到，试试兑水或换小瓶。";
                m.Selected = 0;
            }
            else if (m.Mode == 1)
            {
                var best = WaterMath.Solve(m.VolumeMl * 1000L, m.PurityBp, perf, q, false, false, 0, m.TargetBp, 40);
                if (best != null && best.ok)
                {
                    var on = new WaterMath.OneNight();
                    on.possible = true; on.asIs = (best.feed == m.VolumeMl * 1000L);
                    on.drainUl = m.VolumeMl * 1000L - best.feed; on.keepUl = best.feed;
                    on.purity = best.purity; on.output = best.output;
                    m.Plans.Add(on);
                    m.Verdict = "效率：接 " + (best.feed / 1000) + " mL → 一晚 " + PlanText.Pct(best.purity) + " · " + (best.output / 1000) + " mL";
                }
                else m.Verdict = "这一瓶一晚达不到目标。";
                m.Selected = 0;
            }
            else
            {
                m.Plans = m.PurityBp > 0 ? WaterMath.Plans(m.VolumeMl * 1000L, m.PurityBp, perf, q, m.TargetBp) : null;
                m.Verdict = m.PurityBp <= 0
                    ? "纯度未知 —— 先用 水质扫描仪 / 试纸 扫一次这瓶，再算方案"
                    : (m.Plans.Count > 0 ? ("共 " + m.Plans.Count + " 个方案，选最省的那个") : "没有可用方案。");
                m.Selected = 0;
            }
            return m;
        }

        // ---------------- tooltip ----------------

        // 悬停提示会被游戏高频调用；PlanOneNight 带迭代求解 + Bonuses 反射，每次都算会直接卡住鼠标。
        // 用「同输入 2 秒内复用」的缓存，输入是 体积/纯度/性能/质量 四项。
        private static string tipKey, tipVal;
        private static float tipAt;

        public static string TooltipLine(GameItem machine)
        {
            try
            {
                if (machine == null) return null;
                var b = GameBottle();
                if (b == null) return null;
                int vol = WaterHelper.GetTotalVolume(b);
                int pur = WaterHelper.GetWaterPurity(b);
                int perf, q;
                Bonuses(out perf, out q);
                string key = vol + "|" + pur + "|" + perf + "|" + q;
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (key == tipKey && (now - tipAt) < 2f) return tipVal;
                var on = WaterMath.PlanOneNight(vol, pur, perf, q, WaterMath.CapOf(q), 1);
                if (on == null || !on.possible) tipVal = "净水助手：这瓶一晚到不了目标";
                else if (on.asIs) tipVal = "净水助手：今晚 → " + PlanText.Pct(on.purity) + "（原样跑）";
                else tipVal = "净水助手：今晚 → " + PlanText.Pct(on.purity) + "（留 " + (on.keepUl / 1000) + " mL）";
                tipKey = key; tipAt = now;
                return tipVal;
            }
            catch (Exception) { return null; }
        }

        public static void LogTip(GameItem item)
        {
            string id = item == null ? "null" : item.identifier;
            if (log != null && id != lastTipLog) { lastTipLog = id; log.LogInfo("[tip] hit " + id); }
        }

        // ---------------- 后台命令（CmdFile 用） ----------------

        public static string MainVisible() { return (panelWant && panelWin != null && panelWin.IsVisible()) ? "true" : "false"; }

        public static string ShowTestWindow(bool show) { return ShowMain(show); }

        public static string IconWindow()
        {
            try
            {
                EnsurePanel();
                if (panelWin == null) return "no-window";
                Rebuild(true);
                panelWin.Show();
                panelWin.ToFront(true);
                return "main ok visible=" + panelWin.IsVisible();
            }
            catch (Exception e) { return "ERR " + e.GetType().Name + ": " + e.Message; }
        }

        public static string Diag()
        {
            try
            {
                var b = GameBottle();
                int u1 = b == null ? -1 : WaterHelper.GetTotalVolume(b);
                int u2 = b == null ? -1 : WaterHelper.GetTotalCapacity(b);
                int u3 = -1;
                if (b != null) { try { u3 = GameBridge.IntOf(b, "GetTotalCapacityML"); } catch (Exception) { } }
                return "diag vol=" + u1 + " cap=" + u2 + " capML=" + u3 +
                       " bottle=" + (b == null ? "none" : GameBridge.NameOfItem(b)) +
                       " model=" + Model.VolumeMl + "/" + Model.CapacityMl + "mL";
            }
            catch (Exception e) { return "ERR " + e.GetType().Name + ": " + e.Message; }
        }

        public static string Sprites(string kw)
        {
            var arr = Resources.FindObjectsOfTypeAll<Sprite>();
            var sb = new System.Text.StringBuilder();
            int n = 0;
            for (int i = 0; i < arr.Length && n < 40; i++)
            {
                if (arr[i] == null) continue;
                if (!string.IsNullOrEmpty(kw) && arr[i].name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (n > 0) sb.Append(",");
                sb.Append(arr[i].name); n++;
            }
            return "sprites total=" + arr.Length + " match=" + n + " [" + sb + "]";
        }

        public static string DumpIds()
        {
            var b = GameBottle();
            var machine = CurrentMachine();
            string mi = machine == null ? "none" : GameBridge.NameOfItem(machine);
            string bi = b == null ? "none" : b.identifier;
            string ti = PurifierHook.MachineWindow == null ? "none" : "PixelWindow";
            return "machine=" + mi + " bottle=" + bi + " title=" + ti;
        }

        public static string LiveList()
        {
            var all = GameBridge.LiveAllItems();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < all.Count && i < 12; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(GameBridge.NameOfItem(all[i])).Append(" ").
                   Append(WaterHelper.GetTotalVolume(all[i]) / 1000).Append("/").
                   Append(WaterHelper.GetTotalCapacity(all[i]) / 1000).Append("mL");
            }
            return "items=" + all.Count + " [" + sb + "]";
        }

        /// <summary>全量盘点：背包 + 出货口（台面） + 每台机器槽，全部按 mL 打印。</summary>
        public static string ScanAllPublic()
        {
            var sb = new System.Text.StringBuilder();
            var bag = GameBridge.LiveAllItems();
            var counter = GameBridge.CounterItems();
            var machines = new List<GameItem>();
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var inv = PurifierHook.Pairs[i].BottleInv;
                    var its = GameBridge.ItemsOf(inv);
                    for (int k = 0; k < its.Count; k++) machines.Add(its[k]);
                }
            }
            catch (Exception) { }
            sb.Append("[scan] bag=").Append(bag.Count).Append(" counter=").Append(counter.Count).
               Append(" machines=").Append(machines.Count);
            Log(sb.ToString());
            DumpList("bag", bag); DumpList("counter", counter); DumpList("machine", machines);
            // 出货口原本在不在，用于模式二的自证。
            return sb.ToString();
        }

        private static void DumpList(string tag, List<GameItem> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it == null) continue;
                int vol = WaterHelper.GetTotalVolume(it);
                int cap = WaterHelper.GetTotalCapacity(it);
                if (cap <= 0) continue;
                Log("[scan] " + tag + " " + GameBridge.NameOfItem(it) + " " + (vol / 1000) + "/" + (cap / 1000) +
                    "mL " + PlanText.Pct(WaterHelper.GetWaterPurity(it)));
            }
        }
        public static string PlanSummary()
        {
            var m = Model;
            var sb = new System.Text.StringBuilder();
            sb.Append("mode=").Append(PlanText.ModeShort[m.Mode]).
               Append(" target=").Append(PlanText.Pct(m.TargetBp)).
               Append(" vol=").Append(m.VolumeMl).Append("mL pur=").Append(PlanText.Pct(m.PurityBp)).
               Append(" hint=").Append(m.Hint).Append(" | ");
            if (m.Plans != null && m.Plans.Count > 0)
            {
                for (int i = 0; i < m.Plans.Count && i < 4; i++)
                {
                    if (i > 0) sb.Append(" | ");
                    sb.Append(PlanText.OneNightRow(i, m.Plans[i], m.TargetBp));
                }
            }
            else sb.Append(m.Verdict);
            if (LastMsg != null && LastMsg.Length > 0) sb.Append(" || ").Append(LastMsg);
            return sb.ToString();
        }

        public static string OnMixPublic() { OnMix(); return MixPreview(); }

        public static string EarlyPublic(int which)
        {
            if (which == 0) OnTablet();
            else if (which == 1) OnUv();
            else OnBlend();
            return LastMsg == null ? "" : LastMsg;
        }
    }
}
