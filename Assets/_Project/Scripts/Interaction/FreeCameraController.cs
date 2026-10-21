using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

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
        /// 平滑飞到目标附近并看向它 —— 供告警列表「点击定位」使用。
        /// 刻意复用已有的 TransitionTo 协程，不引入第二套运动逻辑，
        /// 这样缓动曲线、与手动操作的互斥（_transitioning）都自动一致。
        /// </summary>
        public void FocusOn(Transform target, float distance = 6f)
        {
            if (target == null) return;

            distance = Mathf.Max(0.5f, distance);

            // 从「相机 → 目标」的反方向退开一段距离，避免直接怼进物体内部
            Vector3 direction = transform.position - target.position;
            if (direction.sqrMagnitude < 0.0001f) direction = Vector3.forward;
            direction = direction.normalized;

            Viewpoint vp = new Viewpoint();
            vp.Name = "定位：" + target.name;
            vp.Anchor = null;
            vp.Position = target.position + direction * distance + Vector3.up * (distance * 0.3f);
            vp.Euler = Quaternion.LookRotation(target.position - vp.Position, Vector3.up).eulerAngles;

            if (_transition != null) StopCoroutine(_transition);
            _transition = StartCoroutine(TransitionTo(vp));
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
