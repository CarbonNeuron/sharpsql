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
        if (IsSequenceType(receiverType.Name))
        {
            if ((IsListType(receiverType.Name) && member.Name.Identifier.ValueText == "Count") ||
                (IsArrayType(receiverType.Name) && member.Name.Identifier.ValueText == "Length"))
                return SqlScalarExpression.Primary($"(SELECT __count FROM {HeapObjects} WHERE {HeapExecutionFilter()}__id = {receiver})");
        }
        else if (IsDictionaryType(receiverType.Name))
        {
            if (member.Name.Identifier.ValueText == "Count")
                return SqlScalarExpression.Primary($"(SELECT __count FROM {HeapObjects} WHERE {HeapExecutionFilter()}__id = {receiver})");
        }
        else if (TryResolveHeapField(member, scope, substitutions, out var type, out var field))
        {
            return SqlScalarExpression.Primary($"(SELECT {field.SqlName} FROM {type.TableName} WHERE {HeapExecutionFilter()}__object_id = {receiver})");
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
        if (!receiverType.IsString && !IsSequenceType(receiverType.Name) && !IsDictionaryType(receiverType.Name))
            return false;

        EmitVmExpression(element.Expression, scope, context, receiver =>
            EmitVmExpression(element.ArgumentList.Arguments[0].Expression, scope, context, key =>
            {
                if (receiverType.IsString)
                    _sql.Line($"IF {key} < 0 OR {key} >= CONVERT(INT, DATALENGTH({receiver}) / 2) THROW 51003, 'String index was out of range.', 1;");
                else if (IsSequenceType(receiverType.Name))
                    EmitSequenceIndexGuard(receiverType, receiver, key);
                else if (IsDictionaryType(receiverType.Name))
                {
                    var keyType = GenericArguments(receiverType.Name)[0];
                    _sql.Line($"IF NOT EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {receiver} AND {DictionaryKeyPredicate(keyType, key)}) THROW 51010, 'The given key was not present in the dictionary.', 1;");
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
        if (!receiverType.IsString && !IsSequenceType(receiverType.Name) && !IsDictionaryType(receiverType.Name))
            return false;

        EmitVmExpression(element.Receiver, scope, context, receiver =>
            EmitVmExpression(element.Arguments[0], scope, context, key =>
            {
                if (receiverType.IsString)
                    _sql.Line($"IF {key} < 0 OR {key} >= CONVERT(INT, DATALENGTH({receiver}) / 2) THROW 51003, 'String index was out of range.', 1;");
                else if (IsSequenceType(receiverType.Name))
                    EmitSequenceIndexGuard(receiverType, receiver, key);
                else
                {
                    var keyType = GenericArguments(receiverType.Name)[0];
                    _sql.Line($"IF NOT EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {receiver} AND {DictionaryKeyPredicate(keyType, key)}) THROW 51010, 'The given key was not present in the dictionary.', 1;");
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
        if (IsSequenceType(receiverType.Name))
        {
            var itemType = SequenceElementType(receiverType.Name);
            value = SequenceElementSql(receiver, key, itemType);
            return true;
        }
        if (IsDictionaryType(receiverType.Name))
        {
            var types = GenericArguments(receiverType.Name);
            value = $"(SELECT {CollectionReadValue(types[1], false)} FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {receiver} AND {DictionaryKeyPredicate(types[0], key)})";
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
            if (IsDictionaryType(receiverType.Name) && member.Name.Identifier.ValueText == "ContainsKey" &&
                invocation.ArgumentList.Arguments.Count == 1)
            {
                var types = GenericArguments(receiverType.Name);
                var dictionary = EmitScalar(member.Expression, scope);
                var key = EmitScalar(invocation.ArgumentList.Arguments[0].Expression, scope);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {dictionary} AND {DictionaryKeyPredicate(types[0], key)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsListType(receiverType.Name) && member.Name.Identifier.ValueText == "Contains" &&
                invocation.ArgumentList.Arguments.Count == 1)
            {
                var itemType = SequenceElementType(receiverType.Name);
                var list = EmitScalar(member.Expression, scope);
                var value = EmitScalar(invocation.ArgumentList.Arguments[0].Expression, scope);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapIndexedItems} WHERE {HeapExecutionFilter()}__owner_id = {list} AND {CollectionValuePredicate(itemType, value, false)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsDictionaryType(receiverType.Name) && member.Name.Identifier.ValueText == "ContainsValue" &&
                invocation.ArgumentList.Arguments.Count == 1)
            {
                var valueType = GenericArguments(receiverType.Name)[1];
                var dictionary = EmitScalar(member.Expression, scope);
                var value = EmitScalar(invocation.ArgumentList.Arguments[0].Expression, scope);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {dictionary} AND {CollectionValuePredicate(valueType, value, false)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
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
            if (IsDictionaryType(receiverType.Name) && member.MemberName == "ContainsKey" &&
                invocation.Arguments.Count == 1)
            {
                var receiver = EmitScalar(member.Receiver, scope, substitutions);
                var keyType = GenericArguments(receiverType.Name)[0];
                var key = EmitScalar(invocation.Arguments[0], scope, substitutions);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {receiver} AND {DictionaryKeyPredicate(keyType, key)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsListType(receiverType.Name) && member.MemberName == "Contains" &&
                invocation.Arguments.Count == 1)
            {
                var receiver = EmitScalar(member.Receiver, scope, substitutions);
                var itemType = SequenceElementType(receiverType.Name);
                var value = EmitScalar(invocation.Arguments[0], scope, substitutions);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapIndexedItems} WHERE {HeapExecutionFilter()}__owner_id = {receiver} AND {CollectionValuePredicate(itemType, value, false)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsDictionaryType(receiverType.Name) && member.MemberName == "ContainsValue" &&
                invocation.Arguments.Count == 1)
            {
                var receiver = EmitScalar(member.Receiver, scope, substitutions);
                var valueType = GenericArguments(receiverType.Name)[1];
                var value = EmitScalar(invocation.Arguments[0], scope, substitutions);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {receiver} AND {CollectionValuePredicate(valueType, value, false)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
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
        if (IsArrayType(receiver.Name) && member.Name.Identifier.ValueText == "Length")
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
            expression = SqlScalarExpression.Primary(
                $"(SELECT {field.SqlName} FROM {type.TableName} WHERE {HeapExecutionFilter()}__object_id = {receiver})");
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
        if (type.Name == "byte[]")
            return prefix + (key ? "_binary" : "binary_value");
        if (type.IsReference)
            return prefix + (key ? "_reference" : "reference_value");
        return key ? "__key" : "__value";
    }

    private static string CollectionStoredValue(IrType type, string value) =>
        type.IsString || type.Name == "byte[]" || type.IsReference
            ? value
            : type.Name == "char"
                ? $"CONVERT(SQL_VARIANT, CONVERT(NCHAR(1), {value}))"
                : $"CONVERT(SQL_VARIANT, {value})";

    private static string CollectionReadValue(IrType type, bool key, string? qualifier = null)
    {
        var column = (qualifier is null ? string.Empty : qualifier + ".") + CollectionValueColumn(type, key);
        if (type.IsString || type.Name == "byte[]" || type.IsReference)
            return column;
        return $"CONVERT({type.SqlType()}, {column})";
    }

    private static string DictionaryKeyPredicate(IrType type, string value)
    {
        if (type.IsString)
            return $"__key_hash = {DictionaryKeyHash(type, value)} AND __key_text COLLATE Latin1_General_100_BIN2 = {value} COLLATE Latin1_General_100_BIN2";
        if (type.Name == "byte[]")
            return $"__key_hash = {DictionaryKeyHash(type, value)} AND __key_binary = {value}";
        return $"{CollectionValueColumn(type, true)} = {CollectionStoredValue(type, value)}";
    }

    private static string? DictionaryKeyHash(IrType type, string value)
    {
        if (type.IsString)
            return $"HASHBYTES('SHA2_256', CONVERT(VARBINARY(MAX), {value} COLLATE Latin1_General_100_BIN2))";
        if (type.Name == "byte[]")
            return $"HASHBYTES('SHA2_256', {value})";
        return null;
    }

    private static string CollectionValuePredicate(IrType type, string value, bool key)
    {
        var column = CollectionValueColumn(type, key);
        if (type.IsString)
            return $"{column} COLLATE Latin1_General_100_BIN2 = {value} COLLATE Latin1_General_100_BIN2";
        return $"{column} = {CollectionStoredValue(type, value)}";
    }

    private string FormatTextValue(IrType type, string value)
    {
        if (type.IsBoolean)
            return $"CASE {value} WHEN CAST(1 AS BIT) THEN N'True' " +
                   "WHEN CAST(0 AS BIT) THEN N'False' ELSE N'' END";
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
            var fieldValue = $"(SELECT {field.SqlName} FROM {declaringType.TableName} WHERE {HeapExecutionFilter()}__object_id = {reference})";
            parts.Add(FormatTextValue(field.Type, fieldValue));
        }
        parts.Add("N' }'");
        return $"CASE WHEN {reference} IS NULL THEN N'' ELSE CONCAT({string.Join(", ", parts)}) END";
    }

    private static string DefaultSql(IrType type)
    {
        if (type.IsString || type.IsReference || type.Name == "byte[]" || type == IrType.Unknown)
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
        $"(SELECT __count FROM {HeapObjects} WHERE {HeapExecutionFilter()}__id = {collection})";

    private string SequenceElementSql(string collection, string index, IrType itemType) =>
        $"(SELECT {CollectionReadValue(itemType, false)} FROM {HeapIndexedItems} WHERE {HeapExecutionFilter()}__owner_id = {collection} AND __index = {index})";

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

