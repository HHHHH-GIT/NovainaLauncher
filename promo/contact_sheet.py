from pathlib import Path
from PIL import Image, ImageDraw
import json
import sys

root = Path(__file__).parent / 'output'
verified = '--verified' in sys.argv
if verified:
    manifest = json.loads((root / 'verification.json').read_text())
    frames = [(t, root / 'verified' / f'{t:.2f}.png') for t in manifest['sampledFrames']]
else:
    manifest = json.loads((root / 'render-stills.json').read_text())
    frames = [(n / 30, root / f'frame-{n:04d}.png') for n in manifest['sampledFrames']]
canvas = Image.new('RGB', (1440, ((len(frames) + 2) // 3) * 296), '#10151f')
draw = ImageDraw.Draw(canvas)
for i, (t, path) in enumerate(frames):
    im = Image.open(path).convert('RGB')
    im.thumbnail((470, 264))
    x, y = (i % 3) * 480 + 5, (i // 3) * 296 + 5
    canvas.paste(im, (x, y))
    draw.text((x, y + 269), f'{t:.2f}s', fill='#b5c5dc')
output = root / ('verified-storyboard.jpg' if verified else 'storyboard.jpg')
canvas.save(output, quality=94)
print(output)
