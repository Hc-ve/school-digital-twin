const { badge, header, card } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "02 项目简介", "项目目标与交付成果");
  const kpis = [["71", "设备点位"], ["17", "功能分区"], ["5", "管理模块"], ["3", "漫游模式"], ["292", "验收判据"]];
  kpis.forEach((k, i) => {
    const x = 0.6 + i * 1.8;
    card(s, pres, theme, x, 1.6, 1.66, 1.05);
    s.addText(k[0], { x, y: 1.68, w: 1.66, h: 0.55, fontSize: 27, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, align: "center" });
    s.addText(k[1], { x, y: 2.22, w: 1.66, h: 0.35, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.secondary, align: "center" });
  });
  s.addText("项目目标", { x: 0.6, y: 2.9, w: 4.3, h: 0.35, fontSize: 14, fontFace: "Microsoft YaHei", color: theme.primary, bold: true });
  s.addText([
    { text: "把三维场景、实时数据、管理操作收敛到同一套系统", options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 6 } },
    { text: "设备状态实时可见、控制命令可下发可回读", options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 6 } },
    { text: "能耗与告警可统计、可追溯、可对账", options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 6 } },
    { text: "断网可演示、普通 PC 可直接运行", options: { bullet: { code: "25AA", color: theme.accent }, paraSpaceAfter: 0 } },
  ], { x: 0.6, y: 3.25, w: 4.3, h: 1.7, fontSize: 12, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.15 });
  s.addText("交付成果", { x: 5.1, y: 2.9, w: 4.3, h: 0.35, fontSize: 14, fontFace: "Microsoft YaHei", color: theme.primary, bold: true });
  s.addText([
    { text: "可运行的孪生管理系统（五大模块 + 天气系统）", options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 6 } },
    { text: "292 条可执行验收判据 + 82 条踩坑日志", options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 6 } },
    { text: "1159 行项目基线文档（台账 / 版本 / 索引）", options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 6 } },
    { text: "答辩 PPT、演示视频、设计说明书与使用手册", options: { bullet: { code: "25AA", color: theme.accent }, paraSpaceAfter: 0 } },
  ], { x: 5.1, y: 3.25, w: 4.3, h: 1.7, fontSize: 12, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.15 });
  badge(s, pres, theme, 5);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 5 } };
