"""
Compare Blender-exported vs Unity-exported model and animation data.
Usage: python compare_exports.py
"""
import struct
import sys
from hza import HzaReader

JOINT_BLOB_SIZE = 96
JOINT_TRANSFORM_SIZE = 48
KEYFRAME_SIZE = 24


def read_skeleton(r: HzaReader):
    """Read joint names, parent indices, and bind poses from a ModelBlobAsset."""
    off = 48
    mesh_count = r.read_u32(off); off += 4
    joint_count = r.read_u32(off); off += 4
    off += 8  # padding
    armature = r.read_mat4(off); off += 64
    meshes_bp = r.read_blob_ptr(off); off += 16
    joints_bp = r.read_blob_ptr(off); off += 16
    jnames_bp = r.read_blob_ptr(off); off += 16

    joints = []
    for i in range(joint_count):
        j_off = joints_bp.offset + i * JOINT_BLOB_SIZE
        ibm = r.read_mat4(j_off)
        parent = r.read_i32(j_off + 64)
        joints.append({"ibm": ibm, "parent": parent})

    names = []
    for i in range(joint_count):
        sb_off = jnames_bp.offset + i * 8
        name = r.read_string_blob(sb_off, sb_off)
        names.append(name)

    return {"joint_count": joint_count, "joints": joints, "names": names, "armature": armature}


def read_animation(r: HzaReader):
    """Read keyframes and transforms from an AnimationBlobAsset."""
    off = 48
    length = r.read_f32(off); off += 4
    joint_count = r.read_u32(off); off += 4
    keyframe_count = r.read_u32(off); off += 4
    keyframes_bp = r.read_blob_ptr(off); off += 16

    keyframes = []
    for ki in range(keyframe_count):
        kf_off = keyframes_bp.offset + ki * KEYFRAME_SIZE
        kf_base = kf_off
        timestamp = r.read_f32(kf_off); kf_off += 4
        transform_count = r.read_u32(kf_off); kf_off += 4
        transforms_bp = r.read_blob_ptr(kf_off)

        transforms = []
        if not transforms_bp.empty:
            t_abs = kf_base + transforms_bp.offset
            for ti in range(transform_count):
                t_off = t_abs + ti * JOINT_TRANSFORM_SIZE
                ji = r.read_u32(t_off)
                tx, ty, tz = r.read_vec3(t_off + 4)
                rx, ry, rz, rw = r.read_vec4(t_off + 16)
                sx, sy, sz = r.read_vec3(t_off + 32)
                transforms.append({
                    "joint_index": ji,
                    "pos": (tx, ty, tz),
                    "rot": (rx, ry, rz, rw),
                    "scale": (sx, sy, sz),
                })

        keyframes.append({"timestamp": timestamp, "transforms": transforms})

    return {"length": length, "joint_count": joint_count,
            "keyframe_count": keyframe_count, "keyframes": keyframes}


def fmt_vec3(v):
    return f"({v[0]:9.5f}, {v[1]:9.5f}, {v[2]:9.5f})"

def fmt_vec4(v):
    return f"({v[0]:9.5f}, {v[1]:9.5f}, {v[2]:9.5f}, {v[3]:9.5f})"

def fmt_mat4(m):
    # column-major: m[col*4+row]
    rows = []
    for row in range(4):
        vals = [m[col * 4 + row] for col in range(4)]
        rows.append(f"  [{' '.join(f'{v:9.5f}' for v in vals)}]")
    return "\n".join(rows)


def compare_skeletons(blender_model_path, unity_model_path):
    rb = HzaReader(blender_model_path)
    ru = HzaReader(unity_model_path)
    sb = read_skeleton(rb)
    su = read_skeleton(ru)

    print(f"\n{'='*60}")
    print(f"SKELETON COMPARISON")
    print(f"  Blender: {blender_model_path} ({sb['joint_count']} joints)")
    print(f"  Unity:   {unity_model_path} ({su['joint_count']} joints)")

    print(f"\n--- Armature Transform ---")
    print(f"  Blender:\n{fmt_mat4(sb['armature'])}")
    print(f"  Unity:\n{fmt_mat4(su['armature'])}")

    # Match by name
    blender_by_name = {sb['names'][i]: i for i in range(sb['joint_count'])}
    unity_by_name = {su['names'][i]: i for i in range(su['joint_count'])}

    all_names = list(blender_by_name.keys())
    unity_only = [n for n in unity_by_name if n not in blender_by_name]
    blender_only = [n for n in blender_by_name if n not in unity_by_name]

    if unity_only:
        print(f"\n  Unity-only joints: {unity_only}")
    if blender_only:
        print(f"\n  Blender-only joints: {blender_only}")

    print(f"\n--- Joint Index Mapping (first 10) ---")
    print(f"  {'Name':<35} {'Blender':>8} {'Unity':>8} {'Match':>6}")
    mismatches = 0
    for name in all_names[:10]:
        bi = blender_by_name.get(name, -1)
        ui = unity_by_name.get(name, -1)
        match = "YES" if bi == ui else "NO"
        if bi != ui:
            mismatches += 1
        print(f"  {name:<35} {bi:>8} {ui:>8} {match:>6}")
    if mismatches > 0 or len(all_names) > 10:
        total_mismatches = sum(1 for n in all_names if blender_by_name.get(n, -1) != unity_by_name.get(n, -1))
        print(f"  ... total index mismatches: {total_mismatches}/{len(all_names)}")

    # Compare bind poses for first 3 matching joints
    print(f"\n--- Bind Poses (first 3 joints) ---")
    for name in all_names[:3]:
        bi = blender_by_name[name]
        ui = unity_by_name.get(name)
        if ui is None:
            continue
        print(f"\n  Joint '{name}' (blender idx={bi}, unity idx={ui}):")
        print(f"  Blender IBM:\n{fmt_mat4(sb['joints'][bi]['ibm'])}")
        print(f"  Unity IBM:\n{fmt_mat4(su['joints'][ui]['ibm'])}")


def compare_animations(blender_anim_path, unity_anim_path,
                        blender_model_path, unity_model_path):
    ra_b = HzaReader(blender_anim_path)
    ra_u = HzaReader(unity_anim_path)
    ab = read_animation(ra_b)
    au = read_animation(ra_u)

    # Get joint names for both models
    rb = HzaReader(blender_model_path)
    ru = HzaReader(unity_model_path)
    sb = read_skeleton(rb)
    su = read_skeleton(ru)

    print(f"\n{'='*60}")
    print(f"ANIMATION COMPARISON")
    print(f"  Blender: {blender_anim_path}")
    print(f"    length={ab['length']:.4f}s  joints={ab['joint_count']}  keyframes={ab['keyframe_count']}")
    print(f"  Unity:   {unity_anim_path}")
    print(f"    length={au['length']:.4f}s  joints={au['joint_count']}  keyframes={au['keyframe_count']}")

    # Compare first keyframe, all transforms, matched by joint name
    print(f"\n--- Keyframe 0 Transform Comparison ---")

    bkf = ab['keyframes'][0]
    ukf = au['keyframes'][0]

    # Build name → transform maps
    b_by_name = {}
    for t in bkf['transforms']:
        ji = t['joint_index']
        if ji < len(sb['names']):
            b_by_name[sb['names'][ji]] = t

    u_by_name = {}
    for t in ukf['transforms']:
        ji = t['joint_index']
        if ji < len(su['names']):
            u_by_name[su['names'][ji]] = t

    # Compare
    print(f"\n  {'Joint':<35} {'Field':>5}  {'Blender':>45}  {'Unity':>45}")
    print(f"  {'-'*35} {'-'*5}  {'-'*45}  {'-'*45}")

    for name in list(sb['names'])[:10]:
        bt = b_by_name.get(name)
        ut = u_by_name.get(name)
        if bt is None or ut is None:
            print(f"  {name:<35}  MISSING in {'Unity' if ut is None else 'Blender'}")
            continue

        print(f"  {name:<35} {'pos':>5}  {fmt_vec3(bt['pos']):>45}  {fmt_vec3(ut['pos']):>45}")
        print(f"  {'':35} {'rot':>5}  {fmt_vec4(bt['rot']):>45}  {fmt_vec4(ut['rot']):>45}")
        print(f"  {'':35} {'scl':>5}  {fmt_vec3(bt['scale']):>45}  {fmt_vec3(ut['scale']):>45}")


if __name__ == "__main__":
    blender_model = "D:/dev/gabrieldechichi/hz-engine-v3/assets/xbot/xbot_body_block.hza"
    unity_model = "D:/dev/unity/UnityToHZ/ExportedAssets/meshes/X_Bot_Beta_Surface.hza"
    blender_anim = "D:/dev/gabrieldechichi/hz-engine-v3/assets/xbot/xbot_body_block_anim.hza"
    unity_anim = "D:/dev/unity/UnityToHZ/ExportedAssets/animations/Body_Block.hza"

    compare_skeletons(blender_model, unity_model)
    compare_animations(blender_anim, unity_anim, blender_model, unity_model)
