using System.Collections.Generic;
using System.Text;
using SchoolTwin.Core;
using SchoolTwin.Twin;
using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P3-B】单台设备详情页 —— **页栈里比「校园管理系统」更深一层**的独立页面。
    ///
    /// ── 为什么是独立页面，不是面板里的浮层 ────────────────────────────
    /// 主理人 2026-10-31 三次反馈逐条收敛到现在的形态：
    ///   ① 浮层"看起来很别扭"（有圆角边框、浮在面板上、下半截空着）；
    ///   ② 「页面层级不一样，不要所有功能都套在同一页面层级上」——
    ///      详情是"从列表钻进去"的下一层，走页栈 Push/Pop；
    ///   ③ 「没原来面板信息丰富全面」—— 所以本页的信息量对齐浮卡：
    ///      中文指标名 / 单位 / 彩色量程条 / 量程·正常·Δ 说明 / 60s 趋势线 /
    ///      数据新鲜度 / 告警与异常列表，一项不少。
    ///
    /// ── 尺寸为什么和原面板一致 ────────────────────────────────────
    /// 继承 `TwinPageBase`，页栈 `Push` 时 `Stretch` 铺满 `_content`，
    /// 与 `TwinSystemPage` 同一个父容器、同一套 640 宽坐标系（内容区 x=184 / w=440）。
    ///
    /// ── 【2026-11-03 P11 双态统一】本页现在**服务两种宿主** ─────────────
    ///
    /// ── 问题（主理人截图）──────────────────────────────────────────
    /// 全屏态点设备行，右栏切出来的「设备详情」跟本页**完全不是一套东西**：
    ///   全屏右栏：只有 4 行纯文字（位置 / 类型 / 指标数 / 数据更新），宽 168
    ///   本页（窄态）：量程条 + 阈值带 + 60s 趋势线 + 告警与异常列表，宽 440
    /// 主理人原话：「设备详情全屏和窄屏展示效果还不一样啊」。
    ///
    /// ── 修法：全屏也进本页，而不是在右栏另画一套 ─────────────────
    /// 全屏点行 → 本页被 Push 到 L2 的「满宽态」：左轨 200 保留（导航不能丢），
    /// 中栏 600 + 右栏 200 = 800，Pad 16 → 可用 **768**。
    /// 这和统计页进满宽是**同一套几何**（`TwinFullLayer.Pages.cs` 的 `SW`），
    /// 所以两页在同一位置切换时边界不跳。
    ///
    /// 于是本页的坐标**不能写死**，必须按宿主宽度算 —— 见 `ApplyHostGeometry()`。
    /// 写死的后果就是本类第一版：全屏下整页挤在 184~624 那一小条里，右半截空着。
    ///
    /// ── 哪种形态？判据只有一个 ──────────────────────────────────
    /// `TwinDrawer.IsFullScreen`。它已经是公开静态属性（小地图也读它），
    /// 不新增标志位 —— 多一个标志就多一处"两态不同步"的可能。
    /// </summary>
    [AddComponentMenu("SchoolTwin/设备详情页 (TwinDeviceDetailPage)")]
    public sealed class TwinDeviceDetailPage : TwinPageBase
    {
        public override string Title { get { return "设备详情"; } }

        // ---- 窄态几何（640 宽面板，与 TwinSystemPage 同一套坐标）----
        private const float NarrowX = 184f;
        private const float NarrowW = 440f;
        private const float NarrowBottom = 506f;

        // ---- 全屏满宽几何 ----
        //
        // ── 为什么不是「保留左轨 200、内容从 216 起」────────────────────
        // 第一版这么设计的，理由是"导航不能丢"。想清楚之后否掉了，三个问题：
        //   ① **语义上就站不住**：左轨 5 个 Tab 是「校园管理系统」的页内导航，
        //      而本页是被 Push 到它上面的**下一层**。详情页开着时点 Tab，
        //      到底是"切页"还是"换一台设备"？页栈答不出来 —— 只会造出一个
        //      "栈顶是详情页、底下却换成了电力管理"的矛盾态。
        //   ② **保留左轨也没人点得动**：全屏层此时是关的（`ApplyFullMode` 第二态），
        //      要让左轨可见就得再开一层，两层的裁剪/射线又得重新对齐一遍。
        //   ③ **窄态本来就不是这样**：窄态详情页是全宽的 640，头部返回键导航。
        //      两态要一致，全屏就该同样全宽 —— 这才叫"一套东西"。
        //
        // 所以：全屏层整层退场，本页独占 `_content`（它已经让开了头带 80 / 底带 60），
        // 用抽屉自己的头部（标题「设备详情」+ 返回箭头）导航，与窄态一模一样。
        //
        // 几何：`_content` 全屏时横跨 0…1000，左右各留 Pad 16 → 968 宽。
        // 为什么留 16 而不是铺满 1000：铺满会让标题、量程条贴到屏幕物理边缘，
        // 与统计页那套 16 的收边不一致，两页切换时看着像"少了一圈内边距"。
        private const float FullX = TwinFullLayer.Pad;                              // 16
        private const float FullW = TwinDrawer.FullW - TwinFullLayer.Pad * 2f;      // 968

        /// <summary>
        /// 全屏内容区可用下沿（**本页局部 y**，不是 `_root` 的 y）。
        ///
        /// ── 这里第一版算错过一次，记下来 ──────────────────────────────
        /// 第一版写 648 = 720 − 底带 60 − 12 缓冲。看着对，其实**坐标系错了**：
        /// 本页活在 `_content` 里，而 `_content` 的矩形是全屏 720 − 头带 80 − 底带 60 = **580**，
        /// 它的左上角落在 `_root` 的 y=80。所以本页的 y 是从"头带下沿"起算的，
        /// 能用的下沿是 580 − 12 = **568**，不是 648。
        ///
        /// 写 648 的后果：内容会排出 `_content` 下边界 80px —— 告警区直接压到底带上，
        /// 而且 `_content` 挂了 RectMask2D，多出来的部分会被裁掉，观感是"告警列表被切了半截"。
        ///
        /// 统计页的 `SRow2Y 424 + 196 = 620` 是 `_root` 坐标，换算到本页是 540 —— 落在 568 以内，
        /// 所以两个页面的下边界是一致的，没有各算一套。
        /// </summary>
        private const float FullBottom = 568f;

        // ---- 随宿主形态变化的几何（`ApplyHostGeometry()` 写入，Build 与 Rebuild 都读它）----
        private float _x = NarrowX;
        private float _w = NarrowW;
        private float _bottom = NarrowBottom;
        private bool _full;

        private const float BackY = 8f;
        private const float CodeY = 44f;
        private const float ChipY = 86f;
        private const float InfoY = 114f;
        private const float FreshY = 140f;
        private const float DividerY = 160f;

        /// <summary>趋势线采样间隔（秒）。5Hz —— 与浮卡一致。</summary>
        private const float SparkSampleInterval = 0.2f;

        /// <summary>指标块的最小 / 最大高度。块高随数量自适应，见 RebuildMetrics。</summary>
        private const float MetricMinH = 70f;
        private const float MetricMaxH = 210f;

        /// <summary>
        /// 全屏下指标块的高度上限放宽到 230。
        ///
        /// 【为什么两态上限不同】窄态可用高 346（160→506），2 个指标时块高 173，
        /// 趋势线上限 60 才不显空。全屏可用高 408（160→568），2 个指标时块高 204，
        /// 趋势线可以给到 120 —— 块高上限不跟着放宽，趋势线就撑不满，
        /// 下半页会像"没加载完"。
        /// 这是**同一套排版在两种高度下的自然结果**，不是两套设计。
        /// </summary>
        private const float MetricMaxHFull = 230f;

        /// <summary>趋势线高度上限。<see cref="MetricMaxHFull"/> 的同理放宽。</summary>
        private const float SparkMaxHFloat = 60f;
        private const float SparkMaxHFull = 120f;

        private TwinAnchor _anchor;
        private bool _built;

        private Text _code;
        private TwinRoundedRect _chip;
        private Text _chipText;
        private Text _pos;
        private Text _type;
        private Text _power;
        private Text _fresh;
        private Text _alarmTitle;
        private Text _alarmEmpty;

        private readonly List<MetricRow> _rows = new List<MetricRow>(4);
        private readonly List<AlarmRow> _alarms = new List<AlarmRow>(4);
        private readonly List<TwinAlarmHistory.AlarmRecord> _alarmBuf =
            new List<TwinAlarmHistory.AlarmRecord>(32);

        private readonly StringBuilder _sb = new StringBuilder(64);
        private float _nextRefresh;

        /// <summary>一个指标块：名称 + 值 + 量程条（阈值带）+ 说明 + 趋势线。</summary>
        private sealed class MetricRow
        {
            public RectTransform Root;
            public Text Name;
            public Text Value;
            public Text Hint;
            public Image Track;
            public Image Marker;
            public readonly Image[] Bands = new Image[5];
            public TwinSparkline Spark;

            public float LastPushTime;
            public string MetricName = "";
            public string Unit = "";
            public float Min, Max, WarnLow, WarnHigh, AlarmLow, AlarmHigh;
        }

        /// <summary>一条告警：色条 + 描述 + 时长。</summary>
        private sealed class AlarmRow
        {
            public RectTransform Root;
            public Image Bar;
            public Text Desc;
            public Text Age;
        }

        // ================================================================ 对外

        public void Bind(TwinAnchor anchor)
        {
            _anchor = anchor;
            if (_built) RefreshAll();
        }

        public override void OnShow()
        {
            // 【必须先量形态再决定 Build 还是重排】
            // `_built` 为真时只重排，但如果宿主形态变了（窄→全屏），
            // 不重排的话整页会停在旧坐标上 —— 那正是"全屏下内容挤在左半边"的成因。
            bool wasFull = _full;
            ApplyHostGeometry();

            if (!_built) Build();
            else if (wasFull != _full) RebuildForHost();

            // ══════════════════════════════════════════════════════════════
            // 【2026-11-03 P11 修·详情页偶尔只剩骨架】
            //
            // ── 症状（主理人截图）───────────────────────────────────────
            // 有时候点设备详情，画面上只有：返回键、三列标签（位置/类型/电力，
            // 值却是空的）、分割线、底部工具行 —— 没有全码、没有状态胶囊、
            // 没有指标块、没有趋势线、没有告警。时好时坏。
            //
            // ── 根因：`RefreshAll()` 只挂在三条路径的**一条**上 ──────────
            // 上一版写的是：
            //     if (!_built) Build();
            //     else if (wasFull != _full) RebuildForHost();
            //     else RefreshAll();          ← 只有 else 这一条刷
            //
            // 而 `Build()` **只建骨架**：返回键 / 全码 Text / 胶囊 / 三列 / 分割线。
            // 指标块、告警区、所有文字内容**全是 `RefreshAll()` 里建的**
            // （`RefreshAll` → `RebuildMetrics` → `BuildMetricRow`）。
            //
            // 于是两条路径都产出空骨架：
            //   ① 首次打开（`!_built` → `Build()`）→ 只有骨架，直到 0.1s 后
            //      `Update()` 的定时刷新跑 `RefreshValues()` —— 但 `RefreshValues`
            //      只改**已存在**的 `_rows` 里的文字，不会建块。所以块永远不出现。
            //   ② 形态切换（`RebuildForHost()` → 内部 `Build()`）→ 同上。
            //
            // 为什么"有时候正常"：这页是**复用实例**（`OpenDeviceDetail` 里
            // `GetComponentInChildren`），第一次 `Build()` 之后 `_built = true`。
            // 只要之前进过一次、走的都是 `else RefreshAll()` 分支，后面就都正常；
            // 而"第一次进 / 刚切过形态"这两种情况必然空 —— 所以看起来像随机。
            //
            // ── 修法：`RefreshAll()` 提到 if 外面，三条路径都刷 ─────────────
            // 它是幂等的（只读 `_anchor` 重算 DOM），多调一次没有副作用。
            // 这样"建骨架"和"填内容"就总是成对发生，不再依赖调用者记得刷。
            //
            // ⚠️ 与 P10/P11 前面几次事故同型：**同一件事有两个出口，只改了一个**。
            //    `Build()` 建一半、`RefreshAll()` 建另一半，却只有一个出口负责配对 ——
            //    这种"半成品构造函数"本身就是隐患，这里至少先把配对补齐；
            //    真正的收口应该是 `Build() + RefreshAll()` 合成一个入口，
            //    但那要动 `RebuildForHost` 与 `Bind` 的语义，留到下一次重构。
            // ══════════════════════════════════════════════════════════════
            RefreshAll();
        }

        /// <summary>
        /// 读宿主形态并写进 `_x / _w / _bottom / _full` —— **本页唯一量尺寸的地方**。
        ///
        /// 【为什么不读 transform.rect.width】本页 `Stretch` 铺满 `_content`，
        /// 而 `_content` 在**全屏态是被关闭的**（`ApplyFullMode` 里
        /// `_content.SetActive(!_maximized)`）。关着的 RectTransform 尺寸是 0，
        /// 拿它算宽度会得到一屏挤在左上角的东西。所以必须问 `TwinDrawer.IsFullScreen`
        /// 这个**公开的语义开关**，而不是去量一个可能没布局的矩形。
        /// </summary>
        private void ApplyHostGeometry()
        {
            _full = TwinDrawer.IsFullScreen;
            if (_full)
            {
                _x = FullX;
                _w = FullW;
                _bottom = FullBottom;
            }
            else
            {
                _x = NarrowX;
                _w = NarrowW;
                _bottom = NarrowBottom;
            }
        }

        /// <summary>
        /// 宿主形态变了 → 整页按新宽度重排。
        ///
        /// 【为什么要重建而不是只挪位置】本页的排版不是"几个绝对坐标"，而是
        /// 「块高按可用高均分 → 趋势线高按块高算 → 阈值带按块宽百分比算」这一串**级联计算**。
        /// 宽度一改，阈值带的 x/w、趋势线的采样密度、信息三列的 dx 全都要重算。
        /// 逐项打补丁必然漏，直接重建最可靠 —— 代价只是几个 GameObject。
        ///
        /// 返回键这类"有状态"的东西不受影响：本页没有输入框、没有滚动位置、没有展开态，
        /// 唯一的状态是 `_anchor`（选中设备），而它存在字段上，不随重建丢失。
        /// </summary>
        private void RebuildForHost()
        {
            RectTransform root = transform as RectTransform;
            if (root == null) return;

            // 把已建的子物体全部拆掉（跳过自己）
            //
            // ⚠️ 必须先 `SetActive(false)` 再 Destroy：运行时 `Object.Destroy` 是**延迟到帧尾**
            // 真正删除的，`DestroySafe` 用的就是它。只 Destroy 不关，旧节点会在本帧剩下的
            // 时间里继续参与渲染 —— 而 `Build()` 紧接着就建了新节点，
            // 于是会看到新旧两套文字在同一位置叠一帧（切形态时闪一下）。
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                GameObject child = root.GetChild(i).gameObject;
                child.SetActive(false);
                TwinUIFactory.DestroySafe(child);
            }

            _rows.Clear();
            _alarms.Clear();
            _alarmTitle = null;
            _alarmEmpty = null;
            _code = null;
            _chip = null;
            _chipText = null;
            _pos = null;
            _type = null;
            _power = null;
            _fresh = null;

            _built = false;

            // 【P11 补】`Build()` 只建骨架，内容必须紧跟一次 `RefreshAll()`。
            //
            // 外层 `OnShow()` 现在也会刷一次（幂等），这里再补一次是为了
            // **让这个方法自身完整** —— 以后谁单独调 `RebuildForHost()`，
            // 都不会拿到一张空骨架。空骨架这个坑刚踩过一次（见 OnShow 的长注释），
            // 根因就是"建骨架"和"填内容"分属两个方法却只在一个出口配对。
            Build();
            RefreshAll();
        }

        // ================================================================ 构建

        private void Build()
        {
            _built = true;

            RectTransform root = transform as RectTransform;
            if (root == null) return;

            // ---- 返回按钮 ----
            TwinRoundedRect back = TwinUIFactory.Rounded(root, "Back", TwinTheme.R8,
                TwinTheme.Track, TwinTheme.Track, TwinTheme.Border,
                TwinTheme.StrokeHair.Width, true);
            TwinUIFactory.Place(back.rectTransform, _x, BackY, 104f, 28f);

            Text backT = TwinUIFactory.Label(back.rectTransform, "T", TwinTheme.FsCaption,
                TextAnchor.MiddleCenter, TwinTheme.TextSecondary);
            TwinUIFactory.Stretch(backT.rectTransform);
            backT.text = "← 返回列表";

            Button bb = back.gameObject.AddComponent<Button>();
            bb.transition = Selectable.Transition.None;
            bb.onClick.AddListener(OnBackClicked);

            // ---- 全码 ----
            _code = TwinUIFactory.Label(root, "Code", TwinTheme.FsBodyStrong,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_code.rectTransform, _x, CodeY, _w, 20f);
            _code.horizontalOverflow = HorizontalWrapMode.Wrap;
            _code.verticalOverflow = VerticalWrapMode.Overflow;

            // ---- 状态胶囊 ----
            _chip = TwinUIFactory.Rounded(root, "Chip", 11f,
                TwinTheme.Track, TwinTheme.Track, Color.clear, 0f, false);
            TwinUIFactory.Place(_chip.rectTransform, _x, ChipY, 76f, 22f);

            _chipText = TwinUIFactory.Label(_chip.rectTransform, "T", TwinTheme.FsMicro,
                TextAnchor.MiddleCenter, TwinTheme.TextSecondary);
            TwinUIFactory.Stretch(_chipText.rectTransform);
            _chipText.raycastTarget = false;

            // ---- 位置 / 类型 / 电力：三列一行，比三行省 44px 留给告警区 ----
            // 【P11】列间距按内容宽均分，不写死 150/300。
            // 窄态 440 → 每列 146（和原来一致）；全屏 768 → 每列 256，
            // 三列铺满整行。写死 150 的话全屏下三列会挤在左边 450px 里，
            // 右边空 300px —— 那正是"全屏和窄屏不一样"的观感来源之一。
            float colStep = _w / 3f;
            _pos = InfoCell(root, "位置", 0f, colStep);
            _type = InfoCell(root, "类型", colStep, colStep);
            _power = InfoCell(root, "电力", colStep * 2f, colStep);

            // ---- 数据新鲜度 ----
            _fresh = TwinUIFactory.Label(root, "Fresh", TwinTheme.FsMicro,
                TextAnchor.MiddleLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_fresh.rectTransform, _x, FreshY, _w, 14f);

            Image line = TwinUIFactory.Block(root, "Line", TwinTheme.Divider, false);
            TwinUIFactory.Place(line.rectTransform, _x, DividerY, _w, 1f);
        }

        /// <summary>
        /// 「标签 值」横向一格。三列并排省掉两行高度。
        /// <paramref name="cellW"/> 由 `_w/3` 算出（窄态 146 / 全屏 256），
        /// 值文字宽度跟着 cellW 走，不写死 106 —— 全屏下 106 会把
        /// 「1号教学楼2F203办公室」这类长地点截掉一半。
        /// </summary>
        private Text InfoCell(RectTransform parent, string label, float dx, float cellW)
        {
            Text l = TwinUIFactory.Label(parent, "L_" + label, TwinTheme.FsMicro,
                TextAnchor.MiddleLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(l.rectTransform, _x + dx, InfoY, 36f, 18f);
            l.text = label;

            Text v = TwinUIFactory.Label(parent, "V_" + label, TwinTheme.FsCaption,
                TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
            // 留 44 = 标签 36 + 8 间隙；再留 16 给列间距，避免相邻两列文字顶上
            TwinUIFactory.Place(v.rectTransform, _x + dx + 38f, InfoY,
                Mathf.Max(60f, cellW - 54f), 18f);
            return v;
        }

        // ================================================================ 数据

        private void OnBackClicked()
        {
            TwinDrawer.PopPage();
        }

        private void Update()
        {
            if (!_built || _anchor == null) return;

            // 【P11】形态自检 —— 本页是"按宿主宽度排版"的，而宿主形态**不一定伴随 OnShow**。
            //
            // 反例（这次差点漏掉）：详情页开着时点右上角「□/❐」最大化。
            // `ToggleMaximize` 只改 `_maximized` + `ApplyFullMode()`，**页栈没变**
            // → `OnStackChanged` 不触发 → `OnShow` 不执行 → `_x/_w` 还是 440，
            // 于是全屏的画面里详情页仍挤在窄栏那一条上，右边一大片空。
            //
            // 所以把形态比较放到每帧（只比一个 bool，代价为 0）：
            // `ApplyHostGeometry` 只在真变了的时候才重建整页，稳定态下什么都不做。
            if (TwinDrawer.IsFullScreen != _full)
            {
                OnShow();   // 内部会 ApplyHostGeometry + RebuildForHost
                return;
            }

            // 10Hz —— 与浮卡同频。0.5s 太慢，趋势线的当前点会一跳一跳。
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.1f;

            RefreshValues();
        }

        private void RefreshAll()
        {
            if (_anchor == null || _code == null) return;

            _code.text = TwinCode.Full(_anchor.TwinId);

            RefreshChip();

            TwinRoomInfo info = TwinSiteLayout.Find(_anchor.Zone);
            SetText(_pos, info != null ? info.RoomName : TwinCode.Room(_anchor.Zone));
            SetText(_type, TwinCode.TypeName(_anchor.Category));

            DeviceAvailability avail = TwinCircuit.AvailabilityOf(_anchor.TwinId);
            SetText(_power, TwinCircuit.AvailabilityLabel(avail));
            _power.color = TwinCircuit.AvailabilityColor(avail);

            RebuildMetrics();
            RefreshAlarms();
            RefreshValues();
        }

        /// <summary>
        /// 只刷状态胶囊（文字 + 底色 + 边框）。10Hz 调用，代价是三次颜色赋值 + 一次文本比对。
        ///
        /// ── 为什么必须单独抽出来（P27，主理人 2026-11-03 截图）───────────
        /// 症状：详情页胶囊红着写「告警」，可下面温度 26.39°C（正常 16~28）、
        /// 湿度 36.73 %RH（正常 30~65）**两个指标都在正常区**。
        ///
        /// 根因：胶囊原来只在 `RefreshAll()` 里刷，而 `RefreshAll()` 只在
        /// **`OnShow()` 里调一次**。后续 0.1s 的定时刷新走 `RefreshValues()`，
        /// 它只改 `_rows` 里的数值/趋势线/游标，**不碰胶囊**。
        /// 于是胶囊冻结在"打开这一页那一刻"的状态上 ——
        /// 打开时正好在越界（比如某一瞬温度冲到 32°C），之后数据恢复正常，
        /// 胶囊还红着，一直到退出重进才更新。
        ///
        /// 为什么"打开时正好越界"很常见：`MockSignalGenerator` 每 45 秒制造一次
        /// 短促越界（深度 0.95），点进详情页的那一刻有约 13% 概率落在越界窗口里。
        ///
        /// ⚠️ 与浮卡 `TwinDetailPanel.RefreshChip` 同一类问题、同一套修法：
        ///    那个方法也是从 `RefreshValues` 里每周期调一次。**两态必须同步**
        ///    （见项目约定：全屏与窄态本质是一个面板）。
        /// </summary>
        private void RefreshChip()
        {
            if (_chip == null || _anchor == null) return;

            TwinStatus s = _anchor.StatusOfWorst();
            Color sc = TwinStatusPalette.Of(s);

            SetText(_chipText, TwinStatusPalette.Label(s));

            // 文字色 = 状态色，但底色太暗时改用主文字色。
            // Offline / Unknown / Alarm 这几个状态色感知亮度都不到 0.45，
            // 压在 20% 同色底的胶囊上几乎看不见 —— 「离线」看不清等于这个状态白设了。
            //
            // ⚠️ 用 `TwinStatusPalette.TextPrimary` 而不是 `TwinTheme.TextPrimary`：
            // 两者当前数值相同（0.94, 0.96, 0.99），但浮卡那边用的是前者 ——
            // 同一处判定读两个不同的来源，将来谁改了一个就会两态不一致。
            // 胶囊文字色统一以 `TwinStatusPalette` 为准。
            float lum = 0.299f * sc.r + 0.587f * sc.g + 0.114f * sc.b;
            _chipText.color = lum < 0.45f ? TwinStatusPalette.TextPrimary : sc;

            _chip.SetFill(TwinStatusPalette.Of(s, 0.20f), TwinStatusPalette.Of(s, 0.20f));
            _chip.SetBorder(TwinStatusPalette.Of(s, 0.65f), 1f);
        }

        /// <summary>
        /// 重建指标块 + 告警区。**块高随指标数自适应，不是死板的固定 3 块。**
        ///
        /// 主理人 2026-10-31：「每个设备对应的详情页面都不一样，不要死板」。
        /// 场景里设备的指标数差得很远：烟感 1 项、门 1 项、暖气 2 项、电脑 2 项。
        /// 固定 3 块会让"1 项的设备"下面空出两大块，看着像加载失败。
        ///
        /// 现在的规则：
        ///   · 先把告警区要占的高度扣掉（有告警才占）；
        ///   · 剩下的全给指标块，按数量均分，夹在 70~210 之间；
        ///   · 块越高，趋势线越高（上限 60，再高就是空白，不如留给别的）。
        ///
        /// 结果：烟感那种单指标设备 → 一块大波形；电脑那种 2 指标 → 两块中等；
        /// 指标多的设备 → 块自动压缩，不会挤出内容区。
        /// </summary>
        private void RebuildMetrics()
        {
            RectTransform root = transform as RectTransform;
            if (root == null) return;

            // 先关再删：`Object.Destroy` 延迟到帧尾，不关会和下面新建的块叠一帧
            // （换设备时指标块会重排，短暂的重影看着像"闪了一下"）。
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Root == null) continue;
                _rows[i].Root.gameObject.SetActive(false);
                TwinUIFactory.DestroySafe(_rows[i].Root.gameObject);
            }
            _rows.Clear();

            // ---- ① 先数指标 ----
            int metricCount = 0;
            if (_anchor.Values != null)
                foreach (KeyValuePair<string, TwinValue> kv in _anchor.Values)
                    metricCount++;

            // ---- ② 再数告警（决定告警区要占多少）----
            int alarmCount = CountAlarms();
            int alarmRows = alarmCount > 0 ? Mathf.Min(alarmCount, 4) : 0;
            float alarmH = alarmRows > 0 ? 22f + alarmRows * AlarmRowH : 0f;

            // ---- ③ 剩下的全给指标块 ----
            //
            // 【P11】上限按宿主形态取：窄态 210 / 全屏 230。
            // 见 `MetricMaxHFull` 的注释 —— 同一套排版在两个可用高度下的自然结果。
            float metricTop = DividerY + 10f;
            float metricArea = Mathf.Max(0f, _bottom - metricTop - alarmH);
            float maxH = _full ? MetricMaxHFull : MetricMaxH;
            float metricH = metricCount > 0
                ? Mathf.Clamp(metricArea / metricCount, MetricMinH, maxH)
                : MetricMinH;

            // ---- ③b 【P11】指标块少时，把余量还给告警区（全屏才做）----
            //
            // 问题：`metricH` 被封顶在 `maxH`，1~2 个指标的设备（烟感、门、暖气）
            // 算出来 204、但窄态只给 173 —— 差额就这么空在告警区下面。
            // 全屏可用高 408，1 指标设备的块高 230，下方会留 178px 空档，
            // 观感是"页面没加载完"。
            //
            // 做法：全屏时把没用掉的余量**加进告警区高度**，让告警行从 28 变高到 44 ——
            // 行距更舒展、能读全「时间 · 状态 · 设备名」，而且下边界刚好贴到 568。
            // 这是"把空白变成信息"而不是"加个占位框"，与主理人一贯的要求一致。
            float extraH = 0f;
            if (_full && metricCount > 0)
                extraH = Mathf.Max(0f, metricArea - metricCount * metricH);

            if (metricCount > 0)
            {
                TwinRuntime runtime = TwinRuntime.Instance;
                TwinCatalog catalog = runtime != null ? runtime.Catalog : null;
                TwinPointDefinition point = catalog != null ? catalog.Find(_anchor.TwinId) : null;

                int n = 0;
                foreach (KeyValuePair<string, TwinValue> kv in _anchor.Values)
                {
                    BuildMetricRow(root, kv.Key, point, n, metricTop + n * metricH, metricH);
                    n++;
                }
            }

            // ---- ④ 告警区紧跟在指标块下面 ----
            float alarmY = metricTop + metricCount * metricH + 6f;
            BuildAlarmArea(root, alarmY, alarmRows, extraH);
        }

        /// <summary>这台设备有几条告警（含已恢复）。</summary>
        private int CountAlarms()
        {
            _alarmBuf.Clear();
            TwinAlarmHistory h = TwinAlarmHistory.Instance;
            if (h == null) return 0;
            h.GetRecent(_alarmBuf, 64);

            int n = 0;
            for (int i = 0; i < _alarmBuf.Count; i++)
                if (string.Equals(_alarmBuf[i].TwinId, _anchor.TwinId, System.StringComparison.Ordinal))
                    n++;
            return n;
        }

        private void BuildMetricRow(RectTransform root, string metric, TwinPointDefinition point,
                                    int index, float y, float h)
        {
            MetricRow r = new MetricRow();
            r.MetricName = metric;
            r.Min = 0f; r.Max = 100f;
            r.WarnLow = 0f; r.WarnHigh = 100f;
            r.AlarmLow = 0f; r.AlarmHigh = 100f;

            TwinMetricDefinition def = point != null ? point.FindMetric(metric) : null;
            if (def != null)
            {
                r.Unit = def.Unit;
                r.Min = def.Min; r.Max = def.Max;
                r.WarnLow = def.WarnLow; r.WarnHigh = def.WarnHigh;
                r.AlarmLow = def.AlarmLow; r.AlarmHigh = def.AlarmHigh;
            }

            GameObject go = TwinUIFactory.Node("M_" + index, root);
            r.Root = (RectTransform)go.transform;
            TwinUIFactory.Place(r.Root, _x, y, _w, h - 6f);

            // 名称
            r.Name = TwinUIFactory.Label(r.Root, "Name", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(r.Name.rectTransform, 0f, 0f, _w - 140f, 18f);
            r.Name.text = TwinCode.MetricName(metric);

            // 值（含单位）
            r.Value = TwinUIFactory.Label(r.Root, "Value", TwinTheme.FsBodyStrong,
                TextAnchor.UpperRight, TwinTheme.TextPrimary);
            TwinUIFactory.Place(r.Value.rectTransform, _w - 180f, 0f, 180f, 20f);

            // 量程条：底轨 + 阈值带 + 游标
            r.Track = TwinUIFactory.Block(r.Root, "Track", TwinTheme.Track, false);
            TwinUIFactory.Place(r.Track.rectTransform, 0f, 24f, _w, 6f);

            for (int b = 0; b < r.Bands.Length; b++)
            {
                r.Bands[b] = TwinUIFactory.Block(r.Root, "Band" + b,
                    TwinTheme.StatusNormal, false);
                r.Bands[b].gameObject.SetActive(false);
            }

            r.Marker = TwinUIFactory.Block(r.Root, "Marker", TwinTheme.TextPrimary, false);
            TwinUIFactory.Place(r.Marker.rectTransform, 0f, 22f, 2f, 10f);

            // 说明：量程 / 正常范围
            r.Hint = TwinUIFactory.Label(r.Root, "Hint", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(r.Hint.rectTransform, 0f, 32f, _w, 12f);

            // 趋势线：**高度随块高走**，上限按形态取（窄态 60 / 全屏 120）——
            // 指标少的设备波形就大，这是"不死板"最直观的一处。
            // 全屏给到 120 的理由：可用高多出 62（506→568），
            // 若仍封顶 60，2 指标设备的下半块会显空。
            float sparkMaxH = _full ? SparkMaxHFull : SparkMaxHFloat;
            float sparkH = Mathf.Clamp(h - 46f, 22f, sparkMaxH);

            r.Spark = TwinUIFactory.Graphic<TwinSparkline>(r.Root, "Spark");
            TwinUIFactory.Place((RectTransform)r.Spark.transform, 0f, 46f, _w, sparkH);
            r.Spark.SetRange(r.Min, r.Max);

            // 【P3】打开即有完整 60s 曲线 —— 从时序缓冲回填，而不是从空白开始攒。
            // 顺序不能反：先 SetRange 定量程，再 LoadFrom 灌点（灌点会触发重绘）。
            TwinMetricHistory.Fill(_anchor, metric, r.Spark);

            // 回填的点时间戳已经接近当前时刻，把 LastPushTime 拨到"现在"，
            // 避免下一帧立刻再推一个几乎重合的点（曲线末端会起毛刺）。
            r.LastPushTime = Time.unscaledTime;

            BuildBands(r);
            _rows.Add(r);
        }

        /// <summary>
        /// 阈值带：把量程按 告警 / 警告 / 正常 / 警告 / 告警 切 5 段。
        /// 和浮卡同一套算法（`TwinStatusPalette.Of(status, 0.38f)` 带透明度）。
        /// </summary>
        private void BuildBands(MetricRow r)
        {
            int used = 0;
            used += EmitBand(r, used, r.Min, r.AlarmLow, TwinStatus.Alarm);
            used += EmitBand(r, used, r.AlarmLow, r.WarnLow, TwinStatus.Warning);
            used += EmitBand(r, used, r.WarnLow, r.WarnHigh, TwinStatus.Normal);
            used += EmitBand(r, used, r.WarnHigh, r.AlarmHigh, TwinStatus.Warning);
            used += EmitBand(r, used, r.AlarmHigh, r.Max, TwinStatus.Alarm);

            for (int i = used; i < r.Bands.Length; i++)
                if (r.Bands[i] != null) r.Bands[i].gameObject.SetActive(false);
        }

        private int EmitBand(MetricRow r, int slot, float from, float to, TwinStatus status)
        {
            if (slot >= r.Bands.Length) return 0;

            float span = r.Max - r.Min;
            if (span <= 0.0001f) return 0;

            float a = Mathf.Clamp01((from - r.Min) / span);
            float b = Mathf.Clamp01((to - r.Min) / span);
            float x = Mathf.Min(a, b) * _w;
            float w = Mathf.Abs(b - a) * _w;

            if (w < 1f)
            {
                r.Bands[slot].gameObject.SetActive(false);
                return 1;   // 占位但不可见，保持槽位顺序稳定
            }

            Image band = r.Bands[slot];
            band.gameObject.SetActive(true);
            band.color = TwinStatusPalette.Of(status, 0.38f);
            TwinUIFactory.Place(band.rectTransform, x, 24f, w, 6f);
            return 1;
        }

        // ================================================================ 告警区

        private const float AlarmRowH = 28f;

        /// <summary>告警行的最大高度。全屏有多余空间时行会变高到这个值，见 `BuildAlarmArea`。</summary>
        private const float AlarmRowHMax = 46f;

        /// <summary>
        /// 造告警区。`extraH` 是"指标块封顶后没用掉的余量"（只有全屏才可能 > 0）——
        /// 全给它加到**行高**上，而不是在底部留一块空白：
        /// 行高 28 → 44 让「时间 · 状态 · 设备名」读全，下边界也正好贴到 568。
        ///
        /// 为什么加行高而不是加行数：行数是"这台设备历史上出过几条告警"，
        /// 由数据决定，UI 不该为了填满而多显示几条 —— 那会让人以为告警变多了。
        /// </summary>
        private void BuildAlarmArea(RectTransform root, float y, int rows, float extraH)
        {
            for (int i = 0; i < _alarms.Count; i++)
            {
                if (_alarms[i].Root == null) continue;
                _alarms[i].Root.gameObject.SetActive(false);   // 同 RebuildMetrics：先关再删
                TwinUIFactory.DestroySafe(_alarms[i].Root.gameObject);
            }
            _alarms.Clear();

            // 标题节点会被重建 —— 旧的必须销毁，否则每换一台设备就多一个隐藏 Text
            if (_alarmTitle != null) TwinUIFactory.DestroySafe(_alarmTitle.gameObject);

            _alarmTitle = TwinUIFactory.Label(root, "AlarmTitle", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_alarmTitle.rectTransform, _x, y, _w, 16f);

            // 行高：基准 28；有告警且有余量时按行数摊开，封顶 46。
            // 没有告警（rows==0）时余量无处安放 —— 交给下面的空态文案，不硬撑行。
            float rowH = AlarmRowH;
            if (rows > 0 && extraH > 0f)
                rowH = Mathf.Min(AlarmRowHMax, AlarmRowH + extraH / rows);

            for (int i = 0; i < rows; i++)
            {
                AlarmRow a = new AlarmRow();

                GameObject go = TwinUIFactory.Node("A_" + i, root);
                a.Root = (RectTransform)go.transform;
                TwinUIFactory.Place(a.Root, _x, y + 20f + i * rowH, _w, rowH - 2f);

                TwinRoundedRect bg = TwinUIFactory.Rounded(a.Root, "Bg", TwinTheme.R8,
                    TwinTheme.Track, TwinTheme.Track, Color.clear, 0f, false);
                TwinUIFactory.Stretch(bg.rectTransform);

                a.Bar = TwinUIFactory.Block(a.Root, "Bar", TwinTheme.StatusAlarm, false);
                TwinUIFactory.Place(a.Bar.rectTransform, 0f, 5f, 3f, rowH - 12f);

                a.Desc = TwinUIFactory.Label(a.Root, "Desc", TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(a.Desc.rectTransform, 12f, 0f, _w - 80f, rowH - 2f);

                a.Age = TwinUIFactory.Label(a.Root, "Age", TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.TextMuted);
                TwinUIFactory.Place(a.Age.rectTransform, _w - 64f, 0f, 56f, rowH - 2f);

                a.Root.gameObject.SetActive(false);
                _alarms.Add(a);
            }

            // 【P11】没有告警时给一句空态，不留一块白。
            // 【P11】没有告警时给一句空态，不留一块白。
            // 原来没有任何提示：1~2 个指标的设备下半页是一大片纯背景，
            // 看起来像"下面还有内容没加载出来"。全屏可用高 408 时这个空白有 178px，很扎眼。
            //
            // ⚠️ 这里**只负责建节点、不决定显隐** —— 显隐统一由 `RefreshAlarms()`
            // 按"最后到底有几行"来定。两处都管会出现 P10 那种两出口打架：
            // 这里按"要几行"判断，那边按"实际填了几行"判断，一旦不等就闪。
            if (_alarmEmpty == null)
            {
                _alarmEmpty = TwinUIFactory.Label(root, "AlarmEmpty", TwinTheme.FsMicro,
                    TextAnchor.UpperLeft, TwinTheme.TextMuted);
                _alarmEmpty.raycastTarget = false;
                _alarmEmpty.text = "暂无告警记录";
            }
            TwinUIFactory.Place(_alarmEmpty.rectTransform, _x, y + 20f, _w, 16f);
        }

        /// <summary>刷告警区：按 TwinId 过滤这台设备的告警记录，取最近几条。</summary>
        private void RefreshAlarms()
        {
            if (_alarmTitle == null) return;

            // 不重新查：`RebuildMetrics` 里的 CountAlarms() 刚填过 _alarmBuf，
            // 同一帧再 GetRecent 一次是白扫 64 条记录。
            if (_alarmBuf.Count == 0)
            {
                TwinAlarmHistory h = TwinAlarmHistory.Instance;
                if (h != null) h.GetRecent(_alarmBuf, 64);
            }

            int total = 0;
            int n = 0;
            float now = Time.realtimeSinceStartup;

            for (int i = 0; i < _alarmBuf.Count && n < _alarms.Count; i++)
            {
                TwinAlarmHistory.AlarmRecord rec = _alarmBuf[i];
                if (!string.Equals(rec.TwinId, _anchor.TwinId, System.StringComparison.Ordinal)) continue;

                total++;
                AlarmRow a = _alarms[n];
                if (!a.Root.gameObject.activeSelf) a.Root.gameObject.SetActive(true);

                Color c = TwinStatusPalette.Of(rec.PeakStatus);
                a.Bar.color = c;
                SetText(a.Desc, rec.Clock + "  " + TwinStatusPalette.Label(rec.PeakStatus) +
                                " · " + rec.DisplayName);
                SetText(a.Age, rec.Ongoing ? "进行中" : TwinAlarmHistory.FormatDuration(rec.Duration(now)));
                a.Age.color = rec.Ongoing ? TwinTheme.StatusAlarm : TwinTheme.TextMuted;
                n++;
            }

            // 余下的行隐藏
            for (int i = n; i < _alarms.Count; i++)
                if (_alarms[i].Root.gameObject.activeSelf) _alarms[i].Root.gameObject.SetActive(false);

            // 【P11】标题只写分类与条数，**不写「无记录」** ——
            // 那句交给下面的空态标签（`_alarmEmpty`），否则同一层上下两行
            // 都在说"没有告警"，是重复信息（P29-3.5 规格三消的就是这种复读）。
            SetText(_alarmTitle, total > 0 ? "告警与异常（" + total + "）" : "告警与异常");

            // 【P11】空态显隐的**唯一出口**。
            //
            // 判据是 `n == 0`（实际填进去几行），**不是 `_alarms.Count == 0`**：
            // 后者是"建了几个节点"，有告警时它是 4 —— 拿它判会让空态和告警行同时出现。
            // 也不是 `total == 0`：total 统计的是这台设备的全部记录，
            // 而实际显示被 `n < _alarms.Count` 截断，两者相等时会一致，不等时以实际为准。
            if (_alarmEmpty != null)
            {
                bool showEmpty = n == 0;
                if (_alarmEmpty.gameObject.activeSelf != showEmpty)
                    _alarmEmpty.gameObject.SetActive(showEmpty);
            }
        }

        // ================================================================ 数值

        private void RefreshValues()
        {
            if (_anchor == null) return;

            // 【P27】状态会随数据变，胶囊必须跟着走。原来它只在 `OnShow` 时刷一次，
            // 打开时若正好在越界窗口里，之后数据恢复了胶囊还红着 —— 见 RefreshChip 的注释。
            RefreshChip();

            float now = Time.unscaledTime;

            for (int i = 0; i < _rows.Count; i++)
            {
                MetricRow r = _rows[i];
                if (r.Root == null || string.IsNullOrEmpty(r.MetricName)) continue;

                // 时间轴一直往前走：没新数据时线也得继续左滚，
                // 否则「数据停了」看起来像「数据没变」。
                if (r.Spark != null) r.Spark.Tick(now);

                TwinValue v;
                if (!_anchor.TryGetValue(r.MetricName, out v))
                {
                    SetText(r.Value, "--");
                    continue;
                }

                if (r.Spark != null && now - r.LastPushTime >= SparkSampleInterval)
                {
                    r.LastPushTime = now;
                    r.Spark.Push(now, v.Value);
                }

                TwinStatus st = _anchor.StatusOf(r.MetricName);
                r.Value.color = TwinStatusPalette.Of(st);

                _sb.Length = 0;
                _sb.Append(v.IsUsable ? v.Value.ToString("0.##") : "—");
                if (!string.IsNullOrEmpty(r.Unit)) _sb.Append(' ').Append(r.Unit);
                SetText(r.Value, _sb.ToString());

                // 说明：量程 / 正常范围 / 与上一次的差
                _sb.Length = 0;
                _sb.Append("量程 ").Append(r.Min.ToString("0.#")).Append('~').Append(r.Max.ToString("0.#"));
                if (!string.IsNullOrEmpty(r.Unit)) _sb.Append(r.Unit);
                _sb.Append("   ·   正常 ").Append(r.WarnLow.ToString("0.#")).Append('~').Append(r.WarnHigh.ToString("0.#"));
                if (v.IsUsable && Mathf.Abs(v.Delta) > 0.0001f)
                    _sb.Append("   ·   Δ ").Append(v.Delta.ToString("+0.##;-0.##"));
                SetText(r.Hint, _sb.ToString());

                // 游标
                if (r.Marker != null)
                {
                    float t = r.Max > r.Min
                        ? Mathf.Clamp01((v.Value - r.Min) / (r.Max - r.Min))
                        : 0.5f;
                    RectTransform mr = r.Marker.rectTransform;
                    mr.anchoredPosition = new Vector2(t * _w, mr.anchoredPosition.y);
                }
            }

            // 新鲜度行
            _sb.Length = 0;
            _sb.Append("数据 ");
            float age = _anchor.SecondsSinceLastSample;
            _sb.Append(age < 0f ? "尚未收到" : age.ToString("0.0") + " 秒前");
            _sb.Append("   ·   指标 ").Append(_anchor.MetricCount).Append(" 项");
            SetText(_fresh, _sb.ToString());
        }

        private static void SetText(Text t, string value)
        {
            if (t == null) return;
            if (string.Equals(t.text, value, System.StringComparison.Ordinal)) return;
            t.text = value;
        }
    }
}
