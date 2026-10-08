import os
import sys
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.ticker import FuncFormatter, LogLocator, NullFormatter

DATA = {
    'BC1': {
        'Textures': [
            ('SharpBcn', 0.83, 83.18),
            ('rgbcx 18', 9.87, 83.7),
            ('rgbcx 10', 4.02, 82.93),
            ('icbc 9', 2.07, 82.82),
            ('icbc 8', 0.77, 81.5),
            ('Compressonator 1.0', 3.15, 81.12),
            ('stb_dxt', 0.35, 80.7),
            ('ISPC', 0.42, 77.84),
            ('BCnEncoder.NET best', 8.39, 82.51),
            ('BCnEncoder.NET balanced', 9.48, 75.4),
            ('BCnEncoder.NET fast', 2.79, 69.5),
        ],
        'CLIC': [
            ('SharpBcn', 0.54, 83.33),
            ('rgbcx 18', 8.63, 83.57),
            ('rgbcx 10', 2.4, 82.68),
            ('icbc 9', 2.16, 82.89),
            ('icbc 8', 0.79, 81.95),
            ('Compressonator 1.0', 4.36, 80.31),
            ('stb_dxt', 0.26, 80.0),
            ('ISPC', 0.24, 77.25),
            ('BCnEncoder.NET best', 9.07, 82.28),
            ('BCnEncoder.NET balanced', 9.48, 81.87),
            ('BCnEncoder.NET fast', 2.9, 71.93),
        ],
    },
    'BC7': {
        'Textures': [
            ('SharpBcn', 2.91, 89.92),
            ('bc7e 4', 5.02, 89.93),
            ('bc7e 2', 3.81, 89.6),
            ('bc7enc 4', 7.55, 89.29),
            ('ISPC slow', 16.81, 90.0),
            ('ISPC basic', 5.09, 89.87),
            ('ISPC veryfast', 1.38, 89.6),
            ('Compressonator 0.05', 95.03, 89.41),
            ('BCnEncoder.NET fast', 112.44, 89.42),
        ],
        'CLIC': [
            ('SharpBcn', 2.58, 91.32),
            ('bc7e 4', 3.38, 91.49),
            ('bc7e 2', 2.07, 91.43),
            ('bc7enc 4', 5.41, 91.41),
            ('ISPC slow', 16.61, 91.45),
            ('ISPC basic', 4.84, 91.36),
            ('ISPC veryfast', 1.21, 91.32),
            ('Compressonator 0.05', 193.65, 91.42),
            ('Compressonator 0.2', 426.31, 91.66),
            ('BCnEncoder.NET fast', 116.57, 91.5),
        ],
    },
    'BC4': {
        'Textures': [
            ('SharpBcn', 0.34, 89.3),
            ('Compressonator 1.0', 0.77, 89.21),
            ('rgbcx', 2.74, 88.5),
            ('ISPC', 0.21, 88.06),
            ('stb_dxt', 0.18, 87.91),
            ('BCnEncoder.NET best', 2.43, 89.18),
            ('BCnEncoder.NET fast', 1.8, 89.19),
        ],
    },
    'BC6H': {
        'Unsigned': [
            ('SharpBcn', 0.1, 91.06),
            ('ISPC veryslow', 0.79, 91.08),
            ('ISPC slow', 0.33, 91.08),
            ('ISPC basic', 0.07, 91.09),
            ('ISPC fast', 0.03, 90.85),
            ('ISPC veryfast', 0.01, 88.5),
            ('Compressonator 1.0', 159.78, 90.01),
            ('Compressonator 0.05', 7.39, 90.03),
            ('BCnEncoder.NET best', 13.17, 89.08),
            ('BCnEncoder.NET balanced', 1.17, 87.32),
            ('BCnEncoder.NET fast', 0.21, 87.58),
        ],
        'Signed': [
            ('SharpBcn', 0.12, 89.41),
            ('Compressonator 1.0', 122.91, 84.32),
            ('Compressonator 0.05', 7.74, 84.06),
            ('BCnEncoder.NET best', 12.95, 87.57),
            ('BCnEncoder.NET balanced', 1.13, 85.53),
        ],
    },
    'BCnEncoder.NET': [
        ('BC1 Textures', 0.83, 83.18, 8.39, 82.51),
        ('BC1 CLIC', 0.54, 83.33, 9.07, 82.28),
        ('BC4 Textures', 0.34, 89.3, 2.43, 89.18),
        ('BC7 Textures', 2.91, 89.92, 112.44, 89.42),
        ('BC7 CLIC', 2.58, 91.32, 116.57, 91.5),
        ('BC6H HDRIs', 0.1, 91.06, 13.17, 89.08),
        ('BC6H signed HDRIs', 0.12, 89.41, 12.95, 87.57),
    ],
}

FAMILIES = [
    ('SharpBcn', '#F76707'),
    ('BCnEncoder.NET', '#E03131'),
    ('Compressonator', '#AE3EC9'),
    ('ISPC', '#2F9E44'),
    ('icbc', '#1098AD'),
    ('stb_dxt', '#868E96'),
    ('rgbcx', '#4263EB'),
    ('bc7e', '#4263EB'),
    ('bc7enc', '#4263EB'),
]

THEME = {'background': '#161B22', 'text': '#E6EDF3', 'muted': '#9198A1', 'grid': '#30363D', 'front': '#6E7681'}

OFFSETS = {
    ('BC1', 'Textures', 'BCnEncoder.NET balanced'): (-7, 12, 'right'),
    ('BC1', 'Textures', 'BCnEncoder.NET fast'): (-7, 3, 'right'),
    ('BC7', 'Textures', 'SharpBcn'): (-9, 6, 'right'),
    ('BC7', 'Textures', 'bc7e 4'): (6, 5, 'left'),
    ('BC7', 'Textures', 'Compressonator 0.05'): (-7, -11, 'right'),
    ('BC7', 'Textures', 'BCnEncoder.NET fast'): (-7, 6, 'right'),
    ('BC7', 'CLIC', 'ISPC veryfast'): (5, -11, 'left'),
    ('BC7', 'CLIC', 'SharpBcn'): (8, -4, 'left'),
    ('BC7', 'CLIC', 'Compressonator 0.2'): (-7, 4, 'right'),
    ('BC4', 'Textures', 'BCnEncoder.NET fast'): (0, 8, 'center'),
    ('BC4', 'Textures', 'BCnEncoder.NET best'): (0, -13, 'center'),
    ('BC4', 'Textures', 'Compressonator 1.0'): (0, -13, 'center'),
    ('BC6H', 'Unsigned', 'SharpBcn'): (0, -16, 'center'),
    ('BC6H', 'Unsigned', 'ISPC basic'): (-5, 7, 'right'),
    ('BC6H', 'Unsigned', 'ISPC slow'): (0, 7, 'center'),
    ('BC6H', 'Unsigned', 'ISPC veryslow'): (5, 7, 'left'),
    ('BC6H', 'Unsigned', 'ISPC fast'): (-7, -3, 'right'),
    ('BC6H', 'Unsigned', 'Compressonator 1.0'): (-7, 5, 'right'),
    ('BC6H', 'Unsigned', 'Compressonator 0.05'): (7, -11, 'left'),
}

LIMITS = {
    ('BC1', 'Textures'): (76.5, 84.3),
    ('BC1', 'CLIC'): (76.5, 84.3),
    ('BC6H', 'Unsigned'): (86.8, 91.6),
}


def color(name):
    return next(c for prefix, c in FAMILIES if name.startswith(prefix))


def pareto(points):
    front = []

    for name, time, quality in sorted(points, key=lambda p: (p[1], -p[2])):
        if not front or quality > front[-1][2]:
            front.append((name, time, quality))

    return front


def seconds(value, _):
    return f'{value:g} s'


def style(axis, theme, wide=False):
    axis.set_xscale('log')
    axis.xaxis.set_major_locator(LogLocator(base=10, subs=(1,) if wide else (1, 2, 5)))
    axis.xaxis.set_major_formatter(FuncFormatter(seconds))
    axis.xaxis.set_minor_formatter(NullFormatter())
    axis.grid(True, color=theme['grid'], linewidth=0.6)
    axis.set_axisbelow(True)
    axis.tick_params(colors=theme['muted'], labelsize=8.5)

    for spine in axis.spines.values():
        spine.set_visible(False)


def scatter(axis, fmt, panel, points, theme, limits):
    times = [p[1] for p in points]
    style(axis, theme, wide=max(times) / min(times) > 1000)
    low, high = limits if limits else (min(p[2] for p in points) - 0.25, max(p[2] for p in points) + 0.25)
    front = pareto(points)
    axis.plot([p[1] for p in front], [p[2] for p in front], linestyle='--', linewidth=1, color=theme['front'], zorder=1)

    for name, time, quality in points:
        clipped = quality < low
        y = low + (high - low) * 0.02 if clipped else quality
        main = name == 'SharpBcn'
        axis.scatter([time], [y], s=90 if main else 34, color=color(name), marker='v' if clipped else 'o', zorder=3,
                     edgecolors=theme['text'] if main else 'none', linewidths=0.8)
        label = f'{name} ({quality:g})' if clipped else name
        dx, dy, align = OFFSETS.get((fmt, panel, name), (7, 3, 'left'))
        axis.annotate(label, (time, y), xytext=(dx, dy), textcoords='offset points', ha=align, fontsize=8.5 if main else 7.5,
                      fontweight='bold' if main else 'normal', color=theme['text'] if main else theme['muted'], zorder=4)

    axis.set_ylim(low, high)
    axis.set_title(f'{fmt}, {panel}', color=theme['text'], fontsize=10.5, loc='left')
    axis.set_xlabel('Time, log scale', color=theme['muted'], fontsize=8.5)
    axis.set_ylabel('SSIMULACRA 2, higher is better', color=theme['muted'], fontsize=8.5)


def speed_chart(path, theme):
    rows = DATA['BCnEncoder.NET']
    figure, axis = plt.subplots(figsize=(9, 4.2))
    style(axis, theme)
    axis.grid(True, axis='x', color=theme['grid'], linewidth=0.6)
    axis.grid(False, axis='y')

    for i, (label, ours, our_quality, theirs, their_quality) in enumerate(rows):
        y = len(rows) - 1 - i
        axis.barh(y + 0.19, ours, height=0.36, color=color('SharpBcn'))
        axis.barh(y - 0.19, theirs, height=0.36, color=color('BCnEncoder.NET'))
        axis.annotate(f'{ours:g} s, {our_quality:g}', (ours, y + 0.19), xytext=(5, 0), textcoords='offset points', va='center', fontsize=7.5, color=theme['muted'])
        axis.annotate(f'{theirs:g} s, {their_quality:g}', (theirs, y - 0.19), xytext=(5, 0), textcoords='offset points', va='center', fontsize=7.5, color=theme['muted'])

    axis.set_yticks(range(len(rows)), [r[0] for r in reversed(rows)], color=theme['text'], fontsize=8.5)
    axis.set_xlim(0.05, 600)
    axis.set_xlabel('Time, log scale (labels show time and SSIMULACRA 2)', color=theme['muted'], fontsize=8.5)
    handles = [plt.Rectangle((0, 0), 1, 1, color=color(n)) for n in ('SharpBcn', 'BCnEncoder.NET')]
    axis.legend(handles, ['SharpBcn', 'BCnEncoder.NET'], frameon=False, labelcolor=theme['text'], fontsize=8.5, loc='lower right')
    save(figure, path)


def save(figure, path):
    for axis in figure.axes:
        axis.set_facecolor(THEME['background'])

    figure.tight_layout()
    figure.savefig(path, facecolor=THEME['background'])
    plt.close(figure)


def scatter_chart(path, fmt, theme):
    panels = DATA[fmt]
    figure, axes = plt.subplots(1, len(panels), figsize=(5.2 * len(panels), 4.2), squeeze=False)

    for axis, (title, points) in zip(axes[0], panels.items()):
        scatter(axis, fmt, title, points, theme, LIMITS.get((fmt, title)))

    save(figure, path)


def main():
    plt.rcParams['svg.fonttype'] = 'path'
    plt.rcParams['font.family'] = 'DejaVu Sans'
    folder = os.path.dirname(os.path.abspath(__file__))
    extension = sys.argv[1] if len(sys.argv) > 1 else 'svg'

    for fmt in ('BC1', 'BC4', 'BC7', 'BC6H'):
        scatter_chart(os.path.join(folder, f'{fmt.lower()}.{extension}'), fmt, THEME)

    speed_chart(os.path.join(folder, f'bcnencoder.{extension}'), THEME)


if __name__ == '__main__':
    main()
