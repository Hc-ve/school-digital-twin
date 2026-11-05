const { badge, header, card } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "05 成员项目工作汇报", "组长核心贡献：从框架到交付的一条主线");
  const rows = [
    ["孪生框架与数据链路", "五层运行时结构；Mock/MQTT 双源互切；命令链路四段超时校验 1s/3s/3s/5s"],
    ["五大管理模块", "数据监测 / 设备管理 / 电力管理 / 告警中心 / 数据统计；三态界面（窄态 440 / L1 / L2 三栏）"],
    ["数据真实性专项", "室温改物理派生链（热惯性衰减 30%、滞后 3h）+ Magnus 湿度；全年 192 组实算抓出 4 处错"],
    ["两处答辩级缺陷修复", "功率单位混用（空调少算 1000 倍）下沉读取层归一；编号撞号 51 台 → 0"],
    ["交互与漫游", "渐进披露交互；第一/第三人称/观景三模式；告警点击定位全链路"],
    ["工程体系与交付", "292 条判据 + 82 条踩坑日志 + 1159 行基线文档；天气融合；PPT 与演示动线"],
  ];
  rows.forEach((r, i) => {
    const y = 1.55 + i * 0.56;
    card(s, pres, theme, 0.6, y, 8.8, 0.48);
    s.addText(r[0], { x: 0.75, y, w: 2.35, h: 0.48, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, valign: "middle" });
    s.addText(r[1], { x: 3.2, y, w: 6.1, h: 0.48, fontSize: 10.5, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "middle" });
  });
  badge(s, pres, theme, 10);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 9 } };
