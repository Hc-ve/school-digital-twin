using System;
using System.Collections.Generic;
using SchoolTwin.Command;
using SchoolTwin.Core;      // TwinValue 在这里（不在 SchoolTwin.Twin）
using SchoolTwin.Interaction;
using SchoolTwin.Player;
using SchoolTwin.Present;
using SchoolTwin.Twin;
using UnityEngine;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P27】机房主脑的交互驱动器（原 TwinPcMenuPanel 重构）。
    ///
    /// ── 与旧版的差别 ─────────────────────────────────────────────────
    /// 旧版自己搭 UI（Rounded + Button + CanvasGroup，约 200 行）；
    /// 新版只做两件事：
    ///   ① 六态状态机（业务，原样保留）
    ///   ② 把每态的按钮列表翻译成 `TwinInteractMenu.Item`，喂给统一菜单渲染
    /// 视觉/输入（高亮、滚轮、E 确认）全部在 TwinInteractMenu 里，
    /// 门/灯/投影的单按钮也走同一个菜单 —— 风格天然统一。
    ///
    /// ── 六个状态（业务，不变）────────────────────────────────────────
    /// 0 = 主脑关（且机房/投影也关）  开机 / 联动开机 / 投影
    /// 1 = 主脑开                    打开PPT / 视频播放 / 关闭主脑 / 全部关机
    /// 2 = 主脑关但机房或投影还开     全部关机 / 联动开机
    /// 3 = PPT 模式                  上一页 / 下一页 n/m / 退出PPT
    /// 4 = 视频模式                  切换 n/m / 暂停(继续) / 退出视频
    /// 5 = 关机动画中                正在关机…（灰显不可点）
    ///
    /// 【为什么状态 2 存在】用户可能只点「关闭主脑」，18 台和投影仪还开着。
    /// 这时面板切回关机态，如果只有「开机/联动/投影」，用户就没有入口把剩下的关掉了
    /// —— 2026-10-27 用户实测踩到这个："怎么全部关机"。
    ///
    /// 【为什么有 3/4/5】播放模式下只留「翻页/切换/暂停/退出」，
    /// 把「全部关机」「关闭主脑」这些破坏性按钮藏起来 —— 用户要求。
    /// 课堂上正在放 PPT，误触「全部关机」是很尴尬的。
    /// </summary>
    public sealed class TwinPcMenuDriver : MonoBehaviour
    {
        private const string MasterId = "ClassComputer/PC/Master_01";

        // 【P3】没有 `RoomId` 了：场景里不存在 `ClassComputer/PC/Room_01` 这个锚点，
        // 机房的 18 台是 `Seat_01..18`。原来发 `Room_01` 的命令会被拒绝，
        // 联动开机就只剩主脑和投影。现在由 `SendRoom` 逐个下发（见该方法）。

        private const string ProjectorId = "ClassComputer/Projector/Main_01";

        private const float PanelRange = 2.6f;   // 比交互点的 2.2 略大，走近就能看见

        /// <summary>面板在屏幕上的固定位置（左上角锚）。沿用旧版，不跟物体跑。</summary>
        private static readonly Vector2 Anchor = new Vector2(0.26f, 0.62f);

        // ---- 运行期 ----
        private Transform _player;
        private Camera _cam;
        private TwinAnchor _master;
        private float _nextScan;

        /// <summary>联动开机时"机房那 18 台"延迟下发的时间点（秒）；&lt;0 = 没有待发。</summary>
        private float _pendingRoomAt = -1f;

        /// <summary>复用的条目缓冲。每帧 new List 会产生 GC，这里只 Clear+Add。</summary>
        private readonly List<TwinInteractMenu.Item> _items = new List<TwinInteractMenu.Item>(6);

        // 观景模式下不弹面板（E 键归自由相机）
        private FreeCameraController _freeCam;
        private bool _freeCamLooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindObjectOfType<TwinPcMenuDriver>(true) != null) return;
            GameObject go = new GameObject("TwinPcMenuDriver");
            go.transform.SetParent(TwinInteractMenu.Ensure().transform, false);
            go.AddComponent<TwinPcMenuDriver>();
            Debug.Log("[P27] 已自动挂载主脑交互驱动器（TwinPcMenuDriver）");
        }

        // ---------------------------------------------------------------- 主循环

        private void Update()
        {
            TwinInteractMenu menu = TwinInteractMenu.Ensure();

            if (_player == null) { _player = FindPlayer(); if (_player == null) { menu.Hide(); return; } }
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) { menu.Hide(); return; }

            RefreshMaster();
            if (_master == null) { menu.Hide(); return; }

            // 联动开机的"机房延迟下发"
            if (_pendingRoomAt > 0f && Time.unscaledTime >= _pendingRoomAt)
            {
                _pendingRoomAt = -1f;
                SendRoom(true, "联动开机（机房）");
            }

            float dist = Vector3.Distance(_player.position, _master.transform.position);
            if (dist > PanelRange) { menu.Hide(); return; }

            // 主脑必须在画面里，否则背后也弹面板
            Vector3 sp = _cam.WorldToScreenPoint(_master.transform.position + Vector3.up * 0.9f);
            if (sp.z <= 0f || sp.x < 0f || sp.x > _cam.pixelWidth || sp.y < 0f || sp.y > _cam.pixelHeight)
            {
                menu.Hide(); return;
            }

            // 观景模式下角色原地不动，面板没有意义，还会和 E 键抢
            if (!_freeCamLooked)
            {
                _freeCamLooked = true;
                _freeCam = FindObjectOfType<FreeCameraController>();
            }
            if (_freeCam != null && _freeCam.enabled) { menu.Hide(); return; }

            menu.Show(BuildItems(), Anchor);
        }

        private Transform FindPlayer()
        {
            TwinPlayerController pc = FindObjectOfType<TwinPlayerController>();
            return pc != null ? pc.transform : null;
        }

        private void RefreshMaster()
        {
            if (_master != null && Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 1f;

            _master = null;
            TwinRuntime rt = TwinRuntime.Instance;
            if (rt == null || rt.Registry == null) return;

            TwinAnchor a;
            if (rt.Registry.TryGet(MasterId, out a) && a != null) _master = a;
        }

        /// <summary>某个点位当前是不是开着（读 `Power` 回读，带缓存）。</summary>
        private bool IsOn(string twinId)
        {
            TwinRuntime rt = TwinRuntime.Instance;
            if (rt == null || rt.Registry == null) return false;

            TwinAnchor a;
            if (!rt.Registry.TryGet(twinId, out a) || a == null) return false;

            TwinValue v;
            return a.TryGetValue("Power", out v) && v.Value > 0.5f;
        }

        // ---------------------------------------------------------------- 状态 → 条目

        /// <summary>
        /// 六态状态机 → 按钮条目。每帧调用（构建 List 的成本≈0），
        /// 「第 3/12 页」这种同一状态内的文案变化天然正确 —— 不再需要旧版的
        /// `_shownState` 缓存和 `ForceRebuild()`（那次强制重建的由来见旧实现）。
        /// </summary>
        private IReadOnlyList<TwinInteractMenu.Item> BuildItems()
        {
            _items.Clear();

            // 【P3-5】断电时整个菜单只剩一条提示。
            //
            // 主理人 play 实测：把整栋楼断电后走近电脑，菜单仍显示「打开PPT / 视频播放 /
            // 关闭主脑 / 全部关机」—— 因为下面的六态状态机只看 `IsOn(MasterId)`
            // （设备状态仍记为开），不看"有没有电"。点了「关闭主脑」还会提示"关机中"，
            // 而它本来就是断电关机状态，这个反馈是假的。
            //
            // 放在状态机之前：断电是**最优先**的事实，它盖过一切设备状态。
            if (!TwinCircuit.IsPowered(MasterId))
            {
                _items.Add(new TwinInteractMenu.Item("电脑已断电 · 无法操作",
                    TwinInteractMenu.IconKind.PowerOff, null, false));
                return _items;
            }

            TwinDevicePresenter pres = TwinDevicePresenter.Instance;
            int state;

            if (pres != null && pres.IsShuttingDown) state = 5;
            else
            {
                TwinDevicePresenter.PcPlayMode mode = pres != null
                    ? pres.PlayMode : TwinDevicePresenter.PcPlayMode.None;

                if (mode == TwinDevicePresenter.PcPlayMode.Ppt) state = 3;
                else if (mode == TwinDevicePresenter.PcPlayMode.Video) state = 4;
                else state = IsOn(MasterId) ? 1 : (AnySeatOn() || IsOn(ProjectorId) ? 2 : 0);
            }

            if (state == 0)
            {
                _items.Add(new TwinInteractMenu.Item("开机", TwinInteractMenu.IconKind.Power,
                    () => Send(MasterId, 1f, "开机")));
                _items.Add(new TwinInteractMenu.Item("联动开机", TwinInteractMenu.IconKind.Screen, OnLinkBoot));
                _items.Add(new TwinInteractMenu.Item("投影", TwinInteractMenu.IconKind.Screen, OnProject));
            }
            else if (state == 2)
            {
                _items.Add(new TwinInteractMenu.Item("全部关机", TwinInteractMenu.IconKind.PowerOff, OnShutdownAll));
                _items.Add(new TwinInteractMenu.Item("联动开机", TwinInteractMenu.IconKind.Screen, OnLinkBoot));
            }
            else if (state == 3)
            {
                // ── PPT 模式 ──
                int cur = pres != null ? pres.SlideNumber : 0;
                int total = pres != null ? pres.SlideCount : 0;

                _items.Add(new TwinInteractMenu.Item("上一页", TwinInteractMenu.IconKind.Prev, OnPrevSlide));
                _items.Add(new TwinInteractMenu.Item("下一页  " + cur + "/" + total, TwinInteractMenu.IconKind.Next, OnNextSlide));
                _items.Add(new TwinInteractMenu.Item("退出PPT", TwinInteractMenu.IconKind.Exit, OnExitPlayMode));
            }
            else if (state == 4)
            {
                // ── 视频模式 ──
                int cur = pres != null ? pres.VideoNumber : 0;
                int total = pres != null ? pres.VideoCount : 0;
                bool paused = pres != null && pres.VideoPaused;

                _items.Add(new TwinInteractMenu.Item("切换  " + cur + "/" + total, TwinInteractMenu.IconKind.Next, OnNextVideo));
                _items.Add(new TwinInteractMenu.Item(paused ? "继续" : "暂停", TwinInteractMenu.IconKind.Pause, OnToggleVideoPause));
                _items.Add(new TwinInteractMenu.Item("退出视频", TwinInteractMenu.IconKind.Exit, OnExitPlayMode));
            }
            else if (state == 5)
            {
                // 关机动画播放中 —— 只留一个提示，不可点
                _items.Add(new TwinInteractMenu.Item("正在关机…", TwinInteractMenu.IconKind.PowerOff, null, false));
            }
            else
            {
                _items.Add(new TwinInteractMenu.Item("打开PPT", TwinInteractMenu.IconKind.Screen, OnEnterPptMode));
                _items.Add(new TwinInteractMenu.Item("视频播放", TwinInteractMenu.IconKind.Play, OnEnterVideoMode));
                _items.Add(TwinInteractMenu.Item.LetterItem("关闭主脑", "关", TwinInteractMenu.IconKind.PowerOff, OnShutdownMaster));
                _items.Add(new TwinInteractMenu.Item("全部关机", TwinInteractMenu.IconKind.PowerOff, OnShutdownAll));
            }

            return _items;
        }

        // ---------------------------------------------------------------- 动作

        private void OnLinkBoot()
        {
            // 主脑先开，机房 18 台延后 0.35s —— 模拟开机风暴。
            // 同一点位连发两条会被 `_inFlight` 拒（"上一条命令还没结束"），
            // 但 Master 和 Room 是**不同 twinId**，互不影响，所以可以这样错开。
            //
            // 【投影仪也一起开】用户 2026-10-27 反馈"点击联动开机，投影仪不开"。
            // 「联动开机」的语义是"上课，整间机房进入可用状态"，
            // 投影仪是这间机房的一部分，不跟着开就还得再点一次「投影」。
            Send(MasterId, 1f, "开机（主脑）");
            Send(ProjectorId, 1f, "联动开机（投影）");
            _pendingRoomAt = Time.unscaledTime + 0.35f;
        }

        private void OnProject()
        {
            Send(MasterId, 1f, "开机（主脑）");
            Send(ProjectorId, 1f, "开投影");
        }

        /// <summary>
        /// 全部关机：主脑 + 机房 18 台 + 投影仪，**三处一起播关机动画**。
        ///
        /// 【先播关机动画再断电】真实电脑点关机不是瞬间黑屏。
        /// `BeginShutdown` 会先播 `shutdown.mp4`，播完才回调这里真正下发断电命令。
        /// 动画期间面板显示「正在关机…」，避免重复点击。
        /// </summary>
        private void OnShutdownAll()
        {
            TwinDevicePresenter p = TwinDevicePresenter.Instance;
            if (p != null)
            {
                p.BeginShutdown(() =>
                {
                    Send(MasterId, 0f, "全部关机（主脑）");
                    SendRoom(false, "全部关机（机房）");
                    Send(ProjectorId, 0f, "全部关机（投影）");
                }, true);          // includeRoom = true
                return;
            }

            // 没有表现层（极端情况）→ 直接断电，不能因为播不了动画就关不掉
            Send(MasterId, 0f, "全部关机（主脑）");
            SendRoom(false, "全部关机（机房）");
            Send(ProjectorId, 0f, "全部关机（投影）");
        }

        /// <summary>
        /// 只关主脑（机房 18 台和投影保持原样）。
        ///
        /// 【includeRoom = false】机房还开着，跟着播关机动画会很怪 ——
        /// 机组没关却先演一遍"正在关机"再亮回来，不符合现实逻辑。
        /// </summary>
        private void OnShutdownMaster()
        {
            TwinDevicePresenter p = TwinDevicePresenter.Instance;
            if (p != null)
            {
                p.BeginShutdown(() => Send(MasterId, 0f, "关闭主脑"), false);
                return;
            }
            Send(MasterId, 0f, "关闭主脑");
        }

        // ---------------------------------------------------------------- 播放模式

        /// <summary>
        /// 进入 PPT 模式：面板切成「上一页 / 下一页 / 退出」。
        /// 失败（没素材）就什么都不做，留在原面板 —— 比切到一个空模式好。
        ///
        /// 【顺带开投影仪】PPT 就是要投到幕布上看的。
        /// 但「投影」按钮只在**全关状态**才有 —— 主脑一开，面板就没有开投影仪的入口了。
        /// 2026-10-27 用户实测："播放PPT的效果出现在主脑上"，
        /// 真相是幕布根本没开、也没地方开。
        /// </summary>
        private void OnEnterPptMode()
        {
            TwinDevicePresenter p = TwinDevicePresenter.Instance;
            if (p == null) { Debug.LogWarning("[P26] 找不到表现层"); return; }

            EnsureProjectorOn();
            p.EnterPptMode();
        }

        /// <summary>进入视频模式：面板切成「切换 / 暂停 / 退出」。同样顺带开投影仪。</summary>
        private void OnEnterVideoMode()
        {
            TwinDevicePresenter p = TwinDevicePresenter.Instance;
            if (p == null) { Debug.LogWarning("[P26] 找不到表现层"); return; }

            EnsureProjectorOn();
            p.EnterVideoMode();
        }

        /// <summary>
        /// 投影仪没开就开它。已经开着就什么都不做。
        ///
        /// 【为什么只开不关】退出 PPT 后不自动关投影仪 ——
        /// 用户可能只是切到视频、或者翻回来看别的，
        /// 每次都开关会让幕布反复亮灭，而且投影仪真实关机有灯泡冷却时间。
        /// </summary>
        private void EnsureProjectorOn()
        {
            if (IsOn(ProjectorId)) return;
            Send(ProjectorId, 1f, "演示（自动开投影）");
        }

        private void OnPrevSlide()
        {
            TwinDevicePresenter p = TwinDevicePresenter.Instance;
            if (p != null) p.PrevSlide();
        }

        private void OnNextSlide()
        {
            TwinDevicePresenter p = TwinDevicePresenter.Instance;
            if (p != null) p.NextSlide();
        }

        private void OnNextVideo()
        {
            TwinDevicePresenter p = TwinDevicePresenter.Instance;
            if (p != null) p.NextVideo();
        }

        private void OnToggleVideoPause()
        {
            TwinDevicePresenter p = TwinDevicePresenter.Instance;
            if (p != null) p.ToggleVideoPause();
        }

        /// <summary>退出播放模式（PPT 和视频共用）。</summary>
        private void OnExitPlayMode()
        {
            TwinDevicePresenter p = TwinDevicePresenter.Instance;
            if (p == null) return;

            if (p.PlayMode == TwinDevicePresenter.PcPlayMode.Ppt) p.ExitPptMode();
            else if (p.PlayMode == TwinDevicePresenter.PcPlayMode.Video) p.ExitVideoMode();
        }

        /// <summary>
        /// 机房 18 个座位里有没有任何一台开着。
        ///
        /// 原来这里判 `IsOn(Room_01)`，但场景里没有这个锚点，恒为 false ——
        /// 于是"面板单开了几台座位、主脑还关着"时菜单会停在 state 0，
        /// 只显示「开机 / 联动开机 / 投影」，**没有「全部关机」入口**，
        /// 用户没法把开着的座位关掉。
        /// </summary>
        private static bool AnySeatOn()
        {
            IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;
            if (all == null) return false;

            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                if (!a.TwinId.StartsWith("ClassComputer/PC/Seat_", StringComparison.Ordinal)) continue;

                TwinValue v;
                if (a.TryGetValue("Power", out v) && v.Value > 0.5f) return true;
            }
            return false;
        }

        /// <summary>
        /// 给机房 18 个座位批量下发。
        ///
        /// ── 为什么不发 `Room_01` ──────────────────────────────────────────
        /// 场景里**没有** `ClassComputer/PC/Room_01` 这个锚点
        /// （2026-11-02 核实：只有 `Master_01` + `Seat_01..18`）。
        /// 往一个不存在的点位发命令，命令中心会拒绝，
        /// 联动开机就只剩主脑和投影 —— 正是主理人反馈的现象。
        ///
        /// 逐个发 18 条确实比一条多，但它是**唯一真实存在**的路径：
        /// 18 个 Seat 是场景里实际建出来的点位，数据、详情页、告警都挂在它们身上。
        /// </summary>
        private void SendRoom(bool on, string what)
        {
            IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;
            if (all == null) return;

            // 【P3】开机前先打开"批量窗口"：
            // 窗口内 18 个 Seat 的 ApplyScreen 不互相摘除，
            // 一起接机房 RT 播同一段开机动画 —— 这就是"联动开机全部启动"的观感。
            // 不开窗口的话，第一个处理的座位会把其他 17 台摘成静态快照，
            // 变成"只有一台播动画、其余是桌面"。
            if (on)
            {
                TwinDevicePresenter pres = TwinDevicePresenter.Instance;
                if (pres != null) pres.BeginBulkSeats();
            }

            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                if (!a.TwinId.StartsWith("ClassComputer/PC/Seat_", StringComparison.Ordinal)) continue;

                Send(a.TwinId, on ? 1f : 0f, what);
                n++;
            }

            Debug.Log("[P3] " + what + " → " + n + " 个座位");
        }

        private void Send(string twinId, float value, string what)
        {
            TwinCommandCenter center = TwinCommandCenter.Ensure();
            if (center == null) return;

            string reject;
            if (!center.TrySend(twinId, CommandAction.SetPower, value, out reject))
            {
                // 【P3-3】被拒绝时必须让人看见，不能只进 Console。
                //
                // 主理人 play 实测：把 1F 断电后点「开机」，屏幕上什么都没有 ——
                // 因为这里原来只 `Debug.LogWarning`。用户只会认为"按钮是假的"。
                //
                // `reject` 本身已经是人话（`CommandText.FailReason` 的输出，
                // 比如「设备已断电」「上一条命令还没结束」），直接上 Toast 就够，
                // 不用再拼一层"操作失败："前缀 —— 那样两句话会重复。
                Debug.LogWarning("[P26] " + what + " → " + twinId + " 被拒绝：" + reject);
                SchoolTwin.UI.TwinToast.Show(reject);
                return;
            }
            Debug.Log("[P26] " + what + " → " + twinId + " SetPower(" + value + ")");
        }
    }
}
