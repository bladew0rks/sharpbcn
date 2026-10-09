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
            ('SharpBcn perceptual', 0.83, 83.68),
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
            ('SharpBcn perceptual', 0.58, 83.99),
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
            ('SharpBcn perceptual', 2.93, 90.15),
            ('bc7e 4', 5.02, 89.93),
            ('bc7e 4 perceptual', 4.23, 90.14),
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
            ('SharpBcn perceptual', 2.67, 91.85),
            ('bc7e 4', 3.38, 91.49),
            ('bc7e 4 perceptual', 2.85, 91.99),
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
    ('BC7', 'Textures', 'SharpBcn perceptual'): (0, 10, 'center'),
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
        main = name.startswith('SharpBcn')
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


COMPARISONS = [
    ('BC1', 'Textures', 'BC1, textures'),
    ('BC1', 'CLIC', 'BC1, CLIC'),
    ('BC4', 'Textures', 'BC4, textures'),
    ('BC7', 'Textures', 'BC7, textures'),
    ('BC7', 'CLIC', 'BC7, CLIC'),
    ('BC6H', 'Unsigned', 'BC6H'),
    ('BC6H', 'Signed', 'BC6H signed'),
]


def comparison_chart(path, theme):
    figure, axes = plt.subplots(2, 4, figsize=(11, 5.4))

    for axis, (fmt, panel, title) in zip(axes.flat, COMPARISONS):
        points = DATA[fmt][panel]
        ours = next(p for p in points if p[0] == 'SharpBcn')
        theirs = sorted((p for p in points if p[0].startswith('BCnEncoder.NET')), key=lambda p: p[1])
        times = [ours[1]] + [p[1] for p in theirs]
        qualities = [ours[2]] + [p[2] for p in theirs]
        style(axis, theme, wide=True)
        axis.xaxis.set_major_formatter(FuncFormatter(seconds))
        axis.plot([p[1] for p in theirs], [p[2] for p in theirs], color=color('BCnEncoder.NET'), linewidth=1, zorder=2)

        for k, (name, time, quality) in enumerate(theirs):
            crowded = k > 0 and time / theirs[k - 1][1] < 1.3 and quality < theirs[k - 1][2]
            axis.scatter([time], [quality], s=36, color=color('BCnEncoder.NET'), zorder=3)
            axis.annotate(name.split()[-1], (time, quality), xytext=(0, -13 if crowded else 7), textcoords='offset points', ha='center', fontsize=7.5,
                          color=theme['muted'])

        axis.scatter([ours[1]], [ours[2]], s=80, color=color('SharpBcn'), edgecolors=theme['text'], linewidths=0.8, zorder=4)
        axis.annotate('SharpBcn', (ours[1], ours[2]), xytext=(0, 8), textcoords='offset points', ha='center', fontsize=8, fontweight='bold', color=theme['text'])
        spread = max(qualities) - min(qualities)
        axis.set_xlim(min(times) / 3, max(times) * 3)
        axis.set_ylim(min(qualities) - spread * 0.25 - 0.1, max(qualities) + spread * 0.35 + 0.2)
        axis.set_title(title, color=theme['text'], fontsize=10, loc='left')

    legend = axes.flat[-1]
    legend.axis('off')
    legend.scatter([0.1], [0.72], s=80, color=color('SharpBcn'), edgecolors=theme['text'], linewidths=0.8)
    legend.text(0.2, 0.72, 'SharpBcn', va='center', fontsize=9, color=theme['text'])
    legend.plot([0.04, 0.16], [0.52, 0.52], color=color('BCnEncoder.NET'), linewidth=1)
    legend.scatter([0.1], [0.52], s=36, color=color('BCnEncoder.NET'))
    legend.text(0.2, 0.52, 'BCnEncoder.NET levels', va='center', fontsize=9, color=theme['text'])
    legend.text(0.04, 0.28, 'Across: time, log scale\nUp: SSIMULACRA 2, higher is better', va='center', fontsize=8.5, color=theme['muted'])
    legend.set_xlim(0, 1)
    legend.set_ylim(0, 1)
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

    comparison_chart(os.path.join(folder, f'bcnencoder.{extension}'), THEME)


if __name__ == '__main__':
    main()
