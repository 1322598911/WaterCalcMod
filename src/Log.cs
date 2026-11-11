using System;
using System.IO;
using UnityEngine;

namespace WaterCalcMod
{
    /// <summary>
    /// 原生 mod 没有 BepInEx 的 ManualLogSource，这里给一个同名替代：
    /// 同时写 Unity 控制台（F8 能看到）和 Mods\WaterCalcMod\log.txt，方便没有控制台时排查。
    /// </summary>
    public class ManualLogSource
    {
        private readonly string tag;
        private static string logPath;
        private static bool quiet;

        public ManualLogSource(string source) { tag = source; }

        public static void SetQuiet(bool q) { quiet = q; }

        public static void UseFile(string path) { logPath = path; }

        public void LogInfo(string s) { Write("Info", s); }
        public void LogWarning(string s) { Write("Warn", s); }
        public void LogError(string s) { Write("Error", s); }
        // 旧代码里有 Logger.LogWarning(e)（直接传异常）的写法，接口对齐一下。
        public void LogInfo(object o) { Write("Info", o == null ? "null" : o.ToString()); }
        public void LogWarning(object o) { Write("Warn", o == null ? "null" : o.ToString()); }
        public void LogError(object o) { Write("Error", o == null ? "null" : o.ToString()); }
        public void LogDebug(string s) { if (!quiet) Write("Debug", s); }

        private void Write(string lvl, string s)
        {
            if (quiet) return;
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "][" + lvl + ":" + tag + "] " + s;
            try { UnityEngine.Debug.Log(line); } catch (Exception) { }
            if (logPath == null) return;
#if DEV
            try
            {
                // 超过 2 MB 就清空重写，别让 log.txt 无限长（游戏内每 0.4 秒都可能写一条）。
                // 注意：写文件属于 files 能力，创意工坊发布版必须编掉（/define 里不带 DEV）。
                var fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.WriteAllText(logPath, "");
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch (Exception) { }
#endif
        }
    }
}
