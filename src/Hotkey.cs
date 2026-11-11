using System;
using UnityEngine;

namespace WaterCalcMod
{
    /// <summary>
    /// 模组设置里的「面板快捷键」：走官方 ModConfig（主菜单 → 模组 → 净水助手 后面的齿轮面板），
    /// 只提供这一个条目；值存 ModConfigs/watercalc_assistant.json，跨存档共享。
    /// 改键捕获由游戏自带的配置面板负责（点一下后按下一次键，Escape 取消）。
    /// </summary>
    public static class Hotkey
    {
        // 候选键：避开 F8（游戏调试控制台）与 F9（游戏开发菜单）
        public static readonly string[] Choices = {
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F10", "F11", "F12",
            "Q", "E", "R", "T", "Y", "U", "I", "O", "P", "B", "N", "M",
            "H", "J", "K", "L", "V", "X", "Z",
            "Tab", "Delete", "Insert", "Home", "End", "PageUp", "PageDown"
        };

        public static string KeyName = "F7";
        private static KeyCode key = KeyCode.F7;
        private static ManualLogSource log;

        public static void Install(ManualLogSource l, string modId)
        {
            log = l;
            try
            {
                ModConfig config = ModHelper.GetConfig(modId);
                if (config == null) { log.LogWarning("[cfg] 拿不到 ModConfig，快捷键仍是 F7"); return; }
                ConfigEntry<string> e = config.Define("panel_key", "F7", "净水助手", () => "面板快捷键",
                    Choices, () => "打开/关闭净水助手面板（默认 F7）。改完点右下角「应用」。", false);
                Apply(e.Value);
                e.OnChanged += v => Apply(v);
                log.LogInfo("[cfg] 面板快捷键 = " + KeyName);
            }
            catch (Exception e) { log.LogWarning("[cfg] 注册快捷键设置失败 " + e.GetType().Name + " " + e.Message); }
        }

        private static void Apply(string name)
        {
            key = Parse(name);
            KeyName = string.IsNullOrEmpty(name) ? "F7" : name.Trim();
            if (log != null) log.LogInfo("[cfg] 面板快捷键 = " + KeyName);
        }

        private static KeyCode Parse(string name)
        {
            if (string.IsNullOrEmpty(name)) return KeyCode.F7;
            switch (name.Trim().ToUpperInvariant())
            {
                case "F1": return KeyCode.F1;
                case "F2": return KeyCode.F2;
                case "F3": return KeyCode.F3;
                case "F4": return KeyCode.F4;
                case "F5": return KeyCode.F5;
                case "F6": return KeyCode.F6;
                case "F7": return KeyCode.F7;
                case "F10": return KeyCode.F10;
                case "F11": return KeyCode.F11;
                case "F12": return KeyCode.F12;
                case "Q": return KeyCode.Q;
                case "E": return KeyCode.E;
                case "R": return KeyCode.R;
                case "T": return KeyCode.T;
                case "Y": return KeyCode.Y;
                case "U": return KeyCode.U;
                case "I": return KeyCode.I;
                case "O": return KeyCode.O;
                case "P": return KeyCode.P;
                case "B": return KeyCode.B;
                case "N": return KeyCode.N;
                case "M": return KeyCode.M;
                case "H": return KeyCode.H;
                case "J": return KeyCode.J;
                case "K": return KeyCode.K;
                case "L": return KeyCode.L;
                case "V": return KeyCode.V;
                case "X": return KeyCode.X;
                case "Z": return KeyCode.Z;
                case "TAB": return KeyCode.Tab;
                case "DELETE": return KeyCode.Delete;
                case "INSERT": return KeyCode.Insert;
                case "HOME": return KeyCode.Home;
                case "END": return KeyCode.End;
                case "PAGEUP": return KeyCode.PageUp;
                case "PAGEDOWN": return KeyCode.PageDown;
                default: return KeyCode.F7;
            }
        }

        /// <summary>每帧轮询：游戏正在捕获改键时不响应。</summary>
        public static bool Down()
        {
            if (key == KeyCode.None) return false;
            try { if (KeyBindHelper.SuppressHotkeys) return false; }
            catch (Exception) { }
            return Input.GetKeyDown(key);
        }
    }
}
