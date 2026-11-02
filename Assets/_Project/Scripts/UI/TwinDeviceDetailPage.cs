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
    /// </summary>
    [AddComponentMenu("SchoolTwin/设备详情页 (TwinDeviceDetailPage)")]
    public sealed class TwinDeviceDetailPage : TwinPageBase
    {
        public override string Title { get { return "设备详情"; } }

        // ---- 与 TwinSystemPage 共用同一套内容区坐标（640 宽面板）----
        private const float ContentX = 184f;
        private const float ContentW = 440f;

        private const float BackY = 8f;
        private const float CodeY = 44f;
        private const float ChipY = 86f;
        private const float InfoY = 114f;
        private const float FreshY = 140f;
        private const float DividerY = 160f;

        /// <summary>趋势线采样间隔（秒）。5Hz —— 与浮卡一致。</summary>
        private const float SparkSampleInterval = 0.2f;

        /// <summary>内容区可用下沿（面板 720 高，底部留 14）。</summary>
        private const float ContentBottom = 506f;

        /// <summary>指标块的最小 / 最大高度。块高随数量自适应，见 RebuildMetrics。</summary>
        private const float MetricMinH = 70f;
        private const float MetricMaxH = 210f;

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
            if (!_built) Build();
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
            TwinUIFactory.Place(back.rectTransform, ContentX, BackY, 104f, 28f);

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
            TwinUIFactory.Place(_code.rectTransform, ContentX, CodeY, ContentW, 20f);
            _code.horizontalOverflow = HorizontalWrapMode.Wrap;
            _code.verticalOverflow = VerticalWrapMode.Overflow;

            // ---- 状态胶囊 ----
            _chip = TwinUIFactory.Rounded(root, "Chip", 11f,
                TwinTheme.Track, TwinTheme.Track, Color.clear, 0f, false);
            TwinUIFactory.Place(_chip.rectTransform, ContentX, ChipY, 76f, 22f);

            _chipText = TwinUIFactory.Label(_chip.rectTransform, "T", TwinTheme.FsMicro,
                TextAnchor.MiddleCenter, TwinTheme.TextSecondary);
            TwinUIFactory.Stretch(_chipText.rectTransform);
            _chipText.raycastTarget = false;

            // ---- 位置 / 类型 / 电力：三列一行，比三行省 44px 留给告警区 ----
            _pos = InfoCell(root, "位置", 0f);
            _type = InfoCell(root, "类型", 150f);
            _power = InfoCell(root, "电力", 300f);

            // ---- 数据新鲜度 ----
            _fresh = TwinUIFactory.Label(root, "Fresh", TwinTheme.FsMicro,
                TextAnchor.MiddleLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_fresh.rectTransform, ContentX, FreshY, ContentW, 14f);

            Image line = TwinUIFactory.Block(root, "Line", TwinTheme.Divider, false);
            TwinUIFactory.Place(line.rectTransform, ContentX, DividerY, ContentW, 1f);
        }

        /// <summary>「标签 值」横向一格，宽 146。三列并排省掉两行高度。</summary>
        private Text InfoCell(RectTransform parent, string label, float dx)
        {
            Text l = TwinUIFactory.Label(parent, "L_" + label, TwinTheme.FsMicro,
                TextAnchor.MiddleLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(l.rectTransform, ContentX + dx, InfoY, 36f, 18f);
            l.text = label;

            Text v = TwinUIFactory.Label(parent, "V_" + label, TwinTheme.FsCaption,
                TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
            TwinUIFactory.Place(v.rectTransform, ContentX + dx + 38f, InfoY, 106f, 18f);
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

            // 10Hz —— 与浮卡同频。0.5s 太慢，趋势线的当前点会一跳一跳。
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.1f;

            RefreshValues();
        }

        private void RefreshAll()
        {
            if (_anchor == null || _code == null) return;

            _code.text = TwinCode.Full(_anchor.TwinId);

            TwinStatus s = _anchor.StatusOfWorst();
            Color sc = TwinStatusPalette.Of(s);
            SetText(_chipText, TwinStatusPalette.Label(s));
            _chipText.color = sc;
            _chip.SetFill(TwinStatusPalette.Of(s, 0.20f), TwinStatusPalette.Of(s, 0.20f));
            _chip.SetBorder(TwinStatusPalette.Of(s, 0.65f), 1f);

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

            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Root != null) TwinUIFactory.DestroySafe(_rows[i].Root.gameObject);
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
            float metricTop = DividerY + 10f;
            float metricArea = Mathf.Max(0f, ContentBottom - metricTop - alarmH);
            float metricH = metricCount > 0
                ? Mathf.Clamp(metricArea / metricCount, MetricMinH, MetricMaxH)
                : MetricMinH;

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
            BuildAlarmArea(root, alarmY, alarmRows);
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
            TwinUIFactory.Place(r.Root, ContentX, y, ContentW, h - 6f);

            // 名称
            r.Name = TwinUIFactory.Label(r.Root, "Name", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(r.Name.rectTransform, 0f, 0f, ContentW - 140f, 18f);
            r.Name.text = TwinCode.MetricName(metric);

            // 值（含单位）
            r.Value = TwinUIFactory.Label(r.Root, "Value", TwinTheme.FsBodyStrong,
                TextAnchor.UpperRight, TwinTheme.TextPrimary);
            TwinUIFactory.Place(r.Value.rectTransform, ContentW - 180f, 0f, 180f, 20f);

            // 量程条：底轨 + 阈值带 + 游标
            r.Track = TwinUIFactory.Block(r.Root, "Track", TwinTheme.Track, false);
            TwinUIFactory.Place(r.Track.rectTransform, 0f, 24f, ContentW, 6f);

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
            TwinUIFactory.Place(r.Hint.rectTransform, 0f, 32f, ContentW, 12f);

            // 趋势线：**高度随块高走**，上限 60 —— 指标少的设备波形就大，
            // 这是"不死板"最直观的一处。
            float sparkH = Mathf.Clamp(h - 46f, 22f, 60f);

            r.Spark = TwinUIFactory.Graphic<TwinSparkline>(r.Root, "Spark");
            TwinUIFactory.Place((RectTransform)r.Spark.transform, 0f, 46f, ContentW, sparkH);
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
            float x = Mathf.Min(a, b) * ContentW;
            float w = Mathf.Abs(b - a) * ContentW;

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

        private void BuildAlarmArea(RectTransform root, float y, int rows)
        {
            for (int i = 0; i < _alarms.Count; i++)
                if (_alarms[i].Root != null) TwinUIFactory.DestroySafe(_alarms[i].Root.gameObject);
            _alarms.Clear();

            // 标题节点会被重建 —— 旧的必须销毁，否则每换一台设备就多一个隐藏 Text
            if (_alarmTitle != null) TwinUIFactory.DestroySafe(_alarmTitle.gameObject);

            _alarmTitle = TwinUIFactory.Label(root, "AlarmTitle", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_alarmTitle.rectTransform, ContentX, y, ContentW, 16f);

            for (int i = 0; i < rows; i++)
            {
                AlarmRow a = new AlarmRow();

                GameObject go = TwinUIFactory.Node("A_" + i, root);
                a.Root = (RectTransform)go.transform;
                TwinUIFactory.Place(a.Root, ContentX, y + 20f + i * AlarmRowH, ContentW, AlarmRowH - 2f);

                TwinRoundedRect bg = TwinUIFactory.Rounded(a.Root, "Bg", TwinTheme.R8,
                    TwinTheme.Track, TwinTheme.Track, Color.clear, 0f, false);
                TwinUIFactory.Stretch(bg.rectTransform);

                a.Bar = TwinUIFactory.Block(a.Root, "Bar", TwinTheme.StatusAlarm, false);
                TwinUIFactory.Place(a.Bar.rectTransform, 0f, 5f, 3f, AlarmRowH - 12f);

                a.Desc = TwinUIFactory.Label(a.Root, "Desc", TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(a.Desc.rectTransform, 12f, 0f, ContentW - 80f, AlarmRowH - 2f);

                a.Age = TwinUIFactory.Label(a.Root, "Age", TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.TextMuted);
                TwinUIFactory.Place(a.Age.rectTransform, ContentW - 64f, 0f, 56f, AlarmRowH - 2f);

                a.Root.gameObject.SetActive(false);
                _alarms.Add(a);
            }
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

            SetText(_alarmTitle, total > 0 ? "告警与异常（" + total + "）" : "告警与异常 · 无记录");
        }

        // ================================================================ 数值

        private void RefreshValues()
        {
            if (_anchor == null) return;

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
                    mr.anchoredPosition = new Vector2(t * ContentW, mr.anchoredPosition.y);
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
