using System;
using System.IO;
using System.Text;

namespace HZExporter
{
    /// <summary>
    /// Binary writer for hz-engine-v5 BlobAsset format.
    /// All BlobPtr/StringBlob offsets are SELF-RELATIVE (offset from the field's own address).
    /// Little-endian throughout.
    /// </summary>
    public class HZBlobWriter
    {
        MemoryStream _stream;
        BinaryWriter _writer;

        public int Position => (int)_stream.Position;

        public HZBlobWriter(int capacity = 4096)
        {
            _stream = new MemoryStream(capacity);
            _writer = new BinaryWriter(_stream);
        }

        // --- Primitives ---

        public void WriteU8(byte v) => _writer.Write(v);
        public void WriteI32(int v) => _writer.Write(v);
        public void WriteU32(uint v) => _writer.Write(v);
        public void WriteU64(ulong v) => _writer.Write(v);
        public void WriteF32(float v) => _writer.Write(v);

        public void WriteVec3(float x, float y, float z)
        {
            _writer.Write(x);
            _writer.Write(y);
            _writer.Write(z);
        }

        public void WriteVec4(float x, float y, float z, float w)
        {
            _writer.Write(x);
            _writer.Write(y);
            _writer.Write(z);
            _writer.Write(w);
        }

        public void WriteMat4Identity()
        {
            for (int col = 0; col < 4; col++)
                for (int row = 0; row < 4; row++)
                    _writer.Write(col == row ? 1f : 0f);
        }

        public void WriteBytes(byte[] data) => _writer.Write(data);

        public void WriteZeros(int count)
        {
            for (int i = 0; i < count; i++)
                _writer.Write((byte)0);
        }

        // --- Alignment ---

        public void AlignTo(int alignment)
        {
            int pos = (int)_stream.Position;
            int remainder = pos % alignment;
            if (remainder != 0)
                WriteZeros(alignment - remainder);
        }

        // --- Patching ---

        public void PatchU32(int position, uint value)
        {
            long saved = _stream.Position;
            _stream.Position = position;
            _writer.Write(value);
            _stream.Position = saved;
        }

        public void PatchU64(int position, ulong value)
        {
            long saved = _stream.Position;
            _stream.Position = position;
            _writer.Write(value);
            _stream.Position = saved;
        }

        public void PatchI32(int position, int value)
        {
            long saved = _stream.Position;
            _stream.Position = position;
            _writer.Write(value);
            _stream.Position = saved;
        }

        // --- BlobAssetHeader (48 bytes) ---

        public const ulong ASSET_MAGIC = 0x54455353414800UL;
        public const uint ASSET_VERSION = 3;

        /// <summary>
        /// Writes a BlobAssetHeader. Returns the position where it starts.
        /// asset_size must be patched later with PatchU64(pos + 8, totalSize).
        /// dependencies BlobPtr must be filled after writing dependency strings.
        /// </summary>
        public int WriteHeader(string assetTypeName)
        {
            int pos = Position;
            WriteU64(ASSET_MAGIC);                  // magic (8)
            WriteU64(0);                            // asset_size placeholder (8)
            WriteU64((ulong)FNV1a(assetTypeName));  // asset_type_hash (8)
            WriteU32(ASSET_VERSION);                // version (4)
            WriteU32(0);                            // _pad0 (4)
            // dependencies BlobPtr (16) - caller fills via WriteBlobPtr or WriteEmptyBlobPtr
            return pos;
        }

        // --- BlobPtr (16 bytes) ---

        /// <summary>
        /// Reserves space for a BlobPtr (16 bytes) and returns the position.
        /// Caller patches it later with PatchBlobPtr.
        /// </summary>
        public int ReserveBlobPtr()
        {
            int pos = Position;
            WriteZeros(16);
            return pos;
        }

        /// <summary>
        /// Writes a BlobPtr at the current position. Returns the position.
        /// Offset is self-relative: dataPos - thisFieldPos.
        /// </summary>
        public int WriteBlobPtr(int dataPos, int count, int typeSize, string typeName)
        {
            int pos = Position;
            _writer.Write((int)(dataPos - pos));               // offset (i32, self-relative)
            _writer.Write((uint)(count * typeSize));            // size
            _writer.Write((uint)typeSize);                      // type_size
            _writer.Write((uint)TypeHash(typeName, typeSize));  // typehash
            return pos;
        }

        /// <summary>
        /// Patches a previously reserved BlobPtr at blobPtrPos.
        /// Offset is self-relative: dataPos - blobPtrPos.
        /// </summary>
        public void PatchBlobPtr(int blobPtrPos, int dataPos, int count, int typeSize, string typeName)
        {
            long saved = _stream.Position;
            _stream.Position = blobPtrPos;
            _writer.Write((int)(dataPos - blobPtrPos));
            _writer.Write((uint)(count * typeSize));
            _writer.Write((uint)typeSize);
            _writer.Write((uint)TypeHash(typeName, typeSize));
            _stream.Position = saved;
        }

        /// <summary>
        /// Writes an empty BlobPtr (no data, but valid type_size and typehash).
        /// </summary>
        public int WriteEmptyBlobPtr(int typeSize, string typeName)
        {
            int pos = Position;
            _writer.Write((int)0);                         // offset (i32)
            _writer.Write((uint)0);                        // size
            _writer.Write((uint)typeSize);                 // type_size
            _writer.Write((uint)TypeHash(typeName, typeSize)); // typehash
            return pos;
        }

        // --- StringBlob (8 bytes) ---

        /// <summary>
        /// Reserves 8 bytes for a StringBlob. Returns the position.
        /// Patch with PatchStringBlob after writing the string data.
        /// </summary>
        public int ReserveStringBlob()
        {
            int pos = Position;
            WriteZeros(8);
            return pos;
        }

        /// <summary>
        /// Writes string data (null-terminated, 4-byte aligned) and patches
        /// the StringBlob at sbPos. Offset is self-relative (from sbPos).
        /// v5 layout: { i32 offset; u32 len; }
        /// </summary>
        public void WriteAndPatchStringBlob(int sbPos, string str)
        {
            int dataPos = Position;
            byte[] utf8 = Encoding.UTF8.GetBytes(str);
            _writer.Write(utf8);
            _writer.Write((byte)0); // null terminator
            int allocSize = utf8.Length + 1;
            int aligned = (allocSize + 3) & ~3;
            int pad = aligned - allocSize;
            if (pad > 0) WriteZeros(pad);

            // Patch the StringBlob: { i32 offset, u32 len }
            long saved = _stream.Position;
            _stream.Position = sbPos;
            _writer.Write((int)(dataPos - sbPos));   // offset (i32, self-relative)
            _writer.Write((uint)utf8.Length);         // len
            _stream.Position = saved;
        }

        // --- BlobAssetRef (24 bytes) ---
        // Layout: { StringBlob path(8); u32 type_size(4); u32 typehash(4); HzHandle handle(8); }

        /// <summary>
        /// Reserves 24 bytes for a BlobAssetRef. Returns the position.
        /// </summary>
        public int ReserveBlobAssetRef()
        {
            int pos = Position;
            WriteZeros(24);
            return pos;
        }

        /// <summary>
        /// Writes string data for the path and patches the BlobAssetRef at refPos.
        /// typeName/typeSize are for the referenced asset type.
        /// Handle is zeroed (filled at runtime by loader).
        /// </summary>
        public void WriteAndPatchBlobAssetRef(int refPos, string path, int typeSize, string typeName)
        {
            // Write path string data
            int dataPos = Position;
            byte[] utf8 = Encoding.UTF8.GetBytes(path);
            _writer.Write(utf8);
            _writer.Write((byte)0);
            int allocSize = utf8.Length + 1;
            int aligned = (allocSize + 3) & ~3;
            int pad = aligned - allocSize;
            if (pad > 0) WriteZeros(pad);

            // Patch: StringBlob path { i32 offset, u32 len }
            long saved = _stream.Position;
            _stream.Position = refPos;
            _writer.Write((int)(dataPos - refPos));   // path.offset (self-relative)
            _writer.Write((uint)utf8.Length);          // path.len
            _writer.Write((uint)typeSize);             // type_size
            _writer.Write((uint)TypeHash(typeName, typeSize)); // typehash
            _writer.Write((uint)0);                    // handle.idx (zero on disk)
            _writer.Write((uint)0);                    // handle.gen (zero on disk)
            _stream.Position = saved;
        }

        /// <summary>
        /// Patches a BlobAssetRef with an empty path (no reference).
        /// </summary>
        public void PatchEmptyBlobAssetRef(int refPos, int typeSize, string typeName)
        {
            long saved = _stream.Position;
            _stream.Position = refPos;
            _writer.Write((int)0);                     // path.offset
            _writer.Write((uint)0);                    // path.len
            _writer.Write((uint)typeSize);             // type_size
            _writer.Write((uint)TypeHash(typeName, typeSize)); // typehash
            _writer.Write((uint)0);                    // handle.idx
            _writer.Write((uint)0);                    // handle.gen
            _stream.Position = saved;
        }

        // --- Hashing ---

        public static uint FNV1a(string str)
        {
            uint hash = 0x811c9dc5;
            foreach (byte b in Encoding.UTF8.GetBytes(str))
            {
                hash ^= b;
                hash *= 0x01000193;
            }
            return hash;
        }

        /// <summary>
        /// TYPE_HASH(T) = fnv1a(name) ^ sizeof(T)
        /// Used for BlobPtr.typehash validation.
        /// </summary>
        public static uint TypeHash(string typeName, int size)
        {
            return FNV1a(typeName) ^ (uint)size;
        }

        /// <summary>
        /// ASSET_TYPE(T) = fnv1a(name) — NO xor with sizeof.
        /// Used for BlobAssetHeader.asset_type_hash.
        /// </summary>
        public static uint AssetType(string typeName)
        {
            return FNV1a(typeName);
        }

        // --- Output ---

        public byte[] ToArray()
        {
            _writer.Flush();
            return _stream.ToArray();
        }

        public void WriteToFile(string path)
        {
            _writer.Flush();
            File.WriteAllBytes(path, _stream.ToArray());
        }
    }
}
