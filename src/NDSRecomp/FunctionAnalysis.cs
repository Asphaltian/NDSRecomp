using NDSRecomp.ARM;
using static NDSRecomp.ARM.Registers;

namespace NDSRecomp;

internal enum FlowKind
{
    Next,
    Branch,
    TailCall,
    Call,
    NoreturnCall,
    JumpTable,
    BranchExchange,
    IndirectCall,
    IndirectJump,
    ExceptionReturn,
}

internal readonly record struct Flow(FlowKind Kind, uint Target = 0, uint[]? Targets = null, Function? Callee = null)
{
    public IEnumerable<uint> LocalTargets => Kind switch
    {
        FlowKind.Branch or FlowKind.IndirectCall => [Target],
        FlowKind.BranchExchange => [Target & ~1u],
        FlowKind.JumpTable => Targets!,
        _ => [],
    };

    public bool FallsThrough(Instruction instruction)
    {
        return instruction.Condition != Condition.AL || Kind is FlowKind.Next or FlowKind.Call;
    }
}

internal sealed class FunctionAnalysis
{
    private const uint SoftReset = 0x00;
    private const ushort PreservedAcrossCalls = 0x0FF0;
    private const int JumpTablePathLimit = 32;
    private const int KnownIndexWorldLimit = 64;

    private readonly Context _context;
    private readonly SortedDictionary<uint, Instruction> _instructions = [];
    private readonly SortedSet<uint> _labels = [];
    private readonly SortedSet<uint> _landingPads = [];
    private readonly SortedSet<uint> _secondReturns = [];
    private readonly Dictionary<uint, Flow> _flows = [];
    private readonly List<string> _errors = [];
    private readonly List<(uint Target, bool IsThumb)> _unknownTargets = [];
    private readonly Stack<(uint Address, bool IsThumb)> _pending = [];
    private readonly List<(Instruction Dispatch, List<(Instruction Instruction, bool IsTaken)> Path, int Depth)> _jumpTables = [];
    private readonly IReadOnlySet<uint> _joins;

    private FunctionAnalysis(Context context, Function function, IReadOnlySet<uint> joins)
    {
        _context = context;
        Function = function;
        _joins = joins;
    }

    public Function Function { get; }

    public IReadOnlyCollection<Instruction> Instructions => _instructions.Values;

    public IReadOnlySet<uint> Labels => _labels;

    public IReadOnlySet<uint> LandingPads => _landingPads;

    public IReadOnlyDictionary<uint, Flow> Flows => _flows;

    public Function? FallsThroughTo { get; private set; }

    public IReadOnlyList<string> Errors => _errors;

    public IReadOnlyList<(uint Target, bool IsThumb)> UnknownTargets => _unknownTargets;

    public static FunctionAnalysis Analyze(Context context, Function function)
    {
        var joins = new HashSet<uint>();
        while (true)
        {
            var analysis = new FunctionAnalysis(context, function, joins);
            analysis._pending.Push((function.Address, function.IsThumb));

            while (analysis._pending.TryPop(out var start))
            {
                analysis.FollowPath(start.Address, start.IsThumb);
            }

            analysis.CheckLiterals();
            analysis.CheckHooks();
            analysis.FindLandingPads();

            var found = analysis.FindJumpTableJoins();
            if (found.Count == 0)
            {
                return analysis;
            }

            joins.UnionWith(found);
        }
    }

    private HashSet<uint> FindJumpTableJoins()
    {
        var sources = new Dictionary<uint, List<uint>>();
        foreach (var (address, flow) in _flows)
        {
            foreach (uint target in flow.LocalTargets)
            {
                if (!sources.TryGetValue(target, out var list))
                {
                    sources[target] = list = [];
                }

                list.Add(address);
            }
        }

        var joins = new HashSet<uint>();
        foreach (var (dispatch, path, depth) in _jumpTables)
        {
            for (int step = -1; step < depth; step++)
            {
                uint address = step < 0 ? dispatch.Address : path[step].Instruction.Address;
                uint? branch = path[step + 1].IsTaken ? path[step + 1].Instruction.Address : null;
                bool joined = _landingPads.Contains(address)
                    || sources.GetValueOrDefault(address, []).Any(source => source != branch)
                    || (branch is not null && FallsInto(address, out _));
                if (joined)
                {
                    joins.Add(address);
                }
            }
        }

        return joins;
    }

    private void FindLandingPads()
    {
        foreach (uint address in _secondReturns)
        {
            _landingPads.Add(address);
            _labels.Add(address);
        }

        foreach (uint address in _context.PCRelativeAddressesIn(Function))
        {
            bool startsBlock = _labels.Contains(address)
                || (_instructions.ContainsKey(address)
                    && TryGetInstructionBefore(address, out var previous)
                    && _flows.ContainsKey(previous.Address));
            if (startsBlock)
            {
                _landingPads.Add(address);
                _labels.Add(address);
            }
        }
    }

    private void CheckHooks()
    {
        foreach (var hook in _context.Hooks[Function.Name])
        {
            if (hook.BeforeAddress != 0 && !_instructions.ContainsKey(hook.BeforeAddress))
            {
                _errors.Add($"The hook before 0x{hook.BeforeAddress:X8} is not at an instruction that runs. Move it to one that does.");
            }
        }
    }

    private void CheckLiterals()
    {
        foreach (var load in _instructions.Values)
        {
            if (load is not { Opcode: Opcode.Ldr, Rn: PC, OperandKind: OperandKind.Immediate, PreIndexed: true })
            {
                continue;
            }

            uint literal = load.LiteralAddress;

            bool isDecoded = _instructions.ContainsKey(literal)
                || _instructions.ContainsKey(literal + 2)
                || (_instructions.TryGetValue(literal - 2, out var previous) && previous.Size == 4);

            if (isDecoded)
            {
                _errors.Add($"The literal at 0x{literal:X8} loaded by 0x{load.Address:X8} was also decoded as an instruction. If the call before it never returns, add that function to input.noreturn_funcs.");
            }
        }
    }

    private bool IsFarJump(Instruction instruction)
    {
        return instruction.Opcode == Opcode.Bl
            && instruction.Target > Function.Address
            && instruction.Target < Function.End
            && _context.FindFunction(Function.Section, instruction.Target) is null
            && !UsesReturnAddress(instruction.Target, instruction.IsThumb);
    }

    private bool UsesReturnAddress(uint start, bool isThumb)
    {
        var visited = new HashSet<uint>();
        var pending = new Stack<uint>([start]);
        while (pending.TryPop(out uint address))
        {
            while (address + (isThumb ? 2 : 4) <= Function.End && visited.Add(address))
            {
                var instruction = Decode(address, isThumb);
                if (instruction.Reads(LR))
                {
                    return true;
                }

                if (instruction.Opcode == Opcode.B && Contains(instruction.Target))
                {
                    pending.Push(instruction.Target);
                }

                bool endsPath = instruction.Opcode == Opcode.Undefined
                    || (instruction.Condition == Condition.AL
                        && (instruction.Writes(LR) || instruction.Writes(PC) || instruction.Opcode is Opcode.B or Opcode.Bx));
                if (endsPath)
                {
                    break;
                }

                address += instruction.Size;
            }
        }

        return false;
    }

    private Instruction Decode(uint address, bool isThumb)
    {
        if (!isThumb)
        {
            return ARMDecoder.Decode(address, _context.ReadUInt32(Function, address), _context.Architecture);
        }

        ushort next = address + 4 <= Function.End ? _context.ReadUInt16(Function, address + 2) : (ushort)0;
        return ThumbDecoder.Decode(address, _context.ReadUInt16(Function, address), next, _context.Architecture);
    }

    private void FollowPath(uint address, bool isThumb)
    {
        while (!_instructions.ContainsKey(address))
        {
            if (address == Function.End)
            {
                FallsThroughTo = _context.FindFunction(Function.Section, address);
                if (FallsThroughTo is null)
                {
                    _errors.Add("Execution runs past the end and no function starts there. If the call before it never returns, add that function to input.noreturn_funcs; otherwise fix the size with input.function_sizes.");
                }

                return;
            }

            if (address + (isThumb ? 2 : 4) > Function.End)
            {
                _errors.Add($"The instruction at 0x{address:X8} runs past the end. Fix the size with input.function_sizes.");
                return;
            }

            var instruction = Decode(address, isThumb);
            if (instruction.Opcode == Opcode.Undefined)
            {
                _errors.Add($"Undefined instruction 0x{instruction.Encoding:X} at 0x{address:X8}. If it isn't code, check input.noreturn_funcs; if it is, replace the function through patches.ignored.");
                return;
            }

            if (Unsupported(instruction) is { } problem)
            {
                _errors.Add($"0x{address:X8} ({Disassembler.Format(instruction)}) {problem}");
                return;
            }

            _instructions.Add(address, instruction);

            var flow = Classify(instruction);
            if (flow.Kind != FlowKind.Next)
            {
                _flows.Add(address, flow);
            }

            bool targetIsThumb = flow.Kind == FlowKind.BranchExchange ? (flow.Target & 1) != 0 : isThumb;
            foreach (uint target in flow.LocalTargets)
            {
                AddLabel(target, targetIsThumb);
            }

            if (!flow.FallsThrough(instruction))
            {
                return;
            }

            address += instruction.Size;
        }
    }

    private Flow Classify(Instruction instruction)
    {
        switch (instruction.Opcode)
        {
            case Opcode.B when Contains(instruction.Target):
                return new Flow(FlowKind.Branch, instruction.Target);

            case Opcode.B:
                return ResolveCall(instruction, FlowKind.TailCall, instruction.IsThumb);

            case Opcode.Bl when IsFarJump(instruction):
                return new Flow(FlowKind.Branch, instruction.Target);

            case Opcode.Bl:
                return ResolveCall(instruction, FlowKind.Call, instruction.IsThumb);

            case Opcode.Blx when instruction.OperandKind == OperandKind.Immediate:
                return ResolveCall(instruction, FlowKind.Call, !instruction.IsThumb);

            case Opcode.Blx:
                return new Flow(FlowKind.IndirectCall, instruction.Address + instruction.Size);

            case Opcode.Swi when instruction.BIOSFunction == SoftReset:
                return new Flow(FlowKind.NoreturnCall);

            case var _ when instruction.IsExceptionReturn:
                return new Flow(FlowKind.ExceptionReturn);

            case Opcode.Bx:
                return FindBranchExchange(instruction) ?? FindIndirectCall(instruction) ?? new Flow(FlowKind.IndirectJump);

            case Opcode.Mov when instruction.Rd == PC:
                return FindIndirectCall(instruction) ?? new Flow(FlowKind.IndirectJump);

            case Opcode.Add when IsJumpTableDispatch(instruction):
                return FindJumpTable(instruction);

            case var _ when instruction.Writes(PC):
                return new Flow(FlowKind.IndirectJump);

            default:
                return new Flow(FlowKind.Next);
        }
    }

    private Flow ResolveCall(Instruction instruction, FlowKind kind, bool targetIsThumb)
    {
        var resolution = _context.ResolveCall(Function, instruction.Target, out var callee);
        if (resolution is CallResolution.CreateStatic or CallResolution.NoMatch)
        {
            _unknownTargets.Add((instruction.Target, targetIsThumb));
            _errors.Add(resolution == CallResolution.NoMatch
                ? $"0x{instruction.Address:X8} goes to 0x{instruction.Target:X8}, which isn't a function in any section. Check the symbols."
                : $"0x{instruction.Address:X8} goes to 0x{instruction.Target:X8}, which isn't a function yet.");
        }

        if (kind == FlowKind.Call && callee is { IsNoreturn: true })
        {
            kind = FlowKind.NoreturnCall;
        }

        if (kind == FlowKind.Call && callee is { ReturnsTwice: true })
        {
            _secondReturns.Add(instruction.Address + instruction.Size);
        }

        return new Flow(kind, instruction.Target, Callee: callee);
    }

    private static string? Unsupported(Instruction i)
    {
        bool usesPC = i.Opcode switch
        {
            <= Opcode.Mvn => i.OperandKind == OperandKind.RegisterShift && i.Rs == PC,
            Opcode.Mul => i.Rd == PC || i.Rs == PC || i.Rm == PC,
            >= Opcode.Mla and <= Opcode.Smlal => i.Rd == PC || i.Rn == PC || i.Rs == PC || i.Rm == PC,
            Opcode.Mrs => i.Rd == PC,
            Opcode.Msr => i.OperandKind == OperandKind.ImmediateShift && i.Rm == PC,
            Opcode.Swp or Opcode.Swpb => i.Rd == PC || i.Rn == PC || i.Rm == PC,
            >= Opcode.Ldr and <= Opcode.Strh => (i.OperandKind == OperandKind.ImmediateShift && i.Rm == PC) || (i.WriteBack && i.Rn == PC),
            Opcode.Ldm or Opcode.Stm => i.Rn == PC,
            Opcode.Blx => i.OperandKind == OperandKind.None && i.Rm == PC,
            Opcode.Clz => i.Rd == PC || i.Rm == PC,
            >= Opcode.Qadd and <= Opcode.Qdsub => i.Rd == PC || i.Rn == PC || i.Rm == PC,
            Opcode.Smlaxy or Opcode.Smlawy or Opcode.Smlalxy => i.Rd == PC || i.Rn == PC || i.Rs == PC || i.Rm == PC,
            Opcode.Smulwy or Opcode.Smulxy => i.Rd == PC || i.Rs == PC || i.Rm == PC,
            Opcode.Ldrd or Opcode.Strd or Opcode.Pld => (i.OperandKind == OperandKind.ImmediateShift && i.Rm == PC) || (i.WriteBack && i.Rn == PC),
            Opcode.Mcr or Opcode.Mrc => i.Rd == PC,
            _ => false,
        };

        if (usesPC)
        {
            return "uses R15 where the CPU only takes R0 to R14. Replace the function through patches.ignored.";
        }

        if (i.Opcode == Opcode.Bkpt)
        {
            return "is a breakpoint, which isn't supported. Replace the function through patches.ignored.";
        }

        if (i.Opcode is Opcode.Ldrd or Opcode.Strd)
        {
            if (i.Rd % 2 != 0 || i.Rd == LR)
            {
                return "transfers a register pair that doesn't start at an even register from R0 to R12, which the CPU requires. Replace the function through patches.ignored.";
            }

            bool InPair(byte register) => register == i.Rd || register == i.Rd + 1;
            if ((i.WriteBack && InPair(i.Rn)) || (i.Opcode == Opcode.Strd && i.OperandKind == OperandKind.ImmediateShift && InPair(i.Rm)))
            {
                return "uses one of the pair it transfers as its base or index register, which the CPU doesn't allow. Replace the function through patches.ignored.";
            }
        }

        if (i.IsUserBankTransfer && i.WriteBack && i.Rn >= 8)
        {
            return "writes back a banked base register in a user bank transfer, which leaves it unclear whose bank gets the new base. Replace the function through patches.ignored.";
        }

        if (i.Opcode is Opcode.Ldm or Opcode.Stm && i.RegisterList == 0)
        {
            return "has an empty register list, which isn't supported. Replace the function through patches.ignored.";
        }

        bool computesJumpFromPC = i.Rd == PC && i.Rn == PC && i.OperandKind != OperandKind.Immediate
            && (i.Opcode is Opcode.Ldr || (i.Opcode <= Opcode.Mvn && !i.IsCompare && !IsJumpTableDispatch(i)));
        if (computesJumpFromPC)
        {
            return "jumps to an address worked out from R15, which isn't recognized as a jump table. Replace the function through patches.ignored.";
        }

        return null;
    }

    private bool Contains(uint address) => address >= Function.Address && address < Function.End;

    private void AddLabel(uint address, bool isThumb)
    {
        if (!Contains(address))
        {
            return;
        }

        _labels.Add(address);
        _pending.Push((address, isThumb));
    }

    private bool TryGetPrevious(Instruction instruction, out Instruction previous)
    {
        return _instructions.TryGetValue(instruction.Address - (instruction.IsThumb ? 2u : 4u), out previous);
    }

    private Flow? FindBranchExchange(Instruction bx)
    {
        uint target;
        if (bx.Rm == PC)
        {
            target = bx.AlignedPC;
        }
        else if (TryGetPrevious(bx, out var adr)
            && adr is { Opcode: Opcode.Add, Rn: PC, OperandKind: OperandKind.Immediate }
            && adr.Rd == bx.Rm
            && adr.Condition == bx.Condition)
        {
            target = adr.AlignedPC + adr.Immediate;
        }
        else
        {
            return null;
        }

        if (!Contains(target & ~1u))
        {
            _errors.Add($"The BX at 0x{bx.Address:X8} leaves the function for 0x{target & ~1u:X8}. Fix the size with input.function_sizes.");
        }

        return new Flow(FlowKind.BranchExchange, target);
    }

    private Flow? FindIndirectCall(Instruction branch)
    {
        if (!TryGetPrevious(branch, out var link) || link.Rd != LR || link.Condition != branch.Condition)
        {
            return null;
        }

        uint returnAddress;
        if (link is { Opcode: Opcode.Mov, OperandKind: OperandKind.ImmediateShift, Rm: PC, ShiftAmount: 0 })
        {
            returnAddress = link.Address + (link.IsThumb ? 4u : 8u);
        }
        else if (link is { Opcode: Opcode.Add, Rn: PC, OperandKind: OperandKind.Immediate })
        {
            returnAddress = link.AlignedPC + link.Immediate;
        }
        else
        {
            return null;
        }

        if (!Contains(returnAddress))
        {
            _errors.Add($"The indirect call at 0x{branch.Address:X8} returns to 0x{returnAddress:X8}, outside the function. Fix the size with input.function_sizes.");
        }

        return new Flow(FlowKind.IndirectCall, returnAddress);
    }

    private static bool IsJumpTableDispatch(Instruction i)
    {
        return i is { Opcode: Opcode.Add, Rd: PC, Rn: PC, OperandKind: OperandKind.ImmediateShift, SetsFlags: false };
    }

    private Flow FindJumpTable(Instruction dispatch)
    {
        var path = PathBefore(dispatch).ToList();
        uint[]? targets = dispatch.IsThumb ? FindThumbJumpTable(dispatch, path) : FindARMJumpTable(dispatch, path);
        return targets is null ? new Flow(FlowKind.IndirectJump) : new Flow(FlowKind.JumpTable, Targets: targets.Distinct().ToArray());
    }

    private uint[]? FindARMJumpTable(Instruction dispatch, List<(Instruction Instruction, bool IsTaken)> path)
    {
        if (dispatch is not { ShiftType: ShiftType.LSL, ShiftAmount: 2 } || dispatch.Rm == PC)
        {
            AddUnrecognizedJump(dispatch);
            return null;
        }

        uint table = dispatch.Address + 8;
        if (KnownIndexes(dispatch.Rm, path) is var (indexes, depth))
        {
            uint[] targets = [.. indexes.Select(index => table + ((uint)index * 4))];
            if (targets.Any(target => target >= Function.End))
            {
                _errors.Add($"The jump at 0x{dispatch.Address:X8} can go past the end of the function. Fix the size with input.function_sizes.");
                return null;
            }

            _jumpTables.Add((dispatch, path, depth));
            return targets;
        }

        if (FindJumpTableEntryCount(dispatch, ValueOf(dispatch.Rm, path, 0), path) is not { } entryCount)
        {
            return null;
        }

        if (table + (entryCount * 4) > Function.End)
        {
            _errors.Add($"The jump table for 0x{dispatch.Address:X8} is outside the function. Fix the size with input.function_sizes.");
            return null;
        }

        return Enumerable.Range(0, (int)entryCount).Select(i => table + ((uint)i * 4)).ToArray();
    }

    private uint[]? FindThumbJumpTable(Instruction dispatch, List<(Instruction Instruction, bool IsTaken)> path)
    {
        if (FindWriter(dispatch.Rm, dispatch) is not { Opcode: Opcode.Mov, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.ASR, ShiftAmount: 16 } extend
            || FindWriter(extend.Rm, extend) is not { Opcode: Opcode.Mov, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSL, ShiftAmount: 16 } shift
            || FindWriter(shift.Rm, shift) is not { Opcode: Opcode.Ldrh, OperandKind: OperandKind.Immediate, PreIndexed: true, AddOffset: true } load
            || FindWriter(load.Rn, load) is not { Opcode: Opcode.Add, Rm: PC, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSL, ShiftAmount: 0 } addPC
            || FindWriter(addPC.Rn, addPC) is not { Opcode: Opcode.Add, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSL, ShiftAmount: 0 } scale
            || scale.Rn != scale.Rm
            || path.FindIndex(step => step.Instruction.Address == scale.Address) is var scaleStep && scaleStep < 0)
        {
            AddUnrecognizedJump(dispatch);
            return null;
        }

        if (FindJumpTableEntryCount(dispatch, ValueOf(scale.Rn, path, scaleStep + 1).DependingOn(scaleStep), path) is not { } entryCount)
        {
            return null;
        }

        uint table = addPC.Address + 4 + load.Immediate;
        if (table < Function.Address || table + (entryCount * 2) > Function.End)
        {
            _errors.Add($"The jump table for 0x{dispatch.Address:X8} is outside the function. Fix the size with input.function_sizes.");
            return null;
        }

        var targets = new uint[entryCount];
        for (uint i = 0; i < targets.Length; i++)
        {
            uint target = dispatch.Address + 4 + (uint)(short)_context.ReadUInt16(Function, table + (i * 2));
            if (!Contains(target))
            {
                _errors.Add($"Jump table entry 0x{target:X8} at 0x{table + (i * 2):X8} is outside the function. Fix the size with input.function_sizes.");
                return null;
            }

            targets[i] = target;
        }

        return targets;
    }

    private void AddUnrecognizedJump(Instruction dispatch)
    {
        _errors.Add($"0x{dispatch.Address:X8} ({Disassembler.Format(dispatch)}) jumps to an address worked out from R15, which isn't recognized as a jump table. Replace the function through patches.ignored.");
    }

    private uint? FindJumpTableEntryCount(Instruction dispatch, SymbolicValue index, List<(Instruction Instruction, bool IsTaken)> path)
    {
        var nonNegative = NonNegativeValues(path, dispatch.Condition);
        var lower = nonNegative.Where(value => value - index is { IsConstant: true, Constant: 0 }).MinBy(value => value.Depth);
        var upper = nonNegative.Select(value => value + index).Where(sum => sum is { IsConstant: true, Constant: >= 0 }).MinBy(sum => sum.Constant);
        if (lower is null || upper is null)
        {
            _errors.Add($"The jump table dispatch at 0x{dispatch.Address:X8} has no range check before it, so its size is unknown. Replace the function through patches.ignored.");
            return null;
        }

        if (upper.Constant >= Function.Size)
        {
            _errors.Add($"The jump table for 0x{dispatch.Address:X8} is outside the function. Fix the size with input.function_sizes.");
            return null;
        }

        _jumpTables.Add((dispatch, path, Math.Max(lower.Depth, upper.Depth)));
        return (uint)upper.Constant + 1;
    }

    private List<SymbolicValue> NonNegativeValues(List<(Instruction Instruction, bool IsTaken)> path, Condition dispatchCondition)
    {
        var values = new List<SymbolicValue>();
        AddNonNegativeValues(values, path, -1, dispatchCondition);
        for (int step = 0; step < path.Count; step++)
        {
            var (instruction, isTaken) = path[step];
            if (instruction.Opcode == Opcode.B && instruction.Condition != Condition.AL)
            {
                AddNonNegativeValues(values, path, step, isTaken ? instruction.Condition : (Condition)((byte)instruction.Condition ^ 1));
            }
        }

        return values;
    }

    private void AddNonNegativeValues(List<SymbolicValue> values, List<(Instruction Instruction, bool IsTaken)> path, int user, Condition holds)
    {
        if (holds is not (Condition.LS or Condition.GE or Condition.LE or Condition.PL))
        {
            return;
        }

        int setter = user + 1;
        while (setter < path.Count && FlagLiveness.Writes(path[setter].Instruction) == StatusFlags.None)
        {
            setter++;
        }

        if (setter == path.Count || path[setter].Instruction is not { Opcode: Opcode.Cmp or Opcode.Sub, Condition: Condition.AL } compare)
        {
            return;
        }

        var left = ValueOf(compare.Rn, path, setter + 1).DependingOn(setter);
        var right = compare.OperandKind switch
        {
            OperandKind.Immediate => SymbolicValue.Known((int)compare.Immediate, setter),
            OperandKind.ImmediateShift when compare is { ShiftType: ShiftType.LSL, ShiftAmount: 0 } => ValueOf(compare.Rm, path, setter + 1).DependingOn(setter),
            _ => null,
        };

        if (right is null)
        {
            return;
        }

        switch (holds)
        {
            case Condition.LS when right is { IsConstant: true, Constant: >= 0 }:
                values.Add(left);
                values.Add(right - left);
                break;

            case Condition.GE or Condition.PL:
                values.Add(left - right);
                break;

            case Condition.LE:
                values.Add(right - left);
                break;
        }
    }

    private SymbolicValue ValueOf(byte register, List<(Instruction Instruction, bool IsTaken)> path, int from)
    {
        for (int step = from; step < path.Count; step++)
        {
            var writer = path[step].Instruction;
            if (!MayChange(writer, register))
            {
                continue;
            }

            var unknown = SymbolicValue.Unknown(writer.Address, register, step);
            bool readsPC = writer.Rn == PC || (writer.OperandKind == OperandKind.ImmediateShift && writer.Rm == PC);
            if (!writer.Writes(register) || writer.Condition != Condition.AL || (readsPC && writer.Opcode != Opcode.Ldr))
            {
                return unknown;
            }

            return writer switch
            {
                { Opcode: Opcode.Mov, OperandKind: OperandKind.Immediate } => SymbolicValue.Known((int)writer.Immediate, step),
                { Opcode: Opcode.Mov, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSL, ShiftAmount: 0 }
                    => ValueOf(writer.Rm, path, step + 1).DependingOn(step),
                { Opcode: Opcode.Add or Opcode.Sub, OperandKind: OperandKind.Immediate }
                    => Combine(writer.Opcode, ValueOf(writer.Rn, path, step + 1), SymbolicValue.Known((int)writer.Immediate, step)),
                { Opcode: Opcode.Add or Opcode.Sub, OperandKind: OperandKind.ImmediateShift, ShiftType: ShiftType.LSL, ShiftAmount: 0 }
                    => Combine(writer.Opcode, ValueOf(writer.Rn, path, step + 1), ValueOf(writer.Rm, path, step + 1)).DependingOn(step),
                { Opcode: Opcode.Ldr, Rn: PC, OperandKind: OperandKind.Immediate, PreIndexed: true } when ReadLiteral(writer) is { } literal
                    => SymbolicValue.Known((int)literal, step),
                _ => unknown,
            };
        }

        return SymbolicValue.Unknown(uint.MaxValue, register, -1);
    }

    private static SymbolicValue Combine(Opcode opcode, SymbolicValue left, SymbolicValue right) => opcode == Opcode.Add ? left + right : left - right;

    private (int[] Indexes, int Depth)? KnownIndexes(byte register, List<(Instruction Instruction, bool IsTaken)> path)
    {
        var worlds = new List<Dictionary<byte, (int Value, int Depth)>> { new() };
        for (int step = path.Count - 1; step >= 0; step--)
        {
            var instruction = path[step].Instruction;
            byte[] changed = [.. Enumerable.Range(0, PC).Select(r => (byte)r).Where(r => MayChange(instruction, r))];
            if (changed.Length == 0)
            {
                continue;
            }

            var next = new List<Dictionary<byte, (int Value, int Depth)>>();
            foreach (var world in worlds)
            {
                var executed = new Dictionary<byte, (int Value, int Depth)>(world);
                foreach (byte changedRegister in changed)
                {
                    executed.Remove(changedRegister);
                }

                if (Evaluate(instruction, world, step) is { } value)
                {
                    executed[instruction.Rd] = value;
                }

                next.Add(executed);
                if (instruction.Condition != Condition.AL)
                {
                    next.Add(world);
                }
            }

            worlds = [.. next.DistinctBy(world => string.Join(",", world.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}={entry.Value}")))];
            if (worlds.Count > KnownIndexWorldLimit)
            {
                return null;
            }
        }

        if (worlds.Any(world => !world.TryGetValue(register, out var value) || value.Value < 0))
        {
            return null;
        }

        return ([.. worlds.Select(world => world[register].Value).Distinct().Order()], worlds.Max(world => world[register].Depth));
    }

    private (int Value, int Depth)? Evaluate(Instruction instruction, Dictionary<byte, (int Value, int Depth)> world, int step)
    {
        if (instruction.Rd == PC)
        {
            return null;
        }

        if (instruction is { Opcode: Opcode.Ldr, Rn: PC, OperandKind: OperandKind.Immediate, PreIndexed: true } && ReadLiteral(instruction) is { } literal)
        {
            return ((int)literal, step);
        }

        if (instruction.Opcode is not (Opcode.Mov or Opcode.Add or Opcode.Sub or Opcode.Rsb))
        {
            return null;
        }

        (int Value, int Depth)? operand = instruction switch
        {
            { OperandKind: OperandKind.Immediate } => ((int)instruction.Immediate, step),
            { OperandKind: OperandKind.ImmediateShift, Rm: not PC } when world.TryGetValue(instruction.Rm, out var shifted) => instruction switch
            {
                { ShiftType: ShiftType.LSL } => (shifted.Value << instruction.ShiftAmount, Math.Max(shifted.Depth, step)),
                { ShiftType: ShiftType.LSR, ShiftAmount: > 0 } => ((int)((uint)shifted.Value >> instruction.ShiftAmount), Math.Max(shifted.Depth, step)),
                { ShiftType: ShiftType.ASR, ShiftAmount: > 0 } => (shifted.Value >> instruction.ShiftAmount, Math.Max(shifted.Depth, step)),
                _ => null,
            },
            _ => null,
        };

        if (operand is not { } right)
        {
            return null;
        }

        if (instruction.Opcode == Opcode.Mov)
        {
            return right;
        }

        if (instruction.Rn == PC || !world.TryGetValue(instruction.Rn, out var left))
        {
            return null;
        }

        int result = instruction.Opcode switch
        {
            Opcode.Add => left.Value + right.Value,
            Opcode.Sub => left.Value - right.Value,
            _ => right.Value - left.Value,
        };

        return (result, Math.Max(left.Depth, right.Depth));
    }

    private Instruction? FindWriter(byte register, Instruction before)
    {
        foreach (var (instruction, _) in PathBefore(before))
        {
            if (instruction.Writes(register))
            {
                return instruction;
            }

            if (MayChange(instruction, register))
            {
                return null;
            }
        }

        return null;
    }

    private static bool MayChange(Instruction instruction, byte register)
    {
        return instruction.Writes(register)
            || instruction.Opcode == Opcode.Swi
            || (instruction.Opcode is Opcode.Bl or Opcode.Blx && (PreservedAcrossCalls & (1 << register)) == 0);
    }

    private IEnumerable<(Instruction Instruction, bool IsTaken)> PathBefore(Instruction start)
    {
        uint address = start.Address;
        for (int step = 0; step < JumpTablePathLimit && !_joins.Contains(address); step++)
        {
            bool isTaken = !FallsInto(address, out var previous);
            if (isTaken)
            {
                var branches = _flows.Where(f => f.Value.Kind == FlowKind.Branch && f.Value.Target == address).Take(2).ToList();
                if (branches.Count != 1)
                {
                    yield break;
                }

                previous = _instructions[branches[0].Key];
            }

            yield return (previous, isTaken);
            address = previous.Address;
        }
    }

    private bool FallsInto(uint address, out Instruction previous)
    {
        return TryGetInstructionBefore(address, out previous)
            && !(_flows.TryGetValue(previous.Address, out var flow) && !flow.FallsThrough(previous));
    }

    private bool TryGetInstructionBefore(uint address, out Instruction previous)
    {
        return _instructions.TryGetValue(address - 2, out previous)
            || (_instructions.TryGetValue(address - 4, out previous) && previous.Size == 4);
    }

    private uint? ReadLiteral(Instruction load)
    {
        uint address = load.LiteralAddress;
        return address >= Function.Address && address + 4 <= Function.End ? _context.ReadUInt32(Function, address) : null;
    }
}
