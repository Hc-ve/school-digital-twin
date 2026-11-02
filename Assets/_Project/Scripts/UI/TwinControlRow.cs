using System;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Command;
using SchoolTwin.Core;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 详情卡片里的「控制区」：开关按钮 + 设定温度 ± + 命令状态文字。
    ///
    /// 【为什么单独一个组件，而不是塞进 TwinDetailPanel】
    /// 那个文件已经 2500 行，而且它管的是「怎么显示数据」。控制是另一件事：
    /// 它有自己的状态机（四态）、自己的禁用规则、自己的文案来源。
    /// 分开之后，P14 做总控面板时可以把这一套直接复用（总控面板的行就是「一个设备 + 一组按钮」）。
    ///
    /// 【三条界面约束（Spec §10.5）】
    ///   ① 颜色只表示**结果**，过程用文字表示 —— 否则「已开」和「正在开」看起来一样；
    ///   ② 命令进行中按钮禁用（防连点 → 防「点三次变三次执行」）；
    ///   ③ 失败必须给人话原因，不许出现错误码。
    /// </summary>
    [AddComponentMenu("SchoolTwin/UI/控制区 (TwinControlRow)")]
    public sealed class TwinControlRow : MonoBehaviour
    {
        private const float BtnH = 26f;
        private const float BtnW = 56f;
        private const float SmallBtnW = 22f;
        private const float Gap = 6f;
        private const float TempLabelW = 56f;
        private const float TempValueW = 34f;

        private RectTransform _root;
        private Image _onBg, _offBg, _minusBg, _plusBg;
        private Text _onText, _offText, _minusText, _plusText;
        private Text _status, _tempLabel, _tempValue;
        private Button _onBtn, _offBtn, _minusBtn, _plusBtn;

        private float _scale = 1f;
        private int _fsBtn = 10;
        private int _fsStatus = 9;
        private bool _hasTemp;

        private TwinAnchor _anchor;
        private TwinDeviceModel _model;
        private TwinCommandCenter _center;

        private string _rejectText = "";
        private float _rejectUntil;
        private float _width;
        private string _diagTwinId = "";

        /// <summary>当前需要的高度（会随「有没有温度调节」变化）。0 = 不显示。</summary>
        public float Height
        {
            get
            {
                if (_anchor == null) return 0f;
                return _hasTemp ? (BtnH + Gap + BtnH) * _scale : BtnH * _scale;
            }
        }

        public bool Bound { get { return _anchor != null; } }

        // ---------------------------------------------------------------- 构建

        /// <summary>在父节点下建一个控制区。返回的组件挂在它自己的节点上。</summary>
        public static TwinControlRow Create(Transform parent)
        {
            GameObject go = TwinUIFactory.Node("ControlRow", parent);
            TwinControlRow row = go.AddComponent<TwinControlRow>();
            row.BuildInternal();
            return row;
        }

        private void BuildInternal()
        {
            _root = (RectTransform)transform;

            // ---- 第一行：开 / 关 + 状态文字 ----
            _onBg = MakeButton("On", "开", out _onText, out _onBtn);
            _offBg = MakeButton("Off", "关", out _offText, out _offBtn);
            // 动作名不写死：灯/空调/电脑是 SetPower，门是 SetOpen（见 SendSwitch）
            _onBtn.onClick.AddListener(delegate { SendSwitch(true); });
            _offBtn.onClick.AddListener(delegate { SendSwitch(false); });

            _status = TwinUIFactory.Label(_root, "Status", _fsStatus, TextAnchor.MiddleLeft,
                TwinStatusPalette.TextSecondary);

            // ---- 第二行：设定温度 ----
            _tempLabel = TwinUIFactory.Label(_root, "TempLabel", _fsStatus, TextAnchor.MiddleLeft,
                TwinStatusPalette.TextMuted);
            _tempLabel.text = "设定温度";

            _minusBg = MakeSmallButton("TempMinus", "−", out _minusText, out _minusBtn);
            _tempValue = TwinUIFactory.Label(_root, "TempValue", _fsStatus, TextAnchor.MiddleCenter,
                TwinStatusPalette.TextPrimary);
            _plusBg = MakeSmallButton("TempPlus", "+", out _plusText, out _plusBtn);

            _minusBtn.onClick.AddListener(delegate { StepTemperature(-1f); });
            _plusBtn.onClick.AddListener(delegate { StepTemperature(1f); });

            gameObject.SetActive(false);
        }

        private Image MakeButton(string name, string caption, out Text text, out Button button)
        {
            Image bg = TwinUIFactory.Block(_root, name, TwinStatusPalette.Track, true);
            TwinUIFactory.Place(bg.rectTransform, 0f, 0f, BtnW * _scale, BtnH * _scale);

            button = bg.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.ColorTint;
            ApplyTint(button);

            text = TwinUIFactory.Label(bg.transform, "Text", _fsBtn, TextAnchor.MiddleCenter,
                TwinStatusPalette.TextSecondary);
            TwinUIFactory.Stretch(text.rectTransform);
            text.text = caption;
            return bg;
        }

        private Image MakeSmallButton(string name, string caption, out Text text, out Button button)
        {
            Image bg = TwinUIFactory.Block(_root, name, TwinStatusPalette.Track, true);
            TwinUIFactory.Place(bg.rectTransform, 0f, 0f, SmallBtnW * _scale, BtnH * _scale);

            button = bg.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.ColorTint;
            ApplyTint(button);

            text = TwinUIFactory.Label(bg.transform, "Text", _fsBtn, TextAnchor.MiddleCenter,
                TwinStatusPalette.TextPrimary);
            TwinUIFactory.Stretch(text.rectTransform);
            text.text = caption;
            return bg;
        }

        /// <summary>
        /// 按钮配色。Track 是 9% 白的半透明块，ColorTint 是**乘算**，
        /// 所以高亮色给 &gt;1 的白（会被钳到 1，但 alpha 也会跟着放大，视觉上就是「亮了一点」）。
        /// </summary>
        private static void ApplyTint(Button button)
        {
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.8f, 1.8f, 1.8f, 1f);
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.45f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.06f;
            button.colors = colors;
        }

        // ---------------------------------------------------------------- 绑定

        /// <summary>
        /// 绑定一个点位。**不可控 / 未开放控制的点位会被自动隐藏**（不是灰按钮）。
        /// 灰按钮会让人以为「现在不能点，等会儿能点」，而传感器永远不能点。
        /// </summary>
        public void Bind(TwinAnchor anchor, float scale, int fontSizeBtn, int fontSizeStatus)
        {
            _scale = Mathf.Max(0.1f, scale);
            ApplyFontSize(fontSizeBtn, fontSizeStatus);

            _anchor = null;
            _model = null;
            _center = null;
            _hasTemp = false;
            _rejectText = "";
            _rejectUntil = 0f;

            if (anchor == null) { gameObject.SetActive(false); return; }

            // 不在白名单里是**正常情况**（32 个点位里只有 2 个能控），不吵 Console
            if (!TwinControlScope.IsEnabled(anchor.TwinId)) { gameObject.SetActive(false); return; }

            // ---- 从这里往下，这个点位是「应该能控」的。任何失败都必须说出来 ----
            // 【为什么要专门加这段诊断】控制区隐藏的原因有四种，界面上全都表现为「按钮不见了」，
            // 而 Console 一声不响。P12 第一次实测就撞上了：资产里的 DeviceModel 是个
            // 反序列化出来的空壳（不是 null），CanCommand == false，按钮凭空消失、无任何线索。
            TwinDeviceModel model = TwinDeviceModels.Resolve(anchor.TwinId);
            if (model == null || !model.CanCommand)
            {
                ExplainOnce(anchor.TwinId, model == null
                    ? "TwinDeviceModels.Resolve 返回 null —— 点位目录里找不到它，锚点也没有 Category"
                    : "设备模型没有动作（CanCommand=false，Category=" + model.Type + "）—— " +
                      "多半是资产里的 DeviceModel 被填成了空壳");
                gameObject.SetActive(false);
                return;
            }

            _anchor = anchor;
            _model = model;
            _hasTemp = model.FindAction("SetTemperature") != null;
            _center = TwinCommandCenter.Ensure();

            // 门的按钮说「开门 / 关门」：一个「开」字放在门上，读起来像"打开这张卡片"
            // 【P19】上课铃同理，而且语义更紧：「开 / 关」放在铃上会被读成"这个铃启用了/停用了"，
            // 而它的实际含义是"现在响一下 / 停止"。所以单独一套文案。
            bool isDoor = string.Equals(model.Type, "Door", StringComparison.Ordinal);
            bool isBell = string.Equals(model.Type, "Bell", StringComparison.Ordinal);
            if (_onText != null) _onText.text = isDoor ? "开门" : (isBell ? "打铃" : "开");
            if (_offText != null) _offText.text = isDoor ? "关门" : (isBell ? "停止" : "关");

            _tempLabel.gameObject.SetActive(_hasTemp);
            _minusBg.gameObject.SetActive(_hasTemp);
            _tempValue.gameObject.SetActive(_hasTemp);
            _plusBg.gameObject.SetActive(_hasTemp);

            gameObject.SetActive(true);
            Refresh();

            // 正向确认：让「绑上了没有」在 Console 里有一个明确答案，
            // 不用靠「按钮好像出现了」去猜。
            if (!string.Equals(_diagTwinId, anchor.TwinId, StringComparison.Ordinal))
            {
                _diagTwinId = anchor.TwinId;
                Debug.Log("[TwinCtl] 控制区已绑定：" + anchor.TwinId +
                          "（" + model.Actions.Count + " 个动作" +
                          (_hasTemp ? "，含设定温度" : "") + "）");
            }
        }

        /// <summary>
        /// 白名单内的点位却没能绑上时，把原因说一次（同一个点位只说一次，避免刷屏）。
        ///
        /// 【为什么用 Warning】这种失败在界面上只有一个表现：按钮不见了。
        /// 如果 Console 也是干净的，排查就只能靠猜。P12 实测撞过一次，代价是两轮编译。
        /// </summary>
        private void ExplainOnce(string twinId, string reason)
        {
            if (string.Equals(_diagTwinId, twinId, StringComparison.Ordinal)) return;
            _diagTwinId = twinId;
            Debug.LogWarning("[TwinCtl] 「" + twinId + "」在控制白名单里，但控制区绑不上：" + reason);
        }

        private void ApplyFontSize(int btn, int status)
        {
            btn = Mathf.Max(8, btn);
            status = Mathf.Max(8, status);
            if (_fsBtn == btn && _fsStatus == status) return;

            _fsBtn = btn;
            _fsStatus = status;

            if (_onText != null) _onText.fontSize = btn;
            if (_offText != null) _offText.fontSize = btn;
            if (_minusText != null) _minusText.fontSize = btn;
            if (_plusText != null) _plusText.fontSize = btn;
            if (_status != null) _status.fontSize = status;
            if (_tempLabel != null) _tempLabel.fontSize = status;
            if (_tempValue != null) _tempValue.fontSize = status;
        }

        /// <summary>按当前卡片宽度摆放。x/y 以卡片左上角为原点。</summary>
        public void Layout(float x, float y, float width)
        {
            if (_root == null) return;
            _width = width;

            float s = _scale;
            float btnW = BtnW * s;
            float btnH = BtnH * s;
            float gap = Gap * s;

            TwinUIFactory.Place(_root, x, y, width, Height);

            TwinUIFactory.Place(_onBg.rectTransform, 0f, 0f, btnW, btnH);
            TwinUIFactory.Place(_offBg.rectTransform, btnW + gap, 0f, btnW, btnH);

            float statusX = 2f * (btnW + gap) + 6f * s;
            TwinUIFactory.Place(_status.rectTransform, statusX, 0f,
                Mathf.Max(0f, width - statusX), btnH);

            if (!_hasTemp) return;

            float y2 = btnH + gap;
            float labelW = TempLabelW * s;
            TwinUIFactory.Place(_tempLabel.rectTransform, 0f, y2, labelW, btnH);

            float cx = labelW;
            TwinUIFactory.Place(_minusBg.rectTransform, cx, y2, SmallBtnW * s, btnH);
            cx += SmallBtnW * s + 4f * s;
            TwinUIFactory.Place(_tempValue.rectTransform, cx, y2, TempValueW * s, btnH);
            cx += TempValueW * s + 4f * s;
            TwinUIFactory.Place(_plusBg.rectTransform, cx, y2, SmallBtnW * s, btnH);
        }

        // ---------------------------------------------------------------- 刷新

        /// <summary>0.1 秒一次：命令状态 + 回读值。文字与禁用状态都在这里定。</summary>
        public void Refresh()
        {
            if (_anchor == null || _center == null) return;

            CommandState state = _center.StateOf(_anchor.TwinId);
            bool busy = CommandText.IsProgress(state);

            // 断电（P15）：按钮禁用 + 明确写「断电」。
            // 不写的话，用户看到的是"按钮灰着、状态写着已开" —— 自相矛盾，
            // 而真实原因（这一层楼断电了）在总控面板上，卡片里一个字都没有。
            bool dead = !TwinCircuit.IsPowered(_anchor.TwinId);
            SetInteractable(!busy && !dead);

            if (dead)
            {
                SetStatus(TwinCircuit.AvailabilityLabel(DeviceAvailability.PowerOff),
                    TwinCircuit.AvailabilityColor(DeviceAvailability.PowerOff));
            }
            else if (busy)
            {
                SetStatus(CommandText.ProgressLabel(state), TwinStatusPalette.TextMuted);
            }
            else if (state == CommandState.Failed)
            {
                TwinCommand cmd = _center.Current(_anchor.TwinId);
                string reason = cmd != null && !string.IsNullOrEmpty(cmd.Reason)
                    ? cmd.Reason
                    : CommandText.FailReason(CommandFailCode.ExecFailed);
                SetStatus(reason, TwinStatusPalette.Alarm);
            }
            else if (Time.unscaledTime < _rejectUntil && !string.IsNullOrEmpty(_rejectText))
            {
                SetStatus(_rejectText, TwinStatusPalette.Alarm);
            }
            else if (_center.CurrentSource != TwinSourceSwitcher.SourceKind.Mock)
            {
                // 【为什么要提示这一句】MQTT 模式下没有真设备，点按钮必然等到超时后报「设备无响应」。
                // 不解释的话，看到的是「按钮点了没反应，3 秒后变红」—— 那看起来像 bug，其实是对的。
                SetStatus("当前数据源无真设备，命令会超时", TwinStatusPalette.TextMuted);
            }
            else
            {
                SetStatus(StateText(), TwinStatusPalette.TextSecondary);
            }

            RefreshTemperature();
        }

        /// <summary>
        /// 设备当前状态的文字。**来自回读值，不是本地记的「我点过开」** ——
        /// 本地记忆在设备掉线、被别处覆盖时会说谎。
        /// </summary>
        private string StateText()
        {
            if (_model != null && string.Equals(_model.Type, "Door", StringComparison.Ordinal))
            {
                TwinValue d;
                if (_anchor.TryGetValue("Open", out d)) return d.Value >= 0.5f ? "已开" : "已关";
                return "等待数据…";
            }

            TwinValue power;
            if (!_anchor.TryGetValue("Power", out power)) return "等待数据…";

            bool on = power.Value > 0.5f;
            if (_model != null && string.Equals(_model.Type, "AC", StringComparison.Ordinal))
            {
                TwinValue temp;
                if (on && _anchor.TryGetValue("SetTemp", out temp))
                    return "已开 · " + temp.Value.ToString("0") + "°C";
            }
            return on ? "已开" : "已关";
        }

        private void RefreshTemperature()
        {
            if (!_hasTemp || _tempValue == null) return;

            if (!TwinCircuit.IsPowered(_anchor.TwinId))
            {
                _tempValue.text = "--";
                if (_minusBtn != null) _minusBtn.interactable = false;
                if (_plusBtn != null) _plusBtn.interactable = false;
                return;
            }

            TwinValue temp;
            if (!_anchor.TryGetValue("SetTemp", out temp))
            {
                _tempValue.text = "--";
                return;
            }

            float rounded = Mathf.Round(temp.Value);
            _tempValue.text = rounded.ToString("0") + "°C";

            // 到边界就把对应按钮禁掉，别让人点了没反应
            bool canDown = rounded > 16f;
            bool canUp = rounded < 30f;
            if (_minusBtn != null) _minusBtn.interactable = canDown && !_center.IsBusy(_anchor.TwinId);
            if (_plusBtn != null) _plusBtn.interactable = canUp && !_center.IsBusy(_anchor.TwinId);
        }

        private void SetStatus(string text, Color color)
        {
            if (_status == null) return;
            if (!string.Equals(_status.text, text, StringComparison.Ordinal)) _status.text = text;
            if (_status.color != color) _status.color = color;
        }

        private void SetInteractable(bool interactable)
        {
            if (_onBtn != null) _onBtn.interactable = interactable;
            if (_offBtn != null) _offBtn.interactable = interactable;

            // 文字也跟着压暗：只压按钮底色的话，亮着的文字看着还是「可以点」
            float a = interactable ? 1f : 0.45f;
            SetAlpha(_onText, a);
            SetAlpha(_offText, a);
            SetAlpha(_minusText, a);
            SetAlpha(_plusText, a);
        }

        private static void SetAlpha(Text text, float alpha)
        {
            if (text == null) return;
            Color c = text.color;
            if (Mathf.Abs(c.a - alpha) < 0.01f) return;
            c.a = alpha;
            text.color = c;
        }

        // ---------------------------------------------------------------- 交互

        private void StepTemperature(float delta)
        {
            if (_anchor == null || _center == null) return;

            // 基准值取**回读值**而不是本地计数器 —— 否则连点三次会变成 26+3，
            // 而设备实际可能只执行到 27（命令是串行的，后两条被拒绝）。
            float current = 26f;
            TwinValue temp;
            if (_anchor.TryGetValue("SetTemp", out temp)) current = temp.Value;

            float next = Mathf.Clamp(Mathf.Round(current) + delta, 16f, 30f);
            if (Mathf.Abs(next - Mathf.Round(current)) < 0.01f) return;

            Send(CommandAction.SetTemperature, next);
        }

        /// <summary>
        /// 「开 / 关」按钮的统一入口 —— 动作名从设备模型里取，不写死。
        ///
        /// 【为什么必须这样】门只有 `SetOpen` 一个动作。写死 `SetPower` 的话，
        /// 门点「开」会被命令中心以「不支持」拒掉（`TrySend` 里 `FindAction` 返回 null），
        /// 而界面上只是闪一下红字 —— 看起来像"门的控制没做"。
        ///
        /// 拿不到主开关动作就退回 `SetPower`：宁可让命令中心明确拒绝并写清原因，
        /// 也不要在这里悄悄什么都不做。
        /// </summary>
        private void SendSwitch(bool on)
        {
            CommandAction action = CommandAction.SetPower;

            TwinActionDefinition def = _model != null ? _model.PrimarySwitchAction : null;
            if (def != null)
            {
                CommandAction parsed;
                if (CommandText.TryParseAction(def.Name, out parsed)) action = parsed;
            }

            Send(action, on ? 1f : 0f);
        }

        private void Send(CommandAction action, float value)
        {
            if (_anchor == null || _center == null) return;

            // 【P3】单台座位关机：先播关机视频，播完再真正断电。
            //
            // 「全部关机」走 `BeginShutdown`，屏幕会播 Windows 关机画面；
            // 单点一台如果直接发 `SetPower(0)`，就是"啪一下黑掉"，
            // 两套观感不一致（主理人 2026-11-01 要求"单台也要一样"）。
            //
            // 放在这里而不是命令中心：命令中心是同步接口，塞不下"等视频播完"；
            // 而且"关机要不要播动画"是表现层的决定，不该污染命令语义。
            if (action == CommandAction.SetPower && value < 0.5f &&
                _anchor.TwinId.StartsWith("ClassComputer/PC/Seat_", StringComparison.Ordinal))
            {
                SchoolTwin.Present.TwinDevicePresenter pres =
                    SchoolTwin.Present.TwinDevicePresenter.Instance;
                if (pres != null)
                {
                    string id = _anchor.TwinId;
                    pres.BeginSeatShutdown(id, () => SendNow(id, action, value));
                    return;
                }
            }

            SendNow(_anchor.TwinId, action, value);
        }

        private void SendNow(string twinId, CommandAction action, float value)
        {
            if (_center == null) return;

            string reject;
            if (!_center.TrySend(twinId, action, value, out reject))
            {
                _rejectText = string.IsNullOrEmpty(reject) ? "操作被拒绝" : reject;
                _rejectUntil = Time.unscaledTime + 3f;
            }
            Refresh();
        }

        /// <summary>卡片重建时调用。节点会跟着父节点一起销毁，这里只负责清引用。</summary>
        public void Detach()
        {
            _anchor = null;
            _model = null;
            _center = null;
            _root = null;
            _onBg = null; _offBg = null; _minusBg = null; _plusBg = null;
            _onText = null; _offText = null; _minusText = null; _plusText = null;
            _status = null; _tempLabel = null; _tempValue = null;
            _onBtn = null; _offBtn = null; _minusBtn = null; _plusBtn = null;
        }
    }
}
