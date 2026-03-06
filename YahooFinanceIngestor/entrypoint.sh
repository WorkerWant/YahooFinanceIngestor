#!/usr/bin/env bash
set -euo pipefail

should_use_xvfb=false

args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
    current="${args[$i]}"

    if [[ "$current" == "--headless=false" ]]; then
        should_use_xvfb=true
        break
    fi

    if [[ "$current" == "--headless" && $((i + 1)) -lt ${#args[@]} ]]; then
        next_value="${args[$((i + 1))],,}"
        if [[ "$next_value" == "false" ]]; then
            should_use_xvfb=true
            break
        fi
    fi
done

if [[ "$should_use_xvfb" == "true" ]]; then
    display_number=99
    display=":$display_number"
    Xvfb "$display" -screen 0 1280x720x24 -nolisten tcp >/tmp/xvfb.log 2>&1 &
    xvfb_pid=$!

    cleanup() {
        if kill -0 "$xvfb_pid" >/dev/null 2>&1; then
            kill "$xvfb_pid" >/dev/null 2>&1 || true
            wait "$xvfb_pid" 2>/dev/null || true
        fi
    }

    forward_signal() {
        if [[ -n "${app_pid:-}" ]] && kill -0 "$app_pid" >/dev/null 2>&1; then
            kill "$app_pid" >/dev/null 2>&1 || true
        fi
    }

    trap cleanup EXIT
    trap forward_signal INT TERM

    export DISPLAY="$display"
    for _ in {1..50}; do
        if [[ -S "/tmp/.X11-unix/X$display_number" ]]; then
            break
        fi
        sleep 0.1
    done

    dotnet YahooFinanceIngestor.dll "$@" &
    app_pid=$!
    wait "$app_pid"
    exit $?
fi

exec dotnet YahooFinanceIngestor.dll "$@"
