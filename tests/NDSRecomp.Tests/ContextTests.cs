using static NDSRecomp.Tests.TestCode;

namespace NDSRecomp.Tests;

public class ContextTests
{
    private const uint OverlayAddress = 0x02100000;

    private static readonly ushort[] CallIntoAnotherFunction =
    [
        0xB500,         // 00: push {lr}
        0xF000, 0xF802, // 02: bl 0x0a
        0xBD00,         // 06: pop {pc}
        0x2000,         // 08: movs r0, #0
        0x4770,         // 0a: bx lr
    ];

    private static readonly ushort[] CallIntoTheOverlays =
    [
        0xB500,         // 00: push {lr}
        0xF0FF, 0xFFFD, // 02: bl 0x02100000
        0xBD00,         // 06: pop {pc}
    ];

    private static readonly ushort[] ReturnAtTheOverlayAddress =
    [
        0x4770,         // 00: bx lr
    ];

    private static Context CreateOverlayContext(int overlayCount)
    {
        byte[] main = Bytes(CallIntoTheOverlays);
        byte[] overlay = Bytes(ReturnAtTheOverlayAddress);
        var sections = new List<Section> { new(0, "main", Base, (uint)main.Length, 0) };
        var symbols = new List<Symbol> { ThumbFunction(Base, (uint)main.Length, "Caller") };
        byte[] rom = [.. main];
        for (int i = 0; i < overlayCount; i++)
        {
            sections.Add(new Section(i + 1, $"OVY_{i}", OverlayAddress, (uint)overlay.Length, (uint)rom.Length, (uint)i));
            symbols.Add(ThumbFunction(OverlayAddress, (uint)overlay.Length, $"Overlay{i}", section: i + 1));
            rom = [.. rom, .. overlay];
        }

        return Context.Create(rom, sections, symbols, [], new InputConfig());
    }

    [Fact]
    public void CalledLabelBecomesAFunctionNamedAfterIt()
    {
        var context = CreateContext(
            CallIntoAnotherFunction,
            [ThumbFunction(Base, 8, "Caller"), ThumbFunction(Base + 8, 4, "Outer"), ThumbLabel(Base + 0x0A, "inner")]);

        context.AddStaticFunctions();

        var inner = context.FindFunction(0, Base + 0x0A);
        Assert.Equal(new Function("inner", Base + 0x0A, 0, 0x0A, 2, IsThumb: true, IsNoreturn: false, ReturnsTwice: false), inner);
        Assert.Empty(FunctionAnalysis.Analyze(context, context.Functions[0]).Errors);
    }

    [Fact]
    public void CalledAddressWithoutALabelIsNamedAfterItsSectionAndAddress()
    {
        var context = CreateContext(CallIntoAnotherFunction, [ThumbFunction(Base, 8, "Caller"), ThumbFunction(Base + 8, 4, "Outer")]);

        context.AddStaticFunctions();

        Assert.Equal("static_0_0200000A", context.FindFunction(0, Base + 0x0A)?.Name);
    }

    [Fact]
    public void CalledAddressBetweenFunctionsEndsAtTheNextOne()
    {
        var context = CreateContext(
            [
                0xB500,         // 00: push {lr}
                0xF000, 0xF801, // 02: bl 0x08
                0xBD00,         // 06: pop {pc}
                0x2000,         // 08: movs r0, #0
                0x4770,         // 0a: bx lr
                0x4770,         // 0c: bx lr
            ],
            [ThumbFunction(Base, 8, "Caller"), ThumbFunction(Base + 0x0C, 2, "Next")]);

        context.AddStaticFunctions();

        Assert.Equal(Base + 0x0C, context.FindFunction(0, Base + 8)?.End);
    }

    [Fact]
    public void CallToAnAddressOnlyOneOverlayHasGoesStraightToIt()
    {
        var context = CreateOverlayContext(overlayCount: 1);

        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);

        Assert.Empty(analysis.Errors);
        Assert.Equal("Overlay0", analysis.Flows[Base + 2].Callee?.Name);
    }

    [Fact]
    public void CallToAnAddressOverlaysShareIsLookedUpWhenItRuns()
    {
        var context = CreateOverlayContext(overlayCount: 2);

        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);

        Assert.Empty(analysis.Errors);
        Assert.Null(analysis.Flows[Base + 2].Callee);
        Assert.Contains("Recomp.LookupFunc(0x02100000u)(ctx);", new CSharpGenerator(context).Generate(analysis));
    }

    [Fact]
    public void CallInsideAnOverlayGoesToItsOwnFunction()
    {
        byte[] overlay = Bytes(
        [
            0xB500,         // 00: push {lr}
            0xF000, 0xF801, // 02: bl 0x02100008
            0xBD00,         // 06: pop {pc}
            0x4770,         // 08: bx lr
        ]);
        Section[] sections =
        [
            new(0, "OVY_0", OverlayAddress, (uint)overlay.Length, 0, 0),
            new(1, "OVY_1", OverlayAddress, (uint)overlay.Length, (uint)overlay.Length, 1),
        ];
        Symbol[] symbols =
        [
            ThumbFunction(OverlayAddress, 8, "Caller0", section: 0),
            ThumbFunction(OverlayAddress + 8, 2, "Callee0", section: 0),
            ThumbFunction(OverlayAddress, 8, "Caller1", section: 1),
            ThumbFunction(OverlayAddress + 8, 2, "Callee1", section: 1),
        ];
        var context = Context.Create([.. overlay, .. overlay], sections, symbols, [], new InputConfig());

        var analysis = FunctionAnalysis.Analyze(context, context.Functions.Single(f => f.Name == "Caller1"));

        Assert.Equal("Callee1", analysis.Flows[OverlayAddress + 2].Callee?.Name);
    }

    [Fact]
    public void FunctionWithoutASizeIsOnlyALabel()
    {
        var context = CreateContext([0x2000, 0x4770, 0x4770], [ThumbFunction(Base, 0, "Unsized"), ThumbFunction(Base + 4, 2, "Last")]);

        Assert.Equal(["Last"], context.Functions.Select(f => f.Name));
    }

    [Fact]
    public void FunctionSizesMakeALabelAFunction()
    {
        var input = new InputConfig { FunctionSizes = [new FunctionSize { Name = "Pointed", Size = 4 }] };

        var context = CreateContext([0x2000, 0x4770], [ThumbLabel(Base, "Pointed")], input);

        Assert.Equal(new Function("Pointed", Base, 0, 0, 4, IsThumb: true, IsNoreturn: false, ReturnsTwice: false), Assert.Single(context.Functions));
    }

    [Fact]
    public void CodeOutsideItsSectionIsAnError()
    {
        var error = Assert.Throws<InvalidDataException>(() => CreateContext([0x4770], [ThumbFunction(Base, 2, "Inside"), ThumbLabel(Base + 0x10, "Outside")]));

        Assert.Equal("Outside at 0x02000010 is outside the section it's listed in.", error.Message);
    }

    [Fact]
    public void SymbolsOutsideTheCodeAreData()
    {
        var context = CreateContext(
            CallIntoAnotherFunction,
            [
                ThumbFunction(Base, 8, "Caller"),
                ThumbFunction(Base + 8, 4, "Outer"),
                ThumbLabel(Base + 0x0A, "inner"),
                new Symbol(0x02200000, 0, "sTable"),
                new Symbol(0x02200010, 0, "gCounter"),
                new Symbol(0x02200010, 0, "gCounter"),
            ]);

        Assert.Equal([new Symbol(0x02200000, 0, "sTable"), new Symbol(0x02200010, 0, "gCounter")], context.DataSymbols);
    }

    [Fact]
    public void FunctionSizesReplaceTheSymbolSize()
    {
        var input = new InputConfig { FunctionSizes = [new FunctionSize { Name = "Short", Size = 4 }] };

        var context = CreateContext([0x2000, 0x4770], [ThumbFunction(Base, 2, "Short")], input);

        Assert.Equal(4u, context.Functions[0].Size);
    }

    [Fact]
    public void FunctionPastTheEndOfItsSectionIsAnError()
    {
        var error = Assert.Throws<InvalidDataException>(() => CreateContext([0x2000, 0x4770], [ThumbFunction(Base, 8, "Long")]));

        Assert.Equal("Long runs past the end of the main section. Check its size.", error.Message);
    }

    [Fact]
    public void InstructionPatchReplacesTheInstruction()
    {
        var patches = new PatchesConfig { Instruction = [new InstructionPatch { Func = "Zero", Address = Base, Value = 0x2001 }] };

        var context = CreateContext([0x2000, 0x4770], [ThumbFunction(Base, 4, "Zero")], patches: patches);

        Assert.Equal(0x2001, context.ReadUInt16(context.Functions[0], Base));
    }

    [Theory]
    [InlineData(Base + 1, 0x2001u)]
    [InlineData(Base + 4, 0x2001u)]
    [InlineData(Base, 0x12001u)]
    public void InstructionPatchHasToFitAnInstructionOfTheFunction(uint address, uint value)
    {
        var patches = new PatchesConfig { Instruction = [new InstructionPatch { Func = "Zero", Address = address, Value = value }] };

        Assert.Throws<InvalidDataException>(() => CreateContext([0x2000, 0x4770], [ThumbFunction(Base, 4, "Zero")], patches: patches));
    }

    [Fact]
    public void ConfigNamingAnUnknownFunctionIsAnError()
    {
        var patches = new PatchesConfig { Stubs = ["Missing"] };

        Assert.Throws<InvalidDataException>(() => CreateContext([0x4770], [ThumbFunction(Base, 2, "Present")], patches: patches));
    }

    [Fact]
    public void ConfigNamingTwoFunctionsWithOneNameIsAnError()
    {
        var patches = new PatchesConfig { Ignored = ["Twice"] };

        Assert.Throws<InvalidDataException>(() => CreateContext([0x4770, 0x4770], [ThumbFunction(Base, 2, "Twice"), ThumbFunction(Base + 2, 2, "Twice")], patches: patches));
    }

    [Fact]
    public void AnAliasIsTheSameFunction()
    {
        var context = CreateContext([0x2000, 0x4770], [ThumbFunction(Base, 4, "Original"), ThumbFunction(Base, 4, "Alias")]);

        Assert.Equal("Original", Assert.Single(context.Functions).Name);
    }

    [Fact]
    public void ConfigNamingAFunctionByItsAliasIsAnError()
    {
        var input = new InputConfig { NoreturnFuncs = ["Alias"] };

        var error = Assert.Throws<InvalidDataException>(() => CreateContext([0x2000, 0x4770], [ThumbFunction(Base, 4, "Original"), ThumbFunction(Base, 4, "Alias")], input));

        Assert.Contains("use Original for Alias", error.Message);
    }

    [Fact]
    public void SizingAFunctionTwiceIsAnError()
    {
        var input = new InputConfig { FunctionSizes = [new FunctionSize { Name = "Twice", Size = 2 }, new FunctionSize { Name = "Twice", Size = 4 }] };

        Assert.Throws<InvalidDataException>(() => CreateContext([0x4770, 0x4770], [ThumbFunction(Base, 2, "Twice")], input));
    }

    [Fact]
    public void ManualFunctionTakesItsStateFromTheMappingSymbols()
    {
        var input = new InputConfig { ManualFuncs = [new ManualFunction { Name = "Unnamed", Section = "main", Address = Base + 2, Size = 2 }] };

        var context = CreateContext([0x2000, 0x4770], [ThumbFunction(Base, 2, "Zero")], input, mappingSymbols: [new MappingSymbol(0, Base, IsThumb: true)]);

        Assert.Equal(new Function("Unnamed", Base + 2, 0, 2, 2, IsThumb: true, IsNoreturn: false, ReturnsTwice: false), context.FindFunction(0, Base + 2));
    }

    [Fact]
    public void ManualFunctionInASectionThatDoesNotExistIsAnError()
    {
        var input = new InputConfig { ManualFuncs = [new ManualFunction { Name = "Unnamed", Section = "OVY_9", Address = Base, Size = 2 }] };

        var error = Assert.Throws<InvalidDataException>(() => CreateContext([0x4770], [], input, mappingSymbols: [new MappingSymbol(0, Base, IsThumb: true)]));

        Assert.Equal("input.manual_funcs puts Unnamed in the OVY_9 section, which doesn't exist.", error.Message);
    }

    [Fact]
    public void ManualFunctionWithASymbolNameIsAnError()
    {
        var input = new InputConfig { ManualFuncs = [new ManualFunction { Name = "Taken", Section = "main", Address = Base + 2, Size = 2 }] };

        var error = Assert.Throws<InvalidDataException>(() => CreateContext([0x4770, 0x4770], [ThumbFunction(Base, 2, "Taken")], input, mappingSymbols: [new MappingSymbol(0, Base, IsThumb: true)]));

        Assert.Equal("input.manual_funcs has Taken, which the symbols already have.", error.Message);
    }

    [Fact]
    public void ManualFunctionWithoutAMappingSymbolBeforeItIsAnError()
    {
        var input = new InputConfig { ManualFuncs = [new ManualFunction { Name = "Unnamed", Section = "main", Address = Base, Size = 2 }] };

        var error = Assert.Throws<InvalidDataException>(() => CreateContext([0x4770], [], input));

        Assert.Equal("Unnamed has no $a or $t mapping symbol at or before it.", error.Message);
    }

    [Fact]
    public void ManualFunctionThatIsNotWholeInstructionsIsAnError()
    {
        var input = new InputConfig { ManualFuncs = [new ManualFunction { Name = "Unnamed", Section = "main", Address = Base, Size = 6 }] };

        var error = Assert.Throws<InvalidDataException>(() => CreateContext([0xFF1E, 0xE12F, 0xFF1E, 0xE12F], [], input, mappingSymbols: [new MappingSymbol(0, Base, IsThumb: false)]));

        Assert.Equal("input.manual_funcs gives Unnamed an address or size that isn't a whole number of ARM instructions.", error.Message);
    }
}
