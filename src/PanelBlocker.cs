using System;
using UnityEngine;
using UnityEngine.UI;

namespace WaterCalcMod
{
    /// <summary>
    /// 输入隔离：在面板矩形上盖一块透明但可被 EventSystem 命中的 UI，
    /// 让游戏的 Unity UI 拿不到鼠标，避免点面板时误触发游戏里的东西。
    /// （InputGate 只挡 Input.GetMouseButton，挡不住 EventSystem 的 GraphicRaycaster。）
    /// </summary>
    public static class PanelBlocker
    {
        private static ManualLogSource log;
        private static GameObject root;
        private static RectTransform rt;
        private static Image img;
        private static bool ready;

        public static void Install(ManualLogSource logger)
        {
            log = logger;
            try
            {
                root = new GameObject("WaterCalcMod_InputBlocker");
                UnityEngine.Object.DontDestroyOnLoad(root);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30000;
                root.AddComponent<CanvasScaler>();
                root.AddComponent<GraphicRaycaster>();

                var child = new GameObject("block");
                child.transform.SetParent(root.transform, false);
                img = child.AddComponent<Image>();
                img.color = new Color(0f, 0f, 0f, 0f);   // 全透明，看不见
                img.raycastTarget = true;
                rt = img.rectTransform;
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                ready = true;
                log.LogInfo("[gate] 已装上 EventSystem 拦截层（透明 UI 覆盖面板）");
            }
            catch (Exception e)
            {
                log.LogWarning("[gate] 拦截层创建失败 " + e.Message);
            }
        }

        /// <summary>rect 是 IMGUI 坐标（左上原点）；visible=false 时把拦截层隐藏。</summary>
        public static void Sync(Rect rect, bool visible)
        {
            if (!ready) return;
            try
            {
                bool on = visible && rect.width > 1f && rect.height > 1f;
                if (img.enabled != on) img.enabled = on;
                if (!on) return;
                rt.anchoredPosition = new Vector2(rect.x, -rect.y);
                if (Math.Abs(rt.sizeDelta.x - rect.width) > 0.5f || Math.Abs(rt.sizeDelta.y - rect.height) > 0.5f)
                    rt.sizeDelta = new Vector2(rect.width, rect.height);
            }
            catch { }
        }
    }
}
