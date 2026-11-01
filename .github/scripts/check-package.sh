#!/usr/bin/env bash
# Package hygiene checks (run in CI; run locally with: bash .github/scripts/check-package.sh)
set -u
fail=0
err() { echo "ERROR: $*"; fail=1; }

# 1. Every importable file and folder needs a committed .meta (git installs are immutable;
#    Unity would otherwise ignore the asset or generate a random GUID per consumer).
while IFS= read -r f; do
  case "$f" in
    .*|*.meta|*~/*) continue ;;   # dotfiles/dot-folders and ~ folders are ignored by Unity
  esac
  [ -f "$f.meta" ] || err "missing $f.meta"
  d="$(dirname "$f")"
  while [ "$d" != "." ]; do
    case "$d" in *~) break ;; esac
    [ -f "$d.meta" ] || err "missing $d.meta"
    d="$(dirname "$d")"
  done
done < <(git ls-files)

# 2. JSON files parse.
for f in package.json Editor/UIAgentTools.Editor.asmdef Tests/Editor/UIAgentTools.Tests.Editor.asmdef; do
  python3 -c 'import json,sys; json.load(open(sys.argv[1], encoding="utf-8"))' "$f" || err "invalid JSON: $f"
done

# 3. Docs install URL is pinned to the package.json version.
v="$(python3 -c 'import json; print(json.load(open("package.json"))["version"])')"
for f in README.md SETUP.md; do
  grep -q "ui-agent-tools.git#v$v" "$f" || err "$f does not reference tag v$v"
done
grep -q "^## $v\b" CHANGELOG.md || err "CHANGELOG.md has no '## $v' section"

# 4. License is declared and present.
grep -q '"license": "Apache-2.0"' package.json || err "package.json license is not Apache-2.0"
[ -f LICENSE.md ] || err "LICENSE.md missing"

[ "$fail" -eq 0 ] && echo "package checks passed"
exit "$fail"
