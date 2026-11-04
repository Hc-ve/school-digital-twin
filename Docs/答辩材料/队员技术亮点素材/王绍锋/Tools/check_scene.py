import re
import codecs
from collections import Counter

SCENE = r"D:\Uproject\UI\Assets\Classroom.unity"

raw = open(SCENE, "rb").read()
text = raw.decode("utf-8")
print("字节数", len(raw), "字符数", len(text))
print("m_Name 出现次数:", text.count("m_Name"))

names = re.findall(r"m_Name:[ \t]*(.+)", text)
print("匹配到的名字条数:", len(names))


def decode_name(value):
    value = value.strip()
    if value.startswith('"') and value.endswith('"'):
        value = value[1:-1]
    if "\\u" in value:
        try:
            value = codecs.decode(value, "unicode_escape")
        except Exception:
            pass
    return value


decoded = [decode_name(n) for n in names]
print("\n=== 场景对象名（去重）===")
for name, count in sorted(Counter(n for n in decoded if n).items()):
    print(f"  {name}  x{count}")

print("\n=== 关键组件 ===")
for key in ["VideoPlayer:", "AudioSource:", "Light:", "Canvas:", "Camera:",
            "MonoBehaviour:", "CharacterController:", "VideoPlayer", "AudioListener"]:
    print(f"  {key:<24} {text.count(key)}")

print("\n=== VideoPlayer 字段 ===")
m = re.search(r"--- !u!\d+ &\d+\nVideoPlayer:\n(.*?)(?=\n--- )", text, re.S)
if m:
    for line in m.group(1).splitlines():
        if any(k in line for k in ["m_Clip", "m_Source", "m_TargetTexture",
                                   "m_RenderMode", "m_AudioOutputMode",
                                   "m_ControlledAudioTrackCount", "m_PlayOnAwake",
                                   "m_Loop", "m_WaitForFirstFrame"]):
            print("  " + line.strip())
else:
    print("  未找到 VideoPlayer 段")
