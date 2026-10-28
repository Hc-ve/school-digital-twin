using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using SchoolTwin.Twin;

namespace SchoolTwin.Interaction
{
    /// <summary>
    /// 漫游相机控制器（适配旧版 Input Manager，不依赖任何 Input Axis 配置）。
    ///
    /// 操作：
    ///   W A S D    前后左右（方向键同效）
    ///   Q E        下降 / 上升
    ///   Shift      加速（×3）    Alt  减速（×0.25）
    ///   右键拖动   旋转视角
    ///   滚轮       沿视线推进 / 后退
    ///   数字键 1-9 跳转到预设机位（用于性能基线的固定机位复测）
    ///
    /// 刻意不用 Input.GetAxis("Mouse X")：那依赖 Input Manager 的轴配置，
    /// 一旦轴被改过就抛异常。这里直接读 mousePosition 求差分，零依赖。
    /// </summary>
    [AddComponentMenu("SchoolTwin/漫游相机 (FreeCameraController)")]
    public class FreeCameraController : MonoBehaviour
    {
        [System.Serializable]
        public class Viewpoint
        {
            public string Name = "机位";
            public Transform Anchor;
            public Vector3 Position;
            public Vector3 Euler;
        }

        [Header("移动")]
        [SerializeField] private float _moveSpeed = 8f;
        [SerializeField] private float _sprintMultiplier = 3f;
        [SerializeField] private float _slowMultiplier = 0.25f;
        [Tooltip("越大越跟手，越小越飘")]
        [SerializeField] private float _acceleration = 12f;
        [SerializeField] private bool _lockVerticalMovement = false;

        [Header("旋转")]
        [SerializeField] private bool _requireRightMouse = true;
        [SerializeField] private float _rotateSensitivity = 0.18f;
        [SerializeField] private float _minPitch = -85f;
        [SerializeField] private float _maxPitch = 85f;

        [Header("缩放")]
        [SerializeField] private float _zoomSpeed = 3f;
        [SerializeField] private float _zoomMinDistance = 0.5f;

        [Header("预设机位（数字键 1-9）")]
        [SerializeField] private List<Viewpoint> _viewpoints = new List<Viewpoint>();
        [SerializeField] private float _transitionSeconds = 0.8f;

        private float _yaw;
        private float _pitch;
        private Vector3 _velocity;
        private Vector3 _lastMousePosition;
        private bool _transitioning;
        private Coroutine _transition;

        /// <summary>算「房间在哪边」时的临时缓冲（GetByZone 会往里写），预分配避免点击产生 GC。</summary>
        private readonly List<TwinAnchor> _zoneBuf = new List<TwinAnchor>(16);

        public bool IsTransitioning { get { return _transitioning; } }
        public int ViewpointCount { get { return _viewpoints == null ? 0 : _viewpoints.Count; } }

        private void Start()
        {
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
            _lastMousePosition = Input.mousePosition;
        }

        private void Update()
        {
            if (_transitioning)
            {
                _lastMousePosition = Input.mousePosition;
                return;
            }

            HandleRotation();
            HandleMovement();
            HandleZoom();
            HandleViewpointKeys();
        }

        // ---------------------------------------------------------------- 输入

        private void HandleRotation()
        {
            Vector3 mouse = Input.mousePosition;
            bool allowed = !_requireRightMouse || Input.GetMouseButton(1);

            if (allowed)
            {
                Vector3 delta = mouse - _lastMousePosition;
                if (delta.sqrMagnitude > 0.0001f)
                {
                    _yaw += delta.x * _rotateSensitivity;
                    _pitch -= delta.y * _rotateSensitivity;
                    _pitch = Mathf.Clamp(_pitch, _minPitch, _maxPitch);
                    transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
                }
            }
            _lastMousePosition = mouse;
        }

        private void HandleMovement()
        {
            float x = 0f;
            float y = 0f;
            float z = 0f;

            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) x -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) z -= 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) z += 1f;
            if (Input.GetKey(KeyCode.Q)) y -= 1f;
            if (Input.GetKey(KeyCode.E)) y += 1f;

            Vector3 direction = transform.right * x + transform.forward * z;
            if (!_lockVerticalMovement) direction += Vector3.up * y;
            if (direction.sqrMagnitude > 1f) direction.Normalize();

            float speed = _moveSpeed;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) speed *= _sprintMultiplier;
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) speed *= _slowMultiplier;

            // 与帧率无关的指数平滑，避免高帧率下“过冲”
            Vector3 targetVelocity = direction * speed;
            _velocity = Vector3.Lerp(_velocity, targetVelocity, 1f - Mathf.Exp(-_acceleration * Time.deltaTime));
            transform.position += _velocity * Time.deltaTime;
        }

        private void HandleZoom()
        {
            // 指针压在界面上时，滚轮归界面。
            //
            // 【为什么必须挡】相机读的是 Input.mouseScrollDelta，**不经过事件系统**，
            // 而 UGUI 的 ScrollRect 也是直接读滚轮。两边各读各的，谁也不会「消费」掉事件
            // —— 于是在点位清单上滚一下，列表往下滚的同时相机也往前推了一段。
            // TwinPicker 早就有同样的判断（IsPointerOverGameObject），这里补上。
            EventSystem events = EventSystem.current;
            if (events != null && events.IsPointerOverGameObject()) return;

            // 【P27】交互菜单打开时滚轮归菜单（上下切换选中项），自由相机不推进。
            // 自由相机模式下面板本身不弹（驱动器里有 _freeCam.enabled 检查），
            // 这里是双保险 —— 比如菜单淡出动画还没放完的瞬间。
            // 【P28】总控抽屉打开时同理
            if (SchoolTwin.UI.TwinInteractMenu.IsBlocking) return;
            if (SchoolTwin.UI.TwinUIState.BlocksScroll) return;

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) < 0.0001f) return;

            float distance = Mathf.Max(_zoomMinDistance, _zoomSpeed) * scroll;
            transform.position += transform.forward * distance;
        }

        private void HandleViewpointKeys()
        {
            if (_viewpoints == null || _viewpoints.Count == 0) return;

            for (int i = 0; i < 9 && i < _viewpoints.Count; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) ApplyViewpoint(i);
            }
        }

        // ---------------------------------------------------------------- 对外 API

        /// <summary>平滑跳转到第 index 个预设机位。</summary>
        public void ApplyViewpoint(int index)
        {
            if (_viewpoints == null || index < 0 || index >= _viewpoints.Count) return;
            if (_transition != null) StopCoroutine(_transition);
            _transition = StartCoroutine(TransitionTo(_viewpoints[index]));
        }

        public string GetViewpointName(int index)
        {
            if (_viewpoints == null || index < 0 || index >= _viewpoints.Count) return "";
            return _viewpoints[index].Name;
        }

        /// <summary>把当前位姿采集为一个机位（P0 性能基线用的三个机位就是这么来的）。</summary>
        public Viewpoint CaptureCurrent(string name)
        {
            Viewpoint vp = new Viewpoint();
            vp.Name = name;
            vp.Position = transform.position;
            vp.Euler = transform.eulerAngles;
            return vp;
        }

        /// <summary>
        /// 平滑飞到目标附近并看向它 —— 供告警列表 / 点位清单「点击定位」使用。
        /// 刻意复用已有的 TransitionTo 协程，不引入第二套运动逻辑，
        /// 这样缓动曲线、与手动操作的互斥（_transitioning）都自动一致。
        ///
        /// ── 退开方向不能用「当前相机方向」（踩过）─────────────────────
        /// 第一版是 `position = target + normalize(相机 - 目标) * distance`，
        /// 即「从当前相机这边退开」。相机在别的房间、隔着墙的时候，
        /// 这个方向就是「穿墙过去」，落点常常在墙体内部或另一侧 ——
        /// 表现为**画面贴着一片灰墙，被定位的东西根本不在视野里**。
        ///
        /// 现在改成**从本房间的内部退开**：用同一分区内其它点位的重心当「房间在哪边」，
        /// 相机落在「目标 → 房间中心」这条线的延长线上，一定在房间里面。
        ///
        /// ── 相机高度也不能简单往上抬 ─────────────────────────────────
        /// 原来固定 `+ distance * 0.3`（6 米就抬 1.8 米）。天花板上的空调/灯具
        /// 本身就在 y≈6.1~6.9，再抬就顶出天花板了 —— 又变成看一片墙。
        /// 现在把相机高度夹在「本房间最低点位 ~ 最高点位」之间，
        /// 挂在天花板上的就在它下方一点看，落在地面上的就稍微抬高一点。
        /// </summary>
        public void FocusOn(Transform target, float distance = 6f)
        {
            if (target == null) return;
            FocusOn(target, distance, target.position);
        }

        /// <summary>
        /// 带**落点覆盖**的版本：`target` 只用来算「房间在哪边、退多远、相机多高」，
        /// 镜头真正看向 `focusPoint`。
        ///
        /// 【为什么需要它（2026-10-22）】
        /// 补灯的三个点位，锚点留在标记牌上（数据在牌子上），而牌子挂在墙中间、灯在天花板。
        /// 只按锚点飞，镜头对着墙中间 —— 用户看到的就是「还是一块牌子」，会以为灯没做。
        /// 把落点换成 `VisualTarget`（真实设备）之后，镜头会抬起来看那盏灯。
        ///
        /// 房间朝向仍然按 `target`（锚点）算，因为只有锚点知道自己属于哪个分区；
        /// 拿灯去算会走到「分区里没有点位」的兜底分支。
        /// </summary>
        public void FocusOn(Transform target, float distance, Vector3 focusPoint)
        {
            if (target == null) return;

            distance = Mathf.Max(0.5f, distance);

            Vector3 focus = focusPoint;
            Vector3 dir;
            float camY;
            float d = distance;
            ResolveApproach(target, focus, ref d, out dir, out camY);

            Viewpoint vp = new Viewpoint();
            vp.Name = "定位：" + target.name;
            vp.Anchor = null;
            vp.Position = new Vector3(focus.x + dir.x * d, camY, focus.z + dir.z * d);
            vp.Euler = Quaternion.LookRotation(focus - vp.Position, Vector3.up).eulerAngles;

            if (_transition != null) StopCoroutine(_transition);
            _transition = StartCoroutine(TransitionTo(vp));
        }

        /// <summary>
        /// 算出「从哪边靠近目标」、「退多远」以及「相机该多高」。
        ///
        /// 方向优先级：
        ///   ① 同一分区内其它点位的重心 → 那个方向一定在房间里；
        ///   ② 分区里只有这一个点位时，用全部点位的重心（楼中心）当「内侧」；
        ///   ③ 都拿不到就退回「从当前相机方向水平退开」；
        ///   ④ 还不行就朝世界 +Z（只要不是零向量，LookRotation 就不会出问题）。
        ///
        /// 距离也会被本房间的「半径」夹住 —— 6 米对教室够，对主任室那种小房间就穿墙了。
        /// </summary>
        private void ResolveApproach(Transform target, Vector3 focus, ref float distance,
                                     out Vector3 dir, out float camY)
        {
            dir = Vector3.zero;
            camY = focus.y;

            float minY = focus.y;
            float maxY = focus.y;

            TwinAnchor anchor = target.GetComponentInParent<TwinAnchor>();
            TwinRuntime runtime = TwinRuntime.Instance;
            TwinRegistry registry = runtime != null ? runtime.Registry : null;

            if (anchor != null && registry != null)
            {
                registry.GetByZone(anchor.Zone, _zoneBuf);   // 内部会先 Clear

                Vector3 sum = Vector3.zero;
                int n = 0;
                for (int i = 0; i < _zoneBuf.Count; i++)
                {
                    TwinAnchor a = _zoneBuf[i];
                    if (a == null) continue;

                    Vector3 p = a.transform.position;
                    sum += p;
                    n++;
                    if (p.y < minY) minY = p.y;
                    if (p.y > maxY) maxY = p.y;
                }

                if (n > 0)
                {
                    Vector3 center = sum / n;
                    dir = new Vector3(center.x - focus.x, 0f, center.z - focus.z);

                    // 房间半径 = 重心到最远点位的水平距离。退开距离不许超过它太多，
                    // 否则小房间（主任室、教研室）会被推到墙外面去。
                    float radius = 0f;
                    for (int i = 0; i < _zoneBuf.Count; i++)
                    {
                        TwinAnchor a = _zoneBuf[i];
                        if (a == null) continue;
                        Vector3 p = a.transform.position;
                        Vector2 flat = new Vector2(p.x - center.x, p.z - center.z);
                        if (flat.sqrMagnitude > radius) radius = flat.sqrMagnitude;
                    }
                    radius = Mathf.Sqrt(radius);

                    float maxD = Mathf.Max(2.5f, radius * 1.3f);
                    if (distance > maxD) distance = maxD;
                }

                // 目标本身就在房间中央（重心≈目标）或分区里只有它一个 → 用楼中心兜底
                if (dir.sqrMagnitude < 0.25f)
                {
                    IReadOnlyList<TwinAnchor> all = registry.All;
                    Vector3 sumAll = Vector3.zero;
                    int nAll = 0;
                    for (int i = 0; i < all.Count; i++)
                    {
                        TwinAnchor a = all[i];
                        if (a == null) continue;
                        sumAll += a.transform.position;
                        nAll++;
                    }
                    if (nAll > 1)
                    {
                        Vector3 center = sumAll / nAll;
                        dir = new Vector3(center.x - focus.x, 0f, center.z - focus.z);
                    }
                }
            }

            if (dir.sqrMagnitude < 0.25f)
            {
                dir = transform.position - focus;
                dir.y = 0f;
            }
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            dir.Normalize();

            // 相机高度：比目标略高 0.35（看门/暖气时像人站着），
            // 但夹在「本房间最低 ~ 最高点位」之间，避免顶穿天花板。
            // 分区里只有一个点位时上下限会打架（lo > hi），那时就别夹了，跟目标同高。
            float lo = minY + 0.5f;
            float hi = maxY - 0.2f;
            camY = hi > lo ? Mathf.Clamp(focus.y + 0.35f, lo, hi) : focus.y;
        }

        private IEnumerator TransitionTo(Viewpoint vp)
        {
            if (vp == null) yield break;

            _transitioning = true;
            _velocity = Vector3.zero;

            Vector3 fromPosition = transform.position;
            Quaternion fromRotation = transform.rotation;

            Vector3 toPosition = vp.Anchor != null ? vp.Anchor.position : vp.Position;
            Quaternion toRotation = vp.Anchor != null ? vp.Anchor.rotation : Quaternion.Euler(vp.Euler);

            float t = 0f;
            float duration = Mathf.Max(0.05f, _transitionSeconds);
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                float s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                transform.position = Vector3.Lerp(fromPosition, toPosition, s);
                transform.rotation = Quaternion.Slerp(fromRotation, toRotation, s);
                yield return null;
            }

            transform.position = toPosition;
            transform.rotation = toRotation;
            SyncYawPitchFromTransform();

            _transitioning = false;
            _transition = null;
        }

        private void SyncYawPitchFromTransform()
        {
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

        private void OnDrawGizmosSelected()
        {
            if (_viewpoints == null) return;

            for (int i = 0; i < _viewpoints.Count; i++)
            {
                Viewpoint vp = _viewpoints[i];
                if (vp == null) continue;

                Vector3 position = vp.Anchor != null ? vp.Anchor.position : vp.Position;
                Quaternion rotation = vp.Anchor != null ? vp.Anchor.rotation : Quaternion.Euler(vp.Euler);

                Gizmos.color = new Color(0.2f, 0.75f, 1f, 0.9f);
                Gizmos.DrawWireSphere(position, 0.6f);
                Gizmos.DrawRay(position, rotation * Vector3.forward * 3f);
            }
        }
    }
}
