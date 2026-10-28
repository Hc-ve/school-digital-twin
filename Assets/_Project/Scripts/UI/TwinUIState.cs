using UnityEngine;

namespace SchoolTwin.UI
{
    /// <summary>UI 当前占据的层级。数值越大越"上面"。</summary>
    public enum TwinUILayer
    {
        None = 0,     // 纯 3D 场景
        Drawer = 1,   // 总控抽屉（L1/L2）
        Detail = 2,   // 设备详情卡（L3a）
        Modal = 3,    // 模态确认（L3b）
    }

    /// <summary>
    /// 【P28】全局 UI 状态与输入仲裁的**唯一**来源。
    ///
    /// ── 重构前的问题 ────────────────────────────────────────────────
    /// 每个想"挡一下输入"的地方都自己判一下：
    ///   · `TwinInteractMenu.IsBlocking` 只挡了两处相机滚轮
    ///   · `TwinPlayerController` 完全不看 UI，面板开着人照样走
    ///   · `TwinPicker` 只在 `IsPointerOverGameObject` 时让路
    /// 结果：抽屉开着的时候，角色在走、点面板的同时把 3D 设备也选中了。
    ///
    /// ── 现在的规则 ──────────────────────────────────────────────────
    /// 一个 `Current` 层级，四种派生判定，各消费方只读不写：
    ///   Movement / Scroll  → 只要有面板就挡（抽屉开着还走动很奇怪）
    ///   Pick / Look / Hotkeys → **只有模态才挡**（抽屉开着必须还能点场景设备，
    ///                            那是"孪生"的核心动作；右键转视角也要留着）
    ///
    /// ── 为什么用静态类而不是单例 MonoBehaviour ──────────────────────
    /// 这个状态没有生命周期（不随场景销毁），也不需要在 Inspector 里配。
    /// 做成 MonoBehaviour 反而要处理"谁先 Awake"和"切场景丢了引用"。
    /// </summary>
    public static class TwinUIState
    {
        public static TwinUILayer Current { get; private set; }

        /// <summary>Esc 的路由。由 `TwinDrawer` 注册；返回 true = 已被 UI 消费。</summary>
        public static System.Func<bool> EscapeRouter;

        // ================================================================ 写入

        /// <summary>
        /// 设置当前层级。**只增不减**用 `Raise`，**恢复**用 `LowerTo`。
        ///
        /// 【为什么要分两个】抽屉打开时选中设备会进入 Detail，关掉详情卡要回到 Drawer
        /// 而不是 None —— 如果只用一个 `Set`，调用方很容易写错成 Set(None) 把抽屉也"关"了。
        /// </summary>
        public static void Set(TwinUILayer layer)
        {
            Current = layer;
        }

        /// <summary>只在当前层级**低于**目标时才提升（详情卡盖在抽屉上，抽屉不该把它降回去）。</summary>
        public static void Raise(TwinUILayer layer)
        {
            if (layer > Current) Current = layer;
        }

        /// <summary>只在当前层级**高于**目标时才降低。</summary>
        public static void LowerTo(TwinUILayer layer)
        {
            if (layer < Current) Current = layer;
        }

        /// <summary>强制回到场景态（收抽屉、关详情、退模态时调）。</summary>
        public static void Reset()
        {
            Current = TwinUILayer.None;
        }

        // ================================================================ 读（消费方只用这些）

        /// <summary>角色移动输入是否要被清空。</summary>
        public static bool BlocksMovement { get { return Current != TwinUILayer.None; } }

        /// <summary>滚轮是否归 UI（相机缩放/推进让路）。</summary>
        public static bool BlocksScroll { get { return Current != TwinUILayer.None; } }

        /// <summary>左键拾取是否要挡。**只有模态挡** —— 抽屉开着还要能点设备。</summary>
        public static bool BlocksPick { get { return Current == TwinUILayer.Modal; } }

        /// <summary>右键转视角是否要挡。只有模态挡。</summary>
        public static bool BlocksLook { get { return Current == TwinUILayer.Modal; } }

        /// <summary>快捷键（E 确认等）是否要挡。只有模态挡。</summary>
        public static bool BlocksHotkeys { get { return Current == TwinUILayer.Modal; } }

        // ================================================================ Esc

        /// <summary>
        /// 把 Esc 交给 UI 处理。返回 true 表示 UI 已经消费掉，调用方不要再做自己的事。
        ///
        /// 用法（TwinPicker）：
        ///   if (TwinUIState.HandleEscape()) return;   // UI 先走
        ///   ... 原来的"清选中"逻辑
        /// </summary>
        public static bool HandleEscape()
        {
            System.Func<bool> router = EscapeRouter;
            if (router == null) return false;

            // ⚠️ 不能用 `router?.Invoke() ?? false`：Unity 的委托不走空条件运算符的
            // 那套语义，而且这里要的是"有没有人处理"，不是返回值合并。
            return router();
        }
    }
}
