namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private void SaveVmRegisters(VmMethod method)
    {
        if (method.Variables.Count == 0)
            return;

        if (UsesMemoryOptimizedRuntime)
        {
            var variables = method.Variables.Values.ToArray();
            _sql.Line($"DELETE FROM {VmSlotsTable} WHERE __frame_id = {VmFrameId} AND __slot_id IN ({string.Join(", ", variables.Select(variable => variable.Slot))}){VmExecutionPredicate()};");
            var memoryRows = variables.Select(variable =>
            {
                var scalar = variable.Type.IsString || variable.Type.Name == "byte[]"
                    ? "CONVERT(VARBINARY(8000), NULL)"
                    : $"CONVERT(VARBINARY(8000), {variable.SqlName})";
                var text = variable.Type.IsString
                    ? variable.SqlName
                    : "CONVERT(NVARCHAR(MAX), NULL)";
                var binary = variable.Type.Name == "byte[]"
                    ? variable.SqlName
                    : "CONVERT(VARBINARY(MAX), NULL)";
                return $"({RuntimeExecutionId}, {VmFrameId}, {variable.Slot}, {scalar}, {text}, {binary})";
            });
            _sql.Line($"INSERT INTO {VmSlotsTable} (__execution_id, __frame_id, __slot_id, __scalar_value, __text_value, __binary_value) VALUES");
            using (_sql.Indent())
                _sql.Line(string.Join("," + Environment.NewLine, memoryRows) + ";");
            return;
        }

        var rows = method.Variables.Values.Select(variable =>
        {
            var scalar = variable.Type.IsString || variable.Type.Name == "byte[]"
                ? "CONVERT(SQL_VARIANT, NULL)"
                : $"CONVERT(SQL_VARIANT, {variable.SqlName})";
            var text = variable.Type.IsString
                ? variable.SqlName
                : "CONVERT(NVARCHAR(MAX), NULL)";
            var binary = variable.Type.Name == "byte[]"
                ? variable.SqlName
                : "CONVERT(VARBINARY(MAX), NULL)";
            return $"({variable.Slot}, {scalar}, {text}, {binary})";
        });
        _sql.Line($"MERGE {VmSlotsTable} AS target");
        _sql.Line($"USING (VALUES {string.Join(", ", rows)}) AS source (__slot_id, __value, __text_value, __binary_value)");
        _sql.Line($"ON target.__frame_id = {VmFrameId} AND target.__slot_id = source.__slot_id{VmExecutionPredicate("target")}");
        _sql.Line("WHEN MATCHED THEN UPDATE SET __value = source.__value, __text_value = source.__text_value, __binary_value = source.__binary_value");
        var executionColumns = UsesSharedVmStorage ? "__execution_id, " : string.Empty;
        var executionValues = UsesSharedVmStorage ? $"{RuntimeExecutionId}, " : string.Empty;
        _sql.Line($"WHEN NOT MATCHED THEN INSERT ({executionColumns}__frame_id, __slot_id, __value, __text_value, __binary_value) VALUES ({executionValues}{VmFrameId}, source.__slot_id, source.__value, source.__text_value, source.__binary_value);");
    }

    private void LoadVmRegisters(VmMethod method)
    {
        if (method.Variables.Count == 0)
            return;

        var variables = method.Variables.Values.ToArray();
        var assignments = variables.Select((variable, index) =>
        {
            var alias = $"__vm_load_{index}";
            var value = variable.Type.IsString
                ? $"{alias}.__text_value"
                : variable.Type.Name == "byte[]"
                    ? $"{alias}.__binary_value"
                    : UsesMemoryOptimizedRuntime
                        ? $"CONVERT({variable.Type.SqlType()}, {alias}.__scalar_value)"
                        : $"CONVERT({variable.Type.SqlType()}, {alias}.__value)";
            return $"{variable.SqlName} = {value}";
        });
        _sql.Line($"SELECT {string.Join(", ", assignments)}");
        _sql.Line("FROM (VALUES (0)) AS __vm_seed (__value)");
        for (var index = 0; index < variables.Length; index++)
        {
            var variable = variables[index];
            _sql.Line($"LEFT JOIN {VmSlotsTable} AS __vm_load_{index} ON __vm_load_{index}.__frame_id = {VmFrameId} AND __vm_load_{index}.__slot_id = {variable.Slot}{VmExecutionPredicate($"__vm_load_{index}")}");
        }
        _sql.Line(";");
    }

    private void StoreVmSlot(string frameId, int slot, IrType type, string value)
    {
        var (column, storedValue) = type.IsString
            ? ("__text_value", value)
            : type.Name == "byte[]"
                ? ("__binary_value", value)
                : UsesMemoryOptimizedRuntime
                    ? ("__scalar_value", $"CONVERT(VARBINARY(8000), {value})")
                    : ("__value", $"CONVERT(SQL_VARIANT, {value})");
        _sql.Line($"UPDATE {VmSlotsTable} SET {column} = {storedValue} WHERE __frame_id = {frameId} AND __slot_id = {slot}{VmExecutionPredicate()};");
        _sql.Line("IF @@ROWCOUNT = 0");
        InsertVmSlot(frameId, slot, type, value);
    }

    private void InsertVmSlot(string frameId, int slot, IrType type, string value)
    {
        if (type.IsString)
            _sql.Line($"INSERT INTO {VmSlotsTable} ({(UsesSharedVmStorage ? "__execution_id, " : string.Empty)}__frame_id, __slot_id, __text_value) VALUES ({(UsesSharedVmStorage ? RuntimeExecutionId + ", " : string.Empty)}{frameId}, {slot}, {value});");
        else if (type.Name == "byte[]")
            _sql.Line($"INSERT INTO {VmSlotsTable} ({(UsesSharedVmStorage ? "__execution_id, " : string.Empty)}__frame_id, __slot_id, __binary_value) VALUES ({(UsesSharedVmStorage ? RuntimeExecutionId + ", " : string.Empty)}{frameId}, {slot}, {value});");
        else if (UsesMemoryOptimizedRuntime)
            _sql.Line($"INSERT INTO {VmSlotsTable} (__execution_id, __frame_id, __slot_id, __scalar_value) VALUES ({RuntimeExecutionId}, {frameId}, {slot}, CONVERT(VARBINARY(8000), {value}));");
        else
            _sql.Line($"INSERT INTO {VmSlotsTable} ({(UsesSharedVmStorage ? "__execution_id, " : string.Empty)}__frame_id, __slot_id, __value) VALUES ({(UsesSharedVmStorage ? RuntimeExecutionId + ", " : string.Empty)}{frameId}, {slot}, CONVERT(SQL_VARIANT, {value}));");
    }

    private string ReadVmSlot(string frameId, int slot, IrType type)
    {
        if (type.IsString)
            return $"(SELECT __text_value FROM {VmSlotsTable} WHERE __frame_id = {frameId} AND __slot_id = {slot}{VmExecutionPredicate()})";
        if (type.Name == "byte[]")
            return $"(SELECT __binary_value FROM {VmSlotsTable} WHERE __frame_id = {frameId} AND __slot_id = {slot}{VmExecutionPredicate()})";
        if (UsesMemoryOptimizedRuntime)
            return $"CONVERT({type.SqlType()}, (SELECT __scalar_value FROM {VmSlotsTable} WHERE __frame_id = {frameId} AND __slot_id = {slot}{VmExecutionPredicate()}))";
        return $"CONVERT({type.SqlType()}, (SELECT __value FROM {VmSlotsTable} WHERE __frame_id = {frameId} AND __slot_id = {slot}{VmExecutionPredicate()}))";
    }

    private sealed class VmMethod(
        MethodDefinition definition,
        int id,
        string entryLabel,
        string? returnSqlName)
    {
        public MethodDefinition Definition { get; } = definition;
        public int Id { get; } = id;
        public string EntryLabel { get; } = entryLabel;
        public string? ReturnSqlName { get; } = returnSqlName;
        public Dictionary<string, VmVariable> Variables { get; } = new(StringComparer.Ordinal);
        public VariableScope Scope { get; } = new();
        public int NextTemporarySlot { get; set; }
    }

    private sealed record VmVariable(string Name, IrType Type, int Slot, string SqlName);
    private sealed record VmContinuation(int Id, string Label);
    private sealed record VmTemporary(IrType Type, int? Slot, string? SqlName, VmMethod? Context);
}
