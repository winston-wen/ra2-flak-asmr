"""Original Westwood bitmap glyph reader. Attribution: assets/fonts/FONT-LICENSE.txt."""
from pathlib import Path
import struct
from PIL import Image
ROOT=Path(__file__).resolve().parents[1]
FAMILY="Westwood Improved Preview"
class WestwoodFont:

    def __init__(self, path=ROOT / 'assets/fonts/WestwoodImprovedFont/game.fnt'):
        self.data = Path(path).read_bytes()
        assert self.data[:4] == b'fonT'
        self.ideograph, self.stride, self.height, self.lineheight, self.count, self.record = struct.unpack_from('<6I', self.data, 4)
        assert self.stride * self.height == self.record - 1
        self.mapping = struct.unpack_from('<65536H', self.data, 28)
        self.start = 28 + 65536 * 2
        assert self.start + self.count * self.record <= len(self.data)
        self.cache = {}

    def glyph(self, char):
        if char in self.cache:
            return self.cache[char]
        code = ord(char)
        mapped = self.mapping[code] if code < 65536 else 0
        if mapped == 0:
            return (0, Image.new('L', (1, self.height)))
        index = mapped - 1
        pos = self.start + index * self.record
        width = self.data[pos]
        image = Image.new('L', (max(1, width), self.height))
        for y in range(self.height):
            for x in range(width):
                if self.data[pos + 1 + y * self.stride + x // 8] & 128 >> x % 8:
                    image.putpixel((x, y), 255)
        self.cache[char] = (width, image)
        return (width, image)

    def width(self, text):
        return sum((self.glyph(c)[0] + 1 for c in text))

    def mask(self, text):
        mask = Image.new('L', (max(1, self.width(text)), self.height))
        x = 0
        for c in text:
            width, image = self.glyph(c)
            if width:
                mask.paste(image, (x, 0))
            x += width + 1
        return mask

    def runs(self, char):
        width, mask = self.glyph(char)
        runs = []
        for y in range(self.height):
            x = 0
            while x < width:
                if mask.getpixel((x, y)):
                    start = x
                    while x < width and mask.getpixel((x, y)):
                        x += 1
                    runs.append((start, y, x - start))
                else:
                    x += 1
        return runs

    def contours(self, char):
        """Union adjacent pixels so font rasterizers cannot fill shared edges twice."""
        width, mask = self.glyph(char)
        edges = {}

        def on(x, y):
            return 0 <= x < width and 0 <= y < self.height and (mask.getpixel((x, y)) > 0)

        def edge(a, b):
            edges.setdefault(a, []).append(b)
        for y in range(self.height):
            for x in range(width):
                if not on(x, y):
                    continue
                if not on(x, y - 1):
                    edge((x, y), (x + 1, y))
                if not on(x + 1, y):
                    edge((x + 1, y), (x + 1, y + 1))
                if not on(x, y + 1):
                    edge((x + 1, y + 1), (x, y + 1))
                if not on(x - 1, y):
                    edge((x, y + 1), (x, y))
        directions = {(1, 0): 0, (0, 1): 1, (-1, 0): 2, (0, -1): 3}
        rank = {1: 0, 0: 1, 3: 2, 2: 3}
        rings = []
        while edges:
            start = next(iter(edges))
            current = start
            previous = None
            ring = []
            while True:
                ring.append(current)
                choices = edges[current]
                if previous is None:
                    target = choices[0]
                else:
                    incoming = directions[current[0] - previous[0], current[1] - previous[1]]
                    target = min(choices, key=lambda p: rank[(directions[p[0] - current[0], p[1] - current[1]] - incoming) % 4])
                choices.remove(target)
                if not choices:
                    del edges[current]
                previous, current = (current, target)
                if current == start:
                    break
            simple = []
            for i, p in enumerate(ring):
                a = ring[i - 1]
                b = ring[(i + 1) % len(ring)]
                if (p[0] - a[0]) * (b[1] - p[1]) != (p[1] - a[1]) * (b[0] - p[0]):
                    simple.append(p)
            rings.append(simple[::-1])
        return rings
