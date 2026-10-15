using System;
using UnityEngine;
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

        private void Awake()
        {
            if (_camera == null) _camera = GetComponent<Camera>();
            if (_camera == null) _camera = Camera.main;
        }

        private void Update()
        {
            if (Input.GetMouseButtonDown(0)) Pick();

            if (_clearSelectionOnEsc && Input.GetKeyDown(KeyCode.Escape)) Select(null);
        }

        private void Pick()
        {
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
            if (Selected == anchor) return;
            Selected = anchor;

            TwinPicker picker = FindObjectOfType<TwinPicker>();
            if (picker != null) picker.RecomputeBounds();

            if (SelectionChanged != null) SelectionChanged(anchor);
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
