using NDSRecomp.ARM;

namespace NDSRecomp.Tests;

public class DisassemblerTests
{
    [Theory]
    [InlineData(0x00910002u, "addeqs r0, r1, r2")]
    [InlineData(0xE1B00101u, "movs r0, r1, lsl #2")]
    [InlineData(0x00100291u, "muleqs r0, r1, r2")]
    [InlineData(0x18B00006u, "ldmneia r0!, {r1, r2}")]
    [InlineData(0x05D10004u, "ldreqb r0, [r1, #0x4]")]
    [InlineData(0xEB00003Eu, "bl 0x02000100")]
    [InlineData(0xEF000005u, "swi 0x5")]
    public void ARMInstructions(uint encoding, string text)
    {
        Assert.Equal(text, Disassembler.Format(ARMDecoder.Decode(TestCode.Base, encoding, Architecture.ARMv4T)));
    }

    [Theory]
    [InlineData((ushort)0xB510, "stmdb sp!, {r4, lr}")]
    [InlineData((ushort)0x4802, "ldr r0, [pc, #0x8]")]
    [InlineData((ushort)0xDE00, ".hword 0xde00")]
    public void ThumbInstructions(ushort encoding, string text)
    {
        Assert.Equal(text, Disassembler.Format(ThumbDecoder.Decode(TestCode.Base, encoding, 0, Architecture.ARMv4T)));
    }

    [Theory]
    [InlineData(0xE16FCF10u, "clz r12, r0")]
    [InlineData(0xE1002081u, "smlabb r0, r1, r0, r2")]
    [InlineData(0xE1600082u, "smulbb r0, r2, r0")]
    [InlineData(0xEE070F9Au, "mcr p15, 0, r0, c7, c10, 4")]
    [InlineData(0xE12FFF32u, "blx r2")]
    [InlineData(0xE1C120F8u, "strd r2, [r1, #0x8]")]
    public void ARMv5Instructions(uint encoding, string text)
    {
        Assert.Equal(text, Disassembler.Format(ARMDecoder.Decode(TestCode.Base, encoding, Architecture.ARMv5TE)));
    }
}
