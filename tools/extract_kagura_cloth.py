"""Extract MeshCloth settings + colliders from the KAGURA MC2 prefab."""
import re
import sys

PREFAB = r"Assets\MagicaCloth2\Example (Can be deleted)\UnityChan\UnityChanKAGURA\UnityCHanKAGURA_MC2.prefab"

text = open(PREFAB, encoding="utf-8").read()
lines = text.splitlines()

# split into yaml documents keyed by anchor id
docs = {}
cur_id, cur = None, []
for ln in lines:
    m = re.match(r"--- !u!\d+ &(\d+)", ln)
    if m:
        if cur_id is not None:
            docs[cur_id] = cur
        cur_id, cur = m.group(1), []
    elif cur_id is not None:
        cur.append(ln)
docs[cur_id] = cur


def get(block, key, default=None):
    for ln in block:
        s = ln.strip()
        if s.startswith(key + ":"):
            return s.split(":", 1)[1].strip()
    return default


def section(block, name, max_lines=200):
    out, on, indent = [], False, None
    for ln in block:
        s = ln.strip()
        if s.startswith(name + ":"):
            on = True
            indent = len(ln) - len(ln.lstrip())
            continue
        if on:
            ci = len(ln) - len(ln.lstrip())
            if s and ci <= indent:
                break
            out.append(ln)
            if len(out) > max_lines:
                break
    return out


def curve_summary(sec):
    val = get(sec, "value")
    use = get(sec, "useCurve")
    pts = [ln.strip().split(":")[1].strip() for ln in sec if ln.strip().startswith("value:")]
    return f"value={val} useCurve={use} curvePts={pts[1:3] if len(pts) > 2 else pts}"


def gameobject_name(comp_block):
    go = get(comp_block, "m_GameObject", "")
    m = re.search(r"fileID: (\d+)", go or "")
    if m and m.group(1) in docs:
        return get(docs[m.group(1)], "m_Name")
    return "?"


def dump_cloth(cid):
    b = docs[cid]
    print("=" * 70)
    print("CLOTH", cid, gameobject_name(b))
    sd = section(b, "serializeData", 600)
    print(" clothType:", get(sd, "clothType"))
    print(" sourceRenderers:", [ln.strip() for ln in section(sd, "sourceRenderers", 5)])
    red = section(sd, "reductionSetting")
    print(" reduction: simple=", get(red, "simpleDistance"), "shape=", get(red, "shapeDistance"))
    print(" gravity:", get(sd, "gravity"), " blendWeight:", get(sd, "blendWeight"),
          " stablization:", get(sd, "stablizationTimeAfterReset"))
    print(" damping:", curve_summary(section(sd, "damping")))
    print(" radius:", curve_summary(section(sd, "radius")))
    inertia = section(sd, "inertiaConstraint")
    for k in ["worldInertia", "movementInertiaSmoothing", "localInertia", "depthInertia",
              "centrifualAcceleration", "teleportMode"]:
        print(f"  inertia.{k}:", get(inertia, k))
    for lim in ["movementSpeedLimit", "rotationSpeedLimit", "particleSpeedLimit"]:
        s = section(inertia, lim)
        print(f"  inertia.{lim}: value={get(s,'value')} use={get(s,'use')}")
    tether = section(sd, "tetherConstraint")
    print(" tether.distanceCompression:", get(tether, "distanceCompression"))
    dist = section(sd, "distanceConstraint", 60)
    print(" distance.stiffness:", curve_summary(section(dist, "stiffness", 50)))
    tb = section(sd, "triangleBendingConstraint")
    print(" triBending.stiffness:", get(tb, "stiffness"))
    ar = section(sd, "angleRestorationConstraint", 60)
    print(" angleRestoration: use=", get(ar, "useAngleRestoration"),
          curve_summary(section(ar, "stiffness", 50)),
          "velAttn=", get(ar, "velocityAttenuation"), "gravityFalloff=", get(ar, "gravityFalloff"))
    al = section(sd, "angleLimitConstraint", 60)
    print(" angleLimit: use=", get(al, "useAngleLimit"),
          curve_summary(section(al, "limitAngle", 50)), "stiffness=", get(al, "stiffness"))
    mo = section(sd, "motionConstraint", 90)
    print(" motion: useMaxDistance=", get(mo, "useMaxDistance"),
          "maxDist=", curve_summary(section(mo, "maxDistance", 50)),
          "useBackstop=", get(mo, "useBackstop"), "backstopRadius=", get(mo, "backstopRadius"),
          "backstopDist=", curve_summary(section(mo, "backstopDistance", 50)),
          "stiffness=", get(mo, "stiffness"))
    cc = section(sd, "colliderCollisionConstraint", 80)
    print(" collision: mode=", get(cc, "mode"), "friction=", get(cc, "friction"),
          "limitDistance=", curve_summary(section(cc, "limitDistance", 50)))
    cols = [re.search(r"fileID: (\d+)", ln).group(1)
            for ln in section(cc, "colliderList", 10) if "fileID" in ln]
    print(" colliderList:", cols)
    sc = section(sd, "selfCollisionConstraint", 80)
    print(" selfCollision: mode=", get(sc, "selfMode"),
          "thickness=", curve_summary(section(sc, "surfaceThickness", 50)),
          "clothMass=", get(sc, "clothMass"))
    wind = section(sd, "wind")
    print(" wind.influence:", get(wind, "influence"))
    sd2 = section(b, "serializeData2", 40)
    sel = section(b, "selectionData", 10)
    print(" selectionData present:", "positions" in "".join(sd2 + sel) or None)
    return cols


def dump_collider(cid):
    if cid not in docs:
        print("collider", cid, "NOT FOUND")
        return
    b = docs[cid]
    script = get(b, "m_Script", "")
    print("-" * 50)
    print("COLLIDER", cid, "on GO:", gameobject_name(b), "script:", script)
    for k in ["mode", "radiusSeparation", "alignedOnCenter", "reverseDirection"]:
        v = get(b, k)
        if v is not None:
            print("  ", k, "=", v)
    for sec_name in ["size", "center"]:
        s = section(b, sec_name, 6)
        flat = get(b, sec_name)
        if flat:
            print("  ", sec_name, "=", flat)
        elif s:
            print("  ", sec_name, "=", [ln.strip() for ln in s])


all_cols = []
for cloth_id in sys.argv[1:]:
    all_cols += dump_cloth(cloth_id)
print()
for c in dict.fromkeys(all_cols):
    dump_collider(c)
