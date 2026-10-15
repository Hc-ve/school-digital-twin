using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 告警列表的一行：状态点 + 文案 + 悬停反馈 + 点击定位。
    ///
    /// 行对象在面板构建时一次性创建，之后只改文本与颜色（池化），
    /// 运行期不再 new 对象，避免告警列表频繁刷新时产生 GC。
    /// </summary>
    [AddComponentMenu("SchoolTwin/UI/告警行 (TwinAlarmRow)")]
    public class TwinAlarmRow : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>面板反查，点击时回调。</summary>
        [System.NonSerialized] public TwinDetailPanel Owner;
        /// <summary>本行对应的点位；面板每次刷新会重写。</summary>
        [System.NonSerialized] public TwinAnchor Target;
        /// <summary>缓存的文本，内容不变就不重写 Text（重写会触发 Canvas 重排）。</summary>
        [System.NonSerialized] public string LastText;

        public Image Background;
        public Image Dot;
        public Text Label;

        private Color _baseColor = new Color(1f, 1f, 1f, 0f);

        /// <summary>设置常态底色（按严重度带一点淡色），悬停时在此基础上提亮。</summary>
        public void SetBaseColor(Color color)
        {
            _baseColor = color;
            if (Background != null) Background.color = color;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (Owner == null || Target == null) return;
            Owner.FocusOn(Target);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (Background == null) return;
            Background.color = Color.Lerp(_baseColor, Color.white, 0.14f);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (Background == null) return;
            Background.color = _baseColor;
        }
    }
}
