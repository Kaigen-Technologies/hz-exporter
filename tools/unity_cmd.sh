#!/bin/bash
# unity_cmd.sh — sends a command to Unity's RemoteRefresh TCP listener (port 7321).
#
# On macOS, EditorApplication.update only ticks reliably while Unity is the
# frontmost app. `tell application "Unity" to activate` does NOT force front
# when another app is foreground (macOS policy). We briefly use System Events
# to set Unity frontmost, send the command, then restore the previous frontmost
# app so the flicker is ~200ms instead of a prolonged focus steal.
#
# For commands that don't need the main-thread update loop (`ping`, `log`) we
# skip the focus dance entirely.
#
# Usage:
#   ./tools/unity_cmd.sh ping
#   ./tools/unity_cmd.sh refresh
#   ./tools/unity_cmd.sh log
#   ./tools/unity_cmd.sh "exec:HZExportCommands.ExportScene"

CMD="${1:-ping}"
PORT=7322

needs_tick() {
    case "$1" in
        refresh|exec:*) return 0 ;;
        *) return 1 ;;
    esac
}

if needs_tick "$CMD"; then
    # Record current frontmost so we can restore it afterwards.
    PREV_FRONT=$(osascript -e 'tell application "System Events" to name of first application process whose frontmost is true' 2>/dev/null)

    # Force Unity frontmost so its Editor main-thread update loop ticks.
    osascript -e 'tell application "System Events" to tell process "Unity" to set frontmost to true' 2>/dev/null
    # Small pre-send settle so Unity has fully activated before we deliver.
    sleep 0.2
fi

RESPONSE=$(printf '%s' "$CMD" | nc -w 2 localhost $PORT 2>/dev/null)
RC=$?

# After setting s_execMethod / s_refreshRequested, we need Unity's main-thread
# update loop to tick at least once to pick up the command. While Unity is
# frontmost, that happens within one frame (~16ms). Hold focus for 1s to give
# a wide margin for activation latency, then restore the user's previous app.
# For `exec:*`, once mi.Invoke starts on the main thread it runs to completion
# even if Unity subsequently goes to background — so we only need that first
# tick to fire.
if needs_tick "$CMD"; then
    sleep 1.0
    if [ -n "$PREV_FRONT" ] && [ "$PREV_FRONT" != "Unity" ]; then
        osascript -e "tell application \"System Events\" to tell process \"$PREV_FRONT\" to set frontmost to true" 2>/dev/null
    fi
fi

if [ $RC -ne 0 ] || [ -z "$RESPONSE" ]; then
    echo "ERROR: Could not connect to Unity on port $PORT (is the Editor open with this project?)" >&2
    exit 1
fi
echo "$RESPONSE"
