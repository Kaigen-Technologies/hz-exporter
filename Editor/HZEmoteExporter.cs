using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace HZExporter
{
    /// <summary>
    /// Exports Unity Timelines (PlayableDirector + TimelineAsset) to engine EmoteBlobAsset .hzt,
    /// one per bound director, under {OutputDir}/timelines/. Handles anim + audio tracks; expression
    /// ("emotion") tracks and procedural overrides are intentionally emitted empty.
    ///
    /// Two-phase, driven from HZExportCommands.ExportScene:
    ///   1) CollectTimelineClips — feeds every bound AnimationTrack clip into the matching character's
    ///      GLB animation set, so the clips get baked + cooked to unity/glb/&lt;char&gt;/&lt;clip&gt;.hza.
    ///   2) Export — writes the .hzt timelines (runs AFTER the GLB export, paths are deterministic).
    /// </summary>
    public static class HZEmoteExporter
    {
        static string F(float v) => v.ToString("F6", CultureInfo.InvariantCulture);

        struct AnimEntry
        {
            public string path;        // engine anim asset path
            public float start, end, clipIn, speed, transition;
            public bool loop, hold;
        }
        struct AudioEntry { public string clip; public float start, volume; }

        // ---- Phase 1: add bound timeline anim clips to the GLB anim set ----
        public static void CollectTimelineClips(
            GameObject[] roots,
            System.Func<Animator, string> resolveChar,
            System.Action<string, AnimationClip> addClip)
        {
            foreach (var (dir, tl) in Directors(roots))
            {
                foreach (var track in tl.GetOutputTracks())
                {
                    if (!(track is AnimationTrack)) continue;
                    var anim = dir.GetGenericBinding(track) as Animator;
                    string charName = resolveChar(anim);
                    if (string.IsNullOrEmpty(charName)) continue;  // unbound → skip (don't resolve)
                    foreach (var clip in track.GetClips())
                    {
                        var ac = (clip.asset as AnimationPlayableAsset)?.clip;
                        if (ac != null) addClip(charName, ac);
                    }
                }
            }
        }

        // ---- Phase 2: write one EmoteBlobAsset .hzt per bound director ----
        public static void Export(
            GameObject[] roots,
            System.Func<Animator, string> resolveChar,
            string outputDir, string assetPrefix)
        {
            string dir = $"{outputDir}/timelines";
            int count = 0;
            foreach (var (director, tl) in Directors(roots))
            {
                string emoteName = tl.name;
                string fileStem = HZGlbWriter.SanitizeName(emoteName);

                var anims = new List<AnimEntry>();
                var audios = new List<AudioEntry>();
                int audioIdx = 0;

                foreach (var track in tl.GetOutputTracks())
                {
                    if (track is AnimationTrack)
                    {
                        var anim = director.GetGenericBinding(track) as Animator;
                        string charName = resolveChar(anim);
                        if (string.IsNullOrEmpty(charName)) continue;  // unbound → skip
                        foreach (var clip in track.GetClips())
                        {
                            var ac = (clip.asset as AnimationPlayableAsset)?.clip;
                            if (ac == null) continue;
                            float trans = (float)clip.blendInDuration;
                            if (trans <= 0f) trans = (float)clip.easeInDuration;
                            anims.Add(new AnimEntry
                            {
                                path = $"{assetPrefix}/glb/{charName}/{HZGlbWriter.SanitizeName(ac.name)}.hza",
                                start = (float)clip.start,
                                end = (float)clip.end,
                                clipIn = (float)clip.clipIn,
                                speed = (float)clip.timeScale,
                                transition = trans,
                                loop = clip.postExtrapolationMode == TimelineClip.ClipExtrapolation.Loop,
                                hold = clip.postExtrapolationMode != TimelineClip.ClipExtrapolation.None,
                            });
                        }
                    }
                    else if (track is AudioTrack)
                    {
                        foreach (var clip in track.GetClips())
                        {
                            var src = (clip.asset as AudioPlayableAsset)?.clip;
                            if (src == null) continue;
                            string audioName = $"{fileStem}_audio" + (audioIdx == 0 ? "" : audioIdx.ToString());
                            audioIdx++;
                            WriteAudioAsset(src, dir, audioName, assetPrefix);
                            audios.Add(new AudioEntry
                            {
                                clip = $"{assetPrefix}/timelines/{audioName}.hzt",
                                start = (float)clip.start,
                                volume = 1.0f,
                            });
                        }
                    }
                }

                anims.Sort((a, b) => a.start.CompareTo(b.start));
                audios.Sort((a, b) => a.start.CompareTo(b.start));

                string outPath = $"{dir}/{fileStem}.hzt";
                Directory.CreateDirectory(dir);
                File.WriteAllText(outPath, BuildEmoteHzt(emoteName, (float)tl.duration, anims, audios));
                Debug.Log($"[HZEmote] {emoteName}: {anims.Count} anim, {audios.Count} audio → {outPath}");
                count++;
            }
            if (count > 0) Debug.Log($"[HZEmote] exported {count} timeline(s)");
        }

        static string BuildEmoteHzt(string name, float duration, List<AnimEntry> anims, List<AudioEntry> audios)
        {
            var sb = new StringBuilder();
            sb.AppendLine("type EmoteBlobAsset");
            sb.AppendLine($"version {HZBlobWriter.ASSET_VERSION}");
            sb.AppendLine("---");
            sb.AppendLine($"name \"{name}\"");
            sb.AppendLine($"duration {F(duration)}");
            sb.AppendLine("suppress_blink false");
            sb.AppendLine("suppress_gaze false");

            sb.AppendLine($"anim_tracks {anims.Count}");
            foreach (var a in anims)
            {
                sb.AppendLine($"  animation \"{a.path}\"");
                sb.AppendLine($"  start_time {F(a.start)}");
                sb.AppendLine($"  end_time {F(a.end)}");
                sb.AppendLine($"  clip_in {F(a.clipIn)}");
                sb.AppendLine($"  speed {F(a.speed)}");
                sb.AppendLine($"  transition_duration {F(a.transition)}");
                sb.AppendLine($"  loop {(a.loop ? "true" : "false")}");
                sb.AppendLine($"  hold {(a.hold ? "true" : "false")}");
                sb.AppendLine("  layer 0");
                sb.AppendLine();
            }

            sb.AppendLine("expr_tracks 0");
            sb.AppendLine("overrides 0");

            sb.AppendLine($"audio_tracks {audios.Count}");
            foreach (var a in audios)
            {
                sb.AppendLine($"  clip \"{a.clip}\"");
                sb.AppendLine($"  start_time {F(a.start)}");
                sb.AppendLine($"  volume {F(a.volume)}");
            }
            return sb.ToString();
        }

        // Write the AudioClip as a 16-bit PCM .wav + an AudioClipBlobAsset .hzt (engine cooks wav→QOA).
        static void WriteAudioAsset(AudioClip src, string dir, string audioName, string assetPrefix)
        {
            Directory.CreateDirectory(dir);
            WriteWav(src, $"{dir}/{audioName}.wav");

            var sb = new StringBuilder();
            sb.AppendLine("type AudioClipBlobAsset");
            sb.AppendLine($"version {HZBlobWriter.ASSET_VERSION}");
            sb.AppendLine("---");
            sb.AppendLine($"audio \"/{assetPrefix}/timelines/{audioName}\" u8");
            sb.AppendLine("file_format AUDIO_FORMAT_QOA");
            sb.AppendLine("volume 1.000000");
            sb.AppendLine("loop false");
            File.WriteAllText($"{dir}/{audioName}.hzt", sb.ToString());
        }

        static void WriteWav(AudioClip clip, string path)
        {
            var samples = new float[clip.samples * clip.channels];
            if (!clip.GetData(samples, 0))
            {
                Debug.LogWarning($"[HZEmote] could not read AudioClip '{clip.name}' (set Load Type to Decompress On Load / Preload Audio Data) — wav skipped");
                return;
            }
            int ch = clip.channels, hz = clip.frequency;
            int dataLen = samples.Length * 2;
            using var fs = new FileStream(path, FileMode.Create);
            using var w = new BinaryWriter(fs);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataLen); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)ch);
            w.Write(hz); w.Write(hz * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataLen);
            foreach (var s in samples) w.Write((short)(Mathf.Clamp(s, -1f, 1f) * 32767f));
        }

        static IEnumerable<(PlayableDirector dir, TimelineAsset tl)> Directors(GameObject[] roots)
        {
            foreach (var root in roots)
                foreach (var d in root.GetComponentsInChildren<PlayableDirector>(true))
                    if (d.playableAsset is TimelineAsset tl)
                        yield return (d, tl);
        }
    }
}
