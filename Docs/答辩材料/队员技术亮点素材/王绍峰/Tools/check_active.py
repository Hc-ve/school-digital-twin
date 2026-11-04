import re
import codecs

SCENE = r"D:\Uproject\UI\Assets\Classroom.unity"
text = open(SCENE, encoding="utf-8").read()

blocks = re.findall(r"--- !u!1 &\d+\nGameObject:\n(.*?)(?=\n--- )", text, re.S)
print(f"GameObject 块数: {len(blocks)}")
for b in blocks:
    m = re.search(r"m_Name: (.+)", b)
    if not m:
        continue
    name = m.group(1).strip().strip('"')
    if "\\u" in name:
        try:
            name = codecs.decode(name, "unicode_escape")
        except Exception:
            pass
    if "Canvas" in name or name == "空调气流":
        act = re.search(r"m_IsActive: (\d)", b)
        print(f"  {name:<14} m_IsActive = {act.group(1) if act else '?'}")
