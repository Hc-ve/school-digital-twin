const { badge, header, card, imgFit } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "02 项目简介", "技术栈与总体架构");
  const left = [
    ["引擎", "Unity 2022.3.62f3c1 · 内置渲染管线（非 URP）· Linear"],
    ["界面", "legacy UGUI · 零图片资源：圆角/描边/图标全代码图元拼合"],
    ["音频", "告警音/空调嗡鸣/铃声全代码波形合成，工程内零 wav"],
    ["数据", "Mock（10Hz 后台线程，断网兜底）+ MQTT 双源互切"],
  ];
  s.addText("技术选型（两条硬约束）", { x: 0.6, y: 1.5, w: 4.6, h: 0.32, fontSize: 13, fontFace: "Microsoft YaHei", color: theme.primary, bold: true });
  left.forEach((r, i) => {
    const y = 1.88 + i * 0.72;
    card(s, pres, theme, 0.6, y, 4.6, 0.62);
    s.addText(r[0], { x: 0.75, y, w: 0.85, h: 0.62, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.accent, bold: true, valign: "middle" });
    s.addText(r[1], { x: 1.62, y, w: 3.45, h: 0.62, fontSize: 10, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "middle", lineSpacingMultiple: 1.05 });
  });
  s.addText("为什么不用 URP / UE5：内置管线 + 纯代码 UI 更契合零图片资源约束与断网部署。", { x: 0.6, y: 4.82, w: 4.6, h: 0.5, fontSize: 10.5, fontFace: "Microsoft YaHei", color: theme.secondary, italic: true, valign: "top" });
  imgFit(s, pres, theme, "技术图/A01_系统总体架构.png", 5.45, 1.5, 3.95, 3.55);
  s.addText("图：系统总体架构（运行时/锚点/注册表/点位目录/数据总线 五层）", { x: 5.45, y: 5.06, w: 3.95, h: 0.28, fontSize: 9.5, fontFace: "Microsoft YaHei", color: theme.secondary, align: "center" });
  badge(s, pres, theme, 6);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 6 } };
