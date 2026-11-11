using System;
using System.IO;
using UnityEngine;

#if DEV
namespace WaterCalcMod
{
    /// <summary>
    /// 无人值守时的「自动进档」：停在主菜单超过若干秒就自己走 AutoLoad 读最近存档。
    ///
    /// 只有 `_tools\ctl.enable` 存在时才生效（和命令通道同一个开关），
    /// 玩家正常游玩时删掉那个文件即可，插件绝不会擅自读档。
    /// </summary>
    public static class AutoJoin
    {
        const float PollSeconds = 3f;
        const float WaitInMenu = 8f;    // 主菜单停留多久后动手
        const float RetrySeconds = 15f; // 失败后的重试间隔

        static ManualLogSource log;
        static float nextPoll;
        static float menuSince = -1f;
        static float nextRetry;
        static int tries;
        static bool disabled;

        public static void Install(ManualLogSource l) { log = l; }

        static void Info(string s) { if (log != null) log.LogInfo("[autojoin] " + s); }

        public static void Watch()
        {
#if DEV
            if (disabled) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + PollSeconds;

            try
            {
#if DEV
                // 自动进档曾经跟着 ctl.enable 一起开，结果玩家本地联调时 mod 会自己进游戏（玩家 2026-11-11 报障）。
                // 现在只认独立的 auto.enable，那个文件默认不存在 ⇒ 谁都用不到自动进档。
                if (!File.Exists(Path.Combine(CmdFile.Dir, "auto.enable"))) { menuSince = -1f; return; }
#else
                menuSince = -1f; return;   // 发布版：自动进档属开发功能，整体关闭
#endif
                if (!InMainMenu()) { menuSince = -1f; return; }

                if (menuSince < 0f) { menuSince = now; return; }
                if (now - menuSince < WaitInMenu) return;
                if (now < nextRetry) return;

                nextRetry = now + RetrySeconds;
                tries++;
                string r = AutoLoad.Run("latest") ?? "";
                if (r.StartsWith("slot=", StringComparison.OrdinalIgnoreCase))
                {
                    menuSince = -1f;
                    Info("dispatched try=" + tries + " " + r);
                }
                else
                {
                    Info("try=" + tries + " -> " + r);
                }
            }
            catch (Exception e)
            {
                if (log != null) log.LogWarning("[autojoin] fail " + e.GetType().Name + ": " + e.Message);
            }
        #endif
}

        static bool InMainMenu()
        {
            try
            {
                string s = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                return s == "EmporiumMenu" || s == "MainMenu" || s == "MainMenuScene";
            }
            catch (Exception) { return false; }
        }

        /// <summary>ctl: autojoin / autojoin=off / autojoin=on</summary>
        public static string Cmd(string arg)
        {
            if (arg == "off") { disabled = true; return "autojoin -> off"; }
            if (arg == "on") { disabled = false; menuSince = -1f; nextRetry = 0f; return "autojoin -> on tries=" + tries; }
            return "autojoin -> " + (disabled ? "off" : "on") + " tries=" + tries + " menu=" + InMainMenu();
        }
    }
}
#endif
