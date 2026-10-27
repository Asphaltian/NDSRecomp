using System.Buffers.Binary;
using NDSRecomp.ARM;

namespace NDSRecomp;

internal enum Processor
{
    ARM9,
    ARM7,
}

internal sealed record Section(int Index, string Name, uint Address, uint Size, uint ROMAddress, uint? OverlayID = null)
{
    public uint End => Address + Size;

    public bool Contains(uint address) => address >= Address && address < End;
}

internal static class Sections
{
    private const int AutoloadEntrySize = 12;
    private const string Mismatch = "Check that the symbols were built from a decompilation that matches the ROM.";

    public static Architecture ArchitectureOf(Processor processor) => processor == Processor.ARM9 ? Architecture.ARMv5TE : Architecture.ARMv4T;

    public static List<Section> Locate(IReadOnlyDictionary<string, uint> symbols, IEnumerable<string> names, Cartridge cartridge, byte[] rom, Processor processor)
    {
        var bootCode = processor == Processor.ARM9 ? cartridge.ARM9 : cartridge.ARM7;
        var overlays = processor == Processor.ARM9 ? cartridge.ARM9Overlays : cartridge.ARM7Overlays;
        Dictionary<uint, (uint ROMAddress, uint Size)>? autoloads = null;
        bool hasStatic = false;
        var sections = new List<Section>();

        foreach (string name in names)
        {
            int index = sections.Count;
            if (symbols.TryGetValue($"SDK_OVERLAY.{name}.ID", out uint id))
            {
                var overlay = overlays.FirstOrDefault(o => o.ID == id)
                    ?? throw new InvalidDataException($"The ROM has no overlay {id} for the {name} section. {Mismatch}");
                if (Symbol($"SDK_OVERLAY.{name}.START") != overlay.RAMAddress || Symbol($"SDK_OVERLAY.{name}.SIZE") != overlay.RAMSize)
                {
                    throw new InvalidDataException($"The {name} section isn't where overlay {id} is in the ROM. {Mismatch}");
                }

                sections.Add(new Section(index, name, overlay.RAMAddress, overlay.RAMSize, overlay.ROMOffset, id));
            }
            else if (symbols.TryGetValue($"SDK_AUTOLOAD.{name}.START", out uint start))
            {
                autoloads ??= Autoloads(symbols, bootCode, rom);
                if (!autoloads.TryGetValue(start, out var autoload))
                {
                    throw new InvalidDataException($"The ROM's autoload table has nothing for the {name} section. {Mismatch}");
                }

                sections.Add(new Section(index, name, start, autoload.Size, autoload.ROMAddress));
            }
            else
            {
                if (hasStatic)
                {
                    throw new InvalidDataException($"The {name} section isn't an overlay or an autoload, and there's already a static section.");
                }

                if (Symbol("SDK_STATIC_START") != bootCode.RAMAddress || Symbol("SDK_STATIC_SIZE") > bootCode.Size)
                {
                    throw new InvalidDataException($"The {name} section isn't the {processor} boot code in the ROM. {Mismatch}");
                }

                hasStatic = true;
                sections.Add(new Section(index, name, bootCode.RAMAddress, Symbol("SDK_STATIC_SIZE"), bootCode.ROMOffset));
            }
        }

        return sections;

        uint Symbol(string name) => symbols.TryGetValue(name, out uint value)
            ? value
            : throw new InvalidDataException($"The symbols don't give {name} a single value. {Mismatch}");
    }

    private static Dictionary<uint, (uint ROMAddress, uint Size)> Autoloads(IReadOnlyDictionary<string, uint> symbols, BootCode bootCode, byte[] rom)
    {
        uint ROMAddressOf(string name)
        {
            if (!symbols.TryGetValue(name, out uint address))
            {
                throw new InvalidDataException($"The symbols don't give {name} a single value, so the autoloads can't be found. {Mismatch}");
            }

            return address - bootCode.RAMAddress <= bootCode.Size
                ? bootCode.ROMOffset + (address - bootCode.RAMAddress)
                : throw new InvalidDataException($"{name} is outside the boot code. {Mismatch}");
        }

        uint bootEnd = bootCode.ROMOffset + bootCode.Size;
        uint data = ROMAddressOf("SDK_AUTOLOAD_START");
        uint listEnd = ROMAddressOf("SDK_AUTOLOAD_LIST_END");
        var autoloads = new Dictionary<uint, (uint, uint)>();
        for (uint entry = ROMAddressOf("SDK_AUTOLOAD_LIST"); entry + AutoloadEntrySize <= listEnd; entry += AutoloadEntrySize)
        {
            uint load = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan((int)entry));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan((int)entry + 4));
            if ((ulong)data + size > bootEnd)
            {
                throw new InvalidDataException($"The autoload to 0x{load:X8} runs past the end of the boot code. {Mismatch}");
            }

            autoloads[load] = (data, size);
            data += size;
        }

        return autoloads;
    }
}
