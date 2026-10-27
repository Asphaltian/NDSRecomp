using System.Buffers.Binary;

namespace NDSRecomp;

internal sealed record BootCode(uint ROMOffset, uint RAMAddress, uint Size);

internal sealed record Overlay(uint ID, uint RAMAddress, uint RAMSize, uint ROMOffset);

internal sealed class Cartridge
{
    private const int HeaderSize = 0x160;
    private const int OverlayEntrySize = 0x20;
    private const int FATEntrySize = 8;

    private Cartridge(BootCode arm9, BootCode arm7, List<Overlay> arm9Overlays, List<Overlay> arm7Overlays)
    {
        ARM9 = arm9;
        ARM7 = arm7;
        ARM9Overlays = arm9Overlays;
        ARM7Overlays = arm7Overlays;
    }

    public BootCode ARM9 { get; }

    public BootCode ARM7 { get; }

    public IReadOnlyList<Overlay> ARM9Overlays { get; }

    public IReadOnlyList<Overlay> ARM7Overlays { get; }

    public static Cartridge Read(byte[] data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException("The ROM is too small to have a cartridge header.");
        }

        var arm9 = ReadBootCode(data, 0x20, "ARM9");
        var arm7 = ReadBootCode(data, 0x30, "ARM7");
        var fat = ReadTable(data, 0x48, "FAT");
        return new Cartridge(arm9, arm7, ReadOverlays(data, 0x50, fat, "ARM9"), ReadOverlays(data, 0x58, fat, "ARM7"));
    }

    private static BootCode ReadBootCode(byte[] data, int at, string processor)
    {
        var bootCode = new BootCode(Word(data, at), Word(data, at + 8), Word(data, at + 12));
        if (!Fits(data, bootCode.ROMOffset, bootCode.Size))
        {
            throw new InvalidDataException($"The {processor} boot code goes past the end of the ROM.");
        }

        return bootCode;
    }

    private static ReadOnlySpan<byte> ReadTable(byte[] data, int at, string name)
    {
        uint offset = Word(data, at);
        uint size = Word(data, at + 4);
        if (!Fits(data, offset, size))
        {
            throw new InvalidDataException($"The {name} goes past the end of the ROM.");
        }

        return data.AsSpan((int)offset, (int)size);
    }

    private static List<Overlay> ReadOverlays(byte[] data, int at, ReadOnlySpan<byte> fat, string processor)
    {
        var table = ReadTable(data, at, $"{processor} overlay table");
        var overlays = new List<Overlay>();
        for (int entry = 0; entry + OverlayEntrySize <= table.Length; entry += OverlayEntrySize)
        {
            var fields = table[entry..];
            uint id = Word(fields, 0x00);
            uint ramSize = Word(fields, 0x08);
            uint fileID = Word(fields, 0x18);

            if (((ulong)fileID + 1) * FATEntrySize > (ulong)fat.Length)
            {
                throw new InvalidDataException($"{processor} overlay {id} is file {fileID}, which the FAT doesn't have.");
            }

            int fatEntry = (int)fileID * FATEntrySize;

            uint start = Word(fat, fatEntry);
            uint end = Word(fat, fatEntry + 4);
            if (end < start || end - start != ramSize || !Fits(data, start, ramSize))
            {
                throw new InvalidDataException($"{processor} overlay {id} doesn't match file {fileID}. Only overlays stored exactly as they're loaded can be recompiled.");
            }

            overlays.Add(new Overlay(id, Word(fields, 0x04), ramSize, start));
        }

        return overlays;
    }

    private static bool Fits(byte[] data, uint offset, uint size) => (ulong)offset + size <= (ulong)data.Length;

    private static uint Word(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
}
