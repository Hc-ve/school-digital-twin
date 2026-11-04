"""合成可无缝循环的空调运转环境音。

思路：在频域构造噪声（长度恰好 N 点，IFFT 后天然以 N 为周期），
所有调制频率都取 1/duration 的整数倍，因此首尾天然衔接、无爆音。
"""
import numpy as np
import wave

SR = 44100
DUR = 12.0
N = int(SR * DUR)

rng = np.random.default_rng(20261029)

# ---------- 1. 频域整形噪声：风扇气流 ----------
white = rng.standard_normal(N)
spec = np.fft.rfft(white)
freqs = np.fft.rfftfreq(N, 1.0 / SR)

# 低频隆隆声（压缩机/风道）
rumble = 1.0 / (1.0 + (freqs / 180.0) ** 2.4)
# 中频"吹风"质感
air = 0.55 * np.exp(-((np.log(np.maximum(freqs, 1.0)) - np.log(420.0)) ** 2) / (2 * 0.85 ** 2))
# 极高频滚降，避免嘶嘶声刺耳
rolloff = 1.0 / (1.0 + (freqs / 5200.0) ** 2.0)

envelope = (rumble + air) * rolloff
envelope[0] = 0.0
noise = np.fft.irfft(spec * envelope, n=N)
noise /= np.max(np.abs(noise))

# ---------- 2. 压缩机嗡鸣：50Hz 及其谐波 ----------
t = np.arange(N) / SR
hum = np.zeros(N)
for f, a in [(50, 0.32), (100, 0.20), (150, 0.10), (200, 0.055), (250, 0.03)]:
    hum += a * np.sin(2 * np.pi * f * t + rng.uniform(0, 2 * np.pi))
hum /= np.max(np.abs(hum))

# ---------- 3. 缓慢呼吸感 + 轻微抖动 ----------
am_slow = 1.0 + 0.10 * np.sin(2 * np.pi * 0.25 * t)      # 12s 内 3 个周期
am_fast = 1.0 + 0.022 * np.sin(2 * np.pi * 7.5 * t)      # 12s 内 90 个周期

signal = (noise * 0.78 + hum * 0.34) * am_slow * am_fast

# ---------- 4. 归一化并写盘 ----------
signal /= np.max(np.abs(signal))
signal *= 0.55
pcm = np.clip(signal * 32767.0, -32768, 32767).astype("<i2")

out = r"D:\Uproject\UI\Assets\Audio\AC_Hum.wav"
with wave.open(out, "wb") as w:
    w.setnchannels(1)
    w.setsampwidth(2)
    w.setframerate(SR)
    w.writeframes(pcm.tobytes())

rms = float(np.sqrt(np.mean(signal ** 2)))
print(f"已写入 {out}")
print(f"时长 {DUR:.1f}s  采样率 {SR}  单声道 16bit")
print(f"峰值 {np.max(np.abs(signal)):.3f}  RMS {rms:.3f} ({20*np.log10(rms):.1f} dBFS)")
print(f"首尾差值 {abs(float(signal[0]) - float(signal[-1])):.6f}（越小越无缝）")
