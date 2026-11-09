# Sourced by the scripts here. Git Bash drops Windows variables whose names
# have parentheses, like ProgramFiles(x86), and NativeAOT needs them to find
# Visual Studio's linker (through vswhere). Put them back.
if [ -n "${MSYSTEM:-}" ] || [ -n "${WINDIR:-}" ]; then
    PATH="$PATH:/c/Program Files (x86)/Microsoft Visual Studio/Installer"
    export PATH
    aot_env=(env "ProgramFiles(x86)=C:\Program Files (x86)" "CommonProgramFiles(x86)=C:\Program Files (x86)\Common Files")
else
    aot_env=()
fi
