using NDSRecomp.ARM;
using static NDSRecomp.Tests.TestCode;

namespace NDSRecomp.Tests;

public class CSharpGeneratorTests
{
    private static readonly ushort[] ReturnZero =
    [
        0x2000, // 00: movs r0, #0
        0x4770, // 02: bx lr
    ];

    private static string Generate(Context context)
    {
        return new CSharpGenerator(context).Generate(FunctionAnalysis.Analyze(context, context.Functions[0]));
    }

    private static string GenerateARM(uint instruction, Processor processor)
    {
        ushort[] code = [(ushort)instruction, (ushort)(instruction >> 16), 0xFF1E, 0xE12F];
        return Generate(CreateContext(code, [ARMFunction(Base, 8, "Test")], processor: processor));
    }

    [Fact]
    public void DataNamesAreValidAndUnique()
    {
        var context = CreateContext(
            ReturnZero,
            [ThumbFunction(Base, 4, "Zero"), new Symbol(0x02200000, 4, "gValue"), new Symbol(0x02200004, 4, "gValue"), new Symbol(0x02200008, 0, "3.table")]);

        string data = new CSharpGenerator(context).GenerateData();

        Assert.Contains("    public const uint gValue = 0x02200000;\n", data);
        Assert.Contains("    public const uint gValue_02200004 = 0x02200004;\n", data);
        Assert.Contains("    public const uint _3_table = 0x02200008;\n", data);
    }

    [Fact]
    public void NamesThatObjectAlreadyHasGetTheirAddress()
    {
        var context = CreateContext(ReturnZero, [ThumbFunction(Base, 4, "GetType"), new Symbol(0x02200000, 4, "ToString")]);
        var generator = new CSharpGenerator(context);

        Assert.Equal("GetType_02000000", generator.FunctionName(context.Functions[0]));
        Assert.Contains("    public const uint ToString_02200000 = 0x02200000;\n", generator.GenerateData());
    }

    [Fact]
    public void StubDoesNothing()
    {
        var context = CreateContext(ReturnZero, [ThumbFunction(Base, 4, "Zero")], patches: new PatchesConfig { Stubs = ["Zero"] });

        string method = new CSharpGenerator(context).GenerateUnrecompiled(context.Functions[0]);

        Assert.Contains("public static void Zero(RecompContext ctx)\n        {\n        }\n", method);
    }

    [Fact]
    public void IgnoredFunctionThrows()
    {
        var context = CreateContext(ReturnZero, [ThumbFunction(Base, 4, "Zero")], patches: new PatchesConfig { Ignored = ["Zero"] });

        string method = new CSharpGenerator(context).GenerateUnrecompiled(context.Functions[0]);

        Assert.Contains("public static void Zero(RecompContext ctx) => throw new System.NotImplementedException(", method);
    }

    private static readonly ushort[] SaveAndLoad =
    [
        0xB500,         // 00: push {lr}
        0xF000, 0xF803, // 02: bl 0x0c
        0xF000, 0xF803, // 06: bl 0x10
        0xBD00,         // 0A: pop {pc}
        0x2000,         // 0C: movs r0, #0
        0x4770,         // 0E: bx lr
        0x4770,         // 10: bx lr
        0xE7FD,         // 12: b 0x10
    ];

    private static readonly Symbol[] SaveAndLoadSymbols =
    [
        ThumbFunction(Base, 0x0C, "Caller"),
        ThumbFunction(Base + 0x0C, 4, "Save"),
        ThumbFunction(Base + 0x10, 2, "Load"),
        ThumbFunction(Base + 0x12, 2, "Tail"),
    ];

    [Fact]
    public void TheCodeAfterACallToAReturnsTwiceFunctionCanBeResumed()
    {
        var input = new InputConfig { ReturnsTwiceFuncs = ["Save"] };
        var context = CreateContext(SaveAndLoad, SaveAndLoadSymbols, input);

        string method = Generate(context);

        Assert.Contains("L_02000006:", method);
        Assert.Contains("Funcs.Load(ctx);\n                if (Recomp.IsUnwinding)\n                {\n                    if (Recomp.TryResumeAt(0x02000006u)) goto L_02000006;\n", method);
    }

    [Fact]
    public void ATailCallCarriesOnAtTheLinkRegisterItJumpedWith()
    {
        var context = CreateContext(SaveAndLoad, SaveAndLoadSymbols);

        string method = new CSharpGenerator(context).Generate(FunctionAnalysis.Analyze(context, context.Functions[3]));

        Assert.Contains("uint returnAddress = ctx.R14 & ~1u;", method);
        Assert.Contains("uint link = ctx.R14;\n                Funcs.Load(ctx);\n                Recomp.TailJump(ctx, link, returnAddress);\n", method);
    }

    [Fact]
    public void HookRunsBeforeItsInstruction()
    {
        var patches = new PatchesConfig { Hook = [new FunctionHook { Func = "Zero", BeforeAddress = Base + 2, Text = "Hooked(ctx);" }] };
        var context = CreateContext(ReturnZero, [ThumbFunction(Base, 4, "Zero")], patches: patches);

        string method = Generate(context);

        Assert.True(method.IndexOf("// 02000000:") < method.IndexOf("Hooked(ctx);"));
        Assert.True(method.IndexOf("Hooked(ctx);") < method.IndexOf("// 02000002:"));
    }

    [Fact]
    public void HookAtNoInstructionIsAnError()
    {
        var patches = new PatchesConfig { Hook = [new FunctionHook { Func = "Zero", BeforeAddress = Base + 4, Text = "Hooked(ctx);" }] };
        var context = CreateContext(
            [
                .. ReturnZero,
                0x2000, // 04: movs r0, #0
                0x4770, // 06: bx lr
            ],
            [ThumbFunction(Base, 8, "Zero")],
            patches: patches);

        Assert.Single(FunctionAnalysis.Analyze(context, context.Functions[0]).Errors);
    }

    [Fact]
    public void TraceEntryAndStartHookComeFirst()
    {
        var input = new InputConfig { TraceMode = true };
        var patches = new PatchesConfig { Hook = [new FunctionHook { Func = "Zero", Text = "Hooked(ctx);" }] };
        var context = CreateContext(ReturnZero, [ThumbFunction(Base, 4, "Zero")], input, patches);

        string method = Generate(context);

        Assert.Contains("{\n            TraceEntry(\"Zero\");\n            Hooked(ctx);\n", method);
        Assert.Contains("    static partial void TraceEntry(string function);\n", new CSharpGenerator(context).GenerateTables());
    }

    [Fact]
    public void WritingTheCPSRControlFieldChecksForInterrupts()
    {
        string method = GenerateARM(0xE121F000, Processor.ARM7); // msr cpsr_c, r0

        Assert.Contains("ctx.WriteCPSR(ctx.R0, 0b0001);\n                if (Scheduler.Cycles >= Scheduler.NextEvent) Recomp.HandleEvents(ctx, 0x02000004u);\n", method);
    }

    [Fact]
    public void AnExceptionReturnRestoresTheCPSRBeforeCarryingOn()
    {
        string method = GenerateARM(0xE25EF004, Processor.ARM9); // subs pc, lr, #4

        Assert.Contains("uint target = ctx.R14 - 0x4u;\n                ctx.WriteCPSR(ctx.SPSR, 0b1001);\n", method);
        Assert.Contains("Recomp.ReturnFromException(ctx, target);\n                return;\n", method);
    }

    [Fact]
    public void AUserBankTransferGoesThroughTheUserRegisters()
    {
        string store = GenerateARM(0xE9C06000, Processor.ARM7); // stmib r0, {sp, lr}^
        string load = GenerateARM(0xE8D00180, Processor.ARM7); // ldmia r0, {r7, r8}^

        Assert.Contains("Memory.Write32(address, ctx.ReadUserRegister(13));", store);
        Assert.Contains("Memory.Write32(address + 4, ctx.ReadUserRegister(14));", store);
        Assert.Contains("ctx.R7 = Memory.Read32(address);", load);
        Assert.Contains("ctx.WriteUserRegister(8, Memory.Read32(address + 4));", load);
    }

    [Fact]
    public void ASkippedLiteralLoadOnlyLoadsWhenItRuns()
    {
        var context = CreateContext(
            [
                0x0000, 0x159F, // 00: ldrne r0, [pc]
                0xFF1E, 0xE12F, // 04: bx lr
                0x5678, 0x1234, // 08: .word 0x12345678
            ],
            [ARMFunction(Base, 12, "Load")]);

        Assert.Contains("if (!ctx.Z)\n            {\n                Scheduler.Cycles += 22;\n                ctx.R0 = 0x12345678u;\n            }\n", Generate(context));
    }

    [Fact]
    public void ARM9CodeOutsideTCMChargesItsFetchesAndLiteralsThroughTheCaches()
    {
        var context = CreateContext(
            [
                0x0000, 0x159F, // 00: ldrne r0, [pc]
                0xFF1E, 0xE12F, // 04: bx lr
                0x5678, 0x1234, // 08: .word 0x12345678
            ],
            [ARMFunction(Base, 12, "Load")],
            processor: Processor.ARM9);

        string code = Generate(context);
        Assert.Contains($"Scheduler.Cycles += CPUTiming.FetchCycles(0x{Base:X8}u, 2, false);\n", code);
        Assert.Contains($"if (!ctx.Z)\n            {{\n                Scheduler.Cycles += CPUTiming.ReadWordCycles(0x{Base + 8:X8}u);\n                ctx.R0 = 0x12345678u;\n            }}\n", code);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void StoreMultipleStoresTheNewBaseOnlyOnARMv4(bool isARMv5, bool storesNewBase)
    {
        string method = GenerateARM(0xE8A10003, isARMv5 ? Processor.ARM9 : Processor.ARM7); // stmia r1!, {r0, r1}

        int writeBack = method.IndexOf("ctx.R1 = baseAddress + 8;");
        int store = method.IndexOf("Memory.Write32(address + 4, ctx.R1);");
        Assert.Equal(storesNewBase, writeBack < store);
    }

    [Theory]
    [InlineData(0xE8B00003u, true, "ctx.R0 = baseAddress + 8;", true)] // ldmia r0!, {r0, r1}
    [InlineData(0xE8B00003u, false, "ctx.R0 = baseAddress + 8;", false)] // ldmia r0!, {r0, r1}
    [InlineData(0xE8B10003u, true, "ctx.R1 = baseAddress + 8;", false)] // ldmia r1!, {r0, r1}
    public void LoadMultipleWritesBackItsListedBaseOnlyOnARMv5WhenItIsNotLast(uint instruction, bool isARMv5, string writeBack, bool writesBack)
    {
        string method = GenerateARM(instruction, isARMv5 ? Processor.ARM9 : Processor.ARM7);

        Assert.Equal(writesBack, method.Contains(writeBack));
    }

    [Fact]
    public void ThumbLoadMultipleNeverWritesBackItsListedBase()
    {
        var context = CreateContext(
            [
                0xC803, // 00: ldmia r0!, {r0, r1}
                0x4770, // 02: bx lr
            ],
            [ThumbFunction(Base, 4, "Load")],
            processor: Processor.ARM9);

        Assert.DoesNotContain("ctx.R0 = baseAddress + 8;", Generate(context));
    }

    [Fact]
    public void LoadDoublewordReadsBothWordsFromTheAddressBeforeTheLoad()
    {
        string method = GenerateARM(0xE1C000D8, Processor.ARM9); // ldrd r0, [r0, #8]

        Assert.Contains("uint address = ctx.R0 + 0x8u;\n                ctx.R0 = Memory.Read32(address);\n                ctx.R1 = Memory.Read32(address + 4);\n", method);
    }

    [Fact]
    public void HalfwordMultiplyAccumulateSetsQWhenTheAdditionOverflows()
    {
        string method = GenerateARM(0xE1003281, Processor.ARM9); // smlabb r0, r1, r2, r3

        Assert.Contains("int product = (short)ctx.R1 * (short)ctx.R2;", method);
        Assert.Contains("int accumulate = (int)ctx.R3;", method);
        Assert.Contains("if (((product ^ result) & (accumulate ^ result)) < 0) ctx.Q = true;", method);
    }

    [Fact]
    public void DoublingSaturatesBeforeTheAddition()
    {
        string method = GenerateARM(0xE1420051, Processor.ARM9); // qdadd r0, r1, r2

        Assert.Contains("long doubled = (long)(int)ctx.R2 * 2;", method);
        Assert.Contains("long result = (long)(int)ctx.R1 + System.Math.Clamp(doubled, int.MinValue, int.MaxValue);", method);
    }

    [Fact]
    public void BranchWithLinkAndExchangeReadsItsTargetBeforeSettingLR()
    {
        string method = GenerateARM(0xE12FFF3E, Processor.ARM9); // blx lr

        Assert.True(method.IndexOf("uint target = ctx.R14;") < method.IndexOf("ctx.R14 = 0x02000004u;"));
    }

    [Fact]
    public void RecompIncludeStartsEveryFile()
    {
        var input = new InputConfig { RecompInclude = "using MyRuntime;" };
        var context = CreateContext(ReturnZero, [ThumbFunction(Base, 4, "Zero")], input);

        Assert.Contains("\nusing MyRuntime;\n\nnamespace RecompiledFuncs.ARM7;\n", new CSharpGenerator(context).GenerateFile([]));
    }

    [Fact]
    public void EachProcessorGetsItsOwnNamespaceAndRuntime()
    {
        var context = CreateContext(ReturnZero, [ThumbFunction(Base, 4, "Zero")], processor: Processor.ARM9);

        Assert.Contains(
            "\nusing NTRModern.ARM9;\nusing LibRecomp;\nusing LibRecomp.ARM9;\n\nnamespace RecompiledFuncs.ARM9;\n",
            new CSharpGenerator(context).GenerateFile([]));
    }
}
