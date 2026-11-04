using UnityEngine;
using CampusUI.Core;

namespace CampusUI.Environment
{
    /// <summary>
    /// 一整套「环境外观」的数值快照 —— 既当作天气预设表，也当作过渡插值的载体。
    ///
    /// 为什么用同一个结构体做两件事：
    ///   天气切换要做 2~3 秒的平滑过渡，必须有一个"从哪儿到哪儿"的可插值中间态。
    ///   如果预设和运行态分成两个类型，就得写两套字段 + 一个逐字段搬运的转换函数，
    ///   加一个参数要改三处。现在只有一个结构体 + 一个 Lerp，加参数只改这里。
    ///
    /// 注意：这里的数值都是「白天基准」，夜晚/黄昏的差异由 EnvironmentState.Build() 叠乘。
    /// </summary>
    public struct EnvSample
    {
        // ---- 天空盒（Skybox/Procedural）----
        public Color skyTint;
        public float exposure;        // _Exposure
        public float atmosphere;      // _AtmosphereThickness
        public float sunDisk;         // _SunSize，天空里那个太阳圆盘的大小

        // ---- 主光（平行光）----
        public Color sunColor;
        public float sunIntensity;

        // ---- 环境光 ----
        public float ambientIntensity;

        // ---- 雾（RenderSettings，URP 支持内置线性/指数雾）----
        public float fogDensity;      // 0 = 关雾
        public Color fogColor;

        // ---- 粒子 / 云 ----
        public float rain;            // 0~1.6 雨强
        public float snow;            // 0~1.2 雪强
        public float wind;            // 0~1 风（影响粒子横向漂移与风声）
        public float cloud;           // 0~1 云穹顶不透明度
        public Color cloudColor;

        // ---- 特殊 ----
        public float lightning;       // 0 = 无雷，1 = 雷雨
        public float star;            // 0 = 白天（不显示星空），1 = 夜空满天星

        // =====================================================================
        // 预设表
        // =====================================================================

        public static EnvSample Get(Weather w)
        {
            switch (w)
            {
                case Weather.Cloudy: return Cloudy;
                case Weather.Overcast: return Overcast;
                case Weather.Rain: return Rain;
                case Weather.Storm: return Storm;
                case Weather.Fog: return Fog;
                case Weather.Snow: return Snow;
                default: return Clear;
            }
        }

        public static readonly EnvSample Clear = new EnvSample
        {
            skyTint = new Color(0.45f, 0.62f, 0.92f),
            exposure = 1.25f,
            atmosphere = 0.85f,
            sunDisk = 0.040f,
            sunColor = new Color(1f, 0.96f, 0.88f),
            sunIntensity = 1.25f,
            ambientIntensity = 0.85f,
            fogDensity = 0f,
            fogColor = new Color(0.70f, 0.75f, 0.80f),
            rain = 0f, snow = 0f, wind = 0.08f,
            cloud = 0f,
            cloudColor = new Color(1f, 1f, 1f),
            lightning = 0f,
            star = 0f
        };

        public static readonly EnvSample Cloudy = new EnvSample
        {
            skyTint = new Color(0.55f, 0.66f, 0.82f),
            exposure = 1.05f,
            atmosphere = 1.00f,
            sunDisk = 0.030f,
            sunColor = new Color(1f, 0.95f, 0.88f),
            sunIntensity = 0.85f,
            ambientIntensity = 0.80f,
            fogDensity = 0f,
            fogColor = new Color(0.66f, 0.70f, 0.75f),
            rain = 0f, snow = 0f, wind = 0.22f,
            cloud = 0.28f,
            cloudColor = new Color(0.95f, 0.96f, 0.98f),
            lightning = 0f,
            star = 0f
        };

        public static readonly EnvSample Overcast = new EnvSample
        {
            skyTint = new Color(0.56f, 0.58f, 0.63f),
            exposure = 0.85f,
            atmosphere = 1.10f,
            sunDisk = 0.012f,
            sunColor = new Color(0.86f, 0.88f, 0.92f),
            sunIntensity = 0.42f,
            ambientIntensity = 0.70f,
            fogDensity = 0.0020f,
            fogColor = new Color(0.62f, 0.65f, 0.69f),
            rain = 0f, snow = 0f, wind = 0.32f,
            cloud = 0.55f,
            cloudColor = new Color(0.72f, 0.74f, 0.78f),
            lightning = 0f,
            star = 0f
        };

        public static readonly EnvSample Rain = new EnvSample
        {
            skyTint = new Color(0.42f, 0.47f, 0.55f),
            exposure = 0.70f,
            atmosphere = 1.15f,
            sunDisk = 0.008f,
            sunColor = new Color(0.72f, 0.78f, 0.88f),
            sunIntensity = 0.26f,
            ambientIntensity = 0.60f,
            fogDensity = 0.0055f,
            fogColor = new Color(0.55f, 0.59f, 0.64f),
            rain = 1.0f, snow = 0f, wind = 0.50f,
            cloud = 0.72f,
            cloudColor = new Color(0.55f, 0.58f, 0.63f),
            lightning = 0f,
            star = 0f
        };

        public static readonly EnvSample Storm = new EnvSample
        {
            skyTint = new Color(0.26f, 0.29f, 0.36f),
            exposure = 0.55f,
            atmosphere = 1.20f,
            sunDisk = 0.006f,
            sunColor = new Color(0.58f, 0.62f, 0.74f),
            sunIntensity = 0.16f,
            ambientIntensity = 0.50f,
            fogDensity = 0.0075f,
            fogColor = new Color(0.42f, 0.45f, 0.50f),
            rain = 1.6f, snow = 0f, wind = 0.90f,
            cloud = 0.86f,
            cloudColor = new Color(0.35f, 0.38f, 0.44f),
            lightning = 1f,
            star = 0f
        };

        public static readonly EnvSample Fog = new EnvSample
        {
            skyTint = new Color(0.64f, 0.66f, 0.68f),
            exposure = 0.90f,
            atmosphere = 1.05f,
            sunDisk = 0.010f,
            sunColor = new Color(0.92f, 0.92f, 0.92f),
            sunIntensity = 0.45f,
            ambientIntensity = 0.78f,
            fogDensity = 0.0140f,
            fogColor = new Color(0.74f, 0.76f, 0.78f),
            rain = 0f, snow = 0f, wind = 0.05f,
            cloud = 0.40f,
            cloudColor = new Color(0.80f, 0.82f, 0.84f),
            lightning = 0f,
            star = 0f
        };

        public static readonly EnvSample Snow = new EnvSample
        {
            skyTint = new Color(0.68f, 0.73f, 0.82f),
            exposure = 0.95f,
            atmosphere = 1.10f,
            sunDisk = 0.014f,
            sunColor = new Color(0.95f, 0.97f, 1f),
            sunIntensity = 0.52f,
            ambientIntensity = 0.85f,
            fogDensity = 0.0070f,
            fogColor = new Color(0.79f, 0.83f, 0.88f),
            rain = 0f, snow = 1.0f, wind = 0.20f,
            cloud = 0.62f,
            cloudColor = new Color(0.82f, 0.85f, 0.90f),
            lightning = 0f,
            star = 0f
        };

        // =====================================================================
        // 插值
        // =====================================================================

        public static EnvSample Lerp(EnvSample a, EnvSample b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;

            EnvSample s;
            s.skyTint = Color.Lerp(a.skyTint, b.skyTint, t);
            s.exposure = Mathf.Lerp(a.exposure, b.exposure, t);
            s.atmosphere = Mathf.Lerp(a.atmosphere, b.atmosphere, t);
            s.sunDisk = Mathf.Lerp(a.sunDisk, b.sunDisk, t);
            s.sunColor = Color.Lerp(a.sunColor, b.sunColor, t);
            s.sunIntensity = Mathf.Lerp(a.sunIntensity, b.sunIntensity, t);
            s.ambientIntensity = Mathf.Lerp(a.ambientIntensity, b.ambientIntensity, t);
            s.fogDensity = Mathf.Lerp(a.fogDensity, b.fogDensity, t);
            s.fogColor = Color.Lerp(a.fogColor, b.fogColor, t);
            s.rain = Mathf.Lerp(a.rain, b.rain, t);
            s.snow = Mathf.Lerp(a.snow, b.snow, t);
            s.wind = Mathf.Lerp(a.wind, b.wind, t);
            s.cloud = Mathf.Lerp(a.cloud, b.cloud, t);
            s.cloudColor = Color.Lerp(a.cloudColor, b.cloudColor, t);
            s.lightning = Mathf.Lerp(a.lightning, b.lightning, t);
            s.star = Mathf.Lerp(a.star, b.star, t);
            return s;
        }
    }
}
