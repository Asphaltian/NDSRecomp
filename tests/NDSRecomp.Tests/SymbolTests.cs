using System.Buffers.Binary;
using NDSRecomp.ARM;

namespace NDSRecomp.Tests;

public class SymbolTests
{
    private const int ARM9Offset = 0x200;
    private const int ARM7Offset = 0x600;
    private const int OverlayOffset = 0x400;
    private const int FATOffset = 0x680;
    private const int OverlayTableOffset = 0x700;
    private const string Mismatch = "Check that the symbols were built from a decompilation that matches the ROM.";

    private static byte[] ReadData(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name));

    private static byte[] CreateROM()
    {
        byte[] arm9 = ReadData("main.sbin");
        byte[] overlay = ReadData("OVY_0.sbin");
        byte[] overlayTable = ReadData("main_table.sbin");
        byte[] rom = new byte[0x800];
        void Word(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(at), value);

        arm9.CopyTo(rom, ARM9Offset);
        overlay.CopyTo(rom, OverlayOffset);
        overlayTable.CopyTo(rom, OverlayTableOffset);

        Word(0x20, ARM9Offset);
        Word(0x24, 0x02000004);
        Word(0x28, 0x02000000);
        Word(0x2C, (uint)arm9.Length);
        Word(0x30, ARM7Offset);
        Word(0x34, 0x02380000);
        Word(0x38, 0x02380000);
        Word(0x3C, 4);
        Word(0x48, FATOffset);
        Word(0x4C, 8);
        Word(0x50, OverlayTableOffset);
        Word(0x54, (uint)overlayTable.Length);
        Word(FATOffset, OverlayOffset);
        Word(FATOffset + 4, OverlayOffset + (uint)overlay.Length);
        return rom;
    }

    private static (List<Section> Sections, List<Symbol> Symbols, List<MappingSymbol> MappingSymbols) Read(bool fromELF, byte[] rom)
    {
        return fromELF
            ? Symbol.ReadELF(ELF.Read(ReadData("main.elf")), Cartridge.Read(rom), rom, Processor.ARM9)
            : Symbol.ReadXMap(XMap.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "main.nef.xMAP"))), Cartridge.Read(rom), rom, Processor.ARM9);
    }

    private static Context CreateContext(bool fromELF)
    {
        byte[] rom = CreateROM();
        var (sections, symbols, mappingSymbols) = Read(fromELF, rom);
        var context = Context.Create(rom, sections, symbols, mappingSymbols, new InputConfig(), processor: Processor.ARM9);
        context.AddStaticFunctions();
        return context;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CodeSectionsAreFoundInTheROM(bool fromELF)
    {
        var (sections, _, _) = Read(fromELF, CreateROM());

        Assert.Equal(
            [
                new Section(0, "main", 0x02000000, 0xA0, ARM9Offset),
                new Section(1, "ITCM", 0x01FF8000, 0x20, ARM9Offset + 0xA0),
                new Section(2, "OVY_0", 0x020000A0, 0x20, OverlayOffset, OverlayID: 0),
            ],
            sections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FunctionsAnalyzeInTheirOwnState(bool fromELF)
    {
        var context = CreateContext(fromELF);

        Assert.Equal(
            [
                (false, "_start"),
                (false, "_start_AutoloadDoneCallback"),
                (false, "NitroMain"),
                (true, "ThumbMain"),
                (true, "ThumbHelper"),
                (false, "Unsized"),
                (false, "FastCopy"),
                (true, "OverlayFunc"),
            ],
            context.Functions.Where(f => f.Name != "SVC_SoftReset").Select(f => (f.IsThumb, f.Name)));
        Assert.All(context.Functions, f => Assert.Empty(FunctionAnalysis.Analyze(context, f).Errors));
    }

    [Fact]
    public void XMapFindsCodeTheELFGivesNoType()
    {
        Assert.Contains("SVC_SoftReset", CreateContext(fromELF: false).Functions.Select(f => f.Name));
        Assert.DoesNotContain("SVC_SoftReset", CreateContext(fromELF: true).Functions.Select(f => f.Name));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DataInCodeIsData(bool fromELF)
    {
        Assert.Contains("_start_ModuleParams", CreateContext(fromELF).DataSymbols.Select(s => s.Name));
    }

    [Fact]
    public void XMapTellsDataAfterCodeApartButTheELFCannot()
    {
        Assert.Contains("SomeData", CreateContext(fromELF: false).DataSymbols.Select(s => s.Name));
        Assert.DoesNotContain("SomeData", CreateContext(fromELF: true).DataSymbols.Select(s => s.Name));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverlayThatIsNotInTheROMIsAnError(bool fromELF)
    {
        byte[] rom = CreateROM();
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(OverlayTableOffset + 4), 0x02100000);

        var error = Assert.Throws<InvalidDataException>(() => Read(fromELF, rom));

        Assert.Equal($"The OVY_0 section isn't where overlay 0 is in the ROM. {Mismatch}", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AutoloadThatIsNotInTheROMIsAnError(bool fromELF)
    {
        byte[] rom = CreateROM();
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(ARM9Offset + 0xC0), 0x01FF9000);

        var error = Assert.Throws<InvalidDataException>(() => Read(fromELF, rom));

        Assert.Equal($"The ROM's autoload table has nothing for the ITCM section. {Mismatch}", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaticCodeThatIsNotTheBootCodeIsAnError(bool fromELF)
    {
        byte[] rom = CreateROM();
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x28), 0x02004000);

        var error = Assert.Throws<InvalidDataException>(() => Read(fromELF, rom));

        Assert.Equal($"The main section isn't the ARM9 boot code in the ROM. {Mismatch}", error.Message);
    }

    [Fact]
    public void NonELFFileIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ELF.Read(new byte[0x100]));
    }

    [Fact]
    public void CutShortELFFileIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ELF.Read(ReadData("main.elf")[..0x100]));
    }

    [Fact]
    public void NonXMapFileIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => XMap.Read("Hello"));
    }
}
