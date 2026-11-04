using UnityEngine;
using UnityEngine.EventSystems;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P13】表盘拖动的事件代理。
    ///
    /// 【为什么必须单独一个文件、单独一个 MonoBehaviour】
    /// Unity 只认「文件名与类名相同」的那个 MonoBehaviour 为可挂载组件。
    /// 上一版把本类写在 `TwinTimeWeatherCard.cs` 里，于是 `AddComponent&lt;TwinDialDragArea&gt;()`
    /// 在**编辑器里**能跑（脚本已编译进 dll），但 Inspector 里挂不上、打包后也可能出问题，
    /// 而且双 MonoBehaviour 同文件是同型病的一种：同一个东西在两处有定义。
    /// 拆成独立文件后文件名 = 类名，符合项目其余脚本的惯例。
    ///
    /// 【为什么事件不直接写在卡片类上】
    /// UGUI 的 pointer 事件按「射线命中的 Graphic」派发。卡片的根节点是**遮罩**，
    /// 挂在它上面的组件收不到表盘内的拖动；必须由表盘那个全透明 Block 自己接收。
    ///
    /// 【为什么 Down 也提交、Click 也提交】
    /// 触控板/某些输入设备上"按下即抬起"不产生 OnDrag，只实现 OnDrag 会出现
    /// 「点一下表盘没反应」。三次提交（Down / Drag / Click）都是幂等的：
    /// 重复设同一个小时数没有副作用，只是多转一次同一个角度。
    /// </summary>
    public class TwinDialDragArea : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        internal TwinTimeWeatherCard Owner;

        public void OnPointerDown(PointerEventData e) { Feed(e, false); }
        public void OnDrag(PointerEventData e) { Feed(e, false); }
        public void OnEndDrag(PointerEventData e) { Feed(e, true); }

        /// <summary>单击（未产生拖动）走这里提交一次，并标记拖动结束。</summary>
        public void OnPointerClick(PointerEventData e) { Feed(e, true); }

        private void Feed(PointerEventData e, bool commit)
        {
            if (Owner == null) return;

            RectTransform rt = (RectTransform)transform;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    rt, e.position, e.pressEventCamera, out local))
                return;

            Owner.OnDrag(local, commit);
        }
    }
}