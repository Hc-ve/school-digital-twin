const PptxGenJS = require("pptxgenjs");
const fs = require("fs");
const path = require("path");

const theme = {
  primary: "EAF6FF",
  secondary: "A9C2D9",
  accent: "33BFFF",
  light: "132234",
  bg: "0A1420",
};

const pptx = new PptxGenJS();
pptx.layout = "LAYOUT_16x9";
pptx.author = "Succeed 团队";
pptx.company = "新开普电子股份有限公司";
pptx.title = "校园数字孪生管理系统 · 毕业实习答辩";

const files = fs.readdirSync(__dirname).filter(f => /^slide-\d+\.js$/.test(f)).sort();
console.log("页数:", files.length);
for (const f of files) require(path.join(__dirname, f)).createSlide(pptx, theme);

const out = process.argv[2] || path.join(__dirname, "output", "校园数字孪生管理系统-毕业实习答辩.pptx");
pptx.writeFile({ fileName: out }).then(() => console.log("已生成:", out, fs.statSync(out).size, "字节"));
