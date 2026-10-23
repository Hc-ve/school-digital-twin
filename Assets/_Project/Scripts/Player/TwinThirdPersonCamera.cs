using UnityEngine;
using SchoolTwin.Interaction;

namespace SchoolTwin.Player
{
    /// <summary>
    /// 第三人称相机：弹簧臂 + 碰撞缩短 + 贴太近时隐藏角色 + F5 切第一人称。
    ///
    /// ── 为什么必须做碰撞缩短 ──────────────────────────────────────
    /// 这个场景层高 3.47m、2F 走廊净宽约 5m。相机挂在 2.2m 高、后拉 3m，
    /// 一转身就会插进墙里，画面直接穿模（不是「有点丑」，是整块屏幕变成墙的背面）。
    /// 所以每帧从角色肩部往相机方向发一条 SphereCast，撞到就把吊臂缩到命中点前面。
    ///
    /// ── 操作 ────────────────────────────────────────────────────
    /// 按住**鼠标右键**转视角（和现有漫游一致，且不会和「点设备」抢左键）。
    /// 滚轮调距离。F5 切第一/第三人称。
    ///
    /// ── 为什么不用 Cursor.lockState ─────────────────────────────
    /// 这个演示要**用鼠标点场景里的设备**，锁住光标就没法点了。所以视角只在右键按住时转。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TwinThirdPersonCamera : MonoBehaviour
    {
        [Header("目标")]
        [Tooltip("角色根节点。留空会自动找场景里的 TwinPlayerController")]
        [SerializeField] private Transform _target;

        [Tooltip("相机看向的点（相对角色）：1.55 ≈ 成人肩部")]
        [SerializeField] private Vector3 _pivotOffset = new Vector3(0f, 1.55f, 0f);

        [Tooltip("贴太近时隐藏角色模型的阈值（米）")]
        [SerializeField] private float _hideModelDistance = 1.1f;

        [Header("距离")]
        [SerializeField] private float _distance = 3.0f;
        [SerializeField] private float _minDistance = 0.7f;
        [SerializeField] private float _maxDistance = 6.0f;
        [SerializeField] private float _zoomStep = 0.4f;

        [Header("视角")]
        [SerializeField] private float _sensitivity = 3.2f;
        [Tooltip("上下限（度）。下限不能太低，否则相机钻到地板下；上限受层高限制")]
        [SerializeField] private Vector2 _pitchRange = new Vector2(-18f, 62f);

        [Header("碰撞")]
        [Tooltip("球形投射半径，比射线稳：擦着墙角过不会突然穿过去")]
        [SerializeField] private float _probeRadius = 0.22f;
        [SerializeField] private LayerMask _collisionMask = ~0;

        [Header("按键")]
        [SerializeField] private KeyCode _toggleViewKey = KeyCode.F5;

        [Tooltip("切「观景模式」：交出相机控制权给 FreeCameraController（自由漫游 + 数字键 1-9 预设机位）")]
        [SerializeField] private KeyCode _freeCamKey = KeyCode.V;

        private float _yaw;
        private float _pitch = 14f;
        private float _curDistance;
        private bool _firstPerson;
        private Transform _modelRoot;      // 用来在贴脸时隐藏
        private FreeCameraController _free; // 观景模式接管用
        private bool _freeCamMode;

        private void Awake()
        {
            if (_target == null)
            {
                TwinPlayerController pc = FindObjectOfType<TwinPlayerController>();
                if (pc != null) _target = pc.transform;
            }
            if (_target != null)
            {
                _yaw = _target.eulerAngles.y;
                // 模型 = 角色根下面第一个带渲染器的子节点
                Renderer r = _target.GetComponentInChildren<Renderer>();
                if (r != null) _modelRoot = r.transform;
            }
            _curDistance = _distance;

            // 默认角色模式：把自由漫游关掉，否则两个脚本每帧都写相机
            _free = FindObjectOfType<FreeCameraController>();
            ApplyFreeCamMode(false);
        }

        /// <summary>
        /// 观景模式 = 把相机交回 FreeCameraController（自由漫游 + 数字键 1-9 预设机位）。
        ///
        /// 【为什么需要】这两套逻辑都会每帧写相机 transform，同时开着画面会抖。
        /// 而且预设机位那套逻辑本来就在 FreeCameraController 里，角色模式下必须能叫回来。
        /// </summary>
        private void ApplyFreeCamMode(bool on)
        {
            _freeCamMode = on;
            if (_free != null && _free.enabled != on) _free.enabled = on;
            if (!on) SetModelVisible(true);
            Debug.Log("[TwinCam] 模式：" + (on ? "观景（自由漫游，数字键 1-9 可用）" : "角色（第三人称）"));
        }

        private void LateUpdate()
        {
            // 观景/角色 模式切换（放最前面：观景模式下本组件不再碰相机）
            if (Input.GetKeyDown(_freeCamKey)) ApplyFreeCamMode(!_freeCamMode);
            if (_freeCamMode) return;

            if (_target == null) return;

            // 切视角
            if (Input.GetKeyDown(_toggleViewKey))
            {
                _firstPerson = !_firstPerson;
                Debug.Log("[TwinCam] 视角切换：" + (_firstPerson ? "第一人称" : "第三人称"));
            }

            // 右键转视角（左键留给「点设备」）
            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxis("Mouse X") * _sensitivity;
                _pitch -= Input.GetAxis("Mouse Y") * _sensitivity;
                _pitch = Mathf.Clamp(_pitch, _pitchRange.x, _pitchRange.y);
            }

            // 滚轮调距离（鼠标在 UI 上时不要抢 —— 小地图也要用滚轮）
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.001f && !IsPointerOverUI())
            {
                _distance = Mathf.Clamp(_distance - scroll * _zoomStep, _minDistance, _maxDistance);
            }

            Vector3 pivot = _target.position + _pivotOffset;
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);

            if (_firstPerson)
            {
                transform.position = pivot;
                transform.rotation = rot;
                SetModelVisible(false);
                return;
            }

            // 弹簧臂：先算理想位置，再按碰撞缩短
            Vector3 want = pivot - rot * Vector3.forward * _distance;
            Vector3 dir = want - pivot;
            float len = dir.magnitude;
            float allowed = len;

            if (len > 0.01f)
            {
                RaycastHit hit;
                Vector3 origin = pivot;
                Vector3 unit = dir / len;

                // 最多 3 次：命中角色自己就跳过（角色在吊臂起点附近，第一次很容易打到自己）
                for (int i = 0; i < 3; i++)
                {
                    if (!Physics.SphereCast(origin, _probeRadius, unit, out hit, len,
                                            _collisionMask, QueryTriggerInteraction.Ignore))
                        break;

                    if (_target != null && hit.collider.transform.IsChildOf(_target))
                    {
                        origin = hit.point + unit * 0.05f;
                        len -= (hit.point - (origin - unit * 0.05f)).magnitude;
                        if (len <= 0.01f) break;
                        continue;
                    }

                    allowed = Mathf.Max(_minDistance, hit.distance - 0.05f);
                    break;
                }
            }

            // 平滑：缩短要快（否则会穿墙），伸长要慢（否则抖）
            float k = allowed < _curDistance ? 30f : 6f;
            _curDistance = Mathf.Lerp(_curDistance, allowed, 1f - Mathf.Exp(-k * Time.deltaTime));

            transform.position = pivot - rot * Vector3.forward * _curDistance;
            transform.rotation = rot;

            // 贴太近就把角色藏起来，否则半个身子糊在屏幕上
            SetModelVisible(_curDistance > _hideModelDistance);
        }

        private void SetModelVisible(bool visible)
        {
            if (_modelRoot == null) return;
            if (_modelRoot.gameObject.activeSelf == visible) return;
            _modelRoot.gameObject.SetActive(visible);
        }

        /// <summary>鼠标是不是压在 UI 上（小地图/面板）。压在 UI 上时滚轮归 UI 用。</summary>
        private static bool IsPointerOverUI()
        {
            return UnityEngine.EventSystems.EventSystem.current != null &&
                   UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
        }
    }
}
