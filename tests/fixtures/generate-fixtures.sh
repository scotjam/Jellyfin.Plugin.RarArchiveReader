#!/usr/bin/env bash
# Regenerates the RAR test fixtures.
#
# The generated archives are committed, so you only need this when adding a
# format or refreshing the payloads. Requires RARLab's `rar` CLI - 7-Zip and
# unrar can read RAR but cannot create it.
#
#   Linux/macOS : install `rar` from https://www.rarlab.com/download.htm
#   Windows     : WinRAR ships Rar.exe; point RAR_BIN at it, e.g.
#                 RAR_BIN="/c/Program Files/WinRAR/Rar.exe" ./generate-fixtures.sh
#
# Payloads are deterministic (fixed RNG seed), so regenerating produces the same
# content and only the archive containers differ.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$HERE/source"
OUT="$HERE/archives"
RAR_BIN="${RAR_BIN:-rar}"

if ! command -v "$RAR_BIN" >/dev/null 2>&1 && [ ! -x "$RAR_BIN" ]; then
    echo "error: rar not found. Set RAR_BIN to the rar/Rar.exe path." >&2
    exit 1
fi

mkdir -p "$SRC" "$OUT"

# --- payloads -------------------------------------------------------------
# payload.bin  : ~600 KB of word soup - compresses well, so -m3/-m5 really compress
# noise.bin    : 64 KB of random bytes - incompressible, second entry in each archive
python3 - "$SRC" <<'PY'
import os, random, sys
src = sys.argv[1]
random.seed(1234)
words = ['alpha','bravo','charlie','delta','echo','foxtrot','golf','hotel']
buf = bytearray()
while len(buf) < 600 * 1024:
    buf += (' '.join(random.choice(words) for _ in range(12)) + '\n').encode()
open(os.path.join(src, 'payload.bin'), 'wb').write(bytes(buf[:600 * 1024]))
open(os.path.join(src, 'noise.bin'), 'wb').write(
    bytes(random.getrandbits(8) for _ in range(64 * 1024)))
PY

# --- archives -------------------------------------------------------------
# 100 KB volumes keep the fixtures small while still producing several volume
# seams for the random-access tests to cross.
gen() {
    local name="$1"; shift
    local dir="$OUT/$name"
    rm -rf "$dir"; mkdir -p "$dir"
    ( cd "$SRC" && "$RAR_BIN" a -idq -ep1 -o+ "$@" "$dir/$name.rar" payload.bin noise.bin >/dev/null )
    printf '  %-22s %s\n' "$name" "$(ls "$dir" | tr '\n' ' ')"
}

echo "generating fixtures with: $RAR_BIN"
gen rar4-store-single   -ma4 -m0                    # RAR4, stored, one volume
gen rar4-store-multi    -ma4 -m0 -v100k             # RAR4, stored, .partN.rar
gen rar4-store-oldvol   -ma4 -m0 -v100k -vn         # RAR4, stored, .rar/.rNN
gen rar4-comp-multi     -ma4 -m5 -v100k             # RAR4, compressed
gen rar5-store-multi    -ma5 -m0 -v100k             # RAR5, stored
gen rar5-comp-single    -ma5 -m5                    # RAR5, compressed, one volume
gen rar5-comp-multi     -ma5 -m5 -v100k             # RAR5, compressed
gen rar5-comp-m3-multi  -ma5 -m3 -md32m -v100k      # RAR5 m3 - matches common releases

echo "done. Update RarFormatFixtures.All if you added or renamed a format."
