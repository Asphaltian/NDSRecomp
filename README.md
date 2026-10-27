# NDSRecomp

NDSRecomp is a static recompiler that turns the code of a Nintendo DS game into C# code. Every function in the game, overlays included, becomes a C# method, which you then build and run with [NDS Modern Runtime](https://github.com/Asphaltian/NDSModernRuntime).

Keep in mind that NDSRecomp finds every function through the xMAP or ELF file a decompilation of the game builds, so the decompilation has to match the ROM exactly.

## Building

You will need the .NET 10 SDK.

```
git clone https://github.com/Asphaltian/NDSRecomp
cd NDSRecomp
dotnet build -c Release
```

## Running

Run NDSRecomp with a TOML config for your game, once for the ARM9 and once for the ARM7:

```
dotnet run --project src/NDSRecomp -c Release -- arm9.toml
dotnet run --project src/NDSRecomp -c Release -- arm7.toml
```

It generates `Funcs`, which has a method for every function in the game, and `Data`, which has the address of every other symbol. Only the files that actually changed get rewritten, so your build doesn't have to start over every time. If something can't be recompiled, NDSRecomp lists every problem it found and writes nothing.

## Config

Paths in the config are relative to the config file itself. Here's an example you can start from:

```toml
[input]
rom_file_path = "game.nds"
# Or elf_path, if your decompilation builds an ELF with every overlay in it
symbols_file_path = "main.nef.xMAP"
# "ARM9" or "ARM7"
processor = "ARM9"
output_func_path = "RecompiledFuncs"

# Functions that never return
noreturn_funcs = ["Hang"]

# Functions that can return a second time, like setjmp
returns_twice_funcs = ["OS_SaveContext"]

# Functions the symbols don't have, and the section each one is in
manual_funcs = [
    { name = "FastCopy", section = "ITCM", address = 0x01FF8000, size = 0x40 },
]

# Sizes to use when the symbols get one wrong
function_sizes = [
    { name = "UpdateMenu", size = 0x90 },
]

[patches]
# These become empty functions
stubs = ["DebugPrint"]

# These aren't recompiled at all, so you'll have to replace them yourself
ignored = ["StrangeSwitch"]
```
