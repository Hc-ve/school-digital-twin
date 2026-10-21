using System;
using System.IO;
using UnityEngine;
using SchoolTwin.Core;
using SchoolTwin.Data;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 数据源切换器：Mock / MQTT / 回放 三选一。
    ///
    /// 【它存在的意义不是「方便」，是把架构承诺变成可点击的事实】
    /// 这个项目的核心承诺是「换数据源，场景侧一行不改」。但在此之前，要演示这件事
    /// 只能去 Inspector 里勾 / 取消勾选组件 —— 观众看到的只是「他在改设置」，
    /// 看不到「同一套场景、同一套锚点、同一套面板，换了个数据来源照样跑」。
    /// 有了这条切换条，这件事变成一次点击。
    ///
    /// 【为什么用「启停组件」而不是「自己管数据流」】
    /// `DataSourceBase` 的生命周期里已经写好了：OnEnable → 注册 + 启动线程，
    /// OnDisable → 停线程 + 注销。切换器只负责「让该开的开、该关的关」，
    /// 不去碰线程、不去碰总线 —— 少一层控制就少一处和基类抢职责的地方。
    ///
    /// 【为什么同一时刻只允许一个数据源】
    /// 两个源同时往总线里灌同一个 TwinId，锚点的值会来回跳，
    /// 而且「离线判定」会永远不成立（总有新鲜数据进来）。这不是性能问题，是语义问题。
    ///
    /// 【回放器为什么是运行时创建的】
    /// 它是个可选数据源（没有录像时完全用不上），为了它去改场景、让每个打开工程的人
    /// 都多看到一个可能报错的组件，不划算。点「回放」时才建，且建出来先保持关闭。
    /// </summary>
    [AddComponentMenu("SchoolTwin/数据源切换器 (TwinSourceSwitcher)")]
    public class TwinSourceSwitcher : MonoBehaviour
    {
        public enum SourceKind
        {
            Mock = 0,
            Mqtt = 1,
            Playback = 2,
        }

        [Tooltip("Mock 数据源。留空则在场景里自动查找（包括未激活的）")]
        [SerializeField] private MockDataSource _mock;

        [Tooltip("MQTT 数据源。留空则自动查找")]
        [SerializeField] private MqttDataSource _mqtt;

        [Tooltip("录制器（可选）。留空则自动查找；找不到就只是没有录制按钮")]
        [SerializeField] private TwinRecorder _recorder;

        private TwinPlayer _player;
        private SourceKind _current = SourceKind.Mqtt;
        private bool _ready;
        private bool _hasRecording;
        private float _nextRecordingCheck;

        public SourceKind CurrentKind { get { EnsureReady(); return _current; } }

        /// <summary>当前数据源的状态（给状态灯用，省得调用方自己去场景里找组件）。</summary>
        public DataSourceState CurrentState
        {
            get
            {
                EnsureReady();
                DataSourceBase src = SourceOf(_current);
                return src != null ? src.State : DataSourceState.Idle;
            }
        }
        public bool IsRecording { get { EnsureReady(); return _recorder != null && _recorder.IsRecording; } }
        public bool HasRecorder { get { EnsureReady(); return _recorder != null; } }

        /// <summary>是否存在可回放的录像文件（带 1 秒缓存，避免每帧摸磁盘）。</summary>
        public bool HasRecording
        {
            get
            {
                EnsureReady();
                RefreshRecordingFlag(false);
                return _hasRecording;
            }
        }

        // ---------------------------------------------------------------- 状态文本

        public string StatusText
        {
            get
            {
                EnsureReady();
                DataSourceBase src = SourceOf(_current);
                if (src == null) return "未接入数据源";
                return ShortName(_current) + " · " + StateLabel(src.State);
            }
        }

        public static string ShortName(SourceKind kind)
        {
            if (kind == SourceKind.Mock) return "Mock";
            if (kind == SourceKind.Mqtt) return "MQTT";
            return "回放";
        }

        private static string StateLabel(DataSourceState state)
        {
            switch (state)
            {
                case DataSourceState.Idle: return "空闲";
                case DataSourceState.Connecting: return "连接中";
                case DataSourceState.Running: return "运行中";
                case DataSourceState.Degraded: return "降级";
                case DataSourceState.Stopped: return "已停止";
                case DataSourceState.Faulted: return "故障";
                default: return state.ToString();
            }
        }

        // ---------------------------------------------------------------- 切换

        /// <summary>
        /// 切到指定数据源。返回 false 表示切不了（组件不存在 / 没有录像），调用方可以据此提示。
        /// </summary>
        public bool SwitchTo(SourceKind kind)
        {
            EnsureReady();

            if (kind == SourceKind.Playback)
            {
                RefreshRecordingFlag(true);
                if (!_hasRecording) return false;   // 没有录像就没有可回放的东西
                EnsurePlayer();
            }

            DataSourceBase target = SourceOf(kind);
            if (target == null) return false;

            // 先停掉其余全部：不能出现「两个源同时灌同一个 TwinId」的中间态
            StopExcept(target);
            Activate(target);

            _current = kind;
            return true;
        }

        /// <summary>录制开关。返回切换后是否正在录制。</summary>
        public bool ToggleRecord()
        {
            EnsureReady();
            if (_recorder == null) return false;

            if (_recorder.IsRecording)
            {
                _recorder.StopRecording();
                RefreshRecordingFlag(true);   // 刚录完，回放按钮要立刻可用
                return false;
            }

            _recorder.StartRecording();
            return true;
        }

        // ---------------------------------------------------------------- 内部

        private void EnsureReady()
        {
            if (_ready) return;
            _ready = true;

            if (_mock == null) _mock = FindAny<MockDataSource>();
            if (_mqtt == null) _mqtt = FindAny<MqttDataSource>();
            if (_recorder == null) _recorder = FindAny<TwinRecorder>();

            DetectCurrent();
        }

        private static T FindAny<T>() where T : Component
        {
            T[] all = FindObjectsOfType<T>(true);   // true = 连未激活的也找（Mock 默认是关着的）
            return all != null && all.Length > 0 ? all[0] : null;
        }

        /// <summary>启动时以「真正在跑的那个」为准，而不是脚本默认值。</summary>
        private void DetectCurrent()
        {
            if (_mqtt != null && _mqtt.IsRunning) { _current = SourceKind.Mqtt; return; }
            if (_mock != null && _mock.IsRunning) { _current = SourceKind.Mock; return; }
            if (_player != null && _player.IsRunning) { _current = SourceKind.Playback; return; }
            // 都没跑：保留字段默认值，用户点哪个就起哪个
        }

        private DataSourceBase SourceOf(SourceKind kind)
        {
            if (kind == SourceKind.Mock) return _mock;
            if (kind == SourceKind.Mqtt) return _mqtt;
            return _player;
        }

        private TwinPlayer EnsurePlayer()
        {
            if (_player != null) return _player;

            // 先建成「未激活」，再挂组件 —— 这样 OnEnable 不会立刻跑。
            // 否则组件一挂上就开始回放，而此时可能连文件都还没确定，只会先报一次错。
            GameObject go = new GameObject("[TwinPlayer]");
            go.SetActive(false);
            _player = go.AddComponent<TwinPlayer>();
            return _player;
        }

        private void StopExcept(DataSourceBase keep)
        {
            Deactivate(_mock != keep ? _mock : null);
            Deactivate(_mqtt != keep ? _mqtt : null);
            Deactivate(_player != keep ? _player : null);
        }

        private void Deactivate(DataSourceBase src)
        {
            if (src == null) return;
            src.StopSource();

            // 场景里的源用 enabled 控制（OnDisable 会自己注销）；
            // 运行时建的源用 SetActive 控制（它的 GameObject 本来就是关着的）。
            if (ReferenceEquals(src, _player))
            {
                if (src.gameObject.activeSelf) src.gameObject.SetActive(false);
                return;
            }
            if (src.enabled) src.enabled = false;
        }

        private static void Activate(DataSourceBase src)
        {
            if (src == null) return;

            if (!src.gameObject.activeSelf) src.gameObject.SetActive(true);
            if (!src.enabled) src.enabled = true;
            // OnEnable 里若 _autoStart 为 false 不会自己起，这里兜一下
            if (!src.IsRunning) src.StartSource(TwinRuntime.Ensure().Sink);
        }

        /// <summary>录像文件存在性检查（1 秒缓存）。</summary>
        public void RefreshRecordingFlag(bool force)
        {
            float now = Time.unscaledTime;
            if (!force && now < _nextRecordingCheck) return;
            _nextRecordingCheck = now + 1f;

            bool found = false;
            try
            {
                string dir = RecordingReader.RecordingsDirectory();
                if (Directory.Exists(dir))
                {
                    string[] files = Directory.GetFiles(dir, "*.jsonl");
                    found = files != null && files.Length > 0;
                }
            }
            catch (Exception)
            {
                found = false;   // 目录不可读就当没有，不要让一个 IO 异常打断 UI 刷新
            }

            _hasRecording = found;
        }
    }
}
