using System.Buffers.Binary;

namespace NDSRecomp.Tests;

public class CartridgeTests
{
    private static byte[] CreateCartridge(uint overlaySize = 0x40, uint fileEnd = 0x440)
    {
        byte[] data = new byte[0x800];
        void Word(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);

        Word(0x20, 0x200);
        Word(0x24, 0x02000800);
        Word(0x28, 0x02000000);
        Word(0x2C, 0x100);
        Word(0x30, 0x300);
        Word(0x34, 0x02380000);
        Word(0x38, 0x02380000);
        Word(0x3C, 0x80);
        Word(0x48, 0x600);
        Word(0x4C, 0x10);
        Word(0x50, 0x700);
        Word(0x54, 0x20);

        Word(0x600, 0x3C0);
        Word(0x604, 0x3C0);
        Word(0x608, 0x400);
        Word(0x60C, fileEnd);

        Word(0x700, 0);
        Word(0x704, 0x022DC240);
        Word(0x708, overlaySize);
        Word(0x70C, 0x20);
        Word(0x710, 0x022DC260);
        Word(0x714, 0x022DC264);
        Word(0x718, 1);
        return data;
    }

    [Fact]
    public void ReadsBootCodeAndOverlays()
    {
        var cartridge = Cartridge.Read(CreateCartridge());

        Assert.Equal(new BootCode(0x200, 0x02000000, 0x100), cartridge.ARM9);
        Assert.Equal(new BootCode(0x300, 0x02380000, 0x80), cartridge.ARM7);
        Assert.Equal([new Overlay(0, 0x022DC240, 0x40, 0x400)], cartridge.ARM9Overlays);
        Assert.Empty(cartridge.ARM7Overlays);
    }

    [Fact]
    public void RejectsOverlaysNotStoredAsLoaded()
    {
        var error = Assert.Throws<InvalidDataException>(() => Cartridge.Read(CreateCartridge(fileEnd: 0x420)));
        Assert.Equal("ARM9 overlay 0 doesn't match file 1. Only overlays stored exactly as they're loaded can be recompiled.", error.Message);
    }

    [Fact]
    public void RejectsBootCodePastTheEnd()
    {
        byte[] data = CreateCartridge();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C), 0x1000);

        var error = Assert.Throws<InvalidDataException>(() => Cartridge.Read(data));
        Assert.Equal("The ARM9 boot code goes past the end of the ROM.", error.Message);
    }
}
