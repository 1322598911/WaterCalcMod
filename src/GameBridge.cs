using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace WaterCalcMod
{
    /// <summary>
    /// 游戏侧桥接。2026-12-10 起游戏拒绝 reflection-external 能力，
    /// 所以所有「运行时才知道目标」的读写一律走官方 ModReflect（免费、由游戏做目标校验）；
    /// 旧的原生反射路径只保留在 DEV 构建里作兜底，Release 构建里根本不存在。
    /// </summary>
    public static class GameBridge
    {
        private static float _lastWarn = -999f;
        private static ManualLogSource log;
        private static readonly Dictionary<string, Type> cache = new Dictionary<string, Type>();
        private static Assembly[] asms;
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Init(ManualLogSource l)
        {
            log = l;
#if DEV
            asms = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "Assembly-CSharp" || a.GetName().Name == "Assembly-CSharp-firstpass")
                .ToArray();
            log.LogInfo("[bridge] assemblies=" + asms.Length);
#else
            log.LogInfo("[bridge] ModReflect-only（无反射兜底）");
#endif
        }

        /// <summary>类型查询：走官方 ModReflect（不需要 reflection-external 能力）；DEV 版保留旧路径兜底。</summary>
        public static Type T(string name)
        {
            Type t;
            if (cache.TryGetValue(name, out t)) return t;
            t = ReflectType(name);
            cache[name] = t;
            return t;
        }

        private static Type ReflectType(string name)
        {
            try
            {
                var t = ModReflect.FindType(name);
                if (t != null) return t;
            }
            catch (Exception) { }
#if DEV
            foreach (var a in asms)
            {
                var t2 = a.GetType(name, false);
                if (t2 != null) return t2;
            }
#endif
            return null;
        }

        /// <summary>找场景里第一个该类型的对象（含未激活）。</summary>
        public static UnityEngine.Object Find(string typeName)
        {
            var t = T(typeName);
            if (t == null) return null;
            var all = Resources.FindObjectsOfTypeAll(t);
            foreach (var o in all)
            {
                var c = o as Component;
                if (c != null && c.gameObject != null && c.gameObject.scene.IsValid()) return c;
            }
            return all.Length > 0 ? all[0] : null;
        }

        /// <summary>
        /// 读成员（字段或属性）。ModReflect 内部做了访问校验。
        /// 注意：ModReflect 的引擎白名单只有 UnityEngine.UI / TMPro / UnityEngine.EventSystems 三个前缀，
        /// UnityEngine 自己声明的成员（rectTransform、gameObject…）会被它拒绝并抛异常，
        /// 所以这些成员必须先走强类型直取（TryUnity），否则每帧抛异常＝卡顿、拿不到值＝框画不出来。
        /// </summary>
        public static object Get(object o, string member)
        {
#if DEV
            Count("Get:" + member);
#endif
            if (o == null) return null;
            object fast;
            if (TryUnity(o, member, out fast)) return fast;
            string k = Key(o, member);
            if (memoOn)
            {
                object mv;
                if (memo.TryGetValue(k, out mv)) return mv;
            }
            if (deny.ContainsKey(k)) return null;
            try
            {
                object v = ModReflect.Get(o, member);
                if (memoOn) memo[k] = v;
                return v;
            }
            catch (Exception) { deny[k] = true; }
#if DEV
            var t = o.GetType();
            var f = t.GetField(member, F);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(member, F);
            if (p != null) return p.GetValue(o, null);
#endif
            return null;
        }

        /// <summary>读取失败的组合记下来，之后直接返回 null（不重复抛异常，异常在 Mono 上很贵）。</summary>
        private static readonly Dictionary<string, bool> deny = new Dictionary<string, bool>();

        // ★性能：一次整包重算内的读取缓存。ModReflect 每次 Get 都要取调用方 mod、按名字找成员、
        // 再走权限校验（没有内部缓存），同一对象同一成员在本轮里被反复读几十次时开销线性叠加。
        // 只在 BeginMemo/EndMemo 之间生效（同一段同步代码内，不会有别人改数据），
        // 任何写操作都清空它，避免把写后的值读成写前的。
        private static bool memoOn;
        private static readonly Dictionary<string, object> memo = new Dictionary<string, object>();

        public static void BeginMemo() { memoOn = true; memo.Clear(); }
        public static void EndMemo() { memoOn = false; memo.Clear(); }

        // ★性能：深扫（AllItemsDeep）结果缓存。一次深扫要走上百个节点 ×(children/childElement/
        // childElements/contentWindow)，每个成员读都过 ModReflect 权限校验，单帧实测 100~460 ms。
        // 背包 0.6 s 内不会变，主面板每秒重建时直接复用上一次的结果。
        private static List<GameItem> allDeepCache;
        private static float allDeepAt = -999f;
        /// <summary>让下一次 AllItemsDeep 重新扫（物品动过之后可以主动调，非必须）。</summary>
        public static void DropItemCache() { allDeepCache = null; allDeepAt = -999f; }

        /// <summary>
        /// 窗口是否正在显示。`PixelWindow.IsVisible()` 是游戏类型上的公开方法，直接强类型调用即可：
        /// 以前走 GameBridge.Call(win,"IsVisible")，迁到 ModReflect 后这条调用失败，
        /// 所有窗口都被判成「不可见」→ 红框不画、点选被抢回「最后配对」、看起来就是「选中无效」。
        /// </summary>
        public static bool WinVisible(object win)
        {
            PixelWindow pw = win as PixelWindow;
            if (pw == null) return false;
            try { return pw.IsVisible(); } catch (Exception) { return false; }
        }

#if DEV
        /// <summary>DEV 性能探针：统计每一类 GameBridge 调用次数，定位谁在拖慢帧（ModReflect 每次调用都要取调用方 mod + 找成员 + 权限校验，没有缓存）。</summary>
        public static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
        public static void Count(string k)
        {
            int n; Counts.TryGetValue(k, out n); Counts[k] = n + 1;
        }
        public static string CountDump(int top)
        {
            var list = new List<KeyValuePair<string, int>>(Counts);
            list.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
            int total = 0;
            for (int i = 0; i < list.Count; i++) total += list[i].Value;
            var sb = new System.Text.StringBuilder("total=" + total + " kinds=" + list.Count);
            for (int i = 0; i < list.Count && i < top; i++) sb.Append(" | ").Append(list[i].Key).Append("*").Append(list[i].Value);
            Counts.Clear();
            return sb.ToString();
        }
#endif

        private static string Key(object o, string member)
        {
            try { return o.GetType().FullName + "|" + member; }
            catch (Exception) { return "?|" + member; }
        }

        /// <summary>UnityEngine 命名空间下的常用成员：直接按强类型取，不走 ModReflect（它不允许这个命名空间）。</summary>
        private static bool TryUnity(object o, string member, out object v)
        {
            v = null;
            if (o == null) return false;
            if (member == "rectTransform")
            {
                var g = o as UnityEngine.UI.Graphic;
                if (g != null) { v = g.rectTransform; return true; }
                var c = o as Component;
                if (c != null) { v = c.transform as RectTransform; return true; }
                var go = o as GameObject;
                if (go != null) { v = go.transform as RectTransform; return true; }
                return false;
            }
            if (member == "transform")
            {
                var c = o as Component;
                if (c != null) { v = c.transform; return true; }
                return false;
            }
            if (member == "gameObject")
            {
                var c = o as Component;
                if (c != null) { v = c.gameObject; return true; }
                return false;
            }
            if (member == "name")
            {
                var uo = o as UnityEngine.Object;
                if (uo != null) { v = uo.name; return true; }
                return false;
            }
            if (member == "activeSelf")
            {
                var g = o as GameObject;
                if (g != null) { v = g.activeSelf; return true; }
                var c = o as Component;
                if (c != null) { v = c.gameObject.activeSelf; return true; }
                return false;
            }
            if (member == "enabled")
            {
                var b = o as Behaviour;
                if (b != null) { v = b.enabled; return true; }
                return false;
            }
            return false;
        }

        /// <summary>读静态成员（例如 PlayerStore.Instance）。</summary>
        public static object GetStatic(string typeName, string member)
        {
#if DEV
            Count("GetStatic:" + typeName + "." + member);
#endif
            try { return ModReflect.GetStatic<object>(typeName, member); }
            catch (Exception) { }
#if DEV
            // 注意：getter 本身也可能抛（例如存档还没载入时 PlayerStore.Instance 内部的 NRE）。
            // 旧版这里没包 try/catch，TargetInvocationException 一路逃到 Plugin.Awake，
            // 结果 Unity 判 Awake 失败、再也不调用 Update —— 红框/F7/模式按钮/自动刷新全部停摆。
            try
            {
                var t = T(typeName); if (t == null) return null;
                var f = t.GetField(member, F | BindingFlags.Static);
                if (f != null) return f.GetValue(null);
                var p = t.GetProperty(member, F | BindingFlags.Static);
                if (p != null) return p.GetValue(null, null);
            }
            catch (Exception) { }
#endif
            return null;
        }

        /// <summary>取 MethodBase（Harmony 补丁目标用）。签名给 Type[]，找不到回落按名字取第一个。</summary>
        public static System.Reflection.MethodBase Target(string typeName, string member, Type[] sig)
        {
            try
            {
                var mb = ModReflect.Target(typeName, member, sig);
                if (mb != null) return mb;
            }
            catch (Exception) { }
#if DEV
            var t = T(typeName); if (t == null) return null;
            if (sig == null) sig = Type.EmptyTypes;
            var exact = t.GetMethod(member, F, null, sig, null);
            if (exact != null) return exact;
            foreach (var mm in t.GetMethods(F)) if (mm.Name == member) return mm;
#endif
            return null;
        }

        /// <summary>写成员（字段或属性）。成功返回 true。</summary>
        public static bool Set(object o, string member, object v)
        {
#if DEV
            Count("Set:" + member);
#endif
            if (o == null) return false;
            if (memoOn) memo.Clear();          // 写过之后本轮缓存作废：读到写前的值会算错
            string k = "set|" + Key(o, member);
            if (deny.ContainsKey(k)) return false;
            try { ModReflect.Set(o, member, v); return true; }
            catch (Exception) { deny[k] = true; }
#if DEV
            try
            {
                var t = o.GetType();
                var f = t.GetField(member, F);
                if (f != null) { f.SetValue(o, v); return true; }
                var p = t.GetProperty(member, F);
                if (p != null && p.CanWrite) { p.SetValue(o, v, null); return true; }
            }
            catch (Exception) { }
#endif
            return false;
        }

        /// <summary>写静态成员。成功返回 true。</summary>
        public static bool SetStatic(string typeName, string member, object v)
        {
            if (memoOn) memo.Clear();
            try { ModReflect.SetStatic(typeName, member, v); return true; }
            catch (Exception) { }
#if DEV
            try
            {
                var t = T(typeName); if (t == null) return false;
                var f = t.GetField(member, F | BindingFlags.Static);
                if (f != null) { f.SetValue(null, v); return true; }
                var p = t.GetProperty(member, F | BindingFlags.Static);
                if (p != null && p.CanWrite) { p.SetValue(null, v, null); return true; }
            }
            catch (Exception) { }
#endif
            return false;
        }

        /// <summary>构造（无参或按参数匹配的重载）。</summary>
        public static object New(string typeName, params object[] args)
        {
            try { return ModReflect.New(typeName, args); }
            catch (Exception) { }
#if DEV
            var t = T(typeName); if (t == null) return null;
            try { return Activator.CreateInstance(t, args); } catch (Exception) { }
#endif
            return null;
        }

        /// <summary>调实例方法（ModReflect 自己做重载匹配）。</summary>
        public static object Call(object o, string method, params object[] args)
        {
#if DEV
            Count("Call:" + method);
#endif
            if (o == null) return null;
            try { return ModReflect.Call(o, method, args); }
            catch (Exception) { }
#if DEV
            var t = o.GetType();
            var ms = t.GetMethods(F).Where(m => m.Name == method && m.GetParameters().Length == args.Length);
            foreach (var m in ms)
            {
                try { return m.Invoke(o, args); } catch (Exception) { }
            }
#endif
            return null;
        }

        /// <summary>静态方法调用（例如 NetworkUpgrade.GetUpgradeById("RUSTWATER_TAP")）。</summary>
        public static object CallStatic(string typeName, string method, params object[] args)
        {
#if DEV
            Count("CallStatic:" + typeName + "." + method);
#endif
            try { return ModReflect.CallStatic(typeName, method, args); }
            catch (Exception) { }
#if DEV
            try
            {
                var t = T(typeName);
                if (t == null) return null;
                var ms = t.GetMethods(F | System.Reflection.BindingFlags.Static)
                          .Where(m => m.Name == method && m.GetParameters().Length == args.Length);
                foreach (var m in ms)
                {
                    try { return m.Invoke(null, args); } catch (Exception) { }
                }
            }
            catch (Exception) { }
#endif
            return null;
        }

        /// <summary>出货口（前柜台）的格子；MoveItemToFrontCounter 就是往这里放。</summary>
        public static object CounterGrid()
        {
            var t = T("EmporiumEntry");
            if (t == null) return null;
            var inst = GetStatic("EmporiumEntry", "Instance");
            return inst == null ? null : Get(inst, "frontInvinvElement");
        }

        /// <summary>任意 GameInventory / GameGridInventory 里的物品。</summary>
        public static List<GameItem> ItemsOf(object inv)
        {
            var res = new List<GameItem>();
            if (inv == null) return res;
            var e = Get(inv, "childItems") as System.Collections.IEnumerable;
            if (e == null) return res;
            foreach (var o in e) { var gi = o as GameItem; if (gi != null) res.Add(gi); }
            return res;
        }

        public static List<GameItem> CounterItems() { return ItemsOf(CounterGrid()); }

        /// <summary>六项污染物配比（下标对应 Liquid.Liquids 顺序）。</summary>
        public static int[] LiquidParts(GameItem it)
        {
            try { return it == null ? null : WaterHelper.GetLiquidParts(it); }
            catch (Exception e) { log.LogWarning("[parts] " + e.GetType().Name); return null; }
        }

        public static string[] LiquidNames()
        {
            var res = new List<string>();
            try
            {
                for (int i = 0; i < Liquid.Liquids.Count; i++)
                {
                    var liq = Liquid.Liquids[i];
                    var id = Get(liq, "identifier") as string;
                    if (string.IsNullOrEmpty(id)) id = Get(liq, "name") as string;
                    if (string.IsNullOrEmpty(id)) id = Get(liq, "key") as string;
                    res.Add(id == null ? ("#" + i) : id);
                }
            }
            catch (Exception e) { log.LogWarning("[parts] names " + e.GetType().Name); }
            return res.ToArray();
        }

#if DEV
        /// <summary>把对象的所有字段写进日志，供离线分析结构（诊断专用，Release 不编入）。</summary>
        public static void Dump(object o, string tag, int depth = 0)
        {
            if (o == null) { log.LogInfo("[dump] " + tag + " = null"); return; }
            var t = o.GetType();
            log.LogInfo("[dump] " + tag + " :: " + t.FullName);
            foreach (var f in t.GetFields(F))
            {
                object v = null;
                try { v = f.GetValue(o); } catch (Exception) { }
                log.LogInfo("   ." + f.Name + " (" + f.FieldType.Name + ") = " + Str(v));
            }
            foreach (var p in t.GetProperties(F))
            {
                if (!p.CanRead) continue;
                object v = null;
                try { v = p.GetValue(o, null); } catch (Exception) { }
                log.LogInfo("   #" + p.Name + " (" + p.PropertyType.Name + ") = " + Str(v));
            }
        }

        private static string Str(object v)
        {
            if (v == null) return "null";
            var s = v.ToString();
            return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
        }
#endif

        // ---- 实时读背包（不再依赖存档） ----
        private static Type WH { get { return T("WaterHelper"); } }

        private static int WHInt(string method, object item)
        {
            if (item == null) return -1;
            object r = null;
            try { r = ModReflect.CallStatic("WaterHelper", method, new object[] { item }); }
            catch (Exception) { }
#if DEV
            if (r == null)
            {
                var t = WH;
                if (t != null)
                {
                    var m = t.GetMethod(method, new[] { item.GetType() }) ?? t.GetMethod(method);
                    if (m != null)
                    {
                        try { r = m.Invoke(null, new[] { item }); }
                        catch (Exception e) { log.LogWarning("[live] " + method + " " + e.Message); return -1; }
                    }
                }
            }
#endif
            return r is int ? (int)r : -1;
        }

        public static int IntOf(object item, string method) { return WHInt(method, item); }

        /// <summary>调静态方法（传入 1 个对象参数），无返回值；失败打日志。</summary>
        public static void StatVoid(string typeName, string method, object arg)
        {
            if (arg == null) return;
            try { ModReflect.CallStatic(typeName, method, new object[] { arg }); return; }
            catch (Exception) { }
#if DEV
            var t = T(typeName); if (t == null) return;
            MethodInfo m = null;
            foreach (var cand in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (cand.Name != method) continue;
                var ps = cand.GetParameters();
                if (ps.Length != 1) continue;
                if (!ps[0].ParameterType.IsInstanceOfType(arg)) continue;
                m = cand; break;
            }
            if (m == null) { log.LogWarning("[stat] 找不到 " + typeName + "." + method); return; }
            try { m.Invoke(null, new[] { arg }); }
            catch (Exception e) { log.LogWarning("[stat] " + method + " " + (e.InnerException != null ? e.InnerException.Message : e.Message)); }
#endif
        }

        /// <summary>调静态方法（传入 1 个对象参数），返回 int；失败 -1。</summary>
        public static int StatInt(string typeName, string method, object arg)
        {
            if (arg == null) return -1;
            object r = null;
            bool threw = false;
            try { r = ModReflect.CallStatic(typeName, method, new object[] { arg }); }
            catch (Exception) { threw = true; }
            if (r is int) return (int)r;
#if DEV
            if (!threw)
            {
                // 不按 arg.GetType() 精确匹配：item 的运行时类型常是 GameItem 的子类，
                // 精确签名会找不到方法（表现为加成一直读成 0）。
                var t = T(typeName); if (t != null)
                {
                    MethodInfo m = null;
                    try
                    {
                        foreach (var cand in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                        {
                            if (cand.Name != method) continue;
                            var ps = cand.GetParameters();
                            if (ps.Length != 1) continue;
                            if (!ps[0].ParameterType.IsInstanceOfType(arg)) continue;
                            if (cand.ReturnType != typeof(int)) continue;
                            m = cand; break;
                        }
                    }
                    catch (Exception) { }
                    if (m != null)
                    {
                        try { var rv = m.Invoke(null, new[] { arg }); if (rv is int) return (int)rv; }
                        catch (Exception e)
                        {
                            if (UnityEngine.Time.realtimeSinceStartup - _lastWarn > 60f)
                            {
                                _lastWarn = UnityEngine.Time.realtimeSinceStartup;
                                log.LogWarning("[stat] " + method + " " + (e.InnerException != null ? e.InnerException.Message : e.Message));
                            }
                            return -1;
                        }
                    }
                }
            }
#endif
            if (UnityEngine.Time.realtimeSinceStartup - _lastWarn > 60f && threw)
            {
                _lastWarn = UnityEngine.Time.realtimeSinceStartup;
                log.LogWarning("[stat] " + typeName + "." + method + " 调用失败");
            }
            return -1;
        }

        public static string NameOfItem(object item)
        {
            return NameOf(item, WHInt("GetTotalCapacity", item));
        }

        /// <summary>
        /// 列出机器/容器里的子件（模组、滤芯之类），用于诊断「装了模组但加成读成 0」。
        /// 只读：走 GameItem.FindAllChildItems()，不碰任何写接口。
        /// </summary>
        public static string DescribeModules(object item)
        {
            if (item == null) return "none";
            var sb = new System.Text.StringBuilder();
            System.Collections.IEnumerable list = null;
            try { list = ModReflect.Call(item, "FindAllChildItems", null) as System.Collections.IEnumerable; }
            catch (Exception) { }
#if DEV
            if (list == null)
            {
                try
                {
                    var mi = item.GetType().GetMethod("FindAllChildItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null) list = mi.Invoke(item, null) as System.Collections.IEnumerable;
                }
                catch (Exception) { }
            }
#endif
            if (list == null) return "no-api";
            foreach (var c in list)
            {
                try
                {
                    string id = Get(c, "identifier") as string;
                    if (id == null) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(id);
                }
                catch (Exception) { }
            }
            return sb.Length == 0 ? "empty" : sb.ToString();
        }

        public static System.Collections.Generic.List<SaveReader.Ctr> LiveBottles()
        {
            var res = new System.Collections.Generic.List<SaveReader.Ctr>();
            if (T("PlayerStore") == null) return res;
            object inst = GetStatic("PlayerStore", "Instance");
            if (inst == null) return res;

            System.Collections.IEnumerable list = null;
            try { list = ModReflect.Call(inst, "FindAllItem", new object[] { true }) as System.Collections.IEnumerable; }
            catch (Exception e) { log.LogWarning("[live] FindAllItem " + e.Message); }
            if (list == null) return res;

            foreach (var it in list)
            {
                if (it == null) continue;
                int cap = WHInt("GetTotalCapacity", it);
                if (cap <= 0) continue;
                int tot = WHInt("GetTotalVolume", it);
                int pur = WHInt("GetWaterPurity", it);
                if (tot <= 0) continue;
                res.Add(new SaveReader.Ctr { name = NameOf(it, cap), cap = cap, total = tot, water = (long)tot * pur / 10000 });
            }
            return res;
        }

        /// <summary>玩家身上所有有容量的容器（含空瓶），用于兜底选瓶。</summary>
        public static List<GameItem> LiveAllItems()
        {
            var res = new List<GameItem>();
            foreach (var gi in RawItems())
            {
                if (gi == null) continue;
                if (WaterHelper.GetTotalCapacity(gi) <= 0) continue;
                res.Add(gi);
            }
            return res;
        }

        /// <summary>所有含水容器的真实 GameItem 引用（供「按容器凑量」做子集和）。</summary>
        public static List<GameItem> LiveItems()
        {
            var res = new List<GameItem>();
            foreach (var gi in RawItems())
            {
                if (gi == null) continue;
                if (WaterHelper.GetTotalVolume(gi) <= 0) continue;
                res.Add(gi);
            }
            return res;
        }

        /// <summary>
        /// 把「格子里的东西」拆成真正的 GameItem：库存元素可能直接是 GameItem，
        /// 也可能是 GameItemElement / SlotMarker 这类包装对象（它们的 item / childItem 才是物品）。
        /// </summary>
        private static GameItem AsItem(object o)
        {
            var gi = o as GameItem;
            if (gi != null) return gi;
            if (o == null) return null;
            string[] names = { "item", "childItem", "Item", "ChildItem", "currentItem" };
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    if (!ModReflect.Has(o, names[i])) continue;
                    gi = ModReflect.Get(o, names[i]) as GameItem;
                    if (gi != null) return gi;
                }
                catch (Exception) { }
#if DEV
                try
                {
                    var t = o.GetType();
                    var p = t.GetProperty(names[i], F);
                    if (p != null && typeof(GameItem).IsAssignableFrom(p.PropertyType))
                    {
                        gi = p.GetValue(o, null) as GameItem;
                        if (gi != null) return gi;
                    }
                    var f = t.GetField(names[i], F);
                    if (f != null && typeof(GameItem).IsAssignableFrom(f.FieldType))
                    {
                        gi = f.GetValue(o) as GameItem;
                        if (gi != null) return gi;
                    }
                }
                catch (Exception) { }
#endif
            }
            return null;
        }

        /// <summary>按引用去重后追加（GameItem 不是 UnityEngine.Object，没有 GetInstanceID）。</summary>
        public static void AddUnique(List<GameItem> list, GameItem gi)
        {
            if (list == null || gi == null) return;
            for (int i = 0; i < list.Count; i++) if (object.ReferenceEquals(list[i], gi)) return;
            list.Add(gi);
        }

        /// <summary>
        /// 一个库存对象里的所有物品。`childItems` 是 GameInventory（含 GameGridInventory /
        /// GameSlotInventory）真正存放物品的地方，其余成员名是别的库存类型的习惯命名，全试一遍。
        /// 打印机输出格是 GameGridInventory(4×4)，东西只在 `childItems` / `items` 里，漏一个就看不见。
        /// </summary>
        public static List<GameItem> InventoryItems(object inv)
        {
            var res = new List<GameItem>();
            if (inv == null) return res;
            string[] members = { "childItems", "Items", "items", "itemList", "slots", "Slots" };
            for (int i = 0; i < members.Length; i++)
            {
                try
                {
                    if (!ModReflect.Has(inv, members[i])) continue;
                    var e = ModReflect.Get(inv, members[i]) as System.Collections.IEnumerable;
                    if (e == null) continue;
                    foreach (var o in e) AddUnique(res, AsItem(o));
                }
                catch (Exception) { }
            }
            string[] methods = { "GetItems", "GetAllItems", "GetAllItem", "GetAll", "FindAllItem", "ToArray" };
            for (int i = 0; i < methods.Length; i++)
            {
                try
                {
                    var e = ModReflect.Call(inv, methods[i], null) as System.Collections.IEnumerable;
                    if (e == null) continue;
                    foreach (var o in e) AddUnique(res, AsItem(o));
                }
                catch (Exception) { }
            }
            return res;
        }

        /// <summary>沿 `children` / `childElement` / `childElements` 把一棵界面元素树走一遍，收出里面的
        /// GameItem。机器面板结构随机型不同，`GetBodyStorage` 认不出来的（如打印机 4×4 输出格）靠这个兜底。
        /// 只读遍历，带重复检测与节点上限，不会因为环形引用死循环。</summary>
        public static List<GameItem> WalkGraphItems(object root)
        {
            var res = new List<GameItem>();
            if (root == null) return res;
            var seen = new List<object>();
            var queue = new Queue<object>();
            queue.Enqueue(root);
            int guard = 0;
            while (queue.Count > 0 && guard++ < 8000)
            {
                object node = queue.Dequeue();
                if (node == null) continue;
                bool dup = false;
                for (int i = 0; i < seen.Count; i++) if (object.ReferenceEquals(seen[i], node)) { dup = true; break; }
                if (dup) continue;
                seen.Add(node);
                AddUnique(res, AsItem(node));
                try
                {
                    var e = Get(node, "children") as System.Collections.IEnumerable;
                    if (e != null) foreach (var c in e) if (c != null) queue.Enqueue(c);
                }
                catch (Exception) { }
                try
                {
                    var ce = Get(node, "childElement");
                    if (ce != null) queue.Enqueue(ce);
                }
                catch (Exception) { }
                try
                {
                    var ces = Get(node, "childElements") as System.Collections.IEnumerable;
                    if (ces != null) foreach (var c in ces) if (c != null) queue.Enqueue(c);
                }
                catch (Exception) { }
            }
            return res;
        }

        /// <summary>机器仓：`MachineHelper.GetBodyStorage(machine, slot)` 认领的面板（电池槽 / 输出格 …）。</summary>
        private static object BodyStorage(object machine, int slot)
        {
            try { return ModReflect.CallStatic("MachineHelper", "GetBodyStorage", new object[] { machine, slot }); }
            catch (Exception) { }
#if DEV
            try
            {
                var mh = T("MachineHelper");
                var gbs = mh == null ? null : mh.GetMethod("GetBodyStorage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (gbs != null) return gbs.Invoke(null, new object[] { machine, slot });
            }
            catch (Exception) { }
#endif
            return null;
        }

        /// <summary>
        /// 玩家所有道具（深度）：顶层 FindAllItem + 子件 + 机器仓（打印机 / 净水器 / 冰箱 …）。
        /// 关键点：`GameItem.children` 只有 `child` 与 `examineWindow`，**不含 `contentWindow`**，
        /// 所以 `FindAllChildItems()` 永远走不进机器自己的窗口——塞在「水瓶打印机」里的瓶子
        /// 既不在 PlayerStore 顶层，也不在子件里，只能从机器窗口的储物件里捞。
        /// 用 BFS 定点迭代：新捞到的物品（含嵌套机器/箱子里的）继续展开，直到不再出现新引用。
        /// </summary>
        public static List<GameItem> AllItemsDeep()
        {
            // 玩家 2026-11-11「还是有点卡」：这个方法一次要走上百个节点、每个节点 3~4 次 ModReflect 调用
            // （children / childElement / childElements / contentWindow），单帧实测 100~460 ms。
            // 主面板每秒重建时会连着调好几次，背包在 0.6 s 内不可能变，直接复用上次结果。
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (allDeepCache != null && now >= allDeepAt && now - allDeepAt < 0.6f) return allDeepCache;
            var res = new List<GameItem>();
            var queue = new Queue<GameItem>();
            List<GameItem> roots;
            try { roots = RawItems(); } catch (Exception) { roots = new List<GameItem>(); }
            for (int i = 0; i < roots.Count; i++) if (roots[i] != null) queue.Enqueue(roots[i]);
            while (queue.Count > 0)
            {
                var gi = queue.Dequeue();
                if (gi == null) continue;
                // GameItem 不是 UnityEngine.Object，没有 GetInstanceID；用引用相等去重。
                bool dup = false;
                for (int i = 0; i < res.Count; i++) if (object.ReferenceEquals(res[i], gi)) { dup = true; break; }
                if (dup) continue;
                res.Add(gi);
                // ① 子件：容器里套的瓶子、机器上装的模组
                try
                {
                    var kids = ModReflect.Call(gi, "FindAllChildItems", null) as System.Collections.IEnumerable;
                    if (kids != null) foreach (var k in kids) if (k is GameItem) queue.Enqueue((GameItem)k);
                }
                catch (Exception) { }
                // 机器才有 contentWindow，没有就不必白跑查询。
                object win = null;
                try { win = Get(gi, "contentWindow"); } catch (Exception) { }
                if (win == null) continue;
                // ② 机器仓：`MachineHelper.GetBodyStorage(machine, slot)` 认领的面板（电池槽 / 输出格 …）。
                //    IL：contentWindow.childElement 是 MachineryPanelRow 时取 CollectStorage()[slot]，
                //    是 GridPixelElement 时取 GetElement(slot,1)；越界返回 null，不抛异常。
                for (int slot = 0; slot < 8; slot++)
                {
                    object inv = null;
                    try { inv = BodyStorage(gi, slot); }
                    catch (Exception) { break; }
                    if (inv == null) continue;
                    var inside = InventoryItems(inv);
                    for (int i = 0; i < inside.Count; i++) queue.Enqueue(inside[i]);
                }
                // ③ 兜底：把 contentWindow 的整棵元素树走一遍（打印机输出格在 GameGridInventory 里，
                //    个别机器的面板结构 GetBodyStorage 认不出来，这一层直接按图捞）。
                var inside2 = WalkGraphItems(win);
                for (int i = 0; i < inside2.Count; i++) queue.Enqueue(inside2[i]);
            }
            allDeepCache = res;
            allDeepAt = UnityEngine.Time.realtimeSinceStartup;
            return res;
        }

        /// <summary>诊断（ctl `items` 末尾那行）：每台机器仓里有东西就列 `机器=物品,物品`，
        /// 用来确认「打印机里的瓶子」确实被算进了 items。</summary>
        public static string MachineStorageSummary()
        {
            var sb = new System.Text.StringBuilder();
            List<GameItem> roots;
            try { roots = RawItems(); } catch (Exception) { return ""; }
            for (int i = 0; i < roots.Count; i++)
            {
                var gi = roots[i];
                if (gi == null) continue;
                object win = null;
                try { win = Get(gi, "contentWindow"); } catch (Exception) { }
                if (win == null) continue;
                var inside = new List<GameItem>();
                for (int slot = 0; slot < 8; slot++)
                {
                    object inv = null;
                    try { inv = BodyStorage(gi, slot); }
                    catch (Exception) { break; }
                    if (inv == null) continue;
                    var l = InventoryItems(inv);
                    for (int j = 0; j < l.Count; j++) AddUnique(inside, l[j]);
                }
                if (inside.Count == 0) continue;
                sb.Append(sb.Length == 0 ? "" : " ; ").Append(gi.identifier).Append("=");
                for (int j = 0; j < inside.Count; j++) sb.Append(j == 0 ? "" : ",").Append(inside[j].identifier);
            }
            return sb.ToString();
        }

        /// <summary>诊断（ctl `deep?`）：每台带窗口的机器的 identifier、各槽位库存类型与里面的物品，
        /// 以及从 contentWindow 整棵树里走到的物品，用来核对「到底是谁没被枚举到」。</summary>
        public static string DeepReport()
        {
            var sb = new System.Text.StringBuilder();
            List<GameItem> roots;
            try { roots = RawItems(); } catch (Exception e) { return "deep -> " + e.GetType().Name; }
            sb.Append("raw=").Append(roots.Count);
            for (int i = 0; i < roots.Count; i++)
            {
                var gi = roots[i];
                if (gi == null) continue;
                object win = null;
                try { win = Get(gi, "contentWindow"); } catch (Exception) { }
                if (win == null) continue;
                sb.Append(" || ").Append(gi.identifier);
                for (int slot = 0; slot < 8; slot++)
                {
                    object inv = null;
                    try { inv = BodyStorage(gi, slot); }
                    catch (Exception) { sb.Append(" s").Append(slot).Append(":err"); break; }
                    if (inv == null) continue;
                    var l = InventoryItems(inv);
                    sb.Append(" s").Append(slot).Append(":").Append(inv.GetType().Name).Append("=");
                    for (int j = 0; j < l.Count; j++) sb.Append(j == 0 ? "" : ",").Append(l[j].identifier);
                    if (l.Count == 0) sb.Append("-");
                }
                var walked = WalkGraphItems(win);
                sb.Append(" win=").Append(walked.Count);
                for (int j = 0; j < walked.Count; j++) sb.Append(j == 0 ? ":" : ",").Append(walked[j].identifier);
            }
            return "deep -> " + sb;
        }

        /// <summary>
        /// 深度枚举：背包/台面上的物品 + 它们内部的子件 + 每台机器的各个储物件。
        /// 玩家会把水瓶塞进「水瓶打印机」这类机器的仓里，只查 FindAllItem 是看不见的。
        /// </summary>
        public static List<GameItem> DeepItems()
        {
            var res = new List<GameItem>();
            var seen = new List<object>();
            Action<object> add = delegate(object o)
            {
                var gi = o as GameItem;
                if (gi == null) return;
                for (int i = 0; i < seen.Count; i++) if (object.ReferenceEquals(seen[i], gi)) return;
                seen.Add(gi);
                res.Add(gi);
            };
            var roots = RawItems();
            for (int i = 0; i < roots.Count; i++) add(roots[i]);
            // ① 子件：容器里套的瓶子、机器里的模组/滤芯
            for (int i = 0; i < roots.Count; i++)
            {
                try
                {
                    var kids = ModReflect.Call(roots[i], "FindAllChildItems", null) as System.Collections.IEnumerable;
                    if (kids == null) continue;
                    foreach (var k in kids) add(k);
                }
                catch (Exception) { }
            }
            // ② 机器自己的储物件：MachineHelper.GetBodyStorage(machine, idx)
            var roots2 = new List<GameItem>(res);
            for (int i = 0; i < roots2.Count; i++)
            {
                for (int slot = 0; slot < 5; slot++)
                {
                    try
                    {
                        var inv = BodyStorage(roots2[i], slot);
                        if (inv == null) continue;
                        var items = ModReflect.Get(inv, "Items") as System.Collections.IEnumerable;
                        if (items == null) items = ModReflect.Call(inv, "GetItems", null) as System.Collections.IEnumerable;
                        if (items == null) continue;
                        foreach (var o in items) add(o);
                    }
                    catch (Exception) { break; }
                }
            }
            return res;
        }

        public static List<GameItem> RawItems()
        {
            var res = new List<GameItem>();
            try
            {
                if (T("PlayerStore") == null) return res;
                object inst = GetStatic("PlayerStore", "Instance");
                if (inst == null) return res;
                var list = ModReflect.Call(inst, "FindAllItem", new object[] { true }) as System.Collections.IEnumerable;
                if (list == null) return res;
                foreach (var it in list)
                {
                    var gi = it as GameItem;
                    if (gi == null) continue;
                    res.Add(gi);
                }
            }
            catch (Exception e) { log.LogWarning("[live] 背包未就绪，稍后重试"); }
            return res;
        }

        private static string NameOf(object it, int cap)
        {
            string[] mems = { "name", "Name", "identifier" };
            for (int i = 0; i < mems.Length; i++)
            {
                try
                {
                    if (!ModReflect.Has(it, mems[i])) continue;
                    var v = ModReflect.Get(it, mems[i]) as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                catch (Exception) { }
            }
            return cap >= 1000000 ? (cap / 1000000.0).ToString("0.#") + "L 容器" : (cap / 1000.0).ToString("0") + "mL 容器";
        }

#if DEV
        /// <summary>扫描现场：净水器、容器窗口、玩家背包，全部 dump 到日志（诊断专用）。</summary>
        public static string Scan()
        {
            var sb = new System.Text.StringBuilder();
            string[] types =
            {
                "MachinePurifier", "WaterPurifier", "MachineBase", "Machine",
                "InventoryWindow", "ItemContainerWindow", "WaterContainerWindow",
                "PlayerStore", "PlayerInventory", "Inventory", "ItemContainer"
            };
            int found = 0;
            foreach (var n in types)
            {
                try
                {
                    var t = T(n);
                    if (t == null) { sb.Append(n + ":无 "); continue; }
                    var o = Find(n);
                    if (o == null) { sb.Append(n + ":空 "); continue; }
                    Dump(o, n);
                    found++;
                    sb.Append(n + ":有 ");
                }
                catch (Exception e) { sb.Append(n + ":错(" + e.GetType().Name + ") "); log.LogWarning("[scan] " + n + " -> " + e.Message); }
            }
            log.LogInfo("[scan] " + sb);
            return "扫到 " + found + " 个对象，细节见 Mods\\WaterCalcMod\\log.txt";
        }
#endif
    }
}
