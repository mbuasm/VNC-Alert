#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Generator dźwięku alarmu dla VNC Alert.

Tworzy wyraźny, dwutonowy dźwięk ostrzegawczy (naprzemiennie 880 Hz / 1320 Hz,
co 0,25 s) i zapisuje go jako 16-bit PCM mono WAV 22050 Hz do `sounds/alarm.wav`.

Każdy segment tonu ma krótki fade-in/fade-out, żeby przy zmianie częstotliwości
nie było słyszalnego "trzasku". Plik kończy się krótką ciszą, dzięki czemu
zapętlone odtwarzanie (dźwięk gra, dopóki alert jest aktywny) też nie trzeszczy.

Użycie (z katalogu głównego repozytorium):
    python3 tools/make-alarm-wav.py

Nie wymaga żadnych dodatkowych bibliotek – tylko standardowe moduły Pythona
`wave` i `math` (bez numpy), żeby dało się to odtworzyć na dowolnym komputerze
z Pythonem 3.
"""

import math
import struct
import wave
from pathlib import Path

# --- Parametry dźwięku ------------------------------------------------------

OUT_PATH = Path(__file__).resolve().parent.parent / "sounds" / "alarm.wav"

SAMPLE_RATE = 22050       # Hz – niska, ale wystarczająca do prostego sygnału alarmowego
AMPLITUDE = 0.6           # udział pełnej skali 16-bit (0.0–1.0) – głośno, ale bez przesterowania
SEGMENT_SECONDS = 0.25    # długość jednego "beep" (s)
SEGMENT_COUNT = 6         # 6 x 0.25 s = 1.5 s dźwięku (naprzemiennie 880/1320 Hz)
FADE_MS = 8.0             # fade-in/fade-out każdego segmentu (ms) – eliminuje trzaski
TAIL_SILENCE_MS = 50.0    # cisza na końcu pliku – bezpieczne zapętlanie bez trzasku
FREQ_LOW_HZ = 880.0
FREQ_HIGH_HZ = 1320.0


def tone_segment(freq_hz: float, duration_s: float, amplitude: float, fade_ms: float) -> list[float]:
    """Jeden ton (sinus) z fade-in na początku i fade-out na końcu segmentu."""
    sample_count = int(round(SAMPLE_RATE * duration_s))
    fade_samples = max(1, int(round(SAMPLE_RATE * fade_ms / 1000.0)))
    samples: list[float] = []
    for i in range(sample_count):
        if i < fade_samples:
            envelope = i / fade_samples
        elif i >= sample_count - fade_samples:
            envelope = (sample_count - 1 - i) / fade_samples
        else:
            envelope = 1.0
        value = amplitude * envelope * math.sin(2.0 * math.pi * freq_hz * i / SAMPLE_RATE)
        samples.append(value)
    return samples


def silence_segment(duration_s: float) -> list[float]:
    return [0.0] * int(round(SAMPLE_RATE * duration_s))


def build_samples() -> list[float]:
    samples: list[float] = []
    for i in range(SEGMENT_COUNT):
        freq = FREQ_LOW_HZ if i % 2 == 0 else FREQ_HIGH_HZ
        samples.extend(tone_segment(freq, SEGMENT_SECONDS, AMPLITUDE, FADE_MS))
    samples.extend(silence_segment(TAIL_SILENCE_MS / 1000.0))
    return samples


def write_wav(path: Path, samples: list[float]) -> None:
    max_amp = 32767
    frames = bytearray()
    for s in samples:
        clamped = max(-1.0, min(1.0, s))
        frames += struct.pack("<h", int(clamped * max_amp))
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)          # mono
        w.setsampwidth(2)          # 16-bit
        w.setframerate(SAMPLE_RATE)
        w.writeframes(bytes(frames))


def main() -> None:
    samples = build_samples()
    write_wav(OUT_PATH, samples)
    duration_s = len(samples) / SAMPLE_RATE
    print(
        f"Zapisano {OUT_PATH}: {len(samples)} próbek, {duration_s:.3f} s, "
        f"{SAMPLE_RATE} Hz, 16-bit mono, amplituda {AMPLITUDE}"
    )


if __name__ == "__main__":
    main()
