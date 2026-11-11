"""Import supplied footfalls into the existing cue bank; source files are read-only."""

import array, hashlib, json, math, subprocess, wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main():
    out = ROOT / "assets/audio"
    source = ROOT / "assets_itch/sounds/steps"
    bank = json.loads((out / "audio-bank.json").read_text())
    provenance = json.loads((out / "provenance.json").read_text())
    bank["cues"] = [c for c in bank["cues"] if not c["id"].startswith("step.")]
    provenance["files"] = [
        f
        for f in provenance["files"]
        if not f["file"].startswith(
            ("step_wood_", "step_leaves_", "step_mud_", "step_gravel_", "step_stone_")
        )
    ]
    for surface, names in {
        "wood": ["wood01", "wood02", "wood03"],
        "leaves": ["leaves01", "leaves02"],
        "stone": ["stone01"],
        "mud": ["mud02"],
        "gravel": ["gravel"],
    }.items():
        variants = []
        for i, name in enumerate(names):
            src = source / (name + ".ogg")
            filename = f"step_{surface}_{i}.wav"
            command = [
                "ffmpeg",
                "-v",
                "error",
                "-i",
                str(src),
                "-ar",
                "48000",
                "-ac",
                "1",
                "-af",
                "silenceremove=start_periods=1:start_threshold=-55dB:start_silence=0.005,areverse,silenceremove=start_periods=1:start_threshold=-55dB:start_silence=0.015,areverse,afade=t=in:d=0.003",
                "-f",
                "s16le",
                "-",
            ]
            pcm = array.array(
                "h", subprocess.run(command, capture_output=True, check=True).stdout
            )
            rms = math.sqrt(sum(x * x for x in pcm) / max(1, len(pcm)))
            peak = max(abs(x) for x in pcm)
            gain = min(4, 3276 / max(1, rms), 22937 / max(1, peak))
            pcm = array.array("h", (round(x * gain) for x in pcm))
            with wave.open(str(out / filename), "wb") as w:
                w.setnchannels(1)
                w.setsampwidth(2)
                w.setframerate(48000)
                w.writeframes(pcm.tobytes())
            variants.append({"path": filename})
            provenance["files"].append(
                {
                    "file": filename,
                    "source": str(src.relative_to(ROOT)),
                    "sourceSha256": hashlib.sha256(src.read_bytes()).hexdigest(),
                    "sha256": hashlib.sha256((out / filename).read_bytes()).hexdigest(),
                    "channels": 1,
                    "sampleRate": 48000,
                    "duration": len(pcm) / 48000,
                    "frames": len(pcm),
                    "gain": gain,
                    "loop": False,
                    "conversion": "48 kHz PCM16 mono; -55 dB edge trim; 3 ms onset fade; RMS target -20 dBFS, peak cap -3 dBFS, gain cap 4",
                    "rights": "User-supplied recording; no license supplied in steps folder.",
                }
            )
        bank["cues"].append(
            {
                "id": "step." + surface,
                "variants": variants,
                "bus": "Sfx",
                "gain": 0.28,
                "spatialMode": "ThreeDimensional",
                "minDistance": 1,
                "maxDistance": 24,
                "dopplerFactor": 0,
                "looping": False,
                "streamed": False,
                "maxInstancesPerVariant": 8,
            }
        )
    (out / "audio-bank.json").write_text(json.dumps(bank, indent=2) + "\n")
    (out / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    print("Imported eight footfalls in five surface sets.")


if __name__ == "__main__":
    main()
