namespace SharpSql;

internal sealed record IntrinsicDescriptor(
    MethodEffects Effects,
    bool MutatesReceiver = false,
    bool MutatesArguments = false,
    bool ArgumentsEscape = false,
    bool ReturnsFreshReference = false);

internal static class IntrinsicCatalog
{
    private const string ThreadGetCurrentProcessorIdMethodId =
        "M:System.Threading.Thread.GetCurrentProcessorId";

    private static readonly HashSet<string> DeferredLinqOperators = new(StringComparer.Ordinal)
    {
        "AsEnumerable", "AsQueryable", "Where", "Select", "OrderBy", "OrderByDescending",
        "ThenBy", "ThenByDescending", "Distinct", "Skip", "Take", "GroupBy", "Join"
    };

    private static readonly HashSet<string> TerminalLinqOperators = new(StringComparer.Ordinal)
    {
        "Sum", "Count", "LongCount", "Any", "All", "Contains", "First", "FirstOrDefault",
        "Last", "LastOrDefault", "Single", "SingleOrDefault", "ElementAt", "ElementAtOrDefault",
        "Min", "Max", "Average", "MinBy", "MaxBy"
    };

    private static readonly HashSet<string> GuardedLinqOperators = new(StringComparer.Ordinal)
    {
        "First", "Last", "Single", "SingleOrDefault", "ElementAt", "ElementAtOrDefault",
        "Min", "Max", "Average", "MinBy", "MaxBy"
    };

    public static bool IsDeferredLinqOperator(string name) => DeferredLinqOperators.Contains(name);

    public static bool IsTerminalLinqOperator(string name) => TerminalLinqOperators.Contains(name);

    public static bool IsGuardedLinqOperator(string name) => GuardedLinqOperators.Contains(name);

    public static bool IsMaterializer(string name) => name is "ToList" or "ToArray";

    public static bool IsThreadGetCurrentProcessorId(IrInvocationExpression invocation) =>
        invocation.Arguments.Count == 0 &&
        invocation.TargetMethodId.Value == ThreadGetCurrentProcessorIdMethodId;

    public static IntrinsicDescriptor Describe(IrInvocationExpression invocation)
    {
        var name = invocation.MethodName ?? string.Empty;
        var receiverType = invocation.Target is IrMemberExpression member
            ? member.Receiver.Type.Name
            : string.Empty;

        if (name is "Write" or "WriteLine" &&
            invocation.Target is IrMemberExpression { Receiver: IrVariableExpression { Symbol.Name: "Console" } })
            return new IntrinsicDescriptor(MethodEffects.PerformsIo);

        if (IsThreadGetCurrentProcessorId(invocation))
            return new IntrinsicDescriptor(
                MethodEffects.ReadsMutableState | MethodEffects.Nondeterministic);

        if (KnownTypeFacts.IsRandom(receiverType) && name is "Next" or "NextDouble")
            return new IntrinsicDescriptor(
                MethodEffects.ReadsMutableState |
                MethodEffects.WritesMutableState |
                MethodEffects.UsesRandom |
                MethodEffects.MayThrow,
                MutatesReceiver: true);

        if ((KnownTypeFacts.IsList(receiverType) || KnownTypeFacts.IsDictionary(receiverType)) &&
            name is "Add" or "Clear" or "Remove" or "RemoveAt")
            return new IntrinsicDescriptor(
                MethodEffects.WritesMutableState | MethodEffects.MayThrow,
                MutatesReceiver: true,
                ArgumentsEscape: name == "Add");

        if (IsMaterializer(name) || name is "Range" or "Repeat")
            return new IntrinsicDescriptor(
                MethodEffects.ReadsMutableState | MethodEffects.Allocates | MethodEffects.MayThrow,
                ReturnsFreshReference: true);

        if (IsDeferredLinqOperator(name))
            return new IntrinsicDescriptor(
                MethodEffects.ReadsMutableState,
                ArgumentsEscape: true);

        if (IsTerminalLinqOperator(name))
            return new IntrinsicDescriptor(MethodEffects.ReadsMutableState | MethodEffects.MayThrow);

        return new IntrinsicDescriptor(
            MethodEffects.ReadsMutableState |
            MethodEffects.WritesMutableState |
            MethodEffects.InvokesUnknown |
            MethodEffects.MayThrow,
            MutatesReceiver: true,
            MutatesArguments: true,
            ArgumentsEscape: true);
    }
}

internal static class KnownTypeFacts
{
    public static bool IsList(string name) =>
        name.StartsWith("List<", StringComparison.Ordinal) ||
        name.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal);

    public static bool IsDictionary(string name) =>
        name.StartsWith("Dictionary<", StringComparison.Ordinal) ||
        name.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal);

    public static bool IsRandom(string name) => name is "Random" or "System.Random";

    public static bool IsLinqSequence(string name) =>
        name.StartsWith("IEnumerable<", StringComparison.Ordinal) ||
        name.StartsWith("IQueryable<", StringComparison.Ordinal) ||
        name.StartsWith("IOrderedEnumerable<", StringComparison.Ordinal) ||
        name.StartsWith("IOrderedQueryable<", StringComparison.Ordinal);

    public static IrType TypeFromName(string name)
    {
        name = name.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
        return name switch
        {
            "bool" or "System.Boolean" => IrType.Bool,
            "byte" or "System.Byte" => new IrType("byte"),
            "sbyte" or "System.SByte" => new IrType("sbyte"),
            "short" or "System.Int16" => new IrType("short"),
            "ushort" or "System.UInt16" => new IrType("ushort"),
            "int" or "System.Int32" => IrType.Int,
            "uint" or "System.UInt32" => new IrType("uint"),
            "long" or "System.Int64" => new IrType("long"),
            "ulong" or "System.UInt64" => new IrType("ulong"),
            "float" or "System.Single" => new IrType("float"),
            "double" or "System.Double" => new IrType("double"),
            "decimal" or "System.Decimal" => new IrType("decimal"),
            "char" or "System.Char" => new IrType("char"),
            "string" or "System.String" => IrType.String,
            "DateTime" or "System.DateTime" => new IrType("DateTime"),
            "DateOnly" or "System.DateOnly" => new IrType("DateOnly"),
            "TimeOnly" or "System.TimeOnly" or "TimeSpan" or "System.TimeSpan" => new IrType("TimeOnly"),
            "Guid" or "System.Guid" => new IrType("Guid"),
            "byte[]" or "System.Byte[]" => new IrType("byte[]", IsReference: true),
            "void" or "System.Void" => IrType.Void,
            "object" or "System.Object" or "unknown" => IrType.Unknown,
            _ when name.EndsWith("[]", StringComparison.Ordinal) =>
                new IrType(TypeFromName(name[..^2]).Name + "[]", IsReference: true),
            _ => new IrType(name, IsReference: true)
        };
    }
}
