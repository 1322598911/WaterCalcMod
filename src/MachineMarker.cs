using System;
using System.Text;
using UnityEngine;

namespace WaterCalcMod
{
    /// <summary>
    /// 给「当前打开的每一台净水器」做高亮标记。
    /// 早期实现是反射调 GameGridInventory.HighlightSlot(..., Red, true) 把格子底色染红，
    /// 既笨重（一整块红方块）又清不掉。后来改成在物品自己的 UI 节点上叠一圈细描边（见 MarkerOutline），
    /// 但那时只有单例 node/owner，同时开两台只描一台、切台还会把框带错。
    /// 现在按机器对象分表：每台可见窗口各一条，关掉谁就销毁谁。
    /// </summary>
    public static class MachineMarker
    {
        private static ManualLogSource log;
        private static bool enabled = true;

        public static void Install(ManualLogSource l)
        {
            log = l;
            MarkerOutline.Install(l);
        }

        private static void L(string s)
        {
            try { if (log != null) log.LogInfo("[mark] " + s); } catch (Exception) { }
        }

        public static string Cmd(string line)
        {
            if (line == "mark?")
                return "mark -> enabled=" + enabled + " " + MarkerOutline.Describe();
            if (line == "mark=off") { enabled = false; MarkerOutline.Hide(); return "mark -> off " + MarkerOutline.Describe(); }
            if (line == "mark=on") { enabled = true; MarkerOutline.Hide(); return "mark -> on"; }
            if (line == "mark=fill" || line == "mark=nofill") return MarkerOutline.Fill(line == "mark=fill");
            if (line == "mark=item") return MarkerOutline.Anchor(1);
            if (line == "mark=tint") return MarkerOutline.Anchor(2);
            if (line == "mark=auto") return MarkerOutline.Anchor(0);
            if (line == "slots") return Slots();
            if (line == "slots=0") return TreeDump();
            return "mark -> ?";
        }

        /// <summary>诊断：把机器所在库存的渲染树前三层打出来，定位真正的格子节点（SlotMarker 没有 transform）。</summary>
        private static string TreeDump()
        {
            var sb = new System.Text.StringBuilder("tree -> ");
            try
            {
                var all = GameBridge.AllItemsDeep();
                object machine = null;
                foreach (var it in all)
                {
                    string id = GameBridge.Get(it, "identifier") as string;
                    if (id != null && id.StartsWith("water_purifier")) { machine = it; break; }
                }
                if (machine == null) return sb.Append("no-machine").ToString();
                object inv = GameBridge.Get(machine, "parentInventory");
                if (inv == null) return sb.Append("no-inv").ToString();
                sb.Append(inv.GetType().Name).Append(" slot=");
                object slot = GameBridge.Call(inv, "GetItemInventorySlot", machine);
                if (slot != null) sb.Append(GameBridge.Get(slot, "index"));
                object inner = GameBridge.Get(inv, "inner");
                object handler = GameBridge.Get(inv, "handler");
                var it2 = inner as Component;
                if (it2 != null) { sb.Append("\n[inner]"); Dump(it2.transform, 1, sb, 3); }
                var ht = handler as Component;
                if (ht != null) { sb.Append("\n[handler]"); Dump(ht.transform, 1, sb, 3); }
                return sb.ToString();
            }
            catch (Exception e) { return sb.Append(" ERR ").Append(e.GetType().Name).ToString(); }
        }

        private static void Dump(Transform t, int depth, System.Text.StringBuilder sb, int maxDepth)
        {
            if (t == null || depth > maxDepth || sb.Length > 1500) return;
            sb.Append('\n').Append(new string(' ', depth * 2)).Append(t.name);
            var rt = t as RectTransform;
            if (rt != null) sb.Append('[').Append(Mathf.RoundToInt(rt.rect.width)).Append('x').Append(Mathf.RoundToInt(rt.rect.height)).Append(']');
            sb.Append('(');
            int n = 0;
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                if (n++ >= 3) break;
                sb.Append(c.GetType().Name).Append(',');
            }
            sb.Append(')');
            for (int i = 0; i < t.childCount; i++) Dump(t.GetChild(i), depth + 1, sb, maxDepth);
        }

        /// <summary>诊断：看每台机器的 parentInventory 与 GetItemInventorySlot 返回什么。</summary>
        private static string Slots()
        {
            var sb = new System.Text.StringBuilder("slots ->");
            try
            {
                for (int i = 0; i < PurifierHook.Pairs.Count; i++)
                {
                    var p = PurifierHook.Pairs[i];
                    if (p == null || p.Machine == null) { sb.Append(" #").Append(i).Append("=none"); continue; }
                    object m = p.Machine;
                    object inv = GameBridge.Get(m, "parentInventory");
                    sb.Append(" #").Append(i).Append(" inv=").Append(inv == null ? "null" : inv.GetType().Name);
                    if (inv == null) continue;
                    object slot = null;
                    try { slot = GameBridge.Call(inv, "GetItemInventorySlot", m); }
                    catch (Exception e) { sb.Append(" call=").Append(e.GetType().Name); continue; }
                    if (slot == null) { sb.Append(" slot=null"); continue; }
                    sb.Append(" slot=").Append(slot.GetType().Name);
                    sb.Append(" idx=").Append(GameBridge.Get(slot, "index"));
                    var rt = GameBridge.Get(slot, "rectTransform") as RectTransform;
                    if (rt != null) sb.Append(" rect=").Append(Mathf.RoundToInt(rt.rect.width)).Append('x').Append(Mathf.RoundToInt(rt.rect.height));
                }
            }
            catch (Exception e) { sb.Append(" ERR ").Append(e.GetType().Name); }
            return sb.ToString();
        }

        /// <summary>每帧调用：machineItem 非空只维护那一台；null 表示全部收起（兼容旧调用）。</summary>
        public static void Sync(object machineItem)
        {
            try
            {
                if (!enabled) { if (MarkerOutline.HasAny) MarkerOutline.Hide(); return; }
                if (machineItem == null) { if (MarkerOutline.HasAny) MarkerOutline.Hide(); return; }
                MarkerOutline.Show(machineItem);
            }
            catch (Exception e) { L("ERR " + e.GetType().Name + " " + e.Message); }
        }

        /// <summary>每帧调用：按「哪些净水器窗口开着」逐台维护描边（同时开几台就有几条）。</summary>
        public static void SyncAll()
        {
            try
            {
                if (!enabled) { if (MarkerOutline.HasAny) MarkerOutline.Hide(); return; }
                MarkerOutline.SyncAll();
#if DEV
                PurifierUi.Log("[marker] synced enabled=" + enabled + " entries=" + MarkerOutline.EntryCount +
                               " err=" + MarkerOutline.LastErr);
#endif
            }
            catch (Exception e) { L("ERR " + e.GetType().Name + " " + e.Message); }
        }
    }
}
