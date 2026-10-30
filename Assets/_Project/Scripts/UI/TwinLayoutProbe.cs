using System.Text;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Twin;   // TwinDeviceStats 在 SchoolTwin.Twin，不在 Core（A6 坑）

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P29-3.5】布局体检探针 + 运行时断言。
    ///
    /// ── 它解决什么 ──────────────────────────────────────────────────
    /// 「内容被工具行压住」这类问题，肉眼判断不可靠：526 和 544 只差 18px，
    /// 投影仪缩放下一会儿看着像压住、一会儿又像没压住。规格 §8.5 要求交付时附
    /// `page.rect.height` / 最后一行四角 / 工具行上沿三组数 —— 这个类就是产出那三组数的地方。
    ///
    /// ── 为什么用 GetWorldCorners 而不是 rect ─────────────────────────
    /// 布局没刷新时 `rect` 是上一帧的（甚至创建当帧是 0，见 P00 §1.6 B3）。
    /// GetWorldCorners 读的是 transform 矩阵，配合页面根做基准换算，结果是真实上屏位置。
    ///
    /// ── 为什么 L2 要临时建一个页面 ───────────────────────────────────
    /// `TwinSystemPage` 只在被 Push 时才构建。探针若去 Push 真页面，会改掉抽屉的
    /// 页栈和宽度目标（副作用）；所以这里在 Content 下临时挂一个，测完立刻销毁。
    ///
    /// ⚠️ 本类在静态上下文里调用 Unity API，`FindObjectOfType` / `Destroy` 必须写
    ///    全限定 `UnityEngine.Object.xxx` —— static class 不继承 Object（P00 §1.6 A4）。
    /// </summary>
    public static class TwinLayoutProbe
    {
        public const string Tag = "[P29-3.5]";

        // ================================================================ 对外

        /// <summary>把 L1 / 工具行 / L2 三处一次性量出来打一条日志。启动 3 秒后自动跑一次。</summary>
        public static void DumpAll()
        {
            string report = Capture();

            // 有 ✘ 就用 Error 级别，让它在 Console 里带红点，不容易被刷过去
            if (report.Contains("✘")) Debug.LogError(report);
            else Debug.Log(report);
        }

        /// <summary>
        /// 采集三组数并返回文本（不写日志）。供 <see cref="DumpAll"/>、编辑器菜单、
        /// 以及离线采集脚本共用 —— 一处实现，三种触发方式。
        /// </summary>
        public static string Capture()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.AppendLine("===== " + Tag + " 布局体检 =====");
            sb.Append("画布 ").Append(Screen.width).Append("×").Append(Screen.height)
              .Append("   Canvas.rect = ").Append(CanvasSizeText()).AppendLine();

            TwinHomePage home = UnityEngine.Object.FindObjectOfType<TwinHomePage>(true);
            if (home == null)
            {
                sb.AppendLine("未找到 TwinHomePage —— 抽屉没构建出来（检查 TwinDrawer 自举与画布）");
                return sb.ToString();
            }

            // 层级：TwinDrawer(_root) → Content → PageStack → HomePage
            // 【P29-3.6 修】上一版只往上走两层，`content` 拿到的是 PageStack、`drawerRoot`
            // 拿到的是 Content，于是「抽屉高」量出来是 544 而不是 720 —— 探针自己报了个假警。
            // 现在显式区分三层：pageHost = PageStack，content = Content，drawerRoot = 抽屉根。
            RectTransform homeRoot = home.transform as RectTransform;
            RectTransform pageHost = homeRoot != null ? homeRoot.parent as RectTransform : null;   // PageStack
            RectTransform content = pageHost != null ? pageHost.parent as RectTransform : null;   // Content
            RectTransform drawerRoot = content != null ? content.parent as RectTransform : null;  // TwinDrawer

            // ---- 内容区高度：这才是真正决定"内容会不会被压/被裁"的那个数 ----
            //
            // 期望 544（= 720 − 96 − 80）。但抽屉高度是跟随画布的，
            // 而 CanvasScaler match=0 只按宽度缩放、画布高 = screenH × 1000 / screenW，
            // 所以**只有当窗口是 1000:720 的比例时内容区才等于 544**。
            // 不足 544 时右侧数字会标 ✘ 并说明原因 —— 这是 R7，不是面板算错。
            if (content != null)
            {
                float contentH = PageHeightOf(content);
                sb.Append("内容区高（世界尺度）= ").Append(contentH.ToString("0.0"));
                sb.Append("  期望 ").Append(TwinHomePage.PageH.ToString("0.0"));

                if (Mathf.Abs(contentH - TwinHomePage.PageH) < 0.5f)
                {
                    sb.AppendLine("  ✔");
                }
                else
                {
                    sb.Append("  ✘ 差 ").Append((contentH - TwinHomePage.PageH).ToString("+0.0;-0.0"));
                    sb.Append("（画布高 ").Append(PageHeightOf(drawerRoot).ToString("0.0"));
                    sb.AppendLine("≠720：窗口不是 1000:720 比例，属 R7，不是面板算错）");
                }
            }

            // ---- ① L1 页面高度 + ② 最后一行四角 ----
            DumpPage(sb, "L1 总控", homeRoot, "RowSub_3", TwinHomePage.PageH, TwinHomePage.SafeBottom);

            // ---- ③ 工具行上沿（= 内容区底边 544）----
            //
            // 不只用 `drawerRoot.Find("ToolRow")` —— 那个只在直接子节点里找，一旦
            // 抽屉的层级调整（比如给工具行套一层容器）就会静默返回 null，
            // 实测时就出现过「未找到 ToolRow」而布局其实是对的。所以再从画布根递归兜底。
            Transform toolRow = drawerRoot != null ? drawerRoot.Find("ToolRow") : null;
            if (toolRow == null && homeRoot != null) toolRow = FindDeep(homeRoot.root, "ToolRow");

            if (toolRow != null)
            {
                float toolTop = PageTopOf(toolRow as RectTransform, homeRoot);
                sb.Append("工具行上沿（页面坐标 y）= ").Append(toolTop.ToString("0.0"));
                sb.AppendLine("（内容区底边；L1 第 4 行下沿必须 ≤ " +
                              TwinHomePage.SafeBottom.ToString("0.0") + "，留 ≥16 安全区）");
            }
            else
            {
                sb.AppendLine("未找到 ToolRow —— 抽屉壳可能没建完");
            }

            // ---- L2：临时页面，测完销毁 ----
            if (content != null)
            {
                GameObject temp = new GameObject("ProbeSystemPage", typeof(RectTransform));
                temp.transform.SetParent(content, false);
                TwinUIFactory.Stretch((RectTransform)temp.transform);

                TwinSystemPage sys = temp.AddComponent<TwinSystemPage>();
                sys.OnShow();
                Canvas.ForceUpdateCanvases();

                DumpPage(sb, "L2 系统（数据监测）", (RectTransform)temp.transform,
                         "Abn_2", TwinHomePage.PageH, TwinSystemPage.AbnLastRowBottom);

                UnityEngine.Object.Destroy(temp);
            }

            // ---- 附：设备指标快照（顺手核对同屏一致）----
            sb.Append("设备指标快照：总数 ").Append(TwinDeviceStats.ActiveDeviceCount)
              .Append("  在线 ").Append(TwinDeviceStats.OnlineDeviceCount)
              .Append("  离线 ").Append(TwinDeviceStats.OfflineDeviceCount)
              .Append("  告警 ").Append(TwinDeviceStats.AlarmDeviceCount)
              .Append("  自洽 ").Append(TwinDeviceStats.IsPartitionConsistent ? "✔" : "✘")
              .AppendLine();

            sb.AppendLine("=================================");
            return sb.ToString();
        }

        /// <summary>
        /// 主画布尺寸，用来看 match=0 下画布高度到底是多少。
        ///
        /// 【P29-3.6 修】原实现取 `FindObjectOfType<Canvas>` 的**第一个**，命中的是某个子 Canvas，
        /// 日志里那句「Canvas.rect = 220.0×68.0」就是它 —— 一个会误导排查的假数
        /// （主画布实际是 1004×614 那种量级）。改成取**面积最大**的 Overlay Canvas。
        /// </summary>
        private static string CanvasSizeText()
        {
            Canvas[] all = UnityEngine.Object.FindObjectsOfType<Canvas>(true);

            Canvas best = null;
            float bestArea = -1f;

            for (int i = 0; i < all.Length; i++)
            {
                Canvas c = all[i];
                if (c == null || c.renderMode != RenderMode.ScreenSpaceOverlay) continue;

                RectTransform rt = c.transform as RectTransform;
                if (rt == null) continue;

                float area = rt.rect.width * rt.rect.height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = c;
                }
            }

            if (best == null) return "(无 Overlay 画布)";

            RectTransform br = best.transform as RectTransform;
            return br.rect.width.ToString("0.0") + "×" + br.rect.height.ToString("0.0");
        }

        // ================================================================ 测量

        /// <summary>
        /// 页面高度（世界尺度反算）。比 `rect.height` 可信 ——
        /// 布局没跑完时 rect 可能是 0 或上一帧的值。
        /// </summary>
        public static float PageHeightOf(RectTransform pageRoot)
        {
            if (pageRoot == null) return float.NaN;

            Vector3[] c = new Vector3[4];
            pageRoot.GetWorldCorners(c);

            float scale = pageRoot.lossyScale.y;
            if (Mathf.Approximately(scale, 0f)) return pageRoot.rect.height;

            return (c[1].y - c[0].y) / scale;
        }

        /// <summary>某元素**下沿**换算成页面坐标（页面顶 = 0，向下为正）。</summary>
        public static float PageBottomOf(RectTransform item, RectTransform pageRoot)
        {
            if (item == null || pageRoot == null) return float.NaN;

            Vector3[] itemCorners = new Vector3[4];
            item.GetWorldCorners(itemCorners);           // 0=BL 1=TL 2=TR 3=BR

            Vector3[] pageCorners = new Vector3[4];
            pageRoot.GetWorldCorners(pageCorners);

            float scale = pageRoot.lossyScale.y;
            if (Mathf.Approximately(scale, 0f)) return float.NaN;

            return (pageCorners[1].y - itemCorners[0].y) / scale;
        }

        /// <summary>某元素**上沿**换算成页面坐标。</summary>
        public static float PageTopOf(RectTransform item, RectTransform pageRoot)
        {
            if (item == null || pageRoot == null) return float.NaN;

            Vector3[] itemCorners = new Vector3[4];
            item.GetWorldCorners(itemCorners);

            Vector3[] pageCorners = new Vector3[4];
            pageRoot.GetWorldCorners(pageCorners);

            float scale = pageRoot.lossyScale.y;
            if (Mathf.Approximately(scale, 0f)) return float.NaN;

            return (pageCorners[1].y - itemCorners[1].y) / scale;
        }

        // ================================================================ 内部

        private static void DumpPage(StringBuilder sb, string label, RectTransform pageRoot,
                                     string lastItemName, float expectHeight, float limit)
        {
            if (pageRoot == null) return;

            sb.Append("── ").Append(label).AppendLine(" ──");

            float rectH = pageRoot.rect.height;
            float worldH = PageHeightOf(pageRoot);

            sb.Append("① page.rect.height = ").Append(rectH.ToString("0.0"));
            sb.Append("（世界尺度反算 ").Append(worldH.ToString("0.0"));
            sb.Append("，期望 ").Append(expectHeight.ToString("0.0")).Append("）");
            sb.AppendLine(Mathf.Abs(worldH - expectHeight) < 0.5f
                ? "  ✔"
                : "  ✘ 页面根没继承 _content（检查 TwinPageStack.Push 是否 Stretch）");

            RectTransform last = FindDeep(pageRoot, lastItemName);
            if (last == null)
            {
                sb.Append("② 未找到「").Append(lastItemName).AppendLine("」—— 该行可能整行未激活");
                return;
            }

            float bottom = PageBottomOf(last, pageRoot);

            sb.Append("② 「").Append(lastItemName).Append("」四角（页面坐标）");
            AppendCorners(sb, last, pageRoot);
            sb.Append("  下沿 ").Append(bottom.ToString("0.0"));
            sb.Append(" ≤ ").Append(limit.ToString("0.0"));
            sb.AppendLine(bottom <= limit + 0.5f ? "  ✔" : "  ✘ 越过安全区");

            if (bottom > limit + 0.5f)
            {
                Debug.LogError(Tag + " " + label + " 内容下沿 " + bottom.ToString("0.0") +
                               " 越过安全区 " + limit.ToString("0.0") + "（内容底 " +
                               expectHeight.ToString("0.0") + "），会出现内容压在工具行上。");
            }
        }

        /// <summary>输出 `(x 左→右, y 上→下)`，单位 = 页面坐标。</summary>
        private static void AppendCorners(StringBuilder sb, RectTransform item, RectTransform pageRoot)
        {
            Vector3[] itemCorners = new Vector3[4];
            item.GetWorldCorners(itemCorners);

            Vector3[] pageCorners = new Vector3[4];
            pageRoot.GetWorldCorners(pageCorners);

            float scale = pageRoot.lossyScale.x;
            if (Mathf.Approximately(scale, 0f)) scale = 1f;

            float pageLeft = pageCorners[0].x;
            float pageTop = pageCorners[1].y;

            float xMin = (itemCorners[0].x - pageLeft) / scale;
            float xMax = (itemCorners[2].x - pageLeft) / scale;
            float yTop = (pageTop - itemCorners[1].y) / scale;
            float yBottom = (pageTop - itemCorners[0].y) / scale;

            sb.Append(" (x ").Append(xMin.ToString("0.0")).Append("→").Append(xMax.ToString("0.0"))
              .Append(", y ").Append(yTop.ToString("0.0")).Append("→").Append(yBottom.ToString("0.0")).Append(")");
        }

        /// <summary>递归按名字找子节点（L2 的 Top3 行在 Page_monitor 下面，直接 Find 找不到）。</summary>
        private static RectTransform FindDeep(Transform root, string name)
        {
            if (root == null) return null;

            Transform direct = root.Find(name);
            if (direct != null) return direct as RectTransform;

            for (int i = 0; i < root.childCount; i++)
            {
                RectTransform found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        // ================================================================ 自动触发

        /// <summary>
        /// 进 Play 后 3 秒自动量一次。3 秒是给 Registry 注册、抽屉自举、Mock 首批数据留的余量。
        /// 只在编辑器与 Development Build 里跑，正式包不挂。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoDump()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            GameObject runner = new GameObject("TwinLayoutProbe");
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.AddComponent<AutoRunner>();
#endif
        }

        private sealed class AutoRunner : MonoBehaviour
        {
            private float _deadline;
            private bool _done;

            private void Awake()
            {
                _deadline = Time.realtimeSinceStartup + 3f;
            }

            private void Update()
            {
                if (_done) return;
                if (Time.realtimeSinceStartup < _deadline) return;

                _done = true;
                DumpAll();
                Destroy(gameObject);
            }
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("归档/P29 布局体检（运行时）")]
        private static void MenuDump()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning(Tag + " 请先进入 Play 模式再执行本菜单");
                return;
            }
            DumpAll();
        }
#endif
    }
}
