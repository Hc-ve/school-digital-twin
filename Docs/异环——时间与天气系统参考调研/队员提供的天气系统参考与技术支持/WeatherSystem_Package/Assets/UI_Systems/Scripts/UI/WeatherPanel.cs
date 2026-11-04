using UnityEngine;
using UnityEngine.UI;
using CampusUI.Core;
using CampusUI.Environment;

namespace CampusUI.UI
{
    /// <summary>
    /// 两级天气选择面板：一级「白天 / 夜晚」，二级「晴 / 多云 / 阴 / 雨 / 雷雨 / 雾 / 雪」。
    ///
    /// 做成独立控件的原因：数字孪生（左面板 148 宽）和第一人称（设置弹窗 308 宽）
    /// 都要这个功能，但可用宽度差一倍。所以宽度、列数、字号全部由调用方传进来，
    /// 内部按格子均分，不写死任何尺寸。
    ///
    /// 用法：
    ///   var p = WeatherPanel.Create(parentRt, 16f, y, 148f, 3, 11);
    ///   y += p.Height;
    /// </summary>
    public class WeatherPanel : MonoBehaviour
    {
        /// <summary>面板实际占用的高度，调用方用它推进 y 坐标。</summary>
        public float Height { get; private set; }

        private static readonly TimeOfDay[] Times = { TimeOfDay.Day, TimeOfDay.Night };

        private static readonly Weather[] All =
        {
            Weather.Clear, Weather.Cloudy, Weather.Overcast, Weather.Rain,
            Weather.Storm, Weather.Fog, Weather.Snow
        };

        private EnvironmentState env;
        private bool subscribed;
        private Button[] timeBtns;
        private Button[] wBtns;
        private Button autoBtn;

        // =====================================================================

        public static WeatherPanel Create(Transform parent, float x, float y, float width, int columns, int fontSize)
        {
            var go = new GameObject("WeatherPanel");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);

            var p = go.AddComponent<WeatherPanel>();
            p.Build(rt, width, Mathf.Clamp(columns, 2, 5), Mathf.Clamp(fontSize, 10, 16));
            return p;
        }

        private void Build(RectTransform rt, float width, int columns, int fontSize)
        {
            const float Gap = 6f;
            float cellH = fontSize + 13f;

            // ---- 第 0 行：自动轮换开关（对应《异环》的「随机模式」）----
            // 放在最上面，因为它是一个"模式"而不是某一档天气，混在天气网格里会让人误以为
            // 「自动」是第八种天气。
            autoBtn = UIStyle.Button(rt, 0f, 0f, width, cellH, AutoLabel(false),
                                     UITheme.Bg3, UITheme.TextDim, fontSize);
            autoBtn.onClick.AddListener(ToggleAuto);

            // ---- 第 1 行：白天 / 夜晚 ----
            float yTime = cellH + Gap;
            float tabW = (width - Gap) * 0.5f;
            timeBtns = new Button[Times.Length];
            for (int i = 0; i < Times.Length; i++)
            {
                int idx = i;
                var b = UIStyle.Button(rt, i * (tabW + Gap), yTime, tabW, cellH,
                                       EnvironmentState.TimeLabel(Times[i]),
                                       UITheme.Bg3, UITheme.TextDim, fontSize);
                b.onClick.AddListener(() => PickTime(idx));
                timeBtns[i] = b;
            }

            // ---- 天气网格 ----
            float cellW = (width - Gap * (columns - 1)) / columns;
            float y0 = yTime + cellH + Gap;
            wBtns = new Button[All.Length];
            for (int i = 0; i < All.Length; i++)
            {
                int idx = i;
                int cx = i % columns, cy = i / columns;
                var b = UIStyle.Button(rt, cx * (cellW + Gap), y0 + cy * (cellH + Gap), cellW, cellH,
                                       EnvironmentState.WeatherLabel(All[i]),
                                       UITheme.Bg3, UITheme.TextDim, fontSize);
                b.onClick.AddListener(() => PickWeather(idx));
                wBtns[i] = b;
            }

            int rows = (All.Length + columns - 1) / columns;
            Height = y0 + rows * cellH + (rows - 1) * Gap;
            rt.sizeDelta = new Vector2(width, Height);

            Subscribe();
            Refresh();
        }

        private static string AutoLabel(bool on)
        {
            return on ? "自动轮换 · 开" : "自动轮换 · 关";
        }

        // =====================================================================

        private EnvironmentState Env
        {
            get
            {
                if (env == null)
                {
                    env = EnvironmentState.Instance;
                    if (env == null) env = Object.FindObjectOfType<EnvironmentState>();
                    if (env == null)
                    {
                        var go = new GameObject("EnvironmentState");
                        env = go.AddComponent<EnvironmentState>();
                    }
                    Subscribe();
                }
                return env;
            }
        }

        private void Subscribe()
        {
            if (subscribed || env == null) return;
            env.OnChanged += OnEnvChanged;
            subscribed = true;
        }

        private void OnDestroy()
        {
            if (subscribed && env != null) env.OnChanged -= OnEnvChanged;
        }

        private void OnEnable()
        {
            Refresh();
        }

        private void OnEnvChanged(TimeOfDay t, Weather w)
        {
            Refresh();
        }

        private void PickTime(int i)
        {
            var e = Env;
            if (e == null) return;
            e.SetTime(Times[i]);
            Refresh();
        }

        private void PickWeather(int i)
        {
            var e = Env;
            if (e == null) return;

            // 手动选天气视为"接管控制权"：自动轮换自动退出。
            // 否则玩家刚选完晴天，40 秒后又被翻成雨天，会以为按钮没生效。
            var d = WeatherDirector.Instance;
            if (d != null && d.auto) d.SetAuto(false);

            e.SetWeather(All[i]);
            Refresh();
        }

        private void ToggleAuto()
        {
            var d = WeatherDirector.Ensure();
            d.SetAuto(!d.auto);
            Refresh();
        }

        // =====================================================================

        /// <summary>按当前环境状态刷新选中高亮。</summary>
        public void Refresh()
        {
            // AddComponent 会先触发 OnEnable，那时 Build 还没跑、按钮数组还是 null。
            // 这里直接返回，避免在 Build 之前就去摸 EnvironmentState（会提前创建它）。
            if (timeBtns == null && wBtns == null) return;

            var e = Env;
            if (e == null) return;

            if (timeBtns != null)
            {
                for (int i = 0; i < timeBtns.Length; i++)
                    Mark(timeBtns[i], e.timeOfDay == Times[i], UITheme.Blue);
            }
            if (wBtns != null)
            {
                for (int i = 0; i < wBtns.Length; i++)
                    Mark(wBtns[i], e.weather == All[i], UITheme.Teal);
            }

            // ---- 自动轮换开关 ----
            var d = WeatherDirector.Instance;
            bool autoOn = d != null && d.auto;
            if (autoBtn != null)
            {
                Mark(autoBtn, autoOn, UITheme.Amber);
                var at = autoBtn.GetComponentInChildren<Text>();
                if (at != null) at.text = AutoLabel(autoOn);
            }
        }

        private static void Mark(Button b, bool on, Color accent)
        {
            if (b == null) return;

            var cb = b.colors;
            cb.normalColor = on ? accent : UITheme.Bg3;
            cb.selectedColor = cb.normalColor;
            cb.highlightedColor = on ? UIStyle.Lighten(accent, 0.12f) : UITheme.Bg2;
            cb.pressedColor = on ? UIStyle.Darken(accent, 0.15f) : UITheme.Bg1;
            b.colors = cb;

            var t = b.GetComponentInChildren<Text>();
            if (t != null) t.color = on ? UITheme.Text : UITheme.TextDim;
        }
    }
}
