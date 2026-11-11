using System;
using System.IO;
using UnityEngine;

namespace WaterCalcMod
{
    /// <summary>
    /// 游戏原生 mod 入口（创意工坊只认这套：manifest.xml + IMod）。
    /// 旧 BepInEx 插件入口 Plugin.cs 不再参与编译；这里用同样的 Install/Tick 把功能拉起来，
    /// 区别只是不再依赖 BepInEx 的 BaseUnityPlugin 与 ManualLogSource。
    /// </summary>
    public class ModEntry : IMod
    {
        public static ManualLogSource Log;
        private GameObject host;

        public void Init(ModManifest manifest)
        {
            try
            {
                Log = new ManualLogSource("WaterCalcMod");
#if DEV
                // 开发版才落盘到 Mods\WaterCalcMod\log.txt；发布版清单不声明 files 能力，只写 Unity 控制台（F8 可见）。
                ManualLogSource.UseFile(Path.Combine(
                    Path.Combine(Application.dataPath, ".."), "Mods", "WaterCalcMod", "log.txt"));
#endif
                Log.LogInfo("init");
                GameBridge.Init(Log);
                try { Hotkey.Install(Log, manifest.ID); } catch (Exception e) { Log.LogWarning("[cfg] " + e.Message); }
                WaterPricePatch.Install(Log);
                PurifierHook.Install(Log);
                try { ContextHook.Install(Log); } catch (Exception e) { Log.LogWarning("[ctx] " + e.Message); }
                PurifierUi.Install(Log);
                CmdFile.Install(Log);
                MachineMarker.Install(Log);
                WaterBook.Install(Log);
#if DEV
                AutoLoad.Install(Log);
#endif
                Log.LogInfo("installed");
            }
            catch (Exception e)
            {
                if (Log != null) Log.LogError("init failed: " + e);
            }
        }

        public void OnEnable()
        {
            try
            {
                if (host == null)
                {
                    host = new GameObject("WaterCalcModHost");
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    host.AddComponent<Plugin>();   // Plugin 现在就是原生 MonoBehaviour，Awake/Update 逻辑原样保留
                    // 加载时把平衡模式设成「开」＝按瓶里真实水质算（玩家要求，之后可在 mod 设置窗改）
                    try { PurifierUi.SetBalance(true); } catch (Exception) { }
                }
                if (Log != null) Log.LogInfo("enabled");
            }
            catch (Exception e) { if (Log != null) Log.LogError("enable failed: " + e); }
        }

        public void OnDisable()
        {
            try
            {
                if (host != null) UnityEngine.Object.Destroy(host);
                host = null;
                if (Log != null) Log.LogInfo("disabled");
            }
            catch (Exception) { }
        }
    }

    /// <summary>原生 mod 没有 MonoBehaviour 入口，自己挂一个常驻对象跑每帧逻辑。</summary>
    public class ModTicker : MonoBehaviour
    {
        private void Update()
        {
            try { CmdFile.Tick(); } catch (Exception) { }
#if DEV
            try { MorningAuto.Watch(); } catch (Exception) { }
            try { AutoJoin.Watch(); } catch (Exception) { }
#endif
            try
            {
                if (Hotkey.Down()) PurifierUi.ToggleMain();
                PurifierUi.Tick();
            }
            catch (Exception) { }
        }
    }
}
