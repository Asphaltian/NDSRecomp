using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace NDSRecomp;

/// <summary>
/// The TOML config you run NDSRecomp with. Each key is named after its property in snake_case, so
/// <see cref="InputConfig.ROMFilePath"/> becomes <c>rom_file_path</c>.
/// </summary>
public sealed class Config
{
    private static readonly JsonNamingPolicy NamingPolicy = JsonNamingPolicy.SnakeCaseLower;

    public InputConfig Input { get; set; } = new();

    public PatchesConfig Patches { get; set; } = new();

    /// <summary>
    /// Reads a config file. Keep in mind that every path in it is relative to the config file itself,
    /// not to where you run NDSRecomp from.
    /// </summary>
    public static Config Load(string path)
    {
        string text = File.ReadAllText(path);
        var table = TomlSerializer.Deserialize<TomlTable>(text) ?? throw new InvalidDataException($"{path} is empty.");
        var unknownKeys = new List<string>();
        FindUnknownKeys(table, typeof(Config), "", unknownKeys);
        if (unknownKeys.Count > 0)
        {
            throw new InvalidDataException($"{path} has keys NDSRecomp doesn't know: {string.Join(", ", unknownKeys)}.");
        }

        var options = new TomlSerializerOptions { PropertyNamingPolicy = NamingPolicy };
        var config = TomlSerializer.Deserialize<Config>(text, options)!;

        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var input = config.Input;
        if (input.ELFPath.Length > 0 && input.SymbolsFilePath.Length > 0)
        {
            throw new InvalidDataException("The config can't have both input.elf_path and input.symbols_file_path.");
        }

        if (input.ELFPath.Length == 0 && input.SymbolsFilePath.Length == 0)
        {
            throw new InvalidDataException("The config needs input.elf_path or input.symbols_file_path.");
        }

        input.ROMFilePath = Resolve(directory, input.ROMFilePath, "input.rom_file_path");
        input.ELFPath = input.ELFPath.Length > 0 ? Path.GetFullPath(input.ELFPath, directory) : "";
        input.SymbolsFilePath = input.SymbolsFilePath.Length > 0 ? Path.GetFullPath(input.SymbolsFilePath, directory) : "";
        input.OutputFuncPath = Resolve(directory, input.OutputFuncPath, "input.output_func_path");

        if (input.Processor is not ("ARM9" or "ARM7"))
        {
            throw new InvalidDataException("input.processor has to be \"ARM9\" or \"ARM7\".");
        }

        if (input.FunctionsPerOutputFile <= 0)
        {
            throw new InvalidDataException("input.functions_per_output_file has to be more than 0.");
        }

        return config;
    }

    private static void FindUnknownKeys(TomlTable table, Type type, string path, List<string> unknownKeys)
    {
        var properties = type.GetProperties().ToDictionary(p => NamingPolicy.ConvertName(p.Name), p => p.PropertyType);
        foreach (var (key, value) in table)
        {
            string name = path.Length == 0 ? key : $"{path}.{key}";
            if (!properties.TryGetValue(key, out var propertyType))
            {
                unknownKeys.Add(name);
                continue;
            }

            var elementType = propertyType.IsGenericType ? propertyType.GetGenericArguments()[0] : propertyType;
            IEnumerable<TomlTable> children = value switch
            {
                TomlTable child => [child],
                TomlTableArray tables => tables,
                TomlArray array => array.OfType<TomlTable>(),
                _ => [],
            };

            foreach (var child in children)
            {
                FindUnknownKeys(child, value is TomlTable ? propertyType : elementType, name, unknownKeys);
            }
        }
    }

    private static string Resolve(string directory, string path, string key)
    {
        if (path.Length == 0)
        {
            throw new InvalidDataException($"{key} is required.");
        }

        return Path.GetFullPath(path, directory);
    }
}

/// <summary>The <c>[input]</c> table, which tells NDSRecomp what to recompile and where to put the result.</summary>
public sealed class InputConfig
{
    /// <summary><c>rom_file_path</c>: the ROM you want to recompile.</summary>
    public string ROMFilePath { get; set; } = "";

    /// <summary>
    /// <c>elf_path</c>: the ELF file a decompilation builds for the processor you're recompiling, with
    /// every overlay in it. It has to be built from sources that match the ROM exactly.
    /// </summary>
    public string ELFPath { get; set; } = "";

    /// <summary>
    /// <c>symbols_file_path</c>: the .xMAP file the linker writes when a decompilation is built for the
    /// processor you're recompiling. Use this or <c>elf_path</c>, not both.
    /// </summary>
    public string SymbolsFilePath { get; set; } = "";

    /// <summary><c>processor</c>: which of the two processors to recompile the code of, <c>"ARM9"</c> or <c>"ARM7"</c>.</summary>
    public string Processor { get; set; } = "";

    /// <summary><c>output_func_path</c>: the folder the generated C# files go in.</summary>
    public string OutputFuncPath { get; set; } = "";

    /// <summary><c>functions_per_output_file</c>: how many functions go in each generated file. It's 50 if you leave it out.</summary>
    public int FunctionsPerOutputFile { get; set; } = 50;

    /// <summary>
    /// <c>recomp_include</c>: the using directives at the top of every generated file. If your hooks
    /// need other namespaces, add them here, and keep the ones for your processor. For the ARM9 those
    /// are <c>using NTRModern.ARM9;</c>, <c>using LibRecomp;</c> and <c>using LibRecomp.ARM9;</c>,
    /// which is what you get if you leave it out.
    /// </summary>
    public string? RecompInclude { get; set; }

    /// <summary>
    /// <c>trace_mode</c>: makes every function call <c>Funcs.TraceEntry(name)</c> as it starts, which
    /// is handy for finding out what the game is doing. Don't forget to implement that partial method
    /// in the project you build the output in.
    /// </summary>
    public bool TraceMode { get; set; }

    /// <summary><c>noreturn_funcs</c>: functions that never return. Whatever comes after a call to one of these isn't treated as code.</summary>
    public List<string> NoreturnFuncs { get; set; } = [];

    /// <summary>
    /// <c>returns_twice_funcs</c>: functions that can return to where they were called a second time, like
    /// <c>setjmp</c>. The code after a call to one of these can be reached again later from somewhere else.
    /// </summary>
    public List<string> ReturnsTwiceFuncs { get; set; } = [];

    /// <summary><c>manual_funcs</c>: functions the symbols don't have.</summary>
    public List<ManualFunction> ManualFuncs { get; set; } = [];

    /// <summary><c>function_sizes</c>: sizes to use instead of the ones in the symbols, for when those are wrong.</summary>
    public List<FunctionSize> FunctionSizes { get; set; } = [];
}

/// <summary>One entry of <c>manual_funcs</c>.</summary>
public sealed class ManualFunction
{
    public string Name { get; set; } = "";

    /// <summary><c>section</c>: the section the function is in, named the way your xMAP or ELF names it, like <c>main</c>, <c>ITCM</c> or <c>OVY_0</c>.</summary>
    public string Section { get; set; } = "";

    public uint Address { get; set; }

    public uint Size { get; set; }
}

/// <summary>One entry of <c>function_sizes</c>.</summary>
public sealed class FunctionSize
{
    public string Name { get; set; } = "";

    public uint Size { get; set; }
}

/// <summary>The <c>[patches]</c> table, for changing the game while it gets recompiled.</summary>
public sealed class PatchesConfig
{
    /// <summary><c>stubs</c>: functions to recompile as empty ones that do nothing.</summary>
    public List<string> Stubs { get; set; } = [];

    /// <summary>
    /// <c>ignored</c>: functions to leave out of the recompile completely. Calling one of these throws,
    /// so make sure something replaces it through <c>Funcs.Patches</c>.
    /// </summary>
    public List<string> Ignored { get; set; } = [];

    /// <summary><c>[[patches.instruction]]</c>: instructions to change before recompiling.</summary>
    public List<InstructionPatch> Instruction { get; set; } = [];

    /// <summary><c>[[patches.hook]]</c>: your own C# code to put inside recompiled functions.</summary>
    public List<FunctionHook> Hook { get; set; } = [];
}

/// <summary>One entry of <c>[[patches.instruction]]</c>.</summary>
public sealed class InstructionPatch
{
    /// <summary><c>func</c>: the function the instruction is in.</summary>
    public string Func { get; set; } = "";

    /// <summary><c>address</c>: where the instruction is.</summary>
    public uint Address { get; set; }

    /// <summary><c>value</c>: the new instruction. It's a halfword in Thumb code and a word in ARM code.</summary>
    public uint Value { get; set; }
}

/// <summary>One entry of <c>[[patches.hook]]</c>.</summary>
public sealed class FunctionHook
{
    /// <summary><c>func</c>: the function to put your code in.</summary>
    public string Func { get; set; } = "";

    /// <summary>
    /// <c>before_address</c>: the instruction your code runs before. Leave it out and your code runs at
    /// the start of the function.
    /// </summary>
    public uint BeforeAddress { get; set; }

    /// <summary><c>text</c>: your code. The CPU's registers are in <c>ctx</c>.</summary>
    public string Text { get; set; } = "";
}
