const { badge, header, card } = require("./helpers");
function createSlide(pres, theme) {
  const s = pres.addSlide();
  header(s, pres, theme, "08 总结不足与未来展望", "如实呈现局限，比被问出来体面");
  const left = [
    ["功率曲线平时偏平", "播种值写入 override 后永久生效压制波形；改法已验证可行（播种+缓慢漂移），需先验「不自造告警」"],
    ["告警历史为内存态", "重启即失；下一阶段落盘持久化"],
    ["非 16:9 适配未真机验证", "已备命令行分辨率参数与窗口化预案，答辩现场实测确认"],
    ["屋顶积雪未实现", "需按屋面网格单独铺设；当前覆盖地面与树根，已记入缺陷台账"],
  ];
  const right = [
    ["室外路灯 100 盏接入", "先定室外 Zone 命名，再走标准新增点位流程"],
    ["全自动昼夜循环", "接入点已预留（SetHourOverride），解冻时间系统后启用"],
    ["真实 MQTT 设备对接", "loopback 回环已验协议链路，待现场点位量程与阈值对齐"],
    ["打包与多分辨率交付", "首次打包识别字体/Shader 剥离/分辨率三项风险，预案已备"],
  ];
  [["已知不足（照实写）", left, 0.6], ["未来展望", right, 5.1]].forEach(([t, rows, x]) => {
    s.addShape(pres.shapes.RECTANGLE, { x, y: 1.6, w: 4.3, h: 0.42, fill: { color: theme.accent } });
    s.addText(t, { x, y: 1.6, w: 4.3, h: 0.42, fontSize: 13, fontFace: "Microsoft YaHei", color: "06121F", bold: true, align: "center", valign: "middle" });
    rows.forEach((r, i) => {
      const y = 2.12 + i * 0.76;
      card(s, pres, theme, x, y, 4.3, 0.66);
      s.addText(r[0], { x: x + 0.15, y: y + 0.05, w: 4.0, h: 0.26, fontSize: 11, fontFace: "Microsoft YaHei", color: theme.primary, bold: true });
      s.addText(r[1], { x: x + 0.15, y: y + 0.3, w: 4.0, h: 0.34, fontSize: 9, fontFace: "Microsoft YaHei", color: theme.secondary, valign: "top", lineSpacingMultiple: 1.02 });
    });
  });
  s.addText("主动写出局限 = 我们知道且解释了原因；不写 = 等评委问出来。", { x: 0.6, y: 5.15, w: 8.9, h: 0.32, fontSize: 11.5, fontFace: "Microsoft YaHei", color: theme.accent, bold: true });
  badge(s, pres, theme, 18);
  return s;
}
module.exports = { createSlide, slideConfig: { type: "content", index: 17 } };
