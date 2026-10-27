using System.Buffers.Binary;
using System.Text;

namespace NDSRecomp;

internal enum SymbolType : byte
{
    NoType = 0,
    Object = 1,
    Function = 2,
    Section = 3,
    File = 4,
}

internal sealed record ELFSection(int Index, string Name, uint Size, bool IsCode);

internal readonly record struct ELFSymbol(string Name, uint Value, uint Size, SymbolType Type, int Section);

internal sealed class ELF
{
    private const uint SHT_PROGBITS = 1;
    private const uint SHT_SYMTAB = 2;
    private const uint SHF_EXECINSTR = 4;
    private const ushort SHN_UNDEF = 0;
    private const ushort SHN_LORESERVE = 0xFF00;
    private const int SymbolSize = 0x10;

    private ELF(List<ELFSection> sections, List<ELFSymbol> symbols)
    {
        Sections = sections;
        Symbols = symbols;
    }

    public IReadOnlyList<ELFSection> Sections { get; }

    public IReadOnlyList<ELFSymbol> Symbols { get; }

    public static ELF Read(byte[] elf)
    {
        if (elf.Length < 0x34 || BinaryPrimitives.ReadUInt32BigEndian(elf) != 0x7F454C46 || elf[4] != 1 || elf[5] != 1)
        {
            throw new InvalidDataException("This is not a 32-bit little-endian ELF file.");
        }

        try
        {
            return ReadContents(elf);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("The ELF file is cut short or damaged.");
        }
    }

    private static ELF ReadContents(byte[] elf)
    {
        var span = elf.AsSpan();
        int headerTable = (int)Word(span, 0x20);
        int headerSize = Half(span, 0x2E);
        int headerCount = Half(span, 0x30);
        int namesIndex = Half(span, 0x32);

        var headers = Enumerable.Range(0, headerCount).Select(i => headerTable + (i * headerSize)).ToArray();
        int sectionNames = (int)Word(span, headers[namesIndex] + 0x10);

        var sections = new List<ELFSection>();
        var symbols = new List<ELFSymbol>();
        for (int i = 0; i < headerCount; i++)
        {
            int header = headers[i];
            uint type = Word(span, header + 0x04);
            uint flags = Word(span, header + 0x08);
            int offset = (int)Word(span, header + 0x10);
            int size = (int)Word(span, header + 0x14);

            if (type == SHT_PROGBITS)
            {
                string name = String(span, sectionNames + (int)Word(span, header));
                sections.Add(new ELFSection(i, name, (uint)size, (flags & SHF_EXECINSTR) != 0));
            }
            else if (type == SHT_SYMTAB)
            {
                int entrySize = (int)Word(span, header + 0x24);
                if (entrySize < SymbolSize)
                {
                    throw new InvalidDataException("The ELF file's symbol table is damaged.");
                }

                int names = (int)Word(span, headers[(int)Word(span, header + 0x18)] + 0x10);
                for (int entry = offset; entry < offset + size; entry += entrySize)
                {
                    ushort section = Half(span, entry + 0x0E);
                    string name = String(span, names + (int)Word(span, entry));
                    if (name.Length == 0 || section == SHN_UNDEF || section >= SHN_LORESERVE)
                    {
                        continue;
                    }

                    symbols.Add(new ELFSymbol(name, Word(span, entry + 0x04), Word(span, entry + 0x08), (SymbolType)(span[entry + 0x0C] & 0xF), section));
                }
            }
        }

        return new ELF(sections, symbols);
    }

    private static uint Word(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);

    private static ushort Half(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);

    private static string String(ReadOnlySpan<byte> span, int at)
    {
        var bytes = span[at..];
        return Encoding.UTF8.GetString(bytes[..bytes.IndexOf((byte)0)]);
    }
}
