const { badge, header, card } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "01 项目背景与意义", "行业怎么做：五个真实商业案例对标");
  const rows = [
    ["施耐德 EcoStruxure (EBO)", "楼宇级能耗与告警管理", "本系统复现：楼层能耗聚合 + 四级告警"],
    ["优锘 ThingJS-X", "三维场景与看板双向联动", "本系统复现：点设备↔看板双向定位"],
    ["Siemens Building X", "渐进披露：先总览后下钻", "本系统复现：三态界面（窄态/L1/L2）"],
    ["51WORLD / Hightopo", "单容器 + 页栈，非多浮窗并列", "本系统复现：抽屉 + 整页详情"],
  ];
  rows.forEach((r, i) => {
    const y = 1.6 + i * 0.74;
    card(s, pres, theme, 0.6, y, 8.8, 0.62);
    s.addText(r[0], { x: 0.8, y, w: 3.0, h: 0.62, fontSize: 12.5, fontFace: "Microsoft YaHei", color: theme.primary, bold: true, valign: "middle" });
    s.addText(r[1], { x: 3.85, y, w: 2.9, h: 0.62, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "middle" });
    s.addText(r[2], { x: 6.8, y, w: 2.5, h: 0.62, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.accent, valign: "middle" });
  });
  s.addText("定位：用学生可实现的技术栈（内置管线 + 纯代码 UI + 零外部依赖），复现商业平台的核心能力，并适配断网部署与轻量交付。", { x: 0.6, y: 4.72, w: 8.9, h: 0.5, fontSize: 12.5, fontFace: "Microsoft YaHei", color: theme.secondary, italic: true, valign: "top" });
  badge(s, pres, theme, 4);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 4 } };
