#!/bin/bash
# Builds the plug-in (Debug) and runs tests/test_geometry.py inside the running Rhino
# through its script server, then prints the result. Exits 1 if any test failed.
#
#   tests/run_tests.sh                      build and test the current source
#   tests/run_tests.sh path/to/Some.dll     test that DLL instead (no build)
#   tests/run_tests.sh "" corner arc        only scenarios whose name contains a filter
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RHINOCODE="/Applications/Rhino 8.app/Contents/Resources/bin/rhinocode"
OUT=/tmp/stairs_test_result.txt
DLL="$1"
shift 2>/dev/null

if ! "$RHINOCODE" list 2>/dev/null | grep -q rhinocode_; then
    echo "No running Rhino with a script server found (start Rhino 8, then StartScriptServer)." >&2
    exit 2
fi
if [ -z "$DLL" ]; then
    DLL="$ROOT/src/StairsPlugin/bin/Debug/StairsPlugin.dll"
    (cd "$ROOT/src/StairsPlugin" && dotnet build -c Debug -nologo -v q >/dev/null) || { echo "Build failed." >&2; exit 2; }
elif [ -f "$DLL" ]; then
    DLL="$(cd "$(dirname "$DLL")" && pwd)/$(basename "$DLL")" # Rhino needs an absolute path
else
    echo "No such DLL: $DLL" >&2
    exit 2
fi

python3 - "$DLL" "$@" > /tmp/stairs_test_config.json <<'EOF'
import json, sys
print(json.dumps({"dll": sys.argv[1], "only": sys.argv[2:]}))
EOF

rm -f "$OUT"
# rhinocode silently ignores script paths containing spaces (it prints its usage and
# exits 0), so run a copy from a path without them
SCRIPT=/tmp/stairs_test_geometry.py
cp "$ROOT/tests/test_geometry.py" "$SCRIPT"
"$RHINOCODE" script "$SCRIPT" >/dev/null
# rhinocode returns before the script finishes; the script writes its RUN line first
for _ in $(seq 1 60); do [ -f "$OUT" ] && break; sleep 0.5; done
if [ ! -f "$OUT" ]; then echo "The test script did not start in Rhino within 30 s." >&2; exit 2; fi
for _ in $(seq 1 1200); do
    grep -q '^DONE' "$OUT" 2>/dev/null && break
    sleep 0.5
done
if ! grep -q '^DONE' "$OUT"; then cat "$OUT"; echo "Tests did not finish within 10 minutes." >&2; exit 2; fi
cat "$OUT"
! grep -q '^FAIL\|^ERROR' "$OUT"
