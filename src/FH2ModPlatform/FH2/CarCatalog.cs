using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using FH2ModPlatform;

namespace FH2;

/// <summary>A car the game has: its id (Data_Car.Id, the game's "ordinal") and names.</summary>
public sealed record CarInfo(int Id, string ShortName, string FullName)
{
    public override string ToString() => $"{ShortName} ({Id})";
}

/// <summary>
/// Every car in the game, by name, read from the game's own string table (English): no
/// database or game memory needed, so it works anywhere, on any thread, even before the game starts.
/// </summary>
public static partial class CarCatalog
{
    private static readonly Lazy<IReadOnlyList<CarInfo>> s_all = new(Load);

    /// <summary>All the cars, sorted by name. Empty if the game's files couldn't be read.</summary>
    public static IReadOnlyList<CarInfo> All => s_all.Value;

    /// <summary>The car with <paramref name="id"/>, or null.</summary>
    public static CarInfo? Get(int id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>Cars whose names contain every word of <paramref name="query"/> ("camaro ss"), or whose id it is.</summary>
    public static IReadOnlyList<CarInfo> Find(string query)
    {
        if (int.TryParse(query.Trim(), out var id)) return All.Where(c => c.Id == id).ToList();
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return All.Where(c => words.All(w =>
            c.ShortName.Contains(w, StringComparison.OrdinalIgnoreCase) ||
            c.FullName.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // media\Stripped\StringTables\EN.zip, Data_Car.str: IDS_ModelShort_<id> ("Chevy Camaro SS") and
    // IDS_DisplayName_<id> ("Camaro SS Coupe"). Some cars' strings are numbered 10000 above their id.
    private static IReadOnlyList<CarInfo> Load()
    {
        try
        {
            var zipPath = Path.Combine(ModPlatform.GameDir, "media", "Stripped", "StringTables", "EN.zip");
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("Data_Car.str", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return [];
            using var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            var shortNames = new Dictionary<int, string>();
            var fullNames = new Dictionary<int, string>();
            foreach (var (name, text) in ParseStringTable(ms.ToArray()))
            {
                var m = NamePattern().Match(name);
                if (!m.Success) continue;
                var id = int.Parse(m.Groups[2].Value);
                if (id >= 10000) id -= 10000;
                (m.Groups[1].Value == "ModelShort" ? shortNames : fullNames)[id] = text;
            }
            return shortNames
                .Select(kv => new CarInfo(kv.Key, kv.Value, fullNames.GetValueOrDefault(kv.Key, kv.Value)))
                .OrderBy(c => c.ShortName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    [GeneratedRegex(@"^IDS_(ModelShort|DisplayName)_(\d+)$")]
    private static partial Regex NamePattern();

    /// <summary>An LSB2 string table: (IDS_ name, text) for every named string.</summary>
    internal static IEnumerable<(string Name, string Text)> ParseStringTable(byte[] b)
    {
        if (b.Length < 0x18 || Encoding.ASCII.GetString(b, 0, 4) != "LSB2") yield break;
        int sections = BitConverter.ToInt32(b, 0x10);
        int s0 = BitConverter.ToInt32(b, 0x14);
        int count = BitConverter.ToInt32(b, s0 + 4);
        int table = s0 + 8, strings = table + (count + 1) * 6;
        var texts = new Dictionary<int, string>();
        for (int i = 0; i < count; i++)
        {
            int key = BitConverter.ToUInt16(b, table + i * 6);
            int offset = BitConverter.ToInt32(b, table + i * 6 + 2);
            int p = strings + 2 * offset, e = p;
            while (b[e] != 0 || b[e + 1] != 0) e += 2;
            texts[key] = Encoding.Unicode.GetString(b, p, e - p);
        }
        if (sections < 2) yield break;
        int s1 = BitConverter.ToInt32(b, 0x18);
        if (s1 == 0) yield break;
        int c1 = BitConverter.ToInt32(b, s1 + 4);
        int names = s1 + 8 + c1 * 8;
        for (int i = 0; i < c1; i++)
        {
            int key = BitConverter.ToUInt16(b, s1 + 8 + i * 8);
            int nameOffset = BitConverter.ToInt32(b, s1 + 8 + i * 8 + 4);
            int e = names + nameOffset;
            while (b[e] != 0) e++;
            if (texts.TryGetValue(key, out var text))
                yield return (Encoding.ASCII.GetString(b, names + nameOffset, e - names - nameOffset), text);
        }
    }
}
