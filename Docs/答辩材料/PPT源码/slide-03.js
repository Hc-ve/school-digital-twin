const { badge, header, card } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "01 项目背景与意义", "为什么做：校园运维的四个「看不见」");
  const pains = [
    ["状态看不见", "照明、空调、门禁、配电、机房终端分布散，巡检靠人工逐层查看，故障发现滞后"],
    ["能耗算不清", "用电数据分散在不同台账，无法横向比较，「谁最耗电」没有答案"],
    ["电从哪来不明", "停一路电影响哪些设备靠经验判断，误停误送没有事前校验"],
    ["报警追不回", "告警发生后缺时间线与定位手段，处置过程无法复盘"],
  ];
  pains.forEach((p, i) => {
    const x = 0.6 + (i % 2) * 4.6, y = 1.6 + Math.floor(i / 2) * 1.55;
    card(s, pres, theme, x, y, 4.3, 1.38);
    s.addShape(pres.shapes.RECTANGLE, { x, y, w: 0.07, h: 1.38, fill: { color: theme.accent } });
    s.addText(p[0], { x: x + 0.25, y: y + 0.12, w: 3.8, h: 0.35, fontSize: 15, fontFace: "Microsoft YaHei", color: theme.primary, bold: true });
    s.addText(p[1], { x: x + 0.25, y: y + 0.5, w: 3.85, h: 0.8, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.1 });
  });
  s.addText("项目价值：把三维场景、实时数据、管理操作三层信息收敛到同一套系统 —— 一屏看清、一键可控。", { x: 0.6, y: 4.85, w: 8.9, h: 0.4, fontSize: 13, fontFace: "Microsoft YaHei", color: theme.accent, bold: true });
  badge(s, pres, theme, 3);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 3 } };
