namespace SchoolTwin.Weather
{
    /// <summary>
    /// 昼夜三档。**这是视觉档，不是数据档** —— 数据侧的 <see cref="SchoolTwin.Data.TwinClimate"/>
    /// 只有"一个连续的小时数"，没有 Day/Dusk/Night 这种离散概念。
    /// 两者由 <c>TwinWeatherBridge</c> 做映射，见那里的小时分段表。
    ///
    /// 队员包里这两个枚举写在 Models.cs，但那个文件**不在 12 文件清单里**，
    /// 所以搬过来时新建了本文件。数值顺序必须与队员包一致（Day=0/Dusk=1/Night=2），
    /// 否则 EnvSample 的 7 档预设虽然不受影响，但任何按序号存盘的东西会错位。
    /// </summary>
    public enum TimeOfDay
    {
        Day = 0,
        Dusk = 1,
        Night = 2
    }

    /// <summary>
    /// 天气七档（视觉档）。**注意 7 档 ≠ 数据层的 4 档**：
    ///
    ///   视觉（本枚举，7）  Clear / Cloudy / Overcast / Rain / Storm / Fog / Snow
    ///   数据（TwinClimate.Weather，4） Sunny / Cloudy / Rainy / Snowy
    ///
    /// 两套档位各有存在理由，**都不能删**：
    ///   · 7 档是"天空看起来什么样"，阴和雾、雨和雷雨在天空/雾/粒子上是四套完全不同的参数；
    ///   · 4 档是"对室温有什么影响"，阴和雾对气温的作用几乎一样（都是日照少、日较差小），
    ///     雷雨与雨的差别只在雨量，室温模型没必要为它分叉。
    ///
    /// 所以映射是**多对一 + 一对多并存**的，见 <c>TwinWeatherBridge</c>。
    /// 顺序同样必须与队员包一致（Clear=0 … Snow=6）。
    /// </summary>
    public enum Weather
    {
        Clear = 0,
        Cloudy = 1,
        Overcast = 2,
        Rain = 3,
        Storm = 4,
        Fog = 5,
        Snow = 6
    }
}