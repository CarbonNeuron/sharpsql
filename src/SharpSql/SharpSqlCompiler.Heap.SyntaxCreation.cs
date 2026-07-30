using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private void EmitNewArray(
        ArrayCreationExpressionSyntax creation,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (creation.Type.RankSpecifiers.Count != 1 || creation.Type.RankSpecifiers[0].Sizes.Count != 1)
        {
            AddDiagnostic("SS6301", "Only one-dimensional arrays are supported.", creation);
            continuation("NULL");
            return;
        }

        var elementType = CSharpTypeFactory.From(creation.Type.ElementType);
        if (creation.Initializer is not null)
        {
            EmitNewInitializedArray(
                creation.Initializer.Expressions.ToArray(),
                elementType,
                scope,
                context,
                continuation);
            return;
        }

        var sizeExpression = creation.Type.RankSpecifiers[0].Sizes[0];
        EmitVmExpression(sizeExpression, scope, context, size =>
        {
            var sizeTemporary = _names.Allocate("_array_size");
            _sql.Line($"DECLARE {sizeTemporary} INT = {size};");
            var array = AllocateHeapHeader(ArrayHeapTypeId, "__count", sizeTemporary);
            InsertDefaultIndexedItems(array, sizeTemporary, elementType);
            continuation(array);
        });
    }

    private void EmitNewImplicitArray(
        ImplicitArrayCreationExpressionSyntax creation,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var arrayType = InferType(creation, scope);
        if (!IsArrayType(arrayType.Name))
        {
            AddDiagnostic("SS6301", "Could not infer the implicit array element type.", creation);
            continuation("NULL");
            return;
        }

        EmitNewInitializedArray(
            creation.Initializer.Expressions.ToArray(),
            SequenceElementType(arrayType.Name),
            scope,
            context,
            continuation);
    }

    private void EmitNewInitializedArray(
        IReadOnlyList<ExpressionSyntax> items,
        IrType elementType,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var captured = new List<string>();
        EvaluateItem(0);

        void EvaluateItem(int index)
        {
            if (index == items.Count)
            {
                var array = AllocateHeapHeader(ArrayHeapTypeId, "__count", captured.Count.ToString());
                InsertIndexedItems(array, elementType, captured);
                continuation(array);
                return;
            }

            EmitVmExpression(items[index], scope, context, value =>
            {
                captured.Add(CaptureHeapValue(items[index], elementType, value, scope, context));
                EvaluateItem(index + 1);
            });
        }
    }

    private void EmitNewObject(
        BaseObjectCreationExpressionSyntax creation,
        HeapType heapType,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var arguments = creation.ArgumentList?.Arguments ?? default;
        var constructor = heapType.Constructors.FirstOrDefault(candidate => candidate.TargetFields.Count == arguments.Count);
        if (constructor is null || constructor.TargetFields.Any(string.IsNullOrEmpty))
        {
            AddDiagnostic("SS6003", $"No field-mappable constructor for '{heapType.Name}' with {arguments.Count} arguments.", creation);
            continuation("NULL");
            return;
        }

        var assignments = new List<HeapValueAssignment>();
        EvaluateConstructorArgument(0);

        void EvaluateConstructorArgument(int index)
        {
            if (index == arguments.Count)
            {
                EvaluateInitializers(0, InitializerAssignments(creation.Initializer).ToArray());
                return;
            }

            var field = heapType.Fields[constructor.TargetFields[index]];
            EmitVmExpression(arguments[index].Expression, scope, context, value =>
            {
                assignments.Add(new HeapValueAssignment(
                    field,
                    CaptureHeapValue(arguments[index].Expression, field.Type, value, scope, context)));
                EvaluateConstructorArgument(index + 1);
            });
        }

        void EvaluateInitializers(int index, (string Name, ExpressionSyntax Value)[] initializers)
        {
            if (index == initializers.Length)
            {
                Allocate();
                return;
            }

            var initializer = initializers[index];
            if (!heapType.Fields.TryGetValue(initializer.Name, out var field))
            {
                AddDiagnostic("SS6004", $"Unknown member '{initializer.Name}' on '{heapType.Name}'.", initializer.Value);
                EvaluateInitializers(index + 1, initializers);
                return;
            }

            EmitVmExpression(initializer.Value, scope, context, value =>
            {
                assignments.RemoveAll(item => item.Field.Name == field.Name);
                assignments.Add(new HeapValueAssignment(
                    field,
                    CaptureHeapValue(initializer.Value, field.Type, value, scope, context)));
                EvaluateInitializers(index + 1, initializers);
            });
        }

        void Allocate()
        {
            var objectSql = _names.Allocate("_object");
            _sql.Line($"DECLARE {objectSql} INT;");
            _sql.Line($"INSERT INTO {HeapObjects} ({HeapObjectInsertColumns("__type_id")}) VALUES ({HeapObjectInsertValues($"{heapType.Id}")});");
            _sql.Line($"SET {objectSql} = CONVERT(INT, SCOPE_IDENTITY());");
            EmitHeapTypePayload(
                heapType,
                objectSql,
                field => assignments.LastOrDefault(item => item.Field.Name == field.Name)?.ValueSql ?? DefaultSql(field.Type));
            continuation(objectSql);
        }
    }

    private string CaptureHeapValue(
        ExpressionSyntax expression,
        IrType type,
        string value,
        VariableScope scope,
        VmMethod? context)
    {
        if (AnalyzeExpression(expression, scope).HasConstantValue)
            return value;
        var storage = AllocateVmTemporary(type, context);
        StoreVmTemporary(storage, value);
        return ReadVmTemporary(storage);
    }

    private void EmitRecordWith(
        WithExpressionSyntax expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var typeName = InferType(expression.Expression, scope).Name;
        if (!_heapTypes.TryGetValue(typeName, out var heapType))
        {
            AddDiagnostic("SS6004", $"Record clone type '{typeName}' is not available in the managed heap.", expression);
            continuation("NULL");
            return;
        }

        var initializers = InitializerAssignments(expression.Initializer).ToArray();
        var initializedFields = initializers.Select(initializer => initializer.Name).ToHashSet(StringComparer.Ordinal);
        var unknownMember = initializers.FirstOrDefault(initializer => !heapType.Fields.ContainsKey(initializer.Name));
        if (unknownMember != default)
        {
            AddDiagnostic("SS6004", $"Unknown member '{unknownMember.Name}' on '{heapType.Name}'.", unknownMember.Value);
            continuation("NULL");
            return;
        }

        EmitVmExpression(expression.Expression, scope, context, receiver =>
        {
            var receiverStorage = AllocateVmTemporary(new IrType(heapType.Name, IsReference: true), context);
            StoreVmTemporary(receiverStorage, receiver);
            var savedReceiver = ReadVmTemporary(receiverStorage);
            var assignments = new List<HeapValueAssignment>();
            CaptureField(0);

            void CaptureField(int index)
            {
                if (index == heapType.Fields.Count)
                {
                    EvaluateInitializer(0);
                    return;
                }

                var field = heapType.Fields.Values.ElementAt(index);
                if (initializedFields.Contains(field.Name))
                {
                    CaptureField(index + 1);
                    return;
                }
                var storage = AllocateVmTemporary(field.Type, context);
                StoreVmTemporary(
                    storage,
                    HeapFieldReadValue(heapType, field, savedReceiver));
                assignments.Add(new HeapValueAssignment(field, ReadVmTemporary(storage)));
                CaptureField(index + 1);
            }

            void EvaluateInitializer(int index)
            {
                if (index == initializers.Length)
                {
                    AllocateClone();
                    return;
                }

                var initializer = initializers[index];
                var field = heapType.Fields[initializer.Name];
                EmitVmExpression(initializer.Value, scope, context, value =>
                {
                    var storage = AllocateVmTemporary(field.Type, context);
                    StoreVmTemporary(storage, value);
                    assignments.RemoveAll(item => item.Field.Name == field.Name);
                    assignments.Add(new HeapValueAssignment(field, ReadVmTemporary(storage)));
                    EvaluateInitializer(index + 1);
                });
            }

            void AllocateClone()
            {
                var objectSql = _names.Allocate("_object");
                _sql.Line($"DECLARE {objectSql} INT;");
                _sql.Line($"INSERT INTO {HeapObjects} ({HeapObjectInsertColumns("__type_id")}) VALUES ({HeapObjectInsertValues($"{heapType.Id}")});");
                _sql.Line($"SET {objectSql} = CONVERT(INT, SCOPE_IDENTITY());");
                EmitHeapTypePayload(
                    heapType,
                    objectSql,
                    field => assignments.Single(item => item.Field.Name == field.Name).ValueSql);
                continuation(objectSql);
            }
        });
    }

    private void EmitNewList(
        BaseObjectCreationExpressionSyntax creation,
        string typeName,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var elementType = GenericArguments(typeName)[0];
        var items = creation.Initializer?.Expressions ?? default;
        var captured = new List<string>();
        EvaluateItem(0);

        void EvaluateItem(int index)
        {
            if (index == items.Count)
            {
                Allocate();
                return;
            }
            EmitVmExpression(items[index], scope, context, value =>
            {
                if (items[index] is BaseObjectCreationExpressionSyntax creation && IsHeapCreation(creation) ||
                    items[index] is WithExpressionSyntax)
                {
                    captured.Add(value);
                }
                else
                {
                    var storage = AllocateVmTemporary(elementType, context);
                    StoreVmTemporary(storage, value);
                    captured.Add(ReadVmTemporary(storage));
                }
                EvaluateItem(index + 1);
            });
        }

        void Allocate()
        {
            var listSql = AllocateHeapHeader(ListHeapTypeId, "__count", "0");
            InsertIndexedItems(listSql, elementType, captured);
            if (captured.Count > 0)
                _sql.Line($"UPDATE {HeapObjects} SET __count = {captured.Count} WHERE {HeapObjectExecutionFilter()}__id = {listSql};");
            continuation(listSql);
        }
    }

    private void EmitNewDictionary(
        BaseObjectCreationExpressionSyntax creation,
        string typeName,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (creation.Initializer is { Expressions.Count: > 0 })
            AddDiagnostic("SS6005", "Dictionary collection initializers are not supported yet; use Add calls.", creation.Initializer);
        continuation(AllocateHeapHeader(DictionaryHeapTypeId, "__count", "0"));
    }

    private string AllocateHeapHeader(int typeId, string column, string value)
        => AllocateHeapHeader(typeId, [(column, value)]);

    private string AllocateHeapHeader(
        int typeId,
        IReadOnlyList<(string Column, string Value)> state)
    {
        var objectSql = _names.Allocate("_object");
        _sql.Line($"DECLARE {objectSql} INT;");
        var columns = new[] { "__type_id" }.Concat(state.Select(item => item.Column));
        var values = new[] { typeId.ToString() }.Concat(state.Select(item => item.Value));
        _sql.Line($"INSERT INTO {HeapObjects} ({HeapObjectInsertColumns(string.Join(", ", columns))}) VALUES ({HeapObjectInsertValues(string.Join(", ", values))});");
        _sql.Line($"SET {objectSql} = CONVERT(INT, SCOPE_IDENTITY());");
        return objectSql;
    }
}
