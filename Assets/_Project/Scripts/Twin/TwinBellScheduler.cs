using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using SchoolTwin.Command;
using SchoolTwin.Twin;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 上课铃课表：到点自动打铃。
    ///
    /// ── 这一层负责什么、不负责什么 ──────────────────────────────────
    /// 负责：**决定什么时候响**。
    /// 不负责：响多久、怎么响、三维里谁闪、声音从哪来。
    ///   响多久  → `MockDeviceAgent._bellRingSeconds`（设备侧，3 秒后自己复位）
    ///   谁闪/出声 → `TwinDevicePresenter.SyncBell`（表现层，只读 `Ring` 这个值）
    ///
    /// 这条分界不是洁癖。真实校园里打铃器自带时钟，到点自己响，上位机只是**读**它的状态。
    /// 所以「定时」这件事必须落在设备侧（`RingNow`），不能发一条 `twin/cmd/...` ——
    /// 否则将来接真实网关时，网关会收到一条没人按过的命令，而真实打铃器根本不接受外部定时。
    /// 手动打铃才是命令，走 `TwinCommandCenter`。
    ///
    /// ── 为什么用 DateTime.Now 而不是 Time.time ────────────────────
    /// 课表是**钟表时间**（08:00 上课），不是"游戏开始后第几秒"。
    /// 用 Time.time 的话，按 Play 的时刻就成了零点，课表永远对不上。
    ///
    /// ── 边界：只在「跨分钟」的那一下触发 ──────────────────────────
    /// 每帧比对 `HH:mm` 的整数戳。只有戳**变了**才检查课表 ——
    /// 否则同一分钟内会连发几十次（帧率多高就发多少次）。
    /// 按 Play 时把当前分钟记为「已处理」，所以不会一进来就补打一次已经过去的铃。
    /// </summary>
    [AddComponentMenu("SchoolTwin/上课铃课表 (TwinBellScheduler)")]
    public sealed class TwinBellScheduler : MonoBehaviour
    {
        /// <summary>打铃器点位。场景里挂在走廊中段那台 `Alarm01` 上（见 TwinControlScope 的注释）。</summary>
        public const string BellTwinId = "Corridor1/Bell/Speaker_01";

        [Tooltip("关掉 = 只保留手动打铃（面板上的「打铃」按钮仍然有效）")]
        [SerializeField] private bool _autoRing = true;

        [Tooltip("课表：每行一个 HH:mm。支持 # 开头的注释行。留空则用代码里的默认作息表")]
        [SerializeField]
        private string[] _schedule =
        {
            // 上午四节
            "08:00", "08:45",
            "08:55", "09:40",
            "10:00", "10:45",
            "10:55", "11:40",
            // 下午三节
            "14:00", "14:45",
            "14:55", "15:40",
            "16:00", "16:45",
        };

        [Tooltip("每次响几秒。**必须与 MockDeviceAgent 的「打铃持续秒数」一致**，否则复位时刻和声音对不上")]
        [SerializeField] private float _ringSeconds = 3f;

        [Tooltip("排查用：每次检查课表都打日志（默认只打「下一次什么时候响」）")]
        [SerializeField] private bool _verbose = false;

        /// <summary>解析后的课表，单位 = 「今天第几分钟」（0~1439），升序。</summary>
        private readonly List<int> _minutes = new List<int>(32);

        /// <summary>上一次处理过的「日期+分钟」戳。跨分钟才检查课表。</summary>
        private long _lastStamp = long.MinValue;

        private bool _ready;
        private string _lastError;

        public bool AutoRing
        {
            get { return _autoRing; }
            set { _autoRing = value; }
        }

        public int BellCount { get { return _minutes.Count; } }

        // ---------------------------------------------------------------- 生命周期

        /// <summary>
        /// 场景里没有就自己挂一个。
        ///
        /// 【为什么要有这一步】99 号清单第 15 条：`TwinPerfReport` 的 F6 快捷键写了但没挂进场景，
        /// 表现就是「按 F6 永远没反应」。定时打铃是**看不见的功能** ——
        /// 没挂上不会有任何报错，只是"到点了没响"，而那时候人已经不在电脑前了。
        ///
        /// 场景里已经手工挂了一个的话（可以在 Inspector 里改课表），这里不会再挂第二个。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            TwinRuntime runtime = TwinRuntime.Ensure();
            if (runtime == null) return;
            if (runtime.GetComponent<TwinBellScheduler>() != null) return;

            // 场景里可能挂在别的物体上（用户手工摆的），先全局找一遍
            TwinBellScheduler found = UnityEngine.Object.FindObjectOfType<TwinBellScheduler>(true);
            if (found != null) return;

            runtime.gameObject.AddComponent<TwinBellScheduler>();
            Debug.Log("[TwinBell] 场景里没有 TwinBellScheduler，已在 [TwinRuntime] 上自动补一个" +
                      "（课表用代码默认值；要改课表请在场景里手工挂一个再编辑 Inspector）");
        }

        private void Awake()
        {
            BuildSchedule();
        }

        private void Start()
        {
            // 【把当前这一分钟标记为「已处理」】否则按 Play 的那一刻如果正好落在课表时间上，
            // 会立刻补打一次 —— 而现实里那节课的铃可能早就响过了。
            DateTime now = DateTime.Now;
            _lastStamp = (now.Date.Ticks / TimeSpan.TicksPerDay) * 100000L + now.Hour * 100L + now.Minute;

            _ready = true;
            LogNextBell();
        }

        private void Update()
        {
            if (!_autoRing || !_ready) return;

            DateTime now = DateTime.Now;
            // 戳 = 「第几天 × 100000 + HH × 100 + mm」。
            // 带上天数是为了跨天自然重置 —— 只写 HH*100+mm 的话，过了零点戳会变小，
            // 第二天同一分钟会被判成"同一个戳"，那一分钟的铃就永远不响了。
            long stamp = (now.Date.Ticks / TimeSpan.TicksPerDay) * 100000L + now.Hour * 100L + now.Minute;

            if (stamp == _lastStamp) return;
            _lastStamp = stamp;

            int minuteOfDay = now.Hour * 60 + now.Minute;
            if (!_minutes.Contains(minuteOfDay))
            {
                if (_verbose) Debug.Log("[TwinBell] " + now.ToString("HH:mm") + " 不在课表里");
                return;
            }

            Ring(now);
        }

        // ---------------------------------------------------------------- 打铃

        /// <summary>触发一次打铃。课表到点、以及 ContextMenu 的手动测试都走这里。</summary>
        public void Ring(DateTime now)
        {
            MockDeviceAgent agent = MockDeviceAgent.Instance;
            if (agent == null)
            {
                // 没设备代理 = 现在跑的不是 Mock 链路（比如真实 MQTT）。
                // 这时候不该由本组件直接改状态 —— 让网关那边自己去响。
                WarnOnce("设备代理不在（MockDeviceAgent.Instance 为空），定时打铃没有触发。" +
                         "如果现在用的是真实 MQTT 网关，这是正常的：打铃应该由网关侧时钟负责。");
                return;
            }

            agent.RingNow(BellTwinId, _ringSeconds);
            Debug.Log("[TwinBell] " + now.ToString("HH:mm") + " 到点打铃 → " + BellTwinId +
                      "（课表共 " + _minutes.Count + " 个时间点）");
        }

        // ---------------------------------------------------------------- 课表

        private void BuildSchedule()
        {
            _minutes.Clear();

            if (_schedule == null || _schedule.Length == 0)
            {
                Debug.LogWarning("[TwinBell] 课表是空的 —— 不会自动打铃（手动打铃仍然可用）");
                return;
            }

            for (int i = 0; i < _schedule.Length; i++)
            {
                string raw = _schedule[i];
                if (string.IsNullOrEmpty(raw)) continue;

                string s = raw.Trim();
                if (s.Length == 0 || s[0] == '#') continue;

                int h, m;
                if (!TryParseHhMm(s, out h, out m))
                {
                    Debug.LogWarning("[TwinBell] 课表第 " + (i + 1) + " 行「" + raw +
                                     "」不是 HH:mm 格式，已跳过");
                    continue;
                }

                int minuteOfDay = h * 60 + m;
                if (!_minutes.Contains(minuteOfDay)) _minutes.Add(minuteOfDay);
            }

            _minutes.Sort();

            StringBuilder sb = new StringBuilder(128);
            for (int i = 0; i < _minutes.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(FormatMinute(_minutes[i]));
            }
            Debug.Log("[TwinBell] 课表 " + _minutes.Count + " 个时间点：" + sb);
        }

        private static bool TryParseHhMm(string s, out int h, out int m)
        {
            h = m = 0;
            int colon = s.IndexOf(':');
            if (colon <= 0 || colon >= s.Length - 1) return false;

            if (!int.TryParse(s.Substring(0, colon), out h)) return false;
            if (!int.TryParse(s.Substring(colon + 1), out m)) return false;

            return h >= 0 && h <= 23 && m >= 0 && m <= 59;
        }

        private static string FormatMinute(int minuteOfDay)
        {
            return (minuteOfDay / 60).ToString("00") + ":" + (minuteOfDay % 60).ToString("00");
        }

        /// <summary>
        /// 打一条「下一次什么时候响」。
        ///
        /// 【为什么这条日志必须存在】定时打铃是唯一一个"错了也不会立刻发现"的功能：
        /// 课表解析失败、时间格式写错、设备代理不在 —— 全都表现为"到点了没响"，
        /// 而那时候人不在旁边。开机就把下一次时间说出来，问题当场就能看出来。
        /// </summary>
        private void LogNextBell()
        {
            if (_minutes.Count == 0)
            {
                Debug.Log("[TwinBell] 课表为空，不会自动打铃");
                return;
            }

            DateTime now = DateTime.Now;
            int nowMinute = now.Hour * 60 + now.Minute;

            int next = -1;
            for (int i = 0; i < _minutes.Count; i++)
            {
                if (_minutes[i] > nowMinute) { next = _minutes[i]; break; }
            }

            if (next < 0)
            {
                Debug.Log("[TwinBell] 今天的 " + _minutes.Count + " 个铃都已经过了（现在 " +
                          now.ToString("HH:mm") + "），下一次是明天 " + FormatMinute(_minutes[0]));
                return;
            }

            Debug.Log("[TwinBell] 现在 " + now.ToString("HH:mm") + "，下一次打铃 " +
                      FormatMinute(next) + "（还有 " + (next - nowMinute) + " 分钟）。" +
                      "想立刻听一遍：选中走廊的「校园广播打铃器」，点卡片上的「打铃」。");
        }

        private void WarnOnce(string msg)
        {
            if (string.Equals(_lastError, msg, StringComparison.Ordinal)) return;
            _lastError = msg;
            Debug.LogWarning("[TwinBell] " + msg + "（此提示只出现一次）");
        }

        // ---------------------------------------------------------------- 菜单

        [ContextMenu("立即打铃（不等课表，用来验证链路）")]
        private void RingNowMenu()
        {
            if (_minutes.Count == 0) BuildSchedule();
            Ring(DateTime.Now);
        }

        [ContextMenu("打印课表与下一次时间")]
        private void PrintScheduleMenu()
        {
            BuildSchedule();
            LogNextBell();
        }
    }
}
