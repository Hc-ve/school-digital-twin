using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Data;
using SchoolTwin.Twin;      // P13：TwinWeatherBridge（唯一写入出口）
using SchoolTwin.Weather;

// 别名：理由见 TwinWeatherBridge.cs 顶部注释（`SchoolTwin.Weather` 命名空间遮蔽同名枚举）。
using WeatherKind = SchoolTwin.Weather.Weather;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P13】时间天气模态卡（图纸：`Docs/P13-1_时间天气面板设计图纸_见微.md`）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【尺寸为什么不按任务包的 420×300】
    /// 任务包给的是占位稿，见微 §0 实算后裁决 **480×360**：
    ///   垂直方向 420×300 差 34px、水平差 20px；表盘直径需求 200（12 刻度 + 4 数字标 +
    ///   中心 28 号字同层不撞，最小直径 192），300 高的卡里塞不下。
    /// 图纸是施工依据，任务包的 420×300 按图纸订正。**这处订正写进回执。**
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【零图片资源约束下的表盘怎么画】
    /// 全部用 `TwinRoundedRect`（程序化圆角矩形，零纹理）：
    ///   · 环面 = 200×200 圆角 radius=100 的**实心圆**
    ///   · 内盘 = 152×152 圆角 radius=76 的实心圆，叠在上面 → 抠出 24px 宽的环
    ///   · 刻度 = 12 根 1×176 的隐形长条，各自绕圆心旋转 −i×30°，
    ///     子节点一个 1×6（次）/1×8（主）的点贴在长条顶端
    ///   · 指针 = 一个空 RectTransform 绕圆心旋转 −hour×15°，子节点是光晕 + 亮点
    /// 没有一个 Sprite、没有一个 TMP、没有一个汉字图标。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么刻度用「隐形长条 + 顶端点」而不是「算坐标摆 12 个点」】
    /// 摆坐标要写 12 组 sin/cos，且以后改半径要重算全部；旋转长条只需 12 个 rotation.z，
    /// 半径是长条长度的一半，改一处全变。**也正因如此，刻度点必须贴在长条顶端
    /// （y=0，pivot 中心的长条在半长 88 处），而不是长条中心** ——
    /// 贴中心的话 12 个点会全挤在 r=0 的圆心上。
    /// </summary>
    public class TwinTimeWeatherCard : MonoBehaviour
    {
        // ---- 卡片尺寸（图纸 §1）----
        private const float CardW = 480f;
        private const float CardH = 360f;

        // ---- 表盘几何（图纸 §2.2）----
        private const float DialCX = 240f;      // 卡内圆心 x
        private const float DialCY = 156f;      // 卡内圆心 y（y 向下）
        private const float DialD = 200f;       // 环外径
        private const float DiscD = 152f;       // 内盘直径 → 环宽 (200−152)/2 = 24
        private const float TickLen = 176f;     // 刻度条长 = 环半径 88 × 2
        private const float TickR = TickLen * 0.5f;   // 88，刻度点落在 r 82–88

        // ---- 天气四档（图纸 §2.3：随机 / 晴天 / 雨天 / 雪天）----
        //
        // 【为什么是这四档，而不是视觉侧的完整 7 档】
        // 图纸 §2.3 定的是四块等分。四档对应的是**用户真正会想要的四种天气**，
        // 而视觉 7 档里 Overcast/Storm/Fog 是"同族更极端的版本"：
        //   · 阴 = 多云的一种，用户要阴天时会点"随机"让它自己走到
        //   · 雷雨 = 雨天的一种
        //   · 雾   = 极少用于演示（雾会把整个校园糊掉，答辩时看不出建筑）
        // 所以面板暴露 4 档操作入口，内部经 TwinWeatherBridge 映射到 7 档视觉 + 4 档数据。
        private static readonly string[] WeatherNames = { "随机", "晴天", "雨天", "雪天" };

        /// <summary>"随机"不是一档天气，是一个动作：向天气系统请求随机。索引 0 特判。</summary>
        private const int RandomIndex = 0;

        /// <summary>按钮索引 → 视觉档。索引 0（随机）不在这张表里，特判处理。</summary>
        private static readonly WeatherKind[] PickWeather = { WeatherKind.Clear, WeatherKind.Clear, WeatherKind.Rain, WeatherKind.Snow };

        // ---- 运行时节点 ----
        private RectTransform _card;
        private RectTransform _pivot;         // 指针枢轴（只转这个）
        private Text _timeText;               // 中心 HH:mm
        private Text _statusText;             // 底行状态
        private TwinButton[] _buttons;

        private Action _onClosed;

        /// <summary>当前拖动出来的小时数（0~24，保留小数）。指针角度与文字都由它推。</summary>
        private double _hour = 12.0;

        /// <summary>正在拖动：拖动期间不要再从 TwinClimate 反向同步，否则会自我拉扯。</summary>
        private bool _dragging;

        private readonly StringBuilder _sb = new StringBuilder(64);

        // =================================================================
        // 入口
        // =================================================================

        /// <summary>
        /// 弹出居中模态卡。<paramref name="onClosed"/> 在关闭时回调一次（用来让调用方清引用）。
        ///
        /// **父节点用画布根**，不是抽屉根：图纸 §1 要求遮罩盖住 1000×720 全屏，
        /// 而抽屉只有 440 宽。挂在抽屉里遮罩就盖不满，右上角会露出没变暗的场景。
        /// </summary>
        public static TwinTimeWeatherCard Open(Transform canvasRoot, Action onClosed)
        {
            if (canvasRoot == null) return null;

            GameObject go = TwinUIFactory.Node("TimeWeatherCard", canvasRoot);
            RectTransform rt = (RectTransform)go.transform;
            TwinUIFactory.Stretch(rt);          // 遮罩层铺满画布
            rt.SetAsLastSibling();              // 显式置顶，不靠建顺序侥幸

            TwinTimeWeatherCard card = go.AddComponent<TwinTimeWeatherCard>();
            card._onClosed = onClosed;
            card.Build();
            return card;
        }

        /// <summary>关闭并销毁。Esc / 点遮罩 / 点 × 三处等价，都会恢复真实时钟（图纸 §4）。</summary>
        public void Close()
        {
            // 恢复真实时钟：这是"三处等价"里唯一的副作用，必须写在关闭路径上，
            // 而不是只写在 Esc 那一条上 —— 否则点 × 关掉后时钟还是停在拖动值。
            TwinWeatherBridge.ApplyHour(TwinClimate.HourOfDay, false);

            Action cb = _onClosed;
            _onClosed = null;
            if (cb != null) cb();

            TwinUIFactory.DestroySafe(gameObject);
        }

        // =================================================================
        // 构建（顺序严格按图纸 §3 的 B11 纪律）
        // =================================================================

        private void Build()
        {
            // 起始时刻：取当前真实/覆盖时刻，指针直接落在那里（图纸 §4：不做从 0 扫入的入场动画）
            _hour = NormalizeHour(TwinClimate.HourOfDay);

            // ---- 1. 遮罩 ----
            Image scrim = TwinUIFactory.Block(transform, "TWS_Scrim",
                TwinTheme.ModalScrim, true);
            TwinUIFactory.Stretch(scrim.rectTransform);

            // 点空白关闭。整块 scrim 当按钮，比只点 × 好按；
            // 卡片自己的子节点会先吃掉点击（卡内元素 raycast=true），所以点卡不会关。
            Button scrimBtn = scrim.gameObject.AddComponent<Button>();
            scrimBtn.targetGraphic = scrim;
            scrimBtn.transition = Selectable.Transition.None;
            scrimBtn.onClick.AddListener(Close);

            // ---- 2. 卡片底 ----
            TwinRoundedRect cardBg = TwinUIFactory.Rounded(transform, "TWS_Card", TwinTheme.R16,
                TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border, 1f, true);
            _card = cardBg.rectTransform;

            // 【居中用 anchor 而不是绝对 (260,180)】
            // 图纸 §1 给的 (1000−480)/2、(720−360)/2 只在画布**恰好** 1000×720 时成立。
            // 本项目主画布是 `match=0` 的 CanvasScaler：窗口比例一变，画布**高度**就会变
            // （P29-3.6 已记录过 1069×654 窗口下画布只有 1004×614）。
            // 那时绝对 y=180 会让卡片偏上/偏下，而"居中"是图纸的真实意图。
            // 用中心锚点后：1000×720 下算出来仍是 (260,180)，其它尺寸下自动保持居中。
            _card.anchorMin = new Vector2(0.5f, 0.5f);
            _card.anchorMax = new Vector2(0.5f, 0.5f);
            _card.pivot = new Vector2(0.5f, 0.5f);
            _card.anchoredPosition = Vector2.zero;
            _card.sizeDelta = new Vector2(CardW, CardH);

            BuildTitle();
            BuildDial();
            BuildButtons();
            BuildStatus();

            // ---- Hit 最后建（图纸 §3 #15：必须在内容之上，否则拖不动）----
            BuildHitArea();

            RefreshAll();
        }

        private void BuildTitle()
        {
            // 顶部高光线
            Image hi = TwinUIFactory.Block(_card, "TWS_TopHi", TwinTheme.TopHighlight, false);
            TwinUIFactory.Place(hi.rectTransform, 12f, 1f, CardW - 24f, 1f);

            Text title = TwinUIFactory.Label(_card, "TWS_Title", TwinTheme.FsBodyStrong,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(title.rectTransform, 20f, 14f, 200f, 22f);
            title.text = "时间天气";
            title.raycastTarget = false;

            // 关闭钮（底 + 字两层，字不接收点击，点击走底）
            TwinRoundedRect closeBg = TwinUIFactory.Rounded(_card, "TWS_CloseBg", TwinTheme.R8,
                new Color(0f, 0f, 0f, 0f), new Color(0f, 0f, 0f, 0f), Color.clear, 0f, true);
            TwinUIFactory.Place(closeBg.rectTransform, 436f, 14f, 28f, 28f);
            Button closeBtn = closeBg.gameObject.AddComponent<Button>();
            closeBtn.targetGraphic = closeBg;
            closeBtn.transition = Selectable.Transition.None;
            closeBtn.onClick.AddListener(Close);

            Text closeTxt = TwinUIFactory.Label(closeBg.rectTransform, "TWS_CloseTxt", TwinTheme.FsBody,
                TextAnchor.MiddleCenter, TwinTheme.TextSecondary);
            TwinUIFactory.Place(closeTxt.rectTransform, 0f, 0f, 28f, 28f);
            closeTxt.text = "×";
            closeTxt.raycastTarget = false;

            // 提示行
            Text hint = TwinUIFactory.Label(_card, "TWS_Hint", TwinTheme.FsMicro,
                TextAnchor.MiddleCenter, TwinTheme.TextMuted);
            TwinUIFactory.Place(hint.rectTransform, 20f, 40f, 440f, 12f);
            hint.text = "拖动表盘选择时间 · Esc 恢复真实时钟";
            hint.raycastTarget = false;
        }

        private void BuildDial()
        {
            // ---- 外圆环面（实心圆，靠半径 = 半宽 得到正圆）----
            TwinRoundedRect ring = TwinUIFactory.Rounded(_card, "TWS_Ring", DialD * 0.5f,
                TwinTheme.AccentDim, TwinTheme.AccentDim, Color.clear, 0f, false);
            TwinUIFactory.Place(ring.rectTransform, DialCX - DialD * 0.5f, DialCY - DialD * 0.5f, DialD, DialD);

            // ---- 内盘：叠在环上抠出 24px 宽的环 ----
            TwinRoundedRect disc = TwinUIFactory.Rounded(_card, "TWS_Disc", DiscD * 0.5f,
                TwinTheme.PanelBottom, TwinTheme.PanelBottom, Color.clear, 0f, false);
            TwinUIFactory.Place(disc.rectTransform, DialCX - DiscD * 0.5f, DialCY - DiscD * 0.5f, DiscD, DiscD);

            BuildTicks();
            BuildHourMarks();

            // ---- 指针枢轴：零尺寸，Place 的 x/y 即圆心 ----
            GameObject pv = TwinUIFactory.Node("TWS_Pivot", _card);
            _pivot = (RectTransform)pv.transform;
            TwinUIFactory.Place(_pivot, DialCX, DialCY, 0f, 0f);

            // 指针光晕：叠在环面上把点"托"出来。y 向下坐标系里"上方"是负值，
            // 中心要落在圆心上方 88（TickR），所以顶边 = −88 − 8.5
            TwinRoundedRect glow = TwinUIFactory.Rounded(_pivot, "TWS_Glow", 8.5f,
                TwinTheme.AccentDim, TwinTheme.AccentDim, Color.clear, 0f, false);
            TwinUIFactory.Place(glow.rectTransform, -8.5f, -TickR - 8.5f, 17f, 17f);

            // 指针亮点：全卡唯一高亮元素（一卡一焦点）
            TwinRoundedRect dot = TwinUIFactory.Rounded(_pivot, "TWS_Dot", 4.5f,
                TwinTheme.AccentHi, TwinTheme.AccentHi, Color.clear, 0f, false);
            TwinUIFactory.Place(dot.rectTransform, -4.5f, -TickR - 4.5f, 9f, 9f);

            // ---- 中心时刻 ----
            _timeText = TwinUIFactory.Label(_card, "TWS_Time", TwinTheme.FsDisplay,
                TextAnchor.MiddleCenter, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_timeText.rectTransform,
                DialCX - DialD * 0.5f, DialCY - 14f, DialD, 30f);
            _timeText.raycastTarget = false;
        }

        /// <summary>12 根刻度。i=0 在 12 点方向，顺时针每根 +2 小时。</summary>
        private void BuildTicks()
        {
            for (int i = 0; i < 12; i++)
            {
                // 刻度条本身不可见（只有子节点那个点可见），所以用 Node 而不是 Block
                GameObject barGo = TwinUIFactory.Node("Tick_" + i, _card);
                RectTransform bar = (RectTransform)barGo.transform;
                TwinUIFactory.Place(bar, DialCX, DialCY, 1f, TickLen);

                // 【pivot 必须改成中心】Place 设的是 pivot(0,1)，绕左上角转的话
                // 12 根条会绕着圆心各偏一格，刻度点全挤到一边。
                bar.pivot = new Vector2(0.5f, 0.5f);
                bar.anchoredPosition = new Vector2(DialCX, -DialCY);

                // Unity 的 z 正方向是逆时针，而小时是顺时针走，所以取负。
                bar.localRotation = Quaternion.Euler(0f, 0f, -i * 30f);

                // 主刻度 i=0,3,6,9（对应 00/06/12/18 点）：更亮、更长
                bool major = (i % 3) == 0;
                Image tick = TwinUIFactory.Block(bar, "Dot",
                    major ? TwinTheme.AccentLine : TwinTheme.Track, false);
                // 贴在长条顶端：长条高 176、pivot 中心，顶端在局部 y = +88。
                // 点的 pivot 也是中心，所以 y = 88 − 点高/2；这里长条自身在旋转，
                // 用 anchor 中心 + anchoredPosition 表达更稳，不走 Place。
                RectTransform tr = tick.rectTransform;
                tr.anchorMin = tr.anchorMax = new Vector2(0.5f, 1f);
                tr.pivot = new Vector2(0.5f, 0.5f);
                tr.sizeDelta = new Vector2(1f, major ? 8f : 6f);
                tr.anchoredPosition = new Vector2(0f, 0f);
            }
        }

        /// <summary>四个数字标：00/06/12/18，半径按图纸 §2.2 的 r=65（标中心到圆心的距离）。</summary>
        private void BuildHourMarks()
        {
            // 用 Place 直接摆绝对位置：只有 4 个，算三角函数比"再造一套旋转坐标系"简单。
            // 【r 取 65 而不是 §3 对照表里写的 58】图纸自己有两处口径（§2.2 坐标表给 65，
            // §3 图元表注释写 58），§2.2 是逐个标了绝对坐标的那张表，以它为准。
            // 65 的实算核对：判据点落在环带内侧（环内缘 r=76 再往里 11px），
            // 不会和内盘边界或中心 28 号字（最宽约 70px、即 r≤35）相撞。
            const float R = 65f;
            string[] labels = { "00", "06", "12", "18" };
            for (int i = 0; i < 4; i++)
            {
                // 角度：0° 在 12 点，顺时针每格 90°
                double rad = i * Math.PI * 0.5;
                float cx = DialCX + (float)(Math.Sin(rad) * R);
                float cy = DialCY - (float)(Math.Cos(rad) * R);

                Text t = TwinUIFactory.Label(_card, "TWS_Mark" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleCenter, TwinTheme.TextMuted);
                TwinUIFactory.Place(t.rectTransform, cx - 9f, cy - 5.5f, 18f, 11f);
                t.text = labels[i];
                t.raycastTarget = false;
            }
        }

        private void BuildButtons()
        {
            const float y = 272f;
            const float w = 104f;
            const float h = 40f;
            const float gap = 8f;

            _buttons = new TwinButton[WeatherNames.Length];
            for (int i = 0; i < WeatherNames.Length; i++)
            {
                int idx = i;   // 闭包捕获：不走局部变量会全拿到最后一个
                TwinButton btn = TwinButton.Create(_card, WeatherNames[i],
                    TwinButtonStyle.Secondary, w, h, () => OnPickWeather(idx), "TWS_Btn" + i);
                TwinUIFactory.Place(btn.Rect, 20f + i * (w + gap), y, w, h);

                // 字号按图纸 §2.3 是 15（TwinButton 默认 Body=13），单独改一次
                Text lbl = btn.GetComponentInChildren<Text>(true);
                if (lbl != null) lbl.fontSize = TwinTheme.FsBodyStrong;

                _buttons[i] = btn;
            }
        }

        private void BuildStatus()
        {
            _statusText = TwinUIFactory.Label(_card, "TWS_Status", TwinTheme.FsCaption,
                TextAnchor.MiddleCenter, TwinTheme.TextMuted);
            TwinUIFactory.Place(_statusText.rectTransform, 20f, 320f, 440f, 16f);
            _statusText.raycastTarget = false;
        }

        /// <summary>
        /// 拖动命中区：200×200 全透明。**必须最后建**（图纸 §3 #15 B11 纪律），
        /// 否则它会被后建的按钮/状态文字压在下面，拾取不到。
        /// </summary>
        private void BuildHitArea()
        {
            Image hit = TwinUIFactory.Block(_card, "TWS_Hit", new Color(0f, 0f, 0f, 0f), true);
            TwinUIFactory.Place(hit.rectTransform, DialCX - DialD * 0.5f, DialCY - DialD * 0.5f, DialD, DialD);

            TwinDialDragArea drag = hit.gameObject.AddComponent<TwinDialDragArea>();
            drag.Owner = this;
        }

        // =================================================================
        // 拖动与刷新
        // =================================================================

        /// <summary>
        /// 面板拖动回调。由 <see cref="TwinDialDragArea"/> 转过来。
        ///
        /// 角度 → 小时的公式（图纸 §2.2a 给出的规范）：
        ///   hour = ((atan2(dx, dy) 度 + 360) % 360) / 15
        /// dx = 鼠标 x − 圆心 x，dy = 鼠标 y − 圆心 y（**Unity 的 y 向上为正**）。
        ///
        /// ⚠️ 这里最容易写错的是 dy 的符号：面板内部是"y 向下"的坐标（Place 的 y 向下），
        /// 但 atan2 用的必须是数学坐标（y 向上）。我第一版直接用了面板局部坐标的 y，
        /// 结果 12 点方向被判成 6 点，指针整个上下颠倒。
        /// 修法：local 点在 pivot(0,1) 的矩形里是**向下为负**，取反即得数学坐标。
        /// </summary>
        internal void OnDrag(Vector2 localPoint, bool commit)
        {
            _dragging = !commit;

            // 圆心在命中区局部坐标里的位置：命中区正好是 200×200、pivot 左上角，
            // 所以圆心就是 (100, −100)（局部 y 向下为负）。
            const float cx = DialD * 0.5f;
            const float cy = -DialD * 0.5f;

            float dx = localPoint.x - cx;
            float dy = localPoint.y - cy;          // 已含"向上为正"的符号

            // 抓在圆心上（dx=dy=0）时 atan2 无意义，直接忽略这次输入
            if (Mathf.Abs(dx) < 0.001f && Mathf.Abs(dy) < 0.001f) return;

            // atan2(dx, dy)：0° = 正上方（12 点），顺时针为正 —— 正是我们要的口径
            float deg = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
            if (deg < 0f) deg += 360f;

            double hour = deg / 15.0;              // 360° / 24h = 15°/h
            _hour = NormalizeHour(hour);

            // 【同帧调】视觉侧与环境侧必须同一帧改，否则下一帧会看到"指针动了、天色没动"
            TwinWeatherBridge.ApplyHour(_hour, true);

            RefreshDial();
            RefreshStatus();
        }

        /// <summary>把小时规整到 [0,24)。</summary>
        private static double NormalizeHour(double h)
        {
            double r = h % 24.0;
            if (r < 0.0) r += 24.0;
            return r;
        }

        private void OnPickWeather(int index)
        {
            // 手动选天气 = 接管控制权，自动轮换退出（否则 40 秒后被翻走，用户以为没生效）
            WeatherDirector dir = WeatherDirector.Instance;
            if (dir != null && dir.auto) dir.SetAuto(false);

            if (index == RandomIndex)
            {
                // "随机"是一个动作而不是一档状态：从视觉 7 档里随机取一个
                int pick = UnityEngine.Random.Range(0, 7);
                TwinWeatherBridge.ApplyWeather((WeatherKind)pick);
            }
            else
            {
                TwinWeatherBridge.ApplyWeather(PickWeather[index]);
            }

            RefreshButtons();
            RefreshStatus();
        }

        private void RefreshAll()
        {
            RefreshDial();
            RefreshButtons();
            RefreshStatus();
        }

        private void RefreshDial()
        {
            if (_pivot != null)
            {
                // 同样取负：z 正为逆时针，小时顺时针
                _pivot.localRotation = Quaternion.Euler(0f, 0f, -(float)(_hour * 15.0));
            }

            if (_timeText != null)
            {
                int hh = (int)Math.Floor(_hour);
                int mm = (int)Math.Round((_hour - hh) * 60.0);

                // 四舍五入到 60 分时要进位，否则 13.999 会显示 "13:60"
                if (mm >= 60) { mm -= 60; hh = (hh + 1) % 24; }

                _sb.Length = 0;
                _sb.Append(hh.ToString("00")).Append(':').Append(mm.ToString("00"));
                _timeText.text = _sb.ToString();
            }
        }

        private void RefreshButtons()
        {
            if (_buttons == null) return;

            WeatherKind cur = TwinWeatherBridge.CurrentVisual;

            // 四档里哪一格"代表"了当前视觉档。注意多对一：
            // 视觉侧是 Overcast/Storm/Fog 时，面板上没有对应格 —— 那就一格都不选中。
            // 这不是缺陷：面板只是"四个快捷入口"，不代表完整状态（完整状态在底行文字里）。
            int sel = -1;
            if (cur == WeatherKind.Clear) sel = 1;
            else if (cur == WeatherKind.Rain) sel = 2;
            else if (cur == WeatherKind.Snow) sel = 3;

            for (int i = 0; i < _buttons.Length; i++)
            {
                if (_buttons[i] != null) _buttons[i].SetSelected(i == sel);
            }
        }

        /// <summary>
        /// 底行状态：`当前：白天 · 晴　　室温 20.1°C`（图纸 §2.4）。
        ///
        /// 室温取 `TwinClimate.IndoorNowC()` —— 那是全项目唯一权威入口，
        /// 且天然随天气与时刻变化（拖表盘时它会跟着动，这正是 P13 要演示的事）。
        /// 取不到时**整段退化为不带温度**，不显示 `--.-°C`（图纸 §2.4 明确要求）。
        /// </summary>
        private void RefreshStatus()
        {
            if (_statusText == null) return;

            WeatherKind w = TwinWeatherBridge.CurrentVisual;
            double h = _dragging ? _hour : TwinClimate.HourOfDay;

            // 昼 = 06:00–18:00，夜 = 其余（图纸 §2.4 的文案规则，与表盘数字标口径一致）
            bool day = h >= 6.0 && h < 18.0;

            _sb.Length = 0;
            _sb.Append("当前：").Append(day ? "白天" : "夜间")
               .Append(" · ").Append(WeatherLabel(w));

            double indoor;
            bool ok;
            try
            {
                indoor = TwinClimate.IndoorNowC();
                ok = !double.IsNaN(indoor) && !double.IsInfinity(indoor);
            }
            catch (Exception)
            {
                indoor = 0.0;
                ok = false;
            }

            if (ok)
            {
                _sb.Append("　　室温 ").Append(indoor.ToString("0.0")).Append("°C");
            }

            _statusText.text = _sb.ToString();
        }

        /// <summary>7 档的中文名。与 EnvironmentState.WeatherLabel 同口径，但那边在 Weather 命名空间，
        /// 这里为了不让 UI 层直接依赖那个静态方法（它还需要 EnvironmentState 存在），单独给一份。</summary>
        private static string WeatherLabel(WeatherKind w)
        {
            switch (w)
            {
                case WeatherKind.Cloudy: return "多云";
                case WeatherKind.Overcast: return "阴";
                case WeatherKind.Rain: return "雨";
                case WeatherKind.Storm: return "雷雨";
                case WeatherKind.Fog: return "雾";
                case WeatherKind.Snow: return "雪";
                default: return "晴";
            }
        }

        private void Update()
        {
            // ══════════════════════════════════════════════════════════════
            // 【P13-A】Esc 关闭（图纸 §4：Esc / 点遮罩 / 点 × 三处等价）
            //
            // 为什么放在 Update 而不是给某个对象挂 EventTrigger：
            //   · 键盘输入没有"被点到的对象"，必须全局轮询；
            //   · 本组件与卡片同生命周期，卡片一销毁这段就不跑了，天然不会残留。
            //
            // 与项目里其他浮层（帮助浮层）保持同一套写法：优先用旧 Input 管理器。
            // 本项目是 Unity 2022.3 + legacy UGUI，未启用新 Input System 包，
            // 用 Input.GetKeyDown 不会抛异常。
            // ══════════════════════════════════════════════════════════════
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();      // Close 内部负责恢复真实时钟 + 回调 + 销毁
                return;
            }

            // 拖动中由 OnDrag 驱动刷新；不拖动时若真实时钟在走（或外部改了时刻），
            // 让表盘跟上。这是"打开面板后时钟仍在走"的唯一来源 ——
            // 少了这段，不拖的时候面板会定在打开那一刻不动，看起来像卡住了。
            if (_dragging) return;

            double now = NormalizeHour(TwinClimate.HourOfDay);

            // 变化超过 1 分钟才刷，避免每帧重建字符串
            if (Math.Abs(now - _hour) < (1.0 / 60.0)) return;

            _hour = now;
            RefreshDial();
            RefreshStatus();
        }
    }
}
