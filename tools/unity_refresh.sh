#!/bin/bash
# unity_refresh.sh — kick Unity into recompiling, block until it finishes.
#
# Why the focus dance: on macOS the Editor's main-thread update loop (which
# drives `EditorApplication.update`, `assemblyCompilationFinished`, etc.) only
# ticks reliably when Unity is frontmost. Compile itself runs on a background
# thread, but its completion callback is dispatched from the main thread — so
# if Unity drops to background mid-compile, the `log` status will never flip
# from "compiling" to "ok" or "ERRORS(...)".
#
# Strategy: remember who had focus, keep Unity frontmost for the duration of
# the compile, then restore. Focus is stolen for roughly the compile time
# (seconds), not permanently, and it is not re-stolen on every poll iteration.
#
# Exit: 0 on "ok", 1 on compile errors, 2 on timeout/connection failure.

PORT=7322
TIMEOUT="${1:-90}"

activate_unity() {
    osascript -e 'tell application "System Events" to tell process "Unity" to set frontmost to true' 2>/dev/null
}

restore_focus() {
    if [ -n "$PREV_FRONT" ] && [ "$PREV_FRONT" != "Unity" ]; then
        osascript -e "tell application \"System Events\" to tell process \"$PREV_FRONT\" to set frontmost to true" 2>/dev/null
    fi
}

PREV_FRONT=$(osascript -e 'tell application "System Events" to name of first application process whose frontmost is true' 2>/dev/null)
trap restore_focus EXIT

activate_unity
sleep 0.25

QUEUE=$(printf 'refresh' | nc -w 2 localhost $PORT 2>/dev/null)
if [ -z "$QUEUE" ]; then
    echo "ERROR: Could not connect to Unity on port $PORT" >&2
    exit 2
fi
echo "$QUEUE"

start=$(date +%s)
last_status=""
while :; do
    now=$(date +%s)
    elapsed=$((now - start))
    if [ "$elapsed" -gt "$TIMEOUT" ]; then
        echo "ERROR: compile did not finish within ${TIMEOUT}s (last: $last_status)" >&2
        exit 2
    fi

    STATUS=$(printf 'log' | nc -w 2 localhost $PORT 2>/dev/null)
    last_status="$STATUS"

    case "$STATUS" in
        ok)
            echo "ok"
            exit 0
            ;;
        ERRORS*)
            echo "$STATUS" >&2
            exit 1
            ;;
        compiling|"")
            # Empty responses happen briefly during domain reload when the
            # listener restarts; keep polling silently. We do NOT re-steal
            # focus here — Application.runInBackground=true (set by
            # RemoteRefresh) keeps the Editor update loop ticking while
            # backgrounded on macOS, which is what makes this reliable.
            sleep 1
            ;;
        *)
            echo "unexpected status: $STATUS" >&2
            sleep 1
            ;;
    esac
done
