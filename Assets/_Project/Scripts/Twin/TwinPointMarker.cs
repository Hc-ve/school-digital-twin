using System.Text;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Core;
using SchoolTwin.UI;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 世界空间点位标记（BIM 标签风格）。
    ///
    /// 【为什么需要它】
    /// 温湿度、CO₂ 这类点位在场景里**没有对应的设备模型**（整个校园都没有传感器模型）。
    /// 拿一个立方体凑合，看着像 bug；随便找个模型替代，语义又不对。
    ///
    /// 真实数字孪生项目里的做法是「点位标记」：墙上一个小牌子，写清楚
    /// 「这是什么设备、现在是多少」。它不假装有个不存在的设备，但也让人
    /// 一眼知道这里被监控着 —— 这是 BIM 运维视图的标准表达。
    ///
    /// 【为什么不用 UGUI 面板那一套】
    /// 面板是屏幕空间、只有一个。标记是**每个点位一个、在世界里**，
    /// 所以每个标记自带一个 WorldSpace Canvas，随物体移动、始终朝向相机。
    /// 3~5 个标记的开销可以忽略。
    ///
    /// 【为什么要自带 BoxCollider】
    /// TwinPicker 靠 Physics.Raycast 找锚点。标记本身没有 Renderer，
    /// 不加碰撞体就**点不中** —— 点位绑了却选不上，比没绑还难查。
    /// </summary>
    [AddComponentMenu("SchoolTwin/点位标记 (TwinPointMarker)")]
    [DisallowMultipleComponent]
    public class TwinPointMarker : MonoBehaviour
    {
        [Header("数据来源")]
        [Tooltip("留空则自动取同物体上的 TwinAnchor")]
        [SerializeField] private TwinAnchor _anchor;

        [Tooltip("留空则用 TwinAnchor 的显示名")]
        [SerializeField] private string _titleOverride = "";

        [Header("外观")]
        [Tooltip("标记的世界尺寸（米）。0.62m 宽约等于一块门牌，贴墙挂不抢戏")]
        [SerializeField] private float _widthMeters = 0.62f;
        [SerializeField] private float _heightMeters = 0.19f;

        [Tooltip("向上偏移（米）。贴墙挂时保持 0，让牌心正好在物体位置")]
        [SerializeField] private float _lift = 0f;

        [Tooltip("始终正对相机。**贴墙挂时务必关掉** —— billboard 会让牌子绕 Y 轴转，" +
                 "一侧直接转进墙里，表现为「牌子被削掉一半」")]
        [SerializeField] private bool _billboard = false;

        [Tooltip("在标记下方显示实时数值")]
        [SerializeField] private bool _showValues = true;

        [Header("刷新")]
        [SerializeField] private float _refreshInterval = 0.1f;

        private const int CanvasWidth = 220;
        private const int CanvasHeight = 68;
        private const int FontTitle = 15;
        private const int FontValue = 19;

        /// <summary>
        /// 牌子底色。**必须几乎不透明**：半透明压在明亮的窗户上会糊成一片灰，
        /// 看起来像渲染 bug 而不是「牌子上写了字」。这两处原本各写了一份字面量，
        /// 合成一份 —— 同一件事不要留两个来源。
        /// </summary>
        private static readonly Color BgTop = new Color(0.06f, 0.08f, 0.12f, 0.93f);
        private static readonly Color BgBottom = new Color(0.04f, 0.05f, 0.08f, 0.96f);

        private RectTransform _canvasRect;
        private TwinRoundedRect _bg;
        private Image _bar;
        private Text _title;
        private Text _value;

        private Camera _camera;
        private float _nextRefresh;
        private TwinStatus _lastStatus = TwinStatus.Unknown;
        private string _lastValueText;
        private bool _built;

        private readonly StringBuilder _sb = new StringBuilder(64);

        private void OnEnable()
        {
            ResolveAnchor();
            Build();
            Refresh(true);
        }

        private void LateUpdate()
        {
            if (!_built) return;

            if (_billboard) FaceCamera();

            float now = Time.realtimeSinceStartup;
            if (now < _nextRefresh) return;
            _nextRefresh = now + Mathf.Max(0.02f, _refreshInterval);
            Refresh(false);
        }

        // ---------------------------------------------------------------- 构建

        private void ResolveAnchor()
        {
            if (_anchor == null) _anchor = GetComponent<TwinAnchor>();
            if (_anchor == null) _anchor = GetComponentInParent<TwinAnchor>();

            if (_anchor == null)
                Debug.LogWarning("[Twin] TwinPointMarker 找不到 TwinAnchor：" + name + "（标记不会显示数据）");
        }

        private void Build()
        {
            if (_built) return;
            _built = true;

            float scale = _widthMeters / CanvasWidth;

            // ---- Canvas ----
            GameObject canvasGo = new GameObject("MarkerCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.layer = gameObject.layer;

            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            // 不接 GraphicRaycaster：标记不参与 UI 事件，点击靠下面的 BoxCollider 走 3D 射线

            _canvasRect = (RectTransform)canvasGo.transform;
            _canvasRect.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);
            _canvasRect.localScale = Vector3.one * scale;
            _canvasRect.localPosition = new Vector3(0f, _lift, 0f);
            _canvasRect.localRotation = Quaternion.identity;

            // ---- 背景 ----
            _bg = TwinUIFactory.Rounded(_canvasRect, "Bg", 10f,
                BgTop, BgBottom,
                new Color(1f, 1f, 1f, 0.16f), 1f, false);
            TwinUIFactory.Stretch(_bg.rectTransform);

            // ---- 左侧状态条 ----
            // 状态主要靠这条 5px 的竖条表达：不刺眼，但一眼能看出颜色。
            _bar = TwinUIFactory.Block(_canvasRect, "StatusBar", Color.gray, false);
            TwinUIFactory.Place(_bar.rectTransform, 0f, 0f, 5f, CanvasHeight);

            // ---- 标题 ----
            _title = TwinUIFactory.Label(_canvasRect, "Title", FontTitle, TextAnchor.UpperLeft,
                TwinStatusPalette.TextPrimary, FontStyle.Bold);
            TwinUIFactory.Place(_title.rectTransform, 13f, 7f, CanvasWidth - 20f, 20f);

            // ---- 数值 ----
            _value = TwinUIFactory.Label(_canvasRect, "Value", FontValue, TextAnchor.UpperLeft,
                TwinStatusPalette.TextSecondary);
            TwinUIFactory.Place(_value.rectTransform, 13f, 30f, CanvasWidth - 20f, 26f);

            // ---- 碰撞体（让它在场景里点得中）----
            // 只在完全没有碰撞体时补：编辑器工具可能已经加过一个，
            // 再叠一个会让射线多命中一次，选中判定会变怪。
            if (GetComponent<Collider>() == null)
            {
                BoxCollider box = gameObject.AddComponent<BoxCollider>();
                box.center = new Vector3(0f, _lift, 0f);
                // 稍微厚一点，避免和墙面共面时射线打不到
                box.size = new Vector3(_widthMeters, _heightMeters, 0.06f);
            }
        }

        private void FaceCamera()
        {
            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;

            Vector3 dir = _canvasRect.position - _camera.transform.position;
            if (dir.sqrMagnitude < 0.0001f) return;

            // 用 LookRotation 而不是 LookAt：保证标记不会跟着相机滚转，始终是正的
            _canvasRect.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        // ---------------------------------------------------------------- 刷新

        private void Refresh(bool force)
        {
            if (_anchor == null) return;

            TwinStatus status = _anchor.Status;
            if (_anchor.IsStale && status != TwinStatus.Offline) status = TwinStatus.Stale;

            if (force || status != _lastStatus)
            {
                _lastStatus = status;
                Color color = TwinStatusPalette.Of(status);

                if (_bar != null) _bar.color = color;
                if (_bg != null)
                {
                    _bg.SetFill(BgTop, BgBottom);
                    _bg.SetBorder(TwinStatusPalette.Of(status, 0.45f), 1f);
                }
            }

            if (_title != null)
            {
                string title = string.IsNullOrEmpty(_titleOverride) ? _anchor.DisplayName : _titleOverride;
                if (_title.text != title) _title.text = title;
            }

            if (_value != null && _showValues)
            {
                string text = BuildValueText();
                if (force || text != _lastValueText)
                {
                    _lastValueText = text;
                    _value.text = text;
                }
            }
        }

        private string BuildValueText()
        {
            _sb.Length = 0;

            TwinRuntime runtime = TwinRuntime.Instance;
            TwinCatalog catalog = runtime != null ? runtime.Catalog : null;
            TwinPointDefinition point = catalog != null ? catalog.Find(_anchor.TwinId) : null;

            if (point != null && point.Metrics != null)
            {
                for (int i = 0; i < point.Metrics.Count; i++)
                {
                    TwinMetricDefinition def = point.Metrics[i];
                    if (def == null) continue;

                    TwinValue value;
                    if (!_anchor.TryGetValue(def.Metric, out value)) continue;

                    if (_sb.Length > 0) _sb.Append("   ");
                    _sb.Append(MetricLabel(def.Metric)).Append(' ');
                    _sb.Append(FormatValue(value.Value, def.Unit));
                }
            }

            if (_sb.Length == 0) _sb.Append(_anchor.HasData ? "—" : "等待数据…");
            return _sb.ToString();
        }

        private static string MetricLabel(string metric)
        {
            switch (metric)
            {
                case "Temperature": return "温度";
                case "Humidity": return "湿度";
                case "CO2": return "CO₂";
                case "Brightness": return "亮度";
                case "Power": return "功率";
                case "SetTemp": return "设定";
                case "Open": return "开合";
                default: return metric;
            }
        }

        private static string FormatValue(float value, string unit)
        {
            // ppm 这种大整数带小数没意义；温度/湿度/功率保留一位
            string number = Mathf.Abs(value) >= 100f
                ? Mathf.RoundToInt(value).ToString()
                : value.ToString("0.#");

            return string.IsNullOrEmpty(unit) ? number : number + unit;
        }

        [ContextMenu("重建标记")]
        private void RebuildMarker()
        {
            Transform old = transform.Find("MarkerCanvas");
            if (old != null) TwinUIFactory.DestroySafe(old.gameObject);
            _built = false;
            _bar = null;
            _bg = null;
            _title = null;
            _value = null;
            Build();
            Refresh(true);
        }
    }
}
