const { badge, header, card } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "07 项目成果展示", "现场演示动线 + 备用演示视频");
  const steps = [
    ["1", "总览 KPI", "建立整体印象"],
    ["2", "点设备详情", "三维↔数据联动"],
    ["3", "机房联动开机", "20 块屏同播 + 总功率 8.675→11.79 kW"],
    ["4", "拉整层闸", "功率精确归 0.0 kW"],
    ["5", "告警点行定位", "全链路时间线"],
    ["6", "时间天气", "拖表盘到夜 + 切雪天"],
  ];
  steps.forEach((st, i) => {
    const x = 0.6 + i * 1.5;
    card(s, pres, theme, x, 1.6, 1.38, 1.5);
    s.addShape(pres.shapes.OVAL, { x: x + 0.49, y: 1.72, w: 0.4, h: 0.4, fill: { color: theme.accent } });
    s.addText(st[0], { x: x + 0.49, y: 1.72, w: 0.4, h: 0.4, fontSize: 14, fontFace: "Microsoft YaHei", color: "06121F", bold: true, align: "center", valign: "middle" });
    s.addText(st[1], { x: x + 0.05, y: 2.18, w: 1.28, h: 0.32, fontSize: 11, fontFace: "Microsoft YaHei", color: theme.primary, bold: true, align: "center" });
    s.addText(st[2], { x: x + 0.05, y: 2.5, w: 1.28, h: 0.55, fontSize: 8.5, fontFace: "Microsoft YaHei", color: theme.secondary, align: "center", valign: "top", lineSpacingMultiple: 1.05 });
    if (i < 5) s.addText("→", { x: x + 1.36, y: 2.1, w: 0.18, h: 0.4, fontSize: 14, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, align: "center" });
  });
  card(s, pres, theme, 0.6, 3.35, 5.6, 1.55);
  s.addShape(pres.shapes.RECTANGLE, { x: 0.6, y: 3.35, w: 5.6, h: 0.4, fill: { color: theme.light } });
  s.addText("备用：演示视频（李文玉剪辑，周五早成片后插入此页）", { x: 0.75, y: 3.35, w: 5.3, h: 0.4, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, valign: "middle" });
  s.addText("现场演示若遇设备问题，切换播放预录视频：六段动线 + 字幕说明每步验证的能力，解说词与本页动线一一对应。", { x: 0.75, y: 3.85, w: 5.3, h: 0.95, fontSize: 11, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.15 });
  card(s, pres, theme, 6.35, 3.35, 3.05, 1.55);
  s.addText("现场应急", { x: 6.5, y: 3.45, w: 2.8, h: 0.3, fontSize: 12, fontFace: "Microsoft YaHei", color: theme.accent, bold: true });
  s.addText([
    { text: "断网 → Mock 本地兜底，数据照常刷新", options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 5 } },
    { text: "投影非 16:9 → 参考分辨率 + Expand 适配说明", options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 5 } },
    { text: "程序异常 → 重启后从动线第 3 步续演", options: { bullet: { code: "25AA", color: theme.accent }, paraSpaceAfter: 0 } },
  ], { x: 6.5, y: 3.78, w: 2.8, h: 1.05, fontSize: 9.5, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.08 });
  badge(s, pres, theme, 17);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 16 } };
