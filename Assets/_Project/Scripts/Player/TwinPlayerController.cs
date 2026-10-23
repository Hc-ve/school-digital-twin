using UnityEngine;

namespace SchoolTwin.Player
{
    /// <summary>
    /// 第三人称角色移动：CharacterController + 相机相对方向 + 重力/台阶。
    ///
    /// ── 为什么用 CharacterController 而不是 Rigidbody ────────────
    /// 这个场景是「走进去看设备」的演示，需要**精确、不弹跳、能上台阶**的移动。
    /// Rigidbody 在 0.35m 的台阶和门框上会卡住或弹飞，CharacterController 自带 stepOffset 直接过。
    ///
    /// ── 动画怎么驱动 ────────────────────────────────────────────
    /// 只往 Animator 里写一个 `Speed` 参数（水平速度），动画控制器里是一条 1D 混合树：
    ///   0 → Idle，2 → Walk，5 → Run
    /// 这样不需要写「什么时候切状态」的逻辑，速度是多少就播到哪儿，天然平滑。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class TwinPlayerController : MonoBehaviour
    {
        [Header("速度")]
        [Tooltip("走路速度（m/s）。人正常走 ≈ 1.4，这里给 2.2 是为了演示时别太磨蹭")]
        [SerializeField] private float _walkSpeed = 2.2f;

        [Tooltip("跑步速度（按住左 Shift）")]
        [SerializeField] private float _runSpeed = 5.0f;

        [Tooltip("加减速的跟手程度，越大越硬")]
        [SerializeField] private float _accel = 14f;

        [Tooltip("转身速度")]
        [SerializeField] private float _turn = 14f;

        [Header("重力")]
        [SerializeField] private float _gravity = -18f;
        [SerializeField] private float _groundedStick = -2f;

        [Header("引用")]
        [Tooltip("相机（用来算「前后左右」的方向）。留空会自动找 Camera.main")]
        [SerializeField] private Transform _cameraPivot;

        [Tooltip("移动时是否把角色转向移动方向。关掉的话角色永远朝相机正前方（像 FPS）")]
        [SerializeField] private bool _faceMoveDirection = true;

        private CharacterController _cc;
        private Animator _anim;
        private float _vy;
        private Vector3 _planar;          // 当前水平速度（m/s）
        private bool _ready;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();

            // 优先取**同一物体**上的 Animator（生成工具就挂在这里）；
            // 退回子物体。取错对象的话 SetFloat 写到一个没有控制器的 Animator 上，
            // 表现是「Idle 会播、走路不播」，而且没有任何报错。
            _anim = GetComponent<Animator>();
            if (_anim == null) _anim = GetComponentInChildren<Animator>();

            if (_cameraPivot == null && Camera.main != null) _cameraPivot = Camera.main.transform;
            _ready = _cc != null;
            if (!_ready) Debug.LogError("[TwinPlayer] 缺少 CharacterController，移动不生效。");

            Debug.Log("[TwinPlayer] 接线检查：CharacterController=" + (_cc != null) +
                      "  Animator=" + (_anim != null ? _anim.name : "(无)") +
                      "  Controller=" + (_anim != null && _anim.runtimeAnimatorController != null
                          ? _anim.runtimeAnimatorController.name : "(空)") +
                      "  Avatar=" + (_anim != null && _anim.avatar != null ? _anim.avatar.name : "(空)") +
                      "  参数里有 Speed=" + (_anim != null && HasParam(_anim, "Speed")));
        }

        private static bool HasParam(Animator a, string name)
        {
            if (a == null) return false;
            AnimatorControllerParameter[] ps = a.parameters;
            if (ps == null) return false;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i] != null && ps[i].name == name) return true;
            return false;
        }

        private float _nextProbe;

        private void Update()
        {
            if (!_ready) return;

            Vector2 input = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            if (input.sqrMagnitude > 1f) input.Normalize();

            bool running = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float targetSpeed = (running ? _runSpeed : _walkSpeed) * Mathf.Clamp01(input.magnitude);

            // 相机相对方向：按 W 永远是「往屏幕里走」，不管相机怎么转
            Vector3 wish;
            if (_cameraPivot != null)
            {
                Vector3 f = _cameraPivot.forward; f.y = 0f;
                if (f.sqrMagnitude < 0.0001f) f = Vector3.forward;
                f.Normalize();
                Vector3 r = Vector3.Cross(Vector3.up, f);   // 右手系：up × forward = 右
                wish = (f * input.y + r * input.x);
            }
            else
            {
                wish = new Vector3(input.x, 0f, input.y);
            }
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            Vector3 target = wish * targetSpeed;
            _planar = Vector3.Lerp(_planar, target, 1f - Mathf.Exp(-_accel * Time.deltaTime));

            // 朝向：转向移动方向（不是转向相机）
            if (_faceMoveDirection && _planar.sqrMagnitude > 0.04f)
            {
                Quaternion want = Quaternion.LookRotation(_planar, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, want, 1f - Mathf.Exp(-_turn * Time.deltaTime));
            }

            // 重力：落地时给一个小的向下压，避免在斜面上「悬空抖动」
            if (_cc.isGrounded && _vy < 0f) _vy = _groundedStick;
            _vy += _gravity * Time.deltaTime;

            Vector3 motion = _planar + Vector3.up * _vy;
            _cc.Move(motion * Time.deltaTime);

            // 动画：只写速度，混合树自己决定播什么。
            // 用 `_planar`（指令速度）而不是 `_cc.velocity`：后者含重力分量，
            // 而且刚 Move 完读到的值语义容易让人误会。
            float speed = _planar.magnitude;
            if (_anim != null)
                _anim.SetFloat("Speed", speed, 0.08f, Time.deltaTime);

            // 探针：每秒一条，用来判断「Speed 有没有真的写进 Animator」
            if (Time.unscaledTime >= _nextProbe)
            {
                _nextProbe = Time.unscaledTime + 1f;
                if (speed > 0.05f)
                {
                    AnimatorStateInfo st = _anim != null ? _anim.GetCurrentAnimatorStateInfo(0) : default(AnimatorStateInfo);
                    Debug.Log("[TwinPlayer] Speed=" + speed.ToString("0.00") +
                              "  参数实测=" + (_anim != null ? _anim.GetFloat("Speed").ToString("0.00") : "n/a") +
                              "  状态=" + (st.shortNameHash != 0 ? "hash " + st.shortNameHash : "?") +
                              "  归一化时间=" + st.normalizedTime.ToString("0.00"));
                }
            }
        }

        /// <summary>把角色瞬移到某处（预设机位、传送门用）。会清掉垂直速度。</summary>
        public void Teleport(Vector3 position, float yaw)
        {
            if (_cc == null) return;
            _cc.enabled = false;
            transform.position = position;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            _cc.enabled = true;
            _vy = 0f;
            _planar = Vector3.zero;
        }
    }
}
