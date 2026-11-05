const { badge } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  s.background = { color: theme.bg };
  s.addShape(pres.shapes.RECTANGLE, { x: 0, y: 0, w: 10, h: 0.08, fill: { color: theme.accent } });
  s.addShape(pres.shapes.RECTANGLE, { x: 0, y: 5.545, w: 10, h: 0.08, fill: { color: theme.accent } });
  s.addShape(pres.shapes.OVAL, { x: 7.9, y: -1.4, w: 4.2, h: 4.2, fill: { color: theme.light }, line: { color: "1E3A57", width: 1 } });
  s.addShape(pres.shapes.OVAL, { x: 8.5, y: -0.8, w: 3.0, h: 3.0, fill: { type: "none" }, line: { color: theme.accent, width: 1.2 } });
  s.addText("SUCCEED TEAM · 毕业实习答辩", { x: 0.7, y: 1.05, w: 7, h: 0.35, fontSize: 13, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, charSpacing: 3 });
  s.addText("校园数字孪生管理系统", { x: 0.7, y: 1.5, w: 8.6, h: 1.0, fontSize: 42, fontFace: "Microsoft YaHei", color: theme.primary, bold: true });
  s.addText("1 号教学楼 · 71 个设备点位 · 五大管理模块 · 实时监测与远程控制", { x: 0.7, y: 2.55, w: 8.6, h: 0.4, fontSize: 15, fontFace: "Microsoft YaHei", color: theme.secondary });
  s.addShape(pres.shapes.LINE, { x: 0.7, y: 3.25, w: 3.4, h: 0, line: { color: theme.accent, width: 2 } });
  s.addText([
    { text: "汇报人：何超臣（组长，代表全队）", options: { breakLine: true } },
    { text: "团队：Succeed 团队（7 人）", options: { breakLine: true } },
    { text: "实习单位：新开普电子股份有限公司", options: {} },
  ], { x: 0.7, y: 3.45, w: 6, h: 1.0, fontSize: 13, fontFace: "Microsoft YaHei", color: theme.secondary, lineSpacingMultiple: 1.35 });
  s.addText("2026.11.06", { x: 7.2, y: 4.9, w: 2.3, h: 0.35, fontSize: 12, fontFace: "Microsoft YaHei", color: theme.accent, align: "right" });
  return s;
}
module.exports = { createSlide, slideConfig: { type: "cover", index: 1 } };
