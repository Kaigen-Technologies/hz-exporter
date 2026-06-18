"""
Dump and validate a MaterialBlobAsset .hza file (hz-engine-v5 format).
Usage: python dump_material_hza.py <path_to.hza>
"""
import sys
from hza import HzaReader

SIZEOF_MATERIAL = 112
SIZEOF_MATERIAL_PROPERTY = 36


def dump_material(path: str):
    r = HzaReader(path)
    r.validate_header("MaterialBlobAsset")
    r.dump_header()

    off = 48  # after header

    # material_shader BlobAssetRef (24 bytes)
    shader_path, shader_ts, shader_th = r.read_blob_asset_ref(off); off += 24

    depth_test = r.read_u32(off); off += 4
    depth_write = r.read_u32(off); off += 4
    depth_compare = r.read_u32(off); off += 4
    cull_mode = r.read_u32(off); off += 4
    is_transparent = r.read_u32(off); off += 4
    alpha_cutoff = r.read_u32(off); off += 4

    props_bp = r.read_blob_ptr(off); off += 16

    r.check(off == SIZEOF_MATERIAL, f"MaterialBlobAsset size: expected {SIZEOF_MATERIAL}, got {off}")

    print(f"\n=== MaterialBlobAsset ({SIZEOF_MATERIAL} bytes) ===")
    print(f"  material_shader: \"{shader_path}\"")
    print(f"  depth_test:      {depth_test}")
    print(f"  depth_write:     {depth_write}")
    print(f"  depth_compare:   {depth_compare}")
    print(f"  cull_mode:       {cull_mode}")
    print(f"  is_transparent:  {is_transparent}")
    print(f"  alpha_cutoff:    {alpha_cutoff}")
    print(f"  properties:      {props_bp}")
    r.check_blob_ptr(props_bp, "MaterialPropertyBlob", SIZEOF_MATERIAL_PROPERTY, "properties")

    # MaterialPropertyBlob array
    prop_count = props_bp.count
    MAT_PROP_NAMES = {0: "FLOAT", 1: "VEC2", 2: "VEC3", 3: "VEC4", 4: "MAT4",
                      5: "TEXTURE", 6: "COLOR3", 7: "COLOR4", 8: "RENDER_TARGET"}

    for pi in range(prop_count):
        p_off = props_bp.data_pos + pi * SIZEOF_MATERIAL_PROPERTY

        name = r.read_string_blob(p_off)
        prop_type = r.read_u32(p_off + 8)
        type_name = MAT_PROP_NAMES.get(prop_type, f"UNKNOWN({prop_type})")

        print(f"\n  --- Property[{pi}] ---")
        print(f"    name: \"{name}\"")
        print(f"    type: {type_name} ({prop_type})")

        # Union at offset 12 within the property (24 bytes)
        union_off = p_off + 12
        if prop_type in (3, 7):  # VEC4 or COLOR4
            x, y, z, w = r.read_vec4(union_off)
            print(f"    value: ({x:.4f}, {y:.4f}, {z:.4f}, {w:.4f})")
        elif prop_type == 5:  # TEXTURE
            tex_path, tex_ts, tex_th = r.read_blob_asset_ref(union_off)
            print(f"    texture: \"{tex_path}\" (type_size={tex_ts}, typehash=0x{tex_th:08x})")
        elif prop_type == 0:  # FLOAT
            f = r.read_f32(union_off)
            print(f"    value: {f:.4f}")
        elif prop_type == 2:  # VEC3
            x, y, z = r.read_vec3(union_off)
            print(f"    value: ({x:.4f}, {y:.4f}, {z:.4f})")

    r.print_result()
    sys.exit(0 if r.ok else 1)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(f"Usage: python {sys.argv[0]} <file.hza>")
        sys.exit(1)
    dump_material(sys.argv[1])
