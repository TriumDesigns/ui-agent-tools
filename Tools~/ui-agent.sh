#!/usr/bin/env bash
# ui-agent - run UI Agent Tools commands against this Unity project.
#
#   ./ui-agent.sh <command> [asset-or-method]
#
# Commands: map [asset] | validate [asset] | screenshots [asset] | run <Namespace.Class.Method>
#           compile | selftest | baseline | install | all | tests
#
# If the project is open in the Unity Editor, the command is sent to the running Editor
# (via UIAgentMap/requests). Otherwise Unity is started in batch mode.
#
# Environment overrides:
#   UNITY_EDITOR_PATH   full path to the Unity executable (default: Unity Hub location for
#                       the version in ProjectSettings/ProjectVersion.txt)
#   UI_AGENT_TIMEOUT   seconds to wait for a command (default 1800)
#   UI_AGENT_PICKUP    seconds to wait for an open Editor to pick up a request (default 120)
#   UI_AGENT_MODE      "auto" (default), "batch" or "editor"

set -uo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MAP_DIR="$PROJECT_DIR/UIAgentMap"
TIMEOUT="${UI_AGENT_TIMEOUT:-1800}"
PICKUP="${UI_AGENT_PICKUP:-120}"
MODE="${UI_AGENT_MODE:-auto}"

usage() {
  sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

cmd="${1:-}"
arg="${2:-}"
case "$cmd" in
  map|validate|screenshots|run|compile|selftest|baseline|install|all|tests) ;;
  ""|-h|--help|help) usage; exit 0 ;;
  *) echo "ui-agent: unknown command '$cmd'"; usage; exit 2 ;;
esac
if [ "$cmd" = "run" ] && [ -z "$arg" ]; then
  echo "ui-agent: run needs a method, e.g. ./ui-agent.sh run AgentEdits.Edit_20260928_LoginCopy.Run"; exit 2
fi
if [ ! -f "$PROJECT_DIR/ProjectSettings/ProjectVersion.txt" ]; then
  echo "ui-agent: $PROJECT_DIR is not a Unity project root (no ProjectSettings/ProjectVersion.txt)."; exit 2
fi

mkdir -p "$MAP_DIR/logs" "$MAP_DIR/requests"

json_escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }

json_field() { # json_field <file> <field>  (simple scalar fields only)
  sed -n "s/^[[:space:]]*\"$2\":[[:space:]]*\"\{0,1\}\([^\",]*\)\"\{0,1\},\{0,1\}[[:space:]]*$/\1/p" "$1" | head -n 1
}

print_result() { # print_result <result.json>
  local f="$1"
  if [ ! -f "$f" ]; then echo "ui-agent: no result file produced ($f)"; return 1; fi
  # Print summary + messages in a readable way without requiring jq.
  if python3 -c 'import json' >/dev/null 2>&1; then
    python3 - "$f" <<'PY'
import json, sys
r = json.load(open(sys.argv[1], encoding="utf-8"))
print(("RESULT: OK" if r.get("ok") else "RESULT: FAILED") + " [" + r.get("command", "") + (" " + r["target"] if r.get("target") else "") + "]")
print(r.get("summary", ""))
msgs = [m for m in r.get("messages", []) if m]
if msgs:
    print("--- messages ---")
    for m in msgs[-60:]:
        print(m)
PY
  else
    cat "$f"
  fi
  local code
  code="$(json_field "$f" exitCode)"
  [ -z "$code" ] && code=1
  return "$code"
}

editor_is_open() { [ -f "$PROJECT_DIR/Temp/UnityLockfile" ]; }

run_in_editor() {
  local id req processing result waited
  id="$(date +%Y%m%d%H%M%S)-$$-$RANDOM"
  req="$MAP_DIR/requests/$id.request.json"
  processing="$MAP_DIR/requests/$id.processing"
  result="$MAP_DIR/requests/$id.result.json"
  printf '{"id":"%s","command":"%s","target":"%s"}\n' "$id" "$cmd" "$(json_escape "$arg")" > "$req.tmp" && mv "$req.tmp" "$req"
  echo "ui-agent: Unity Editor is open - sent '$cmd' to it (request $id). Waiting..."

  waited=0
  while [ ! -f "$processing" ] && [ ! -f "$result" ]; do
    sleep 1; waited=$((waited + 1))
    if [ "$waited" -ge "$PICKUP" ]; then
      rm -f "$req"
      echo "ui-agent: the Editor did not pick up the request within ${PICKUP}s."
      echo "  - Is Unity busy (importing, compiling, a modal dialog open, or in Play Mode)? Bring it to the front and retry."
      echo "  - Is the UI Agent Tools package installed in this project?"
      echo "  - If Unity is actually closed, delete Temp/UnityLockfile (stale lock) or use UI_AGENT_MODE=batch."
      return 1
    fi
  done
  waited=0
  while [ ! -f "$result" ]; do
    sleep 1; waited=$((waited + 1))
    if [ "$waited" -ge "$TIMEOUT" ]; then
      echo "ui-agent: timed out after ${TIMEOUT}s waiting for the Editor to finish '$cmd'."; return 1
    fi
  done
  sleep 0.2
  print_result "$result"; local code=$?
  rm -f "$result"
  return $code
}

find_unity() {
  if [ -n "${UNITY_EDITOR_PATH:-}" ]; then echo "$UNITY_EDITOR_PATH"; return; fi
  local version hubcfg secondary candidate
  version="$(sed -n 's/^m_EditorVersion:[[:space:]]*//p' "$PROJECT_DIR/ProjectSettings/ProjectVersion.txt" | tr -d '\r' | head -n 1)"
  case "$(uname -s)" in
    Darwin)
      candidate="/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity"
      hubcfg="$HOME/Library/Application Support/UnityHub/secondaryInstallPath.json"
      suffix="/$version/Unity.app/Contents/MacOS/Unity" ;;
    Linux)
      candidate="$HOME/Unity/Hub/Editor/$version/Editor/Unity"
      hubcfg="$HOME/.config/UnityHub/secondaryInstallPath.json"
      suffix="/$version/Editor/Unity" ;;
    MINGW*|MSYS*|CYGWIN*)
      candidate="/c/Program Files/Unity/Hub/Editor/$version/Editor/Unity.exe"
      hubcfg="${APPDATA:-}/UnityHub/secondaryInstallPath.json"
      if command -v cygpath >/dev/null 2>&1 && [ -n "${APPDATA:-}" ]; then hubcfg="$(cygpath -u "$APPDATA")/UnityHub/secondaryInstallPath.json"; fi
      suffix="/$version/Editor/Unity.exe" ;;
    *) echo ""; return ;;
  esac
  if [ -f "$candidate" ]; then echo "$candidate"; return; fi
  # Unity Hub "custom install location"
  if [ -f "$hubcfg" ]; then
    secondary="$(tr -d '"\r\n' < "$hubcfg" | sed 's#\\\\#/#g; s#\\#/#g')"
    if [ -n "$secondary" ]; then
      if command -v cygpath >/dev/null 2>&1; then secondary="$(cygpath -u "$secondary")"; fi
      if [ -f "$secondary$suffix" ]; then echo "$secondary$suffix"; return; fi
    fi
  fi
  echo "$candidate"
}

native_path() { # convert to a Windows path when running under Git Bash/MSYS
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else echo "$1"; fi
}

run_in_batch() {
  local unity log method needs_gpu=0 code
  unity="$(find_unity)"
  if [ -z "$unity" ] || [ ! -f "$unity" ]; then
    echo "ui-agent: Unity executable not found at '$unity'."
    echo "  Install the project's Unity version via Unity Hub, or set UNITY_EDITOR_PATH to the Unity executable."
    return 1
  fi
  log="$MAP_DIR/logs/$cmd-$(date +%Y%m%d-%H%M%S).log"

  case "$cmd" in
    map) method=Map ;; validate) method=Validate ;; screenshots) method=Screenshots; needs_gpu=1 ;;
    run) method=Run ;; compile) method=Compile ;; selftest) method=SelfTestAll; needs_gpu=1 ;;
    baseline) method=Baseline ;; install) method=Install ;; all) method=All; needs_gpu=1 ;;
    tests) method="" ;;
  esac

  local args=(-batchmode -projectPath "$(native_path "$PROJECT_DIR")" -logFile "$(native_path "$log")")
  if [ "$cmd" = "tests" ]; then
    args+=(-runTests -testPlatform EditMode -assemblyNames UIAgentTools.Tests.Editor -testResults "$(native_path "$MAP_DIR/test-results.xml")")
  else
    [ "$needs_gpu" -eq 0 ] && args+=(-nographics)
    args+=(-quit -executeMethod "UIAgentTools.Cli.$method")
    [ -n "$arg" ] && args+=(-agentTarget "$arg")
  fi

  local launcher=()
  if [ "$needs_gpu" -eq 1 ] && [ "$(uname -s)" = "Linux" ] && [ -z "${DISPLAY:-}" ] && command -v xvfb-run >/dev/null 2>&1; then
    launcher=(xvfb-run -a)
  fi

  echo "ui-agent: starting Unity in batch mode for '$cmd' (log: ${log#$PROJECT_DIR/})..."
  rm -f "$MAP_DIR/last-result.json"
  ${launcher[@]+"${launcher[@]}"} "$unity" "${args[@]}"
  code=$?

  if [ "$cmd" = "tests" ]; then
    case $code in
      0) echo "RESULT: OK - all tests passed (UIAgentMap/test-results.xml)";;
      2) echo "RESULT: FAILED - some tests failed (UIAgentMap/test-results.xml)";;
      *) echo "RESULT: FAILED - test run error (exit $code), see $log";;
    esac
    return $code
  fi

  if [ -f "$MAP_DIR/last-result.json" ]; then
    print_result "$MAP_DIR/last-result.json"; code=$?
  else
    echo "RESULT: FAILED - Unity exited with code $code before the command ran."
    code=1
  fi
  if [ "$code" -ne 0 ] && [ -f "$log" ]; then
    if grep -q "error CS" "$log"; then
      echo "--- compile errors (from $log) ---"; grep "error CS" "$log" | sort -u | head -n 40
    fi
    if grep -qi "another Unity instance is running" "$log"; then
      echo "ui-agent: Unity says the project is already open. Retry with the Editor open (it will be used automatically) or close it."
    fi
    echo "--- last lines of $log ---"; tail -n 40 "$log"
  fi
  return $code
}

if [ "$MODE" = "editor" ] || { [ "$MODE" = "auto" ] && editor_is_open; }; then
  if [ "$cmd" = "tests" ]; then
    echo "ui-agent: 'tests' needs Unity closed (batch mode). While the Editor is open use 'validate' - it runs the same checks."
    exit 2
  fi
  run_in_editor; exit $?
fi
run_in_batch; exit $?
