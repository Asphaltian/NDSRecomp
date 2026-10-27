using System.Diagnostics;
using NDSRecomp.ARM;

namespace NDSRecomp;

internal enum CodeRegion
{
    TCM,
    MainMemory,
    WRAM,
    VRAM,
}

internal static class CycleCosts
{
    public const uint CacheLineSize = 32;

    private const int BusCycle = 2;

    public static CodeRegion RegionOf(Processor processor, uint address) => (processor, address >> 24) switch
    {
        (Processor.ARM9, < 0x02) => CodeRegion.TCM,
        (_, 0x02) => CodeRegion.MainMemory,
        (Processor.ARM9, 0x03 or 0x04 or 0x07 or 0xFF) or (Processor.ARM7, 0x00 or 0x03 or 0x04) => CodeRegion.WRAM,
        (Processor.ARM9, 0x05 or 0x06) => CodeRegion.VRAM,
        _ => throw new InvalidDataException($"NDSRecomp doesn't know how long the {processor} takes to run code at 0x{address:X8}."),
    };

    public static int Fetch(Processor processor, CodeRegion region, bool isThumb, bool isSequential)
    {
        if (processor == Processor.ARM9)
        {
            return region == CodeRegion.TCM ? 1 : throw new UnreachableException();
        }

        return (region, isThumb, isSequential) switch
        {
            (CodeRegion.MainMemory, false, false) => 18,
            (CodeRegion.MainMemory, false, true) => 4,
            (CodeRegion.MainMemory, true, false) => 16,
            (CodeRegion.MainMemory, true, true) => 2,
            _ => 2,
        };
    }

    public static int Jump(Processor processor, uint target, bool isThumb)
    {
        var region = RegionOf(processor, target);
        if (processor == Processor.ARM7)
        {
            return Fetch(processor, region, isThumb, isSequential: false) + Fetch(processor, region, isThumb, isSequential: true);
        }

        int first = Fetch(processor, region, isThumb: isThumb && (target & 2) == 0, isSequential: false);
        return first + Fetch(processor, region, isThumb, isSequential: true);
    }

    public static int Internal(Processor processor, Instruction instruction)
    {
        int cycles = instruction.Opcode switch
        {
            Opcode.Ldr or Opcode.Ldrb or Opcode.Ldrh or Opcode.Ldrsb or Opcode.Ldrsh => processor == Processor.ARM7 ? 1 : 0,
            Opcode.Ldm or Opcode.Swp or Opcode.Swpb or Opcode.Smlalxy => 1,
            _ => instruction.OperandKind == OperandKind.RegisterShift ? 1 : 0,
        };

        return processor == Processor.ARM7 ? cycles * BusCycle : cycles;
    }

    public static bool IsStore(Instruction instruction)
    {
        return instruction.Opcode is Opcode.Str or Opcode.Strb or Opcode.Strh or Opcode.Stm or Opcode.Strd;
    }

    public static int ReadWord(Processor processor, CodeRegion region) => (processor, region) switch
    {
        (Processor.ARM9, CodeRegion.TCM) => 1,
        (Processor.ARM9, _) => throw new UnreachableException(),
        (_, CodeRegion.MainMemory) => 20,
        _ => 2,
    };
}
