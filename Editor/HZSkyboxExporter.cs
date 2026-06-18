using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace HZExporter
{
    /// <summary>
    /// Renders the active Unity skybox to an equirectangular Radiance HDR (.hdr).
    /// The engine's environment_bake_cook reads the .hdr and produces the cubemap KTX +
    /// HdrEnvironmentBlobAsset on its own — Unity only needs to drop the .hdr.
    ///
    /// Convention matches env_sample_equirect in environment_bake.c:
    ///   u = atan2(dir.z, dir.x) / (2*pi) + 0.5
    ///   v_flipped = asin(dir.y) / pi + 0.5; stored v = 1 - v_flipped (top = +Y)
    /// X is negated (engine RH <-> Unity LH) so the engine's render matches Unity's view.
    /// </summary>
    public static class HZSkyboxExporter
    {
        const string SHADER_NAME = "Hidden/HZ/CubemapToEquirect";

        /// <param name="outputPath">Absolute .hdr path.</param>
        /// <param name="faceSize">Cubemap face size (Unity-side render). 1024 is plenty.</param>
        /// <param name="eqWidth">Equirect width. Height = width/2. Engine cooks at face_size 256, so 2048 is sufficient.</param>
        public static bool Export(string outputPath, int faceSize = 1024, int eqWidth = 2048)
        {
            int eqHeight = eqWidth / 2;

            var shader = Shader.Find(SHADER_NAME);
            if (shader == null)
            {
                Debug.LogError($"[HZSkyboxExporter] Shader '{SHADER_NAME}' not found. Check Assets/Editor/HZExporter/CubemapToEquirect.shader is imported.");
                return false;
            }

            GameObject camGO = null;
            RenderTexture cubeRT = null;
            RenderTexture eqRT = null;
            Texture2D eqTex = null;
            Material mat = null;
            try
            {
                camGO = new GameObject("__hz_skybox_cam") { hideFlags = HideFlags.HideAndDontSave };
                var cam = camGO.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.cullingMask = 0;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 100f;
                cam.allowHDR = true;
                cam.enabled = false;

                cubeRT = new RenderTexture(faceSize, faceSize, 0, RenderTextureFormat.ARGBFloat)
                {
                    dimension = TextureDimension.Cube,
                    useMipMap = false,
                    autoGenerateMips = false,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                cubeRT.Create();

                if (!cam.RenderToCubemap(cubeRT))
                {
                    Debug.LogError("[HZSkyboxExporter] Camera.RenderToCubemap failed");
                    return false;
                }

                eqRT = new RenderTexture(eqWidth, eqHeight, 0, RenderTextureFormat.ARGBFloat)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                eqRT.Create();

                mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                mat.SetTexture("_Cubemap", cubeRT);
                Graphics.Blit(null, eqRT, mat);

                eqTex = new Texture2D(eqWidth, eqHeight, TextureFormat.RGBAFloat, false, true);
                var prevActive = RenderTexture.active;
                RenderTexture.active = eqRT;
                eqTex.ReadPixels(new Rect(0, 0, eqWidth, eqHeight), 0, 0, false);
                RenderTexture.active = prevActive;

                Color[] pixels = eqTex.GetPixels();

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                WriteRadianceHDR(outputPath, pixels, eqWidth, eqHeight);

                Debug.Log($"[HZSkyboxExporter] Exported → {outputPath} ({eqWidth}x{eqHeight}, source skybox: {(RenderSettings.skybox != null ? RenderSettings.skybox.name : "<procedural>")})");
                return true;
            }
            finally
            {
                if (eqTex != null) UnityEngine.Object.DestroyImmediate(eqTex);
                if (mat != null) UnityEngine.Object.DestroyImmediate(mat);
                if (eqRT != null) { eqRT.Release(); UnityEngine.Object.DestroyImmediate(eqRT); }
                if (cubeRT != null) { cubeRT.Release(); UnityEngine.Object.DestroyImmediate(cubeRT); }
                if (camGO != null) UnityEngine.Object.DestroyImmediate(camGO);
            }
        }

        // ---- Radiance HDR (RGBE) ----
        // Texture2D.GetPixels returns row-major bottom-up: pixels[y*w + x] with y=0 at bottom.
        // We write the HDR with `-Y h +X w` (top-down), so iterate y from h-1 down to 0.
        static void WriteRadianceHDR(string path, Color[] pixels, int w, int h)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                string header = "#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y " + h + " +X " + w + "\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                fs.Write(headerBytes, 0, headerBytes.Length);

                byte[] scanline = new byte[w * 4];
                for (int y = h - 1; y >= 0; y--)
                {
                    int rowStart = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        var c = pixels[rowStart + x];
                        EncodeRGBE(c.r, c.g, c.b, scanline, x * 4);
                    }
                    fs.Write(scanline, 0, scanline.Length);
                }
            }
        }

        // RGBE encoding: (r,g,b) = (R+0.5, G+0.5, B+0.5) / 256 * 2^(E - 128).
        // Find E so the largest channel falls in [128, 256).
        static void EncodeRGBE(float r, float g, float b, byte[] dst, int offset)
        {
            // negative HDR values aren't representable; clamp to 0
            if (r < 0) r = 0;
            if (g < 0) g = 0;
            if (b < 0) b = 0;
            if (float.IsNaN(r) || float.IsInfinity(r)) r = 0;
            if (float.IsNaN(g) || float.IsInfinity(g)) g = 0;
            if (float.IsNaN(b) || float.IsInfinity(b)) b = 0;

            float v = r > g ? r : g;
            if (b > v) v = b;

            if (v < 1e-32f)
            {
                dst[offset + 0] = 0;
                dst[offset + 1] = 0;
                dst[offset + 2] = 0;
                dst[offset + 3] = 0;
                return;
            }

            // frexp: v = m * 2^e, m in [0.5, 1.0). Use IEEE-754 bits for an exact answer.
            int bits = BitConverter.SingleToInt32Bits(v);
            int rawExp = ((bits >> 23) & 0xff) - 127; // unbiased
            int e = rawExp + 1;                       // because we shift mantissa to [0.5, 1.0)
            // scale = 256 / 2^e, applied to each channel.
            float scale = 256f * (float)Math.Pow(2.0, -e);

            int rb = (int)(r * scale); if (rb < 0) rb = 0; if (rb > 255) rb = 255;
            int gb = (int)(g * scale); if (gb < 0) gb = 0; if (gb > 255) gb = 255;
            int bb = (int)(b * scale); if (bb < 0) bb = 0; if (bb > 255) bb = 255;

            dst[offset + 0] = (byte)rb;
            dst[offset + 1] = (byte)gb;
            dst[offset + 2] = (byte)bb;
            dst[offset + 3] = (byte)(e + 128);
        }
    }
}
