using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace WaterCalcMod
{
    /// <summary>
    /// 给「当前打开的每一台净水器」在店里那台机器自己的格子上画一圈描边。
    /// 早期是单例 static node/owner，同时开两台只有一台有框、切台还会残留；
    /// 现在按机器对象分表：每台机器一个独立节点，窗口关掉就销毁那一台的节点，
    /// 节点挂在机器物品自己的 RectTransform（店里的库存格）下面，所以框跟着机器位置走，
    /// 不是屏幕固定坐标。同一帧里可以先开 A 再开 B，两条框互不影响。
    /// </summary>
    public static class MarkerOutline
    {
        public const string NodeName = "WaterCalc_MarkerOutline";

        private const float EdgeThickness = 1.5f;   // 描边宽度（UI 单位；1080p 画布 scaleFactor=2，约 3 屏幕像素）

        private static ManualLogSource log;
        private static readonly List<Entry> entries = new List<Entry>();
        private static string lastErr = "";
        private static int lastVisCount = -1;
        private static bool debugFill;      // 调试：描边换成品红，确认节点到底渲染不渲染
        private static int anchorMode = 1;  // 兼容旧 ctl：mark=item/tint/auto；当前一律锚到机器格

        /// <summary>一台机器对应一条描边。</summary>
        private sealed class Entry
        {
            public object Machine;
            public GameObject Node;
            public Image Image;
            public string HostKind = "";
        }

        public static bool HasAny { get { return entries.Count > 0; } }

        /// <summary>诊断用：当前有几条描边、最近一次失败原因（DEV 日志会打这两个）。</summary>
        public static int EntryCount { get { return entries.Count; } }
        public static string LastErr { get { return lastErr; } }

        /// <summary>ctl 用：切锚点策略，返回当前模式。</summary>
        public static string Anchor(int mode) { anchorMode = mode; Hide(); return "anchor=" + anchorMode; }

        public static void Install(ManualLogSource l) { log = l; }

        private static void L(string s)
        {
            try { if (log != null) log.LogInfo("[mark] " + s); } catch (Exception) { }
        }

        /// <summary>只维护一台（单台调试/兼容旧调用）。多台请走 SyncAll。</summary>
        public static void Show(object machineItem)
        {
            try
            {
                if (machineItem == null) { Hide(); return; }
                Ensure(machineItem);
            }
            catch (Exception e) { lastErr = e.GetType().Name + ":" + e.Message; L("ERR " + lastErr); }
        }

        /// <summary>
        /// 每帧（实际每 0.4s）调用：以「窗口是否可见」为准，逐台维护描边。
        /// 新开的窗口补一条，关掉的窗口销毁那一条，切台不会把别人的框带走。
        /// </summary>
        public static void SyncAll()
        {
            try
            {
                List<object> keep = new List<object>();
                var pairs = PurifierHook.Pairs;
                for (int i = 0; i < pairs.Count; i++)
                {
                    var p = pairs[i];
                    if (p == null || p.Machine == null || p.Window == null) continue;
                    bool vis = false;
                    try { vis = GameBridge.WinVisible(p.Window); }
                    catch (Exception) { }
                    if (vis) keep.Add(p.Machine);
                }

                // 先收掉已经不可见/已销毁的那几台（关窗、切台、机器被搬走都在这里清掉）
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (IndexOfRef(keep, entries[i].Machine) < 0) DestroyEntry(entries[i]);
                }
                // 再给每一台可见的补/更新一条
                for (int i = 0; i < keep.Count; i++) Ensure(keep[i]);

#if DEV
                if (keep.Count != lastVisCount)
                {
                    lastVisCount = keep.Count;
                    PurifierUi.Log("[marker] pairs=" + pairs.Count + " visible=" + keep.Count);
                }
#endif

                if (keep.Count == 0) lastErr = "";
            }
            catch (Exception e) { lastErr = e.GetType().Name + ":" + e.Message; L("ERR " + lastErr); }
        }

        /// <summary>确保这台机器有一条贴在它自己格子上的描边；锚点变了就原地重建。</summary>
        private static void Ensure(object machine)
        {
            string kind;
            // 主锚点：机器物品自己的 RectTransform（店里的库存格，48×64 那一层）。
            // SlotMarker 是纯数据对象没有 transform，GetItemInventorySlot→index 在渲染树里对不上，
            // 所以这里直接从机器物品拿 UI 节点，拿到的一定是这台机器当前占的那个格子。
            RectTransform host = RectOf(machine, out kind);
            if (host == null)
            {
                string k2;
                host = SlotRect(machine, out k2);
                if (host != null) kind = k2;
                else if (kind.Length == 0) kind = k2;
            }
            if (host == null || !host.gameObject.activeInHierarchy)
            {
                // 格子不在屏幕上（库存界面收起等）：把这台的旧框收掉，绝不留残影
                Entry stale = Find(machine);
                if (stale != null) DestroyEntry(stale);
                lastErr = kind.Length > 0 ? kind : "no-rect";
                return;
            }

            Entry e = Find(machine);
            // 同一台、锚点没变：框是格子的子节点，位置自动跟着走，不用重建
            if (e != null && e.Node != null && e.Node.transform.parent == host) return;
            if (e != null) DestroyEntry(e);
            Attach(machine, host, kind);
        }

        /// <summary>在锚点节点上铺一张铺满的 UI 图，用四条实心边描一个空心框。</summary>
        private static void Attach(object machine, RectTransform host, string kind)
        {
            try
            {
                GameObject go = new GameObject(NodeName);
                go.layer = host.gameObject.layer;
                RectTransform rt = go.AddComponent<RectTransform>();
                rt.SetParent(host, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.localRotation = Quaternion.identity;
                rt.localScale = Vector3.one;

                // 原生 border_red 是 16×16 / border=1 的九宫格，铺到 48×64 上会被拉变形看不见，
                // 所以不依赖图集，只用游戏调色板里的红色 + 4 条 1.5 单位的实心条。
                Image img = MakeEdge(rt, "top", 0, EdgeThickness);
                MakeEdge(rt, "bottom", 1, EdgeThickness);
                MakeEdge(rt, "left", 2, EdgeThickness);
                MakeEdge(rt, "right", 3, EdgeThickness);

                Entry e = new Entry { Machine = machine, Node = go, Image = img, HostKind = kind };
                entries.Add(e);
                ApplyDebug(e);
                go.transform.SetAsLastSibling();   // 压在物品图标之上，边框才看得见
                lastErr = "";
            }
            catch (Exception e) { lastErr = e.GetType().Name + ":" + e.Message; L("ERR " + lastErr); }
        }

        /// <summary>在描边容器里放一条实心边：0=上 1=下 2=左 3=右，四条拼成一个空心框。</summary>
        private static Image MakeEdge(RectTransform host, string name, int side, float t)
        {
            GameObject go = new GameObject(name);
            go.layer = host.gameObject.layer;
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.SetParent(host, false);
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one;
            switch (side)
            {
                case 0:   // 上边：贴住顶边，向下厚 t
                    rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.offsetMin = new Vector2(0f, -t); rt.offsetMax = Vector2.zero;
                    break;
                case 1:   // 下边
                    rt.anchorMin = Vector2.zero; rt.anchorMax = new Vector2(1f, 0f);
                    rt.pivot = new Vector2(0.5f, 0f);
                    rt.offsetMin = Vector2.zero; rt.offsetMax = new Vector2(0f, t);
                    break;
                case 2:   // 左边
                    rt.anchorMin = Vector2.zero; rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0f, 0.5f);
                    rt.offsetMin = Vector2.zero; rt.offsetMax = new Vector2(t, 0f);
                    break;
                default:  // 右边
                    rt.anchorMin = new Vector2(1f, 0f); rt.anchorMax = Vector2.one;
                    rt.pivot = new Vector2(1f, 0.5f);
                    rt.offsetMin = new Vector2(-t, 0f); rt.offsetMax = Vector2.zero;
                    break;
            }
            Image img = go.AddComponent<Image>();
            img.raycastTarget = false;
            img.color = MarkerRed();
            return img;
        }

        /// <summary>销毁全部描边（关开关、切换锚点模式时用）。</summary>
        public static void Hide()
        {
            for (int i = entries.Count - 1; i >= 0; i--) DestroyEntry(entries[i]);
            lastErr = "";
        }

        private static void DestroyEntry(Entry e)
        {
            if (e == null) return;
            try
            {
                entries.Remove(e);
                if (e.Node != null)
                {
                    e.Node.SetActive(false);   // 立刻不可见，Destroy 是帧末才真正回收
                    UnityEngine.Object.Destroy(e.Node);
                }
            }
            catch (Exception) { }
        }

        private static Entry Find(object machine)
        {
            for (int i = 0; i < entries.Count; i++)
                if (ReferenceEquals(entries[i].Machine, machine)) return entries[i];
            return null;
        }

        private static int IndexOfRef(List<object> list, object item)
        {
            for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], item)) return i;
            return -1;
        }

        /// <summary>高亮框的红色：优先取游戏调色板里的 Red（GetColorFromPalette），取不到再退回同色系常量。</summary>
        private static Color MarkerRed()
        {
            try
            {
                Color c = RenderHandler.GetColorFromPalette(RenderHandler.ColorPalette.Red);
                if (c.a < 0.01f) c.a = 1f;      // 调色板里偶有 alpha=0，铺上去等于全透明（玩家报「框有了但没红色」）
                return c;
            }
            catch (Exception) { }
            try
            {
                RenderHandler cur = RenderHandler.current;
                if (cur != null)
                {
                    Color c = cur.textColorRed;
                    if (c.a < 0.01f) c.a = 1f;
                    return c;
                }
            }
            catch (Exception) { }
            return new Color(0.85f, 0.13f, 0.13f, 1f);
        }

        /// <summary>
        /// ctl 调试：把描边换成品红，用来判断「节点到底渲染不渲染」。
        /// 正常高亮不用它，只是为了在看不见时区分「没画出来」与「画在别处」。
        /// </summary>
        public static string Fill(bool on)
        {
            debugFill = on;
            for (int i = 0; i < entries.Count; i++) ApplyDebug(entries[i]);
            return "mark -> fill=" + on;
        }

        private static void ApplyDebug(Entry e)
        {
            try
            {
                if (e == null || e.Node == null) return;
                for (int i = 0; i < e.Node.transform.childCount; i++)
                {
                    Image im = e.Node.transform.GetChild(i).GetComponent<Image>();
                    if (im == null) continue;
                    im.color = debugFill ? new Color(1f, 0f, 1f, 1f) : MarkerRed();
                }
            }
            catch (Exception) { }
        }

        /// <summary>ctl 诊断：有几条描边、各自锚在哪台机器的哪个节点、屏幕位置与是否可见。</summary>
        public static string Describe()
        {
            var sb = new System.Text.StringBuilder("outline count=").Append(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                sb.Append(" [#").Append(i).Append(']');
                try
                {
                    if (e.Machine == null) { sb.Append(" machine=null"); continue; }
                    sb.Append(" pair=").Append(PairIndex(e.Machine));
                    sb.Append(" slot=").Append(SlotIndexOf(e.Machine));
                    sb.Append(" host=").Append(e.HostKind);
                    RectTransform prt = e.Node == null ? null : e.Node.transform.parent as RectTransform;
                    if (prt != null)
                    {
                        Vector2 sp = RectTransformUtility.WorldToScreenPoint(null, prt.position);
                        sb.Append(" node=").Append(prt.name)
                          .Append(':').Append(Mathf.RoundToInt(prt.rect.width)).Append('x').Append(Mathf.RoundToInt(prt.rect.height))
                          .Append('@').Append(Mathf.RoundToInt(sp.x)).Append(',').Append(Mathf.RoundToInt(sp.y));
                    }
                    sb.Append(" active=").Append(e.Node != null && e.Node.activeInHierarchy);
                    sb.Append(" chain=").Append(Chain(e.Node == null ? null : e.Node.transform, 3));
                }
                catch (Exception) { sb.Append(" ERR"); }
            }
            if (lastErr.Length > 0) sb.Append(" err=").Append(lastErr);
            sb.Append(" sceneNodes=").Append(CountNodes());
            return sb.ToString();
        }

        private static string Chain(Transform t, int n)
        {
            var sb = new System.Text.StringBuilder();
            if (t == null) return "";
            for (int i = 0; i < n && t != null; i++)
            {
                if (i > 0) sb.Append('<');
                sb.Append(t.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        private static string PairIndex(object machine)
        {
            var pairs = PurifierHook.Pairs;
            for (int i = 0; i < pairs.Count; i++)
                if (ReferenceEquals(pairs[i].Machine, machine)) return i.ToString();
            return "?";
        }

        private static string SlotIndexOf(object machine)
        {
            try
            {
                object inv = GameBridge.Get(machine, "parentInventory");
                if (inv == null) return "?";
                object slot = GameBridge.Call(inv, "GetItemInventorySlot", machine);
                if (slot == null) return "?";
                object idx = GameBridge.Get(slot, "index");
                return idx == null ? "?" : idx.ToString();
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>场景里（含未激活）名叫 WaterCalc_MarkerOutline 的对象数，用来查泄漏。</summary>
        private static int CountNodes()
        {
            int n = 0;
            try
            {
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(GameObject));
                for (int i = 0; i < all.Length; i++)
                {
                    GameObject g = all[i] as GameObject;
                    if (g != null && g.name == NodeName && g.activeInHierarchy) n++;
                }
            }
            catch (Exception) { }
            return n;
        }

        /// <summary>物品在 UI 上的矩形：GameItemElement.rectTransform 取的是物品自己占的格子。</summary>
        private static RectTransform RectOf(object item, out string kind)
        {
            kind = "";
            try
            {
                RectTransform rt = GameBridge.Get(item, "rectTransform") as RectTransform;
                if (rt != null) kind = "item:" + rt.name;
                else kind = "rect-null";
                return rt;
            }
            catch (Exception e) { kind = "ERRRECT:" + e.GetType().Name; return null; }
        }

        /// <summary>备选锚点：按 SlotMarker.index 到库存渲染树里反查格子节点（本作取不到时会返回 null）。</summary>
        private static RectTransform SlotRect(object item, out string kind)
        {
            kind = "";
            try
            {
                object inv = GameBridge.Get(item, "parentInventory");
                if (inv == null) { kind = "no-inv"; return null; }
                object slot = GameBridge.Call(inv, "GetItemInventorySlot", item);
                if (slot == null) { kind = "no-slot"; return null; }
                object idxObj = GameBridge.Get(slot, "index");
                if (!(idxObj is int)) { kind = "no-index"; return null; }
                int idx = (int)idxObj;
                var inner = GameBridge.Get(inv, "inner") as Component;
                if (inner == null) { kind = "no-inner"; return null; }
                RectTransform found = FindSlotNode(inner.transform, idx, 0);
                if (found == null) { kind = "slot-node-missing:" + idx; return null; }
                kind = "slot:" + idx;
                return found;
            }
            catch (Exception e) { kind = "ERRSLOT:" + e.GetType().Name; return null; }
        }

        // 在库存渲染树里找「index == 槽位号」的格子节点（属性或字段都认）。
        private static RectTransform FindSlotNode(Transform root, int idx, int depth)
        {
            if (root == null || depth > 6) return null;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);
                if (c == null) continue;
                Component[] comps = c.GetComponents<Component>();
                for (int k = 0; k < comps.Length; k++)
                {
                    Component comp = comps[k];
                    if (comp == null) continue;
                    try
                    {
                        int v = -999;
                        object iv = GameBridge.Get(comp, "index");
                        if (iv is int) v = (int)iv;
                        if (v == idx)
                        {
                            RectTransform rt = c as RectTransform;
                            if (rt != null && rt.rect.width > 20f) return rt;
                        }
                    }
                    catch (Exception) { }
                }
                RectTransform deep = FindSlotNode(c, idx, depth + 1);
                if (deep != null) return deep;
            }
            return null;
        }

    }
}
