namespace NDSRecomp;

internal readonly record struct MappingSymbol(int Section, uint Address, bool IsThumb);

internal readonly record struct Symbol(uint Address, uint Size, string Name, bool? IsThumb = null, int Section = -1, bool IsFunction = false)
{
    private const string ARMCode = "$a";
    private const string ThumbCode = "$t";
    private const string DataInCode = "$d";

    public bool IsCode => IsThumb is not null;

    public static (List<Section> Sections, List<Symbol> Symbols, List<MappingSymbol> MappingSymbols) ReadELF(ELF elf, Cartridge cartridge, byte[] rom, Processor processor)
    {
        var functionSections = elf.Symbols.Where(s => s.Type == SymbolType.Function && !s.Name.StartsWith('$')).Select(s => s.Section).ToHashSet();
        var codeSections = elf.Sections.Where(s => s.IsCode && s.Size > 0 && functionSections.Contains(s.Index)).ToList();
        var linkerSymbols = elf.Symbols
            .GroupBy(s => s.Name)
            .Where(g => g.Select(s => s.Value).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Value);
        var sections = NDSRecomp.Sections.Locate(linkerSymbols, codeSections.Select(s => s.Name), cartridge, rom, processor);
        var sectionOf = codeSections.Select((code, i) => (code.Index, Section: i)).ToDictionary(p => p.Index, p => p.Section);

        var mapping = elf.Symbols
            .Where(s => s.Name is ARMCode or ThumbCode or DataInCode && sectionOf.ContainsKey(s.Section))
            .GroupBy(s => sectionOf[s.Section])
            .ToDictionary(g => g.Key, g => g.Select(s => (Address: s.Value, s.Name)).OrderBy(s => s.Address).ToArray());
        var codeMapping = mapping.ToDictionary(p => p.Key, p => p.Value.Where(s => s.Name != DataInCode).ToArray());

        var symbols = new List<Symbol>();
        foreach (var symbol in elf.Symbols.Where(s => !s.Name.StartsWith('$')))
        {
            int section = sectionOf.GetValueOrDefault(symbol.Section, -1);
            bool isFunction = symbol.Type == SymbolType.Function && section >= 0;
            uint address = isFunction ? symbol.Value & ~1u : symbol.Value;
            bool? isThumb = section < 0 || symbol.Type == SymbolType.Object || (!isFunction && !sections[section].Contains(address))
                ? null
                : StateAt((isFunction ? codeMapping : mapping).GetValueOrDefault(section, []), address);
            if (isFunction && isThumb is null)
            {
                throw new InvalidDataException($"{symbol.Name} has no $a or $t mapping symbol at or before it.");
            }

            symbols.Add(new Symbol(address, symbol.Size, symbol.Name, isThumb, isThumb is null ? -1 : section, isFunction));
        }

        var mappingSymbols = codeMapping
            .SelectMany(p => p.Value.Select(s => new MappingSymbol(p.Key, s.Address, s.Name == ThumbCode)))
            .ToList();
        return (sections, symbols, mappingSymbols);
    }

    public static (List<Section> Sections, List<Symbol> Symbols, List<MappingSymbol> MappingSymbols) ReadXMap(XMap xmap, Cartridge cartridge, byte[] rom, Processor processor)
    {
        var codeSections = xmap.Sections.Where(s => s.Symbols.Any(symbol => symbol.Name is ARMCode or ThumbCode)).ToList();
        var sections = NDSRecomp.Sections.Locate(xmap.CommandFileSymbols, codeSections.Select(s => s.Name), cartridge, rom, processor);

        var symbols = new List<Symbol>();
        var mappingSymbols = new List<MappingSymbol>();
        foreach (var xmapSection in xmap.Sections)
        {
            int section = codeSections.IndexOf(xmapSection);
            var mapping = xmapSection.Symbols
                .Where(s => s.Name is ARMCode or ThumbCode or DataInCode)
                .GroupBy(s => (s.Object, s.InputSection))
                .ToDictionary(g => g.Key, g => g.Select(s => (s.Address, s.Name)).OrderBy(s => s.Address).ToArray());

            foreach (var symbol in xmapSection.Symbols.Where(s => !s.Name.StartsWith('$') && !s.Name.StartsWith('.')))
            {
                bool? isThumb = section < 0 || xmap.Types.GetValueOrDefault((symbol.Name, XMap.FileOf(symbol))) == "object"
                    ? null
                    : StateAt(mapping.GetValueOrDefault((symbol.Object, symbol.InputSection), []), symbol.Address);
                symbols.Add(new Symbol(symbol.Address, symbol.Size, symbol.Name, isThumb, isThumb is null ? -1 : section, isThumb is not null && symbol.Size > 0));
            }

            if (section >= 0)
            {
                mappingSymbols.AddRange(xmapSection.Symbols
                    .Where(s => s.Name is ARMCode or ThumbCode)
                    .Select(s => new MappingSymbol(section, s.Address, s.Name == ThumbCode)));
            }
        }

        return (sections, symbols, mappingSymbols);
    }

    private static bool? StateAt((uint Address, string Name)[] mapping, uint address)
    {
        int low = 0;
        int high = mapping.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (mapping[middle].Address <= address)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low == 0 ? null : mapping[low - 1].Name switch
        {
            ARMCode => false,
            ThumbCode => true,
            _ => null,
        };
    }
}
