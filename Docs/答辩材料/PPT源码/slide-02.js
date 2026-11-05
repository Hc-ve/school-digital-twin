const { badge } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  s.background = { color: theme.bg };
  s.addShape(pres.shapes.RECTANGLE, { x: 0, y: 0, w: 10, h: 0.06, fill: { color: theme.accent } });
  s.addText("CONTENTS", { x: 0.6, y: 0.4, w: 5, h: 0.3, fontSize: 11, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, charSpacing: 3 });
  s.addText("目录", { x: 0.6, y: 0.68, w: 5, h: 0.6, fontSize: 27, fontFace: "Microsoft YaHei", color: theme.primary, bold: true });
  const items = ["项目背景与意义", "项目简介", "项目策划", "成员分工总览", "成员项目工作汇报", "项目踩坑汇报", "项目成果展示", "总结不足与未来展望"];
  items.forEach((t, i) => {
    const col = i < 4 ? 0 : 1, row = i % 4;
    const x = 0.6 + col * 4.6, y = 1.6 + row * 0.92;
    s.addShape(pres.shapes.RECTANGLE, { x, y, w: 4.3, h: 0.76, fill: { color: theme.light }, line: { color: "1E3A57", width: 0.75 } });
    s.addText("0" + (i + 1), { x: x + 0.18, y, w: 0.7, h: 0.76, fontSize: 21, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, valign: "middle" });
    s.addText(t, { x: x + 0.95, y, w: 3.2, h: 0.76, fontSize: 15, fontFace: "Microsoft YaHei", color: theme.primary, bold: true, valign: "middle" });
  });
  badge(s, pres, theme, 2);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "toc", index: 2 } };
