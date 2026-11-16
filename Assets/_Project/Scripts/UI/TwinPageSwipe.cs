using UnityEngine;
using UnityEngine.EventSystems;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P43-UI】时间天气卡的分页滑动事件代理。
    ///
    /// 【为什么必须单独一个文件、单独一个 MonoBehaviour】
    /// 与 <see cref="TwinDialDragArea"/> 同一条理由：Unity 只认「文件名与类名相同」的
    /// 那个 MonoBehaviour 为可挂载组件；写进 TwinTimeWeatherCard.cs 里虽然编辑器能跑，
    /// 但 Inspector 挂不上、打包也可能出问题，且双 MonoBehaviour 同文件本身就是同型病。
    ///
    /// 【为什么挂在卡片根节点（_card）而不是每个页面各挂一个】
    /// UGUI 的拖动事件派发走 `ExecuteEvents.GetEventHandler&lt;IDragHandler&gt;`，
    /// 它会**沿父链向上找**第一个实现 IDragHandler 的组件。
    /// 所以：
    ///   · 在按钮上起手拖动 → 按钮不实现 IDragHandler → 冒泡到卡片根 → 触发翻页；
    ///   · 在表盘上起手拖动 → 表盘命中区自己实现了 IDragHandler → 表盘优先，不会误翻页。
    /// 这正是想要的优先级。挂在每个页面上做不到这一点（页面是兄弟节点，不互相冒泡）。
    ///
    /// 【为什么挂在 _card 上、不挂在最外层遮罩上】
    /// 遮罩上已有 Button（点空白关闭）。若把拖动也加到遮罩：
    /// Unity 的 Button 只要「按下与抬起都在同一目标上」就触发 onClick，
    /// 于是"在卡片外拖一下再松手"会被判成点击 → 卡片直接关掉。挂在卡片根上不会。
    /// </summary>
    public class TwinPageSwipe : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        internal TwinTimeWeatherCard Owner;

        private bool _active;

        public void OnBeginDrag(PointerEventData e)
        {
            if (Owner == null) return;
            if (e.button != PointerEventData.InputButton.Left) return;
            _active = true;
            Owner.BeginSwipe(e.position);
        }

        public void OnDrag(PointerEventData e)
        {
            if (!_active || Owner == null) return;
            Owner.UpdateSwipe(e.position);
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (!_active || Owner == null) return;
            _active = false;
            Owner.EndSwipe(e.position);
        }
    }
}