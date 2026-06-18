"""
Dump and validate an AnimationBlobAsset .hza file (hz-engine-v5 format).
Usage: python dump_animation_hza.py <path_to.hza>

v5 layout:
  AnimationBlobAsset (72): header(48) + length(4) + joint_count(4) + keyframes BlobPtr(16)
  KeyframeBlobAsset (24):  timestamp(4) + transform_count(4) + joint_transforms BlobPtr(16)
  JointTransformBlob (44): joint_index(4) + translation(12) + rotation(16) + scale(12)
"""
import sys
from hza import HzaReader

SIZEOF_ANIMATION = 72
SIZEOF_KEYFRAME = 24
SIZEOF_JOINT_TRANSFORM = 44


def dump_animation(path: str):
    r = HzaReader(path)
    r.validate_header("AnimationBlobAsset")
    r.dump_header()

    # === AnimationBlobAsset (72 bytes) ===
    off = 48  # after header
    length = r.read_f32(off); off += 4
    joint_count = r.read_u32(off); off += 4
    keyframes_bp = r.read_blob_ptr(off); off += 16

    r.check(off == SIZEOF_ANIMATION,
            f"AnimationBlobAsset size: expected {SIZEOF_ANIMATION}, got {off}")

    keyframe_count = keyframes_bp.count

    print(f"\n=== AnimationBlobAsset ({SIZEOF_ANIMATION} bytes) ===")
    print(f"  length:          {length:.4f}s")
    print(f"  joint_count:     {joint_count}")
    print(f"  keyframe_count:  {keyframe_count} (from BlobArray)")
    print(f"  keyframes:       {keyframes_bp}")

    r.check_blob_ptr(keyframes_bp, "KeyframeBlobAsset", SIZEOF_KEYFRAME, "keyframes")

    # === KeyframeBlobAsset(s) ===
    total_transforms = 0
    for ki in range(keyframe_count):
        kf_off = keyframes_bp.data_pos + ki * SIZEOF_KEYFRAME

        timestamp = r.read_f32(kf_off)
        transform_count = r.read_u32(kf_off + 4)
        transforms_bp = r.read_blob_ptr(kf_off + 8)

        r.check_blob_ptr(transforms_bp, "JointTransformBlob", SIZEOF_JOINT_TRANSFORM,
                         f"keyframe[{ki}].joint_transforms")
        r.check(transforms_bp.count == transform_count,
                f"keyframe[{ki}]: BlobPtr count {transforms_bp.count} != transform_count {transform_count}")

        # Validate joint indices are in range
        if not transforms_bp.empty:
            for ti in range(transform_count):
                t_off = transforms_bp.data_pos + ti * SIZEOF_JOINT_TRANSFORM
                joint_idx = r.read_u32(t_off)
                r.check(joint_idx < joint_count,
                        f"keyframe[{ki}].transform[{ti}].joint_index={joint_idx} "
                        f"out of range [0,{joint_count})")

        total_transforms += transform_count

        # Print first few and last keyframe
        if ki < 3 or ki >= keyframe_count - 1:
            print(f"  [{ki:4d}] t={timestamp:.4f}s  transforms={transform_count}")

            # Sample first transform
            if not transforms_bp.empty:
                t_off = transforms_bp.data_pos
                ji = r.read_u32(t_off)
                tx, ty, tz = r.read_vec3(t_off + 4)
                rx, ry, rz, rw = r.read_vec4(t_off + 16)
                sx, sy, sz = r.read_vec3(t_off + 32)
                print(f"         joint[{ji}] pos=({tx:.4f},{ty:.4f},{tz:.4f}) "
                      f"rot=({rx:.4f},{ry:.4f},{rz:.4f},{rw:.4f}) "
                      f"scl=({sx:.4f},{sy:.4f},{sz:.4f})")
        elif ki == 3:
            print(f"  ... ({keyframe_count - 4} more keyframes) ...")

    print(f"\n  Total joint transforms: {total_transforms}")

    r.print_result()
    sys.exit(0 if r.ok else 1)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(f"Usage: python {sys.argv[0]} <file.hza>")
        sys.exit(1)
    dump_animation(sys.argv[1])
