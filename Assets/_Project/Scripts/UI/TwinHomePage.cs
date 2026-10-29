using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Core;      // TwinStatus 在 Core，不在 Twin（踩过：CS0246/CS0103）
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】L1 总控面板（收纳面板第一层）。
    ///
    /// ── 2026-10-29 修正：这一层只放"入口"，不放 5 个模块 ──────────────
    /// 第一版把「数据监测/设备管理/电力管理/告警中心/数据统计」直接平铺在这一层，
    /// 等于把中间那层"校园管理系统"跳过了 —— 不符合用户原始诉求：
    /// ```
    /// 点总控钮 → 总控面板 → 点「校园管理系统」→ 管理系统面板（内含 5 个模块）
    /// ```
    /// 现在这一层只有两个格子：校园管理系统（主入口）+ 天气系统（预留）。
    /// 5 个模块在 `TwinSystemPage` 里以左侧 Tab 的形式展开。
    ///
    /// ── 为什么上面还留三张概览卡 ────────────────────────────────────
    /// 调研结论「总控只放入口」说的是**功能入口**，不是"什么信息都不给"。
    /// 三个数字（设备/在线/告警）回答的是"现在系统正不正常"，
    /// 不用进任何页面就能看到，这是总控面板该有的价值。
    /// </summary>
    public sealed class TwinHomePage : TwinPageBase
    {
        public override string Title { get { return "总控面板"; } }
        public override string Subtitle { get { return "1号教学楼 · 室内 + 室外路灯"; } }

        /// <summary>点「校园管理系统」时回调（抽屉负责 Push 二级页）。</summary>
        public Action OnOpenSystem;

        private TwinModuleGrid _grid;
        private Text _statDevice;
        private Text _statOnline;
        private Text _statAlarm;
        private float _nextRefresh;
        private bool _built;

        public TwinModuleGrid Grid { get { return _grid; } }

        // ---------------------------------------------------------------- 构建

        /// <summary>
        /// 在 `OnShow` 里构建，**不要**在 Push 之前构建。
        ///
        /// 上一版的 bug 就是这么来的：`BuildModules()` 在 `_stack.Push()` 之前调，
        /// 而拉伸是在 Push 里做的 —— 那一刻页面还是 RectTransform 的默认尺寸，
        /// 任何"读父节点宽度算居中"的逻辑都会算错（图标整组跑到抽屉外面）。
        /// 现在网格虽然已改成固定尺寸布局、不再量父节点，
        /// 但这个"构建时机"的约束仍然保留：OnShow 才是页面真正就位的时刻。
        /// </summary>
        public override void OnShow()
        {
            if (!_built)
            {
                _built = true;
                BuildContent();
            }
            Refresh();
            _nextRefresh = Time.unscaledTime + 0.5f;
        }

        private void BuildContent()
        {
            RectTransform root = transform as RectTransform;
            if (root == null) return;

            BuildStatRow(root);

            // 网格：固定尺寸，从概览卡下方 24 起排
            GameObject go = new GameObject("GridArea", typeof(RectTransform));
            go.transform.SetParent(root, false);
            RectTransform area = go.transform as RectTransform;
            area.anchorMin = new Vector2(0.5f, 1f);
            area.anchorMax = new Vector2(0.5f, 1f);
            area.pivot = new Vector2(0.5f, 1f);
            area.sizeDelta = TwinModuleGrid.GridSize;
            area.anchoredPosition = new Vector2(0f, -104f);

            _grid = TwinModuleGrid.Create(area);
            _grid.Picked = OnGridPicked;
            _grid.Add("system", "校园管理系统", "管", true, null)
                 // 天气：等时间系统一起做（用户 2026-10-28 要求），这里只留位
                 .Add("weather", "天气系统", "天", false, null);
            _grid.Build();
        }

        private void OnGridPicked(string key)
        {
            if (string.Equals(key, "system", StringComparison.Ordinal))
            {
                Action a = OnOpenSystem;
                if (a != null) a();
                return;
            }
            if (string.Equals(key, "weather", StringComparison.Ordinal))
            {
                TwinToast.Show("天气系统要和「时间系统」一起做，本轮只留入口");
            }
        }

        private void BuildStatRow(RectTransform root)
        {
            // 3 × 116 + 2 × 20 + 2 × 16 = 420，左右留边一致（原来 12/140/268 右边会空 36）
            _statDevice = BuildStatCard(root, 16f, "设备总数", "—", TwinTheme.TextPrimary);
            _statOnline = BuildStatCard(root, 152f, "在线", "—", TwinTheme.StatusNormal);
            _statAlarm = BuildStatCard(root, 288f, "告警", "—", TwinTheme.StatusAlarm);
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

        private void Update()
        {
            if (!_built) return;
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
