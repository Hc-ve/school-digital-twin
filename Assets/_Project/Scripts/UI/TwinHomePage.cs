using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Core;      // TwinStatus 在 Core，不在 Twin（踩过：CS0246/CS0103）
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】抽屉首页（L1）：顶部三张概览卡 + 4×3 模块图标格。
    ///
    /// ── 为什么首页不做"仪表盘" ──────────────────────────────────────
    /// 调研结论：总控只放**入口**，细节在二级（渐进披露）。
    /// 首页如果堆满图表，用户还是得先"找入口"，等于把原来的问题换个位置。
    /// 这里只留三个数字（设备/在线/告警），让人一眼知道"系统是否正常"，
    /// 然后点图标进模块。
    ///
    /// ── 模块清单 ────────────────────────────────────────────────────
    /// 5 个已实现模块 + 1 个天气预留位（灰显）。
    /// 天气要和时间系统绑在一起做（用户 2026-10-28 明确要求），
    /// 所以这里只留位置和说明，不实现内容。
    /// </summary>
    public sealed class TwinHomePage : TwinPageBase
    {
        public override string Title { get { return "校园总控"; } }
        public override string Subtitle { get { return "1号教学楼 · 室内 + 室外路灯"; } }

        private TwinModuleGrid _grid;
        private Text _statDevice;
        private Text _statOnline;
        private Text _statAlarm;
        private float _nextRefresh;

        public TwinModuleGrid Grid { get { return _grid; } }

        // ---- 模块 key → 中文名（抽屉弹提示、P2 起路由都用它）----
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "monitor", "数据监测" },
            { "device",  "设备管理" },
            { "power",   "电力管理" },
            { "alarm",   "告警中心" },
            { "stats",   "数据统计" },
            { "weather", "天气系统" },
        };

        public static string NameOf(string key)
        {
            string n;
            return Names.TryGetValue(key, out n) ? n : key;
        }

        // ---------------------------------------------------------------- 构建

        public void BuildModules(Action<string> onPick)
        {
            RectTransform root = transform as RectTransform;
            if (root == null) return;

            BuildStatRow(root);

            // 网格容器：从概览卡下方开始，占满剩余高度
            GameObject go = new GameObject("GridArea", typeof(RectTransform));
            go.transform.SetParent(root, false);
            RectTransform area = go.transform as RectTransform;
            area.anchorMin = new Vector2(0f, 0f);
            area.anchorMax = new Vector2(1f, 1f);
            area.pivot = new Vector2(0f, 1f);
            area.offsetMin = new Vector2(0f, 0f);
            area.offsetMax = new Vector2(0f, -96f);   // 让开顶部三张卡

            _grid = TwinModuleGrid.Create(area);
            _grid.Picked = onPick;

            // 注意：`Add` 的 onPick 传 null —— 回调统一走 `Picked`，
            // 否则 TwinModuleGrid 会先调条目回调再调 Picked，一次点击触发两次。
            _grid.Add("monitor", "数据监测", "监", true, null)
                 .Add("device", "设备管理", "设", true, null)
                 .Add("power", "电力管理", "电", true, null)
                 .Add("alarm", "告警中心", "警", true, null)
                 .Add("stats", "数据统计", "图", true, null)
                 // 天气：等时间系统一起做（用户 2026-10-28 要求），这里只留位
                 .Add("weather", "天气系统", "天", false, null);

            _grid.Build();
        }

        private void BuildStatRow(RectTransform root)
        {
            _statDevice = BuildStatCard(root, 12f, "设备总数", "—", TwinTheme.TextPrimary);
            _statOnline = BuildStatCard(root, 140f, "在线", "—", TwinTheme.StatusNormal);
            _statAlarm = BuildStatCard(root, 268f, "告警", "—", TwinTheme.StatusAlarm);
        }

        /// <summary>一张概览卡：标题 11 + 数字 22。返回数字的 Text 供刷新。</summary>
        private Text BuildStatCard(RectTransform root, float x, string caption, string value, Color valueColor)
        {
            TwinRoundedRect card = TwinUIFactory.Rounded(root, "Stat_" + caption, TwinTheme.R12,
                TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border, 1f, false);
            TwinUIFactory.Place(card.rectTransform, x, 12f, 116f, 72f);

            Text c = TwinUIFactory.Label(card.rectTransform, "Cap", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextSecondary);
            TwinUIFactory.Place(c.rectTransform, 10f, 8f, 96f, 14f);
            c.text = caption;

            Text v = TwinUIFactory.Label(card.rectTransform, "Val", TwinTheme.FsTitle,
                TextAnchor.LowerLeft, valueColor);
            TwinUIFactory.Place(v.rectTransform, 10f, 26f, 96f, 30f);
            v.text = value;

            return v;
        }

        // ---------------------------------------------------------------- 刷新

        public override void OnShow()
        {
            Refresh();
            _nextRefresh = Time.unscaledTime + 0.5f;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;
            Refresh();
        }

        private void Refresh()
        {
            TwinRuntime rt = TwinRuntime.Instance;
            if (rt == null || rt.Registry == null) return;

            int total = 0, online = 0, alarm = 0;
            IReadOnlyList<TwinAnchor> all = rt.Registry.All;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    TwinAnchor a = all[i];
                    if (a == null) continue;
                    total++;

                    TwinStatus s = a.StatusOfWorst();
                    if (s == TwinStatus.Alarm || s == TwinStatus.Warning) alarm++;
                    if (s != TwinStatus.Offline && s != TwinStatus.Stale) online++;
                }
            }

            SetText(_statDevice, total.ToString());
            SetText(_statOnline, online.ToString());
            SetText(_statAlarm, alarm.ToString());
        }

        private static void SetText(Text t, string v)
        {
            if (t == null) return;
            if (!string.Equals(t.text, v, StringComparison.Ordinal)) t.text = v;
        }
    }
}
