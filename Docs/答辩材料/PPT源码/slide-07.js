const { badge, header, card, imgFit } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "02 项目简介", "技术深度：命令链路 · 电力回路 · 告警状态机");
  imgFit(s, pres, theme, "技术图/A03_命令链路时序.png", 0.6, 1.5, 5.2, 2.31);
  s.addText("图：命令链路四段超时校验（下发→回执→执行→回读 1s/3s/3s/5s）", { x: 0.6, y: 3.83, w: 5.2, h: 0.26, fontSize: 9.5, fontFace: "Microsoft YaHei", color: theme.secondary, align: "center" });
  const cards = [
    ["四段超时校验", "下发 1s / 回执 3s / 执行 3s / 回读 5s，失败给出明确拒绝原因，不静默"],
    ["SBO 两步确认", "合闸前摊开受影响设备名单 + 可见倒计时，电力行业 SCADA 标准操作"],
    ["告警四级状态", "正常/警告/告警/离线+过期；重复折叠、2s 内抖动抑制、升级并入同条记录"],
  ];
  cards.forEach((c, i) => {
    const y = 1.5 + i * 0.86;
    card(s, pres, theme, 6.0, y, 3.4, 0.76);
    s.addText(c[0], { x: 6.15, y: y + 0.06, w: 3.1, h: 0.26, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.accent, bold: true });
    s.addText(c[1], { x: 6.15, y: y + 0.32, w: 3.1, h: 0.4, fontSize: 9.5, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.05 });
  });
  imgFit(s, pres, theme, "技术图/A06_告警状态机.png", 0.6, 4.2, 4.6, 1.12);
  card(s, pres, theme, 5.4, 4.2, 4.0, 1.12);
  s.addText("编号体系：三层编号 = 身份证", { x: 5.55, y: 4.28, w: 3.7, h: 0.28, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.accent, bold: true });
  s.addText("全码「1号教学楼2F201教室001灯具」/ 短码「2F203-灯001」/ 带类型；唯一化后 70 台撞号 15 组 → 0。", { x: 5.55, y: 4.58, w: 3.7, h: 0.66, fontSize: 10, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.1 });
  badge(s, pres, theme, 7);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 7 } };
