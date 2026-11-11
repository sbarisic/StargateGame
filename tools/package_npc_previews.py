"""Assemble the Blender-rendered gallery frames into labeled GIF/MP4 previews."""

from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import subprocess

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts/npc-rigs"
FRAMES = OUT / "gallery"
labels = ["Fantasy villager", "Peasant warrior", "Old villager", "Old villager 2"]
font = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 18)
# A regional rerender can update the fantasy character without spending time
# rendering the unchanged three actors again. Camera and scene stay identical.
for patch in FRAMES.glob("*-fantasy-update.png"):
    target = patch.with_name(patch.name.replace("-fantasy-update", ""))
    if target.exists():
        full = Image.open(target).convert("RGB")
        region = Image.open(patch).convert("RGB")
        assert region.height == full.height
        full.paste(
            region, (full.width - region.width if "-rear-" in target.name else 0, 0)
        )
        full.save(target)
for clip, duration in [("walk", 4 / 3), ("idle", 2.5)]:
    frames = []
    for i in range(24):
        source = Image.open(FRAMES / f"{clip}-{i:02}.png").convert("RGB")
        canvas = Image.new("RGB", (source.width, source.height + 48), (31, 36, 43))
        canvas.paste(source, (0, 0))
        draw = ImageDraw.Draw(canvas)
        for j, label in enumerate(labels):
            x = (j + 0.5) * source.width / 4
            draw.text(
                (x, source.height + 12), label, anchor="mt", font=font, fill="white"
            )
        canvas.save(FRAMES / f"{clip}-labeled-{i:02}.png")
        frames.append(canvas)
    # One common palette avoids distracting color flicker between frames.
    palette = frames[0].quantize(colors=192)
    images = [f.quantize(palette=palette, dither=Image.Dither.NONE) for f in frames]
    delays = [
        10 * (round((i + 1) * duration * 100 / 24) - round(i * duration * 100 / 24))
        for i in range(24)
    ]
    images[0].save(
        OUT / f"npcs-{clip}.gif",
        save_all=True,
        append_images=images[1:],
        duration=delays,
        loop=0,
        disposal=2,
    )
    frames[0].save(OUT / f"npcs-{clip}.jpg", quality=94)
    subprocess.run(
        [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-framerate",
            str(24 / duration),
            "-i",
            str(FRAMES / f"{clip}-labeled-%02d.png"),
            "-c:v",
            "libx264",
            "-crf",
            "18",
            "-pix_fmt",
            "yuv420p",
            "-movflags",
            "+faststart",
            str(OUT / f"npcs-{clip}.mp4"),
        ],
        check=True,
    )
    print(clip, "saved GIF, MP4 and still preview")
