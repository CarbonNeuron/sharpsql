namespace SharpSql;

/// <summary>
/// Backend-neutral type identity used by the bound program.
/// </summary>
internal sealed record IrType(
    string Name,
    bool IsBoolean = false,
    bool IsString = false,
    bool IsReference = false,
    IrType? ScalarRepresentation = null)
{
    public static readonly IrType Bool = new("bool", IsBoolean: true);
    public static readonly IrType String = new("string", IsString: true, IsReference: true);
    public static readonly IrType Int = new("int");
    public static readonly IrType Unknown = new("unknown");
    public static readonly IrType Void = new("void");
}
