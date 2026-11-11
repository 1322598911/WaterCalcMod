using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace WaterCalcMod
{
    /// <summary>
    /// 把「水商手册」做成游戏原生的说明书：注入 StoreBookData → 用 ingot_book 工厂造一本临时书 →
    /// PaperUIManager.OpenBook 打开。整个界面（纸面、装订孔、翻页按钮）都是游戏自己的 prefab，
    /// 与玩家手里的《仿生机械手鉴定指南》完全同源。
    /// </summary>
    public static class WaterBook
    {
        private const string BookId = "watercalc_guide";
        private const string ItemKey = "tutorial_book";

        private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static ManualLogSource log;
        private static object bookItem;   // GameItem
        private static string last = "none";

        public static void Install(ManualLogSource l) { log = l; }

        private static void Log(string s) { if (log != null) log.LogInfo("[book] " + s); }

        /// <summary>每页标题（书页上方那行小字）。</summary>
        private static readonly string[] PageTitles =
        {
            "一 · 前期 2 L 瓶",
            "二 · 中期 6 L 桶",
            "三 · 后期 海德拉",
            "四 · 上限与耗材",
            "五 · 顺序",
            "六 · 水商利润"
        };

        /// <summary>正文：直接复用配方窗的分页文本，保证两边数字一致。</summary>
        /// <remarks>
        /// 正文生成要跑整包规划（每页一次），曾经是「点手册卡 32 秒」的唯一来源。
        /// 现在两层挡着：PurifierUi.CurrentPlan 会复用 0.25 秒内的同一份 Report，
        /// 这里再把生成结果按「整机质量」缓存，翻页/重开手册不再重算。
        /// </remarks>
        private static string[] bodiesCache;
        private static int bodiesCacheQ = -999;

        private static string[] PageBodies(int quality)
        {
            if (bodiesCache != null && bodiesCacheQ == quality) return bodiesCache;
            var res = new string[PageTitles.Length];

            for (int i = 0; i < res.Length; i++)
            {
                string body = null;
                try
                {
                    var lines = PurifierUi.RecipePage(i + 1);
                        if (lines != null && lines.Length > 0) body = string.Join("\n", lines);
                    }
                catch (Exception e) { Log("页 " + (i + 1) + " 生成失败 " + e.GetType().Name); }
                if (string.IsNullOrEmpty(body)) body = "（第 " + (i + 1) + " 页暂无内容）";
                // 书页正文里不要留 RichText 富文本标签，原生书页只吃纯文本。
                res[i] = Strip(body);
            }
            bodiesCache = res;
            bodiesCacheQ = quality;
            return res;
        }

        /// <summary>整机质量：手册第 1/3 页的数字跟它走，质量变了才需要重生成正文。</summary>
        private static int MachineQuality()
        {
            try { return PurifierUi.QualityForBook(); } catch (Exception) { return -999; }
        }

        /// <summary>标题跟着正文走：取正文首行，去掉 "#N. " 前缀，保证标题与内容永远一致。</summary>
        private static string[] TitlesFromBodies(string[] bodies)
        {
            var res = new string[bodies == null ? 0 : bodies.Length];
            for (int i = 0; i < res.Length; i++)
            {
                string t = "";
                string b = bodies[i] == null ? "" : bodies[i];
                int nl = b.IndexOf('\n');
                t = nl > 0 ? b.Substring(0, nl) : b;
                t = t.Trim();
                if (t.StartsWith("#"))
                {
                    int dot = t.IndexOf('.');
                    if (dot > 0) t = t.Substring(dot + 1).Trim();
                }
                if (t.Length == 0) t = (i < PageTitles.Length ? PageTitles[i] : ("第 " + (i + 1) + " 页"));
                res[i] = t;
            }
            return res;
        }

        /// <summary>
        /// 去掉 TMP 富文本标签（书页正文只吃纯文本）。
        /// 只认「&lt; 后紧跟字母或 /」的标签；`&lt;15 → 99.01%`、`&lt;90%` 这种数学小于号必须原样保留，
        /// 否则整行会被当成标签吃掉（实测第 1 页只剩半句）。
        /// </summary>
        private static string Strip(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<' && i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] == '/'))
                {
                    int close = s.IndexOf('>', i + 1);
                    if (close > 0 && close - i <= 48) { i = close; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>构造并登记 StoreBookData（重复调用只会覆盖，不会重复注册）。</summary>
        private static bool EnsureData(out string err)
        {
            err = null;

            // 直接 new + 直赋 public 字段：官方文档写明「字面游戏类型 + 字面成员名」属直接调用，
            // 不需要任何能力；StoreBookData 的 title/subtitle/body/pageCount/bookDatas 全是 public。
            StoreBookData d = new StoreBookData();
            string[] bodies = PageBodies(MachineQuality());
            string[] titles = TitlesFromBodies(bodies);
            // ★缓存数组不能再被就地改写：PageBodies 返回的是 bodiesCache 本体，
            // 之前直接 bodies[i] = bodies[i].Substring(...) 会把缓存里的首行剥掉，
            // 第二次开手册时标题没了、页数跟着退化（玩家 2026-11-11 报「手册换不了页面」）。
            bodies = bodies == null ? new string[0] : (string[])bodies.Clone();
            for (int i = 0; i < bodies.Length; i++)
            {
                int nl = bodies[i] == null ? -1 : bodies[i].IndexOf('\n');
                if (nl > 0) bodies[i] = bodies[i].Substring(nl + 1);   // 首行已当标题，正文里去掉，避免重复
            }
            d.title = "水商净水手册";
            d.subtitle = titles;
            d.body = bodies;
            d.pageCount = titles.Length;
            Log("手册登记 pageCount=" + d.pageCount + " subtitle=" + (d.subtitle == null ? 0 : d.subtitle.Length) + " body=" + (d.body == null ? 0 : d.body.Length));

            if (StoreBookData.bookDatas == null)
            {
                StoreBookData.bookDatas = new Dictionary<string, StoreBookData>();
                Log("bookDatas 为空，已新建 Dictionary");
            }
            try { StoreBookData.bookDatas[BookId] = d; }
            catch (Exception e) { err = "dict " + e.GetType().Name; return false; }
            return true;
        }

        /// <summary>造/复用一本临时书物品（不进背包，双击图标时才用）。</summary>
        private static object EnsureItem(out string err)
        {
            err = null;
            if (bookItem != null) return bookItem;

            // 直连：DirectoryMaster.Create<GameItem>(string) 与 BookHelper.InitBookItem(GameItem,string)
            // 都是 public static，属「字面游戏类型直接调用」，无需任何能力。
            try { bookItem = DirectoryMaster.Create<GameItem>(ItemKey); }
            catch (Exception e)
            {
                var ie = e.InnerException ?? e;
                err = "Item(" + ItemKey + ") " + ie.GetType().Name + ": " + ie.Message;
                return null;
            }
            if (bookItem == null) { err = "Item(" + ItemKey + ") -> null"; return null; }

            // 把这本书的 BOOK_ID 换成我们自己的手册。
            try { BookHelper.InitBookItem((GameItem)bookItem, BookId); }
            catch (Exception e)
            {
                var ie = e.InnerException ?? e;
                err = "InitBookItem " + ie.GetType().Name + ": " + ie.Message;
                return null;
            }
            return bookItem;
        }

        private static object Manager()
        {
            // 直连 public static 属性 PaperUIManager.Instance。
            return PaperUIManager.Instance;
        }

        /// <summary>打开手册并翻到第 page 页（1 起）。</summary>
        public static string Open(int page)
        {
#if DEV
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
#endif
            try
            {
                string err;
                if (!EnsureData(out err)) { last = "data-fail " + err; return "book -> " + last; }
#if DEV
                Log("[book] data " + sw.ElapsedMilliseconds + "ms");
#endif
                object it = EnsureItem(out err);
                if (it == null) { last = "item-fail " + err; return "book -> " + last; }
#if DEV
                Log("[book] item " + sw.ElapsedMilliseconds + "ms");
#endif
                object pum = Manager();
                if (pum == null) { last = "no PaperUIManager"; return "book -> " + last; }

                // 打开书之前先把屏幕上残留的纸面平板（日历/夜间报告/状态 那张）收掉，
                // 否则书会叠在它上面、它继续挡着整个店面的点击。
                try { GameBridge.Call(pum, "CloseAllPaperUI"); } catch (Exception) { }
                GameBridge.Call(pum, "OpenBook", it);
                string fontNote = "";
#if DEV
                Log("[book] openBook " + sw.ElapsedMilliseconds + "ms");
#endif
                // OpenBook 内部走的是 CURRENT_BOOK_PAGE 标签，新造的书是 -1 会停在目录页；
                // 这里无条件翻到目标页（第 1 页就是索引 0），否则点配方图标先看到的是目录。
                GameBridge.Call(pum, "BookOnFlipPage", page - 1);

                object cur = GameBridge.Get(pum, "currentBook");
                object panel = GameBridge.Get(pum, "bookPanel");
                int fontN = FixFonts(panel);
#if DEV
                Log("[book] fonts " + sw.ElapsedMilliseconds + "ms n=" + fontN);
#endif
                if (!string.IsNullOrEmpty(lastFontName)) fontNote = lastFontName;
                bool vis = false;
                try { if (panel is UnityEngine.GameObject) vis = ((UnityEngine.GameObject)panel).activeSelf; }
                catch (Exception) { }

                last = "open page=" + page + " panel=" + (vis ? "visible" : "hidden") + " current=" + (cur != null ? "yes" : "no") + " font=" + fontNote;
                Log(last);
                return "book -> " + last;
            }
            catch (Exception e)
            {
                last = "ERR " + e.GetType().Name + ": " + e.Message;
                Log(last);
                return "book -> " + last;
            }
        }


        private static UnityEngine.Font osFont = null;
        private static string lastFontName = "";
        private static int FixFonts(object panel)
        {
            int n = 0;
            try
            {
                UnityEngine.GameObject go = panel as UnityEngine.GameObject;
                if (go == null && panel is UnityEngine.Component) go = ((UnityEngine.Component)panel).gameObject;
                if (go == null) { Log("fixfont panel=" + (panel == null ? "null" : panel.GetType().Name)); return 0; }
                try
                {
                    var tr = go.transform;
                    int cnt = tr.childCount;
                    string names = "";
                    for (int i = 0; i < cnt && i < 12; i++)
                        names += tr.GetChild(i).name + ",";
                    var comps = go.GetComponents<UnityEngine.Component>();
                    string cts = "";
                    for (int i = 0; i < comps.Length && i < 8; i++)
                        cts += comps[i].GetType().Name + ",";
                    Log("fixfont root=" + go.name + " children=" + cnt + " [" + names + "] comps=[" + cts + "]");
                }
                catch (Exception) { }
                if (osFont == null)
                    osFont = UnityEngine.Font.CreateDynamicFontFromOSFont(new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun", "Arial" }, 22);
                foreach (var t in go.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                {
                    try { t.font = osFont; t.fontSize = Math.Max(t.fontSize, 16); n++; }
                    catch (Exception) { }
                }
                int tmp = 0;
                foreach (var c in go.GetComponentsInChildren<UnityEngine.Component>(true))
                {
                    if (c == null) continue;
                    if (c.GetType().Name.IndexOf("TMP", StringComparison.Ordinal) >= 0) tmp++;
                }
                Log("fixfont texts=" + n + " tmpComps=" + tmp);
            }
            catch (Exception e) { Log("fixfont ERR " + e.GetType().Name); }
            try
            {
                UnityEngine.GameObject go2 = panel as UnityEngine.GameObject;
                if (go2 != null)
                {
                    foreach (string nm in new string[] { "title", "subtitle", "body", "signature", "page" })
                    {
                        var ch = go2.transform.Find(nm);
                        if (ch == null) { Log("node " + nm + " missing"); continue; }
                        var cs = ch.GetComponents<UnityEngine.Component>();
                        string ty = "";
                        for (int i = 0; i < cs.Length && i < 6; i++) ty += cs[i].GetType().FullName + ",";
                        var cs2 = ch.GetComponentsInChildren<UnityEngine.Component>(true);
                        string ty2 = "";
                        for (int i = 0; i < cs2.Length && i < 8; i++) ty2 += cs2[i].GetType().Name + ",";
                        Log("node " + nm + " self=[" + ty + "] kids=[" + ty2 + "]");
                    }
                }
            }
            catch (Exception e) { Log("node ERR " + e.GetType().Name); }

            try
            {
                lastFontName = ApplyCjkFont(panel);
                Log("cjkfont = " + (lastFontName == null ? "none" : lastFontName));
            }
            catch (Exception e) { Log("cjkfont ERR " + e.GetType().Name); }
            return n;
        }

        /// <summary>把书页 5 个 TMP 文本换成含中文字形的字体资源，返回字体名。</summary>
        private static string ApplyCjkFont(object panel)
        {
            UnityEngine.GameObject go = panel as UnityEngine.GameObject;
            if (go == null && panel is UnityEngine.Component) go = ((UnityEngine.Component)panel).gameObject;
            if (go == null) return null;

            var all = UnityEngine.Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>();
            if (all == null || all.Length == 0) return null;
            TMPro.TMP_FontAsset best = null;
            int bestScore = -1;
            foreach (var fa in all)
            {
                if (fa == null) continue;
                string nm = fa.name == null ? "" : fa.name;
                int score = 0;
                string low = nm.ToLower();
                if (low.Contains("chinese") || low.Contains("cjk") || low.Contains("sc") || low.Contains("hans") || low.Contains("zh")) score += 100000;
                try { if (fa.characterTable != null) score += fa.characterTable.Count; } catch (Exception) { }
                if (score > bestScore) { bestScore = score; best = fa; }
            }
            if (best == null) return null;

            int set = 0;
            foreach (string nm in new string[] { "title", "subtitle", "body", "signature", "page" })
            {
                var ch = go.transform.Find(nm);
                if (ch == null) continue;
                var tmp = ch.GetComponent<TMPro.TextMeshProUGUI>();
                if (tmp == null) continue;
                try
                {
                    if (tmp.font != best)
                    {
                        if (tmp.font != null && tmp.font.fallbackFontAssetTable != null)
                        {
                            if (!tmp.font.fallbackFontAssetTable.Contains(best))
                                tmp.font.fallbackFontAssetTable.Add(best);
                        }
                        tmp.font = best;
                        tmp.SetAllDirty();
                    }
                    set++;
                }
                catch (Exception) { }
            }
            Log("cjkfont set=" + set + " asset=" + best.name);
            return best.name;
        }
        public static string Close()
        {
            try
            {
                object pum = Manager();
                if (pum != null) GameBridge.Call(pum, "CloseBook");
                last = "closed";
                return "book -> closed";
            }
            catch (Exception e) { return "book -> ERR " + e.GetType().Name; }
        }

        public static string Status()
        {
            object pum = Manager();
            object panel = pum == null ? null : GameBridge.Get(pum, "bookPanel");
            bool vis = false;
            try { if (panel is UnityEngine.GameObject) vis = ((UnityEngine.GameObject)panel).activeSelf; }
            catch (Exception) { }
            // 直连静态字段：bookDatas 是 public static Dictionary<string,StoreBookData>。
            var dict = StoreBookData.bookDatas;
            string diag = dict == null ? "null" : ("Dictionary=" + dict.Count);
            return "book -> last=" + last + " panel=" + (vis ? "visible" : "hidden") + " pum=" + (pum != null ? "yes" : "no") + " data:" + diag;
        }

        /// <summary>诊断：列出 DirectoryMaster / BookHelper 相关方法签名。</summary>
#if DEV
        public static string Methods()
        {
            var sb = new StringBuilder();
            Type dm = typeof(DirectoryMaster);
            sb.Append("DM=" + (dm != null ? "yes" : "no"));
            if (dm != null)
            {
                foreach (var m in dm.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "Create" && m.Name != "Item" && m.Name != "ItemReadOnly") continue;
                    var ps = m.GetParameters();
                    sb.Append(" | " + (m.IsPublic ? "+" : "-") + m.Name + "(");
                    for (int i = 0; i < ps.Length; i++) { if (i > 0) sb.Append(","); sb.Append(ps[i].ParameterType.Name); }
                    sb.Append(")");
                }
            }
            Type bh = typeof(BookHelper);
            sb.Append(" || BH=" + (bh != null ? "yes" : "no"));
            if (bh != null)
            {
                foreach (var m in bh.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "InitBookItem") continue;
                    var ps = m.GetParameters();
                    sb.Append(" | " + (m.IsPublic ? "+" : "-") + m.Name + "(");
                    for (int i = 0; i < ps.Length; i++) { if (i > 0) sb.Append(","); sb.Append(ps[i].ParameterType.Name); }
                    sb.Append(")");
                }
            }
            return "bookm -> " + sb.ToString();
        }
#endif

        /// <summary>后台命令：book / book=3 / book0。</summary>
        public static string Cmd(string line)
        {
#if DEV
            if (line == "bookm") return Methods();
#endif
            if (line == "book0") return Close();
            if (line == "book?") return Status();
            if (line == "book") return Open(1);
            int pg;
            if (line.StartsWith("book=") && int.TryParse(line.Substring(5), out pg)) return Open(pg);
            return "unknown";
        }
    }
}
