"""Quick steady-state looseness metric per cloth from an engine/unity capture."""
import json
import math
import sys

d = json.load(open(sys.argv[1]))
names = [c["name"] for c in d["cloths"]]
frames = d["framesData"]
for c in range(len(names)):
    avgs, maxs = [], []
    for fr in frames[120:]:
        pos = fr["c"][c]["pos"]
        base = fr["c"][c]["base"]
        n = len(pos) // 3
        ds = [math.dist(pos[3*i:3*i+3], base[3*i:3*i+3]) for i in range(n)]
        avgs.append(sum(ds) / n)
        maxs.append(max(ds))
    print(f"{names[c]:24s} avgD={sum(avgs)/len(avgs):.4f} maxD={sum(maxs)/len(maxs):.4f}")
