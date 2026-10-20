using UnityEngine;
using SchoolTwin.Core;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 状态 → 颜色 / 文案 / 严重度 的唯一来源。
    ///
    /// 之前 TwinStatusColor 自己存了一份配色，如果面板再存一份，两边一定会漂移
    /// （改了一处忘了另一处，界面与场景颜色对不上）。后续新增任何可视化
    /// （图例、导出报表、告警邮件）都从这里取色取词。
    ///
    /// 配色口径与 TwinStatusColor 的默认值保持一致。
    /// </summary>
    public static class TwinStatusPalette
    {
        // ---------------------------------------------------------------- 状态色
        public static readonly Color Unknown = new Color(0.35f, 0.38f, 0.45f, 1f);
        public static readonly Color Normal = new Color(0.20f, 0.80f, 0.35f, 1f);
        public static readonly Color Warning = new Color(1.00f, 0.72f, 0.15f, 1f);
        public static readonly Color Alarm = new Color(0.90f, 0.20f, 0.20f, 1f);
        public static readonly Color Stale = new Color(0.50f, 0.50f, 0.55f, 1f);
        public static readonly Color Offline = new Color(0.28f, 0.28f, 0.30f, 1f);

        // ---------------------------------------------------------------- 面板配色
        public static readonly Color PanelTop = new Color(0.078f, 0.106f, 0.157f, 0.94f);
        public static readonly Color PanelBottom = new Color(0.055f, 0.075f, 0.114f, 0.96f);
        public static readonly Color PanelBorder = new Color(0.165f, 0.212f, 0.282f, 0.80f);

        public static readonly Color TextPrimary = new Color(0.94f, 0.96f, 0.99f, 1f);
        public static readonly Color TextSecondary = new Color(0.62f, 0.69f, 0.79f, 1f);
        public static readonly Color TextMuted = new Color(0.46f, 0.52f, 0.61f, 1f);

        public static readonly Color Track = new Color(1f, 1f, 1f, 0.09f);
        public static readonly Color Divider = new Color(1f, 1f, 1f, 0.10f);
        public static readonly Color RowHover = new Color(1f, 1f, 1f, 0.08f);
        public static readonly Color Transparent = new Color(1f, 1f, 1f, 0f);

        // ---------------------------------------------------------------- 查询

        public static Color Of(TwinStatus status)
        {
            switch (status)
            {
                case TwinStatus.Normal: return Normal;
                case TwinStatus.Warning: return Warning;
                case TwinStatus.Alarm: return Alarm;
                case TwinStatus.Stale: return Stale;
                case TwinStatus.Offline: return Offline;
                default: return Unknown;
            }
        }

        /// <summary>同色但指定透明度，用于胶囊底色、告警行底色这类「淡背景」。</summary>
        public static Color Of(TwinStatus status, float alpha)
        {
            Color c = Of(status);
            c.a = alpha;
            return c;
        }

        public static string Label(TwinStatus status)
        {
            switch (status)
            {
                case TwinStatus.Normal: return "正常";
                case TwinStatus.Warning: return "警告";
                case TwinStatus.Alarm: return "告警";
                case TwinStatus.Stale: return "数据陈旧";
                case TwinStatus.Offline: return "离线";
                default: return "未知";
            }
        }

        public static string QualityLabel(DataQuality quality)
        {
            switch (quality)
            {
                case DataQuality.Good: return "良好";
                case DataQuality.Uncertain: return "存疑";
                case DataQuality.Bad: return "异常";
                default: return "离线";
            }
        }

        /// <summary>是否属于「需要人关注」的状态 —— 告警列表的筛选条件。</summary>
        public static bool NeedsAttention(TwinStatus status)
        {
            return status == TwinStatus.Warning || status == TwinStatus.Alarm
                || status == TwinStatus.Stale || status == TwinStatus.Offline;
        }

        /// <summary>严重度，越大越严重（与 TwinAnchor 内部口径一致）。</summary>
        public static int Severity(TwinStatus status)
        {
            switch (status)
            {
                case TwinStatus.Offline: return 4;
                case TwinStatus.Alarm: return 3;
                case TwinStatus.Warning: return 2;
                case TwinStatus.Stale: return 1;
                // 与 TwinAnchor 内部口径保持一致：Unknown（未配置阈值）排在 Normal 之上，
                // 否则「有一个正常指标」就会把「另一个指标没配阈值」盖成绿色。
                case TwinStatus.Unknown: return 1;
                case TwinStatus.Normal: return 0;
                default: return -1;
            }
        }
    }
}
