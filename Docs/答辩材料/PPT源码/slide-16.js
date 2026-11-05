const { badge, header, imgSlot, imgFit } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "07 项目成果展示", "本周新增：时间天气系统（队员成果融合）");
  imgFit(s, pres, theme, "设计稿/09_时间天气面板_设计试绘_白天.png", 0.6, 1.6, 2.85, 2.3);
  s.addText("设计试绘（先图后码）", { x: 0.6, y: 3.92, w: 2.85, h: 0.28, fontSize: 10, fontFace: "Microsoft YaHei", color: theme.secondary, align: "center" });
  imgSlot(s, pres, theme, "运行截图/S06_天气面板.png", 3.6, 1.6, 2.85, 2.3, "S06_天气面板.png\n表盘拖动 + 四档天气");
  imgSlot(s, pres, theme, "运行截图/S07_雪天场景.png", 6.6, 1.6, 2.85, 2.3, "S07_雪天场景.png\n切雪天 2.4s 场景渐变");
  s.addText([
    { text: "24h 表盘按住拖动连续调时刻，松手不吸附；Esc/遮罩/× 三处关闭恢复真实时钟", options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 5 } },
    { text: "四档天气（随机/晴/雨/雪）单击即生效，视觉层 2.4s 渐变、面板不等待", options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 5 } },
    { text: "视觉 7 档 ↔ 数据 4 档映射表对接：切雪天后室温/湿度同帧联动，不各走各的", options: { bullet: { code: "25AA", color: theme.accent }, breakLine: true, paraSpaceAfter: 5 } },
    { text: "雨雪/云穹顶/星空/积雪积水全部程序化生成，零外部素材（队员杨续模块）", options: { bullet: { code: "25AA", color: theme.accent }, paraSpaceAfter: 0 } },
  ], { x: 0.6, y: 4.3, w: 8.9, h: 1.05, fontSize: 11, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.1 });
  badge(s, pres, theme, 16);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 15 } };
