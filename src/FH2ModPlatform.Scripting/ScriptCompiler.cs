using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace FH2ModPlatform.Scripting;

/// <summary>
/// Compiles one script file (.cs or .vb) into an assembly in memory, for Platform.NET to start
/// like a hosted mod. Called by reflection from the SDK's HostEntry, so its signature only
/// uses plain types.
/// </summary>
public static class ScriptCompiler
{
    // What every script gets without writing it: the SDK, and the namespaces a mod uses most.
    private static readonly string[] s_imports =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Numerics",
        "System.Text", "System.Threading", "System.Threading.Tasks", "FH2", "FH2ModPlatform",
    ];

    /// <summary>
    /// Compiles <paramref name="sourcePath"/> against <paramref name="references"/> (file paths).
    /// Returns the assembly's bytes, or null if it didn't compile; <paramref name="pdb"/> gets its
    /// symbols (with the source in them, for line numbers and debugging), <paramref name="messages"/>
    /// the errors and warnings as "file(line,col): error XX0000: text".
    /// </summary>
    public static byte[]? Compile(string sourcePath, string assemblyName, string[] references, out byte[]? pdb, out string[] messages)
    {
        pdb = null;
        SourceText text;
        using (var file = File.OpenRead(sourcePath))
            text = SourceText.From(file, Encoding.UTF8, canBeEmbedded: true);   // UTF-8 unless the file says otherwise (a BOM)
        var refs = references.Where(File.Exists).Select(r => (MetadataReference)MetadataReference.CreateFromFile(r)).ToList();
        bool vb = sourcePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);

        Compilation compilation;
        if (vb)
        {
            var parse = new VisualBasicParseOptions(Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Latest);
            var tree = VisualBasicSyntaxTree.ParseText(text, parse, sourcePath);
            var options = new VisualBasicCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    globalImports: s_imports.Select(GlobalImport.Parse).Append(GlobalImport.Parse("Microsoft.VisualBasic")),
                    optionStrict: OptionStrict.Off, optionInfer: true, optionExplicit: true)
                .WithOptimizationLevel(OptimizationLevel.Debug);
            compilation = VisualBasicCompilation.Create(assemblyName, [tree], refs, options);
        }
        else
        {
            var parse = new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest);
            // With an encoding: the compiler needs one to write debug information for it.
            var imports = SourceText.From(string.Concat(s_imports.Select(n => $"global using {n};\n")), Encoding.UTF8);
            var usings = CSharpSyntaxTree.ParseText(imports, parse, "<imports>.cs");
            var tree = CSharpSyntaxTree.ParseText(text, parse, sourcePath);
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true)
                .WithOptimizationLevel(OptimizationLevel.Debug);
            compilation = CSharpCompilation.Create(assemblyName, [usings, tree], refs, options);
        }

        using var dll = new MemoryStream();
        using var symbols = new MemoryStream();
        var result = compilation.Emit(dll, symbols,
            embeddedTexts: [EmbeddedText.FromSource(sourcePath, text)],
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

        messages = result.Diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .OrderByDescending(d => d.Severity)
            .Select(Format)
            .ToArray();
        if (!result.Success) return null;
        pdb = symbols.ToArray();
        return dll.ToArray();
    }

    private static string Format(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        var where = span.IsValid ? $"{Path.GetFileName(span.Path)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})" : "";
        return $"{where}: {d.Severity.ToString().ToLowerInvariant()} {d.Id}: {d.GetMessage()}";
    }
}
