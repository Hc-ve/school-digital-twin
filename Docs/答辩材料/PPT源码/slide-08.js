const { badge, header, card } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "03 项目策划", "需求分析 → 工作流程 → 时间规划");
  const cols = [
    ["需求分析", ["校园运维四痛点（状态/能耗/电力/告警）", "答辩现场演示 = 硬交付形式", "断网可跑、普通 PC 可跑 = 硬约束", "7 人拼凑组 = 口径必须统一"]],
    ["工作流程", ["先出图纸/判据，再施工", "每轮改动 → 判据体检 → play 验收", "踩坑即归档（现象→根因→修法→教训）", "数值类任务强制附实算脚本"]],
    ["时间规划", ["10.12~10.29 培训 + 框架奠基 + 五模块", "10.30~11.03 统计大屏 + 数据真实性专项", "11.04~11.05 清理 + 天气融合 + 面板", "11.06 答辩（前夜冻结功能只修阻断 bug）"]],
  ];
  cols.forEach((c, i) => {
    const x = 0.6 + i * 3.1;
    card(s, pres, theme, x, 1.6, 2.9, 3.3);
    s.addShape(pres.shapes.RECTANGLE, { x, y: 1.6, w: 2.9, h: 0.45, fill: { color: theme.accent } });
    s.addText(c[0], { x, y: 1.6, w: 2.9, h: 0.45, fontSize: 14, fontFace: "Microsoft YaHei", color: "06121F", bold: true, align: "center", valign: "middle" });
    s.addText(c[1].map(t => ({ text: t, options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 9 } })),
      { x: x + 0.18, y: 2.2, w: 2.6, h: 2.6, fontSize: 10.5, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.12 });
    if (i < 2) s.addText("→", { x: x + 2.86, y: 2.9, w: 0.3, h: 0.5, fontSize: 20, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, align: "center" });
  });
  s.addText("纪律：答辩前 72 小时冻结功能；演示动线按认知成本从轻到重编排。", { x: 0.6, y: 5.05, w: 8.9, h: 0.35, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.secondary, italic: true });
  badge(s, pres, theme, 8);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 8 } };
