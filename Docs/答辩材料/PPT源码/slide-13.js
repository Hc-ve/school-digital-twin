const { badge, header, card } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "06 项目踩坑汇报", "坑三与坑四：不会报错的错，最贵");
  const cases = [
    ["坑 3 · 横贯全屏的一根白线", "现象", "雪天屏幕中央一条稳定白线，盖住墙体与人物，位置恒定",
     "根因", "树积雪用世界坐标建网格却挂到每帧跟随相机的父节点下，雪盘被额外平移、恰好压在相机眼高，视线共面被透视压扁；叠加地形采样漏加 0.5m 偏移",
     "修法", "世界坐标对象脱离跟随父节点；排查口诀：横贯全屏的线 = 高度≈相机眼高的水平面"],
    ["坑 4 · 导入模型大面积发黑", "现象", "吊顶等结构件整体全黑，材质槽、渲染器开关、合并网格逐项排查均正常",
     "根因", "这些面下表面背对全部光源，烘焙光照按法线投影结果为零：图集有光、该 UV 区块无光",
     "修法", "运行时将 12 处结构件 lightmap 索引置 −1、改用实时光+环境光；代价（失去烘焙明暗）如实记入缺陷台账"],
  ];
  cases.forEach((c, i) => {
    const x = 0.6 + i * 4.6;
    card(s, pres, theme, x, 1.55, 4.3, 3.35);
    s.addShape(pres.shapes.RECTANGLE, { x, y: 1.55, w: 4.3, h: 0.5, fill: { color: theme.accent } });
    s.addText(c[0], { x: x + 0.12, y: 1.55, w: 4.06, h: 0.5, fontSize: 12.5, fontFace: "Microsoft YaHei", color: "06121F", bold: true, valign: "middle" });
    let y = 2.2;
    for (let k = 1; k < 6; k += 2) {
      s.addText(c[k], { x: x + 0.15, y, w: 0.62, h: 0.3, fontSize: 10.5, fontFace: "Microsoft YaHei", color: theme.accent, bold: true });
      s.addText(c[k + 1], { x: x + 0.8, y, w: 3.35, h: 0.78, fontSize: 10.5, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.08 });
      y += 0.86;
    }
  });
  s.addText("教训：现象与根因的距离比想象远；排查先查「最近新增了什么」，再穷举参数。", { x: 0.6, y: 5.05, w: 8.9, h: 0.35, fontSize: 12.5, fontFace: "Microsoft YaHei", color: theme.accent, bold: true });
  badge(s, pres, theme, 13);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 12 } };
