# UnityToHZ Project

Unity Editor tool that exports Unity scenes to hz-engine-v5 binary asset format (.hza/.hzt).

## CLI Tools

Everything can be done from the command line. **Do not ask the user to click anything in Unity.**

### Remote Unity control (TCP port 7321)

A TCP listener (`RemoteRefresh.cs`, port 7321) runs inside the Unity Editor main process (asset-import worker processes skip it via `Application.isBatchMode`). It accepts raw TCP commands.

**macOS — always prefer the wrapper scripts for `refresh`/`exec:*`.** They handle the required focus dance automatically: on macOS the Editor's main-thread update loop only ticks while Unity is frontmost, so `refresh` and `exec:*` commands need Unity briefly activated (~1s) to drain. The scripts force Unity frontmost, send the command, wait for the tick, and restore whichever app had focus before — so there's no lingering focus steal.

```bash
./tools/unity_cmd.sh ping                                    # Health check → "pong"
./tools/unity_cmd.sh log                                     # Compile result ("ok" / "ERRORS(...)" / "compiling")
./tools/unity_cmd.sh "exec:ClassName.MethodName"             # Run static no-arg method
./tools/unity_refresh.sh [timeout-seconds]                   # Trigger compile, block until ok/ERRORS/timeout (default 90s)
```

Raw `printf '<cmd>' | nc -w 2 localhost 7321` still works for `ping` and `log` (no main-thread tick needed). For `refresh` or `exec:*` via raw `nc`, you must separately force Unity frontmost or the command just sits queued.

**Windows (PowerShell):**
```powershell
$CMD = "powershell -ExecutionPolicy Bypass -File 'tools/unity_cmd.ps1'"
& $CMD -cmd ping
& $CMD -cmd refresh
& $CMD -cmd log
& $CMD -cmd "exec:ClassName.MethodName"
```

The `exec:` command is the primary way to **run exports, tests, and any Unity logic** from CLI. For parameterized operations, create static wrapper methods with hardcoded args.

### macOS one-time setup

```bash
defaults write com.unity3d.UnityEditor5.x NSAppSleepDisabled -bool YES   # disable App Nap for Unity
```

### Troubleshooting

Commands silently succeed (`exec_queued:...`) but method never runs → check there is exactly one listener:
```bash
lsof -nP -iTCP:7321 -sTCP:LISTEN                # should list one Unity PID, not several
```
If you see multiple Unity processes bound to 7321, an asset-import worker has started its own listener — verify `Application.isBatchMode` check in `RemoteRefresh` is intact and `SO_REUSEADDR` is NOT being set.

### Available export commands

```bash
./tools/unity_cmd.sh "exec:HZExportCommands.ExportScene"          # Export active scene
./tools/unity_cmd.sh "exec:HZExportCommands.OpenSampleScene"      # Switch to SampleScene
./tools/unity_cmd.sh "exec:HZExportCommands.OpenAssetsShowcase"   # Switch to AssetsShowcase
./tools/unity_cmd.sh "exec:HZExportCommands.ExportXBot"           # Export X Bot mesh
./tools/unity_cmd.sh "exec:HZExportCommands.ExportBodyBlock"      # Export Body Block animation
./tools/unity_cmd.sh "exec:HZExportCommands.ExportTestCube"       # Export test cube GLB
```

### Python validators

```bash
python tools/dump_model_hza.py <file.hza>      # Validate ModelBlobAsset
python tools/dump_material_hza.py <file.hza>    # Validate MaterialBlobAsset
python tools/dump_animation_hza.py <file.hza>   # Validate AnimationBlobAsset
```

## Workflow

### Writing and testing C# scripts

1. Write/edit `.cs` files under `Assets/Editor/HZExporter/`
2. `./tools/unity_refresh.sh` — triggers compile and blocks until it completes; exits 0 on ok, 1 on compile errors (printed to stderr), 2 on timeout
3. If errors, fix and repeat from step 1

### Running exports

1. Write a static no-arg method (e.g., `HZExportCommands.ExportXBot()`)
2. `./tools/unity_refresh.sh` — compile it
3. `./tools/unity_cmd.sh "exec:HZExportCommands.ExportXBot"` — returns `exec_queued:...` immediately
4. Wait ~20-30s for the export to finish on the main thread, then check `ExportedAssets/` for output (the exec is fire-and-forget; use file presence or Editor.log to confirm completion)

### Notes

- The TCP listener auto-starts via `[InitializeOnLoad]` and survives domain reloads. Only the main editor binds it (batch-mode asset-import workers skip via `Application.isBatchMode`).
- Compile status is derived from `EditorApplication.isCompiling` + `assemblyCompilationFinished` callbacks. `log` returns `compiling` while a refresh is pending or a compile is in flight, `ok` after success (domain reload resets state), `ERRORS(n):...` if the compile failed.
- `ExportScene` exports the currently active scene — use `OpenSampleScene`/`OpenAssetsShowcase` to switch.
- Connection error = Unity not open or listener not running (see Troubleshooting).

## Project Structure

- `Assets/Editor/HZExporter/` — all exporter C# scripts (editor-only)
  - `HZBlobWriter.cs` — binary writer with self-relative BlobPtr/StringBlob
  - `HZGlbWriter.cs` — glTF 2.0 (.glb) intermediate mesh export (static + skinned)
  - `HZMeshExporter.cs` — ModelBlobAsset binary export
  - `HZMaterialExporter.cs` — MaterialBlobAsset export (.hzt text + .hza binary)
  - `HZTextureExporter.cs` — TextureBlobAsset + PVRTexTool BC1/BC3 compression
  - `HZAnimationExporter.cs` — AnimationBlobAsset (baked at 30fps)
  - `HZSceneExporter.cs` — SceneBlobAsset manifest + per-entity EntityBlobAsset (.hzt)
  - `HZExportCommands.cs` — CLI-callable static methods for exports
  - `RemoteRefresh.cs` — TCP listener for CLI control (port 7321)
- `tools/` — CLI tools (unity_cmd, validators, python libs)
- `ExportedAssets/` — export output directory (cross-platform, uses relative paths)
- `EXPORT_PLAN.md` — detailed binary format spec and implementation plan

## Engine Reference

Target engine: hz-engine-v5 (`/Users/gabrieldechichi/dev/gabrieldechichi/hz-engine-v5/` on macOS)

The engine loads Unity-exported scenes from `assets/unity/`. To test:
1. Copy `ExportedAssets/scenes/` and `ExportedAssets/glb/` and `ExportedAssets/animations/` to engine's `assets/unity/`
2. Set `SCENE_FILE` in `src/demos/demo_scene_system.c` to point to the scene
3. `./hzbuild macos --screenshot` to build, run, and capture a screenshot

See `EXPORT_PLAN.md` for full binary format documentation.

## Known Issues / TODO

- **Skeleton bone skipping**: Currently only skips children of root-level Animator GameObjects. Characters nested deeper in the hierarchy (like in AssetsShowcase) export all skeleton bones as empty entities. Needs recursive Animator detection.
- **GLB deduplication**: Multiple GameObjects sharing the same source mesh (e.g. Sphere, Sphere (1)) each export separate GLBs. Should deduplicate by source asset path.
- **PVRTexTool on macOS**: KTX texture compression is skipped if PVRTexToolCLI is not found (logs a warning). Set `PVRTEXTOOL_PATH` env var if available.
- **Skinned mesh in-scene**: Rendered in T-pose without animation integration.
