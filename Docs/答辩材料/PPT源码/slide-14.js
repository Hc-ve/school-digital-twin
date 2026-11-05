const { badge, header, card, imgSlot } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "07 项目成果展示", "五大管理模块：一屏看清、一键可控");
  const mods = ["数据监测", "设备管理", "电力管理", "告警中心", "数据统计"];
  mods.forEach((m, i) => {
    const x = 0.6 + i * 1.8;
    s.addShape(pres.shapes.ROUNDED_RECTANGLE, { x, y: 1.5, w: 1.66, h: 0.4, fill: { color: theme.light }, line: { color: theme.accent, width: 1 }, rectRadius: 0.06 });
    s.addText(m, { x, y: 1.5, w: 1.66, h: 0.4, fontSize: 12, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, align: "center", valign: "middle" });
  });
  imgSlot(s, pres, theme, "运行截图/S01_总览监控.png", 0.6, 2.05, 2.9, 2.55, "S01_总览监控.png\n全屏 L1 监控态");
  imgSlot(s, pres, theme, "运行截图/S02_设备详情.png", 3.55, 2.05, 2.9, 2.55, "S02_设备详情.png\n点设备→整页详情+曲线");
  imgSlot(s, pres, theme, "运行截图/S09_机房联动.png", 6.5, 2.05, 2.9, 2.55, "S09_机房联动.png\n20 块屏联动开机");
  s.addText("点场景设备 → 相机飞行定位 + 详情钻取；点告警行 → 定位设备 + 时间线；机房主脑开机带动 20 块屏同播。", { x: 0.6, y: 4.75, w: 8.9, h: 0.4, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.secondary, italic: true });
  badge(s, pres, theme, 14);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 14 } };
