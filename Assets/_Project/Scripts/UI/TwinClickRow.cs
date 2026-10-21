using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 可点击列表行：状态点 + 文案 + 悬停反馈 + 点击回调。
    ///
    /// 【谁在用】
    ///   · 详情卡片里的「告警与异常」区（TwinDetailPanel）
    ///   · 点位清单窗口（TwinPointListPanel）
    /// 两个面板的行长得一样、行为一样，只是点击之后干的事不同 —— 所以这里
    /// 不写死「点击 = 定位到这个点位」，而是留一个 OnClick 回调，谁建的行谁给回调。
    /// （原来这里叫 TwinAlarmRow 且直接调 Owner.FocusOn，只有告警列表能用。）
    ///
    /// 【为什么行对象要池化】
    /// 行在面板构建时一次性创建，之后只改文本与颜色。
    /// 运行期不再 new 对象，避免列表频繁刷新时产生 GC。
    /// </summary>
    [AddComponentMenu("SchoolTwin/UI/可点击行 (TwinClickRow)")]
    public class TwinClickRow : MonoBehaviour,
        IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>点击时回调，参数是本行的 Target。为空则点击无反应。</summary>
        [System.NonSerialized] public System.Action<TwinAnchor> OnClick;

        /// <summary>本行对应的点位；面板每次刷新会重写。</summary>
        [System.NonSerialized] public TwinAnchor Target;

        /// <summary>缓存的文本，内容不变就不重写 Text（重写会触发 Canvas 重排）。</summary>
        [System.NonSerialized] public string LastText;

        public Image Background;
        public Image Dot;
        public Text Label;

        /// <summary>可选的右侧标签（点位清单用来显示状态文字）。告警行不用，留空即可。</summary>
        public Text Badge;

        private Color _baseColor = new Color(1f, 1f, 1f, 0f);
        private bool _hoverEnabled = true;

        /// <summary>设置常态底色（按严重度带一点淡色），悬停时在此基础上提亮。</summary>
        public void SetBaseColor(Color color)
        {
            _baseColor = color;
            if (Background != null) Background.color = color;
        }

        /// <summary>
        /// 关掉悬停提亮。分组标题那种「不响应点击」的行用得上 ——
        /// 鼠标移上去会亮一下却不给点，看着像坏了。
        /// </summary>
        public void SetHoverEnabled(bool enabled)
        {
            _hoverEnabled = enabled;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (OnClick == null || Target == null) return;
            OnClick(Target);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!_hoverEnabled || Background == null) return;
            Background.color = Color.Lerp(_baseColor, Color.white, 0.14f);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!_hoverEnabled || Background == null) return;
            Background.color = _baseColor;
        }
    }
}
