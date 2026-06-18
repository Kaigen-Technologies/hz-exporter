"""Validate a ClothAuthoringBlobAsset .hza (layout mirror of cloth_authoring_blob.h).

Usage: python tools/dump_cloth_authoring_hza.py <file.hza>
"""
import struct
import sys

ASSET_MAGIC = 0x54455353414800
HDR = 48
PARAMS_OFF = 60
PARAMS_SIZE = 820


def fnv1a(s):
    h = 0x811C9DC5
    for b in s.encode():
        h ^= b
        h = (h * 0x01000193) & 0xFFFFFFFF
    return h


def read_blobptr(d, off):
    o, size, tsize, thash = struct.unpack_from("<iIII", d, off)
    return dict(off=off + o if size else 0, size=size, type_size=tsize, typehash=thash)


def read_string(d, off):
    o, ln = struct.unpack_from("<iI", d, off)
    if ln == 0:
        return ""
    return d[off + o:off + o + ln].decode()


def f32(d, off):
    return struct.unpack_from("<f", d, off)[0]


def u32(d, off):
    return struct.unpack_from("<I", d, off)[0]


def curve(d, off):
    return list(struct.unpack_from("<16f", d, off))


PARAM_FIELDS = [
    ("gravity_dir", "v3"), ("gravity", "f"), ("gravity_falloff", "f"),
    ("damping", "f"), ("use_damping_curve", "b"), ("damping_curve", "c"),
    ("stabilization_time", "f"), ("animation_pose_ratio", "f"),
    ("distance_stiffness", "f"), ("use_distance_curve", "b"),
    ("distance_stiffness_curve", "c"), ("distance_iterations", "i"),
    ("distance_velocity_attenuation", "f"), ("tether_compression", "f"),
    ("use_angle_restoration", "b"), ("angle_restoration_stiffness", "f"),
    ("use_angle_restoration_curve", "b"), ("angle_restoration_curve", "c"),
    ("angle_restoration_velocity_attenuation", "f"),
    ("angle_restoration_gravity_falloff", "f"),
    ("use_angle_limit", "b"), ("angle_limit_deg", "f"),
    ("angle_limit_stiffness", "f"), ("use_angle_limit_curve", "b"),
    ("angle_limit_curve", "c"),
    ("use_max_distance", "b"), ("max_distance", "f"),
    ("use_max_distance_curve", "b"), ("max_distance_curve", "c"),
    ("motion_stiffness", "f"), ("use_backstop", "b"), ("backstop_radius", "f"),
    ("backstop_distance", "f"), ("use_backstop_distance_curve", "b"),
    ("backstop_distance_curve", "c"), ("normal_axis", "u"),
    ("is_spring", "b"), ("spring_power", "f"), ("spring_limit_distance", "f"),
    ("spring_normal_limit_ratio", "f"), ("spring_noise", "f"),
    ("radius", "f"), ("use_radius_curve", "b"), ("radius_curve", "c"),
    ("use_collision", "b"), ("collision_friction", "f"),
    ("use_self_collision", "b"), ("self_collision_thickness", "f"),
    ("use_self_thickness_curve", "b"), ("self_thickness_curve", "c"),
    ("self_collision_cloth_mass", "f"),
    ("world_inertia", "f"), ("depth_inertia", "f"),
    ("movement_inertia_smoothing", "f"), ("movement_speed_limit", "f"),
    ("rotation_speed_limit", "f"), ("local_inertia", "f"),
    ("local_movement_speed_limit", "f"), ("local_rotation_speed_limit", "f"),
    ("particle_speed_limit", "f"), ("centrifugal_acceleration", "f"),
    ("teleport_mode", "u"), ("teleport_distance", "f"),
    ("wind_mode", "u"), ("wind_dir", "v3"), ("wind_main", "f"),
    ("wind_influence", "f"), ("wind_turbulence", "f"),
    ("wind_depth_weight", "f"), ("wind_frequency", "f"), ("wind_blend", "f"),
    ("wind_synchronization", "f"), ("wind_zone_center", "v3"),
    ("wind_zone_radius", "f"),
    ("use_bending", "b"), ("bending_stiffness", "f"), ("blend_weight", "f"),
    ("rotational_interpolation", "f"), ("root_rotation", "f"),
]


def main():
    path = sys.argv[1]
    d = open(path, "rb").read()
    magic, size, thash = struct.unpack_from("<QQQ", d, 0)
    version = u32(d, 24)
    assert magic == ASSET_MAGIC, f"bad magic {magic:#x}"
    assert thash == fnv1a("ClothAuthoringBlobAsset"), "bad type hash"
    assert size == len(d), f"asset_size {size} != file size {len(d)}"
    print(f"OK header: version={version} size={size}")

    cloth_type = u32(d, 48)
    mesh_name = read_string(d, 52)
    print(f"cloth_type={cloth_type} target_mesh='{mesh_name}'")

    off = PARAMS_OFF
    params = {}
    for name, kind in PARAM_FIELDS:
        if kind == "v3":
            params[name] = struct.unpack_from("<3f", d, off)
            off += 12
        elif kind == "c":
            params[name] = curve(d, off)
            off += 64
        elif kind == "u" or kind == "b" or kind == "i":
            params[name] = u32(d, off)
            off += 4
        else:
            params[name] = f32(d, off)
            off += 4
    assert off == PARAMS_OFF + PARAMS_SIZE, f"params end {off} != {PARAMS_OFF + PARAMS_SIZE}"

    for name, kind in PARAM_FIELDS:
        v = params[name]
        if kind == "c":
            print(f"  {name}: [{v[0]:.4f} .. {v[15]:.4f}]")
        elif kind == "v3":
            print(f"  {name}: ({v[0]:.3f}, {v[1]:.3f}, {v[2]:.3f})")
        else:
            print(f"  {name}: {v if kind != 'f' else round(v, 5)}")

    red = struct.unpack_from("<2f", d, 880)
    maxconn = f32(d, 888)
    na_mode = u32(d, 892)
    na_pos = struct.unpack_from("<3f", d, 896)
    tvc = u32(d, 908)
    print(f"reduction: simple={red[0]:.4f} shape={red[1]:.4f}")
    print(f"selection maxConnectionDistance={maxconn:.5f} target_vertex_count={tvc}")
    print(f"normal_alignment mode={na_mode} pos=({na_pos[0]:.3f}, {na_pos[1]:.3f}, {na_pos[2]:.3f})")

    sp = read_blobptr(d, 912)
    sa = read_blobptr(d, 928)
    co = read_blobptr(d, 944)
    nsel = sp["size"] // 12 if sp["size"] else 0
    nattr = sa["size"] // 1 if sa["size"] else 0
    ncol = co["size"] // 60 if co["size"] else 0
    assert nsel == nattr, f"sel pos/attr mismatch {nsel}/{nattr}"
    print(f"selection points: {nsel}")
    counts = [0, 0, 0]
    for i in range(nattr):
        a = d[sa["off"] + i]
        counts[a if a < 3 else 0] += 1
    print(f"  invalid={counts[0]} fixed={counts[1]} move={counts[2]}")
    if nsel:
        xs = [f32(d, sp["off"] + i * 12) for i in range(nsel)]
        ys = [f32(d, sp["off"] + i * 12 + 4) for i in range(nsel)]
        zs = [f32(d, sp["off"] + i * 12 + 8) for i in range(nsel)]
        print(f"  bounds x[{min(xs):.3f}..{max(xs):.3f}] y[{min(ys):.3f}..{max(ys):.3f}] z[{min(zs):.3f}..{max(zs):.3f}]")

    print(f"colliders: {ncol}")
    for i in range(ncol):
        base = co["off"] + i * 60
        ctype = u32(d, base)
        bone = read_string(d, base + 4)
        lp = struct.unpack_from("<3f", d, base + 12)
        lr = struct.unpack_from("<4f", d, base + 24)
        cdir = u32(d, base + 40)
        csize = struct.unpack_from("<3f", d, base + 44)
        aligned = u32(d, base + 56)
        tname = {1: "sphere", 2: "capsule", 3: "plane"}.get(ctype, "?")
        print(f"  [{i}] {tname} bone='{bone}' pos=({lp[0]:.3f},{lp[1]:.3f},{lp[2]:.3f}) "
              f"rot=({lr[0]:.3f},{lr[1]:.3f},{lr[2]:.3f},{lr[3]:.3f}) dir={cdir} "
              f"size=({csize[0]:.3f},{csize[1]:.3f},{csize[2]:.3f}) aligned={aligned}")

    # BoneCloth/BoneSpring: bone_names (StringBlob[8] each) + bone_attributes (u8)
    bn = read_blobptr(d, 960)
    ba = read_blobptr(d, 976)
    nbone = bn["size"] // 8 if bn["size"] else 0
    nbattr = ba["size"] // 1 if ba["size"] else 0
    print(f"bones: {nbone}")
    if nbone:
        names = [read_string(d, bn["off"] + i * 8) for i in range(nbone)]
        bcounts = [0, 0, 0]
        for i in range(nbattr):
            a = d[ba["off"] + i]
            bcounts[a if a < 3 else 0] += 1
        print(f"  fixed={bcounts[1]} move={bcounts[2]} invalid={bcounts[0]}")
        print(f"  first: {', '.join(names[:6])}{' ...' if nbone > 6 else ''}")


if __name__ == "__main__":
    main()
