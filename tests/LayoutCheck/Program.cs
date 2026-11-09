// Prints the C# bindings' layout in the same form as tests/layout.c, so
// tests/check_layout.sh can diff them. Fields come in declaration order.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FH2ModPlatform.Native;

Type[] types =
[
    typeof(FH2Xmm), typeof(FH2Context), typeof(FH2ModInfo), typeof(FH2Setting), typeof(FH2Page),
    typeof(FH2PageRow), typeof(FH2TitleEntry), typeof(FH2Hotkey), typeof(FH2ModApi),
];

foreach (var t in types)
{
    Console.WriteLine($"{t.Name} {RuntimeHelpers.SizeOf(t.TypeHandle)}");
    if (t.IsExplicitLayout) continue; // a union: the C side lists no members
    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        Console.WriteLine($"  {Marshal.OffsetOf(t, f.Name)}");
}
