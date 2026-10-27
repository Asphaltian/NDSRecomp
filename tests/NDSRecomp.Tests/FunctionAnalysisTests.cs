using NDSRecomp.ARM;
using static NDSRecomp.Tests.TestCode;

namespace NDSRecomp.Tests;

public class FunctionAnalysisTests
{
    private static Context CreateContext(
        ushort[] code, Symbol[] symbols, string[]? noreturnFuncs = null, Processor processor = Processor.ARM7)
    {
        var input = new InputConfig { NoreturnFuncs = [.. noreturnFuncs ?? []] };
        return TestCode.CreateContext(code, symbols, input, processor: processor);
    }

    private static FunctionAnalysis Analyze(Context context)
    {
        return FunctionAnalysis.Analyze(context, context.Functions[0]);
    }

    [Fact]
    public void AnAddressWorkedOutFromPCIsALandingPadOnlyWhereABlockStarts()
    {
        var context = CreateContext(
            [
                0xA001, // 00: add r0, pc, #0x4
                0xA100, // 02: add r1, pc, #0x0
                0x2200, // 04: movs r2, #0
                0xE7FF, // 06: b 0x08
                0x4770, // 08: bx lr
            ],
            [ThumbFunction(Base, 0x0A, "Test")]);
        context.FindPCRelativeAddresses();

        Assert.Equal([Base + 8], Analyze(context).LandingPads);
    }

    [Fact]
    public void CodeDoesNotContinueAfterASoftReset()
    {
        var context = CreateContext(
            [
                0xDF00,         // 00: swi 0x00
                0xE800,         // 02: .hword 0xe800
            ],
            [ThumbFunction(Base, 4, "Reset")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Single(analysis.Instructions);
    }

    [Fact]
    public void BranchWithLinkInsideTheFunctionIsAJump()
    {
        var context = CreateContext(
            [
                0xB500,         // 00: push {lr}
                0xF000, 0xF803, // 02: bl 0x0c
                0xDE00,         // 06: undefined
                0xDE00,         // 08: undefined
                0xDE00,         // 0a: undefined
                0xBD00,         // 0c: pop {pc}
            ],
            [ThumbFunction(Base, 0x0E, "FarJump")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal(new Flow(FlowKind.Branch, Base + 0x0C), analysis.Flows[Base + 0x02]);
        Assert.Equal([Base + 0x0C], analysis.Labels);
        Assert.Equal([Base, Base + 0x02, Base + 0x0C], analysis.Instructions.Select(i => i.Address));
    }

    [Fact]
    public void BranchWithLinkInsideTheFunctionToCodeThatUsesTheReturnAddressIsACall()
    {
        var context = CreateContext(
            [
                0xB500,         // 00: push {lr}
                0xF000, 0xF801, // 02: bl 0x08
                0xBD00,         // 06: pop {pc}
                0x4770,         // 08: bx lr
            ],
            [ThumbFunction(Base, 0x0A, "Caller")]);

        var analysis = Analyze(context);

        Assert.Equal(new Flow(FlowKind.Call, Base + 8), analysis.Flows[Base + 2]);
        Assert.Equal([(Base + 8, true)], analysis.UnknownTargets);
    }

    [Fact]
    public void BranchWithLinkToAFunctionInsideTheFunctionIsACall()
    {
        var context = CreateContext(
            [
                0xB500,         // 00: push {lr}
                0xF000, 0xF801, // 02: bl 0x08
                0xBD00,         // 06: pop {pc}
                0x4718,         // 08: bx r3
            ],
            [ThumbFunction(Base, 0x0A, "Caller"), ThumbFunction(Base + 8, 2, "call_r3")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal(FlowKind.Call, analysis.Flows[Base + 2].Kind);
        Assert.Equal("call_r3", analysis.Flows[Base + 2].Callee?.Name);
    }

    [Fact]
    public void CodeDoesNotContinueAfterANoreturnCall()
    {
        ushort[] code =
        [
            0xF000, 0xF802, // 00: bl 0x08
            0xDE00,         // 04: undefined
            0xDE00,         // 06: undefined
            0xE7FE,         // 08: b .
        ];
        Symbol[] symbols = [ThumbFunction(Base, 8, "Caller"), ThumbFunction(Base + 8, 2, "Abort")];

        Assert.Empty(Analyze(CreateContext(code, symbols, ["Abort"])).Errors);
        Assert.Single(Analyze(CreateContext(code, symbols)).Errors);
    }

    [Fact]
    public void BranchExchangeToALabelSwitchesState()
    {
        var context = CreateContext(
            [
                0xA000,         // 00: adr r0, 0x04
                0x4700,         // 02: bx r0
                0xFF1E, 0xE12F, // 04: bx lr (ARM)
            ],
            [ThumbFunction(Base, 8, "ThumbToARM")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal(new Flow(FlowKind.BranchExchange, Base + 4), analysis.Flows[Base + 2]);

        var last = analysis.Instructions.Last();
        Assert.False(last.IsThumb);
        Assert.Equal(Opcode.Bx, last.Opcode);
    }

    [Fact]
    public void BXPCSwitchesToARMAtTheNextWord()
    {
        var context = CreateContext(
            [
                0x4778,         // 00: bx pc
                0x46C0,         // 02: mov r8, r8
                0xFF1E, 0xE12F, // 04: bx lr (ARM)
            ],
            [ThumbFunction(Base, 8, "ThumbToARM")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal(new Flow(FlowKind.BranchExchange, Base + 4), analysis.Flows[Base]);
        Assert.False(analysis.Instructions.Last().IsThumb);
    }

    [Theory]
    [InlineData(0x019F, 0xE000)] // mul r0, pc, r1
    [InlineData(0x0F11, 0xE1A0)] // mov r0, r1, lsl pc
    [InlineData(0xF000, 0xE10F)] // mrs pc, cpsr
    [InlineData(0x0092, 0xE10F)] // swp r0, r2, [pc]
    [InlineData(0x0004, 0xE5BF)] // ldr r0, [pc, #4]!
    [InlineData(0x0001, 0xE8F8)] // ldmia r8!, {r0}^
    [InlineData(0x0000, 0xE890)] // ldmia r0, {}
    [InlineData(0xF100, 0xE08F)] // add pc, pc, r0, lsl #2
    public void UnsupportedARMInstructionsAreErrors(ushort low, ushort high)
    {
        var context = CreateContext([low, high, 0xFF1E, 0xE12F], [ARMFunction(Base, 8, "Unsupported")]);

        Assert.Single(Analyze(context).Errors);
    }

    [Fact]
    public void SettingLRBeforeBXIsACall()
    {
        var context = CreateContext(
            [
                0xE00F, 0xE1A0, // 00: mov lr, pc
                0xFF11, 0xE12F, // 04: bx r1
                0xFF1E, 0xE12F, // 08: bx lr
            ],
            [ARMFunction(Base, 0x0C, "CallsPointer")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal(new Flow(FlowKind.IndirectCall, Base + 8), analysis.Flows[Base + 4]);
        Assert.Equal(new Flow(FlowKind.IndirectJump), analysis.Flows[Base + 8]);
    }

    [Fact]
    public void FallingOffTheEndContinuesInTheNextFunction()
    {
        var context = CreateContext(
            [
                0x2000, // 00: movs r0, #0
                0x4770, // 02: bx lr
            ],
            [ThumbFunction(Base, 2, "First"), ThumbFunction(Base + 2, 2, "Second")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal("Second", analysis.FallsThroughTo?.Name);
    }

    [Fact]
    public void LiteralDecodedAsAnInstructionIsAnError()
    {
        var context = CreateContext(
            [
                0x4800,         // 00: ldr r0, [pc, #0]
                0x2000,         // 02: movs r0, #0
                0x2000, 0x2000, // 04: literal
                0x4770,         // 08: bx lr
            ],
            [ThumbFunction(Base, 0x0A, "MissingReturn")]);

        Assert.Single(Analyze(context).Errors);
    }

    [Fact]
    public void UnsignedJumpTableJumpsToEverySlot()
    {
        var context = CreateContext(
            [
                0x0002, 0xE350, // 00: cmp r0, #2
                0xF100, 0x908F, // 04: addls pc, pc, r0, lsl #2
                0x0004, 0xEA00, // 08: b 0x20
                0x0001, 0xEA00, // 0c: b 0x18
                0x0001, 0xEA00, // 10: b 0x1c
                0x0001, 0xEA00, // 14: b 0x20
                0x0000, 0xE3A0, // 18: mov r0, #0
                0x0001, 0xE3A0, // 1c: mov r0, #1
                0xFF1E, 0xE12F, // 20: bx lr
            ],
            [ARMFunction(Base, 0x24, "Switch")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal(FlowKind.JumpTable, analysis.Flows[Base + 4].Kind);
        Assert.Equal([Base + 0x0C, Base + 0x10, Base + 0x14], analysis.Flows[Base + 4].Targets!);
        Assert.Equal([Base + 0x0C, Base + 0x10, Base + 0x14, Base + 0x18, Base + 0x1C, Base + 0x20], analysis.Labels);
    }

    [Fact]
    public void AJumpIntoUnrolledCodeGoesWhereverItsKnownOffsetsLead()
    {
        var context = CreateContext(
            [
                0x1000, 0xE3A0, // 00: mov r1, #0
                0x200C, 0xE3A0, // 04: mov r2, #12
                0x0001, 0xE350, // 08: cmp r0, #1
                0x2004, 0xD242, // 0c: suble r2, r2, #4
                0x0000, 0xE350, // 10: cmp r0, #0
                0x2004, 0xD242, // 14: suble r2, r2, #4
                0xF102, 0xE08F, // 18: add pc, pc, r2, lsl #2
                0x0000, 0xE1A0, // 1c: mov r0, r0
                0x1001, 0xE281, // 20: add r1, r1, #1
                0x1001, 0xE281, // 24: add r1, r1, #1
                0x1001, 0xE281, // 28: add r1, r1, #1
                0x1001, 0xE281, // 2c: add r1, r1, #1
                0x1001, 0xE281, // 30: add r1, r1, #1
                0x1001, 0xE281, // 34: add r1, r1, #1
                0x1001, 0xE281, // 38: add r1, r1, #1
                0x1001, 0xE281, // 3c: add r1, r1, #1
                0x1001, 0xE281, // 40: add r1, r1, #1
                0x1001, 0xE281, // 44: add r1, r1, #1
                0x1001, 0xE281, // 48: add r1, r1, #1
                0x1001, 0xE281, // 4c: add r1, r1, #1
                0x0001, 0xE1A0, // 50: mov r0, r1
                0xFF1E, 0xE12F, // 54: bx lr
            ],
            [ARMFunction(Base, 0x58, "CountUp")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal(FlowKind.JumpTable, analysis.Flows[Base + 0x18].Kind);
        Assert.Equal([Base + 0x30, Base + 0x40, Base + 0x50], analysis.Flows[Base + 0x18].Targets!);
    }

    [Fact]
    public void SignedJumpTableAfterASubtractionIsSizedByTheCheckBeforeIt()
    {
        var context = CreateContext(
            [
                0x0066, 0xE350, // 00: cmp r0, #0x66
                0x0006, 0xCA00, // 04: bgt 0x24
                0x1064, 0xE250, // 08: subs r1, r0, #0x64
                0xF101, 0x508F, // 0c: addpl pc, pc, r1, lsl #2
                0x0003, 0xEA00, // 10: b 0x24
                0x0001, 0xEA00, // 14: b 0x20
                0x0001, 0xEA00, // 18: b 0x24
                0xFFFF, 0xEAFF, // 1c: b 0x20
                0x0000, 0xE3A0, // 20: mov r0, #0
                0xFF1E, 0xE12F, // 24: bx lr
            ],
            [ARMFunction(Base, 0x28, "Switch")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 0x14, Base + 0x18, Base + 0x1C], analysis.Flows[Base + 0x0C].Targets!);
    }

    [Fact]
    public void SignedJumpTableAfterAComparisonWithZeroIsSizedByTheCheckBeforeIt()
    {
        var context = CreateContext(
            [
                0x0002, 0xE350, // 00: cmp r0, #2
                0x0006, 0xCA00, // 04: bgt 0x24
                0x0000, 0xE350, // 08: cmp r0, #0
                0xF100, 0xA08F, // 0c: addge pc, pc, r0, lsl #2
                0x0003, 0xEA00, // 10: b 0x24
                0x0002, 0xEA00, // 14: b 0x24
                0x0000, 0xEA00, // 18: b 0x20
                0x0000, 0xEA00, // 1c: b 0x24
                0x0000, 0xE3A0, // 20: mov r0, #0
                0xFF1E, 0xE12F, // 24: bx lr
            ],
            [ARMFunction(Base, 0x28, "Switch")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 0x14, Base + 0x18, Base + 0x1C], analysis.Flows[Base + 0x0C].Targets!);
    }

    [Fact]
    public void JumpTableInALoopIsSizedByTheCheckInsideTheLoop()
    {
        var context = CreateContext(
            [
                0x5000, 0xE3A0, // 00: mov r5, #0
                0x0002, 0xE355, // 04: cmp r5, #2
                0xF105, 0x908F, // 08: addls pc, pc, r5, lsl #2
                0x0002, 0xEA00, // 0c: b 0x1c
                0x0001, 0xEA00, // 10: b 0x1c
                0x0000, 0xEA00, // 14: b 0x1c
                0xFFFF, 0xEAFF, // 18: b 0x1c
                0x5001, 0xE285, // 1c: add r5, r5, #1
                0x0003, 0xE355, // 20: cmp r5, #3
                0xFFF6, 0xBAFF, // 24: blt 0x04
                0xFF1E, 0xE12F, // 28: bx lr
            ],
            [ARMFunction(Base, 0x2C, "Loop")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 0x10, Base + 0x14, Base + 0x18], analysis.Flows[Base + 8].Targets!);
    }

    [Fact]
    public void JumpTableBoundInARegisterACallChangesIsUnknown()
    {
        var context = CreateContext(
            [
                0x1002, 0xE3A0, // 00: mov r1, #2
                0xFF33, 0xE12F, // 04: blx r3
                0x0001, 0xE150, // 08: cmp r0, r1
                0xF100, 0x908F, // 0c: addls pc, pc, r0, lsl #2
                0x0000, 0xEA00, // 10: b 0x18
                0xFFFF, 0xEAFF, // 14: b 0x18
                0xFF1E, 0xE12F, // 18: bx lr
            ],
            [ARMFunction(Base, 0x1C, "Switch")],
            processor: Processor.ARM9);

        Assert.Equal(
            "The jump table dispatch at 0x0200000C has no range check before it, so its size is unknown. Replace the function through patches.ignored.",
            Assert.Single(Analyze(context).Errors));
    }

    [Fact]
    public void ThumbJumpTableUsesTheOffsetsAfterTheDispatch()
    {
        var context = CreateContext(
            [
                0x2802,         // 00: cmp r0, #2
                0xD809,         // 02: bhi 0x18
                0x1800,         // 04: adds r0, r0, r0
                0x4478,         // 06: add r0, pc
                0x88C0,         // 08: ldrh r0, [r0, #6]
                0x0400,         // 0a: lsls r0, r0, #16
                0x1400,         // 0c: asrs r0, r0, #16
                0x4487,         // 0e: add pc, r0
                0x0004,         // 10: .hword 0x16 - 0x10 - 2
                0x0006,         // 12: .hword 0x18 - 0x10 - 2
                0x0004,         // 14: .hword 0x16 - 0x10 - 2
                0x2000,         // 16: movs r0, #0
                0x4770,         // 18: bx lr
            ],
            [ThumbFunction(Base, 0x1A, "Switch")]);

        var analysis = Analyze(context);

        Assert.Empty(analysis.Errors);
        Assert.Equal([Base + 0x16, Base + 0x18], analysis.Flows[Base + 0x0E].Targets!);
        Assert.DoesNotContain(analysis.Instructions, i => i.Address is >= Base + 0x10 and < Base + 0x16);
    }

    [Theory]
    [InlineData(0x0070, 0xE120)] // bkpt 0x0000
    [InlineData(0xFF90, 0xEE07)] // mcr p15, 0, pc, c7, c0, 4
    [InlineData(0x10D0, 0xE1C0)] // ldrd r1, [r0]
    [InlineData(0x00D8, 0xE0C0)] // ldrd r0, [r0], #8
    public void UnsupportedARMv5InstructionsAreErrors(ushort low, ushort high)
    {
        var context = CreateContext([low, high, 0xFF1E, 0xE12F], [ARMFunction(Base, 8, "Unsupported")], processor: Processor.ARM9);

        Assert.Single(Analyze(context).Errors);
    }
}
