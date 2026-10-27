using System.Buffers.Binary;
using NDSRecomp.ARM;

namespace NDSRecomp.Tests;

internal static class TestCode
{
    public const uint Base = 0x02000000;

    public static Symbol ThumbFunction(uint address, uint size, string name, int section = 0) => new(address, size, name, IsThumb: true, Section: section, IsFunction: true);

    public static Symbol ARMFunction(uint address, uint size, string name, int section = 0) => new(address, size, name, IsThumb: false, Section: section, IsFunction: true);

    public static Symbol ThumbLabel(uint address, string name) => new(address, 0, name, IsThumb: true, Section: 0);

    public static byte[] Bytes(ushort[] code)
    {
        byte[] data = new byte[code.Length * 2];
        for (int i = 0; i < code.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), code[i]);
        }

        return data;
    }

    public static Context CreateContext(
        ushort[] code,
        Symbol[] symbols,
        InputConfig? input = null,
        PatchesConfig? patches = null,
        Processor processor = Processor.ARM7,
        MappingSymbol[]? mappingSymbols = null)
    {
        byte[] data = Bytes(code);
        var section = new Section(0, "main", Base, (uint)data.Length, 0);
        return Context.Create(data, [section], symbols, mappingSymbols ?? [], input ?? new InputConfig(), patches, processor);
    }
}
