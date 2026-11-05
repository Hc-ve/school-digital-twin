const { badge, header, card, imgSlot } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "07 项目成果展示", "数据统计大屏：原始诉求的兑现");
  imgSlot(s, pres, theme, "运行截图/S05_统计大屏.png", 0.6, 1.6, 5.5, 3.15, "S05_统计大屏.png\n全屏 L2 数据统计大屏（四主卡 + 曲线 + 榜单）");
  const pts = [
    ["总功率口径", "实时读数为主；无读数设备按额定×0.15 估算，界面如实标注「含估算」"],
    ["榜单口径", "只收实测点位（收集环节过滤），与总量口径分离、互不污染"],
    ["断电归零", "拉闸后读数精确归 0.0 kW，走回路物理事实而非数据源开关"],
    ["编号唯一", "三层编号 + 短码带类型，70 台撞号 15 组 → 0"],
  ];
  pts.forEach((p, i) => {
    const y = 1.6 + i * 0.82;
    card(s, pres, theme, 6.25, y, 3.15, 0.72);
    s.addText(p[0], { x: 6.4, y: y + 0.06, w: 2.9, h: 0.28, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.accent, bold: true });
    s.addText(p[1], { x: 6.4, y: y + 0.33, w: 2.9, h: 0.36, fontSize: 9.5, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.05 });
  });
  s.addText("同一页 KPI、楼层聚合、榜单三处数字同源同口径 —— 评委对账不穿帮。", { x: 0.6, y: 4.9, w: 8.9, h: 0.35, fontSize: 12, fontFace: "Microsoft YaHei", color: theme.secondary, italic: true });
  badge(s, pres, theme, 15);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 14 } };
