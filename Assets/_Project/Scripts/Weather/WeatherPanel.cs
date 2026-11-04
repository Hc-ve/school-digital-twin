using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.UI;

namespace SchoolTwin.Weather
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
    ///
    /// ── 【P13 移植时改写的一处，必须记下来】────────────────────────────
    /// 队员包原版依赖它自己的 `UIStyle.Button(...)` / `UITheme.Bg3·TextDim·Blue·Teal·Amber`
    /// —— **那两个类不在 12 文件清单里**，随包也没带过来。
    ///
    /// 三条路：
    ///   1. 把队员的 UIStyle/UITheme 也搬进来 → **任务包明令禁止**（会与本项目 TwinTheme 打架，
    ///      两套主色并存，正是"同一件事两个状态源"）
    ///   2. 把本文件删掉 → 但 `WeatherPanel` 是包内公开控件，删了以后谁想用就没得用
    ///   3. 把按钮工厂换成项目自己的 `TwinButton`，配色换成 `TwinTheme` token  ← **选这条**
    ///
    /// 布局算法（格子均分、Height 对外、字号传参）**一个字没动**，
    /// 所以队员文档 §11 对布局的描述仍然有效；变的只有"用什么画按钮"。
    /// 这也是任务包 §行要求「视觉全走 TwinTheme、禁止带队员包的 UIStyle/UITheme」的落地。
    ///
    /// ⚠️ 本项目的主天气入口是 `TwinTimeWeatherCard`（工具行第三项）。
    /// 本类是**通用控件**，保留给"某个面板里想内嵌一条天气选择"的场景，当前没有任何调用点，
    /// 因此它不会自己出现在界面上 —— 这是有意的，避免和模态卡重复。
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
        private TwinButton[] timeBtns;
        private TwinButton[] wBtns;
        private TwinButton autoBtn;

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
            autoBtn = MakeButton(rt, 0f, 0f, width, cellH, AutoLabel(false), fontSize);
            autoBtn.OnClick(ToggleAuto);

            // ---- 第 1 行：白天 / 夜晚 ----
            float yTime = cellH + Gap;
            float tabW = (width - Gap) * 0.5f;
            timeBtns = new TwinButton[Times.Length];
            for (int i = 0; i < Times.Length; i++)
            {
                int idx = i;
                var b = MakeButton(rt, i * (tabW + Gap), yTime, tabW, cellH,
                                   EnvironmentState.TimeLabel(Times[i]), fontSize);
                b.OnClick(() => PickTime(idx));
                timeBtns[i] = b;
            }

            // ---- 天气网格 ----
            float cellW = (width - Gap * (columns - 1)) / columns;
            float y0 = yTime + cellH + Gap;
            wBtns = new TwinButton[All.Length];
            for (int i = 0; i < All.Length; i++)
            {
                int idx = i;
                int cx = i % columns, cy = i / columns;
                var b = MakeButton(rt, cx * (cellW + Gap), y0 + cy * (cellH + Gap), cellW, cellH,
                                   EnvironmentState.WeatherLabel(All[i]), fontSize);
                b.OnClick(() => PickWeather(idx));
                wBtns[i] = b;
            }

            int rows = (All.Length + columns - 1) / columns;
            Height = y0 + rows * cellH + (rows - 1) * Gap;
            rt.sizeDelta = new Vector2(width, Height);

            Subscribe();
            Refresh();
        }

        /// <summary>
        /// 用项目的 <see cref="TwinButton"/> 造一个格子。
        /// 原来调的是队员包 `UIStyle.Button(rt,x,y,w,h,text,bg,fg,fontSize)`；
        /// TwinButton 的签名不同（宽度/高度在 Create 里、位置由 Place 给），
        /// 所以这里包一层，保持下面布局代码一个字不用改。
        /// </summary>
        private static TwinButton MakeButton(Transform parent, float x, float y,
                                             float w, float h, string caption, int fontSize)
        {
            TwinButton btn = TwinButton.Create(parent, caption, TwinButtonStyle.Secondary,
                                               w, h, null, "WPBtn");
            TwinUIFactory.Place(btn.Rect, x, y, w, h);

            Text lbl = btn.GetComponentInChildren<Text>(true);
            if (lbl != null) lbl.fontSize = fontSize;

            return btn;
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
                    if (env == null) env = FindObjectOfType<EnvironmentState>();
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
            // 只取一次 Env：不为它本身，为了触发「没有 EnvironmentState 就建一个」的兜底，
            // 否则桥上找 Instance 会拿到 null，时刻改了却没有天空侧响应。
            if (Env == null) return;

            // 【P13】选「白天/夜晚」等价于把时刻跳到 12:00 / 22:00 并接管时钟，
            // 不能再直调 SetTime —— 那样只改天空，室温仍按真实时钟算。
            // 两个数字与 TwinWeatherBridge.ToTimeOfDay 的分段同源（12 落在 Day，22 落在 Night）。
            double hour = Times[i] == TimeOfDay.Night ? 22.0 : 12.0;
            SchoolTwin.Twin.TwinWeatherBridge.ApplyHour(hour, true);
            Refresh();
        }

        private void PickWeather(int i)
        {
            // 同上：为兜底建 EnvironmentState，不直接对它写值（写值统一走桥）。
            if (Env == null) return;

            // 手动选天气视为"接管控制权"：自动轮换自动退出。
            // 否则玩家刚选完晴天，40 秒后又被翻成雨天，会以为按钮没生效。
            var d = WeatherDirector.Instance;
            if (d != null && d.auto) d.SetAuto(false);

            // 【P13】走桥而不是直调 SetWeather —— 直调只改视觉侧，
            // 室温那边还停在上一个天气。理由见 TwinWeatherBridge 类注释。
            SchoolTwin.Twin.TwinWeatherBridge.ApplyWeather(All[i]);
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

            // TwinButton 自带选中态（选中=AccentDim 底 + AccentLine 描边 + 主色字），
            // 所以原来手写的那套 Mark(...) 改色逻辑整段删掉了 —— 那是为了弥补
            // 队员包 UIStyle.Button 只有 normal/hover/pressed 三态、没有"选中"态。
            if (timeBtns != null)
            {
                for (int i = 0; i < timeBtns.Length; i++)
                    if (timeBtns[i] != null) timeBtns[i].SetSelected(e.timeOfDay == Times[i]);
            }
            if (wBtns != null)
            {
                for (int i = 0; i < wBtns.Length; i++)
                    if (wBtns[i] != null) wBtns[i].SetSelected(e.weather == All[i]);
            }

            // ---- 自动轮换开关 ----
            var d = WeatherDirector.Instance;
            bool autoOn = d != null && d.auto;
            if (autoBtn != null)
            {
                autoBtn.SetSelected(autoOn);
                autoBtn.SetText(AutoLabel(autoOn));
            }
        }
    }
}