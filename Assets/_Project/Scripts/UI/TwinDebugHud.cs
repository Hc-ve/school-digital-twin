using System.Collections.Generic;
using System.Text;
using UnityEngine;
using SchoolTwin.Core;
using SchoolTwin.Data;
using SchoolTwin.Interaction;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 调试 HUD（IMGUI，零配置、零依赖）。
    ///
    /// P1 的验收标准是「10Hz 下帧率无可见下降」「拔掉数据源显示离线」，
    /// 这些都需要一个能当场看见数字的地方。正式 UI 面板留到 P1 后段再做。
    ///
    /// 性能注意：字符串每帧重建会产生大量 GC，所以这里按 _refreshInterval 缓存文本。
    /// </summary>
    [AddComponentMenu("SchoolTwin/调试 HUD (TwinDebugHud)")]
    public class TwinDebugHud : MonoBehaviour
    {
        [SerializeField] private KeyCode _toggleKey = KeyCode.F3;
        [SerializeField] private bool _visible = true;
        [SerializeField] private float _refreshInterval = 0.25f;
        [SerializeField] private int _maxRows = 14;
        [SerializeField] private bool _showTwinRows = true;

        private readonly StringBuilder _sb = new StringBuilder(4096);
        private readonly List<TwinAnchor> _rows = new List<TwinAnchor>(256);
        private static readonly System.Comparison<TwinAnchor> s_compare =
            (a, b) => string.CompareOrdinal(a.TwinId, b.TwinId);

        private float _nextRefresh;
        private float _fpsSmoothed = 60f;
        private string _text = "初始化中…";
        private int _lineCount = 1;

        // ---- P29-3.6：IMGUI 缩放缓存（窗口尺寸变了才重算）----
        private float _canvasScale = -1f;
        private int _scaleForW = -1;
        private int _scaleForH = -1;

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey)) _visible = !_visible;

            float dt = Time.unscaledDeltaTime;
            if (dt > 0f) _fpsSmoothed = Mathf.Lerp(_fpsSmoothed, 1f / dt, 0.08f);

            if (!_visible) return;
            if (Time.realtimeSinceStartup < _nextRefresh) return;
            _nextRefresh = Time.realtimeSinceStartup + _refreshInterval;
            Rebuild();
        }

        private void Rebuild()
        {
            TwinRuntime runtime = TwinRuntime.Instance;
            _sb.Length = 0;

            _sb.Append("数字孪生调试 HUD   [").Append(_toggleKey).Append(" 开关]\n");
            _sb.Append("FPS ").Append(_fpsSmoothed.ToString("0.0")).Append("   ")
               .Append((1000f / Mathf.Max(0.01f, _fpsSmoothed)).ToString("0.00")).Append(" ms\n");

            if (runtime == null)
            {
                _sb.Append("TwinRuntime：未初始化\n");
                _text = _sb.ToString(); _lineCount = CountLines(_text);
                return;
            }

            TwinBusStats st = runtime.Bus.GetStats();

            // 【P29-3.6】设备数与正式 UI 同口径（A1~A4 的 Active 判定）。
            // 上一版这里显示的是 `Registry.Count`（注册数），和面板上的「设备总数」不是同一个数 ——
            // 排查时会以为面板算错了。现在两个数并列：「设备 53 / 注册 59」的差额
            // 就是 BAK 备份锚点或未激活层级，这恰好是排查时最想看到的信息。
            _sb.Append("设备 ").Append(TwinDeviceStats.ActiveDeviceCount)
               .Append("   在线 ").Append(TwinDeviceStats.OnlineDeviceCount)
               .Append("   离线 ").Append(TwinDeviceStats.OfflineDeviceCount)
               .Append("   告警 ").Append(TwinDeviceStats.AlarmDeviceCount)
               .Append("   受控 ").Append(TwinDeviceStats.ControlScopeCount).Append('\n');
            _sb.Append("注册 ").Append(runtime.Registry.Count)
               .Append("   数据源 ").Append(runtime.Sources.Count)
               .Append("   订阅 ").Append(st.SubscriberCount).Append('\n');
            _sb.Append("总线 收 ").Append(st.Received)
               .Append(" / 发 ").Append(st.Dispatched)
               .Append(" / 丢 ").Append(st.Dropped)
               .Append(" / 无主 ").Append(st.Orphan)
               .Append(" / 待处理 ").Append(st.PendingInbox).Append('\n');

            _sb.Append("状态 正常 ").Append(runtime.Registry.CountByStatus(TwinStatus.Normal))
               .Append("  警告 ").Append(runtime.Registry.CountByStatus(TwinStatus.Warning))
               .Append("  告警 ").Append(runtime.Registry.CountByStatus(TwinStatus.Alarm))
               .Append("  陈旧 ").Append(runtime.Registry.CountByStatus(TwinStatus.Stale))
               .Append("  离线 ").Append(runtime.Registry.CountByStatus(TwinStatus.Offline))
               .Append("  未知 ").Append(runtime.Registry.CountByStatus(TwinStatus.Unknown)).Append('\n');

            for (int i = 0; i < runtime.Sources.Count; i++)
            {
                DataSourceBase src = runtime.Sources[i];
                if (src == null) continue;
                _sb.Append("  · ").Append(src.StatusText);
                MockDataSource mock = src as MockDataSource;
                if (mock != null) _sb.Append("   已生成 ").Append(mock.EmittedTotal).Append(" 条");
                _sb.Append('\n');
            }

            if (TwinPicker.Selected != null)
            {
                TwinAnchor sel = TwinPicker.Selected;
                _sb.Append("选中：").Append(sel.DisplayName)
                   .Append("  (").Append(sel.TwinId).Append(")  状态=").Append(sel.Status).Append('\n');
                foreach (KeyValuePair<string, TwinValue> kv in sel.Values)
                {
                    _sb.Append("    ").Append(kv.Key).Append(" = ")
                       .Append(kv.Value.Value.ToString("0.###"))
                       .Append("  Δ").Append(kv.Value.Delta.ToString("+0.###;-0.###;0"))
                       .Append("  ").Append(kv.Value.Quality).Append('\n');
                }
            }
            else
            {
                _sb.Append("选中：（左键点击孪生点位）\n");
            }

            if (_showTwinRows)
            {
                _rows.Clear();
                // 遍历入口走快照层（P29-3.6）：UI 层不再直接写 Registry.All，
                // 「谁在遍历注册表」这件事只留一个入口。
                IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (all[i] != null) _rows.Add(all[i]);
                    }
                }
                _rows.Sort(s_compare);

                int limit = Mathf.Min(_maxRows, _rows.Count);
                for (int i = 0; i < limit; i++)
                {
                    TwinAnchor a = _rows[i];
                    _sb.Append("  ").Append(Pad(a.TwinId, 34)).Append(Pad(a.Status.ToString(), 9));
                    bool first = true;
                    foreach (KeyValuePair<string, TwinValue> kv in a.Values)
                    {
                        if (!first) _sb.Append("  ");
                        _sb.Append(kv.Key).Append('=').Append(kv.Value.Value.ToString("0.#"));
                        first = false;
                    }
                    _sb.Append('\n');
                }
                if (_rows.Count > limit) _sb.Append("  … 其余 ").Append(_rows.Count - limit).Append(" 个点位省略\n");
            }

            _text = _sb.ToString(); _lineCount = CountLines(_text);
        }

        private static string Pad(string s, int width)
        {
            if (string.IsNullOrEmpty(s)) s = "";
            if (s.Length >= width) return s.Substring(0, width);
            return s.PadRight(width);
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            int n = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') n++;
            }
            return n;
        }

        /// <summary>
        /// 画布逻辑单位 → 屏幕像素 的倍率。改了 Screen 尺寸才重算（resize 时自动跟上）。
        ///
        /// 优先问主画布的 `scaleFactor`（那是 Unity 的权威值，已把 Expand 算过一遍）；
        /// 拿不到就按 Expand 语义自己算 `min(w/1000, h/720)` —— 与
        /// `TwinUIFactory.ApplyCanvasPolicy()` 统一的口径一致。
        /// </summary>
        private float ResolveCanvasScale()
        {
            if (Screen.width == _scaleForW && Screen.height == _scaleForH && _canvasScale > 0.0001f)
                return _canvasScale;

            _scaleForW = Screen.width;
            _scaleForH = Screen.height;

            Canvas canvas = FindObjectOfType<Canvas>(true);
            _canvasScale = (canvas != null && canvas.scaleFactor > 0.0001f)
                ? canvas.scaleFactor
                : Mathf.Min(Screen.width / 1000f, Screen.height / 720f);

            if (_canvasScale <= 0.0001f) _canvasScale = 1f;
            return _canvasScale;
        }

        private void OnGUI()
        {
            if (!_visible) return;

            // 【P29-3.6】IMGUI 的坐标是**屏幕像素**，完全不经过 CanvasScaler。
            // 原来写死 560 宽、用 Screen.height 算高，于是窗口一变，F3 面板就和其他面板
            // 不在同一个尺度上（1069×654 下明显比 1000×720 下"大一号"）。
            // 这里把 GUI.matrix 缩到「画布逻辑坐标」，尺寸与位置全部用逻辑单位，
            // 两个窗口下看到的字与框一样大 —— 这才是"基于主 Canvas 逻辑坐标"。
            float scale = ResolveCanvasScale();
            Matrix4x4 prev = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            const float width = 560f;                       // 逻辑单位，不是屏幕像素
            float viewH = Screen.height / scale;            // 画布逻辑高度（Expand 下恒 ≥ 720）
            float height = Mathf.Min(viewH - 20f, 22f * _lineCount + 20f);

            GUILayout.BeginArea(new Rect(10f, 10f, width, height), GUI.skin.box);
            GUILayout.Label(_text);
            GUILayout.EndArea();

            GUI.matrix = prev;
        }
    }
}
