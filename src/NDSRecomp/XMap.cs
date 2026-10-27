using System.Globalization;
using System.Text.RegularExpressions;

namespace NDSRecomp;

internal sealed record XMapSymbol(string Name, uint Address, uint Size, string InputSection, string Object);

internal sealed record XMapSection(string Name, List<XMapSymbol> Symbols);

internal sealed partial class XMap
{
    private XMap(List<XMapSection> sections, Dictionary<string, uint> commandFileSymbols, Dictionary<(string Name, string File), string> types)
    {
        Sections = sections;
        CommandFileSymbols = commandFileSymbols;
        Types = types;
    }

    public IReadOnlyList<XMapSection> Sections { get; }

    public IReadOnlyDictionary<string, uint> CommandFileSymbols { get; }

    public IReadOnlyDictionary<(string Name, string File), string> Types { get; }

    public static XMap Read(string text)
    {
        var sections = new List<XMapSection>();
        var commandFileValues = new Dictionary<string, HashSet<uint>>();
        var types = new Dictionary<(string, string), string>();
        XMapSection? current = null;

        foreach (string line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (SectionHeader().Match(line) is { Success: true } header)
            {
                current = new XMapSection(header.Groups[1].Value, []);
                sections.Add(current);
            }
            else if (line.StartsWith("# Memory map", StringComparison.Ordinal))
            {
                current = null;
            }
            else if (CommandFileSymbol().Match(line) is { Success: true } assignment)
            {
                string name = assignment.Groups[2].Value;
                if (!commandFileValues.TryGetValue(name, out var values))
                {
                    commandFileValues[name] = values = [];
                }

                values.Add(Hex(assignment.Groups[1].Value));
            }
            else if (ClosureEntry().Match(line) is { Success: true } reference)
            {
                types.TryAdd((reference.Groups[1].Value, reference.Groups[3].Value), reference.Groups[2].Value);
            }
            else if (current is not null && SymbolEntry().Match(line) is { Success: true } entry)
            {
                current.Symbols.Add(new XMapSymbol(
                    entry.Groups[4].Value,
                    Hex(entry.Groups[1].Value),
                    Hex(entry.Groups[2].Value),
                    entry.Groups[3].Value,
                    entry.Groups[5].Value.Trim()));
            }
        }

        if (sections.Count == 0)
        {
            throw new InvalidDataException("This isn't an xMAP file. It has no sections.");
        }

        var commandFileSymbols = commandFileValues.Where(p => p.Value.Count == 1).ToDictionary(p => p.Key, p => p.Value.Single());
        return new XMap(sections, commandFileSymbols, types);
    }

    public static string FileOf(XMapSymbol symbol) => symbol.Object.Split(' ', 2)[0];

    private static uint Hex(string digits) => uint.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^# \.(\S+)$")]
    private static partial Regex SectionHeader();

    [GeneratedRegex(@"^#>([0-9A-F]{8})\s+(\S+) \(linker command file\)$")]
    private static partial Regex CommandFileSymbol();

    [GeneratedRegex(@"^\s*\d+\]\((.+),(\w+)\) found in \w+ (\S+)$")]
    private static partial Regex ClosureEntry();

    [GeneratedRegex(@"^  ([0-9A-F]{8}) ([0-9A-F]{8}) (\.\S+)\s+(\S+)\t\((.*)\)$")]
    private static partial Regex SymbolEntry();
}
