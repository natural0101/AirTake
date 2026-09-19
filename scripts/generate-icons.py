"""Generate original geometric AirTake icons, using only the Python standard library."""
import json
import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def png(size: int) -> bytes:
    rows = bytearray()
    polygon = [(262, 740), (461, 275), (579, 275), (781, 740), (639, 740), (523, 443), (404, 740)]
    def inside(x, y):
        result = False
        j = len(polygon) - 1
        for i, (xi, yi) in enumerate(polygon):
            xj, yj = polygon[j]
            if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
                result = not result
            j = i
        return result
    for y0 in range(size):
        rows.append(0)
        for x0 in range(size):
            x, y = (x0 + .5) * 1024 / size, (y0 + .5) * 1024 / size
            color = (16, 25, 37)
            if 100 <= x <= 924 and 160 <= y <= 864:
                color = (25, 44, 59)
            if inside(x, y): color = (123, 219, 200)
            if 431 <= x <= 617 and 630 <= y <= 695: color = (208, 255, 243)
            rows.extend(color)
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')

images = [(size, png(size)) for size in (16, 32, 48, 256)]
offset = 6 + 16 * len(images)
header = bytearray(struct.pack('<HHH', 0, 1, len(images)))
for size, image in images:
    header.extend(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(image), offset))
    offset += len(image)
(ROOT / 'src/AirTake.Desktop/AirTake.ico').write_bytes(header + b''.join(image for _, image in images))
assets = ROOT / 'ios/AirTakeCamera/Assets.xcassets'
icons = assets / 'AppIcon.appiconset'
icons.mkdir(parents=True, exist_ok=True)
(assets / 'Contents.json').write_text(json.dumps({'info': {'author': 'xcode', 'version': 1}}))
items = []
for size in (20, 29, 40, 60):
    for scale in (2, 3):
        name = f'icon-{size}@{scale}x.png'
        (icons / name).write_bytes(png(size * scale))
        items.append({'size': f'{size}x{size}', 'idiom': 'iphone', 'filename': name, 'scale': f'{scale}x'})
(icons / 'icon-1024.png').write_bytes(png(1024))
items.append({'size': '1024x1024', 'idiom': 'ios-marketing', 'filename': 'icon-1024.png', 'scale': '1x'})
(icons / 'Contents.json').write_text(json.dumps({'images': items, 'info': {'author': 'xcode', 'version': 1}}, indent=2))
