"""Prepare CC0 recorded rifle audio; preserve existing dry-fire and impacts.

Matches the existing local-audio workflow; no source game is modified.
"""
import hashlib, json, subprocess, wave
from pathlib import Path
import numpy as np

ROOT=Path(__file__).resolve().parents[1]
RATE = 48000
SOURCE = ROOT / "assets_itch/sounds/military"
# Exact donor cuts. Mechanical roles still need listening review: the agent's
# audio-input tool cannot audition recordings. Never infer approval from a name.
CUTS = [
    ("fire_01", "assault_rifles/ar/ar_single_near_01.wav", 0, None, -16, -3),
    ("fire_02", "assault_rifles/ar/ar_single_near_02.wav", 0, None, -16, -3),
    ("reload_remove", "assault_rifles/ar/ar_reload_near_01.wav", .075, .22, -21, -6),
    ("reload_insert", "assault_rifles/ar/ar_reload_near_01.wav", .87, 1.26, -21, -6),
    ("reload_pull", "pistols/pi_hand/pi_hand_reload_near_02.wav", .12, .36, -21, -6),
    ("reload_release", "pistols/pi_hand/pi_hand_reload_near_02.wav", .53, .74, -21, -6),
]

def write(path, samples):
    pcm = np.round(np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes()
    with wave.open(str(path), "wb") as f:
        f.setnchannels(1); f.setsampwidth(2); f.setframerate(RATE); f.writeframes(pcm)
    return pcm

def sha(data):
    return hashlib.sha256(data).hexdigest()

def keep_auxiliary(out, bank, provenance):
    # Retain the original dry/impact PCM exactly. This also bootstraps a clean
    # full import, whose general audio bank does not yet contain rifle cues.
    rng = np.random.default_rng(7319)
    for length in [.12, .24, .16, .15, .18]:
        rng.normal(0, 1, round(length * RATE))  # Original reload RNG consumption.
    for name, seconds, length, pitch, gain, cue_gain, distance in [
        ("dry", .14, .1, 1700, .45, .5, 8),
        ("impact", .24, .2, 2200, .45, .5, 35),
    ]:
        file = out / f"rifle_{name}.wav"
        t = np.arange(round(length * RATE)) / RATE
        noise = rng.normal(0, 1, len(t)); high = noise - np.roll(noise, 1)
        signal = np.zeros(round(seconds * RATE))
        signal[:len(t)] = (.45 * high + .5 * np.sin(2*np.pi*pitch*t) + .2*np.sin(2*np.pi*pitch*2.37*t)) * np.exp(-t*35) * gain
        if not file.exists():
            with wave.open(str(file), "wb") as f:
                f.setnchannels(1); f.setsampwidth(2); f.setframerate(RATE)
                f.writeframes((np.tanh(signal)*.8*32767).astype("<i2").tobytes())
        if not any(c["id"] == "rifle." + name for c in bank["cues"]):
            bank["cues"].append(dict(id="rifle."+name, variants=[dict(path=file.name)], bus="Sfx", gain=cue_gain,
                spatialMode="ThreeDimensional", minDistance=1, maxDistance=distance, dopplerFactor=0,
                looping=False, streamed=False, maxInstancesPerVariant=16))
        if not any(f["file"] == file.name for f in provenance["files"]):
            with wave.open(str(file), "rb") as f:
                frames = f.getnframes(); pcm = f.readframes(frames)
            provenance["files"].append(dict(file=file.name, source="Original procedural audio: tools/import_rifle_audio.py",
                sourceSha256=sha(Path(__file__).read_bytes()), sha256=sha(file.read_bytes()), pcmSha256=sha(pcm),
                channels=1, sampleRate=RATE, frames=frames, duration=frames/RATE, loop=False, loopStart=0,
                loopEndFrames=frames, loopCrossfadeFrames=0, conversion=["unchanged original mono PCM16 48000 Hz"]))

def main():
    out = ROOT / "assets/audio"
    bank = json.loads((out / "audio-bank.json").read_text())
    provenance = json.loads((out / "provenance.json").read_text())
    keep_auxiliary(out, bank, provenance)
    replaced = {"rifle.fire", "rifle.reload"} | {"rifle.reload." + p for p in ["remove", "insert", "pull", "release"]}
    bank["cues"] = [c for c in bank["cues"] if c["id"] not in replaced]
    replaced_files = {"rifle_fire.wav", "rifle_reload.wav"} | {f"rifle_{c[0]}.wav" for c in CUTS}
    provenance["files"] = [f for f in provenance["files"] if f["file"] not in replaced_files]
    samples_by_name = {}
    for name, source, start, end, target_db, peak_db in CUTS:
        path = SOURCE / source
        raw = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-ac", "1", "-ar", str(RATE), "-f", "f32le", "pipe:1"], capture_output=True, check=True).stdout
        samples = np.frombuffer(raw, dtype="<f4").astype(np.float64)
        stop = len(samples) if end is None else round(end * RATE)
        samples = samples[round(start * RATE):stop].copy()
        if name.startswith("fire"):
            active = np.flatnonzero(np.abs(samples) > np.max(np.abs(samples)) * .005)
            trim = max(0, int(active[0]) - RATE // 1000) if len(active) else 0
            start += trim / RATE; samples = samples[trim:]
        block = RATE // 100
        levels = np.array([np.sqrt(np.mean(samples[i:i+block] ** 2)) for i in range(0, len(samples), block)])
        active = levels[levels > levels.max() * .1]
        rms = np.sqrt(np.mean(active ** 2))
        gain = min(10 ** (target_db / 20) / max(rms, 1e-9), 10 ** (peak_db / 20) / max(np.abs(samples).max(), 1e-9))
        samples *= gain
        fade = min(round(RATE * (.00025 if name.startswith("fire") else .002)), len(samples) // 2)
        samples[:fade] *= np.linspace(0, 1, fade)
        tail = min(round(RATE * .005), len(samples) // 2)
        samples[-tail:] *= np.linspace(1, 0, tail)
        file = out / f"rifle_{name}.wav"
        pcm = write(file, samples); samples_by_name[name] = samples
        provenance["files"].append(dict(file=file.name, source=path.relative_to(ROOT).as_posix(), sourceSha256=sha(path.read_bytes()),
            sha256=sha(file.read_bytes()), pcmSha256=sha(pcm), channels=1, sampleRate=RATE, frames=len(samples), duration=len(samples)/RATE,
            loop=False, loopStart=0, loopEndFrames=len(samples), loopCrossfadeFrames=0,
            license="CC0-1.0 (supplied military pack)", licenseFile="assets_itch/sounds/military/SOURCES_CC0.txt",
            cutStartSeconds=start, cutEndSeconds=stop/RATE, gain=gain, listeningReviewPending=True,
            conversion=["mono PCM16 48000 Hz", "active-block RMS gain with peak ceiling; no compression or pitch change", "short boundary fades"]))
    for cue, files, gain, distance in [
        ("rifle.fire", ["rifle_fire_01.wav", "rifle_fire_02.wav"], .65, 100),
        *[(f"rifle.reload.{part}", [f"rifle_reload_{part}.wav"], .6, 15) for part in ["remove", "insert", "pull", "release"]],
    ]:
        bank["cues"].append(dict(id=cue, variants=[dict(path=f) for f in files], bus="Sfx", gain=gain,
            spatialMode="ThreeDimensional", minDistance=1, maxDistance=distance, dopplerFactor=0,
            looping=False, streamed=False, maxInstancesPerVariant=16))
    (out / "audio-bank.json").write_text(json.dumps(bank, indent=2) + "\n")
    (out / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    # Preview reads the runtime marker definition rather than duplicating timings.
    import re
    definition = (ROOT / "src/StargateGame.Client/RifleReloadAudio.cs").read_text()
    markers = re.findall(r'new\("rifle\.reload\.(\w+)", ([\d.]+)\)', definition)
    if len(markers) != 4:
        raise ValueError("Expected four reload presentation markers")
    preview = np.zeros(round(164 / 60 * RATE))
    for part, seconds in markers:
        sample = samples_by_name["reload_" + part]; at = round(float(seconds) * RATE)
        preview[at:at+len(sample)] += sample
    work = ROOT / "artifacts/audio-import/rifle"; work.mkdir(parents=True, exist_ok=True)
    write(work / "reload-preview.wav", preview)
    for name in ["rifle_fire.wav", "rifle_reload.wav"]:
        if not any(v["path"] == name for c in bank["cues"] for v in c["variants"]):
            (out / name).unlink(missing_ok=True)
    print("Prepared two shot variants, four reload cues and reload-preview.wav; dry/impact unchanged. Listening review pending.")

if __name__ == "__main__":
    main()
