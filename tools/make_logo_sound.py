# Synthesises the "da-dam" logo sound: a short low hit ("da") and, 0.32 s later, a big sustained chord with a
# sub boom and a long reverb tail ("dam"). Writes Assets/Audio/logo_dadam.wav (48 kHz stereo, 16-bit).
# Run:  C:\RoboDK\Python-Embedded\python.exe tools\make_logo_sound.py
import os, wave
import numpy as np

RATE, LENGTH = 48000, 5.0
OUT = os.path.join(os.path.dirname(__file__), "..", "Assets", "Audio", "logo_dadam.wav")
t = np.arange(int(RATE * LENGTH)) / RATE


def note(freq, start, attack, decay, harmonics=10, bright=1.6, detune=0.0):
    """Brassy tone: decaying harmonics, slight detune for width, exponential envelope."""
    tt = np.clip(t - start, 0, None)
    env = np.where(t < start, 0, np.minimum(tt / attack, 1) * np.exp(-tt / decay))
    tone = np.zeros_like(t)
    for k in range(1, harmonics + 1):
        hdecay = decay / (1 + 0.35 * k)                            # high harmonics fade first: the hit "blooms" darker
        tone += np.sin(2 * np.pi * freq * k * (1 + detune) * tt) * np.exp(-tt / hdecay) / k ** bright
    return tone * env


def boom(start, freq=42, decay=1.1):
    tt = np.clip(t - start, 0, None)
    sweep = freq * (1 + 1.5 * np.exp(-tt * 18))                    # pitch drops quickly: a kick-like thump
    phase = 2 * np.pi * np.cumsum(sweep) / RATE
    return np.where(t < start, 0, np.sin(phase) * np.exp(-tt / decay))


def reverb(x, seconds=2.6, mix=0.35, seed=3):
    rng = np.random.default_rng(seed)
    n = int(RATE * seconds)
    ir = rng.standard_normal(n) * np.exp(-np.arange(n) / RATE * 6.9 / seconds)
    ir[: int(RATE * 0.012)] = 0                                    # pre-delay
    size = 1 << int(np.ceil(np.log2(len(x) + n)))
    wet = np.fft.irfft(np.fft.rfft(x, size) * np.fft.rfft(ir, size), size)[: len(x)]
    wet *= np.abs(x).max() / (np.abs(wet).max() + 1e-9)
    return (1 - mix) * x + mix * wet


A1, E2, A2, Cs3, E3 = 55.0, 82.41, 110.0, 138.59, 164.81
DA, DAM = 0.05, 0.37

left = right = None
for side, det in (("L", -0.0025), ("R", 0.0025)):
    da = note(E2, DA, 0.004, 0.16, detune=det) + 0.6 * note(E3, DA, 0.004, 0.12, detune=-det)
    dam = (note(A1, DAM, 0.006, 1.6, detune=det) + 0.9 * note(A2, DAM, 0.006, 1.4, detune=-det)
           + 0.55 * note(Cs3, DAM, 0.008, 1.2, detune=det) + 0.5 * note(E3, DAM, 0.008, 1.1, detune=-det))
    mix = 0.75 * da + dam + 0.9 * boom(DA, 60, 0.18) + 1.3 * boom(DAM)
    mix = reverb(mix, seed=3 if side == "L" else 4)
    if side == "L":
        left = mix
    else:
        right = mix

stereo = np.stack([left, right], 1)
fade = int(RATE * 0.6)
stereo[-fade:] *= np.linspace(1, 0, fade)[:, None]
stereo = np.tanh(1.4 * stereo / np.abs(stereo).max()) / np.tanh(1.4) * 0.89   # gentle saturation, -1 dB peak
with wave.open(OUT, "wb") as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(RATE)
    w.writeframes((stereo * 32767).astype("<i2").tobytes())
print("saved", os.path.abspath(OUT))
