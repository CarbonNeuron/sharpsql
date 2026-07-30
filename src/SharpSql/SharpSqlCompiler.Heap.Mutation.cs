using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private bool TryEmitHeapStatement(ExpressionSyntax expression, VariableScope scope, VmMethod? context = null)
    {
        if (expression is InvocationExpressionSyntax invocation &&
            invocation.Expression is MemberAccessExpressionSyntax member &&
            member.Name.Identifier.ValueText == "Add")
        {
            var receiverType = InferType(member.Expression, scope);
            if (IsListType(receiverType.Name))
            {
                EmitListAdd(member.Expression, invocation.ArgumentList.Arguments, receiverType, scope, context);
                return true;
            }
            if (IsDictionaryType(receiverType.Name))
            {
                EmitDictionaryAdd(member.Expression, invocation.ArgumentList.Arguments, receiverType, scope, context);
                return true;
            }
        }

        if (expression is InvocationExpressionSyntax collectionCall &&
            collectionCall.Expression is MemberAccessExpressionSyntax collectionMember)
        {
            var receiverType = InferType(collectionMember.Expression, scope);
            var methodName = collectionMember.Name.Identifier.ValueText;
            if (methodName == "Clear" && collectionCall.ArgumentList.Arguments.Count == 0)
            {
                var receiver = EmitScalar(collectionMember.Expression, scope);
                if (IsListType(receiverType.Name))
                {
                    _sql.Line($"DELETE FROM {HeapIndexedItems} WHERE {HeapExecutionFilter()}__owner_id = {receiver};");
                    _sql.Line($"UPDATE {HeapObjects} SET __count = 0 WHERE {HeapExecutionFilter()}__id = {receiver};");
                    return true;
                }
                if (IsDictionaryType(receiverType.Name))
                {
                    _sql.Line($"DELETE FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {receiver};");
                    _sql.Line($"UPDATE {HeapObjects} SET __count = 0 WHERE {HeapExecutionFilter()}__id = {receiver};");
                    return true;
                }
            }
            if (methodName == "RemoveAt" && IsListType(receiverType.Name) &&
                collectionCall.ArgumentList.Arguments.Count == 1)
            {
                EmitVmExpression(collectionCall.ArgumentList.Arguments[0].Expression, scope, context, index =>
                {
                    var receiver = EmitScalar(collectionMember.Expression, scope);
                    _sql.Line($"IF {index} < 0 OR {index} >= {SequenceCountSql(receiver)} THROW 51002, 'List index was out of range.', 1;");
                    _sql.Line($"DELETE FROM {HeapIndexedItems} WHERE {HeapExecutionFilter()}__owner_id = {receiver} AND __index = {index};");
                    _sql.Line($"UPDATE {HeapIndexedItems} SET __index = __index - 1 WHERE {HeapExecutionFilter()}__owner_id = {receiver} AND __index > {index};");
                    _sql.Line($"UPDATE {HeapObjects} SET __count = __count - 1 WHERE {HeapExecutionFilter()}__id = {receiver};");
                });
                return true;
            }
            if (methodName == "Remove" && IsDictionaryType(receiverType.Name) &&
                collectionCall.ArgumentList.Arguments.Count == 1)
            {
                var types = GenericArguments(receiverType.Name);
                EmitVmExpression(collectionCall.ArgumentList.Arguments[0].Expression, scope, context, key =>
                {
                    var receiver = EmitScalar(collectionMember.Expression, scope);
                    var predicate = DictionaryKeyPredicate(types[0], key);
                    _sql.Line($"DELETE FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {receiver} AND {predicate};");
                    _sql.Line("IF @@ROWCOUNT > 0");
                    _sql.Line("BEGIN");
                    using (_sql.Indent())
                        _sql.Line($"UPDATE {HeapObjects} SET __count = __count - 1 WHERE {HeapExecutionFilter()}__id = {receiver};");
                    _sql.Line("END;");
                });
                return true;
            }
        }

        if (expression is AssignmentExpressionSyntax assignment &&
            (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) || HeapAssignmentOperator(assignment.Kind()) is not null))
        {
            if (assignment.Left is MemberAccessExpressionSyntax fieldAccess &&
                TryResolveHeapField(fieldAccess, scope, out var heapType, out var field))
            {
                var receiver = EmitScalar(fieldAccess.Expression, scope);
                var currentValue = $"(SELECT {field.SqlName} FROM {heapType.TableName} WHERE {HeapExecutionFilter()}__object_id = {receiver})";
                EmitVmExpression(assignment.Right, scope, context, value =>
                    _sql.Line($"UPDATE {heapType.TableName} SET {field.SqlName} = {HeapAssignmentValue(assignment, field.Type, currentValue, value)} WHERE {HeapExecutionFilter()}__object_id = {receiver};"));
                return true;
            }

            if (assignment.Left is IdentifierNameSyntax implicitField &&
                scope.Find(implicitField.Identifier.ValueText) is null &&
                TryResolveImplicitHeapField(
                    implicitField.Identifier.ValueText,
                    scope,
                    substitutions: null,
                    out var implicitType,
                    out var implicitMember,
                    out var implicitReceiver))
            {
                var currentValue = $"(SELECT {implicitMember.SqlName} FROM {implicitType.TableName} WHERE {HeapExecutionFilter()}__object_id = {implicitReceiver})";
                EmitVmExpression(assignment.Right, scope, context, value =>
                    _sql.Line($"UPDATE {implicitType.TableName} SET {implicitMember.SqlName} = {HeapAssignmentValue(assignment, implicitMember.Type, currentValue, value)} WHERE {HeapExecutionFilter()}__object_id = {implicitReceiver};"));
                return true;
            }

            if (assignment.Left is ElementAccessExpressionSyntax element)
            {
                var receiverType = InferType(element.Expression, scope);
                if (IsSequenceType(receiverType.Name))
                {
                    EmitListSet(element, assignment.Right, receiverType, scope, context);
                    return true;
                }
                if (IsDictionaryType(receiverType.Name))
                {
                    EmitDictionarySet(element, assignment.Right, receiverType, scope, context);
                    return true;
                }
            }
        }
        return false;
    }

    private bool TryEmitHeapStatement(IrExpression expression, VariableScope scope, VmMethod? context = null)
    {
        if (expression is IrInvocationExpression { Target: IrMemberExpression member } invocation)
        {
            var receiverType = member.Receiver.Type;
            if (member.MemberName == "Add")
            {
                if (IsListType(receiverType.Name))
                {
                    EmitListAdd(member.Receiver, invocation.Arguments, receiverType, scope, context, invocation.Source);
                    return true;
                }
                if (IsDictionaryType(receiverType.Name))
                {
                    EmitDictionaryAdd(member.Receiver, invocation.Arguments, receiverType, scope, context, invocation.Source);
                    return true;
                }
            }

            if (member.MemberName == "Clear" && invocation.Arguments.Count == 0)
            {
                var receiver = EmitScalar(member.Receiver, scope);
                if (IsListType(receiverType.Name))
                {
                    _sql.Line($"DELETE FROM {HeapIndexedItems} WHERE {HeapExecutionFilter()}__owner_id = {receiver};");
                    _sql.Line($"UPDATE {HeapObjects} SET __count = 0 WHERE {HeapExecutionFilter()}__id = {receiver};");
                    return true;
                }
                if (IsDictionaryType(receiverType.Name))
                {
                    _sql.Line($"DELETE FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {receiver};");
                    _sql.Line($"UPDATE {HeapObjects} SET __count = 0 WHERE {HeapExecutionFilter()}__id = {receiver};");
                    return true;
                }
            }

            if (member.MemberName == "RemoveAt" && IsListType(receiverType.Name) && invocation.Arguments.Count == 1)
            {
                EmitVmExpression(invocation.Arguments[0], scope, context, index =>
                {
                    var receiver = EmitScalar(member.Receiver, scope);
                    _sql.Line($"IF {index} < 0 OR {index} >= {SequenceCountSql(receiver)} THROW 51002, 'List index was out of range.', 1;");
                    _sql.Line($"DELETE FROM {HeapIndexedItems} WHERE {HeapExecutionFilter()}__owner_id = {receiver} AND __index = {index};");
                    _sql.Line($"UPDATE {HeapIndexedItems} SET __index = __index - 1 WHERE {HeapExecutionFilter()}__owner_id = {receiver} AND __index > {index};");
                    _sql.Line($"UPDATE {HeapObjects} SET __count = __count - 1 WHERE {HeapExecutionFilter()}__id = {receiver};");
                });
                return true;
            }

            if (member.MemberName == "Remove" && IsDictionaryType(receiverType.Name) && invocation.Arguments.Count == 1)
            {
                var types = GenericArguments(receiverType.Name);
                EmitVmExpression(invocation.Arguments[0], scope, context, key =>
                {
                    var receiver = EmitScalar(member.Receiver, scope);
                    var predicate = DictionaryKeyPredicate(types[0], key);
                    _sql.Line($"DELETE FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {receiver} AND {predicate};");
                    _sql.Line("IF @@ROWCOUNT > 0");
                    _sql.Line("BEGIN");
                    using (_sql.Indent())
                        _sql.Line($"UPDATE {HeapObjects} SET __count = __count - 1 WHERE {HeapExecutionFilter()}__id = {receiver};");
                    _sql.Line("END;");
                });
                return true;
            }
        }

        if (expression is IrUnaryExpression
            {
                Operator: IrUnaryOperator.PreIncrement or IrUnaryOperator.PostIncrement or
                    IrUnaryOperator.PreDecrement or IrUnaryOperator.PostDecrement
            } mutation)
        {
            HeapType? mutationType = null;
            HeapField? mutationField = null;
            string? mutationReceiver = null;
            if (mutation.Operand is IrMemberExpression memberTarget &&
                TryResolveHeapField(
                    memberTarget.Receiver.Type,
                    memberTarget.MemberName,
                    memberTarget.MemberId,
                    out mutationType,
                    out mutationField))
            {
                mutationReceiver = EmitScalar(memberTarget.Receiver, scope);
            }
            else if (mutation.Operand is IrVariableExpression implicitTarget &&
                     scope.Find(implicitTarget.Symbol) is null &&
                     TryResolveImplicitHeapField(
                         implicitTarget.Symbol.Name,
                         implicitTarget.Symbol.ReferencedMemberId,
                         scope,
                         substitutions: null,
                         out mutationType,
                         out mutationField,
                         out mutationReceiver))
            {
            }

            if (mutationType is not null && mutationField is not null && mutationReceiver is not null)
            {
                var operation = mutation.Operator is IrUnaryOperator.PreIncrement or IrUnaryOperator.PostIncrement
                    ? "+ 1"
                    : "- 1";
                _sql.Line($"UPDATE {mutationType.TableName} SET {mutationField.SqlName} = {mutationField.SqlName} {operation} WHERE {HeapExecutionFilter()}__object_id = {mutationReceiver};");
                return true;
            }
        }

        if (expression is not IrAssignmentExpression assignment)
            return false;

        if (assignment.Target is IrMemberExpression fieldAccess &&
            TryResolveHeapField(
                fieldAccess.Receiver.Type,
                fieldAccess.MemberName,
                fieldAccess.MemberId,
                out var heapType,
                out var field))
        {
            var receiver = EmitScalar(fieldAccess.Receiver, scope);
            var currentValue = $"(SELECT {field.SqlName} FROM {heapType.TableName} WHERE {HeapExecutionFilter()}__object_id = {receiver})";
            EmitVmExpression(assignment.Value, scope, context, value =>
                _sql.Line($"UPDATE {heapType.TableName} SET {field.SqlName} = {HeapAssignmentValue(assignment.Operator, field.Type, currentValue, value)} WHERE {HeapExecutionFilter()}__object_id = {receiver};"));
            return true;
        }

        if (assignment.Target is IrVariableExpression implicitField &&
            scope.Find(implicitField.Symbol) is null &&
            TryResolveImplicitHeapField(
                implicitField.Symbol.Name,
                implicitField.Symbol.ReferencedMemberId,
                scope,
                substitutions: null,
                out var implicitType,
                out var implicitMember,
                out var implicitReceiver))
        {
            var currentValue = $"(SELECT {implicitMember.SqlName} FROM {implicitType.TableName} WHERE {HeapExecutionFilter()}__object_id = {implicitReceiver})";
            EmitVmExpression(assignment.Value, scope, context, value =>
                _sql.Line($"UPDATE {implicitType.TableName} SET {implicitMember.SqlName} = {HeapAssignmentValue(assignment.Operator, implicitMember.Type, currentValue, value)} WHERE {HeapExecutionFilter()}__object_id = {implicitReceiver};"));
            return true;
        }

        if (assignment.Target is IrElementExpression { Arguments.Count: 1 } element)
        {
            var receiverType = element.Receiver.Type;
            if (IsSequenceType(receiverType.Name))
            {
                EmitListSet(element, assignment.Value, receiverType, scope, context);
                return true;
            }
            if (IsDictionaryType(receiverType.Name))
            {
                EmitDictionarySet(element, assignment.Value, receiverType, scope, context);
                return true;
            }
        }
        return false;
    }

    private static string HeapAssignmentValue(
        IrAssignmentOperator assignmentOperator,
        IrType targetType,
        string currentValue,
        string value)
    {
        if (assignmentOperator == IrAssignmentOperator.Assign)
            return value;
        var operation = assignmentOperator switch
        {
            IrAssignmentOperator.Add => "+",
            IrAssignmentOperator.Subtract => "-",
            IrAssignmentOperator.Multiply => "*",
            IrAssignmentOperator.Divide => "/",
            IrAssignmentOperator.Remainder => "%",
            IrAssignmentOperator.BitwiseAnd => "&",
            IrAssignmentOperator.BitwiseOr => "|",
            IrAssignmentOperator.ExclusiveOr => "^",
            _ => string.Empty
        };
        return targetType.IsString && operation == "+"
            ? $"CONCAT({currentValue}, {value})"
            : $"{currentValue} {operation} ({value})";
    }

    private static string HeapAssignmentValue(
        AssignmentExpressionSyntax assignment,
        IrType targetType,
        string currentValue,
        string value)
    {
        if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            return value;

        var operation = HeapAssignmentOperator(assignment.Kind())!;
        return targetType.IsString && operation == "+"
            ? $"CONCAT({currentValue}, {value})"
            : $"{currentValue} {operation} ({value})";
    }

    private static string? HeapAssignmentOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.AddAssignmentExpression => "+",
        SyntaxKind.SubtractAssignmentExpression => "-",
        SyntaxKind.MultiplyAssignmentExpression => "*",
        SyntaxKind.DivideAssignmentExpression => "/",
        SyntaxKind.ModuloAssignmentExpression => "%",
        SyntaxKind.AndAssignmentExpression => "&",
        SyntaxKind.OrAssignmentExpression => "|",
        SyntaxKind.ExclusiveOrAssignmentExpression => "^",
        _ => null
    };

    private void EmitListAdd(
        ExpressionSyntax receiver,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        IrType listType,
        VariableScope scope,
        VmMethod? context)
    {
        if (arguments.Count != 1)
        {
            AddDiagnostic("SS6101", "List.Add expects one argument.", receiver);
            return;
        }
        var elementType = SequenceElementType(listType.Name);
        EmitVmExpression(arguments[0].Expression, scope, context, value =>
        {
            var list = EmitScalar(receiver, scope);
            var index = $"(SELECT __count FROM {HeapObjects} WHERE {HeapExecutionFilter()}__id = {list})";
            InsertIndexedItem(list, index, elementType, value);
            _sql.Line($"UPDATE {HeapObjects} SET __count = __count + 1 WHERE {HeapExecutionFilter()}__id = {list};");
        });
    }

    private void EmitListAdd(
        IrExpression receiver,
        IReadOnlyList<IrExpression> arguments,
        IrType listType,
        VariableScope scope,
        VmMethod? context,
        IrSource source)
    {
        if (arguments.Count != 1)
        {
            AddDiagnostic("SS6101", "List.Add expects one argument.", source);
            return;
        }
        var elementType = SequenceElementType(listType.Name);
        EmitVmExpression(arguments[0], scope, context, value =>
        {
            var list = EmitScalar(receiver, scope);
            var index = $"(SELECT __count FROM {HeapObjects} WHERE {HeapExecutionFilter()}__id = {list})";
            InsertIndexedItem(list, index, elementType, value);
            _sql.Line($"UPDATE {HeapObjects} SET __count = __count + 1 WHERE {HeapExecutionFilter()}__id = {list};");
        });
    }

    private void EmitListSet(
        ElementAccessExpressionSyntax element,
        ExpressionSyntax valueExpression,
        IrType listType,
        VariableScope scope,
        VmMethod? context)
    {
        var elementType = SequenceElementType(listType.Name);
        var indexExpression = element.ArgumentList.Arguments.Single().Expression;
        EmitVmExpression(indexExpression, scope, context, index =>
            EmitVmExpression(valueExpression, scope, context, value =>
            {
                var list = EmitScalar(element.Expression, scope);
                EmitSequenceIndexGuard(listType, list, index);
                _sql.Line($"UPDATE {HeapIndexedItems} SET {CollectionValueColumn(elementType, false)} = {CollectionStoredValue(elementType, value)} WHERE {HeapExecutionFilter()}__owner_id = {list} AND __index = {index};");
            }));
    }

    private void EmitListSet(
        IrElementExpression element,
        IrExpression valueExpression,
        IrType listType,
        VariableScope scope,
        VmMethod? context)
    {
        var elementType = SequenceElementType(listType.Name);
        EmitVmExpression(element.Arguments[0], scope, context, index =>
            EmitVmExpression(valueExpression, scope, context, value =>
            {
                var list = EmitScalar(element.Receiver, scope);
                EmitSequenceIndexGuard(listType, list, index);
                _sql.Line($"UPDATE {HeapIndexedItems} SET {CollectionValueColumn(elementType, false)} = {CollectionStoredValue(elementType, value)} WHERE {HeapExecutionFilter()}__owner_id = {list} AND __index = {index};");
            }));
    }

    private void InsertIndexedItem(string list, string index, IrType type, string value) =>
        _sql.Line($"INSERT INTO {HeapIndexedItems} ({HeapInsertColumns($"__owner_id, __index, {CollectionValueColumn(type, false)}")}) VALUES ({HeapInsertValues($"{list}, {index}, {CollectionStoredValue(type, value)}")});");

    private void InsertDefaultIndexedItems(string owner, string count, IrType type)
    {
        var generated = $"__array_generated_{++_nextHeapAliasId}";
        var column = CollectionValueColumn(type, false);
        var value = CollectionStoredValue(type, DefaultSql(type));
        _sql.Line(
            $"INSERT INTO {HeapIndexedItems} ({HeapInsertColumns($"__owner_id, __index, {column}")}) " +
            $"SELECT {HeapInsertValues($"{owner}, CONVERT(INT, {generated}.[value]), {value}")} " +
            $"FROM GENERATE_SERIES(CONVERT(BIGINT, 0), CONVERT(BIGINT, {count}) - 1, CONVERT(BIGINT, 1)) AS {generated} " +
            $"WHERE {count} > 0;");
    }

    private void InsertIndexedItems(string list, IrType type, IReadOnlyList<string> values)
    {
        const int maximumRowsPerValuesClause = 1000;
        var column = CollectionValueColumn(type, key: false);
        for (var start = 0; start < values.Count; start += maximumRowsPerValuesClause)
        {
            var count = Math.Min(maximumRowsPerValuesClause, values.Count - start);
            _sql.Line($"INSERT INTO {HeapIndexedItems} ({HeapInsertColumns($"__owner_id, __index, {column}")}) VALUES");
            using (_sql.Indent())
            {
                for (var offset = 0; offset < count; offset++)
                {
                    var index = start + offset;
                    var terminator = offset + 1 == count ? ";" : ",";
                    _sql.Line($"({HeapInsertValues($"{list}, {index}, {CollectionStoredValue(type, values[index])}")}){terminator}");
                }
            }
        }
    }

    private void EmitDictionaryAdd(
        ExpressionSyntax receiver,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        IrType dictionaryType,
        VariableScope scope,
        VmMethod? context)
    {
        if (arguments.Count != 2)
        {
            AddDiagnostic("SS6201", "Dictionary.Add expects two arguments.", receiver);
            return;
        }
        var types = GenericArguments(dictionaryType.Name);
        EmitVmExpression(arguments[0].Expression, scope, context, key =>
        {
            var keyStore = AllocateVmTemporary(types[0], context);
            StoreVmTemporary(keyStore, key);
            EmitVmExpression(arguments[1].Expression, scope, context, value =>
            {
                var dictionary = EmitScalar(receiver, scope);
                var savedKey = ReadVmTemporary(keyStore);
                _sql.Line($"IF EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {dictionary} AND {DictionaryKeyPredicate(types[0], savedKey)}) THROW 51001, 'Duplicate dictionary key.', 1;");
                InsertDictionaryEntry(dictionary, types[0], savedKey, types[1], value);
                _sql.Line($"UPDATE {HeapObjects} SET __count = __count + 1 WHERE {HeapExecutionFilter()}__id = {dictionary};");
            });
        });
    }

    private void EmitDictionaryAdd(
        IrExpression receiver,
        IReadOnlyList<IrExpression> arguments,
        IrType dictionaryType,
        VariableScope scope,
        VmMethod? context,
        IrSource source)
    {
        if (arguments.Count != 2)
        {
            AddDiagnostic("SS6201", "Dictionary.Add expects two arguments.", source);
            return;
        }
        var types = GenericArguments(dictionaryType.Name);
        EmitVmExpression(arguments[0], scope, context, key =>
        {
            var keyStore = AllocateVmTemporary(types[0], context);
            StoreVmTemporary(keyStore, key);
            EmitVmExpression(arguments[1], scope, context, value =>
            {
                var dictionary = EmitScalar(receiver, scope);
                var savedKey = ReadVmTemporary(keyStore);
                _sql.Line($"IF EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE {HeapExecutionFilter()}__dictionary_id = {dictionary} AND {DictionaryKeyPredicate(types[0], savedKey)}) THROW 51001, 'Duplicate dictionary key.', 1;");
                InsertDictionaryEntry(dictionary, types[0], savedKey, types[1], value);
                _sql.Line($"UPDATE {HeapObjects} SET __count = __count + 1 WHERE {HeapExecutionFilter()}__id = {dictionary};");
            });
        });
    }

    private void EmitDictionarySet(
        ElementAccessExpressionSyntax element,
        ExpressionSyntax valueExpression,
        IrType dictionaryType,
        VariableScope scope,
        VmMethod? context)
    {
        var types = GenericArguments(dictionaryType.Name);
        var keyExpression = element.ArgumentList.Arguments.Single().Expression;
        EmitVmExpression(keyExpression, scope, context, key =>
        {
            var keyStore = AllocateVmTemporary(types[0], context);
            StoreVmTemporary(keyStore, key);
            EmitVmExpression(valueExpression, scope, context, value =>
            {
                var dictionary = EmitScalar(element.Expression, scope);
                var savedKey = ReadVmTemporary(keyStore);
                var predicate = DictionaryKeyPredicate(types[0], savedKey);
                _sql.Line($"UPDATE {HeapDictionaryEntries} SET {CollectionValueColumn(types[1], false)} = {CollectionStoredValue(types[1], value)} WHERE {HeapExecutionFilter()}__dictionary_id = {dictionary} AND {predicate};");
                _sql.Line("IF @@ROWCOUNT = 0");
                _sql.Line("BEGIN");
                using (_sql.Indent())
                {
                    InsertDictionaryEntry(dictionary, types[0], savedKey, types[1], value);
                    _sql.Line($"UPDATE {HeapObjects} SET __count = __count + 1 WHERE {HeapExecutionFilter()}__id = {dictionary};");
                }
                _sql.Line("END;");
            });
        });
    }

    private void EmitDictionarySet(
        IrElementExpression element,
        IrExpression valueExpression,
        IrType dictionaryType,
        VariableScope scope,
        VmMethod? context)
    {
        var types = GenericArguments(dictionaryType.Name);
        EmitVmExpression(element.Arguments[0], scope, context, key =>
        {
            var keyStore = AllocateVmTemporary(types[0], context);
            StoreVmTemporary(keyStore, key);
            EmitVmExpression(valueExpression, scope, context, value =>
            {
                var dictionary = EmitScalar(element.Receiver, scope);
                var savedKey = ReadVmTemporary(keyStore);
                var predicate = DictionaryKeyPredicate(types[0], savedKey);
                _sql.Line($"UPDATE {HeapDictionaryEntries} SET {CollectionValueColumn(types[1], false)} = {CollectionStoredValue(types[1], value)} WHERE {HeapExecutionFilter()}__dictionary_id = {dictionary} AND {predicate};");
                _sql.Line("IF @@ROWCOUNT = 0");
                _sql.Line("BEGIN");
                using (_sql.Indent())
                {
                    InsertDictionaryEntry(dictionary, types[0], savedKey, types[1], value);
                    _sql.Line($"UPDATE {HeapObjects} SET __count = __count + 1 WHERE {HeapExecutionFilter()}__id = {dictionary};");
                }
                _sql.Line("END;");
            });
        });
    }

    private void InsertDictionaryEntry(string dictionary, IrType keyType, string key, IrType valueType, string value)
    {
        var columns = new List<string> { "__dictionary_id", CollectionValueColumn(keyType, true) };
        var values = new List<string> { dictionary, CollectionStoredValue(keyType, key) };
        var hash = DictionaryKeyHash(keyType, key);
        if (hash is not null)
        {
            columns.Add("__key_hash");
            values.Add(hash);
        }
        columns.Add(CollectionValueColumn(valueType, false));
        values.Add(CollectionStoredValue(valueType, value));
        _sql.Line($"INSERT INTO {HeapDictionaryEntries} ({HeapInsertColumns(string.Join(", ", columns))}) VALUES ({HeapInsertValues(string.Join(", ", values))});");
    }

}

