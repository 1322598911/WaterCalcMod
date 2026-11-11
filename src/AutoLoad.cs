using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

#if DEV
namespace WaterCalcMod
{
    /// <summary>
    /// 后台读档：不移动鼠标、不切窗口，直接走游戏自己的「开始游戏」链路载入最近存档。
    ///
    /// 调用链（反编译确认真实入口）：
    ///   LoadGameUI.LoadSlot(int slotId, string file)   [private static，10 条 IL]
    ///     -> NewGameData.Instance.saveSlot = slotId
    ///     -> NewGameData.Instance.saveFile = file
    ///     -> LoadGameUI.Close()
    ///     -> MainMenuUIController.Instance.StartNewGameClick()
    ///         -> SceneManager.LoadScene("InventoryScene")（0.25s 黑屏后）
    ///         -> PlayerStore.PreLoadStore() -> ES3.Load("playerStore", SaveFiles.ResolveLoadFile(...))
    ///
    /// 只读档：不调用任何 SaveGame / SaveFiles.RestoreFromHistory / RotateIntoHistory /
    /// DuplicateSlot / RenameSlot。载入前先自校验文件存在且完整，因为文件缺失时
    /// PlayerStore.LoadGame 会 SaveFiles.DeleteSlot(slot) 去清索引（那是写档）。
    /// </summary>
    public static class AutoLoad
    {
        const BindingFlags F = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static ManualLogSource log;
        static float lastDispatch = -9999f;

        public static void Install(ManualLogSource l) { log = l; }

        static void Info(string s) { if (log != null) log.LogInfo("[autoload] " + s); }
        static void Warn(string s) { if (log != null) log.LogWarning("[autoload] " + s); }

        // ── 入口 ────────────────────────────────────────────────────────────

        /// <summary>arg: "" / "latest" / "2" / "slot:2"。</summary>
        public static string Run(string arg)
        {
            try { return RunInner(arg == null ? "latest" : arg.Trim()); }
            catch (TargetInvocationException te)
            {
                return Fail(te.InnerException != null ? te.InnerException : te);
            }
            catch (Exception e) { return Fail(e); }
        }

        static string Fail(Exception e)
        {
            string name = e.GetType().Name;
            string msg = e.Message;
            Warn("load fail " + name + ": " + msg);
            if (log != null) log.LogWarning("[cmd] load fail " + name);
            return "FAIL " + name + ": " + msg;
        }

        static string RunInner(string arg)
        {
            Type tNGD = GameBridge.T("NewGameData");
            Type tMenu = GameBridge.T("MainMenuUIController");
            Type tLoad = GameBridge.T("LoadGameUI");
            Type tSF = GameBridge.T("SaveFiles");
            if (tNGD == null || tMenu == null || tLoad == null || tSF == null)
                return "FAIL missing-type（NewGameData/MainMenuUIController/LoadGameUI/SaveFiles 未找到）";

            string scene = SceneName();
            object ng = StatProp(tNGD, "Instance");
            if (ng == null) return "FAIL no-NewGameData scene=" + scene;

            // 主菜单判定：EmporiumMenu 就是主菜单场景；isInMainMenu 在部分版本被提前置 false，不能单独用。
            bool menuScene = scene == "EmporiumMenu" || scene == "MainMenu" || scene == "MainMenuScene";
            object ps = null;
            try { Type tPS = GameBridge.T("PlayerStore"); ps = tPS == null ? null : StatProp(tPS, "Instance"); } catch (Exception) { }
            if (!menuScene && ps != null)
                return "FAIL not-in-main-menu scene=" + scene + "（PlayerStore 已存在，拒绝自动读档以免覆盖当前进度）";
            if (!menuScene && !Truthy(GetAny(ng, "isInMainMenu")))
                return "FAIL not-in-main-menu scene=" + scene + "（不在主菜单）";

            object menu = StatProp(tMenu, "Instance");
            if (menu == null) return "FAIL no-MainMenuUIController scene=" + scene + "（主菜单控制器未就绪）";

            if (Time.realtimeSinceStartup - lastDispatch < 10f)
                return "FAIL busy（" + (int)(Time.realtimeSinceStartup - lastDispatch) + " 秒前刚触发过读档，等场景切换完成）";

            List<Cand> list = Candidates();
            string err;
            Cand pick = Pick(list, arg, out err);
            if (pick == null) return "FAIL " + err + " dir=" + SaveDir();

            string bad = CheckCand(pick, tSF);
            if (bad != null) return "FAIL " + bad;

            // load=dry：只做选档+校验，不触发场景切换（给装机前联调用）
            if (string.Equals(arg, "dry", StringComparison.OrdinalIgnoreCase))
                return "slot=" + pick.slot + " file=" + pick.rel + " dry=1（未触发读档）";

            return Dispatch(ng, menu, tLoad, pick);
        }

        /// <summary>只读诊断：当前场景 / 主菜单状态 / 存档目录。</summary>
        public static string Status()
        {
            try
            {
                Type tNGD = GameBridge.T("NewGameData");
                Type tMenu = GameBridge.T("MainMenuUIController");
                object ng = tNGD == null ? null : StatProp(tNGD, "Instance");
                object menu = tMenu == null ? null : StatProp(tMenu, "Instance");
                string s = "scene=" + SceneName() + " newGameData=" + (ng != null) + " mainMenu=" + (menu != null);
                if (ng != null)
                    s += " isInMainMenu=" + GetAny(ng, "isInMainMenu") + " saveSlot=" + GetAny(ng, "saveSlot") + " saveFile=" + GetAny(ng, "saveFile");
                s += " dir=" + SaveDir();
                return s;
            }
            catch (Exception e) { return "FAIL " + e.GetType().Name + ": " + e.Message; }
        }

        // ── 存档枚举 ────────────────────────────────────────────────────────

        class Cand
        {
            public int slot;
            public string rel;
            public string abs;
            public long bytes;
            public DateTime mtime;
            public int order;     // 索引里的顺序，0 = 游戏认为最近
            public string Time { get { return mtime.ToString("yyyy-MM-dd HH:mm:ss"); } }
        }

        /// <summary>存档目录 = Application.persistentDataPath（与 SaveFiles.AbsolutePath 同一口径）。</summary>
        static string SaveDir()
        {
            try
            {
                string p = Application.persistentDataPath;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch (Exception) { }
            try
            {
                Type tSF = GameBridge.T("SaveFiles");
                if (tSF != null)
                {
                    MethodInfo mi = tSF.GetMethod("AbsolutePath", F, null, new Type[] { typeof(string) }, null);
                    if (mi != null)
                    {
                        object r = mi.Invoke(null, new object[] { "probe.es3" });
                        if (r is string) return System.IO.Path.GetDirectoryName((string)r);
                    }
                }
            }
            catch (Exception) { }
            return "";
        }

        static List<Cand> Candidates()
        {
            List<Cand> list = new List<Cand>();
            string dir = SaveDir();
            if (string.IsNullOrEmpty(dir)) return list;

            HashSet<int> seen = new HashSet<int>();

            // 1) 游戏自己的「最近优先」顺序：SaveUIManager.GetOrderedSlotIds()
            List<int> ordered = OrderedSlotIds();
            int order = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                int id = ordered[i];
                if (seen.Contains(id)) continue;
                Cand c = Make(dir, id);
                if (c == null) continue;
                c.order = order++;
                seen.Add(id);
                list.Add(c);
            }

            // 2) 目录兜底：索引缺失时也能读到
            try
            {
                string[] files = System.IO.Directory.GetFiles(dir, "save_*.es3");
                for (int i = 0; i < files.Length; i++)
                {
                    int id;
                    if (!ParseSlot(System.IO.Path.GetFileName(files[i]), out id)) continue;
                    if (seen.Contains(id)) continue;
                    Cand c = Make(dir, id);
                    if (c == null) continue;
                    c.order = 999;
                    seen.Add(id);
                    list.Add(c);
                }
            }
            catch (Exception e) { Warn("scan-dir fail " + e.GetType().Name + ": " + e.Message); }

            return list;
        }

        static bool ParseSlot(string fileName, out int slot)
        {
            slot = -1;
            if (fileName == null) return false;
            if (!fileName.StartsWith("save_", StringComparison.OrdinalIgnoreCase)) return false;
            if (!fileName.EndsWith(".es3", StringComparison.OrdinalIgnoreCase)) return false;
            string mid = fileName.Substring(5, fileName.Length - 5 - 4);
            return int.TryParse(mid, out slot);
        }

        static Cand Make(string dir, int id)
        {
            string rel = "save_" + id + ".es3";
            string abs = System.IO.Path.Combine(dir, rel);
            if (!System.IO.File.Exists(abs)) return null;
            Cand c = new Cand();
            c.slot = id;
            c.rel = rel;
            c.abs = abs;
            try
            {
                System.IO.FileInfo fi = new System.IO.FileInfo(abs);
                c.bytes = fi.Length;
                c.mtime = fi.LastWriteTime;
            }
            catch (Exception) { c.mtime = DateTime.MinValue; }
            return c;
        }

        static List<int> OrderedSlotIds()
        {
            List<int> res = new List<int>();
            try
            {
                Type tSU = GameBridge.T("SaveUIManager");
                if (tSU == null) return res;
                MethodInfo mi = tSU.GetMethod("GetOrderedSlotIds", F, null, Type.EmptyTypes, null);
                if (mi == null) return res;
                object r = mi.Invoke(null, null);
                IEnumerable en = r as IEnumerable;
                if (en == null) return res;
                foreach (object o in en) if (o is int) res.Add((int)o);
            }
            catch (Exception e) { Warn("GetOrderedSlotIds fail " + e.GetType().Name + ": " + e.Message); }
            return res;
        }

        /// <summary>选档：latest = 文件 mtime 最新（索引顺序做同分裁决）。</summary>
        static Cand Pick(List<Cand> list, string arg, out string err)
        {
            err = null;
            if (list.Count == 0) { err = "no-save-file"; return null; }

            string a = (arg == null ? "" : arg.Trim().ToLowerInvariant());
            if (a == "dry") a = "latest";   // load=dry 走最近档，但不触发读档
            if (a.Length == 0 || a == "latest" || a == "newest" || a == "recent")
            {
                Cand best = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    Cand c = list[i];
                    if (c.mtime > best.mtime) best = c;
                    else if (c.mtime == best.mtime && c.order < best.order) best = c;
                }
                return best;
            }

            if (a.StartsWith("slot:")) a = a.Substring(5);
            int id;
            if (int.TryParse(a, out id))
            {
                for (int i = 0; i < list.Count; i++) if (list[i].slot == id) return list[i];
                err = "no-slot " + id;
                return null;
            }

            err = "bad-arg " + arg;
            return null;
        }

        /// <summary>只读校验：文件存在 + SaveFiles.IsSaveFileIntact（首字节 { / 末字节 } / 非全零）。</summary>
        static string CheckCand(Cand c, Type tSF)
        {
            if (!System.IO.File.Exists(c.abs)) return "file-missing " + c.rel;
            if (c.bytes <= 0) return "file-empty " + c.rel;
            if (tSF == null) return null;
            MethodInfo mi = tSF.GetMethod("IsSaveFileIntact", F, null, new Type[] { typeof(string), typeof(string).MakeByRefType() }, null);
            if (mi == null) return null;
            object[] args = new object[] { c.abs, null };
            object r = mi.Invoke(null, args);
            if (r is bool && !(bool)r)
                return "not-intact " + c.rel + " (" + (args[1] == null ? "no-reason" : args[1].ToString()) + ")";
            return null;
        }

        // ── 真正触发读档 ────────────────────────────────────────────────────

        static string Dispatch(object ng, object menu, Type tLoad, Cand c)
        {
            FieldInfo fSlot = GetFieldDeep(ng.GetType(), "saveSlot");
            FieldInfo fFile = GetFieldDeep(ng.GetType(), "saveFile");
            if (fSlot == null) return "FAIL field-missing NewGameData.saveSlot";
            fSlot.SetValue(ng, c.slot);
            if (fFile != null) fFile.SetValue(ng, c.rel);

            // 首选游戏自己的 private 入口 LoadSlot：它就是上面那 10 条 IL，无额外副作用。
            MethodInfo loadSlot = tLoad.GetMethod("LoadSlot", F, null, new Type[] { typeof(int), typeof(string) }, null);
            if (loadSlot != null)
            {
                loadSlot.Invoke(null, new object[] { c.slot, c.rel });
                lastDispatch = Time.realtimeSinceStartup;
                Info("dispatched via=LoadGameUI.LoadSlot slot=" + c.slot + " file=" + c.rel + " mtime=" + c.Time);
                return "slot=" + c.slot + " file=" + c.rel;
            }

            // 兜底：等价手工链（全部 public）
            MethodInfo close = tLoad.GetMethod("Close", F, null, Type.EmptyTypes, null);
            if (close != null)
            {
                try { close.Invoke(null, null); }
                catch (Exception e) { Warn("LoadGameUI.Close fail " + e.GetType().Name + ": " + e.Message); }
            }
            MethodInfo start = menu.GetType().GetMethod("StartNewGameClick", F, null, Type.EmptyTypes, null);
            if (start == null) return "FAIL method-missing MainMenuUIController.StartNewGameClick";
            start.Invoke(menu, null);
            lastDispatch = Time.realtimeSinceStartup;
            Info("dispatched via=manual(saveSlot+saveFile+Close+StartNewGameClick) slot=" + c.slot + " file=" + c.rel + " mtime=" + c.Time);
            return "slot=" + c.slot + " file=" + c.rel;
        }

        // ── 反射小工具 ──────────────────────────────────────────────────────

        static object StatProp(Type t, string name)
        {
            if (t == null) return null;
            try
            {
                PropertyInfo p = t.GetProperty(name, F);
                if (p != null) return p.GetValue(null, null);
                FieldInfo f = t.GetField(name, F);
                if (f != null) return f.GetValue(null);
                FieldInfo bf = t.GetField("<" + name + ">k__BackingField", F);
                if (bf != null) return bf.GetValue(null);
            }
            catch (Exception) { }
            return null;
        }

        static object GetAny(object o, string field)
        {
            if (o == null) return null;
            try
            {
                FieldInfo f = GetFieldDeep(o.GetType(), field);
                if (f != null) return f.GetValue(o);
                PropertyInfo p = GetPropDeep(o.GetType(), field);
                if (p != null) return p.GetValue(o, null);
            }
            catch (Exception) { }
            return null;
        }

        static FieldInfo GetFieldDeep(Type t, string name)
        {
            while (t != null)
            {
                FieldInfo f = t.GetField(name, F);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        static PropertyInfo GetPropDeep(Type t, string name)
        {
            while (t != null)
            {
                PropertyInfo p = t.GetProperty(name, F);
                if (p != null) return p;
                t = t.BaseType;
            }
            return null;
        }

        static bool Truthy(object v) { return v is bool && (bool)v; }

        static string SceneName()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
            catch (Exception) { return "?"; }
        }

        // ── saves：只读列表 ─────────────────────────────────────────────────

        public static string RunList()
        {
            try
            {
                List<Cand> list = Candidates();
                if (list.Count == 0) return "none dir=" + SaveDir();
                Cand latest = null;
                for (int i = 0; i < list.Count; i++)
                    if (latest == null || list[i].mtime > latest.mtime) latest = list[i];

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("count=" + list.Count + " dir=" + SaveDir());
                for (int i = 0; i < list.Count; i++)
                {
                    Cand c = list[i];
                    sb.Append(" | ");
                    if (c == latest) sb.Append("*latest ");
                    sb.Append("[" + c.slot + "] " + c.rel + " " + (c.bytes / 1024) + "KB " + c.Time);
                }
                return sb.ToString();
            }
            catch (Exception e) { return "FAIL " + e.GetType().Name + ": " + e.Message; }
        }

        /// <summary>给 ctl.txt 的 load 用：该行要做的最小解析（load / load=latest / load=2）。</summary>
        public static string ArgOf(string line)
        {
            int eq = line.IndexOf('=');
            if (eq < 0) return "latest";
            string a = line.Substring(eq + 1).Trim();
            return a.Length == 0 ? "latest" : a;
        }
    }
}
#endif
