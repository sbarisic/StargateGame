"""Reproducible local audio import; never modifies the source games."""

import argparse, hashlib, json, subprocess, wave, array
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--source", type=Path, default=Path("D:/Games/Stargate Network"))
parser.add_argument("--raylib", type=Path, default=Path("E:/Projects/RaylibGame"))
args = parser.parse_args()
out = ROOT / "assets/audio"
work = ROOT / "artifacts/audio-import"
out.mkdir(parents=True, exist_ok=True)
work.mkdir(parents=True, exist_ok=True)
inventory = json.loads((args.source / "extraction/audio-inventory.json").read_text())
records = {r["name"]: r for r in inventory if r["class"] == "SoundWave"}
base = [
    str(args.source / "extraction/tools/umodel/umodel.exe"),
    "-path=" + str(args.source / "game/SGN/Content/Paks"),
    "-game=ue4.21",
]
sources, cues, provenance = {}, [], []


def run(cmd):
    result = subprocess.run(cmd, capture_output=True, text=True, errors="replace")
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout + result.stderr


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def source(name):
    if name not in sources:
        record = records[name]
        log = run(
            base + ["-export", "-sounds", "-out=" + str(work), record["package"], name]
        )
        (work / (name + ".log")).write_text(log)
        found = [
            p
            for p in work.rglob(name + ".*")
            if p.suffix.lower() in (".wav", ".ogg", ".flac")
        ]
        if len(found) != 1:
            raise RuntimeError("Expected one recording: " + name)
        sources[name] = found[0]
    return sources[name]


def cue(
    id, names, gain=0.5, distance=32, loop=False, spatial=True, bus="Sfx", raylib=False
):
    variants = []
    for i, name in enumerate(names):
        src = args.raylib / "Voxelgine/data/sound" / name if raylib else source(name)
        dst = out / (id.replace(".", "_") + f"_{i}" + (".flac" if loop else ".wav"))
        # Lossless PCM output; short effects fade at the edges. Loop endpoints
        # use a short offline overlap crossfade, never a runtime mixer callback.
        cmd = [
            "ffmpeg",
            "-v",
            "error",
            "-y",
            "-i",
            str(src),
            "-map_metadata",
            "-1",
            "-ar",
            "48000",
            "-ac",
            "1" if spatial else "2",
        ]
        if not loop:
            cmd += ["-af", "afade=t=in:d=0.005,areverse,afade=t=in:d=0.01,areverse"]
        cmd += ["-c:a", "flac" if loop else "pcm_s16le", str(dst)]
        run(cmd)
        if loop:
            channels = 1 if spatial else 2
            raw = subprocess.run(
                ["ffmpeg", "-v", "error", "-i", str(dst), "-f", "s16le", "-"],
                capture_output=True,
                check=True,
            ).stdout
            samples = array.array("h", raw)
            frames = len(samples) // channels
            overlap = min(int(48000 * (0.04 if id == "scrape" else 0.15)), frames // 4)
            blended = array.array("h")
            for frame in range(overlap):
                weight = frame / max(1, overlap - 1)
                for ch in range(channels):
                    blended.append(
                        round(
                            samples[(frames - overlap + frame) * channels + ch]
                            * (1 - weight)
                            + samples[frame * channels + ch] * weight
                        )
                    )
            pcm = samples[overlap * channels : (frames - overlap) * channels] + blended
            prepared = work / (dst.stem + "-loop.wav")
            with wave.open(str(prepared), "wb") as wav:
                wav.setnchannels(channels)
                wav.setsampwidth(2)
                wav.setframerate(48000)
                wav.writeframes(pcm.tobytes())
            run(
                [
                    "ffmpeg",
                    "-v",
                    "error",
                    "-y",
                    "-i",
                    str(prepared),
                    "-map_metadata",
                    "-1",
                    "-c:a",
                    "flac",
                    str(dst),
                ]
            )
        info = json.loads(
            run(
                [
                    "ffprobe",
                    "-v",
                    "error",
                    "-show_streams",
                    "-show_format",
                    "-of",
                    "json",
                    str(dst),
                ]
            )
        )
        stream = info["streams"][0]
        pcm = subprocess.run(
            ["ffmpeg", "-v", "error", "-i", str(dst), "-f", "s16le", "-"],
            capture_output=True,
            check=True,
        ).stdout
        provenance.append(
            dict(
                file=dst.name,
                source=str(src if raylib else records[name]["package"]),
                sourceSha256=sha(src),
                sha256=sha(dst),
                pcmSha256=hashlib.sha256(pcm).hexdigest(),
                channels=stream["channels"],
                sampleRate=int(stream["sample_rate"]),
                frames=len(pcm) // (2 * stream["channels"]),
                duration=float(info["format"]["duration"]),
                loop=loop,
                loopStart=0,
                loopEndFrames=len(pcm) // (2 * stream["channels"]),
                loopCrossfadeFrames=overlap if loop else 0,
                conversion=cmd[cmd.index("-map_metadata") : -1],
            )
        )
        variants.append(dict(path=dst.name))
    cues.append(
        dict(
            id=id,
            variants=variants,
            bus=bus,
            gain=gain,
            spatialMode="ThreeDimensional" if spatial else "TwoDimensional",
            minDistance=2 if distance > 32 else 1,
            maxDistance=distance,
            dopplerFactor=0,
            looping=loop,
            streamed=loop,
            maxInstancesPerVariant=4 if not loop else 8,
        )
    )


cue("dhd.press", ["A_Press"], 0.18)
cue("dhd.orb", ["A_Dome_Button"], 0.55)
cue("dhd.glyph", [f"A_Glyph_Button_{i:02}" for i in range(1, 8)], 0.45)
cue("gate.chevron", ["A_Chevron_Encoding_DHD"], 0.65, 64)
cue("gate.incoming", [f"A_Chevron_Closing0{i}" for i in range(1, 4)], 0.6, 64)
cue("gate.open", ["A_Kawoosh"], 0.75, 96)
cue("gate.abort", ["A_Abort_NOrotating"], 0.55, 64)
cue("gate.close", ["A_Shutdown"], 0.65, 96)
cue("gate.power", ["A_Offworld_Powered_Loop"], 0.25, 48, True)
cue("gate.hum", ["A_Puddle_Loop"], 0.3, 48, True)
cue("gate.contact", [f"A_Puddle_Through_{i:02}" for i in range(8)], 0.5, 32)
cue("door", ["A_SimpleDoor_Opening"], 0.4)
cue(
    "ambience.wind",
    ["A_Wind_01_Ambience_Loop"],
    0.22,
    loop=True,
    spatial=False,
    bus="Ambience",
)
cue(
    "ambience.desert",
    ["A_Wind_02_Ambience_Loop"],
    0.3,
    loop=True,
    spatial=False,
    bus="Ambience",
)
cue(
    "ambience.snow",
    ["A_Snow_Ambience_Loop"],
    0.35,
    loop=True,
    spatial=False,
    bus="Ambience",
)
cue("step", [f"walk/walk{i}.wav" for i in range(1, 6)], 0.28, 24, raylib=True)
cue("jump", ["jump/jump1.wav"], 0.25, 24, raylib=True)
cue("land", ["crash1/crash1.wav"], 0.25, 24, raylib=True)
cue("impact", ["crash1/crash1.wav"], 0.3, 32, raylib=True)
cue("scrape", ["walk/walk3.wav"], 0.09, 24, True, raylib=True)
cue("swim", ["swim/swim1.wav"], 0.25, 24, raylib=True)
cue("build.place", ["block/place1.wav"], 0.4, raylib=True)
cue("build.remove", ["block/break1.wav"], 0.4, raylib=True)
cue("ui.error", ["info/info2.wav"], 0.2, spatial=False, bus="Ui", raylib=True)
cue(
    "ambience.birds",
    ["birds/birds_loop.flac"],
    0.13,
    loop=True,
    spatial=False,
    bus="Ambience",
    raylib=True,
)
cue(
    "ambience.underwater",
    ["underwater1/underwater1.wav"],
    0.3,
    loop=True,
    spatial=False,
    bus="Ambience",
    raylib=True,
)
cue("water", ["ambient_pond/pond1.wav"], 0.12, 32, True, raylib=True)
# Store original cue metadata as extraction evidence, not as a runtime dependency.
for r in inventory:
    if r["class"] == "SoundCue" and r["group"] in ("DHD", "Stargate and iris"):
        (work / (r["name"] + ".txt")).write_text(
            run(base + ["-dump", r["package"], r["name"]])
        )
(out / "audio-bank.json").write_text(
    json.dumps(dict(version=1, cues=cues), indent=2) + "\n"
)
(out / "provenance.json").write_text(
    json.dumps(
        dict(
            version=1,
            raylibRevision="a183d45726f60d50e1a7d2a4803acf4ad82474a6",
            note="Extracted recordings retain their original rights; code licenses do not grant recording rights.",
            files=provenance,
        ),
        indent=2,
    )
    + "\n"
)
print(f"Imported {len(provenance)} recordings / {len(cues)} cues into {out}")
import import_steps

import_steps.main()

run(["python", str(ROOT / "tools/import_rifle_audio.py")])
