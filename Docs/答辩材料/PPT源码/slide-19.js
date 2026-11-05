const { badge } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  s.background = { color: theme.bg };
  s.addShape(pres.shapes.RECTANGLE, { x: 0, y: 0, w: 10, h: 0.08, fill: { color: theme.accent } });
  s.addShape(pres.shapes.RECTANGLE, { x: 0, y: 5.545, w: 10, h: 0.08, fill: { color: theme.accent } });
  s.addShape(pres.shapes.OVAL, { x: -1.6, y: 3.4, w: 3.6, h: 3.6, fill: { color: theme.light }, line: { color: "1E3A57", width: 1 } });
  s.addText("致谢", { x: 0.7, y: 1.5, w: 8.6, h: 0.9, fontSize: 40, fontFace: "Microsoft YaHei", color: theme.primary, bold: true });
  s.addText([
    { text: "感谢实习单位新开普电子股份有限公司提供的工程实践平台；", options: { breakLine: true, paraSpaceAfter: 6 } },
    { text: "感谢指导老师在全过程的方向把关与验收要求；", options: { breakLine: true, paraSpaceAfter: 6 } },
    { text: "感谢 Succeed 团队七位成员在天气系统、UI 设计、场景资产、交互反馈、演示制作、文档交付上的分工协作。", options: { breakLine: true, paraSpaceAfter: 10 } },
    { text: "请各位老师批评指正。", options: { bold: true, color: theme.accent } },
  ], { x: 0.7, y: 2.6, w: 8.6, h: 1.8, fontSize: 14, fontFace: "Microsoft YaHei", color: theme.secondary, lineSpacingMultiple: 1.25 });
  s.addText("Succeed 团队 · 何超臣 代表全队汇报 · 2026.11.06", { x: 0.7, y: 4.85, w: 8.6, h: 0.35, fontSize: 12, fontFace: "Microsoft YaHei", color: theme.accent });
  badge(s, pres, theme, 19);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "end", index: 18 } };
