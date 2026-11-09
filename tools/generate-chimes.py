"""
Synthesises AgentsCursor's built-in chimes into src/AgentsCursor.App/Assets/Sounds/<name>.wav
(16-bit mono PCM, 44.1 kHz), embedded in the app as resources.

Each chime is a few soft mallet-like notes: a sine fundamental with quieter, faster-fading
overtones, a gentle 8 ms attack (no click), an exponential decay and a 120 ms fade to silence. Kept
in the mid register (C5-B5) and short (under a second), so they read as a nudge, not an alarm.

    Bell    one note (A5) with a faint bell shimmer
    Rise    E5 -> B5, a rising fifth that sounds like a question   ("Needs you" default)
    Settle  G5 -> C5, a falling fifth that comes to rest           ("Done" default)
    Glow    C5 E5 G5, a quick quiet arpeggio

Every file is normalised to the same peak (PEAK, -3 dBFS); the app scales the samples by the
volume setting when it plays one, never the system volume.

Run from anywhere (pure standard library):
    python tools/generate-chimes.py
"""

import math
import struct
import wave
from pathlib import Path

RATE = 44100
PEAK = 0.708  # -3 dBFS
ATTACK = 0.008  # s
FADE = 0.120  # s, at the very end

# (ratio to the fundamental, level, decay time constant as a fraction of the note's)
MALLET = [(1.0, 1.0, 1.0), (2.0, 0.20, 0.45), (3.0, 0.06, 0.30)]
BELL = [(1.0, 1.0, 1.0), (2.0, 0.24, 0.55), (3.0, 0.07, 0.35), (5.4, 0.03, 0.18)]

C5, E5, G5, A5, B5 = 523.25, 659.26, 783.99, 880.00, 987.77

# name -> (length in s, [(start s, frequency Hz, level, decay s, partials)])
CHIMES = {
    "bell": (1.0, [(0.00, A5, 1.0, 0.42, BELL)]),
    "rise": (0.85, [(0.00, E5, 0.85, 0.22, MALLET), (0.14, B5, 1.0, 0.30, MALLET)]),
    "settle": (0.85, [(0.00, G5, 0.85, 0.22, MALLET), (0.14, C5, 1.0, 0.32, MALLET)]),
    "glow": (0.95, [(0.00, C5, 0.75, 0.30, MALLET), (0.07, E5, 0.75, 0.30, MALLET), (0.14, G5, 0.85, 0.34, MALLET)]),
}


def render(length: float, notes: list) -> list[float]:
    n = int(RATE * length)
    out = [0.0] * n
    for start, freq, level, decay, partials in notes:
        first = int(RATE * start)
        for i in range(first, n):
            t = (i - first) / RATE
            attack = 0.5 - 0.5 * math.cos(math.pi * t / ATTACK) if t < ATTACK else 1.0
            sample = 0.0
            for ratio, partial_level, partial_decay in partials:
                sample += partial_level * math.exp(-t / (decay * partial_decay)) * math.sin(2 * math.pi * freq * ratio * t)
            out[i] += level * attack * sample
    fade = int(RATE * FADE)
    for k in range(fade):
        out[n - 1 - k] *= k / fade
    peak = max(abs(v) for v in out)
    return [v * PEAK / peak for v in out]


def write(path: Path, samples: list[float]) -> None:
    with wave.open(str(path), "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(RATE)
        f.writeframes(b"".join(struct.pack("<h", round(v * 32767)) for v in samples))


def main() -> None:
    folder = Path(__file__).resolve().parent.parent / "src" / "AgentsCursor.App" / "Assets" / "Sounds"
    folder.mkdir(parents=True, exist_ok=True)
    for name, (length, notes) in CHIMES.items():
        path = folder / f"{name}.wav"
        write(path, render(length, notes))
        print(f"wrote {path} ({path.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
