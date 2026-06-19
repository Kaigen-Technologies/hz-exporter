using System.IO;
using System.Text;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Exports a Unity Texture2D as a TextureBlobAsset (.hzt text or .hza binary) + PNG.
    /// Texture compression (KTX) is done by the engine, not here.
    /// </summary>
    public static class HZTextureExporter
    {
        const int SIZEOF_TEXTURE_BLOB_ASSET = 88;
        const int SIZEOF_STRING_BLOB = 8;

        // TextureFormat enum (must match engine)
        const uint TEX_FORMAT_BC1 = 1;
        const uint TEX_FORMAT_BC3 = 2;
        const uint TEX_FORMAT_BC5 = 3;

        /// <summary>
        /// Export a texture as TextureBlobAsset (.hza) + PNG.
        /// </summary>
        /// <param name="texture">Source texture</param>
        /// <param name="imageBasePath">Asset-relative base path for the image (e.g. "/unity/albedo") — no extension</param>
        /// <param name="isSrgb">sRGB for albedo/emission, linear for normal/metallic</param>
        /// <param name="hasAlpha">Whether the texture has meaningful alpha</param>
        /// <param name="outputHzaPath">Output .hza file path</param>
        /// <param name="outputPngPath">Output PNG file path</param>
        public static void Export(Texture2D texture, string imageBasePath, bool isSrgb,
            bool hasAlpha, string outputHzaPath, string outputPngPath)
        {
            // Export the PNG
            Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath));
            var readable = MakeReadable(texture);
            byte[] pngData = readable.EncodeToPNG();
            File.WriteAllBytes(outputPngPath, pngData);
            if (readable != texture)
                Object.DestroyImmediate(readable);

            uint format = hasAlpha ? TEX_FORMAT_BC3 : TEX_FORMAT_BC1;

            // Write TextureBlobAsset
            var w = new HZBlobWriter(1024);
            int blobBase = w.Position;

            w.WriteHeader("TextureBlobAsset");
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob"); // no header deps

            int imageRefPos = w.ReserveBlobAssetRef(); // image

            w.WriteU32(format);  // format
            w.WriteU32(0);       // width (engine gets from KTX)
            w.WriteU32(0);       // height (engine gets from KTX)
            w.WriteU32(isSrgb ? 1u : 0u); // is_srgb

            Debug.Assert(w.Position == SIZEOF_TEXTURE_BLOB_ASSET,
                $"TextureBlobAsset size mismatch: expected {SIZEOF_TEXTURE_BLOB_ASSET}, got {w.Position}");

            // image.path = base path (engine appends _bc1.ktx etc.)
            w.WriteAndPatchBlobAssetRef(imageRefPos, imageBasePath, 1, "u8");

            int finalSize = w.Position;
            w.PatchU64(blobBase + 8, (ulong)finalSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputHzaPath));
            w.WriteToFile(outputHzaPath);
            Debug.Log($"[HZTextureExporter] Exported → {outputHzaPath} ({finalSize} bytes, format={(format == TEX_FORMAT_BC1 ? "BC1" : "BC3")}, srgb={isSrgb})");
        }

        /// <summary>Export as .hzt text file + PNG.</summary>
        public static void ExportHzt(Texture2D texture, string imageBasePath, bool isSrgb,
            bool hasAlpha, string outputHztPath, string outputPngPath)
        {
            // Export PNG
            Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath));
            var readable = MakeReadable(texture);
            byte[] pngData = readable.EncodeToPNG();
            File.WriteAllBytes(outputPngPath, pngData);
            if (readable != texture)
                Object.DestroyImmediate(readable);

            string formatName = hasAlpha ? "TEX_FORMAT_BC3" : "TEX_FORMAT_BC1";

            // Write .hzt
            var sb = new StringBuilder();
            sb.AppendLine("type TextureBlobAsset");
            sb.AppendLine($"version {HZBlobWriter.ASSET_VERSION}");
            sb.AppendLine("---");
            sb.AppendLine($"image \"{imageBasePath}\" u8");
            sb.AppendLine($"format {formatName}");
            sb.AppendLine("width 0");
            sb.AppendLine("height 0");
            sb.AppendLine($"is_srgb {(isSrgb ? "true" : "false")}");
            sb.AppendLine($"has_alpha {(hasAlpha ? "true" : "false")}");
            sb.AppendLine("has_alpha_cutoff false");

            Directory.CreateDirectory(Path.GetDirectoryName(outputHztPath));
            File.WriteAllText(outputHztPath, sb.ToString());
            Debug.Log($"[HZTextureExporter] Exported → {outputHztPath} (format={formatName}, srgb={isSrgb})");
        }

        static Texture2D MakeReadable(Texture2D src)
        {
            if (src.isReadable) return src;

            RenderTexture tmp = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, tmp);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = tmp;
            Texture2D readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            return readable;
        }
    }
}
