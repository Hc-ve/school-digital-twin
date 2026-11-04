using System.Text;
using UnityEditor;
using UnityEngine;
using SchoolTwin.Weather;

// 【别名：必须加，否则 2 处 CS0118】理由见 TwinP13WeatherSetup.cs 顶部注释
using WeatherKind = SchoolTwin.Weather.Weather;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 【P13】天气系统诊断：把"为什么看不到雨雪"的每一环都打出来。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么需要这个工具而不是继续读代码】
    ///
    /// "play 了但没下雨没下雪"有 7 个可能断点，逐个读代码排不出来，
    /// 因为它们全在**运行时**才能看出真假：
    ///
    ///   ① 场景里有没有 `EnvironmentState` 这个组件
    ///      —— 没有它，整套天气系统根本不运行（本工程历史遗留：从来没挂过）
    ///   ② `EnvironmentState.weather` 到底是什么值
    ///      —— 默认是 `Clear`(0)。没点过面板就永远是晴天，rain/snow 都是 0
    ///   ③ `WeatherVisuals` 建出来了没有
    ///      —— 它在 EnvironmentState.Awake 里 Ensure，没 Awake 就没有粒子
    ///   ④ `rainPs` / `snowPs` 的 emission.rateOverTime 是多少
    ///      —— 逻辑算对了但没写进去，或者 effRain 被 altFade/shelter 乘成 0
    ///   ⑤ `altFade` 是多少
    ///      —— 相机高度 60m 以下=1，130m 以上=0。第三人称相机若被误当成
    ///         数字孪生那台高空相机，altFade 直接归零，粒子一发都不出
    ///   ⑥ `indoor` 是不是 true
    ///      —— 头顶 40m 有遮挡就判室内，`indoorFade → 0`，
    ///         effRain/effSnow 乘 0.25 的 shelter，溅射和积雪**完全归零**
    ///   ⑦ 材质/Shader 是否可用
    ///      —— `Sprites/Default` 被剥离时会拿到粉色材质
    ///
    /// 这七条里，①②③ 是"系统根本没跑"，④⑤⑥ 是"跑了但输出被乘成 0"，
    /// ⑦ 是"跑了但看不见"。**三类的修法完全不同**，所以必须先分清是哪一类。
    /// </summary>
    public static class TwinP13WeatherDiag
    {
        [MenuItem("P13 时间天气/② 诊断：为什么看不到雨雪", false, 20)]
        public static void DiagMenu()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.AppendLine("[P13] ==== 天气系统诊断 ====");

            // ---------- ① 组件在不在 ----------
            sb.AppendLine();
            sb.AppendLine("[① 组件是否存在]");

            EnvironmentState env = Object.FindObjectOfType<EnvironmentState>(true);
            WeatherDirector dir = Object.FindObjectOfType<WeatherDirector>(true);
            WeatherVisuals vis = Object.FindObjectOfType<WeatherVisuals>(true);

            sb.AppendLine("  EnvironmentState : " + (env == null ? "❌ 不存在 ← 整套天气系统不会运行" : "✅ " + env.name));
            sb.AppendLine("  WeatherDirector  : " + (dir == null ? "❌ 不存在（自动轮换不会工作）" : "✅ auto=" + dir.auto));
            sb.AppendLine("  WeatherVisuals   : " + (vis == null ? "❌ 不存在（没有粒子）" : "✅ " + vis.name));

            if (env == null)
            {
                sb.AppendLine();
                sb.AppendLine("【结论】没有 EnvironmentState，天气系统从未启动。");
                sb.AppendLine();
                sb.AppendLine("  自举（TwinWeatherBootstrap）只在**有 TwinRuntime 的场景**里起作用，");
                sb.AppendLine("  因为它拿 TwinRuntime 当「这是不是孪生演示场景」的判据。");
                sb.AppendLine("  按 guid 查过，TwinRuntime 只在 SchoolSceneDay 里有一份。");
                sb.AppendLine();
                sb.AppendLine("  所以先确认两件事：");
                sb.AppendLine("    1) 当前打开的是不是 SchoolSceneDay（不是菜单/废弃场景）");
                sb.AppendLine("    2) 是不是 Play 模式 —— 自举只在运行时跑，编辑模式必然为空");
                sb.AppendLine();
                sb.AppendLine("  若两件都对还没有，说明 TwinWeatherBootstrap 没编译进去，");
                sb.AppendLine("  或它的重试器 5 秒到期前 TwinRuntime 一直没出现。");
                Debug.Log(sb.ToString());
                EditorUtility.DisplayDialog("P13 天气诊断", sb.ToString(), "好");
                return;
            }

            // ---------- ② 当前状态值 ----------
            sb.AppendLine();
            sb.AppendLine("[② 当前状态值]");
            sb.AppendLine("  timeOfDay = " + env.timeOfDay + "   weather = " + env.weather
                          + "（" + EnvironmentState.WeatherLabel(env.weather) + "）");
            sb.AppendLine("  数值：Clear=0 Cloudy=1 Overcast=2 Rain=3 Storm=4 Fog=5 Snow=6");
            if (env.weather != WeatherKind.Rain && env.weather != WeatherKind.Storm && env.weather != WeatherKind.Snow)
            {
                sb.AppendLine("  ⚠️ weather 不是雨/雷雨/雪 → rain/snow 预设值就是 0，粒子不可能出现。");
            }

            // ---------- ③ 相机与 altFade ----------
            sb.AppendLine();
            sb.AppendLine("[③ 相机与高度衰减]");

            Camera cam = EnvironmentState.FindActiveCamera();
            if (cam == null)
            {
                sb.AppendLine("  ❌ FindActiveCamera() 返回 null —— 所有跟随/衰减都停摆");
            }
            else
            {
                float h = cam.transform.position.y;
                // 与 WeatherVisuals.UpdateRange 完全同一公式，用来验算
                float altFade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(60f, 130f, h));
                sb.AppendLine("  相机 : " + cam.name + "  tag=" + cam.tag
                              + "  enabled=" + cam.isActiveAndEnabled + "  depth=" + cam.depth);
                sb.AppendLine("  位置 : " + cam.transform.position.ToString("F2") + "   高度=" + h.ToString("F2") + "m");
                sb.AppendLine("  altFade = " + altFade.ToString("F3")
                              + "（60m 以下=1，130m 以上=0）");
                if (altFade < 0.05f)
                {
                    sb.AppendLine("  ⚠️ altFade≈0 → 粒子发射率被乘成 0。相机太高了。");
                }

                Camera[] all = Camera.allCameras;
                sb.AppendLine("  场景里共 " + (all == null ? 0 : all.Length) + " 台启用相机：");
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        Camera c = all[i];
                        if (c == null) continue;
                        sb.AppendLine("    - " + c.name + "  depth=" + c.depth
                                      + "  y=" + c.transform.position.y.ToString("F1")
                                      + (c.targetTexture != null ? "  [渲染到贴图]" : "")
                                      + (c == cam ? "   ← 被选中" : ""));
                    }
                }
            }

            // ---------- ④ 室内判定与积雪量（**雪天没积雪就查这里**） ----------
            sb.AppendLine();
            sb.AppendLine("[④ 室内判定与积雪量]");
            sb.AppendLine("  以下是【运行时真实值】，不是用公式重算的猜测：");
            sb.AppendLine("    altFade    = " + vis.AltFadeNow.ToString("F3") + "   （1=正常，0=相机太高）");
            sb.AppendLine("    indoor     = " + vis.IndoorNow + "       （true=判定为室内）");
            sb.AppendLine("    indoorFade = " + vis.IndoorFadeNow.ToString("F3") + "   （1=户外，0=室内）");
            sb.AppendLine("    snowAccum  = " + vis.SnowAccumNow.ToString("F3") + "   （已积了多少雪，0~1）");
            sb.AppendLine("    snowAmount = " + vis.SnowAmountNow.ToString("F3") + "   （= snowAccum × altFade × indoorFade）");

            // 雪盘实体状态：区分"量算对了但物体没显示"和"量根本没算出来"
            sb.AppendLine();
            sb.AppendLine("  雪盘实体状态（这一组决定「地上到底有没有白」）：");
            sb.AppendLine("    雪盘存在   = " + (vis.SnowGroundT != null ? "✅ " + vis.SnowGroundT.name : "❌ null（没建出来）"));
            sb.AppendLine("    可见(含父级) = " + (vis.SnowGroundVisible ? "✅ 是" : "❌ 否 ← 物体被 SetActive(false) 了"));
            sb.AppendLine("    有材质     = " + (vis.SnowGroundHasMat ? "✅" : "❌ null"));
            sb.AppendLine("    Shader 正确 = " + (vis.SnowGroundShaderOk ? "✅ CampusUI/SnowGround" : "❌ 不是目标 Shader（会退成别的材质）"));
            sb.AppendLine("    材质内_SnowAmount = " + vis.SnowAmountInMat.ToString("F3")
                          + (vis.SnowAmountInMat < 0.01f ? "   ⚠️ 几乎为 0 → 片元里 smoothstep(0,0.22) 会全透明" : ""));
            sb.AppendLine("    雪盘世界坐标 = " + vis.SnowGroundWorldPos.ToString("F2")
                          + "   相机 y=" + (cam != null ? cam.transform.position.y.ToString("F2") : "?"));

            if (vis.IndoorNow)
            {
                sb.AppendLine();
                sb.AppendLine("  ⚠️⚠️ 判定为【室内】—— 积雪会恒为 0！");
                sb.AppendLine("      积雪量 = snowAccum × altFade × indoorFade，indoorFade=0 时恒为 0。");
                sb.AppendLine("      雨雪粒子还能看到一点，是因为它们乘的是 shelter = Lerp(0.25,1,indoorFade)，不是 0。");
                sb.AppendLine();
                sb.AppendLine("  误判原因（按可能性排序）：");
                sb.AppendLine("    1) 向上射线打到了玩家自己的手臂/模型 —— 已修：IsPlayerCollider 不再靠 cam.transform.root");
                sb.AppendLine("    2) 相机正好在树冠/路灯/建筑挑檐下方 40m 内（换开阔地再看）");
                sb.AppendLine("    3) 场景里有巨大的 Trigger 碰撞体（已排除 isTrigger）");
            }
            else if (vis.AltFadeNow < 0.05f)
            {
                sb.AppendLine();
                sb.AppendLine("  ⚠️ altFade ≈ 0 —— 相机高度超过 130m，雨雪被主动关掉了。");
            }
            else if (vis.SnowAmountInMat < 0.01f)
            {
                sb.AppendLine();
                sb.AppendLine("  ⚠️ 积雪量算出来了但没写进材质 —— 见上面 _SnowAmount。");
                sb.AppendLine("  若 snowAmount 有值而 _SnowAmount 是 0，说明 SetFloat 那段没执行到（on != snowGroundOn 分支）。");
            }
            else if (env.weather == WeatherKind.Snow && vis.SnowAccumNow < 0.05f)
            {
                sb.AppendLine();
                sb.AppendLine("  积雪量接近 0，但 indoor/altFade 都正常 ——");
                sb.AppendLine("  可能只是刚切到雪天不到 1 秒（积雪累积约 12 秒到满）。等 10 秒再看。");
            }

            // ---------- ⑤ 粒子系统实际状态 ----------
            sb.AppendLine();
            sb.AppendLine("[⑤ 粒子系统实测]");
            sb.AppendLine("  （Play 模式下才能读到真实发射率；非 Play 读到的是场景里的静态值）");
            AppendParticles(sb, vis);

            // ---------- ⑥ 材质 ----------
            sb.AppendLine();
            sb.AppendLine("[⑥ 材质/Shader]");
            sb.AppendLine("  Sprites/Default 可找到 : "
                          + (Shader.Find("Sprites/Default") != null ? "✅" : "❌ 剥离了，粒子会变粉"));

            // ---------- 结论 ----------
            sb.AppendLine();
            sb.AppendLine("[⑦ 一句话结论]");
            if (env.weather == WeatherKind.Clear)
            {
                sb.AppendLine("  天气是「晴」，rain/snow 预设都是 0 —— 这是最可能的原因。");
                sb.AppendLine("  点抽屉工具行「时间天气」→ 点「雨天」或「雪天」。");
            }

            string report = sb.ToString();
            Debug.Log(report);
            EditorUtility.DisplayDialog("P13 天气诊断", report, "好");
        }

        private static void AppendParticles(StringBuilder sb, WeatherVisuals vis)
        {
            if (vis == null) { sb.AppendLine("  （没有 WeatherVisuals，跳过）"); return; }

            ParticleSystem[] all = vis.GetComponentsInChildren<ParticleSystem>(true);
            if (all == null || all.Length == 0) { sb.AppendLine("  ❌ 子系统下一个粒子系统都没有"); return; }

            for (int i = 0; i < all.Length; i++)
            {
                ParticleSystem ps = all[i];
                if (ps == null) continue;
                var em = ps.emission;

                sb.AppendLine("  [" + ps.name + "]"
                              + "  激活=" + ps.gameObject.activeInHierarchy
                              + "  正在播=" + ps.isPlaying
                              + "  粒子数=" + ps.particleCount
                              + "  rate=" + em.rateOverTime.constant.ToString("F1")
                              + "  max=" + ps.main.maxParticles);

                ParticleSystemRenderer pr = ps.GetComponent<ParticleSystemRenderer>();
                if (pr != null)
                {
                    sb.AppendLine("      材质=" + (pr.sharedMaterial == null ? "❌ null" : pr.sharedMaterial.shader.name)
                                  + "  renderMode=" + pr.renderMode
                                  + "  bounds=" + pr.bounds.size.ToString("F1"));
                }
            }
        }
    }
}