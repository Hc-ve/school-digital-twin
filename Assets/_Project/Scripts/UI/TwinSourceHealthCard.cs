using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Core;
using SchoolTwin.Data;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 数据源健康度卡片（窄态数据监测页底部 / 全屏统计页底部共用一份实现）。
    ///
    /// ================================================================
    /// 它解决什么问题
    /// ================================================================
    /// 之前"这个数据源到底在不在跑"只能靠两处间接线索：
    ///   · 浮卡底部那条横条上的一个色点（位置小、且要先点开一台设备才看得到）
    ///   · 曲线是不是在动（但这只能说明"有数据"，说不出"哪个源在发、发了几条、丢了几条"）
    ///
    /// 演示现场最常被问到的一句话是「你这数据是真的还是假的」——
    /// 这个卡片就是用来当场回答它的：当前是哪个源、状态是什么、发出了多少条、
    /// 有没有出现过故障。**数字是活的，不是写死的文案。**
    ///
    /// ================================================================
    /// 为什么单独一个类，而不是塞进 TwinSystemPage
    /// ================================================================
    /// 那个文件已经 2700+ 行。再往里塞 UI 构建代码，下次改这个卡片的人
    /// 得先翻完五个页的布局才能找到它。这里只依赖 TwinUIFactory + 数据源，
    /// 两种形态都能直接复用 —— 也正好符合"全屏与窄态必须同步"的项目约定：
    /// **同一个类、同一套口径，只是父容器和宽度不同。**
    /// </summary>
    public class TwinSourceHealthCard
    {
        // ---------------------------------------------------------------- 布局

        /// <summary>行高。</summary>
        private const float RowH = 30f;

        /// <summary>标题区高（标题 18 + 下间隙 4）。</summary>
        private const float HeadH = 22f;

        /// <summary>明细区与脚注之间的间隙。</summary>
        private const float FootGap = 8f;

        /// <summary>脚注高。</summary>
        private const float FootH = 16f;

        /// <summary>行数（Mock / MQTT / 回放 / 录制）。</summary>
        public const int Rows = 4;

        /// <summary>
        /// 卡片总高 —— **公开成常量**，而不是实例属性。
        ///
        /// 【为什么要这样】上一版是 `public float Height { get; private set; }`，
        /// 在构造函数里算。结果调用方（TwinSystemPage）那边的内容高度只能**手抄**一个
        /// 数字（802），抄错了不报错，表现为「最后一行被 RectMask2D 裁掉、看起来少一行」。
        /// 上一版就抄错了：卡片实际占 860，内容只给了 802，第 4 行整行没显示。
        ///
        /// 现在改成 const，调用方可以直接写
        /// `NStatsHealthY + TwinSourceHealthCard.TotalHeight` —— 只有一个来源，
        /// 改行数不用再记得改两处。
        /// </summary>
        public const float TotalHeight = HeadH + Rows * RowH + FootGap + FootH;

        private readonly float _width;

        // ---------------------------------------------------------------- 节点

        private RectTransform _root;

        private readonly Text[] _name = new Text[4];
        private readonly Text[] _state = new Text[4];
        private readonly Text[] _detail = new Text[4];
        private readonly Image[] _dot = new Image[4];

        private Text _title;
        private Text _footer;

        // ---------------------------------------------------------------- 刷新节流

        private float _nextRefresh;

        /// <summary>构建。`width` 是可用宽度（窄态 434 / 全屏看栏宽）。</summary>
        public TwinSourceHealthCard(float width)
        {
            _width = width;
        }

        /// <summary>
        /// 在 `parent` 的 (x, y) 处建卡片。`y` 是**正数向下**（与 TwinUIFactory.Place 一致）。
        /// </summary>
        public void Build(RectTransform parent, float x, float y)
        {
            _root = (RectTransform)TwinUIFactory.Node("NSourceHealth", parent).transform;
            TwinUIFactory.Place(_root, x, y, _width, TotalHeight);

            _title = TwinUIFactory.Label(_root, "Title", TwinTheme.FsBodyStrong,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_title.rectTransform, 0f, 0f, 200f, 18f);
            _title.text = "数据源健康度";

            for (int i = 0; i < Rows; i++)
            {
                float ry = HeadH + i * RowH;

                // 状态点：7×7 圆
                _dot[i] = TwinUIFactory.Block(_root, "Dot_" + i, TwinTheme.StatusUnknown, false);
                TwinUIFactory.Place(_dot[i].rectTransform, 0f, ry + 8f, 7f, 7f);

                // 源名（Mock / MQTT / 回放 / 录制）
                _name[i] = TwinUIFactory.Label(_root, "Name_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(_name[i].rectTransform, 14f, ry, 58f, 20f);

                // 状态词（运行中 / 已停止 / 已禁用 …）
                _state[i] = TwinUIFactory.Label(_root, "State_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(_state[i].rectTransform, 74f, ry, 62f, 20f);

                // 明细（条数 / 文件名 / 倍速）—— 右对齐，长文本自己截断
                _detail[i] = TwinUIFactory.Label(_root, "Detail_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.TextMuted);
                TwinUIFactory.Place(_detail[i].rectTransform, 136f, ry,
                    Mathf.Max(0f, _width - 136f), 20f);
            }

            // 脚注：当前生效的源 + 一条口径说明。
            //
            // 【为什么这行必须写清"切换时只有一个源在跑"】这是本项目最容易被人误会的点：
            // 看到 4 个源都在列表里，会以为"它们同时在发数据"。
            // 实际上切换器保证同一时刻只有一个生效，其余是 stopped —— 这一行说明它。
            _footer = TwinUIFactory.Label(_root, "Footer", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_footer.rectTransform, 0f, TotalHeight - FootH, _width, 14f);
            _footer.horizontalOverflow = HorizontalWrapMode.Wrap;
            _footer.text = "同一时刻只有一个源生效";

            Refresh(true);
        }

        /// <summary>0.5s 节流刷新。给 Update 每帧喊，不会造成每帧重建文本。</summary>
        public void Tick()
        {
            if (_root == null) return;
            Refresh(false);
        }

        private void Refresh(bool force)
        {
            if (_root == null) return;

            float now = Time.unscaledTime;
            if (!force && now < _nextRefresh) return;
            _nextRefresh = now + 0.5f;

            // 【两层查找：先已注册，再场景兜底 —— 2026-11-04 修「MQTT 显示不存在」】
            //
            // 只用 `TwinRuntime.Sources` 是错的。`Sources` 是**已注册**的列表
            // （`DataSourceBase.OnEnable` 里 RegisterSource），而组件被 disabled 时
            // `OnEnable` 根本不执行 → 不会注册 → 查不到。
            //
            // 上一版因此把「场景里有、但被禁用」的 MQTT 显示成「不存在」。
            // 而 P12 就是把 MQTT 的 `m_Enabled` 改成了 0（默认源改 Mock），
            // 于是每次 Play 都在健康度卡片上看到一句假话 —— 观众会以为 MQTT 这个通道没做。
            //
            // 正确语义是三层：
            //   ① 注册了且在跑        → 运行中（绿）
            //   ② 场景里有、但被禁用  → 已禁用（灰）← 这才是"配了但没开"
            //   ③ 场景里真没有        → 不存在（灰）
            IReadOnlyList<DataSourceBase> sources =
                TwinRuntime.Instance != null ? TwinRuntime.Instance.Sources : null;

            MockDataSource mock = FindIn<MockDataSource>(sources)
                                  ?? FindInScene<MockDataSource>();
            MqttDataSource mqtt = FindIn<MqttDataSource>(sources)
                                  ?? FindInScene<MqttDataSource>();
            TwinPlayer player = FindIn<TwinPlayer>(sources)
                                ?? FindInScene<TwinPlayer>();

            // 录制器是 `IDataSink`（数据出口），**不是** DataSourceBase，
            // 所以它不在 `Sources` 列表里 —— 必须在场景里找。
            // 这一点很容易写错：`as T` 拿不到它，会静默显示"不存在"。
            TwinRecorder rec = UnityEngine.Object.FindObjectOfType<TwinRecorder>(true);

            FillRow(0, "Mock", mock, mock != null ? mock.EmittedTotal.ToString("N0") + " 条" : "");
            FillRow(1, "MQTT", mqtt, mqtt != null ? mqtt.StatusText : "");
            FillRow(2, "回放", player, player != null ? PlaybackDetail(player) : "");
            FillRecorderRow(rec);

            // 切换器同样不是 DataSourceBase，走场景查找。
            // 它可能是运行时才 AddComponent 的（TwinDetailPanel 建数据源条时），
            // 所以 includeInactive 要给 true。
            TwinSourceSwitcher sw = UnityEngine.Object.FindObjectOfType<TwinSourceSwitcher>(true);
            if (_footer != null)
            {
                if (sw == null)
                {
                    _footer.text = "切换器未就绪 · 同一时刻只有一个源生效";
                }
                else
                {
                    _sb.Length = 0;
                    _sb.Append("当前生效 ").Append(TwinSourceSwitcher.ShortName(sw.CurrentKind));
                    if (sw.IsRecording) _sb.Append(" · 录制中");
                    _sb.Append(" · 同一时刻只有一个源生效");
                    _footer.text = _sb.ToString();
                }
            }
        }

        /// <summary>
        /// 在已注册列表里找指定类型的数据源。找不到返回 null（表示"这个源没配"）。
        ///
        /// 泛型约束是 `DataSourceBase` 而不是 `Component` —— 让编译器帮我挡住
        /// "把不是数据源的组件传进来"这类错（TwinRecorder / TwinSourceSwitcher 就踩过）。
        /// </summary>
        private static T FindIn<T>(IReadOnlyList<DataSourceBase> list) where T : DataSourceBase
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                T hit = list[i] as T;
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>
        /// 场景兜底查找（含未激活/已禁用）。
        ///
        /// 【它和 FindIn 的区别就是"配了没开"这一档】只有 FindIn 的话，
        /// 一个 `m_Enabled: 0` 的源会被显示成「不存在」——
        /// 那不是事实，事实是"场景里配好了、当前没启用"。
        /// </summary>
        private static T FindInScene<T>() where T : DataSourceBase
        {
            return UnityEngine.Object.FindObjectOfType<T>(true);
        }

        private readonly StringBuilder _sb = new StringBuilder(64);

        /// <summary>填一行「设备类数据源」。null 表示场景里没有这个组件。</summary>
        private void FillRow(int i, string name, DataSourceBase src, string detail)
        {
            if (_name[i] == null) return;
            _name[i].text = name;

            if (src == null)
            {
                _state[i].text = "不存在";
                _state[i].color = TwinTheme.TextMuted;
                if (_dot[i] != null) _dot[i].color = TwinTheme.StatusUnknown;
                if (_detail[i] != null) _detail[i].text = "";
                return;
            }

            // ⚠️ 组件存在但没启用 → 说「已禁用」，不能说「已停止」也不能说「不存在」。
            //
            // 三种状态对使用者是三个不同的问题：
            //   已禁用 = 场景里配好了，这次没开（点一下数据源条就能开）
            //   已停止 = 开过、被切换器停掉了
            //   不存在 = 这个通道根本没做
            // 混成一句，排查方向就全歪了。
            if (!src.enabled || !src.gameObject.activeInHierarchy)
            {
                _state[i].text = "已禁用";
                _state[i].color = TwinTheme.TextMuted;
                if (_dot[i] != null) _dot[i].color = TwinTheme.StatusUnknown;
                if (_detail[i] != null) _detail[i].text = "已配置 · 未启用";
                return;
            }

            DataSourceState st = src.State;
            _state[i].text = StateLabel(st);
            _state[i].color = StateColor(st);
            if (_dot[i] != null) _dot[i].color = StateColor(st);
            if (_detail[i] != null) _detail[i].text = detail ?? "";
        }

        private static string PlaybackDetail(TwinPlayer p)
        {
            if (p.SampleCount <= 0) return "无录像";
            string fileName = string.IsNullOrEmpty(p.ResolvedPath)
                ? "" : System.IO.Path.GetFileNameWithoutExtension(p.ResolvedPath);
            string pause = p.IsPaused ? "暂停 " : "";
            return pause + fileName + " · " + (p.Progress * 100f).ToString("0") + "%";
        }

        private void FillRecorderRow(TwinRecorder rec)
        {
            const int i = 3;
            if (_name[i] == null) return;
            _name[i].text = "录制";

            if (rec == null)
            {
                _state[i].text = "不存在";
                _state[i].color = TwinTheme.TextMuted;
                if (_dot[i] != null) _dot[i].color = TwinTheme.StatusUnknown;
                if (_detail[i] != null) _detail[i].text = "";
                return;
            }

            bool on = rec.IsRecording;
            _state[i].text = on ? "录制中" : "待机";
            Color c = on ? TwinTheme.StatusAlarm : TwinTheme.StatusUnknown;
            _state[i].color = on ? TwinTheme.TextPrimary : TwinTheme.TextMuted;
            if (_dot[i] != null) _dot[i].color = c;
            if (_detail[i] != null)
            {
                _sb.Length = 0;
                _sb.Append(on ? "写入中" : "未开始");
                _sb.Append(" · 共 ").Append(RecordingReader.ListRecordings().Count).Append(" 个录像");
                _detail[i].text = _sb.ToString();
            }
        }

        private static string StateLabel(DataSourceState st)
        {
            switch (st)
            {
                case DataSourceState.Idle: return "空闲";
                case DataSourceState.Connecting: return "连接中";
                case DataSourceState.Running: return "运行中";
                case DataSourceState.Degraded: return "降级";
                case DataSourceState.Stopped: return "已停止";
                case DataSourceState.Faulted: return "故障";
                default: return st.ToString();
            }
        }

        private static Color StateColor(DataSourceState st)
        {
            switch (st)
            {
                case DataSourceState.Running: return TwinTheme.StatusNormal;
                case DataSourceState.Degraded: return TwinTheme.StatusWarning;
                case DataSourceState.Faulted: return TwinTheme.StatusAlarm;
                case DataSourceState.Connecting: return TwinTheme.Accent;
                default: return TwinTheme.StatusUnknown;
            }
        }
    }
}