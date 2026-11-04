import re
import os

PROJ = r"D:\Uproject\UI"
SCENE = os.path.join(PROJ, "Assets", "Classroom.unity")


def guid_of(asset_path):
    meta = asset_path + ".meta"
    if not os.path.exists(meta):
        return None
    text = open(meta, encoding="utf-8").read(300)
    m = re.search(r"guid:\s*([0-9a-f]{32})", text)
    return m.group(1) if m else None


script = os.path.join(PROJ, "Assets", "Scripts", "ClassroomUI", "ClassroomSceneController.cs")
script_guid = guid_of(script)
print("控制器脚本 guid:", script_guid)

scene = open(SCENE, encoding="utf-8").read()

blocks = re.findall(r"--- !u!114 &\d+\nMonoBehaviour:\n(.*?)(?=\n--- )", scene, re.S)
target = None
for b in blocks:
    if script_guid and script_guid in b:
        target = b
        break

if target is None:
    print("未找到 ClassroomSceneController 组件实例")
else:
    print("\n=== 控制器序列化字段 ===")
    for line in target.splitlines():
        line = line.strip()
        if not line or line.startswith("m_Object") or line.startswith("m_Corresponding") \
           or line.startswith("m_Prefab") or line.startswith("m_GameObject") \
           or line.startswith("m_Enabled") or line.startswith("m_Script") \
           or line.startswith("m_EditorHideFlags") or line.startswith("m_EditorClass"):
            continue
        flag = ""
        if "{fileID: 0}" in line:
            flag = "   <-- 空引用"
        print("  " + line + flag)
