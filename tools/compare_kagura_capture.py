"""Compare Unity vs engine KAGURA cloth captures (skirt + sode L/R, WAIT02).

Diagnostic split per cloth:
  BASE  = the skinned driver (animated proxy rest). If these diverge, the
          engine's base skinning / animation timing is wrong.
  SIM   = the solver output. If bases match but sims differ, it's the solver.
Particle orders differ, so everything is aggregate per frame.
"""
import json
import math
import sys


def load(path):
    d = json.load(open(path))
    cloths = d["cloths"]
    frames = []
    for fr in d["framesData"]:
        per = []
        for c in fr["c"]:
            pos = c["pos"]
            base = c["base"]
            n = len(pos) // 3
            per.append((
                [(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]) for i in range(n)],
                [(base[3 * i], base[3 * i + 1], base[3 * i + 2]) for i in range(n)],
            ))
        frames.append(per)
    return cloths, frames


def centroid(pts):
    n = len(pts)
    return (sum(p[0] for p in pts) / n, sum(p[1] for p in pts) / n,
            sum(p[2] for p in pts) / n)


def dist(a, b):
    return math.dist(a, b)


def aabb_size(pts):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    zs = [p[2] for p in pts]
    return (max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs))


def stats(name, cloths, frames, ci):
    rows = []
    for f, per in enumerate(frames):
        pos, base = per[ci]
        bc = centroid(base)
        pc = centroid(pos)
        md = max(dist(p, b) for p, b in zip(pos, base))
        ad = sum(dist(p, b) for p, b in zip(pos, base)) / len(pos)
        rows.append((f, bc, pc, md, ad))
    return rows


def main():
    cu, fu = load(sys.argv[1])
    ce, fe = load(sys.argv[2])

    # per-frame anchor alignment: the skirt base centroid tracks the hips, so
    # shifting unity data by (engine_anchor - unity_anchor) removes the scene
    # placement offset (and any root drift) from the comparison
    for f in range(min(len(fu), len(fe))):
        ua = centroid(fu[f][0][1])
        ea = centroid(fe[f][0][1])
        off = (ea[0] - ua[0], ea[1] - ua[1], ea[2] - ua[2])
        for c in range(3):
            pos, base = fu[f][c]
            fu[f][c] = (
                [(p[0] + off[0], p[1] + off[1], p[2] + off[2]) for p in pos],
                [(p[0] + off[0], p[1] + off[1], p[2] + off[2]) for p in base],
            )

    print("=== build ===")
    for i in range(3):
        u, e = cu[i], ce[i]
        print(f"  [{i}] {u['name']:24s} unity: n={u['particleCount']} fix={u['fixed']} "
              f"e={u['edges']} t={u['tris']}   engine: n={e['particleCount']} "
              f"fix={e['fixed']} e={e['edges']} t={e['tris']}")

    for i in range(3):
        ru = stats("U", cu, fu, i)
        re = stats("E", ce, fe, i)
        print(f"=== cloth {i} ({cu[i]['name']}) ===")
        # base agreement: centroid distance per frame (driver parity)
        bdist = [dist(u[1], e[1]) for u, e in zip(ru, re)]
        # base extent (size sanity)
        bu0 = aabb_size(fu[10][i][1])
        be0 = aabb_size(fe[10][i][1])
        print(f"  base aabb f10        unity=({bu0[0]:.3f},{bu0[1]:.3f},{bu0[2]:.3f}) "
              f"engine=({be0[0]:.3f},{be0[1]:.3f},{be0[2]:.3f})")
        print(f"  base centroid dist   mean={sum(bdist)/len(bdist):.4f} "
              f"max={max(bdist):.4f}")
        # sim deviation from own base (response magnitude)
        ssu = ru[120:]
        sse = re[120:]
        print(f"  maxDistFromBase mean unity={sum(r[3] for r in ssu)/len(ssu):.4f} "
              f"engine={sum(r[3] for r in sse)/len(sse):.4f}")
        print(f"  avgDistFromBase mean unity={sum(r[4] for r in ssu)/len(ssu):.4f} "
              f"engine={sum(r[4] for r in sse)/len(sse):.4f}")
        # sim centroid agreement
        sdist = [dist(u[2], e[2]) for u, e in zip(ru, re)]
        print(f"  sim centroid dist    mean={sum(sdist)/len(sdist):.4f} "
              f"max={max(sdist):.4f}")
        # trace
        print("  trace f: U(maxD avgD) E(maxD avgD) baseCdist simCdist")
        for f in range(0, len(ru), 60):
            u, e = ru[f], re[f]
            print(f"    f={f:3d} U({u[3]:.3f} {u[4]:.3f}) E({e[3]:.3f} {e[4]:.3f}) "
                  f"{dist(u[1], e[1]):.4f} {dist(u[2], e[2]):.4f}")


if __name__ == "__main__":
    main()
