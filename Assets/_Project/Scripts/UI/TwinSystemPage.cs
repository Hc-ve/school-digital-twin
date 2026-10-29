using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】L2 校园管理系统面板 —— 5 个功能模块在这里展开。
    ///
    /// ── 为什么用"左侧 Tab + 右侧内容"而不是再来一层图标格 ──────────────
    /// 用户原话是"管理系统分为多个功能模块**展开**设计"。
    /// "展开"意味着在同一块面板里切换，不是再点一次图标进第三层。
    /// 调研也支持这个选择：Siemens Desigo CC / EcoStruxure 都是
    /// "左侧导航树 + 右侧工作区"，运维要的是"一眼看到模块全貌 + 快速横向切换"。
    ///
    /// 图标格适合"入口少、每次只进一个"的场景（L1 总控就是这样）；
    /// 一旦进入"同一个系统的多个功能"，Tab 才是对的 —— 切换成本从 2 次点击降到 1 次。
    ///
    /// ── 为什么内容区现在是占位 ──────────────────────────────────────
    /// 5 个模块的页面在 P2~P5 逐个实现。这里先搭好壳：
    /// Tab 能切、标题描述都对、内容区有明确的"待接入"提示，
    /// 保证 P1 结束时用户能看到完整的信息架构，而不是 5 个空白页。
    /// </summary>
    public sealed class TwinSystemPage : TwinPageBase
    {
        public override string Title { get { return "校园管理系统"; } }
        public override string Subtitle { get { return "5 个功能模块 · 数据 / 设备 / 电力 / 告警 / 统计"; } }

        private const float TabW = 132f;
        private const float TabH = 52f;
        private const float TabGap = 6f;
        private const float Pad = 12f;

        private sealed class Module
        {
            public string Key;
            public string Name;
            public string Glyph;
            public string Desc;
        }

        private static readonly Module[] Modules =
        {
            new Module { Key = "monitor", Name = "数据监测", Glyph = "监",
                Desc = "全场景设备实时数据：综合概览 + 单设备详情（用电量 / 功率 / 温度…）" },
            new Module { Key = "device",  Name = "设备管理", Glyph = "设",
                Desc = "设备台账与开关控制，按「场景-位置-类型-序号」统一编号，确保无遗漏" },
            new Module { Key = "power",   Name = "电力管理", Glyph = "电",
                Desc = "分级停电：整层批量分合闸 / 单个场景（如机房）独立停电" },
            new Module { Key = "alarm",   Name = "告警中心", Glyph = "警",
                Desc = "异常精准定位 + 单台设备全链路追溯（通知 / 报文 / 日志）" },
            new Module { Key = "stats",   Name = "数据统计", Glyph = "图",
                Desc = "全量设备数据转可视化大屏统计：趋势 / 占比 / 排行" },
        };

        private readonly List<TwinButton> _tabs = new List<TwinButton>(Modules.Length);
        private RectTransform _contentHost;
        private int _selected = -1;
        private bool _built;

        /// <summary>切到某个模块（P2 起在这里换成真实页面）。</summary>
        public Action<string> OnModulePicked;

        // ---------------------------------------------------------------- 构建

        public override void OnShow()
        {
            if (_built) return;
            _built = true;
            BuildContent();
            Select(0);
        }

        private void BuildContent()
        {
            RectTransform root = transform as RectTransform;
            if (root == null) return;

            // ---- 左：Tab 列 ----
            GameObject tabGo = new GameObject("Tabs", typeof(RectTransform));
            tabGo.transform.SetParent(root, false);
            RectTransform tabs = tabGo.transform as RectTransform;
            tabs.anchorMin = new Vector2(0f, 1f);
            tabs.anchorMax = new Vector2(0f, 1f);
            tabs.pivot = new Vector2(0f, 1f);
            tabs.sizeDelta = new Vector2(TabW, 0f);
            tabs.anchoredPosition = new Vector2(Pad, -Pad);

            for (int i = 0; i < Modules.Length; i++)
            {
                int index = i;
                Module m = Modules[i];

                TwinButton tab = TwinButton.Create(tabs, m.Name, TwinButtonStyle.Row,
                    TabW, TabH, () => Select(index), "Tab_" + m.Key);
                TwinUIFactory.Place(tab.Rect, 0f, i * (TabH + TabGap), TabW, TabH);

                // Tab 里加一个小图标（行变体默认只有文字，这里补一个）
                RectTransform icon = TwinIconGlyph.Create(tab.Rect, m.Glyph, 24f,
                    TwinTheme.WithAlpha(TwinTheme.Accent, 0.14f), TwinTheme.Accent, "Icon");
                icon.anchorMin = icon.anchorMax = new Vector2(0f, 0.5f);
                icon.pivot = new Vector2(0f, 0.5f);
                icon.anchoredPosition = new Vector2(10f, 0f);

                // 文字右移，给图标让位。
                // ⚠️ 必须按名字找：图标里的汉字也是 Text，
                // `GetComponentInChildren<Text>` 一旦顺序变了就会抓成图标的字。
                Transform labelTf = tab.Rect.Find("Label");
                Text label = labelTf != null ? labelTf.GetComponent<Text>() : null;
                if (label != null)
                {
                    label.alignment = TextAnchor.MiddleLeft;
                    TwinUIFactory.Place(label.rectTransform, 42f, 0f, TabW - 50f, TabH);
                }

                _tabs.Add(tab);
            }

            // ---- 右：内容区 ----
            GameObject hostGo = new GameObject("ContentHost", typeof(RectTransform));
            hostGo.transform.SetParent(root, false);
            _contentHost = hostGo.transform as RectTransform;
            _contentHost.anchorMin = new Vector2(0f, 0f);
            _contentHost.anchorMax = new Vector2(1f, 1f);
            _contentHost.pivot = new Vector2(0f, 1f);
            _contentHost.offsetMin = new Vector2(TabW + Pad * 2f, Pad);
            _contentHost.offsetMax = new Vector2(-Pad, -Pad);

            TwinRoundedRect card = TwinUIFactory.Rounded(_contentHost, "Bg", TwinTheme.R12,
                TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border, 1f, false);
            TwinUIFactory.Stretch(card.rectTransform);
        }

        // ---------------------------------------------------------------- Tab 切换

        private void Select(int index)
        {
            if (index < 0 || index >= Modules.Length) return;
            if (_selected == index) return;

            int old = _selected;
            _selected = index;

            for (int i = 0; i < _tabs.Count; i++)
                _tabs[i].SetSelected(i == index);

            RebuildContent(Modules[index]);

            if (old >= 0)
            {
                Action<string> cb = OnModulePicked;
                if (cb != null) cb(Modules[index].Key);
            }
        }

        private void RebuildContent(Module m)
        {
            if (_contentHost == null) return;

            // 清掉旧内容（保留背景 Bg）
            for (int i = _contentHost.childCount - 1; i >= 0; i--)
            {
                Transform c = _contentHost.GetChild(i);
                if (c == null) continue;
                if (string.Equals(c.name, "Bg", StringComparison.Ordinal)) continue;
                TwinUIFactory.DestroySafe(c.gameObject);
            }

            // 模块名
            Text title = TwinUIFactory.Label(_contentHost, "ModuleTitle", TwinTheme.FsSubtitle,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(title.rectTransform, 16f, 14f, 300f, 24f);
            title.text = m.Name;

            // 描述
            Text desc = TwinUIFactory.Label(_contentHost, "ModuleDesc", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextSecondary);
            // 左右拉伸而不是固定宽度：抽屉宽度有 420→640 的 200ms 动画，
            // OnShow 那一刻量到的是 420 的宽度，写死就会永远窄 220。
            RectTransform dr = desc.rectTransform;
            dr.anchorMin = new Vector2(0f, 1f);
            dr.anchorMax = new Vector2(1f, 1f);
            dr.pivot = new Vector2(0.5f, 1f);
            dr.sizeDelta = new Vector2(-32f, 34f);
            dr.anchoredPosition = new Vector2(0f, -42f);
            desc.horizontalOverflow = HorizontalWrapMode.Wrap;
            desc.text = m.Desc;

            // 占位提示
            Text hint = TwinUIFactory.Label(_contentHost, "Hint", TwinTheme.FsBody,
                TextAnchor.MiddleCenter, TwinTheme.TextMuted);
            TwinUIFactory.Stretch(hint.rectTransform);
            hint.rectTransform.offsetMin = new Vector2(0f, 0f);
            hint.rectTransform.offsetMax = new Vector2(0f, -80f);
            hint.text = "该模块在 P2~P5 逐个接入\n（当前为信息架构占位）";
        }
    }
}
