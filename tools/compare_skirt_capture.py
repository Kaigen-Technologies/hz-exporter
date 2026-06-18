"""Compare Unity MC2 vs engine skirt captures (same sway drive, same settings).

Particle order/count differ slightly between the two proxies, so the comparison
uses aggregate per-frame metrics: pinned-band mean, hem-band mean, centroid,
sway amplitude/phase transfer, droop, and oscillation energy.
"""
import json
import math
import sys


def load(path):
    with open(path) as f:
        d = json.load(f)
    n = d["particleCount"]
    rest = d["restPositions"]
    rest = [(rest[3 * i], rest[3 * i + 1], rest[3 * i + 2]) for i in range(n)]
    frames = []
    for fr in d["frames"]:
        pos = fr["pos"]
        pts = [(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]) for i in range(n)]
        frames.append((fr["swayX"], pts))
    attrs = d["attributes"]
    return d, rest, frames, attrs


def bands(rest, attrs):
    ys = [p[1] for p in rest]
    ymin, ymax = min(ys), max(ys)
    ext = ymax - ymin
    fixed = [i for i, a in enumerate(attrs) if a == 1]
    hem = [i for i, p in enumerate(rest) if p[1] < ymin + 0.15 * ext]
    return fixed, hem, ymin, ymax


def mean_of(pts, idx, axis):
    return sum(pts[i][axis] for i in idx) / len(idx)


def metrics(name, d, rest, frames, attrs):
    fixed, hem, ymin, ymax = bands(rest, attrs)
    print(f"--- {name}")
    print(f"  particles={d['particleCount']} fixed={len(fixed)} hem={len(hem)} "
          f"restY=[{ymin:.4f}..{ymax:.4f}] extent={ymax - ymin:.4f}")

    rest_hem_x = mean_of(rest, hem, 0)
    rest_hem_y = mean_of(rest, hem, 1)
    rest_fix_x = mean_of(rest, fixed, 0)

    rows = []
    for f, (sway, pts) in enumerate(frames):
        hx = mean_of(pts, hem, 0) - rest_hem_x
        hy = mean_of(pts, hem, 1) - rest_hem_y
        fx = mean_of(pts, fixed, 0) - rest_fix_x
        # max single-particle displacement from rest (deformation magnitude)
        md = max(math.dist(pts[i], rest[i]) for i in range(len(rest)))
        rows.append((f, sway, fx, hx, hy, md))
    return rows


def summarize(name, rows, t0=120):
    """steady-state stats over rows[t0:] (skip 2s of settle)."""
    ss = rows[t0:]
    sway_amp = max(abs(r[1]) for r in ss)
    fix_amp = max(abs(r[2]) for r in ss)
    hem_amp = max(abs(r[3]) for r in ss)
    hem_y_mean = sum(r[4] for r in ss) / len(ss)
    hem_y_min = min(r[4] for r in ss)
    hem_y_max = max(r[4] for r in ss)
    md_mean = sum(r[5] for r in ss) / len(ss)
    md_max = max(r[5] for r in ss)

    # phase: cross-correlate hem x vs sway over steady state
    best_lag, best_corr = 0, -1e30
    sway_series = [r[1] for r in ss]
    hem_series = [r[3] for r in ss]
    m = len(ss)
    for lag in range(0, 50):
        c = sum(sway_series[i - lag] * hem_series[i] for i in range(lag, m))
        if c > best_corr:
            best_corr, best_lag = c, lag
    print(f"  [{name}] steady-state (frames {t0}+):")
    print(f"    drive amp           = {sway_amp:.4f}")
    print(f"    pinned-band x amp   = {fix_amp:.4f}  (how much the anchor actually moves)")
    print(f"    hem x amp           = {hem_amp:.4f}  (gain vs drive: {hem_amp / sway_amp:.2f})")
    print(f"    hem lag             = {best_lag} frames ({best_lag / 60.0 * 1000:.0f} ms)")
    print(f"    hem y mean/min/max  = {hem_y_mean:+.4f} / {hem_y_min:+.4f} / {hem_y_max:+.4f} (vs rest)")
    print(f"    maxdisp mean/max    = {md_mean:.4f} / {md_max:.4f}")
    return dict(sway=sway_amp, fix=fix_amp, hem=hem_amp, lag=best_lag,
                hemy=hem_y_mean, md=md_mean)


def main():
    unity_path, engine_path = sys.argv[1], sys.argv[2]
    du, ru, fu, au = load(unity_path)
    de, re_, fe, ae = load(engine_path)

    rows_u = metrics("UNITY", du, ru, fu, au)
    rows_e = metrics("ENGINE", de, re_, fe, ae)
    su = summarize("UNITY", rows_u)
    se = summarize("ENGINE", rows_e)

    print("--- VERDICT")
    for k, label in [("fix", "anchor x amplitude"), ("hem", "hem x amplitude"),
                     ("lag", "hem lag frames"), ("hemy", "hem droop"),
                     ("md", "mean max-displacement")]:
        print(f"  {label:24s} unity={su[k]:+.4f}  engine={se[k]:+.4f}")

    # per-frame table every 30 frames
    print("--- trace f, sway, fixX, hemX, hemY, maxDisp")
    for f in range(0, len(rows_u), 30):
        u, e = rows_u[f], rows_e[f]
        print(f"  f={f:3d} U: sw={u[1]:+.3f} fx={u[2]:+.3f} hx={u[3]:+.3f} hy={u[4]:+.3f} md={u[5]:.3f}"
              f"   E: sw={e[1]:+.3f} fx={e[2]:+.3f} hx={e[3]:+.3f} hy={e[4]:+.3f} md={e[5]:.3f}")


if __name__ == "__main__":
    main()
