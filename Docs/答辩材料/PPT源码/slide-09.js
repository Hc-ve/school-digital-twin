const { badge, header, card } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "04 成员分工总览", "Succeed 团队 · 7 人 · 8 个职能域");
  const rows = [
    ["何超臣", "组长", "统筹管控 · 核心系统架构与开发 · 数据链路 · 交互设计 · 漫游三模式 · 天气融合 · PPT"],
    ["杨续", "天气系统", "天气系统设计与开发 · 模块化打包产物 · 融合联调 · 机房视频与铃声素材收集"],
    ["赵朕", "UI 设计", "面板 UI 设计与视觉规范（token）落地 · 设计稿与样式图输出 · 设备交互开发"],
    ["张峻才", "场景资产", "场景素材收集整理与轻量化 · 设备交互开发 · 演示素材录制"],
    ["王绍锋", "交互与视觉", "空调反馈仿真交互（气流粒子+嗡鸣）· 场景视觉调优 · 发黑缺陷排查修复"],
    ["李文玉", "演示制作", "演示视频剪辑制作 · 设备交互开发 · 同类孪生项目调研协作文档"],
    ["王雯信", "文档与交付", "踩坑日志归档 · 答辩素材整理 · 设备交互开发 · 性能调试与打包"],
  ];
  rows.forEach((r, i) => {
    const y = 1.55 + i * 0.485;
    card(s, pres, theme, 0.6, y, 8.8, 0.42);
    s.addText(r[0], { x: 0.75, y, w: 1.15, h: 0.42, fontSize: 12.5, fontFace: "Microsoft YaHei", color: theme.primary, bold: true, valign: "middle" });
    s.addShape(pres.shapes.RECTANGLE, { x: 1.95, y: y + 0.08, w: 1.15, h: 0.26, fill: { color: theme.accent } });
    s.addText(r[1], { x: 1.95, y: y + 0.08, w: 1.15, h: 0.26, fontSize: 10, fontFace: "Microsoft YaHei", color: "06121F", bold: true, align: "center", valign: "middle" });
    s.addText(r[2], { x: 3.25, y, w: 6.05, h: 0.42, fontSize: 10.5, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "middle" });
  });
  s.addText("协作机制：职能域划分 + 统一口径（PPT / 发言 / 实习报告三处逐字一致）；开发过程引入 AI 专家团队辅助分工，组长负责任务拆解、验收与裁决。", { x: 0.6, y: 5.02, w: 8.9, h: 0.4, fontSize: 11, fontFace: "Microsoft YaHei", color: theme.secondary, italic: true, valign: "top" });
  badge(s, pres, theme, 9);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 9 } };
