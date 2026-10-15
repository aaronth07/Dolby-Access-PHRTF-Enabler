"""Plot matched-axis frequency and impulse-response energy comparisons."""

from __future__ import annotations

import argparse
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np


NFFT = 131072
CENTERS_HZ = np.geomspace(20, 20000, 700)


def load_response(path: Path, direction_index: int) -> np.ndarray:
    responses = np.load(path, allow_pickle=False)
    if responses.ndim != 3 or responses.shape[1] != 2:
        raise ValueError(f"Expected direction × 2 ears × samples in {path}")
    if not 0 <= direction_index < responses.shape[0]:
        raise ValueError(f"Direction index is outside the capture in {path}")
    response = np.asarray(responses[direction_index], dtype=np.float64)
    if response.shape[-1] < 8192 or not np.isfinite(response).all():
        raise ValueError(f"Response is too short or contains non-finite samples: {path}")
    return response


def frequency_response(response: np.ndarray, sample_rate: int) -> tuple[np.ndarray, np.ndarray]:
    frequencies = np.fft.rfftfreq(NFFT, 1 / sample_rate)
    power = np.mean(np.abs(np.fft.rfft(response, n=NFFT, axis=-1)) ** 2, axis=0)
    low = np.searchsorted(frequencies, CENTERS_HZ / 2 ** (1 / 12))
    high = np.searchsorted(frequencies, CENTERS_HZ * 2 ** (1 / 12))
    high = np.maximum(high, low + 1)
    high = np.minimum(high, len(power))
    cumulative = np.r_[0.0, np.cumsum(power)]
    smoothed = (cumulative[high] - cumulative[low]) / (high - low)
    return CENTERS_HZ, 10 * np.log10(np.maximum(smoothed, 1e-24))


def impulse_energy(response: np.ndarray, sample_rate: int) -> tuple[np.ndarray, np.ndarray]:
    anchor = int(np.min(np.argmax(np.abs(response[:, :8192]), axis=-1)))
    block = round(0.002 * sample_rate)
    count = (response.shape[-1] - anchor) // block
    if count < 1:
        raise ValueError("Response does not contain a complete 2 ms energy block")
    blocks = response[:, anchor : anchor + count * block].reshape(2, count, block)
    mean_square = np.mean(blocks**2, axis=(0, 2))
    return np.arange(count) * (1000 * block / sample_rate), 10 * np.log10(np.maximum(mean_square, 1e-24))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--balanced-input", type=Path, required=True, help="Balanced response NPY")
    parser.add_argument("--spacious-input", type=Path, required=True, help="Spacious response NPY")
    parser.add_argument("--output", type=Path, required=True, help="Output PNG path")
    parser.add_argument("--sample-rate", type=int, required=True, help="Capture sample rate in Hz")
    parser.add_argument("--direction-index", type=int, required=True, help="Zero-based direction index in each capture")
    parser.add_argument("--direction-label", required=True, help="Direction and distance shown below the plot")
    args = parser.parse_args()
    if args.sample_rate <= 0:
        parser.error("--sample-rate must be positive")

    responses = [
        ("Balanced", load_response(args.balanced_input, args.direction_index)),
        ("Spacious", load_response(args.spacious_input, args.direction_index)),
    ]
    plt.rcParams.update(
        {
            "font.family": "DejaVu Sans",
            "font.size": 11,
            "axes.spines.top": False,
            "axes.spines.right": False,
            "axes.grid": True,
            "grid.alpha": 0.18,
        }
    )
    figure, axes = plt.subplots(2, 2, figsize=(12, 8), sharex="row", sharey="row", layout="constrained")
    for column, (label, response) in enumerate(responses):
        frequency, magnitude = frequency_response(response, args.sample_rate)
        axes[0, column].semilogx(frequency, magnitude, color="#006b9c", linewidth=1.6)
        axes[0, column].set(
            title=label,
            xlim=(20, 20000),
            ylim=(-40, 15),
            xlabel="Frequency (Hz)",
            ylabel="Transfer magnitude (dB re 1)",
        )
        axes[0, column].set_xticks([20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000])
        axes[0, column].set_xticklabels(["20", "50", "100", "200", "500", "1k", "2k", "5k", "10k", "20k"])

        time_ms, energy = impulse_energy(response, args.sample_rate)
        axes[1, column].plot(time_ms, energy, color="#006b9c", linewidth=1.2)
        axes[1, column].set(
            xlim=(0, 350),
            ylim=(-110, 0),
            xlabel="Time after earliest direct peak (ms)",
            ylabel="Mean-square level (dB re 1)",
        )

    figure.suptitle("DTS Headphone:X · Personalized FIRs held constant", fontsize=15, fontweight="bold")
    figure.supxlabel(
        f"{args.direction_label} · FR: 1/6-octave smoothing · IR energy: 2 ms blocks · Mean left/right power · No gain normalization",
        fontsize=10,
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    figure.savefig(args.output, dpi=180)
    plt.close(figure)


if __name__ == "__main__":
    main()
