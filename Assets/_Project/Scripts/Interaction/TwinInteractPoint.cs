using UnityEngine;

namespace SchoolTwin.Interaction
{
    /// <summary>
    /// 「交互点」标记：挂在**场景里能被走近的实体**上（比如墙上的灯开关方块），
    /// 指向真正要控制的那个点位。
    ///
    /// 【为什么不直接给开关一个 TwinAnchor】一个点位只能有一个锚点 ——
    /// 两个锚点带同一个 TwinId 会让表现层和命令中心都出现"两个设备"的错觉
    /// （P20 就为清理重复锚点专门做过工具）。所以开关用这个轻量组件做"代理"。
    ///
    /// 门的门扇本身不需要这个组件：门扇就是实物，直接按 TwinAnchor 判定。
    /// </summary>
    [AddComponentMenu("SchoolTwin/交互点 (TwinInteractPoint)")]
    public sealed class TwinInteractPoint : MonoBehaviour
    {
        [Tooltip("要控制的点位 TwinId，例如 Class2-1/Light/Main_01")]
        public string TargetTwinId;

        [Tooltip("按钮上显示的文字。留空 = 按设备类型自动取（开关门 / 开灯 / 关灯）")]
        public string Label = "";

        [Tooltip("走到多近才出现按钮（米）")]
        public float Range = 2.4f;
    }
}
