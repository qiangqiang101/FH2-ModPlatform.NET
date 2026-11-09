#!/usr/bin/env bash
# Builds everything and stages dist/, laid out like the Azimuth folder:
#   dist/DWrite.dll                                the FH2 Mod Platform (the submodule's; Azimuth 1.0.3 no longer loads d3dcompiler_47.dll)
#   dist/fh2-mods/FH2DotNet.dll                    runs hosted .NET mods
#   dist/fh2-mods/dotnet/FH2ModPlatform.dll        the SDK, shared by hosted mods
#   dist/fh2-mods/dotnet/FH2ModPlatform.runtimeconfig.json
#   dist/fh2-mods/dotnet/FH2ModPlatform.Scripting.dll  the script compiler, with Roslyn (Microsoft.CodeAnalysis*.dll)
#   dist/fh2-mods/dotnet/FH2Console.exe           the console window (MODS > Console window)
#   dist/examples/hosted/HelloMod/                 the example, for fh2-mods-dotnet\
#   dist/examples/aot/HelloMod.dll                 the same, NativeAOT, for fh2-mods\
#   dist/sdk/FH2ModPlatform.<version>.nupkg        the SDK package, for mod projects
# Windows only (NativeAOT links with Visual Studio's C++ tools). Needs
# llvm-mingw, CMake, Ninja and the .NET 10 SDK.
#   scripts/build.sh [--install <azimuth dir>]   also copies the runtime parts (not the examples) there
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"
. scripts/env.sh

cmake -S . -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE=platform/cmake/mingw-w64-x86_64.cmake -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build build
dotnet build src/FH2ModPlatform -c Release -nologo -v q
rm -rf build/nupkg
# FH2_PACKAGE_VERSION (set by the CI: 0.9.0-nightly.20261110, say) overrides the package's version.
dotnet pack src/FH2ModPlatform -c Release -nologo -v q -o build/nupkg ${FH2_PACKAGE_VERSION:+-p:PackageVersion=$FH2_PACKAGE_VERSION}
rm -rf build/scripting
dotnet publish src/FH2ModPlatform.Scripting -c Release -nologo -v q -o build/scripting
dotnet build examples/HelloMod -c Release -nologo -v q -p:FH2ModsDir=
"${aot_env[@]}" dotnet publish examples/HelloMod -c Release -p:FH2ModHosting=Aot -nologo -v q -p:FH2ModsDir=

rm -rf dist
mkdir -p dist/fh2-mods/dotnet dist/examples/hosted/HelloMod dist/examples/aot dist/sdk
cp build/platform/DWrite.dll dist/
cp build/FH2DotNet.dll dist/fh2-mods/
cp build/FH2Console.exe dist/fh2-mods/dotnet/
cp src/FH2ModPlatform/bin/Release/net10.0/FH2ModPlatform.{dll,runtimeconfig.json} dist/fh2-mods/dotnet/
cp build/scripting/*.dll build/scripting/FH2ModPlatform.Scripting.deps.json dist/fh2-mods/dotnet/
cp examples/HelloMod/bin/Release/net10.0/HelloMod.{dll,deps.json,runtimeconfig.json} dist/examples/hosted/HelloMod/
cp examples/HelloMod/bin/Release/net10.0/win-x64/publish/HelloMod.dll dist/examples/aot/
cp build/nupkg/*.nupkg dist/sdk/
cp platform/LICENSE dist/LICENSE-FH2ModPlatform.txt
cp platform/third_party/minhook/LICENSE.txt dist/LICENSE-MinHook.txt
cp LICENSE dist/LICENSE.txt
cp THIRD-PARTY-NOTICES.txt dist/
roslyn_notices=$(ls "${NUGET_PACKAGES:-$HOME/.nuget/packages}"/microsoft.codeanalysis.common/*/ThirdPartyNotices.rtf 2>/dev/null | tail -1)
[ -n "$roslyn_notices" ] && cp "$roslyn_notices" dist/ThirdPartyNotices-Roslyn.rtf
(cd dist && find . -type f | sed 's#^\./#  #')

if [ "${1:-}" = "--install" ]; then
    dest=${2:?usage: build.sh --install <azimuth dir>}
    [ -f "$dest/azimuth.exe" ] || { echo "no azimuth.exe in $dest" >&2; exit 1; }
    mkdir -p "$dest/fh2-mods/dotnet" "$dest/fh2-mods-dotnet/scripts"
    cp dist/fh2-mods/FH2DotNet.dll "$dest/fh2-mods/"
    cp dist/fh2-mods/dotnet/* "$dest/fh2-mods/dotnet/"
    echo "installed FH2DotNet.dll and the SDK in $dest/fh2-mods (the platform DLLs there were left alone)"
fi
