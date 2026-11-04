using UnityEngine;

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 天气自动轮换，对应《异环》天气系统的「随机模式」。
    ///
    /// 为什么不用纯随机：
    ///   纯随机会出现「晴天直接跳雷雨」这种不符合大气演进的情况，玩家一眼就看出是随机数。
    ///   这里用**天气转移权重链**——每种天气只允许转向"气象上说得通"的下一档，
    ///   于是自然会走成 晴 → 多云 → 阴 → 雨 → 雷雨 → 雨 → 阴 → 多云 → 晴 这样的循环。
    ///
    /// 停留时长在 90~200 秒之间随机。太短会显得天气在抽搐，太长玩家感受不到变化。
    /// </summary>
    public class WeatherDirector : MonoBehaviour
    {
        public static WeatherDirector Instance;

        [Tooltip("是否自动轮换天气（对应《异环》的随机模式）")]
        public bool auto;

        [Tooltip("每次天气停留的最短/最长秒数")]
        [SerializeField] private float minInterval = 90f;
        [SerializeField] private float maxInterval = 200f;

        private float timer;
        private EnvironmentState env;

        public float Remaining { get { return Mathf.Max(0f, timer); } }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>UI 用：找到或创建一个 WeatherDirector。</summary>
        public static WeatherDirector Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("WeatherDirector");
            return go.AddComponent<WeatherDirector>();
        }

        public void SetAuto(bool on)
        {
            auto = on;
            // 刚打开时不要等满 90 秒，否则玩家会以为按钮没生效
            if (on) timer = Random.Range(minInterval * 0.35f, minInterval * 0.75f);
        }

        void Update()
        {
            if (!auto) return;

            if (env == null)
            {
                env = EnvironmentState.Instance;
                if (env == null) env = FindObjectOfType<EnvironmentState>();
                if (env == null) return;
            }

            // 正在过渡时不要叠加新目标，否则会把 2.4 秒的平滑过渡打断成两段
            if (env.IsTransitioning) return;

            timer -= Time.deltaTime;
            if (timer > 0f) return;

            timer = Random.Range(minInterval, maxInterval);

            // 【P13 必须走桥，不能直调 env.SetWeather】
            // 直调只改了视觉侧，`TwinClimate` 那边还停在上一个天气，
            // 于是自动轮换到「雪」时，室温仍按旧天气算 —— 两条数据各自都"正常"，
            // 只有放一起看才矛盾。`TwinWeatherBridge.ApplyWeather` 在同一方法体内
            // 同时写两侧，是唯一不会只写一半的路径。
            SchoolTwin.Twin.TwinWeatherBridge.ApplyWeather(PickNext(env.weather));
        }

        // =====================================================================
        // 天气转移权重链
        // =====================================================================

        private static Weather PickNext(Weather cur)
        {
            Weather[] pool;
            float[] weights;
            Table(cur, out pool, out weights);

            float sum = 0f;
            for (int i = 0; i < weights.Length; i++) sum += weights[i];

            float r = Random.value * sum;
            for (int i = 0; i < pool.Length; i++)
            {
                r -= weights[i];
                if (r <= 0f) return pool[i];
            }
            return pool[pool.Length - 1];
        }

        /// <summary>
        /// 每种天气允许转向的下一档及权重。
        /// 设计依据：云量是渐变的（晴→多云→阴→雨），雷雨只会从雨里长出来，
        /// 雾和雪都需要特定的湿度/温度条件，所以不能直接连到晴天暴晒。
        /// </summary>
        private static void Table(Weather w, out Weather[] pool, out float[] weights)
        {
            switch (w)
            {
                case Weather.Clear:
                    pool = new[] { Weather.Cloudy, Weather.Clear, Weather.Fog };
                    weights = new[] { 0.60f, 0.25f, 0.15f };
                    return;

                case Weather.Cloudy:
                    pool = new[] { Weather.Overcast, Weather.Clear, Weather.Fog, Weather.Rain };
                    weights = new[] { 0.45f, 0.35f, 0.10f, 0.10f };
                    return;

                case Weather.Overcast:
                    pool = new[] { Weather.Rain, Weather.Cloudy, Weather.Snow, Weather.Fog };
                    weights = new[] { 0.45f, 0.35f, 0.10f, 0.10f };
                    return;

                case Weather.Rain:
                    pool = new[] { Weather.Overcast, Weather.Storm, Weather.Cloudy };
                    weights = new[] { 0.50f, 0.30f, 0.20f };
                    return;

                case Weather.Storm:
                    pool = new[] { Weather.Rain, Weather.Overcast };
                    weights = new[] { 0.60f, 0.40f };
                    return;

                case Weather.Fog:
                    pool = new[] { Weather.Clear, Weather.Cloudy };
                    weights = new[] { 0.50f, 0.50f };
                    return;

                default: // Weather.Snow
                    pool = new[] { Weather.Overcast, Weather.Cloudy };
                    weights = new[] { 0.60f, 0.40f };
                    return;
            }
        }
    }
}
