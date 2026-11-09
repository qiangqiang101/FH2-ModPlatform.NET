#!/usr/bin/env bash
# End-to-end test without the game: builds FH2DotNet.dll, the SDK and the test
# mods, stages a fake Azimuth folder laid out like a real install, and runs
# fake_platform.exe on it, loading
#   fh2-mods\FH2DotNet.dll   with HelloMod and TestMod hosted in fh2-mods-dotnet
#   HelloMod.dll             the same example, compiled with NativeAOT
# Needs llvm-mingw, CMake, Ninja, the .NET 10 SDK and Visual Studio's C++ tools.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"
. scripts/env.sh

cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=platform/cmake/mingw-w64-x86_64.cmake \
      -DCMAKE_BUILD_TYPE=Release -DFH2DOTNET_TESTS=ON >/dev/null
cmake --build build --target fh2dotnet fh2console fake_platform >/dev/null
dotnet build src/FH2ModPlatform -c Release -v q -nologo >/dev/null
dotnet publish src/FH2ModPlatform.Scripting -c Release -v q -nologo -o build/scripting >/dev/null
dotnet build examples/HelloMod -c Release -v q -nologo -p:FH2ModsDir= >/dev/null
dotnet build tests/mods/TestMod -c Release -v q -nologo -p:FH2ModsDir= >/dev/null
"${aot_env[@]}" dotnet publish examples/HelloMod -c Release -p:FH2ModHosting=Aot -p:FH2ModsDir= -v q -nologo 2>&1 | grep -v NU1903 || true

az=$(mktemp -d)
trap 'rm -rf "$az"' EXIT
mods="$az/fh2-mods"
mkdir -p "$mods/dotnet" "$az/fh2-mods-dotnet/scripts" "$az/fh2-mods-dotnet/HelloMod" "$az/fh2-mods-dotnet/TestMod" "$az/fh2-mods-dotnet/Off.disabled" "$az/aot"
cp build/FH2DotNet.dll "$mods/"
cp build/FH2Console.exe "$mods/dotnet/"
cp build/scripting/*.dll build/scripting/FH2ModPlatform.Scripting.deps.json "$mods/dotnet/"
cp tests/scripts/*.cs tests/scripts/*.vb "$az/fh2-mods-dotnet/scripts/"
cp src/FH2ModPlatform/bin/Release/net10.0/FH2ModPlatform.dll \
   src/FH2ModPlatform/bin/Release/net10.0/FH2ModPlatform.runtimeconfig.json "$mods/dotnet/"
cp examples/HelloMod/bin/Release/net10.0/HelloMod.* "$az/fh2-mods-dotnet/HelloMod/"
cp tests/mods/TestMod/bin/Release/net10.0/{TestMod,TestModHelper}.* "$az/fh2-mods-dotnet/TestMod/"
cp examples/HelloMod/bin/Release/net10.0/win-x64/publish/HelloMod.dll "$az/aot/"
# A hosted build copied into fh2-mods itself, by mistake: FH2DotNet.dll should say where it goes.
cp examples/HelloMod/bin/Release/net10.0/HelloMod.dll "$mods/Stray.dll"

win() { cygpath -w "$1" 2>/dev/null || echo "$1"; }
out="$az/output.txt"
# After loading: reload TestMod by command, use everything again (the new copy should answer),
# switch auto-reload on and rewrite TestMod.dll as a build would, and try the error cases.
FH2_FAKE_ANNA=1 ./build/fake_platform.exe "$(win "$az")" "$(win "$mods/FH2DotNet.dll")" "$(win "$az/aot/HelloMod.dll")" -- \
    "Platform.NET mods" \
    "Platform.NET reload TestMod" \
    "!exercise" \
    "!set Platform.NET AutoReload 1" \
    "!touch $(win "$az/fh2-mods-dotnet/TestMod/TestMod.dll")" \
    "!sleep 3000" \
    "HelloScript ping" \
    "HiScript hi there" \
    "!touch $(win "$az/fh2-mods-dotnet/scripts/HiScript.vb")" \
    "!sleep 3000" \
    "Platform.NET mods" \
    "Platform.NET reload Nope" \
    "Platform.NET reload" \
    "!select Platform.NET Hosted TestMod" \
    "!sleep 1500" \
    "!open Platform.NET Hosted" \
    | tee "$out" || true   # Platform.NET reports a problem: scripts/Broken.cs doesn't compile, on purpose

fail=0
expect() {
    if grep -qF -- "$1" "$out"; then echo "ok:   $1"; else echo "FAIL: $1"; fail=1; fi
}
echo
echo "--- checks ---"
expect "[platform] fh2mod_init returned 0"
expect "[HelloMod.NET] hello from C# (hosted by Platform.NET)"
expect "[HelloMod.NET] hello from C# (NativeAOT)"
expect "[TestMod.NET] OnInit (copy FH2Mod:TestMod, reload: False)"
expect "[Platform.NET] console: Console.WriteLine from copy FH2Mod:TestMod"
expect "[Platform.NET] trace: Trace.WriteLine works too"
expect "[TestMod.NET] OnUnload (copy FH2Mod:TestMod)"
expect "[TestMod.NET] OnInit (copy FH2Mod:TestMod#2, reload: True)"
expect "[platform] TestMod: reloaded"
expect "[TestMod.NET] slider -> 0.5 (copy FH2Mod:TestMod#2)"
expect "[TestMod.NET] hotkey pressed (copy FH2Mod:TestMod#2)"
expect "[Platform.NET] reloading hosted mods when their DLLs or scripts change"
expect "[TestMod.NET] OnInit (copy FH2Mod:TestMod#3, reload: True)"
expect "running (reload 2)"
expect "auto-reload is on"
expect "error: no hosted mod Nope"
expect "[TestMod.NET] OnInit (copy FH2Mod:TestMod#4, reload: True)"
expect "[platform] done: 1 failed"   # Platform.NET, for Broken.cs
expect "Platform.NET add_page Hosted"
expect "HelloMod.NET | 1.1.0 | key HelloMod | style 0 alert 0"
expect "TestMod.NET | 0.1.0 | key TestMod | style 0 alert 0 | SDK test mod. Its settings are in Pause > Settings > MODS and MODS > All settings (3 rows). Press A to reload it"
expect "Off | Off | key (null) | style 1"
expect "[TestMod.NET] OnInit (copy FH2Mod:TestMod#5, reload: True)"
expect "WRONG FOLDER: Stray.dll is a hosted .NET mod"
expect 'set_notice ".NET MOD IN WRONG FOLDER: STRAY.DLL"'
if grep -q "WRONG FOLDER: FH2DotNet" "$out"; then echo "FAIL: flagged a native DLL"; fail=1; fi
expect "ANNA add_entry key=testmod.car.lights.test_light parent=car.lights"
expect "ANNA entry Lost failed: System.InvalidOperationException: no list in ANNA's menu is called 'No Such List'"
expect "[HelloScript] hello from a C# script, ${FH2_GAME_DIR:+354}${FH2_GAME_DIR:-0} cars, 6"   # 354 from the game's string tables (FH2_GAME_DIR)
expect "[HiScript] hi from a VB script, reload: False"
expect "Broken: Broken.cs(4,44): error CS0103: The name 'ThisDoesNotExist' does not exist in the current context"
expect "Broken: Broken.cs doesn't compile"
expect "pong from a script"
expect "hi there from VB"
expect "[HiScript] hi from a VB script, reload: True"
if grep -q "FAILED\|Unhandled exception" "$out"; then echo "FAIL: errors in the output"; fail=1; fi
[ $fail = 0 ] && echo "host test passed" || { echo "host test FAILED" >&2; exit 1; }
