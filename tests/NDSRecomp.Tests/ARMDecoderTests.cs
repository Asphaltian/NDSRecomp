using NDSRecomp.ARM;

namespace NDSRecomp.Tests;

public class ARMDecoderTests
{
    private static Instruction Expected(uint address, uint encoding) => new()
    {
        Address = address,
        Encoding = encoding,
        Size = 4,
        Condition = Condition.AL,
    };

    private static void AssertDecodes(Instruction expected)
    {
        Assert.Equal(expected, ARMDecoder.Decode(expected.Address, expected.Encoding, Architecture.ARMv4T));
    }

    [Fact]
    public void MovImmediate()
    {
        AssertDecodes(Expected(0x080000C0, 0xE3A00012) with
        {
            Opcode = Opcode.Mov,
            OperandKind = OperandKind.Immediate,
            Immediate = 0x12,
        });
    }

    [Fact]
    public void MovRotatedImmediate()
    {
        AssertDecodes(Expected(0x08000104, 0xE3A03301) with
        {
            Opcode = Opcode.Mov,
            Rd = 3,
            OperandKind = OperandKind.Immediate,
            Immediate = 0x04000000,
            ShiftAmount = 6,
        });

        AssertDecodes(Expected(0x08000178, 0xE3A01D82) with
        {
            Opcode = Opcode.Mov,
            Rd = 1,
            OperandKind = OperandKind.Immediate,
            Immediate = 0x2080,
            ShiftAmount = 26,
        });
    }

    [Fact]
    public void MovRegister()
    {
        AssertDecodes(Expected(0x080000E8, 0xE1A0E00F) with
        {
            Opcode = Opcode.Mov,
            Rd = 14,
            OperandKind = OperandKind.ImmediateShift,
            Rm = 15,
        });
    }

    [Fact]
    public void AddPCRelative()
    {
        AssertDecodes(Expected(0x080000DC, 0xE28F0020) with
        {
            Opcode = Opcode.Add,
            Rn = 15,
            OperandKind = OperandKind.Immediate,
            Immediate = 0x20,
        });
    }

    [Fact]
    public void AndWithShiftedRegister()
    {
        AssertDecodes(Expected(0x08000124, 0xE0021822) with
        {
            Opcode = Opcode.And,
            Rd = 1,
            Rn = 2,
            OperandKind = OperandKind.ImmediateShift,
            Rm = 2,
            ShiftType = ShiftType.LSR,
            ShiftAmount = 16,
        });
    }

    [Fact]
    public void AndsImmediate()
    {
        AssertDecodes(Expected(0x0800012C, 0xE21100C0) with
        {
            Opcode = Opcode.And,
            SetsFlags = true,
            Rn = 1,
            OperandKind = OperandKind.Immediate,
            Immediate = 0xC0,
        });
    }

    [Fact]
    public void ShiftByRegister()
    {
        AssertDecodes(Expected(0, 0xE1A00211) with
        {
            Opcode = Opcode.Mov,
            OperandKind = OperandKind.RegisterShift,
            Rm = 1,
            Rs = 2,
        });
    }

    [Fact]
    public void ShiftAmountZeroEncodings()
    {
        AssertDecodes(Expected(0, 0xE1A00021) with
        {
            Opcode = Opcode.Mov,
            OperandKind = OperandKind.ImmediateShift,
            Rm = 1,
            ShiftType = ShiftType.LSR,
            ShiftAmount = 32,
        });

        AssertDecodes(Expected(0, 0xE1A00061) with
        {
            Opcode = Opcode.Mov,
            OperandKind = OperandKind.ImmediateShift,
            Rm = 1,
            ShiftType = ShiftType.RRX,
            ShiftAmount = 1,
        });
    }

    [Fact]
    public void Msr()
    {
        AssertDecodes(Expected(0x080000C4, 0xE129F000) with
        {
            Opcode = Opcode.Msr,
            PSRFieldMask = 0b1001,
            OperandKind = OperandKind.ImmediateShift,
        });

        AssertDecodes(Expected(0x080001D0, 0xE169F000) with
        {
            Opcode = Opcode.Msr,
            UsesSPSR = true,
            PSRFieldMask = 0b1001,
            OperandKind = OperandKind.ImmediateShift,
        });
    }

    [Fact]
    public void Mrs()
    {
        AssertDecodes(Expected(0x08000114, 0xE14F0000) with { Opcode = Opcode.Mrs, UsesSPSR = true });

        AssertDecodes(Expected(0x08000188, 0xE10F3000) with { Opcode = Opcode.Mrs, Rd = 3 });
    }

    [Fact]
    public void LdrPCRelative()
    {
        AssertDecodes(Expected(0x080000C8, 0xE59FD028) with
        {
            Opcode = Opcode.Ldr,
            Rd = 13,
            Rn = 15,
            OperandKind = OperandKind.Immediate,
            Immediate = 0x28,
            PreIndexed = true,
            AddOffset = true,
        });
    }

    [Fact]
    public void LdrRegisterOffset()
    {
        AssertDecodes(Expected(0, 0xE7910102) with
        {
            Opcode = Opcode.Ldr,
            Rn = 1,
            OperandKind = OperandKind.ImmediateShift,
            Rm = 2,
            ShiftAmount = 2,
            PreIndexed = true,
            AddOffset = true,
        });
    }

    [Fact]
    public void LdrPostIndexed()
    {
        AssertDecodes(Expected(0, 0xE4910004) with
        {
            Opcode = Opcode.Ldr,
            Rn = 1,
            OperandKind = OperandKind.Immediate,
            Immediate = 4,
            AddOffset = true,
            WriteBack = true,
        });
    }

    [Fact]
    public void StrbConditionalNegativeOffset()
    {
        AssertDecodes(Expected(0x0800016C, 0x1543017C) with
        {
            Opcode = Opcode.Strb,
            Condition = Condition.NE,
            Rn = 3,
            OperandKind = OperandKind.Immediate,
            Immediate = 0x17C,
            PreIndexed = true,
        });
    }

    [Fact]
    public void HalfwordTransfers()
    {
        AssertDecodes(Expected(0x08000110, 0xE1D310B8) with
        {
            Opcode = Opcode.Ldrh,
            Rd = 1,
            Rn = 3,
            OperandKind = OperandKind.Immediate,
            Immediate = 8,
            PreIndexed = true,
            AddOffset = true,
        });

        AssertDecodes(Expected(0x08000174, 0xE1C300B2) with
        {
            Opcode = Opcode.Strh,
            Rn = 3,
            OperandKind = OperandKind.Immediate,
            Immediate = 2,
            PreIndexed = true,
            AddOffset = true,
        });
    }

    [Fact]
    public void BlockTransfers()
    {
        AssertDecodes(Expected(0x08000118, 0xE92D400F) with
        {
            Opcode = Opcode.Stm,
            Rn = 13,
            RegisterList = 0x400F,
            PreIndexed = true,
            WriteBack = true,
        });

        AssertDecodes(Expected(0x080001B0, 0xE8BD4000) with
        {
            Opcode = Opcode.Ldm,
            Rn = 13,
            RegisterList = 0x4000,
            AddOffset = true,
            WriteBack = true,
        });
    }

    [Fact]
    public void Branches()
    {
        AssertDecodes(Expected(0x080000F0, 0xEAFFFFF2) with { Opcode = Opcode.B, Target = 0x080000C0 });

        AssertDecodes(Expected(0x08000130, 0x1A00000F) with
        {
            Opcode = Opcode.B,
            Condition = Condition.NE,
            Target = 0x08000174,
        });

        AssertDecodes(Expected(0x08000130, 0xEB00000F) with { Opcode = Opcode.Bl, Target = 0x08000174 });

        AssertDecodes(Expected(0x080000EC, 0xE12FFF11) with { Opcode = Opcode.Bx, Rm = 1 });
    }

    [Fact]
    public void Multiplies()
    {
        AssertDecodes(Expected(0, 0xE0010392) with { Opcode = Opcode.Mul, Rd = 1, Rs = 3, Rm = 2 });

        AssertDecodes(Expected(0x080AE268, 0xE0832190) with
        {
            Opcode = Opcode.Umull,
            Rd = 3,
            Rn = 2,
            Rs = 1,
            Rm = 0,
        });
    }

    [Fact]
    public void Swp()
    {
        AssertDecodes(Expected(0, 0xE1010092) with { Opcode = Opcode.Swp, Rn = 1, Rm = 2 });
    }

    [Fact]
    public void Swi()
    {
        AssertDecodes(Expected(0, 0xEF050000) with { Opcode = Opcode.Swi, Immediate = 0x050000 });
    }

    [Theory]
    [InlineData(0xEE000000)] // cdp p0, 0, c0, c0, c0, 0
    [InlineData(0xE1C000D0)] // ldrd r0, [r0]
    [InlineData(0xE1200090)]
    [InlineData(0xE1000000)]
    [InlineData(0xE7000010)]
    [InlineData(0xF3A00001)] // movnv r0, #1
    [InlineData(0xFA000000)] // blx 0x8
    public void UndefinedEncodings(uint encoding)
    {
        Assert.Equal(Opcode.Undefined, ARMDecoder.Decode(0, encoding, Architecture.ARMv4T).Opcode);
    }

    private static void AssertDecodesARMv5(Instruction expected)
    {
        Assert.Equal(expected, ARMDecoder.Decode(expected.Address, expected.Encoding, Architecture.ARMv5TE));
    }

    [Fact]
    public void Clz()
    {
        AssertDecodesARMv5(Expected(0, 0xE16FCF10) with { Opcode = Opcode.Clz, Rd = 12 });
    }

    [Fact]
    public void HalfwordMultiplies()
    {
        AssertDecodesARMv5(Expected(0, 0xE1002081) with { Opcode = Opcode.Smlaxy, Rn = 2, Rm = 1 });
        AssertDecodesARMv5(Expected(0, 0xE1600082) with { Opcode = Opcode.Smulxy, Rm = 2 });
        AssertDecodesARMv5(Expected(0, 0xE12002E1) with { Opcode = Opcode.Smulwy, Rs = 2, Rm = 1, RsTop = true });
        AssertDecodesARMv5(Expected(0, 0xE14321C0) with { Opcode = Opcode.Smlalxy, Rd = 3, Rn = 2, Rs = 1, RsTop = true });
    }

    [Fact]
    public void CoprocessorTransfers()
    {
        AssertDecodesARMv5(Expected(0, 0xEE110F10) with { Opcode = Opcode.Mrc, Coprocessor = 15, Cn = 1 });
        AssertDecodesARMv5(Expected(0, 0xEE070F9A) with { Opcode = Opcode.Mcr, Coprocessor = 15, Cn = 7, Cm = 10, CoprocessorInfo = 4 });
    }

    [Fact]
    public void BranchWithLinkAndExchange()
    {
        AssertDecodesARMv5(Expected(0, 0xE12FFF32) with { Opcode = Opcode.Blx, Rm = 2 });
        AssertDecodesARMv5(Expected(0, 0xFA000000) with { Opcode = Opcode.Blx, OperandKind = OperandKind.Immediate, Target = 8 });
        AssertDecodesARMv5(Expected(0, 0xFB000000) with { Opcode = Opcode.Blx, OperandKind = OperandKind.Immediate, Target = 10 });
    }

    [Fact]
    public void SaturatingAdd()
    {
        AssertDecodesARMv5(Expected(0, 0xE1020051) with { Opcode = Opcode.Qadd, Rn = 2, Rm = 1 });
    }

    [Fact]
    public void Breakpoint()
    {
        AssertDecodesARMv5(Expected(0, 0xE1212374) with { Opcode = Opcode.Bkpt, Immediate = 0x1234 });
    }

    [Fact]
    public void DoublewordTransfers()
    {
        AssertDecodesARMv5(Expected(0, 0xE1C000D0) with
        {
            Opcode = Opcode.Ldrd,
            OperandKind = OperandKind.Immediate,
            PreIndexed = true,
            AddOffset = true,
        });

        AssertDecodesARMv5(Expected(0, 0xE1C120F8) with
        {
            Opcode = Opcode.Strd,
            Rn = 1,
            Rd = 2,
            OperandKind = OperandKind.Immediate,
            Immediate = 8,
            PreIndexed = true,
            AddOffset = true,
        });
    }

    [Fact]
    public void PrepareCache()
    {
        AssertDecodesARMv5(Expected(0, 0xF5D1F010) with
        {
            Opcode = Opcode.Pld,
            Rn = 1,
            OperandKind = OperandKind.Immediate,
            Immediate = 0x10,
            PreIndexed = true,
            AddOffset = true,
        });
    }

    [Theory]
    [InlineData(0xEE000000)] // cdp p0, 0, c0, c0, c0, 0
    [InlineData(0xEE110E10)] // mrc p14, 0, r0, c1, c0, 0
    [InlineData(0xEE000610)] // mcr p6, 0, r0, c0, c0, 0
    [InlineData(0xF4D1F010)] // pld [r1], #0x10
    [InlineData(0xE1200090)]
    [InlineData(0xE1000000)]
    [InlineData(0xE7000010)]
    [InlineData(0xF3A00001)] // movnv r0, #1
    public void UndefinedARMv5Encodings(uint encoding)
    {
        Assert.Equal(Opcode.Undefined, ARMDecoder.Decode(0, encoding, Architecture.ARMv5TE).Opcode);
    }
}
