using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HZExporter
{
    /// <summary>
    /// Launches and stops the hz-engine (`./hzbuild <platform> run`) from the configured
    /// engine project path. The launched PID is stashed in SessionState so a domain reload
    /// (script recompile) doesn't orphan the process — it can still be stopped afterwards.
    /// </summary>
    [InitializeOnLoad]
    public static class HZEnginePlayer
    {
        const string PidKey = "HZExporter.EnginePid";

        static HZEnginePlayer()
        {
            // Don't leave the engine running when the editor quits.
            EditorApplication.quitting += Stop;
        }

        public static bool IsRunning => TryGetProcess(out _);

        static bool TryGetProcess(out Process proc)
        {
            proc = null;
            int pid = SessionState.GetInt(PidKey, 0);
            if (pid == 0) return false;
            try
            {
                var p = Process.GetProcessById(pid);
                if (p.HasExited) { SessionState.EraseInt(PidKey); return false; }
                proc = p;
                return true;
            }
            catch
            {
                SessionState.EraseInt(PidKey);
                return false;
            }
        }

        /// <summary>Start the engine. No-op (with a warning) if already running or unconfigured.</summary>
        public static void Play()
        {
            if (IsRunning)
            {
                Debug.LogWarning("[HZEnginePlayer] engine already running; stop it first.");
                return;
            }

            var cfg = HZExporterConfig.Instance;
            string root = cfg.engineProjectPath;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                Debug.LogWarning("[HZEnginePlayer] engine project path not set or missing (Hz Exporter/Configure).");
                return;
            }

            if (!TryGetLaunchCommand(root, out string exe, out string args))
            {
                Debug.LogError($"[HZEnginePlayer] hzbuild not found in {root}.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log($"[engine] {e.Data}"); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.LogError($"[engine] {e.Data}"); };

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                SessionState.SetInt(PidKey, proc.Id);
                Debug.Log($"[HZEnginePlayer] launched: {exe} {args} (pid {proc.Id})");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[HZEnginePlayer] failed to launch engine: {ex.Message}");
            }
        }

        /// <summary>Stop the engine (kills the whole process tree). Safe to call when not running.</summary>
        public static void Stop()
        {
            int pid = SessionState.GetInt(PidKey, 0);
            if (pid == 0) return;
            try
            {
                KillTree(pid);
                Debug.Log("[HZEnginePlayer] engine stopped.");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[HZEnginePlayer] stop failed: {ex.Message}");
            }
            finally
            {
                SessionState.EraseInt(PidKey);
            }
        }

        // Kill the process tree. Mono here lacks Process.Kill(bool), and the engine spawns the
        // game as a child of hzbuild, so killing only the parent would orphan it.
        static void KillTree(int pid)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                RunQuiet("taskkill", $"/PID {pid} /T /F");
                return;
            }
            // Unix: depth-first kill children (via pgrep -P) then the process itself.
            foreach (var child in ChildPids(pid))
                KillTree(child);
            RunQuiet("/bin/kill", $"-9 {pid}");
        }

        static int[] ChildPids(int pid)
        {
            string output = RunQuiet("/usr/bin/pgrep", $"-P {pid}");
            if (string.IsNullOrEmpty(output)) return System.Array.Empty<int>();
            var lines = output.Split('\n');
            var list = new System.Collections.Generic.List<int>();
            foreach (var line in lines)
                if (int.TryParse(line.Trim(), out int c)) list.Add(c);
            return list.ToArray();
        }

        static string RunQuiet(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                return outp;
            }
            catch
            {
                return string.Empty;
            }
        }

        static bool TryGetLaunchCommand(string root, out string exe, out string args)
        {
            exe = null;
            args = null;
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    exe = Path.Combine(root, "hzbuild");
                    args = "macos run";
                    break;
                case RuntimePlatform.LinuxEditor:
                    exe = Path.Combine(root, "hzbuild");
                    args = "linux run";
                    break;
                case RuntimePlatform.WindowsEditor:
                    exe = Path.Combine(root, "hzbuild.exe");
                    if (!File.Exists(exe)) exe = Path.Combine(root, "hzbuild");
                    args = "windows run";
                    break;
                default:
                    return false;
            }
            return File.Exists(exe);
        }
    }
}
