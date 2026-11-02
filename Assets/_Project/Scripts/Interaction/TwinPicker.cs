using System;
using UnityEngine;
using UnityEngine.EventSystems;
using SchoolTwin.Twin;

namespace SchoolTwin.Interaction
{
    /// <summary>
    /// 点选拾取：左键点击场景里的孪生点位，选中并高亮。
    ///
    /// 细节说明：
    ///   · 用 RaycastNonAlloc + 预分配数组，避免每次点击产生 GC；
    ///   · 高亮用 GL 画线框（不创建任何场景对象、不动材质），
    ///     这样就不会和 TwinStatusColor 的 MaterialPropertyBlock 抢同一个渲染器；
    ///   · 若运行环境取不到内置着色器，高亮静默失效，但选中逻辑与 HUD 显示仍然正常。
    /// </summary>
    [AddComponentMenu("SchoolTwin/点位拾取 (TwinPicker)")]
    public class TwinPicker : MonoBehaviour
    {
        [SerializeField] private Camera _camera;
        [SerializeField] private float _maxDistance = 800f;
        [SerializeField] private LayerMask _layerMask = ~0;
        [SerializeField] private bool _clearSelectionOnEsc = true;
        [SerializeField] private Color _highlightColor = new Color(0.20f, 0.75f, 1.00f, 1f);

        private readonly RaycastHit[] _hits = new RaycastHit[16];
        private static Material s_lineMaterial;
        private Bounds _bounds;
        private bool _hasBounds;

        public static TwinAnchor Selected { get; private set; }
        public static event Action<TwinAnchor> SelectionChanged;

        /// <summary>
        /// 「点选动作」计数。**即使选中的还是同一个点位也会 +1**。
        ///
        /// 【为什么要单独有它】<see cref="Select"/> 在「选中的没变」时会直接 return，
        /// 于是「点同一个点位」对外完全不可见。详情面板被 × 关掉之后，
        /// 用户自然会再点一次那个点位想把它叫回来 —— 没有这个计数就永远叫不回来。
        /// </summary>
        public static int PickStamp { get; private set; }

        private void Awake()
        {
            if (_camera == null) _camera = GetComponent<Camera>();
            if (_camera == null) _camera = Camera.main;

            // 【必须排除角色层】第三人称下角色就在屏幕中间，
            // 射线第一下必然打到自己身上 → 设备永远点不到，而且看起来像"点了没反应"。
            // 这里在运行时把 `TwinPlayer` 层从掩码里摘掉（不改场景里的序列化值）。
            int player = LayerMask.NameToLayer("TwinPlayer");
            if (player >= 0) _layerMask &= ~(1 << player);
        }

        private void Update()
        {
            if (Input.GetMouseButtonDown(0)) Pick();

            // 【P28】Esc 先给 UI（抽屉逐层退），UI 不要才轮到"清选中"
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (SchoolTwin.UI.TwinUIState.HandleEscape()) return;
                if (_clearSelectionOnEsc) Select(null);
            }
        }

        private void Pick()
        {
            // 指针压在 UI 上时不穿透到场景。
            // P1-C 的信息面板/告警列表背景是 raycastTarget，如果不挡一下，
            // 点面板会顺手把面板背后的三维物体选中，体验很怪。
            EventSystem events = EventSystem.current;
            if (events != null && events.IsPointerOverGameObject()) return;

            Camera cam = _camera != null ? _camera : Camera.main;
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            int count = Physics.RaycastNonAlloc(ray, _hits, _maxDistance, _layerMask, QueryTriggerInteraction.Ignore);

            TwinAnchor best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hits[i];
                if (hit.collider == null) continue;

                TwinAnchor anchor = hit.collider.GetComponentInParent<TwinAnchor>();
                if (anchor == null) continue;
                if (hit.distance < bestDistance)
                {
                    bestDistance = hit.distance;
                    best = anchor;
                }
            }

            Select(best);
        }

        public static void Select(TwinAnchor anchor)
        {
            PickStamp++;    // 先 +1：连点同一个点位也算一次「点选动作」

            // 【C-27】选中就转向 —— 放在 `Selected == anchor` 提前返回**之前**：
            // 连点同一台设备也应该重新回正镜头，否则转开之后再也转不回来。
            FaceAnchor(anchor);

            if (Selected == anchor) return;
            Selected = anchor;

            TwinPicker picker = FindObjectOfType<TwinPicker>();
            if (picker != null) picker.RecomputeBounds();

            if (SelectionChanged != null) SelectionChanged(anchor);
        }

        /// <summary>
        /// 【C-27 修正】选中设备后的**镜头定位**。
        ///
        /// ── 只在观景模式下做 ────────────────────────────────────────────
        /// 主理人 2026-10-31 裁决：镜头定位的用途是「在自由漫游里快速跳到某台设备」，
        /// 那是**观景模式**（V 键，FreeCameraController 接管）的典型场景。
        ///
        /// 第三人称 / 第一人称下**不做**，两个原因：
        ///   ① 那两个模式的镜头跟着角色走，玩家意图是"我走过去看"，不是"把镜头扭过去"；
        ///      强行转视线会打断操作，而且角色朝向没变，画面会割裂；
        ///   ② 点列表行时人可能根本不在那台设备附近，转过去多半只看到一面墙。
        ///
        /// ── 为什么用 FreeCameraController.FocusOn 而不是自己算角度 ─────────
        /// 它已经解决了"从哪个方向靠近、退多远、相机多高、避开墙"这一整套
        /// （见 ResolveApproach 的注释）。自己再写一遍只会更差。
        /// 而且观景模式下它本来就拥有相机控制权，走它的入口不会和第三人称打架。
        /// </summary>
        private static void FaceAnchor(TwinAnchor anchor)
        {
            if (anchor == null) return;

            // 全屏 L2 数据态：中间整块不透明，飞过去也看不见，纯属白折腾
            if (SchoolTwin.UI.TwinDrawer.IsFullScreen && !SchoolTwin.UI.TwinDrawer.IsFullScreenHome)
                return;

            // 只认观景模式：`enabled == false` = 第三人称/第一人称在用相机，此时不动。
            FreeCameraController free = FindObjectOfType<FreeCameraController>();
            if (free == null || !free.enabled) return;

            GameObject visual = anchor.VisualTarget;
            Transform t = visual != null ? visual.transform : anchor.transform;

            // 瞄包围盒中心而不是轴心：不少设备（吊灯、门禁）轴心在地脚或墙面，
            // 瞄轴心会转到"对着地板"或者"对着墙"。
            Vector3 aim = t.position;
            Renderer r = t.GetComponentInChildren<Renderer>(false);
            if (r != null) aim = r.bounds.center;

            free.FocusOn(anchor.transform, 6f, aim);
        }

        private void RecomputeBounds()
        {
            _hasBounds = false;
            if (Selected == null) return;

            Renderer[] renderers = Selected.GetComponentsInChildren<Renderer>(false);
            if (renderers == null || renderers.Length == 0) return;

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            _bounds = bounds;
            _hasBounds = true;
        }

        private void OnRenderObject()
        {
            if (Selected == null || !_hasBounds) return;

            Material material = GetLineMaterial();
            if (material == null) return;

            material.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            GL.Color(_highlightColor);
            DrawBox(_bounds.center, _bounds.extents);
            GL.End();
            GL.PopMatrix();
        }

        private static void DrawBox(Vector3 center, Vector3 e)
        {
            Vector3 p0 = center + new Vector3(-e.x, -e.y, -e.z);
            Vector3 p1 = center + new Vector3(e.x, -e.y, -e.z);
            Vector3 p2 = center + new Vector3(e.x, -e.y, e.z);
            Vector3 p3 = center + new Vector3(-e.x, -e.y, e.z);
            Vector3 p4 = center + new Vector3(-e.x, e.y, -e.z);
            Vector3 p5 = center + new Vector3(e.x, e.y, -e.z);
            Vector3 p6 = center + new Vector3(e.x, e.y, e.z);
            Vector3 p7 = center + new Vector3(-e.x, e.y, e.z);

            Line(p0, p1); Line(p1, p2); Line(p2, p3); Line(p3, p0);
            Line(p4, p5); Line(p5, p6); Line(p6, p7); Line(p7, p4);
            Line(p0, p4); Line(p1, p5); Line(p2, p6); Line(p3, p7);
        }

        private static void Line(Vector3 a, Vector3 b)
        {
            GL.Vertex(a);
            GL.Vertex(b);
        }

        private static Material GetLineMaterial()
        {
            if (s_lineMaterial != null) return s_lineMaterial;

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null;

            s_lineMaterial = new Material(shader);
            s_lineMaterial.hideFlags = HideFlags.HideAndDontSave;
            s_lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            s_lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            s_lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            s_lineMaterial.SetInt("_ZWrite", 0);
            return s_lineMaterial;
        }
    }
}
