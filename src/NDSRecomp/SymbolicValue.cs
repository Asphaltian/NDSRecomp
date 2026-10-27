namespace NDSRecomp;

internal sealed class SymbolicValue
{
    private readonly Dictionary<(uint Address, byte Register), int> _unknowns;

    private SymbolicValue(Dictionary<(uint Address, byte Register), int> unknowns, long constant, int depth)
    {
        _unknowns = unknowns;
        Constant = constant;
        Depth = depth;
    }

    public long Constant { get; }

    public int Depth { get; }

    public bool IsConstant => _unknowns.Count == 0;

    public static SymbolicValue Known(long constant, int depth) => new([], constant, depth);

    public static SymbolicValue Unknown(uint address, byte register, int depth) => new(new() { [(address, register)] = 1 }, 0, depth);

    public SymbolicValue DependingOn(int depth) => new(_unknowns, Constant, Math.Max(Depth, depth));

    public static SymbolicValue operator +(SymbolicValue left, SymbolicValue right) => Combine(left, right, 1);

    public static SymbolicValue operator -(SymbolicValue left, SymbolicValue right) => Combine(left, right, -1);

    private static SymbolicValue Combine(SymbolicValue left, SymbolicValue right, int sign)
    {
        var unknowns = new Dictionary<(uint Address, byte Register), int>(left._unknowns);
        foreach (var (unknown, factor) in right._unknowns)
        {
            int sum = unknowns.GetValueOrDefault(unknown) + (sign * factor);
            if (sum == 0)
            {
                unknowns.Remove(unknown);
            }
            else
            {
                unknowns[unknown] = sum;
            }
        }

        return new SymbolicValue(unknowns, left.Constant + (sign * right.Constant), Math.Max(left.Depth, right.Depth));
    }
}
