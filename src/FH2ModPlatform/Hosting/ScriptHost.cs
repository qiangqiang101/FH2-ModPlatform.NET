using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace FH2ModPlatform.Hosting;

/// <summary>
/// Compiles scripts (fh2-mods-dotnet\scripts\*.cs, *.vb) through FH2ModPlatform.Scripting.dll,
/// which carries Roslyn. Loaded the first time a script needs compiling, into a load context of
/// its own, so a game without scripts never loads the compiler.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only runs on the JIT runtime FH2DotNet.dll hosts.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Only runs on the JIT runtime FH2DotNet.dll hosts.")]
[UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Only runs on the JIT runtime FH2DotNet.dll hosts.")]
internal static class ScriptHost
{
    private const string CompilerFile = "FH2ModPlatform.Scripting.dll";
    private static MethodInfo? s_compile;
    private static string[]? s_references;

    /// <summary>The script folder's file extensions, as the watcher and the listing use them.</summary>
    public static bool IsScript(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compiles <paramref name="path"/> as assembly <paramref name="name"/>. Returns the assembly and its
    /// symbols, or null with the compiler's errors in <paramref name="messages"/>.
    /// </summary>
    public static (byte[] Dll, byte[]? Pdb)? Compile(string path, string name, out string[] messages)
    {
        var compile = Compiler();
        object?[] args = [path, name, References(), null, null];
        var dll = (byte[]?)compile.Invoke(null, args);
        messages = (string[]?)args[4] ?? [];
        return dll is null ? null : (dll, (byte[]?)args[3]);
    }

    private static MethodInfo Compiler()
    {
        if (s_compile is not null) return s_compile;
        var dir = Path.GetDirectoryName(typeof(Mod).Assembly.Location)!;
        var file = Path.Combine(dir, CompilerFile);
        if (!File.Exists(file))
            throw new FileNotFoundException($"the script compiler isn't installed (fh2-mods\\dotnet\\{CompilerFile})");
        var alc = new CompilerContext(file);
        var type = alc.LoadFromAssemblyPath(file).GetType("FH2ModPlatform.Scripting.ScriptCompiler", throwOnError: true)!;
        return s_compile = type.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static)!;
    }

    // What scripts compile against: .NET itself (the runtime's own assemblies) and the SDK.
    private static string[] References() => s_references ??=
    [
        .. ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
        typeof(Mod).Assembly.Location,
    ];

    // Roslyn, from next to the compiler (its deps.json says what's there).
    private sealed class CompilerContext(string compiler) : AssemblyLoadContext("FH2 script compiler")
    {
        private readonly AssemblyDependencyResolver _resolver = new(compiler);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
