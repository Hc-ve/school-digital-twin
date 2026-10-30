using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 左上角圆形小地图（原神式：跟随玩家 + 北向上 + 箭头转）。
    ///
    /// ── 为什么是圆形，不是方形 ────────────────────────────────────
    /// 这张底图是 96.5 × 45.5m（长宽比 2.12:1）。塞进方形框里必然上下各留一大条空白；
    /// 圆形直接裁掉四角，而且"我周围有什么"这个语义本来就该是圆的。
    /// 方形适合 MOBA 那种要读全局态势、还要在小地图上点选的地图 —— 我们不是。
    ///
    /// ── 底图从哪来 ──────────────────────────────────────────────
    /// `Editor/TwinMapBaker` 烘焙三张（室外/1F/2F），写进 `Resources/TwinMapAtlas.asset`。
    /// 本组件只做：定位 → 判楼层/室内外 → 选底图 → 摆位置 → 画箭头。
    ///
    /// ── 位置源可插拔 ────────────────────────────────────────────
    /// `_player` 留空 = 用主相机（现在的自由漫游）；导入第三人称角色后把它拖进来即可，
    /// **本脚本一行都不用改**。
    /// </summary>
    [AddComponentMenu("SchoolTwin/小地图 (TwinMinimap)")]
    public sealed class TwinMinimap : MonoBehaviour, IScrollHandler
    {
        // ---------------------------------------------------------------- 配置

        [Tooltip("玩家/角色的根节点。留空 = 用主相机（自由漫游阶段）。")]
        [SerializeField] private Transform _player;

        [Tooltip("圆形直径（UI 单位，Canvas 参考分辨率 1920×1080）。")]
        [SerializeField] private float _diameter = 160f;

        [Tooltip("离屏幕左上角的边距")]
        [SerializeField] private Vector2 _margin = new Vector2(24f, 24f);

        [Tooltip("室外缩放。**1 = 底图高度正好填满圆**（宽度溢出、被圆形裁掉）")]
        [Range(0.6f, 3f)]
        [SerializeField] private float _zoomOutdoor = 1f;

        [Tooltip("室内缩放（1.6 = 再放大，看清走廊）")]
        [Range(0.6f, 3f)]
        [SerializeField] private float _zoomIndoor = 1.6f;

        [Tooltip("缩放下限/上限（滚轮可调）")]
        [SerializeField] private Vector2 _zoomRange = new Vector2(0.8f, 2.6f);

        [Tooltip("楼层判定：y 高于这个值算 2F（本场景 2F 地面 ≈ 3.44，1F 吊顶 ≈ 3.4）")]
        [SerializeField] private float _floor2Height = 3.2f;

        [Tooltip("切房间的滞回（秒）——站在门框上时防止两帧内一进一出")]
        [SerializeField] private float _switchCooldown = 0.5f;

        [Tooltip("离最近的房间矩形多远以内仍算「室内」。用来兜住门洞、墙缝、房间表估算误差 —— " +
                 "房间矩形是照灯反推的，贴着墙站可能刚好落在矩形外，严格判定会掉成室外。")]
        [SerializeField] private float _indoorReach = 4f;

        // ---------------------------------------------------------------- 运行时

        private TwinMapAtlas _atlas;
        private RectTransform _root;        // 整个小地图（左上角锚定）
        private RectTransform _clip;        // 圆形裁剪区
        private RectTransform _map;         // 底图（尺寸 = 直径 × zoom）
        private Image _mapImage;
        private Text _label;                // "计算机教室 · 1F"
        private RectTransform _arrow;       // 玩家箭头（固定在圆心）

        private TwinMapAtlas.Entry _current;
        private string _currentZone = "";
        private int _currentFloor = -1;
        private float _zoom;
        private float _zoomTarget;
        private float _nextSwitchTime;
        private bool _warnedNoAtlas;

        /// <summary>
        /// 面板打开时用来整体淡出小地图。**不能用 SetActive**：小地图的根就是本对象，
        /// 关掉自己之后 Update 不再执行，就再也没有机会恢复。
        /// </summary>
        private CanvasGroup _group;

        /// <summary>上一次摆放的形态（false = 窄态左上 / true = 全屏 3D 右下）。只在变化时重摆。</summary>
        private bool _fullPlaced;
        private Sprite _circleSprite;

        /// <summary>每张底图只建一次 Sprite —— 每次切楼层都 new 一个会一直泄漏。</summary>
        private readonly Dictionary<TwinMapAtlas.Entry, Sprite> _sprites =
            new Dictionary<TwinMapAtlas.Entry, Sprite>(4);

        private int _shownFloor = -2;
        private string _shownZone = "";

        // ================================================================ 生命周期

        /// <summary>
        /// 场景里没有就自己补一个 —— 小地图是「看不见的功能」：没挂上不会有任何报错，
        /// 只是左上角空着（99 号清单第 15 条）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindObjectOfType<TwinMinimap>(true) != null) return;

            Canvas canvas = FindOverlayCanvas();
            GameObject go = new GameObject("TwinMinimap", typeof(RectTransform));
            go.transform.SetParent(canvas != null ? canvas.transform : null, false);
            go.AddComponent<TwinMinimap>();
            Debug.Log("[TwinMap] 场景里没有 TwinMinimap，已自动补一个（左上角圆形小地图）");
        }

        private static Canvas FindOverlayCanvas()
        {
            Canvas[] all = FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Canvas c = all[i];
                if (c != null && c.renderMode == RenderMode.ScreenSpaceOverlay) return c;
            }

            GameObject go = new GameObject("TwinMinimapCanvas", typeof(RectTransform));
            Canvas canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 90;

            CanvasScaler scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // 【P29-3.6】与主画布统一：1000×720 + Expand。
            // 原来是 1920×1080 + match 0.5 —— 与主画布（1000×720 / match 0）在 1069×654 下
            // 缩放比是 0.581 : 1.069，差 1.84 倍。一旦走到这个兜底分支（场景里一个
            // Overlay Canvas 都没有），小地图会只有正常尺寸的 54%，位置也偏。
            scaler.referenceResolution = new Vector2(1000f, 720f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.matchWidthOrHeight = 0f;   // Expand 下该值无效，归零避免误读

            if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        private void Awake()
        {
            _atlas = Resources.Load<TwinMapAtlas>("TwinMapAtlas");
            _zoom = _zoomTarget = _zoomOutdoor;
            Build();
        }

        private void OnDestroy()
        {
            foreach (KeyValuePair<TwinMapAtlas.Entry, Sprite> kv in _sprites)
                if (kv.Value != null) Destroy(kv.Value);
            _sprites.Clear();

            if (_circleSprite != null && _circleSprite.texture != null) Destroy(_circleSprite.texture);
            if (_circleSprite != null) Destroy(_circleSprite);
        }

        // ================================================================ 构建界面

        private void Build()
        {
            _root = transform as RectTransform;
            if (_root == null)
            {
                Debug.LogError("[TwinMap] TwinMinimap 必须挂在有 RectTransform 的物体上。");
                return;
            }

            // 根节点：左上角锚定
            _root.anchorMin = new Vector2(0f, 1f);
            _root.anchorMax = new Vector2(0f, 1f);
            _root.pivot = new Vector2(0f, 1f);
            _root.sizeDelta = new Vector2(_diameter, _diameter + 30f);
            _root.anchoredPosition = new Vector2(_margin.x, -_margin.y);

            // 圆形裁剪区
            Image clip = TwinUIFactory.Block(_root, "Clip", Color.white, true);
            _clip = clip.rectTransform;
            TwinUIFactory.Place(_clip, 0f, 0f, _diameter, _diameter);
            clip.sprite = MakeCircleSprite(256, 2f);
            clip.type = Image.Type.Simple;
            Mask mask = clip.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            // 底图
            _mapImage = TwinUIFactory.Block(_clip, "Map", Color.white, false);
            _map = _mapImage.rectTransform;
            _map.anchorMin = _map.anchorMax = new Vector2(0.5f, 0.5f);
            _map.pivot = new Vector2(0.5f, 0.5f);
            _map.sizeDelta = new Vector2(_diameter, _diameter);

            // 玩家箭头（圆心的三角）
            Image arrow = TwinUIFactory.Block(_clip, "Arrow", new Color(0.16f, 0.72f, 0.52f, 1f), false);
            _arrow = arrow.rectTransform;
            _arrow.anchorMin = _arrow.anchorMax = new Vector2(0.5f, 0.5f);
            _arrow.pivot = new Vector2(0.5f, 0.5f);
            _arrow.sizeDelta = new Vector2(16f, 16f);
            arrow.sprite = MakeArrowSprite(32);

            // 边框（画在最上层，盖住裁剪边缘）
            TwinRoundedRect border = TwinUIFactory.Rounded(_root, "Border", _diameter * 0.5f,
                new Color(1f, 1f, 1f, 0f), new Color(1f, 1f, 1f, 0f),
                new Color(0.10f, 0.11f, 0.12f, 0.55f), 2f, false);
            TwinUIFactory.Place(border.rectTransform, 0f, 0f, _diameter, _diameter);

            // 房间名
            _label = TwinUIFactory.Label(_root, "RoomLabel", 14, TextAnchor.MiddleLeft,
                new Color(0.12f, 0.13f, 0.14f, 1f));
            TwinUIFactory.Place(_label.rectTransform, 4f, _diameter + 6f, _diameter - 8f, 22f);
            _label.text = "";

            // 北向标记
            Text north = TwinUIFactory.Label(_root, "North", 12, TextAnchor.MiddleCenter,
                new Color(0.10f, 0.11f, 0.12f, 0.75f));
            TwinUIFactory.Place(north.rectTransform, _diameter * 0.5f - 8f, 2f, 16f, 16f);
            north.text = "N";
            north.fontStyle = FontStyle.Bold;
        }

        // ================================================================ 每帧

        /// <summary>
        /// 全屏态把小地图摆到 3D 区右下角（P29-6 §7：三栏下唯一露着 3D 的地方）。
        ///
        /// 抽屉全屏锁 1000×720 并**居中**，所以必须先算出抽屉左缘在画布里的偏移，
        /// 再按三栏坐标（3D 区 x 200–800、y 80–660）取右下角。
        /// 直接写死 620/450 会在画布宽 ≠ 1000 时贴错位置 —— Expand 下画布宽是变的。
        /// </summary>
        private void ApplyFullPlacement(bool full)
        {
            if (_root == null) return;
            if (_fullPlaced == full) return;
            _fullPlaced = full;

            if (!full)
            {
                _root.anchoredPosition = new Vector2(_margin.x, -_margin.y);
                return;
            }

            RectTransform parent = _root.parent as RectTransform;
            float canvasW = parent != null ? parent.rect.width : 1000f;
            float drawerLeft = Mathf.Max(0f, (canvasW - 1000f) * 0.5f);

            float h = _diameter + 30f;      // 圆 + 房间名那一行
            float x = drawerLeft + 800f - 20f - _diameter;
            float y = 660f - 20f - h;

            _root.anchoredPosition = new Vector2(x, -y);
        }

        private void Update()
        {
            if (_root == null || _mapImage == null) return;

            // 【P29-4】抽屉/详情卡打开时让位。
            // 小地图是独立的 Overlay Canvas，sortingOrder = 90，而主画布（抽屉所在）是 0 ——
            // 所以它**永远压在面板之上**。抽屉只有 420 宽时两者不重叠，看不出来；
            // 一点最大化铺满整屏，小地图就浮在面板上面了。
            // 【P29-6 §7】但**全屏态要显示**，只是移到 3D 区右下角 —— 那里是三栏布局里
            // 唯一露着 3D 的地方，地图放那儿既能看到自己位置、也不挡数据栏。
            bool full = TwinDrawer.IsFullScreen;
            bool occluded = TwinUIState.Current != TwinUILayer.None && !full;

            ApplyFullPlacement(full);

            if (_group == null)
            {
                _group = GetComponent<CanvasGroup>();
                if (_group == null) _group = gameObject.AddComponent<CanvasGroup>();
            }
            float wantAlpha = occluded ? 0f : 1f;
            if (!Mathf.Approximately(_group.alpha, wantAlpha))
            {
                _group.alpha = wantAlpha;
                _group.blocksRaycasts = !occluded;
            }
            if (occluded) return;

            if (_atlas == null)
            {
                if (!_warnedNoAtlas)
                {
                    _warnedNoAtlas = true;
                    Debug.LogWarning("[TwinMap] 找不到 Resources/TwinMapAtlas.asset —— " +
                                     "小地图底图没烘焙。菜单：P21 地图 / ① 烘焙三张底图。");
                }
                return;
            }

            Vector3 pos;
            float yaw;
            if (!TryGetPose(out pos, out yaw)) return;

            // 楼层 / 房间（带滞回，门框上不来回闪）
            int floor;
            string zone;
            ResolveZone(pos, out floor, out zone);

            TwinMapAtlas.Entry entry = _atlas.GetByFloor(floor) ?? _atlas.GetByFloor(1);
            if (entry == null) return;

            if (entry != _current)
            {
                _current = entry;
                _mapImage.sprite = GetEntrySprite(entry);
                _mapImage.type = Image.Type.Simple;
                // 室外看全貌、室内放大 —— 整张图里一栋楼只占一小块，不放大看不清走廊
                _zoom = _zoomTarget = (floor <= 0 ? _zoomOutdoor : _zoomIndoor);
            }

            if (floor != _shownFloor || !string.Equals(zone, _shownZone, StringComparison.Ordinal))
            {
                _shownFloor = floor;
                _shownZone = zone;
                _label.text = floor <= 0
                    ? "校园"
                    : TwinSiteLayout.DisplayName(zone) + " · " + floor + "F";
            }

            // 缩放缓动
            if (Mathf.Abs(_zoom - _zoomTarget) > 0.001f)
            {
                _zoom = Mathf.Lerp(_zoom, _zoomTarget, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
            }

            // 底图：按「玩家在整图里的归一化位置」反向平移，让玩家永远落在圆心。
            // 推导：想让图上的点 (px,py)（图内左上原点的像素坐标）落到裁剪区中心 (d/2,d/2)，
            //       底图中心相对裁剪区中心 = (mapW/2 - px, mapH/2 - py)，再把 y 翻成 UGUI 的向上为正。
            // 【尺寸：让底图**高度**填满圆，宽度溢出被裁】
            // 底图是 2048×966（2.12:1）。如果按"宽度=直径"算，高度只有直径的 47%，
            // 圆里上下会各空一大块 —— 这就是第一版"地图没撑满"的原因。
            // 改成高度=直径×zoom，宽度按长宽比溢出，圆形 Mask 自然裁掉两侧。
            Vector2 uv = entry.WorldToUv(pos);
            float mapH = _diameter * _zoom;
            float mapW = mapH * (entry.Texture.width / (float)entry.Texture.height);
            _map.sizeDelta = new Vector2(mapW, mapH);

            float px = uv.x * mapW;
            float py = (1f - uv.y) * mapH;          // uv 原点在左下、UI 原点在左上 → 翻一次

            float offX = mapW * 0.5f - px;
            float offY = py - mapH * 0.5f;

            // 【地图边缘：滚到边界就停住，让人物箭头自己往边缘走】
            // 这就是原神/王者的做法 —— 小地图只在"地图还盖得住圆"的范围内跟随，
            // 走到图外时不再平移，否则圆里就会露出图外的空白。
            float limX = Mathf.Max(0f, (mapW - _diameter) * 0.5f);
            float limY = Mathf.Max(0f, (mapH - _diameter) * 0.5f);
            offX = Mathf.Clamp(offX, -limX, limX);
            offY = Mathf.Clamp(offY, -limY, limY);
            _map.anchoredPosition = new Vector2(offX, offY);

            // 箭头不再恒在圆心：按"夹住之后"的实际位置算，并限制在圆内
            float ax = offX - mapW * 0.5f + px;
            float ay = offY + mapH * 0.5f - py;
            float maxA = _diameter * 0.5f - 10f;
            float alen = Mathf.Sqrt(ax * ax + ay * ay);
            if (alen > maxA && alen > 0.0001f)
            {
                ax = ax / alen * maxA;
                ay = ay / alen * maxA;
            }
            _arrow.anchoredPosition = new Vector2(ax, ay);

            // 箭头随朝向转（北向上：地图不转，只转箭头）
            _arrow.localRotation = Quaternion.Euler(0f, 0f, -yaw);
        }

        /// <summary>位置与朝向。有角色读角色，没有就退回主相机。</summary>
        private bool TryGetPose(out Vector3 pos, out float yaw)
        {
            pos = Vector3.zero;
            yaw = 0f;

            // 没配 _player 就自动找角色 —— 小地图是运行时自举的，
            // 场景里没有实例可以让你在 Inspector 里拖引用，所以必须自己找。
            if (_player == null)
            {
                SchoolTwin.Player.TwinPlayerController pc =
                    FindObjectOfType<SchoolTwin.Player.TwinPlayerController>();
                if (pc != null) _player = pc.transform;
            }

            if (_player != null)
            {
                pos = _player.position;
                Vector3 f = _player.forward; f.y = 0f;
                if (f.sqrMagnitude > 0.0001f) yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                return true;
            }

            Camera cam = Camera.main;
            if (cam == null) return false;
            pos = cam.transform.position;
            Vector3 cf = cam.transform.forward; cf.y = 0f;
            if (cf.sqrMagnitude > 0.0001f) yaw = Mathf.Atan2(cf.x, cf.z) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>
        /// 判定楼层与房间。规则：
        ///   ① 先按 y 定楼层（高于 _floor2Height 算 2F）
        ///   ② 用 XZ 命中该楼层的分区矩形；命中 = 室内
        ///   ③ 一个都不命中 = 室外（显示校园总览）
        /// 切换加滞回：_switchCooldown 内不重复切，避免门框上抖动。
        /// </summary>
        private void ResolveZone(Vector3 pos, out int floor, out string zone)
        {
            int rawFloor = pos.y >= _floor2Height ? 2 : 1;
            float dist;
            string rawZone = FindZone(rawFloor, pos, out dist);

            // 【室内判定不看"是否严格落在矩形内"，看"离最近的房间有多远"】
            // 房间矩形是照灯反推的，贴着墙站常常刚好落在矩形外 0.2~1m；
            // 门洞、墙缝同理。严格判定会让玩家一走到门口就掉成"室外"，小地图整块换图。
            // 所以：离最近房间 ≤ _indoorReach 就算室内，房间名取最近的那间。
            int raw = (!string.IsNullOrEmpty(rawZone) && dist <= _indoorReach) ? rawFloor : 0;

            if (_currentFloor >= 0)
            {
                bool same = raw == _currentFloor && (raw == 0 || string.Equals(rawZone, _currentZone, StringComparison.Ordinal));
                if (same) { floor = _currentFloor; zone = _currentZone; return; }

                // 滞回：刚切过就不许再切，防止站在门框上两帧内一进一出
                if (Time.unscaledTime < _nextSwitchTime) { floor = _currentFloor; zone = _currentZone; return; }
            }

            _currentFloor = raw;
            _currentZone = rawZone;
            _nextSwitchTime = Time.unscaledTime + _switchCooldown;
            floor = raw;
            zone = rawZone;
        }

        /// <summary>
        /// 找该楼层**离玩家最近的房间**，并给出到它矩形的距离（在矩形内 = 0）。
        /// 重叠时取中心最近的（和 TwinExpandPoints 同口径）。
        /// </summary>
        private static string FindZone(int floor, Vector3 pos, out float dist)
        {
            IReadOnlyList<TwinRoomInfo> rooms = TwinSiteLayout.Rooms;
            string bestZone = "";
            float bestCenter = float.MaxValue;
            dist = float.MaxValue;

            for (int i = 0; i < rooms.Count; i++)
            {
                TwinRoomInfo info = rooms[i];
                if (info == null || info.Floor != floor) continue;

                float x0, x1, z0, z1;
                if (!TwinRoomBounds.TryGetBounds(info.Zone, out x0, out x1, out z0, out z1)) continue;

                // 到矩形的距离（内部为 0）
                float dx = Mathf.Max(0f, Mathf.Max(x0 - pos.x, pos.x - x1));
                float dz = Mathf.Max(0f, Mathf.Max(z0 - pos.z, pos.z - z1));
                float d = Mathf.Sqrt(dx * dx + dz * dz);

                // 距离相同再比中心，保证结果稳定
                float cx = (x0 + x1) * 0.5f, cz = (z0 + z1) * 0.5f;
                float dc = (pos.x - cx) * (pos.x - cx) + (pos.z - cz) * (pos.z - cz);

                if (d < dist - 0.0001f || (Mathf.Abs(d - dist) <= 0.0001f && dc < bestCenter))
                {
                    dist = d;
                    bestCenter = dc;
                    bestZone = info.Zone;
                }
            }
            return bestZone;
        }

        // ================================================================ 滚轮缩放

        public void OnScroll(PointerEventData eventData)
        {
            float delta = eventData.scrollDelta.y;
            if (Mathf.Abs(delta) < 0.01f) return;

            _zoomTarget = Mathf.Clamp(_zoomTarget + delta * 0.15f, _zoomRange.x, _zoomRange.y);
            eventData.Use();
        }

        // ================================================================ 贴图

        private Sprite GetEntrySprite(TwinMapAtlas.Entry entry)
        {
            Sprite sp;
            if (_sprites.TryGetValue(entry, out sp) && sp != null) return sp;
            if (entry == null || entry.Texture == null) return null;

            sp = Sprite.Create(entry.Texture,
                new Rect(0f, 0f, entry.Texture.width, entry.Texture.height),
                new Vector2(0.5f, 0.5f), 100f);
            _sprites[entry] = sp;
            return sp;
        }

        /// <summary>运行时生成圆形遮罩贴图（白，圆内 alpha=1、圆外 0，边缘抗锯齿）。</summary>
        private Sprite MakeCircleSprite(int size, float soft)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "TwinMinimapCircle";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Color[] px = new Color[size * size];
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r;
                    float dy = y + 0.5f - r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((r - d) / Mathf.Max(0.5f, soft));
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>三角形箭头（朝上 = 北）。</summary>
        private Sprite MakeArrowSprite(int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "TwinMinimapArrow";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Color[] px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // 归一化到 [-1,1]，y 向上
                    float u = (x + 0.5f) / size * 2f - 1f;
                    float v = (y + 0.5f) / size * 2f - 1f;
                    // 三角形：顶点在 (0,1)，底边在 y=-0.55，半宽 0.62
                    float t = (v + 0.55f) / 1.55f;
                    bool inside = v >= -0.55f && v <= 1f && Mathf.Abs(u) <= 0.62f * (1f - t);
                    px[y * size + x] = inside ? Color.white : new Color(1f, 1f, 1f, 0f);
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
