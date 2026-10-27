using System.Numerics;

namespace NDSRecomp.ARM;

internal static class ARMDecoder
{
    public static Instruction Decode(uint address, uint encoding, Architecture architecture)
    {
        var instruction = new Instruction
        {
            Address = address,
            Encoding = encoding,
            Size = 4,
            Condition = (Condition)(encoding >> 28),
        };

        bool isARMv5 = architecture == Architecture.ARMv5TE;
        if (instruction.Condition == Condition.NV)
        {
            return isARMv5 ? DecodeUnconditional(instruction, encoding) : instruction with { Opcode = Opcode.Undefined };
        }

        if ((encoding & 0x0FFFFFF0) == 0x012FFF10)
        {
            return instruction with { Opcode = Opcode.Bx, Rm = Register(encoding, 0) };
        }

        if (isARMv5 && DecodeARMv5(instruction, encoding) is { } armv5)
        {
            return armv5;
        }

        if ((encoding & 0x0FC000F0) == 0x00000090)
        {
            return DecodeMultiply(instruction, encoding);
        }

        if ((encoding & 0x0F8000F0) == 0x00800090)
        {
            return DecodeMultiplyLong(instruction, encoding);
        }

        if ((encoding & 0x0FB00FF0) == 0x01000090)
        {
            return instruction with
            {
                Opcode = Bit(encoding, 22) ? Opcode.Swpb : Opcode.Swp,
                Rn = Register(encoding, 16),
                Rd = Register(encoding, 12),
                Rm = Register(encoding, 0),
            };
        }

        if ((encoding & 0x0E000090) == 0x00000090)
        {
            return DecodeHalfwordTransfer(instruction, encoding, isARMv5);
        }

        if ((encoding & 0x0FBF0FFF) == 0x010F0000)
        {
            return instruction with
            {
                Opcode = Opcode.Mrs,
                Rd = Register(encoding, 12),
                UsesSPSR = Bit(encoding, 22),
            };
        }

        if ((encoding & 0x0FB0FFF0) == 0x0120F000)
        {
            return instruction with
            {
                Opcode = Opcode.Msr,
                UsesSPSR = Bit(encoding, 22),
                PSRFieldMask = (byte)((encoding >> 16) & 0xF),
                OperandKind = OperandKind.ImmediateShift,
                Rm = Register(encoding, 0),
            };
        }

        if ((encoding & 0x0FB0F000) == 0x0320F000)
        {
            return WithRotatedImmediate(instruction, encoding) with
            {
                Opcode = Opcode.Msr,
                UsesSPSR = Bit(encoding, 22),
                PSRFieldMask = (byte)((encoding >> 16) & 0xF),
            };
        }

        return ((encoding >> 25) & 7) switch
        {
            0b000 or 0b001 => DecodeDataProcessing(instruction, encoding),
            0b010 or 0b011 => DecodeSingleTransfer(instruction, encoding),
            0b100 => DecodeBlockTransfer(instruction, encoding),
            0b101 => DecodeBranch(instruction, encoding),
            0b111 when Bit(encoding, 24) => instruction with
            {
                Opcode = Opcode.Swi,
                Immediate = encoding & 0x00FFFFFF,
            },
            _ => instruction with { Opcode = Opcode.Undefined },
        };
    }

    private static Instruction DecodeUnconditional(Instruction instruction, uint encoding)
    {
        instruction = instruction with { Condition = Condition.AL };
        if ((encoding & 0x0E000000) == 0x0A000000)
        {
            int offset = (int)(encoding << 8) >> 6;
            return instruction with
            {
                Opcode = Opcode.Blx,
                OperandKind = OperandKind.Immediate,
                Target = instruction.Address + 8 + (uint)offset + (Bit(encoding, 24) ? 2u : 0),
            };
        }

        if ((encoding & 0x0D70F000) == 0x0550F000 && !(Bit(encoding, 25) && Bit(encoding, 4)))
        {
            instruction = instruction with
            {
                Opcode = Opcode.Pld,
                Rn = Register(encoding, 16),
                PreIndexed = true,
                AddOffset = Bit(encoding, 23),
            };

            return Bit(encoding, 25)
                ? WithImmediateShift(instruction, encoding)
                : instruction with { OperandKind = OperandKind.Immediate, Immediate = encoding & 0xFFF };
        }

        return instruction with { Opcode = Opcode.Undefined };
    }

    private static Instruction? DecodeARMv5(Instruction instruction, uint encoding)
    {
        if ((encoding & 0x0FFFFFF0) == 0x012FFF30)
        {
            return instruction with { Opcode = Opcode.Blx, Rm = Register(encoding, 0) };
        }

        if ((encoding & 0xFFF000F0) == 0xE1200070)
        {
            return instruction with { Opcode = Opcode.Bkpt, Immediate = ((encoding >> 4) & 0xFFF0) | (encoding & 0xF) };
        }

        if ((encoding & 0x0FFF0FF0) == 0x016F0F10)
        {
            return instruction with { Opcode = Opcode.Clz, Rd = Register(encoding, 12), Rm = Register(encoding, 0) };
        }

        if ((encoding & 0x0F900FF0) == 0x01000050)
        {
            return instruction with
            {
                Opcode = ((encoding >> 21) & 3) switch
                {
                    0 => Opcode.Qadd,
                    1 => Opcode.Qsub,
                    2 => Opcode.Qdadd,
                    _ => Opcode.Qdsub,
                },
                Rn = Register(encoding, 16),
                Rd = Register(encoding, 12),
                Rm = Register(encoding, 0),
            };
        }

        if ((encoding & 0x0F900090) == 0x01000080)
        {
            return DecodeHalfwordMultiply(instruction, encoding);
        }

        if ((encoding & 0x0F000F10) == 0x0E000F10)
        {
            return instruction with
            {
                Opcode = Bit(encoding, 20) ? Opcode.Mrc : Opcode.Mcr,
                CoprocessorOpcode = (byte)((encoding >> 21) & 7),
                Cn = Register(encoding, 16),
                Rd = Register(encoding, 12),
                Coprocessor = Register(encoding, 8),
                CoprocessorInfo = (byte)((encoding >> 5) & 7),
                Cm = Register(encoding, 0),
            };
        }

        return null;
    }

    private static Instruction DecodeHalfwordMultiply(Instruction instruction, uint encoding)
    {
        bool rmTop = Bit(encoding, 5);
        var opcode = ((encoding >> 21) & 3) switch
        {
            0 => Opcode.Smlaxy,
            1 => rmTop ? Opcode.Smulwy : Opcode.Smlawy,
            2 => Opcode.Smlalxy,
            _ => Opcode.Smulxy,
        };

        return instruction with
        {
            Opcode = opcode,
            Rd = Register(encoding, 16),
            Rn = Register(encoding, 12),
            Rs = Register(encoding, 8),
            Rm = Register(encoding, 0),
            RmTop = rmTop && opcode is not (Opcode.Smlawy or Opcode.Smulwy),
            RsTop = Bit(encoding, 6),
        };
    }

    private static Instruction DecodeMultiply(Instruction instruction, uint encoding)
    {
        return instruction with
        {
            Opcode = Bit(encoding, 21) ? Opcode.Mla : Opcode.Mul,
            SetsFlags = Bit(encoding, 20),
            Rd = Register(encoding, 16),
            Rn = Register(encoding, 12),
            Rs = Register(encoding, 8),
            Rm = Register(encoding, 0),
        };
    }

    private static Instruction DecodeMultiplyLong(Instruction instruction, uint encoding)
    {
        var opcode = ((encoding >> 21) & 3) switch
        {
            0 => Opcode.Umull,
            1 => Opcode.Umlal,
            2 => Opcode.Smull,
            _ => Opcode.Smlal,
        };

        return instruction with
        {
            Opcode = opcode,
            SetsFlags = Bit(encoding, 20),
            Rd = Register(encoding, 16),
            Rn = Register(encoding, 12),
            Rs = Register(encoding, 8),
            Rm = Register(encoding, 0),
        };
    }

    private static Instruction DecodeHalfwordTransfer(Instruction instruction, uint encoding, bool isARMv5)
    {
        bool load = Bit(encoding, 20);
        var opcode = ((encoding >> 5) & 3, load) switch
        {
            (1, false) => Opcode.Strh,
            (1, true) => Opcode.Ldrh,
            (2, true) => Opcode.Ldrsb,
            (3, true) => Opcode.Ldrsh,
            (2, false) when isARMv5 => Opcode.Ldrd,
            (3, false) when isARMv5 => Opcode.Strd,
            _ => Opcode.Undefined,
        };

        if (opcode == Opcode.Undefined)
        {
            return instruction with { Opcode = opcode };
        }

        bool preIndexed = Bit(encoding, 24);
        instruction = instruction with
        {
            Opcode = opcode,
            Rn = Register(encoding, 16),
            Rd = Register(encoding, 12),
            PreIndexed = preIndexed,
            AddOffset = Bit(encoding, 23),
            WriteBack = !preIndexed || Bit(encoding, 21),
        };

        if (Bit(encoding, 22))
        {
            return instruction with
            {
                OperandKind = OperandKind.Immediate,
                Immediate = ((encoding >> 4) & 0xF0) | (encoding & 0xF),
            };
        }

        return instruction with
        {
            OperandKind = OperandKind.ImmediateShift,
            Rm = Register(encoding, 0),
        };
    }

    private static Instruction DecodeDataProcessing(Instruction instruction, uint encoding)
    {
        var opcode = (Opcode)((encoding >> 21) & 0xF);
        bool setsFlags = Bit(encoding, 20);

        if (opcode is >= Opcode.Tst and <= Opcode.Cmn && !setsFlags)
        {
            return instruction with { Opcode = Opcode.Undefined };
        }

        instruction = instruction with
        {
            Opcode = opcode,
            SetsFlags = setsFlags,
            Rn = Register(encoding, 16),
            Rd = Register(encoding, 12),
        };

        if (Bit(encoding, 25))
        {
            return WithRotatedImmediate(instruction, encoding);
        }

        if (!Bit(encoding, 4))
        {
            return WithImmediateShift(instruction, encoding);
        }

        return instruction with
        {
            OperandKind = OperandKind.RegisterShift,
            Rm = Register(encoding, 0),
            Rs = Register(encoding, 8),
            ShiftType = (ShiftType)((encoding >> 5) & 3),
        };
    }

    private static Instruction DecodeSingleTransfer(Instruction instruction, uint encoding)
    {
        bool registerOffset = Bit(encoding, 25);
        if (registerOffset && Bit(encoding, 4))
        {
            return instruction with { Opcode = Opcode.Undefined };
        }

        var opcode = (Bit(encoding, 20), Bit(encoding, 22)) switch
        {
            (false, false) => Opcode.Str,
            (false, true) => Opcode.Strb,
            (true, false) => Opcode.Ldr,
            (true, true) => Opcode.Ldrb,
        };

        bool preIndexed = Bit(encoding, 24);
        instruction = instruction with
        {
            Opcode = opcode,
            Rn = Register(encoding, 16),
            Rd = Register(encoding, 12),
            PreIndexed = preIndexed,
            AddOffset = Bit(encoding, 23),
            WriteBack = !preIndexed || Bit(encoding, 21),
        };

        if (registerOffset)
        {
            return WithImmediateShift(instruction, encoding);
        }

        return instruction with
        {
            OperandKind = OperandKind.Immediate,
            Immediate = encoding & 0xFFF,
        };
    }

    private static Instruction DecodeBlockTransfer(Instruction instruction, uint encoding)
    {
        return instruction with
        {
            Opcode = Bit(encoding, 20) ? Opcode.Ldm : Opcode.Stm,
            Rn = Register(encoding, 16),
            RegisterList = (ushort)encoding,
            PreIndexed = Bit(encoding, 24),
            AddOffset = Bit(encoding, 23),
            PSROrUserBank = Bit(encoding, 22),
            WriteBack = Bit(encoding, 21),
        };
    }

    private static Instruction DecodeBranch(Instruction instruction, uint encoding)
    {
        int offset = (int)(encoding << 8) >> 6;
        return instruction with
        {
            Opcode = Bit(encoding, 24) ? Opcode.Bl : Opcode.B,
            Target = instruction.Address + 8 + (uint)offset,
        };
    }

    private static Instruction WithRotatedImmediate(Instruction instruction, uint encoding)
    {
        int rotation = (int)((encoding >> 8) & 0xF) * 2;
        return instruction with
        {
            OperandKind = OperandKind.Immediate,
            Immediate = BitOperations.RotateRight(encoding & 0xFF, rotation),
            ShiftAmount = (byte)rotation,
        };
    }

    private static Instruction WithImmediateShift(Instruction instruction, uint encoding)
    {
        var shiftType = (ShiftType)((encoding >> 5) & 3);
        uint amount = (encoding >> 7) & 0x1F;

        if (amount == 0)
        {
            switch (shiftType)
            {
                case ShiftType.LSR:
                case ShiftType.ASR:
                    amount = 32;
                    break;
                case ShiftType.ROR:
                    shiftType = ShiftType.RRX;
                    amount = 1;
                    break;
            }
        }

        return instruction with
        {
            OperandKind = OperandKind.ImmediateShift,
            Rm = Register(encoding, 0),
            ShiftType = shiftType,
            ShiftAmount = (byte)amount,
        };
    }

    private static bool Bit(uint encoding, int bit) => ((encoding >> bit) & 1) != 0;

    private static byte Register(uint encoding, int lowBit) => (byte)((encoding >> lowBit) & 0xF);
}
