using System.Buffers.Binary;
using NDSRecomp.ARM;

namespace NDSRecomp;

internal enum CallResolution
{
    Match,
    CreateStatic,
    NoMatch,
    Ambiguous,
}

internal sealed record Function(string Name, uint Address, int Section, uint ROMAddress, uint Size, bool IsThumb, bool IsNoreturn, bool ReturnsTwice)
{
    public uint End => Address + Size;
}

internal sealed class Context
{
    private readonly List<Function> _functions;
    private readonly Dictionary<(int Section, uint Address), Function> _functionsBySection;
    private readonly Dictionary<uint, List<Function>> _functionsByAddress;
    private readonly Function[] _declaredFunctions;
    private readonly Dictionary<(int Section, uint Address), string> _labels;
    private readonly Dictionary<int, SortedSet<uint>> _pcRelativeAddresses = [];

    private Context(byte[] rom, IReadOnlyList<Section> sections, List<Function> functions, Dictionary<(int, uint), string> labels, List<Symbol> dataSymbols, InputConfig input, PatchesConfig patches, Processor processor)
    {
        ROM = rom;
        Sections = sections;
        DataSymbols = dataSymbols;
        Processor = processor;
        _functions = functions;
        _functionsBySection = functions.ToDictionary(f => (f.Section, f.Address));
        _functionsByAddress = functions.GroupBy(f => f.Address).ToDictionary(g => g.Key, g => g.ToList());
        _declaredFunctions = [.. functions];
        _labels = labels;
        TraceMode = input.TraceMode;
        RecompInclude = input.RecompInclude ?? $"using NTRModern.{processor};\nusing LibRecomp;\nusing LibRecomp.{processor};";
        Stubs = patches.Stubs.ToHashSet();
        Ignored = patches.Ignored.ToHashSet();
        Hooks = patches.Hook.ToLookup(hook => hook.Func);
    }

    public byte[] ROM { get; }

    public IReadOnlyList<Section> Sections { get; }

    public Processor Processor { get; }

    public Architecture Architecture => NDSRecomp.Sections.ArchitectureOf(Processor);

    public IReadOnlyList<Function> Functions => _functions;

    public IReadOnlyList<Symbol> DataSymbols { get; }

    public bool TraceMode { get; }

    public string RecompInclude { get; }

    public IReadOnlySet<string> Stubs { get; }

    public IReadOnlySet<string> Ignored { get; }

    public ILookup<string, FunctionHook> Hooks { get; }

    public Function? FindFunction(int section, uint address) => _functionsBySection.GetValueOrDefault((section, address));

    public CallResolution ResolveCall(Function caller, uint target, out Function? callee)
    {
        callee = FindFunction(caller.Section, target);
        if (callee is not null)
        {
            return CallResolution.Match;
        }

        if (Sections[caller.Section].Contains(target))
        {
            return CallResolution.CreateStatic;
        }

        var candidates = _functionsByAddress.GetValueOrDefault(target) ?? [];
        callee = candidates.Count == 1 ? candidates[0] : null;
        return candidates.Count switch
        {
            0 => CallResolution.NoMatch,
            1 => CallResolution.Match,
            _ => CallResolution.Ambiguous,
        };
    }

    public bool IsRecompiled(Function function) => !Stubs.Contains(function.Name) && !Ignored.Contains(function.Name);

    public IEnumerable<uint> PCRelativeAddressesIn(Function function)
    {
        return function.Size > 2 && _pcRelativeAddresses.TryGetValue(function.Section, out var addresses)
            ? addresses.GetViewBetween(function.Address + 1, function.End - 1)
            : [];
    }

    public ushort ReadUInt16(Function function, uint address) => BinaryPrimitives.ReadUInt16LittleEndian(ROM.AsSpan((int)(address - function.Address + function.ROMAddress)));

    public uint ReadUInt32(Function function, uint address) => BinaryPrimitives.ReadUInt32LittleEndian(ROM.AsSpan((int)(address - function.Address + function.ROMAddress)));

    public static Context Load(Config config)
    {
        var input = config.Input;
        byte[] rom = File.ReadAllBytes(input.ROMFilePath);
        var processor = Enum.Parse<Processor>(input.Processor);
        var cartridge = Cartridge.Read(rom);
        var (sections, symbols, mappingSymbols) = input.ELFPath.Length > 0
            ? Symbol.ReadELF(ELF.Read(File.ReadAllBytes(input.ELFPath)), cartridge, rom, processor)
            : Symbol.ReadXMap(XMap.Read(File.ReadAllText(input.SymbolsFilePath)), cartridge, rom, processor);

        var context = Create(rom, sections, symbols, mappingSymbols, input, config.Patches, processor);
        context.AddStaticFunctions();
        context.FindPCRelativeAddresses();
        return context;
    }

    public static Context Create(
        byte[] rom,
        IReadOnlyList<Section> sections,
        IEnumerable<Symbol> symbols,
        IReadOnlyList<MappingSymbol> mappingSymbols,
        InputConfig input,
        PatchesConfig? patches = null,
        Processor processor = Processor.ARM7)
    {
        patches ??= new PatchesConfig();
        var noreturnFuncs = input.NoreturnFuncs.ToHashSet();
        var returnsTwiceFuncs = input.ReturnsTwiceFuncs.ToHashSet();
        var sizedTwice = input.FunctionSizes.GroupBy(f => f.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (sizedTwice.Count > 0)
        {
            throw new InvalidDataException($"input.function_sizes has {string.Join(", ", sizedTwice)} more than once.");
        }

        var sizes = input.FunctionSizes.ToDictionary(f => f.Name, f => f.Size);
        var code = symbols
            .Where(s => s.IsCode)
            .Select(s => sizes.TryGetValue(s.Name, out uint size) ? s with { Size = size, IsFunction = true } : s)
            .ToList();
        foreach (var symbol in code.Where(s => s.Section < 0 || s.Section >= sections.Count || !sections[s.Section].Contains(s.Address)))
        {
            throw new InvalidDataException($"{symbol.Name} at 0x{symbol.Address:X8} is outside the section it's listed in.");
        }

        var sameCode = code.Where(s => s.IsFunction && s.Size > 0).GroupBy(s => (s.Section, s.Address, s.Size)).ToList();
        var declared = sameCode.Select(g => g.First()).ToList();
        var aliases = sameCode.SelectMany(g => g.Skip(1), (g, alias) => (alias.Name, Function: g.First().Name)).ToList();

        var functions = declared
            .Select(s => new Function(
                s.Name,
                s.Address,
                s.Section,
                sections[s.Section].ROMAddress + (s.Address - sections[s.Section].Address),
                s.Size,
                s.IsThumb == true,
                IsNoreturn: noreturnFuncs.Contains(s.Name),
                ReturnsTwice: returnsTwiceFuncs.Contains(s.Name)))
            .Concat(ManualFunctions(input.ManualFuncs, sections, mappingSymbols, code.Select(s => s.Name).ToHashSet(), noreturnFuncs, returnsTwiceFuncs))
            .OrderBy(f => f.Section)
            .ThenBy(f => f.Address)
            .ToList();

        var byName = functions.ToLookup(f => f.Name);
        var configNames = noreturnFuncs.Concat(returnsTwiceFuncs).Concat(sizes.Keys)
            .Concat(patches.Stubs).Concat(patches.Ignored)
            .Concat(patches.Instruction.Select(p => p.Func)).Concat(patches.Hook.Select(h => h.Func))
            .Distinct()
            .ToList();

        var aliasNames = aliases.Where(a => configNames.Contains(a.Name)).ToList();
        if (aliasNames.Count > 0)
        {
            throw new InvalidDataException($"The config names functions by an alias: {string.Join(", ", aliasNames.Select(a => $"use {a.Function} for {a.Name}"))}.");
        }

        var unknownNames = configNames.Where(n => !byName.Contains(n)).ToList();
        if (unknownNames.Count > 0)
        {
            throw new InvalidDataException($"The config names functions that do not exist: {string.Join(", ", unknownNames)}. Check the spelling against the symbols.");
        }

        var ambiguousNames = configNames.Where(n => byName[n].Count() > 1).ToList();
        if (ambiguousNames.Count > 0)
        {
            throw new InvalidDataException($"The config names more than one function with each of these: {string.Join(", ", ambiguousNames)}.");
        }

        for (int i = 0; i < functions.Count; i++)
        {
            var function = functions[i];
            if ((ulong)function.Address + function.Size > sections[function.Section].End)
            {
                throw new InvalidDataException($"{function.Name} runs past the end of the {sections[function.Section].Name} section. Check its size.");
            }

            if (i > 0 && functions[i - 1].Section == function.Section && functions[i - 1].Address == function.Address)
            {
                throw new InvalidDataException($"{functions[i - 1].Name} and {function.Name} have the same address.");
            }
        }

        foreach (var patch in patches.Instruction)
        {
            var function = byName[patch.Func].Single();
            int size = function.IsThumb ? 2 : 4;
            if (patch.Address < function.Address || patch.Address + size > function.End || patch.Address % size != 0)
            {
                throw new InvalidDataException($"The instruction patch at 0x{patch.Address:X8} is not an instruction of {function.Name}.");
            }

            if (function.IsThumb && patch.Value > ushort.MaxValue)
            {
                throw new InvalidDataException($"The instruction patch at 0x{patch.Address:X8} is in Thumb code, so its value has to fit in 16 bits.");
            }

            var bytes = rom.AsSpan((int)(patch.Address - function.Address + function.ROMAddress), size);
            if (size == 2)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)patch.Value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, patch.Value);
            }
        }

        foreach (var hook in patches.Hook)
        {
            var function = byName[hook.Func].Single();
            if (hook.BeforeAddress != 0 && (hook.BeforeAddress < function.Address || hook.BeforeAddress >= function.End))
            {
                throw new InvalidDataException($"The hook at 0x{hook.BeforeAddress:X8} is outside {function.Name}.");
            }
        }

        var labels = code.Where(s => !s.IsFunction || s.Size == 0).GroupBy(s => (s.Section, s.Address)).ToDictionary(g => g.Key, g => g.First().Name);
        var dataSymbols = symbols
            .Where(s => !s.IsCode && !byName.Contains(s.Name))
            .DistinctBy(s => (s.Name, s.Address))
            .OrderBy(s => s.Address)
            .ToList();

        return new Context(rom, sections, functions, labels, dataSymbols, input, patches, processor);
    }

    private static IEnumerable<Function> ManualFunctions(
        List<ManualFunction> manualFuncs,
        IReadOnlyList<Section> sections,
        IReadOnlyList<MappingSymbol> mappingSymbols,
        HashSet<string> symbolNames,
        HashSet<string> noreturnFuncs,
        HashSet<string> returnsTwiceFuncs)
    {
        foreach (var manual in manualFuncs)
        {
            if (manual.Name.Length == 0 || manual.Section.Length == 0 || manual.Size == 0)
            {
                throw new InvalidDataException("input.manual_funcs has an entry without a name, section or size.");
            }

            if (symbolNames.Contains(manual.Name))
            {
                throw new InvalidDataException($"input.manual_funcs has {manual.Name}, which the symbols already have.");
            }

            var section = sections.FirstOrDefault(s => s.Name == manual.Section)
                ?? throw new InvalidDataException($"input.manual_funcs puts {manual.Name} in the {manual.Section} section, which doesn't exist.");
            if (!section.Contains(manual.Address))
            {
                throw new InvalidDataException($"input.manual_funcs puts {manual.Name} outside the {manual.Section} section.");
            }

            var before = mappingSymbols.Where(m => m.Section == section.Index && m.Address <= manual.Address).ToList();
            if (before.Count == 0)
            {
                throw new InvalidDataException($"{manual.Name} has no $a or $t mapping symbol at or before it.");
            }

            var state = before.MaxBy(m => m.Address);

            uint instructionSize = state.IsThumb ? 2u : 4u;
            if (manual.Address % instructionSize != 0 || manual.Size % instructionSize != 0)
            {
                throw new InvalidDataException($"input.manual_funcs gives {manual.Name} an address or size that isn't a whole number of {(state.IsThumb ? "Thumb" : "ARM")} instructions.");
            }

            yield return new Function(
                manual.Name,
                manual.Address,
                section.Index,
                section.ROMAddress + (manual.Address - section.Address),
                manual.Size,
                state.IsThumb,
                IsNoreturn: noreturnFuncs.Contains(manual.Name),
                ReturnsTwice: returnsTwiceFuncs.Contains(manual.Name));
        }
    }

    public void AddStaticFunctions()
    {
        var pending = new Queue<Function>(_functions);
        while (pending.TryDequeue(out var caller))
        {
            if (!IsRecompiled(caller))
            {
                continue;
            }

            foreach (var (target, isThumb) in FunctionAnalysis.Analyze(this, caller).UnknownTargets)
            {
                if (ResolveCall(caller, target, out _) != CallResolution.CreateStatic)
                {
                    continue;
                }

                var section = Sections[caller.Section];
                var function = new Function(
                    _labels.GetValueOrDefault((section.Index, target), $"static_{section.Index}_{target:X8}"),
                    target,
                    section.Index,
                    section.ROMAddress + (target - section.Address),
                    StaticFunctionEnd(section, target) - target,
                    isThumb,
                    IsNoreturn: false,
                    ReturnsTwice: false);

                int index = _functions.BinarySearch(function, Comparer<Function>.Create((a, b) => (a.Section, a.Address).CompareTo((b.Section, b.Address))));
                _functions.Insert(~index, function);
                _functionsBySection.Add((function.Section, target), function);
                if (!_functionsByAddress.TryGetValue(target, out var atAddress))
                {
                    _functionsByAddress[target] = atAddress = [];
                }

                atAddress.Add(function);
                pending.Enqueue(function);
            }
        }
    }

    public void FindPCRelativeAddresses()
    {
        foreach (var function in _functions.Where(IsRecompiled))
        {
            foreach (var instruction in FunctionAnalysis.Analyze(this, function).Instructions)
            {
                if (instruction.PCRelativeAddress is not { } address)
                {
                    continue;
                }

                if (!_pcRelativeAddresses.TryGetValue(function.Section, out var addresses))
                {
                    _pcRelativeAddresses[function.Section] = addresses = [];
                }

                addresses.Add(address & ~1u);
            }
        }
    }

    private uint StaticFunctionEnd(Section section, uint address)
    {
        var around = _declaredFunctions.LastOrDefault(f => f.Section == section.Index && f.Address < address && f.End > address);
        if (around is not null)
        {
            return around.End;
        }

        var next = _declaredFunctions.FirstOrDefault(f => f.Section == section.Index && f.Address > address);
        return next?.Address ?? section.End;
    }
}
