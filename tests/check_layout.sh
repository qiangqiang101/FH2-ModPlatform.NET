#!/usr/bin/env bash
# Checks that the C# bindings (src/FH2ModPlatform/Native/Structs.cs) lay out
# every struct exactly as the C compiler does fh2mod.h. Needs clang (llvm-mingw)
# and the .NET SDK. Run after changing the bindings or updating the platform.
set -euo pipefail
cd "$(dirname "$0")/.."
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT
clang --target=x86_64-w64-mingw32 -std=c11 -Iplatform/include tests/layout.c -o "$out/layout.exe"
"$out/layout.exe" | tr -d '\r' > "$out/c.txt"
dotnet run --project tests/LayoutCheck -c Release | tr -d '\r' > "$out/cs.txt"
if diff -u "$out/c.txt" "$out/cs.txt"; then
    echo "layout OK: $(grep -c '^[A-Z]' "$out/c.txt") structs, $(grep -c '^ ' "$out/c.txt") fields match fh2mod.h"
else
    echo "layout MISMATCH (- C, + C#)" >&2
    exit 1
fi
