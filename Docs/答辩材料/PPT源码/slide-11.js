const { badge, header, card } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "05 成员项目工作汇报", "六位组员的专项贡献");
  const tiles = [
    ["杨续 · 天气系统", "程序化雨雪/天空/积雪积水（8 脚本 + 4 Shader + 1200 行文档），零外部素材；支撑融合联调"],
    ["赵朕 · UI 设计", "视觉 token 体系与 10 张设计稿；时间天气面板图纸与试绘；先图后码流程"],
    ["张峻才 · 场景资产", "场景素材收集归类与轻量化（合批/实例化）；门灯交互联调；演示素材录制"],
    ["王绍锋 · 交互与视觉", "空调气流粒子+程序化嗡鸣反馈；发黑缺陷根因定位（法线投影为零）修 12 处"],
    ["李文玉 · 演示制作", "演示视频剪辑与字幕解说；同类孪生项目调研协作文档（5 家商业平台对照）"],
    ["王雯信 · 文档与交付", "82 条踩坑日志七类归档与索引；答辩素材命名规范；性能调试与打包"],
  ];
  tiles.forEach((t, i) => {
    const x = 0.6 + (i % 2) * 4.6, y = 1.55 + Math.floor(i / 2) * 1.18;
    card(s, pres, theme, x, y, 4.3, 1.05);
    s.addShape(pres.shapes.RECTANGLE, { x, y, w: 4.3, h: 0.36, fill: { color: theme.light } });
    s.addText(t[0], { x: x + 0.15, y, w: 4.0, h: 0.36, fontSize: 12, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, valign: "middle" });
    s.addText(t[1], { x: x + 0.15, y: y + 0.4, w: 4.0, h: 0.6, fontSize: 10.5, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.08 });
  });
  s.addText("每人职责在 PPT、现场发言、实习报告三处逐字一致；全部贡献均有仓库内真实产物可查。", { x: 0.6, y: 5.12, w: 8.9, h: 0.32, fontSize: 11, fontFace: "Microsoft YaHei", color: theme.secondary, italic: true });
  badge(s, pres, theme, 11);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 10 } };
