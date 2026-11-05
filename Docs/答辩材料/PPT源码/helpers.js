const fs = require("fs");
const LIB = "E:/Unity Projects/School-pro/Docs/答辩材料/PPT素材库";

function badge(slide, pres, theme, n) {
  slide.addShape(pres.shapes.OVAL, { x: 9.3, y: 5.1, w: 0.4, h: 0.4, fill: { color: theme.accent } });
  slide.addText(String(n), { x: 9.3, y: 5.1, w: 0.4, h: 0.4, fontSize: 12, fontFace: "Microsoft YaHei", color: "06121F", bold: true, align: "center", valign: "middle" });
}

function header(slide, pres, theme, kicker, title) {
  slide.background = { color: theme.bg };
  slide.addShape(pres.shapes.RECTANGLE, { x: 0, y: 0, w: 10, h: 0.06, fill: { color: theme.accent } });
  slide.addText(kicker, { x: 0.55, y: 0.32, w: 8.9, h: 0.3, fontSize: 11, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, charSpacing: 2 });
  slide.addText(title, { x: 0.55, y: 0.6, w: 8.9, h: 0.62, fontSize: 27, fontFace: "Microsoft YaHei", color: theme.primary, bold: true });
  slide.addShape(pres.shapes.LINE, { x: 0.55, y: 1.32, w: 8.9, h: 0, line: { color: theme.light, width: 1 } });
}

function card(slide, pres, theme, x, y, w, h) {
  slide.addShape(pres.shapes.RECTANGLE, { x, y, w, h, fill: { color: theme.light }, line: { color: "1E3A57", width: 0.75 } });
}

// 图片槽：文件存在则插图，否则占位框
function imgSlot(slide, pres, theme, rel, x, y, w, h, label) {
  const p = LIB + "/" + rel;
  if (fs.existsSync(p)) {
    slide.addImage({ path: p, x, y, w, h, sizing: { type: "cover", w, h } });
    slide.addShape(pres.shapes.RECTANGLE, { x, y, w, h, fill: { type: "none" }, line: { color: theme.accent, width: 1 } });
  } else {
    slide.addShape(pres.shapes.RECTANGLE, { x, y, w, h, fill: { color: "0E1B2C" }, line: { color: "2A4A6B", width: 1, dashType: "dash" } });
    slide.addText("【截图位】\n" + label, { x, y, w, h, fontSize: 12, fontFace: "Microsoft YaHei", color: theme.secondary, align: "center", valign: "middle" });
  }
}

function imgFit(slide, pres, theme, rel, x, y, w, h) {
  const p = LIB + "/" + rel;
  if (fs.existsSync(p)) {
    slide.addImage({ path: p, x, y, w, h, sizing: { type: "contain", w, h } });
  }
}

function bullets(slide, theme, items, x, y, w, h, size) {
  slide.addText(items.map(t => ({ text: t, options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 8 } })),
    { x, y, w, h, fontSize: size || 13, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.15 });
}

module.exports = { badge, header, card, imgSlot, imgFit, bullets, LIB };
