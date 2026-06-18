using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

/// <summary>
/// Listens on TCP port 7322 for commands from external processes.
/// Supported commands:
///   "refresh"  — CompilationPipeline.RequestScriptCompilation (forces compile)
///   "log"      — returns last compilation result
///   "ping"     — returns "pong"
///   "exec:Type.Method" — runs a static method on the main thread
/// </summary>
[InitializeOnLoad]
public static class RemoteRefresh
{
    const int Port = 7322;
    static TcpListener s_listener;
    static Thread s_thread;
    static volatile bool s_running;
    static volatile bool s_refreshRequested;
    static volatile string s_execMethod;

    // Mirror of EditorApplication.isCompiling, updated on the main thread
    // and read from the TCP listener thread.
    static volatile bool s_isCompiling;
    // Set true when a refresh was requested; cleared after a compile has
    // started+finished OR a grace window elapses with no compile (no changes).
    static volatile bool s_compilePending;
    static int s_pendingGraceTicks;

    // In-memory compile result. After domain reload, the static constructor
    // runs again and resets this to "ok" (meaning compile succeeded).
    static volatile string s_compileResult = "ok";

    static RemoteRefresh()
    {
        // Domain reload happened — if we get here, compilation succeeded.
        s_compileResult = "ok";

        // CRITICAL: skip batchmode processes. Asset Import Workers (spawned
        // with -batchMode) also run [InitializeOnLoad], and SO_REUSEADDR lets
        // them share the port — so TCP commands would be round-robin'd between
        // the main editor and the workers, silently dropping most of them.
        if (Application.isBatchMode) return;

        Application.runInBackground = true;
        Start();
        EditorApplication.update += OnUpdate;
        CompilationPipeline.assemblyCompilationFinished += OnCompilationFinished;
    }

    static void Start()
    {
        if (s_running) return;
        s_running = true;
        s_thread = new Thread(ListenLoop) { IsBackground = true, Name = "RemoteRefresh" };
        s_thread.Start();
        Debug.Log($"[RemoteRefresh] Listening on port {Port}");
    }

    static void ListenLoop()
    {
        try
        {
            s_listener = new TcpListener(IPAddress.Loopback, Port);
            // Intentionally NO SO_REUSEADDR: asset import workers share this
            // assembly and would otherwise also bind the port, causing TCP
            // commands to be round-robin'd between editor and workers.
            s_listener.Start();

            while (s_running)
            {
                if (!s_listener.Pending())
                {
                    Thread.Sleep(50);
                    continue;
                }

                using var client = s_listener.AcceptTcpClient();
                client.ReceiveTimeout = 2000;
                using var stream = client.GetStream();

                var buf = new byte[256];
                int n = stream.Read(buf, 0, buf.Length);
                string rawCmd = Encoding.UTF8.GetString(buf, 0, n).Trim();
                string cmd = rawCmd.ToLowerInvariant();

                string response;
                if (cmd == "refresh")
                {
                    s_refreshRequested = true;
                    s_compilePending = true;
                    response = "refresh_queued";
                }
                else if (cmd == "log")
                {
                    // Report "compiling" until the refresh has been drained
                    // AND any actual compile has completed. This avoids the
                    // old bug where the handler flipped to "ok" before the
                    // async compile had a chance to fail.
                    response = (s_compilePending || s_isCompiling) ? "compiling" : s_compileResult;
                }
                else if (cmd == "ping")
                {
                    response = "pong";
                }
                else if (cmd.StartsWith("exec:"))
                {
                    s_execMethod = rawCmd.Substring(5);
                    response = $"exec_queued:{s_execMethod}";
                }
                else
                {
                    response = $"unknown_command:{cmd}";
                }

                byte[] resp = Encoding.UTF8.GetBytes(response);
                stream.Write(resp, 0, resp.Length);
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Debug.LogWarning($"[RemoteRefresh] Port {Port} already in use, skipping.");
        }
        catch (Exception ex)
        {
            if (s_running) Debug.LogError($"[RemoteRefresh] {ex.Message}");
        }
    }

    static void OnUpdate()
    {
        if (s_refreshRequested)
        {
            s_refreshRequested = false;
            s_pendingGraceTicks = 0;
            Debug.Log("[RemoteRefresh] Triggering script compilation...");
            // AssetDatabase.Refresh won't recompile if mtimes/content look
            // unchanged; RequestScriptCompilation forces a real compile.
            // Domain reload resets s_compileResult to "ok" on success; an
            // errored compile is captured in assemblyCompilationFinished.
            AssetDatabase.Refresh(ImportAssetOptions.Default);
            CompilationPipeline.RequestScriptCompilation();
        }

        // Mirror isCompiling so the TCP thread can read it safely.
        bool nowCompiling = EditorApplication.isCompiling;
        s_isCompiling = nowCompiling;

        // Clear s_compilePending once compile has actually started+finished.
        // If no compile is ever seen (nothing to compile), clear after a short
        // grace window so `log` doesn't hang on "compiling" forever.
        if (s_compilePending)
        {
            if (nowCompiling)
            {
                // Compile is running — stay pending; the tick count is only
                // meaningful while isCompiling is false.
                s_pendingGraceTicks = 0;
            }
            else if (++s_pendingGraceTicks > 30)
            {
                // ~30 idle ticks with no compile — treat as no-op refresh.
                s_compilePending = false;
            }
        }

        if (s_execMethod != null)
        {
            string method = s_execMethod;
            s_execMethod = null;
            try
            {
                int dot = method.LastIndexOf('.');
                if (dot < 0) { Debug.LogError($"[RemoteRefresh] Invalid method: {method}"); return; }
                string typeName = method.Substring(0, dot);
                string methodName = method.Substring(dot + 1);

                System.Type type = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType(typeName);
                    if (type != null) break;
                }
                if (type == null) { Debug.LogError($"[RemoteRefresh] Type not found: {typeName}"); return; }

                var mi = type.GetMethod(methodName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (mi == null) { Debug.LogError($"[RemoteRefresh] Method not found: {method}"); return; }

                Debug.Log($"[RemoteRefresh] Executing {method}...");
                mi.Invoke(null, null);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[RemoteRefresh] exec failed: {ex}");
            }
        }
    }

    static void OnCompilationFinished(string assemblyPath, CompilerMessage[] messages)
    {
        int errors = 0;
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            if (msg.type == CompilerMessageType.Error)
            {
                errors++;
                sb.AppendLine(msg.message);
            }
        }
        if (errors > 0)
            s_compileResult = $"ERRORS({errors}):\n{sb}";
        // "ok" is set by the static constructor after domain reload
    }
}
