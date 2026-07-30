using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private SqlScalarExpression EmitHeapMemberScalar(
        MemberAccessExpressionSyntax member,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null,
        string? receiverOverride = null)
    {
        var receiverType = InferType(member.Expression, scope, substitutions);
        var receiver = receiverOverride ?? EmitScalar(member.Expression, scope, substitutions);
        if (IsGroupingType(receiverType.Name) && member.Name.Identifier.ValueText == "Key")
            return SqlScalarExpression.Primary(receiver);
        if (receiverType.IsString && member.Name.Identifier.ValueText == "Length")
        {
            return SqlScalarExpression.Primary($"CONVERT(INT, DATALENGTH({receiver}) / 2)");
        }
        if (receiverType.Name == "byte[]" && member.Name.Identifier.ValueText == "Length")
            return SqlScalarExpression.Primary(ByteArrayLengthSql(receiver));
        if (IsSequenceType(receiverType.Name))
        {
            if ((IsListType(receiverType.Name) && member.Name.Identifier.ValueText == "Count") ||
                (IsArrayType(receiverType.Name) && member.Name.Identifier.ValueText == "Length"))
                return SqlScalarExpression.Primary($"(SELECT __count FROM {HeapObjects} WHERE {HeapObjectExecutionFilter()}__id = {receiver})");
        }
        else if (IsDictionaryType(receiverType.Name))
        {
            if (member.Name.Identifier.ValueText == "Count")
                return SqlScalarExpression.Primary($"(SELECT __count FROM {HeapObjects} WHERE {HeapObjectExecutionFilter()}__id = {receiver})");
        }
        else if (TryResolveHeapField(member, scope, substitutions, out var type, out var field))
        {
            return SqlScalarExpression.Primary(HeapFieldReadValue(type, field, receiver));
        }
        return SqlScalarExpression.Primary(UnsupportedExpression(member, $"Unknown heap member '{member.Name.Identifier.ValueText}'."));
    }

    private SqlScalarExpression EmitHeapElementScalar(ElementAccessExpressionSyntax element, VariableScope scope)
    {
        var receiverType = InferType(element.Expression, scope);
        var receiver = EmitScalar(element.Expression, scope);
        var argument = element.ArgumentList.Arguments.Single().Expression;
        var key = EmitScalar(argument, scope);
        if (TryGetHeapElementSql(receiverType, receiver, key, out var value))
            return SqlScalarExpression.Primary(value);
        return SqlScalarExpression.Primary(UnsupportedExpression(element, "Only string, list, array, and dictionary indexing is supported."));
    }

    private bool TryEmitHeapElementExpression(
        ElementAccessExpressionSyntax element,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (element.ArgumentList.Arguments.Count != 1)
            return false;

        var receiverType = InferType(element.Expression, scope);
        if (!receiverType.IsString && receiverType.Name != "byte[]" &&
            !IsSequenceType(receiverType.Name) && !IsDictionaryType(receiverType.Name))
            return false;

        EmitVmExpression(element.Expression, scope, context, receiver =>
            EmitVmExpression(element.ArgumentList.Arguments[0].Expression, scope, context, key =>
            {
                if (receiverType.IsString)
                    _sql.Line($"IF {key} < 0 OR {key} >= CONVERT(INT, DATALENGTH({receiver}) / 2) THROW 51003, 'String index was out of range.', 1;");
                else if (receiverType.Name == "byte[]")
                    _sql.Line($"IF {key} < 0 OR {key} >= {ByteArrayLengthSql(receiver)} THROW 51003, 'Array index was out of range.', 1;");
                else if (IsSequenceType(receiverType.Name))
                    EmitSequenceIndexGuard(receiverType, receiver, key);
                else if (IsDictionaryType(receiverType.Name))
                {
                    var keyType = GenericArguments(receiverType.Name)[0];
                    _sql.Line($"IF NOT EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {DictionaryEntryExecutionFilter()}__dictionary_id = {receiver} AND {DictionaryKeyPredicate(keyType, key)}) THROW 51010, 'The given key was not present in the dictionary.', 1;");
                }

                if (TryGetHeapElementSql(receiverType, receiver, key, out var value))
                    continuation(value);
                else
                    continuation(UnsupportedExpression(element));
            }));
        return true;
    }

    private bool TryEmitHeapElementExpression(
        IrElementExpression element,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (element.Arguments.Count != 1)
            return false;

        var receiverType = element.Receiver.Type;
        if (!receiverType.IsString && receiverType.Name != "byte[]" &&
            !IsSequenceType(receiverType.Name) && !IsDictionaryType(receiverType.Name))
            return false;

        EmitVmExpression(element.Receiver, scope, context, receiver =>
            EmitVmExpression(element.Arguments[0], scope, context, key =>
            {
                if (receiverType.IsString)
                    _sql.Line($"IF {key} < 0 OR {key} >= CONVERT(INT, DATALENGTH({receiver}) / 2) THROW 51003, 'String index was out of range.', 1;");
                else if (receiverType.Name == "byte[]")
                    _sql.Line($"IF {key} < 0 OR {key} >= {ByteArrayLengthSql(receiver)} THROW 51003, 'Array index was out of range.', 1;");
                else if (IsSequenceType(receiverType.Name))
                    EmitSequenceIndexGuard(receiverType, receiver, key);
                else
                {
                    var keyType = GenericArguments(receiverType.Name)[0];
                    _sql.Line($"IF NOT EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {DictionaryEntryExecutionFilter()}__dictionary_id = {receiver} AND {DictionaryKeyPredicate(keyType, key)}) THROW 51010, 'The given key was not present in the dictionary.', 1;");
                }

                if (TryGetHeapElementSql(receiverType, receiver, key, out var value))
                    continuation(value);
                else
                    continuation(UnsupportedExpression(element.Source));
            }));
        return true;
    }

    private void EmitSequenceIndexGuard(IrType receiverType, string receiver, string index)
    {
        var (code, message) = IsListType(receiverType.Name)
            ? (51002, "List index was out of range.")
            : (51003, "Array index was out of range.");
        _sql.Line($"IF {index} < 0 OR {index} >= {SequenceCountSql(receiver)} THROW {code}, '{message}', 1;");
    }

    private bool TryGetHeapElementSql(
        IrType receiverType,
        string receiver,
        string key,
        out string value)
    {
        if (receiverType.IsString)
        {
            value = $"SUBSTRING({receiver}, {key} + 1, 1)";
            return true;
        }
        if (receiverType.Name == "byte[]")
        {
            value = $"CONVERT(TINYINT, SUBSTRING({ByteArrayPayloadSql(receiver)}, {key} + 1, 1))";
            return true;
        }
        if (IsSequenceType(receiverType.Name))
        {
            var itemType = SequenceElementType(receiverType.Name);
            value = SequenceElementSql(receiver, key, itemType);
            return true;
        }
        if (IsDictionaryType(receiverType.Name))
        {
            var types = GenericArguments(receiverType.Name);
            value = $"(SELECT {DictionaryValueRead(types[1])} FROM {HeapDictionaryEntries} WHERE {DictionaryEntryExecutionFilter()}__dictionary_id = {receiver} AND {DictionaryKeyPredicate(types[0], key)})";
            return true;
        }
        value = string.Empty;
        return false;
    }

    private bool TryEmitHeapInvocationScalar(
        InvocationExpressionSyntax invocation,
        VariableScope scope,
        out SqlScalarExpression expression)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax member)
        {
            var receiverType = InferType(member.Expression, scope);
            if (receiverType.Name == "byte[]" && member.Name.Identifier.ValueText == "SequenceEqual" &&
                invocation.ArgumentList.Arguments.Count == 1)
            {
                var receiver = EmitScalar(member.Expression, scope);
                var other = EmitScalar(invocation.ArgumentList.Arguments[0].Expression, scope);
                expression = SqlScalarExpression.Primary(
                    $"CASE WHEN {ByteArrayPayloadSql(receiver)} = {ByteArrayPayloadSql(other)} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsDictionaryType(receiverType.Name) && member.Name.Identifier.ValueText == "ContainsKey" &&
                invocation.ArgumentList.Arguments.Count == 1)
            {
                var types = GenericArguments(receiverType.Name);
                var dictionary = EmitScalar(member.Expression, scope);
                var key = EmitScalar(invocation.ArgumentList.Arguments[0].Expression, scope);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {DictionaryEntryExecutionFilter()}__dictionary_id = {dictionary} AND {DictionaryKeyPredicate(types[0], key)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsListType(receiverType.Name) && member.Name.Identifier.ValueText == "Contains" &&
                invocation.ArgumentList.Arguments.Count == 1)
            {
                var itemType = SequenceElementType(receiverType.Name);
                var list = EmitScalar(member.Expression, scope);
                var value = EmitScalar(invocation.ArgumentList.Arguments[0].Expression, scope);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapIndexedItems} WHERE {IndexedItemExecutionFilter()}__owner_id = {list} AND {IndexedItemValuePredicate(itemType, value)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsDictionaryType(receiverType.Name) && member.Name.Identifier.ValueText == "ContainsValue" &&
                invocation.ArgumentList.Arguments.Count == 1)
            {
                var valueType = GenericArguments(receiverType.Name)[1];
                var dictionary = EmitScalar(member.Expression, scope);
                var value = EmitScalar(invocation.ArgumentList.Arguments[0].Expression, scope);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {DictionaryEntryExecutionFilter()}__dictionary_id = {dictionary} AND {DictionaryValuePredicate(valueType, value)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
        }
        expression = null!;
        return false;
    }

    private bool TryEmitHeapInvocationScalar(
        IrInvocationExpression invocation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlScalarExpression expression)
    {
        if (invocation.Target is IrMemberExpression member)
        {
            var receiverType = member.Receiver.Type;
            if (receiverType.Name == "byte[]" && member.MemberName == "SequenceEqual" &&
                invocation.Arguments.Count == 1)
            {
                var receiver = EmitScalar(member.Receiver, scope, substitutions);
                var other = EmitScalar(invocation.Arguments[0], scope, substitutions);
                expression = SqlScalarExpression.Primary(
                    $"CASE WHEN {ByteArrayPayloadSql(receiver)} = {ByteArrayPayloadSql(other)} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsDictionaryType(receiverType.Name) && member.MemberName == "ContainsKey" &&
                invocation.Arguments.Count == 1)
            {
                var receiver = EmitScalar(member.Receiver, scope, substitutions);
                var keyType = GenericArguments(receiverType.Name)[0];
                var key = EmitScalar(invocation.Arguments[0], scope, substitutions);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {DictionaryEntryExecutionFilter()}__dictionary_id = {receiver} AND {DictionaryKeyPredicate(keyType, key)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsListType(receiverType.Name) && member.MemberName == "Contains" &&
                invocation.Arguments.Count == 1)
            {
                var receiver = EmitScalar(member.Receiver, scope, substitutions);
                var itemType = SequenceElementType(receiverType.Name);
                var value = EmitScalar(invocation.Arguments[0], scope, substitutions);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapIndexedItems} WHERE {IndexedItemExecutionFilter()}__owner_id = {receiver} AND {IndexedItemValuePredicate(itemType, value)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsDictionaryType(receiverType.Name) && member.MemberName == "ContainsValue" &&
                invocation.Arguments.Count == 1)
            {
                var receiver = EmitScalar(member.Receiver, scope, substitutions);
                var valueType = GenericArguments(receiverType.Name)[1];
                var value = EmitScalar(invocation.Arguments[0], scope, substitutions);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {DictionaryEntryExecutionFilter()}__dictionary_id = {receiver} AND {DictionaryValuePredicate(valueType, value)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
        }
        expression = null!;
        return false;
    }

    private IrType InferHeapMemberType(MemberAccessExpressionSyntax member, VariableScope scope)
    {
        var receiver = InferType(member.Expression, scope);
        if ((IsListType(receiver.Name) || IsDictionaryType(receiver.Name)) && member.Name.Identifier.ValueText == "Count")
            return IrType.Int;
        if ((IsArrayType(receiver.Name) || receiver.Name == "byte[]") && member.Name.Identifier.ValueText == "Length")
            return IrType.Int;
        if (TryResolveHeapField(
                receiver,
                member.Name.Identifier.ValueText,
                MemberIdentity(SemanticModelFor(member)?.GetSymbolInfo(member).Symbol),
                out _,
                out var field))
            return field.Type;
        return IrType.Unknown;
    }

    private IrType InferHeapElementType(ElementAccessExpressionSyntax element, VariableScope scope)
    {
        var receiver = InferType(element.Expression, scope);
        if (receiver.Name == "byte[]")
            return new IrType("byte");
        if (IsSequenceType(receiver.Name))
            return SequenceElementType(receiver.Name);
        if (IsDictionaryType(receiver.Name))
            return GenericArguments(receiver.Name)[1];
        return IrType.Unknown;
    }

    private bool TryResolveHeapField(
        MemberAccessExpressionSyntax member,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out HeapType type,
        out HeapField field)
    {
        var receiverType = InferType(member.Expression, scope, substitutions);
        return TryResolveHeapField(
            receiverType,
            member.Name.Identifier.ValueText,
            MemberIdentity(SemanticModelFor(member)?.GetSymbolInfo(member).Symbol),
            out type,
            out field);
    }

    private bool TryResolveHeapField(
        MemberAccessExpressionSyntax member,
        VariableScope scope,
        out HeapType type,
        out HeapField field) =>
        TryResolveHeapField(member, scope, substitutions: null, out type, out field);

    private bool TryEmitImplicitHeapField(
        IdentifierNameSyntax identifier,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlScalarExpression expression)
    {
        if (TryResolveImplicitHeapField(
                identifier.Identifier.ValueText,
                scope,
                substitutions,
                out var type,
                out var field,
                out var receiver))
        {
            expression = SqlScalarExpression.Primary(HeapFieldReadValue(type, field, receiver));
            return true;
        }
        expression = null!;
        return false;
    }

    private bool TryResolveImplicitHeapField(
        string name,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out HeapType type,
        out HeapField field,
        out string receiver) =>
        TryResolveImplicitHeapField(
            name,
            IrMemberId.None,
            scope,
            substitutions,
            out type,
            out field,
            out receiver);

    private bool TryResolveImplicitHeapField(
        string name,
        IrMemberId memberId,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out HeapType type,
        out HeapField field,
        out string receiver)
    {
        IrType? receiverType = null;
        receiver = string.Empty;
        if (substitutions is not null && substitutions.TryGetValue("this", out var replacement))
        {
            receiverType = replacement.Type;
            receiver = replacement.Expression.Sql;
        }
        else if (scope.Find("this") is ScalarVariableBinding binding)
        {
            receiverType = binding.Type;
            receiver = binding.SqlName;
        }

        if (receiverType is not null && TryResolveHeapField(receiverType, name, memberId, out type, out field))
            return true;
        type = null!;
        field = null!;
        return false;
    }

    private static IEnumerable<(string Name, ExpressionSyntax Value)> InitializerAssignments(InitializerExpressionSyntax? initializer)
    {
        if (initializer is null)
            yield break;
        foreach (var expression in initializer.Expressions.OfType<AssignmentExpressionSyntax>())
        {
            var name = AssignmentMemberName(expression.Left);
            if (name is not null)
                yield return (name, expression.Right);
        }
    }

    private static string? AssignmentMemberName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };

    private static string CollectionValueColumn(IrType type, bool key)
    {
        var prefix = key ? "__key" : "__";
        if (type.IsString)
            return prefix + (key ? "_text" : "text_value");
        if (type.IsReference)
            return prefix + (key ? "_reference" : "reference_value");
        return key ? "__key" : "__value";
    }

    private static string CollectionStoredValue(IrType type, string value) =>
        type.IsString || type.IsReference
            ? value
            : type.Name == "char"
                ? $"CONVERT(SQL_VARIANT, CONVERT(NCHAR(1), {value}))"
                : $"CONVERT(SQL_VARIANT, {value})";

    private static string CollectionReadValue(IrType type, bool key, string? qualifier = null)
    {
        var column = (qualifier is null ? string.Empty : qualifier + ".") + CollectionValueColumn(type, key);
        if (type.IsString || type.IsReference)
            return column;
        return $"CONVERT({type.SqlType()}, {column})";
    }

    private string HeapFieldValueColumn(HeapField field)
    {
        if (!UsesMemoryOptimizedRuntime)
            return field.SqlName;
        if (field.Type.IsString)
            return "__text_value";
        if (field.Type.IsReference)
            return "__reference_value";
        return "__scalar_value";
    }

    private string HeapFieldStoredValue(HeapField field, string value)
    {
        if (!UsesMemoryOptimizedRuntime || field.Type.IsString || field.Type.IsReference)
            return value;
        var typedValue = field.Type.Name == "char"
            ? $"CONVERT(NCHAR(1), {value})"
            : value;
        return $"CONVERT(VARBINARY(8000), {typedValue})";
    }

    private string HeapFieldReadValue(HeapType declaringType, HeapField field, string objectSql)
    {
        if (!UsesMemoryOptimizedRuntime)
            return $"(SELECT {field.SqlName} FROM {declaringType.TableName} WHERE {HeapExecutionFilter()}__object_id = {objectSql})";

        var column = HeapFieldValueColumn(field);
        var value = field.Type.IsString || field.Type.IsReference
            ? column
            : $"CONVERT({field.Type.SqlType()}, {column})";
        return $"(SELECT {value} FROM {MemoryOptimizedHeapFields} WHERE __execution_id = {RuntimeExecutionId} AND __object_id = {objectSql} AND __declaring_type_id = {declaringType.Id} AND __field_id = {field.RuntimeFieldId})";
    }

    private void EmitHeapFieldUpdate(HeapType declaringType, HeapField field, string objectSql, string value)
    {
        if (!UsesMemoryOptimizedRuntime)
        {
            _sql.Line($"UPDATE {declaringType.TableName} SET {field.SqlName} = {value} WHERE {HeapExecutionFilter()}__object_id = {objectSql};");
            return;
        }

        _sql.Line($"UPDATE {MemoryOptimizedHeapFields} SET {HeapFieldValueColumn(field)} = {HeapFieldStoredValue(field, value)} WHERE __execution_id = {RuntimeExecutionId} AND __object_id = {objectSql} AND __declaring_type_id = {declaringType.Id} AND __field_id = {field.RuntimeFieldId};");
    }

    private void EmitHeapTypePayload(HeapType declaringType, string objectSql, Func<HeapField, string> valueFor)
    {
        if (!UsesMemoryOptimizedRuntime)
        {
            var columns = new[] { "__object_id" }
                .Concat(declaringType.Fields.Values.Select(field => field.SqlName));
            var values = new[] { objectSql }
                .Concat(declaringType.Fields.Values.Select(valueFor));
            _sql.Line($"INSERT INTO {declaringType.TableName} ({HeapInsertColumns(string.Join(", ", columns))}) VALUES ({HeapInsertValues(string.Join(", ", values))});");
            return;
        }

        foreach (var field in declaringType.Fields.Values)
        {
            var valueColumn = HeapFieldValueColumn(field);
            var value = HeapFieldStoredValue(field, valueFor(field));
            _sql.Line($"INSERT INTO {MemoryOptimizedHeapFields} (__execution_id, __object_id, __declaring_type_id, __field_id, {valueColumn}) VALUES ({RuntimeExecutionId}, {objectSql}, {declaringType.Id}, {field.RuntimeFieldId}, {value});");
        }
    }

    private string IndexedItemValueColumn(IrType type) =>
        UsesMemoryOptimizedRuntime && !type.IsString && !type.IsReference
            ? "__scalar_value"
            : CollectionValueColumn(type, key: false);

    private string IndexedItemStoredValue(IrType type, string value)
    {
        if (!UsesMemoryOptimizedRuntime || type.IsString || type.IsReference)
            return CollectionStoredValue(type, value);
        var typedValue = type.Name == "char"
            ? $"CONVERT(NCHAR(1), {value})"
            : value;
        return $"CONVERT(VARBINARY(8000), {typedValue})";
    }

    private string IndexedItemReadValue(IrType type, string? qualifier = null)
    {
        if (!UsesMemoryOptimizedRuntime || type.IsString || type.IsReference)
            return CollectionReadValue(type, key: false, qualifier);
        var column = (qualifier is null ? string.Empty : qualifier + ".") + "__scalar_value";
        return $"CONVERT({type.SqlType()}, {column})";
    }

    private string IndexedItemValuePredicate(IrType type, string value)
    {
        if (!UsesMemoryOptimizedRuntime)
            return CollectionValuePredicate(type, value, key: false);
        var column = IndexedItemValueColumn(type);
        if (type.IsString)
            return $"{column} COLLATE Latin1_General_100_BIN2 = {value} COLLATE Latin1_General_100_BIN2";
        return $"{column} = {IndexedItemStoredValue(type, value)}";
    }

    private string DictionaryKeyColumn(IrType type) =>
        UsesMemoryOptimizedRuntime && !type.IsString && !type.IsReference
            ? "__key_scalar"
            : CollectionValueColumn(type, key: true);

    private string DictionaryKeyStoredValue(IrType type, string value)
    {
        if (!UsesMemoryOptimizedRuntime || type.IsString || type.IsReference)
            return CollectionStoredValue(type, value);
        var typedValue = type.Name == "char"
            ? $"CONVERT(NCHAR(1), {value})"
            : value;
        return $"CONVERT(VARBINARY(8000), {typedValue})";
    }

    private string DictionaryValueColumn(IrType type) =>
        UsesMemoryOptimizedRuntime && !type.IsString && !type.IsReference
            ? "__value_scalar"
            : CollectionValueColumn(type, key: false);

    private string DictionaryValueStored(IrType type, string value)
    {
        if (!UsesMemoryOptimizedRuntime || type.IsString || type.IsReference)
            return CollectionStoredValue(type, value);
        var typedValue = type.Name == "char"
            ? $"CONVERT(NCHAR(1), {value})"
            : value;
        return $"CONVERT(VARBINARY(8000), {typedValue})";
    }

    private string DictionaryValueRead(IrType type, string? qualifier = null)
    {
        if (!UsesMemoryOptimizedRuntime || type.IsString || type.IsReference)
            return CollectionReadValue(type, key: false, qualifier);
        var column = (qualifier is null ? string.Empty : qualifier + ".") + "__value_scalar";
        return $"CONVERT({type.SqlType()}, {column})";
    }

    private string DictionaryKeyPredicate(IrType type, string value)
    {
        if (type.IsString)
            return $"__key_hash = {DictionaryKeyHash(type, value)} AND __key_text COLLATE Latin1_General_100_BIN2 = {value} COLLATE Latin1_General_100_BIN2";
        return $"{DictionaryKeyColumn(type)} = {DictionaryKeyStoredValue(type, value)}";
    }

    private static string? DictionaryKeyHash(IrType type, string value)
    {
        if (type.IsString)
            return $"HASHBYTES('SHA2_256', CONVERT(VARBINARY(MAX), {value} COLLATE Latin1_General_100_BIN2))";
        return null;
    }

    private static string CollectionValuePredicate(IrType type, string value, bool key)
    {
        var column = CollectionValueColumn(type, key);
        if (type.IsString)
            return $"{column} COLLATE Latin1_General_100_BIN2 = {value} COLLATE Latin1_General_100_BIN2";
        return $"{column} = {CollectionStoredValue(type, value)}";
    }

    private string DictionaryValuePredicate(IrType type, string value)
    {
        if (!UsesMemoryOptimizedRuntime)
            return CollectionValuePredicate(type, value, key: false);
        var column = DictionaryValueColumn(type);
        if (type.IsString)
            return $"{column} COLLATE Latin1_General_100_BIN2 = {value} COLLATE Latin1_General_100_BIN2";
        return $"{column} = {DictionaryValueStored(type, value)}";
    }

    private string FormatTextValue(IrType type, string value)
    {
        if (type.IsBoolean)
            return $"CASE {value} WHEN CAST(1 AS BIT) THEN N'True' " +
                   "WHEN CAST(0 AS BIT) THEN N'False' ELSE N'' END";
        if (type.Name == "byte[]")
            return $"CASE WHEN {value} IS NULL THEN N'' ELSE N'System.Byte[]' END";
        return _heapTypes.TryGetValue(type.Name, out var heapType) &&
               heapType.IsRecord
            ? FormatRecordText(heapType, value)
            : value;
    }

    private string FormatRecordText(HeapType type, string reference)
    {
        var parts = new List<string> { $"N'{EscapeSqlString(type.Name)} {{ '" };
        var fields = HeapHierarchyBaseFirst(type)
            .SelectMany(declaringType => declaringType.Fields.Values.Select(field => (DeclaringType: declaringType, Field: field)))
            .ToArray();
        for (var index = 0; index < fields.Length; index++)
        {
            var (declaringType, field) = fields[index];
            if (index > 0)
                parts.Add("N', '");
            parts.Add($"N'{EscapeSqlString(field.Name)} = '");
            var fieldValue = HeapFieldReadValue(declaringType, field, reference);
            parts.Add(FormatTextValue(field.Type, fieldValue));
        }
        parts.Add("N' }'");
        return $"CASE WHEN {reference} IS NULL THEN N'' ELSE CONCAT({string.Join(", ", parts)}) END";
    }

    private static string DefaultSql(IrType type)
    {
        if (type.IsString || type.IsReference || type == IrType.Unknown)
            return "NULL";
        if (type.IsBoolean)
            return "CAST(0 AS BIT)";
        return type.Name switch
        {
            "char" => "NCHAR(0)",
            "DateTime" => "CONVERT(DATETIME2, '0001-01-01T00:00:00')",
            "DateOnly" => "CONVERT(DATE, '0001-01-01')",
            "TimeOnly" => "CONVERT(TIME, '00:00:00')",
            "Guid" => "CONVERT(UNIQUEIDENTIFIER, '00000000-0000-0000-0000-000000000000')",
            _ => "0"
        };
    }

    private static bool IsListType(string name) =>
        KnownTypeFacts.IsList(name);

    private static bool IsArrayType(string name) => name.EndsWith("[]", StringComparison.Ordinal) && name != "byte[]";

    private static bool IsSequenceType(string name) => IsListType(name) || IsArrayType(name);

    private static IrType SequenceElementType(string name) => IsArrayType(name)
        ? KnownTypeFacts.TypeFromName(name[..^2])
        : GenericArguments(name)[0];

    private string SequenceCountSql(string collection) =>
        $"(SELECT __count FROM {HeapObjects} WHERE {HeapObjectExecutionFilter()}__id = {collection})";

    private string ByteArrayLengthSql(string array) => SequenceCountSql(array);

    private string ByteArrayPayloadSql(string array) =>
        $"(SELECT __binary_value FROM {HeapIndexedItems} WHERE {IndexedItemExecutionFilter()}__owner_id = {array} AND __index = 0)";

    private string SequenceElementSql(string collection, string index, IrType itemType) =>
        $"(SELECT {IndexedItemReadValue(itemType)} FROM {HeapIndexedItems} WHERE {IndexedItemExecutionFilter()}__owner_id = {collection} AND __index = {index})";

    private static bool IsDictionaryType(string name) =>
        KnownTypeFacts.IsDictionary(name);

    private static IrType[] GenericArguments(string name)
    {
        var open = name.IndexOf('<');
        var close = name.LastIndexOf('>');
        if (open < 0 || close <= open)
            return [];
        var content = name[(open + 1)..close];
        var arguments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '<') depth++;
            else if (content[index] == '>') depth--;
            else if (content[index] == ',' && depth == 0)
            {
                arguments.Add(content[start..index].Trim());
                start = index + 1;
            }
        }
        arguments.Add(content[start..].Trim());
        return arguments.Select(KnownTypeFacts.TypeFromName).ToArray();
    }

    private static string NormalizeTypeName(string name) =>
        name.Replace("global::", string.Empty, StringComparison.Ordinal)
            .Replace("System.Collections.Generic.", string.Empty, StringComparison.Ordinal);

    private static string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

}
