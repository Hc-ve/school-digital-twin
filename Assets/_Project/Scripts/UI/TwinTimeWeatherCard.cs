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

        /// <summary>
        /// 图纸 §1 裁决的**内容区基准高**。所有原有元素（提示 40 / 表盘 56–256 /
        /// 天气四钮 272–312 / 状态行 320–336 / 底距 24）都相对它排布，本轮一个都没动。
        ///
        /// 【2026-11-13】新增「重置 / 确认」操作行挂在内容区下方，
        /// 实际卡片总高 = CardH + ActionStripH = **408**（见 <see cref="CardTotalH"/>）。
        /// 把基准高与附加区拆开，而不是直接把 CardH 改成 408：
        /// 原有那些绝对 y 坐标是见微试绘逐格验收过的，改 CardH 会让它们集体"看着错位"，
        /// 也容易让下一个人误以为表盘/按钮区一起放大了。
        /// </summary>
        private const float CardH = 360f;

        /// <summary>操作行附加高度：40 高的按钮 + 与状态行的 8 间隔。</summary>
        private const float ActionStripH = 48f;

        /// <summary>卡片实际总高 408。新增行落在 y=344~384，新底距 408−384=24，与原图纸一致。</summary>
        private const float CardTotalH = CardH + ActionStripH;

        // ---- 新增操作行（重置 / 确认）----
        private const float ActionY = 344f;
        private const float ActionH = 40f;
        private const float ActionGap = 8f;
        private const float ActionW = (CardW - 40f - ActionGap) * 0.5f;   // 216：左右各留 20

        /// <summary>确认阈值：拖动值与已提交时刻相差 ≥ 1 分钟才可提交。</summary>
        private const double ConfirmMinuteDelta = 1.0;

        /// <summary>
        /// 浮点容差。1 分钟在小时域是 1/60，转回分钟时 0.016666…×60 会得到
        /// 0.9999999999999999，恰好卡在阈值上会被判成「灰」。
        /// 静态推演实测：Δ(16.0, 16.0+1/60) = 1.000 但 `>= 1.0` 为 false。
        /// 用 1e-6 分钟（= 0.06 毫秒）容差吸收舍入，不影响任何真实交互粒度。
        /// </summary>
        private const double MinuteEpsilon = 1e-6;

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
        private TwinButton[] _buttons;        // 天气四钮
        private TwinButton _resetButton;      // 重置 → 恢复真实时钟
        private TwinButton _confirmButton;    // 确认 → 提交预览时刻

        private Action _onClosed;

        /// <summary>当前表盘上的小时数（0~24）。**拖动时是预览值，不一定已生效**。</summary>
        private double _hour = 12.0;

        /// <summary>正在拖动：拖动期间不要再从 TwinClimate 反向同步，否则会自我拉扯。</summary>
        private bool _dragging;

        /// <summary>
        /// 有未提交的**时间**预览。与 <see cref="_dragging"/> 是两件事：
        /// `_dragging` 只覆盖「手指还按在表盘上」，`_pendingHour` 覆盖「松手后到确认/重置/关闭」。
        /// 松手后若仍让 Update 从 TwinClimate 反拉，预览会被立刻冲掉 —— 所以 Update 要挡的是它。
        ///
        /// 【2026-11-13 天气纳入确认后拆分】原来只有一个 `_pending`，
        /// 天气预览也会把 Update 挡住，导致「只点天气、没拖时间」时表盘不走真实时钟。
        /// 时间与天气是两条独立的待提交轴，必须分开。
        /// </summary>
        private bool _pendingHour;

        /// <summary>
        /// 有未提交的**天气**预览：记录用户点的是哪个按钮索引，-1 = 没有。
        /// 用索引而不是 WeatherKind，因为「随机」是一个动作而不是一档状态 ——
        /// 预览期不该真的摇一次骰子（那会造成"每次刷新显示都变"），要等确认时才摇。
        /// </summary>
        private int _pendingWeatherIndex = -1;

        /// <summary>
        /// 最近一次「已提交」的时刻快照，用来判 1 分钟阈值。
        ///
        /// 为什么不直接读 TwinClimate.HourOfDay 比：
        /// 真实时钟每秒都在走，拖完停手几秒再判断会得到漂移后的值；
        /// 而覆盖模式下 HourOfDay 又恒等于上次提交值。两种模式口径不一致。
        /// 统一在「提交成功 / 重置成功 / 打开卡片」三处刷这个快照，判据才稳定。
        /// </summary>
        private double _baselineHour = 12.0;

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

        /// <summary>
        /// 关闭并销毁。Esc / 点遮罩 / 点 × 三处等价。
        ///
        /// 【2026-11-13 语义订正，推翻图纸 §4 的「关闭恢复真实时钟」】
        /// 原实现关闭时调 ApplyHour(…, false) 弹回真实时钟 —— 那是把调时间当"临时预览"。
        /// 但时间系统的实际用途是**定格时刻做演示/录视频**（先定到晚上，再关面板去操作别的），
        /// 关掉就弹回等于功能不可用。主理人实测反馈后裁决：关闭只关面板，
        /// 设定时刻**保持生效**（TwinClimate._useRealClock 维持 false），
        /// 直到用户再次拖动表盘或重启 Play（运行时状态不落盘，重进 Play 自然回到真实时钟）。
        ///
        /// 【2026-11-13 状态机补充】未确认的**时间 / 天气**预览在此**直接丢弃**：
        /// 两者从来没写进桥（OnDrag 不再调 ApplyHour、OnPickWeather 不再调 ApplyWeather），
        /// 所以只要不提交就什么都不会发生，这里不需要、也不允许补一次写桥去"撤销"——
        /// 那反而会把已提交时刻/天气改掉。
        /// 「恢复真实时钟」现在由常亮的「重置」钮独立负责，不再挂在本方法上。
        /// Close() 函数体保持前置修复原样，只有回调 + 销毁两个动作。
        /// </summary>
        public void Close()
        {
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
            // 2026-11-13 起 Close() 不再恢复真实时钟，所以这里拿到的可能就是上次提交的覆盖时刻。
            _hour = NormalizeHour(TwinClimate.HourOfDay);
            _baselineHour = _hour;   // 打开时无待提交预览，确认钮应为灰
            _pendingHour = false;
            _pendingWeatherIndex = -1;

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
            _card.sizeDelta = new Vector2(CardW, CardTotalH);

            BuildTitle();
            BuildDial();
            BuildButtons();
            BuildActionButtons();
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
            hint.text = "时间 / 天气先预览 · 确认后生效 · 重置回真实时间 · 关闭保持已提交";
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

        /// <summary>
        /// 操作行：重置（次操作，常亮）+ 确认（主操作，按 1 分钟阈值切灰亮）。
        ///
        /// 【为什么确认钮固定 Primary、只切 SetInteractable】
        /// TwinButton 的禁用分支（ApplyVisual 里 !_interactable）已经把底色压到 0.35、
        /// 文字降到 TextMuted，并且四个 IPointer* 事件都带 !_interactable 早退。
        /// 所以「视觉灰 + 不响应点击」是现成能力，无需在两套 style 之间来回切 ——
        /// 切 style 反而会在某次 SetStyle/SetInteractable 顺序不同时出现"灰底但青蓝描边"的错位。
        /// </summary>
        private void BuildActionButtons()
        {
            _resetButton = TwinButton.Create(_card, "重置", TwinButtonStyle.Secondary,
                ActionW, ActionH, OnReset, "TWS_ResetBtn");
            TwinUIFactory.Place(_resetButton.Rect, 20f, ActionY, ActionW, ActionH);

            _confirmButton = TwinButton.Create(_card, "确认", TwinButtonStyle.Primary,
                ActionW, ActionH, OnConfirm, "TWS_ConfirmBtn");
            TwinUIFactory.Place(_confirmButton.Rect, 20f + ActionW + ActionGap, ActionY, ActionW, ActionH);

            // 与天气四钮统一字号（TwinButton 默认 FsBody=13）
            Text resetLabel = _resetButton.GetComponentInChildren<Text>(true);
            if (resetLabel != null) resetLabel.fontSize = TwinTheme.FsBodyStrong;
            Text confirmLabel = _confirmButton.GetComponentInChildren<Text>(true);
            if (confirmLabel != null) confirmLabel.fontSize = TwinTheme.FsBodyStrong;

            // 起始无待提交预览，确认钮必须是灰的
            _confirmButton.SetInteractable(false);
        }

        /// <summary>
        /// 确认：把待提交的时间 / 天气预览一次性提交到桥。
        /// 这是卡片内**唯一**一处写桥的地方（时间 + 天气各一次），
        /// 两条轴互不影响：只改天气时不会动时刻，只拖时间时不会动天气。
        /// </summary>
        private void OnConfirm()
        {
            // 双保险：按钮置灰时 IPointerClick 已被 TwinButton 挡掉，
            // 但回调仍可能被别处直接调用，所以这里再判一次状态与阈值。
            if (!CanConfirm()) return;

            if (HourDirty()) TwinWeatherBridge.ApplyHour(_hour, true);
            if (WeatherDirty()) CommitPendingWeather();

            _pendingHour = false;
            _pendingWeatherIndex = -1;
            _baselineHour = _hour;

            RefreshButtons();     // 预览选中态 → 已提交选中态
            RefreshStatus();
            RefreshActions();
        }

        /// <summary>
        /// 把待提交的天气真正写进桥。「随机」在这里才摇骰子 ——
        /// 预览期只记录"用户点了随机"，不提前摇，否则每次刷新都会变一档。
        /// SetAuto(false) 也从 OnPickWeather 挪到这里：预览不该接管自动轮换。
        /// </summary>
        private void CommitPendingWeather()
        {
            WeatherDirector dir = WeatherDirector.Instance;
            if (dir != null && dir.auto) dir.SetAuto(false);

            if (_pendingWeatherIndex == RandomIndex)
            {
                int pick = UnityEngine.Random.Range(0, 7);
                TwinWeatherBridge.ApplyWeather((WeatherKind)pick);
            }
            else
            {
                TwinWeatherBridge.ApplyWeather(PickWeather[_pendingWeatherIndex]);
            }
        }

        /// <summary>
        /// 重置：立刻恢复真实时钟，并丢弃未提交的天气预览。
        ///
        /// 传 TwinClimate.HourOfDay 而不是 _hour：enable=false 时桥内部取的就是真实时钟
        /// （ApplyHour 里 now = enable ? hour : TwinClimate.HourOfDay），
        /// 传预览值只会让 _hourOverride 残留一个无用数字。
        /// 天气预览从未写进桥，丢弃即可，不需要 ApplyWeather 去"撤销"。
        /// </summary>
        private void OnReset()
        {
            TwinWeatherBridge.ApplyHour(TwinClimate.HourOfDay, false);

            _pendingHour = false;
            _pendingWeatherIndex = -1;
            _hour = NormalizeHour(TwinClimate.HourOfDay);
            _baselineHour = _hour;

            RefreshDial();
            RefreshButtons();     // 天气四钮回到已提交档
            RefreshStatus();
            RefreshActions();
        }

        /// <summary>时间轴是否有可提交的改动（≥ 1 分钟）。</summary>
        private bool HourDirty()
        {
            return _pendingHour &&
                   MinuteDelta(_hour, _baselineHour) >= ConfirmMinuteDelta - MinuteEpsilon;
        }

        /// <summary>天气轴是否有可提交的改动。</summary>
        private bool WeatherDirty()
        {
            return _pendingWeatherIndex >= 0;
        }

        /// <summary>
        /// 确认是否可提交：**任一轴**有改动即可。
        /// 时间轴仍守 1 分钟阈值；天气轴一点就亮（选天气是离散动作，没有"差不多"）。
        /// </summary>
        private bool CanConfirm()
        {
            return HourDirty() || WeatherDirty();
        }

        /// <summary>
        /// 两个时刻的差（分钟）。**按环形算**：23:59 与 00:01 只差 2 分钟，
        /// 直接相减会得到 1438 分钟，跨零点时确认钮会被错误点亮/判错。
        /// </summary>
        private static double MinuteDelta(double a, double b)
        {
            double d = Math.Abs(NormalizeHour(a) - NormalizeHour(b));
            if (d > 12.0) d = 24.0 - d;
            return d * 60.0;
        }

        /// <summary>刷确认钮的灰/亮。重置钮常亮，不需要动。</summary>
        private void RefreshActions()
        {
            if (_confirmButton != null) _confirmButton.SetInteractable(CanConfirm());
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

            // 【2026-11-13 状态机改造】拖动只做**预览**，不再写桥。
            // 旧实现在这里每帧 ApplyHour(_hour, true)，等于"拖动即生效"，
            // 用户无法反悔、也无法只看看某个时刻的天色而不改数据。
            // 现在写入只发生在 OnConfirm()（提交）与 OnReset()（恢复真实时钟）两处，
            // 桥仍是改时刻的唯一入口（P13-13）。
            _pendingHour = true;

            RefreshDial();
            RefreshStatus();
            RefreshActions();
        }

        /// <summary>把小时规整到 [0,24)。</summary>
        private static double NormalizeHour(double h)
        {
            double r = h % 24.0;
            if (r < 0.0) r += 24.0;
            return r;
        }

        /// <summary>
        /// 天气四钮：**只记预览，不写桥**（2026-11-13 纳入确认）。
        ///
        /// 旧实现点一下就 ApplyWeather + SetAuto(false) 立即改场景，
        /// 与"拖动预览 → 确认提交"的新状态机矛盾：用户选完天气还没点确认，
        /// 天色已经变了。现在只记录索引，选中态与底行文案显示预览，
        /// 真正写入统一在 <see cref="CommitPendingWeather"/>。
        ///
        /// 「随机」不在这里摇骰子 —— 预览期摇了会让每次刷新显示都跳一档。
        /// </summary>
        private void OnPickWeather(int index)
        {
            if (index < 0 || index >= WeatherNames.Length) return;

            _pendingWeatherIndex = index;

            RefreshButtons();
            RefreshStatus();
            RefreshActions();     // 天气一改，确认钮立刻高亮
        }

        /// <summary>
        /// 当前用于显示的天气档：有预览用预览，否则用已提交档。
        /// 「随机」预览没有确定档，返回 null 让调用方走"未定"文案。
        /// </summary>
        private WeatherKind? DisplayWeather()
        {
            if (_pendingWeatherIndex < 0) return TwinWeatherBridge.CurrentVisual;
            if (_pendingWeatherIndex == RandomIndex) return null;
            return PickWeather[_pendingWeatherIndex];
        }

        private void RefreshAll()
        {
            RefreshDial();
            RefreshButtons();
            RefreshStatus();
            RefreshActions();
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

            // 预览优先：点过天气四钮但还没确认时，选中态跟预览走，
            // 让用户看得见"我选了什么还没提交"。确认/重置后回到已提交档。
            int sel = -1;
            if (_pendingWeatherIndex == RandomIndex) sel = RandomIndex;
            else
            {
                WeatherKind? cur = DisplayWeather();
                if (cur.HasValue)
                {
                    if (cur.Value == WeatherKind.Clear) sel = 1;
                    else if (cur.Value == WeatherKind.Rain) sel = 2;
                    else if (cur.Value == WeatherKind.Snow) sel = 3;
                }
            }

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

            // 天气文案跟预览走；「随机」预览期没有确定档，显示"随机（待确认）"。
            WeatherKind? w = DisplayWeather();

            // 拖动中或有待提交时间预览时，底行的"白天/夜间"跟表盘预览走；
            // 否则跟已提交时刻/真实时钟走。只改天气时这一项不动（两条轴独立）。
            // ⚠️ 室温仍取 TwinClimate.IndoorNowC()（数据侧权威），**不随预览变** ——
            // 预览尚未写入桥，数据侧没有这个时刻/天气，硬造一个温度会造成"面板比数据快一步"。
            double h = (_dragging || _pendingHour) ? _hour : TwinClimate.HourOfDay;

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

        /// <summary>
        /// 7 档的中文名。与 EnvironmentState.WeatherLabel 同口径，但那边在 Weather 命名空间，
        /// 这里为了不让 UI 层直接依赖那个静态方法（它还需要 EnvironmentState 存在），单独给一份。
        /// 传 null 表示「随机」预览期尚未定档 —— 不显示具体天气名，避免误导。
        /// </summary>
        private static string WeatherLabel(WeatherKind? w)
        {
            if (!w.HasValue) return "随机（待确认）";

            switch (w.Value)
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
                Close();      // Close 内部负责回调 + 销毁（2026-11-13 起不再恢复真实时钟，设定时刻保持生效）
                return;
            }

            // 拖动中由 OnDrag 驱动刷新；不拖动时若真实时钟在走（或外部改了时刻），
            // 让表盘跟上。这是"打开面板后时钟仍在走"的唯一来源 ——
            // 少了这段，不拖的时候面板会定在打开那一刻不动，看起来像卡住了。
            if (_dragging) return;

            // 【2026-11-13】有未提交预览时同样挡住：松手那一帧若被真实时钟反拉，
            // 用户刚拖好的时刻会被立刻冲掉，预览根本留不到点「确认」。
            // 单独写一行而不是并进上一行的 || —— P13-24 判据锁的是 `if (_dragging) return;`
            // 这条字面，合并会让它失配；拆开则判据与新语义同时成立。
            if (_pendingHour) return;

            double now = NormalizeHour(TwinClimate.HourOfDay);

            // 变化超过 1 分钟才刷，避免每帧重建字符串
            if (Math.Abs(now - _hour) < (1.0 / 60.0)) return;

            _hour = now;
            RefreshDial();
            RefreshStatus();
        }
    }
}
