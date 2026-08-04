namespace SharpSql;

// Core IR is intentionally small. It is the backend-facing register/control-flow
// layer; source syntax, comments, query shapes, heap metadata, and async state are
// kept in the richer bound model or in specialized plans.
internal readonly record struct CoreBlockId(int Value);

internal readonly record struct CoreValueId(int Value);

internal sealed record CoreParameter(CoreValueId Value, IrType Type);

internal sealed record CoreLocal(CoreValueId Value, IrType Type);

internal abstract record CoreInstruction(CoreValueId Result, IrType Type);

internal sealed record CoreConstantInstruction(
    CoreValueId Result,
    IrType Type,
    object? Value) : CoreInstruction(Result, Type);

internal sealed record CoreMoveInstruction(
    CoreValueId Result,
    IrType Type,
    CoreValueId Operand) : CoreInstruction(Result, Type);

internal sealed record CoreBinaryInstruction(
    CoreValueId Result,
    IrType Type,
    IrBinaryOperator Operator,
    CoreValueId Left,
    CoreValueId Right) : CoreInstruction(Result, Type);

internal sealed record CoreUnaryInstruction(
    CoreValueId Result,
    IrType Type,
    IrUnaryOperator Operator,
    CoreValueId Operand) : CoreInstruction(Result, Type);

internal sealed record CoreConvertInstruction(
    CoreValueId Result,
    IrType Type,
    CoreValueId Operand) : CoreInstruction(Result, Type);

internal sealed record CoreCallInstruction(
    CoreValueId Result,
    IrType Type,
    IrMethodId Target,
    IReadOnlyList<CoreValueId> Arguments) : CoreInstruction(Result, Type);

internal enum CoreHostOperation
{
    WriteLine = 1
}

internal sealed record CoreHostCallInstruction(
    CoreHostOperation Operation,
    IReadOnlyList<CoreValueId> Arguments) : CoreInstruction(default, IrType.Void);

internal abstract record CoreTerminator;

internal sealed record CoreJump(CoreBlockId Target) : CoreTerminator;

internal sealed record CoreBranch(
    CoreValueId Condition,
    CoreBlockId WhenTrue,
    CoreBlockId WhenFalse) : CoreTerminator;

internal sealed record CoreReturn(CoreValueId? Value) : CoreTerminator;

internal sealed record CoreBlock(
    CoreBlockId Id,
    IReadOnlyList<CoreInstruction> Instructions,
    CoreTerminator Terminator);

internal sealed record CoreMethod(
    IrMethodId Id,
    IrType ReturnType,
    IReadOnlyList<CoreParameter> Parameters,
    IReadOnlyList<CoreLocal> Locals,
    CoreBlockId EntryBlock,
    IReadOnlyList<CoreBlock> Blocks);
