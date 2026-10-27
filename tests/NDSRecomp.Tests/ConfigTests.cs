namespace NDSRecomp.Tests;

public class ConfigTests : IDisposable
{
    private const string Minimal = """
        [input]
        rom_file_path = "game.nds"
        elf_path = "main.elf"
        processor = "ARM9"
        output_func_path = "out"

        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("config").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string toml)
    {
        string path = Path.Combine(_folder, "game.toml");
        File.WriteAllText(path, toml);
        return path;
    }

    [Fact]
    public void PathsAreRelativeToTheConfig()
    {
        var config = Config.Load(Write(Minimal));

        Assert.Equal(Path.Combine(_folder, "game.nds"), config.Input.ROMFilePath);
        Assert.Equal(Path.Combine(_folder, "main.elf"), config.Input.ELFPath);
    }

    [Fact]
    public void UnknownKeysAreErrors()
    {
        string toml = Minimal + """
            text_address = 0x02000000

            [[patches.hook]]
            func = "Foo"
            txt = "Hooked(ctx);"
            """;

        var error = Assert.Throws<InvalidDataException>(() => Config.Load(Write(toml)));

        Assert.Contains("input.text_address", error.Message);
        Assert.Contains("patches.hook.txt", error.Message);
    }

    [Fact]
    public void SymbolsFilePathIsRelativeToTheConfig()
    {
        string toml = Minimal.Replace("elf_path = \"main.elf\"", "symbols_file_path = \"main.nef.xMAP\"");

        Assert.Equal(Path.Combine(_folder, "main.nef.xMAP"), Config.Load(Write(toml)).Input.SymbolsFilePath);
    }

    [Fact]
    public void AnELFOrASymbolsFileIsRequired()
    {
        string toml = Minimal.Replace("elf_path = \"main.elf\"", "");

        var error = Assert.Throws<InvalidDataException>(() => Config.Load(Write(toml)));

        Assert.Equal("The config needs input.elf_path or input.symbols_file_path.", error.Message);
    }

    [Fact]
    public void AnELFAndASymbolsFileTogetherAreAnError()
    {
        string toml = Minimal + "symbols_file_path = \"main.nef.xMAP\"\n";

        var error = Assert.Throws<InvalidDataException>(() => Config.Load(Write(toml)));

        Assert.Equal("The config can't have both input.elf_path and input.symbols_file_path.", error.Message);
    }

    [Fact]
    public void TheProcessorIsARM9OrARM7()
    {
        string toml = Minimal.Replace("processor = \"ARM9\"", "processor = \"ARM11\"");

        var error = Assert.Throws<InvalidDataException>(() => Config.Load(Write(toml)));

        Assert.Equal("input.processor has to be \"ARM9\" or \"ARM7\".", error.Message);
    }
}
