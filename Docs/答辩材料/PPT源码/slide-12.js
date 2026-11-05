const { badge, header, card } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "06 项目踩坑汇报", "坑一与坑二：数据是假的，尺子也是坏的");
  const cases = [
    ["坑 1 · 空调功耗少算 1000 倍", "现象", "统计页空调占全楼总功率 0.08%（应约 45%），最大耗电设备在屏上「不存在」",
     "根因", "点位目录里 5 台空调功率单位是 kW、其余 39 个是 W，读取函数直接当瓦返回",
     "修法", "单位归一下沉到数据读取层，按目录声明的单位换算；禁止按设备类型硬编码"],
    ["坑 2 · 判据连续三天报红，代码却是对的", "现象", "P5 三条判据持续报红，反复查代码无果",
     "根因", "判据命中注释里引用的旧文案字面量 —— 判据工具不分注释与代码",
     "修法", "判据入口统一剔除注释行；并立新纪律：判据报出的数量级必须先自检是否合理"],
  ];
  cases.forEach((c, i) => {
    const x = 0.6 + i * 4.6;
    card(s, pres, theme, x, 1.55, 4.3, 3.35);
    s.addShape(pres.shapes.RECTANGLE, { x, y: 1.55, w: 4.3, h: 0.5, fill: { color: theme.accent } });
    s.addText(c[0], { x: x + 0.12, y: 1.55, w: 4.06, h: 0.5, fontSize: 12.5, fontFace: "Microsoft YaHei", color: "06121F", bold: true, valign: "middle" });
    let y = 2.2;
    for (let k = 1; k < 6; k += 2) {
      s.addText(c[k], { x: x + 0.15, y, w: 0.62, h: 0.3, fontSize: 10.5, fontFace: "Microsoft YaHei", color: theme.accent, bold: true });
      s.addText(c[k + 1], { x: x + 0.8, y, w: 3.35, h: 0.72, fontSize: 10.5, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.08 });
      y += 0.82;
    }
  });
  s.addText("教训：判据全绿 ≠ 结果正确；验收工具本身也要被验收。", { x: 0.6, y: 5.05, w: 8.9, h: 0.35, fontSize: 12.5, fontFace: "Microsoft YaHei", color: theme.accent, bold: true });
  badge(s, pres, theme, 12);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 11 } };
