using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private bool TryEmitHeapExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (expression is ElementAccessExpressionSyntax element &&
            TryEmitHeapElementExpression(element, scope, context, continuation))
            return true;

        if (expression is WithExpressionSyntax withExpression)
        {
            EmitRecordWith(withExpression, scope, context, continuation);
            return true;
        }

        if (TryEmitLinqMaterialization(expression, scope, context, continuation))
            return true;

        if (expression is InvocationExpressionSyntax randomInvocation && IsRandomInvocation(randomInvocation))
        {
            EmitRandomInvocation(randomInvocation, scope, context, continuation);
            return true;
        }

        if (expression is ArrayCreationExpressionSyntax arrayCreation &&
            arrayCreation.Type.ElementType.ToString() != "byte")
        {
            EmitNewArray(arrayCreation, scope, context, continuation);
            return true;
        }

        if (expression is ImplicitArrayCreationExpressionSyntax implicitArrayCreation &&
            InferType(implicitArrayCreation, scope).Name != "byte[]")
        {
            EmitNewImplicitArray(implicitArrayCreation, scope, context, continuation);
            return true;
        }

        if (expression is not BaseObjectCreationExpressionSyntax creation || !IsHeapCreation(creation))
            return false;

        var typeName = CreationTypeName(creation);
        if (IsListType(typeName))
            EmitNewList(creation, typeName, scope, context, continuation);
        else if (IsDictionaryType(typeName))
            EmitNewDictionary(creation, typeName, scope, context, continuation);
        else if (IsRandomType(typeName))
            EmitNewRandom(creation, scope, context, continuation);
        else
            EmitNewObject(creation, _heapTypes[typeName], scope, context, continuation);
        return true;
    }

    private bool TryEmitHeapExpression(
        IrExpression expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        switch (expression)
        {
            case IrElementExpression element:
                return TryEmitHeapElementExpression(element, scope, context, continuation);
            case IrArrayCreationExpression array when array.ElementType.Name != "byte":
                EmitNewArray(array, scope, context, continuation);
                return true;
            case IrObjectCreationExpression creation when IsListType(creation.CreatedType.Name):
                EmitNewList(creation, scope, context, continuation);
                return true;
            case IrObjectCreationExpression creation when IsDictionaryType(creation.CreatedType.Name):
                EmitNewDictionary(creation, continuation);
                return true;
            case IrObjectCreationExpression creation when IsRandomType(creation.CreatedType.Name):
                EmitNewRandom(creation, scope, context, continuation);
                return true;
            case IrObjectCreationExpression creation when _heapTypes.TryGetValue(creation.CreatedType.Name, out var heapType):
                EmitNewObject(creation, heapType, scope, context, continuation);
                return true;
            case IrWithExpression withExpression when _heapTypes.TryGetValue(withExpression.Type.Name, out var heapType):
                EmitRecordWith(withExpression, heapType, scope, context, continuation);
                return true;
            case IrInvocationExpression
            {
                Target: IrMemberExpression member
            } invocation when
                IsRandomType(member.Receiver.Type.Name) &&
                member.MemberName is "Next" or "NextDouble":
                EmitRandomInvocation(invocation, member, scope, context, continuation);
                return true;
            default:
                return false;
        }
    }

    private void EmitNewArray(
        IrArrayCreationExpression creation,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (creation.Length is null)
        {
            EmitNewInitializedArray(creation.Elements, creation.ElementType, scope, context, continuation);
            return;
        }

        EmitVmExpression(creation.Length, scope, context, size =>
        {
            var sizeTemporary = _names.Allocate("_array_size");
            _sql.Line($"DECLARE {sizeTemporary} INT = {size};");
            var array = AllocateHeapHeader(ArrayHeapTypeId, "__count", sizeTemporary);
            InsertDefaultIndexedItems(array, sizeTemporary, creation.ElementType);
            continuation(array);
        });
    }

    private void EmitNewInitializedArray(
        IReadOnlyList<IrExpression> items,
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
                if (items[index].Facts.HasConstantValue)
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
    }

    private void EmitNewObject(
        IrObjectCreationExpression creation,
        HeapType heapType,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var constructor = creation.ConstructorId.IsNone
            ? heapType.Constructors.FirstOrDefault(candidate =>
                (candidate.Parameters.Count > 0 ? candidate.Parameters.Count : candidate.TargetFields.Count) ==
                creation.Arguments.Count)
            : heapType.Constructors.FirstOrDefault(candidate => candidate.Id == creation.ConstructorId);
        if (constructor is null)
        {
            AddDiagnostic(
                "SS6003",
                $"No constructor for '{heapType.Name}' with {creation.Arguments.Count} arguments.",
                creation.Source);
            continuation("NULL");
            return;
        }

        var initializers = new List<(string Name, IrExpression Value)>();
        foreach (var initializer in creation.Initializers)
        {
            if (initializer is not IrAssignmentExpression assignment ||
                AssignmentMemberName(assignment.Target) is not { } name)
            {
                AddDiagnostic("SS6004", $"Unsupported initializer on '{heapType.Name}'.", initializer.Source);
                continue;
            }
            initializers.Add((name, assignment.Value));
        }

        var capturedArguments = new List<string>();
        EvaluateConstructorArgument(0);

        void EvaluateConstructorArgument(int index)
        {
            if (index == creation.Arguments.Count)
            {
                Allocate();
                return;
            }

            var argument = creation.Arguments[index];
            var parameterType = index < constructor.Parameters.Count
                ? constructor.Parameters[index].Type
                : argument.Type;
            EmitVmExpression(argument, scope, context, value =>
            {
                capturedArguments.Add(CaptureHeapValue(argument, parameterType, value, context));
                EvaluateConstructorArgument(index + 1);
            });
        }

        void Allocate()
        {
            string objectSql;
            VmTemporary? objectStorage = null;
            if (context is null)
            {
                objectSql = _names.Allocate("_object");
                _sql.Line($"DECLARE {objectSql} INT;");
            }
            else
            {
                objectStorage = AllocateVmTemporary(
                    new IrType(heapType.Name, IsReference: true),
                    context);
                objectSql = ReadVmTemporary(objectStorage);
            }
            _sql.Line($"INSERT INTO {HeapObjects} ({HeapInsertColumns("__type_id")}) VALUES ({HeapInsertValues($"{heapType.Id}")});");
            if (objectStorage is null)
                _sql.Line($"SET {objectSql} = CONVERT(INT, SCOPE_IDENTITY());");
            else
                StoreVmTemporary(objectStorage, "CONVERT(INT, SCOPE_IDENTITY())");
            foreach (var allocatedType in HeapHierarchyBaseFirst(heapType))
            {
                var columns = new List<string> { "__object_id" };
                var values = new List<string> { objectSql };
                foreach (var field in allocatedType.Fields.Values)
                {
                    columns.Add(field.SqlName);
                    values.Add(DefaultSql(field.Type));
                }
                _sql.Line($"INSERT INTO {allocatedType.TableName} ({HeapInsertColumns(string.Join(", ", columns))}) VALUES ({HeapInsertValues(string.Join(", ", values))});");
            }

            var objectScope = scope.Child();
            objectScope.Add("this", new ScalarVariableBinding(
                objectSql,
                new IrType(heapType.Name, IsReference: true)));
            ExecuteConstructor(heapType, constructor, capturedArguments, objectScope, objectSql, [],
                () => EvaluateObjectInitializer(0));

            void EvaluateObjectInitializer(int index)
            {
                if (index == initializers.Count)
                {
                    continuation(objectSql);
                    return;
                }

                var initializer = initializers[index];
                if (!TryResolveHeapField(
                        new IrType(heapType.Name, IsReference: true),
                        initializer.Name,
                        IrMemberId.None,
                        out var declaringType,
                        out var field))
                {
                    AddDiagnostic(
                        "SS6004",
                        $"Unknown member '{initializer.Name}' on '{heapType.Name}'.",
                        initializer.Value.Source);
                    EvaluateObjectInitializer(index + 1);
                    return;
                }

                EmitVmExpression(initializer.Value, scope, context, value =>
                {
                    _sql.Line($"UPDATE {declaringType.TableName} SET {field.SqlName} = {value} WHERE {HeapExecutionFilter()}__object_id = {objectSql};");
                    EvaluateObjectInitializer(index + 1);
                });
            }
        }

        void ExecuteConstructor(
            HeapType declaringType,
            HeapConstructor current,
            IReadOnlyList<string> arguments,
            VariableScope objectScope,
            string objectSql,
            HashSet<IrConstructorId> active,
            Action completed)
        {
            if (!current.Id.IsNone && !active.Add(current.Id))
            {
                AddDiagnostic("SS6006", $"Constructor cycle detected on '{declaringType.Name}'.", creation.Source);
                completed();
                return;
            }

            var typeScope = objectScope.Child();
            typeScope.Add("this", new ScalarVariableBinding(
                objectSql,
                new IrType(declaringType.Name, IsReference: true)));
            var constructorScope = typeScope.Child();
            for (var index = 0; index < current.Parameters.Count; index++)
            {
                var parameter = current.Parameters[index];
                var parameterSql = _names.Allocate($"_ctor_{declaringType.Name}_{parameter.Name}");
                var value = index < arguments.Count ? arguments[index] : DefaultSql(parameter.Type);
                _sql.Line($"DECLARE {parameterSql} {parameter.Type.SqlType()} = {value};");
                constructorScope.Add(parameter.Symbol, new ScalarVariableBinding(parameterSql, parameter.Type));
            }

            if (current.InitializerKind == IrConstructorInitializerKind.This)
            {
                var target = declaringType.Constructors.FirstOrDefault(candidate =>
                    candidate.Id == current.InitializerConstructorId);
                if (target is null)
                {
                    AddDiagnostic("SS6006", $"The chained constructor for '{declaringType.Name}' could not be resolved.", creation.Source);
                    EmitBody();
                    return;
                }
                var initializerArguments = new List<string>();
                EvaluateChainedArgument(0);

                void EvaluateChainedArgument(int index)
                {
                    if (index == current.InitializerArguments.Count)
                    {
                        ExecuteConstructor(declaringType, target, initializerArguments, objectScope, objectSql, active, EmitBody);
                        return;
                    }
                    var argument = current.InitializerArguments[index];
                    var parameterType = index < target.Parameters.Count
                        ? target.Parameters[index].Type
                        : argument.Type;
                    EmitVmExpression(argument, constructorScope, context, value =>
                    {
                        initializerArguments.Add(CaptureHeapValue(argument, parameterType, value, context));
                        EvaluateChainedArgument(index + 1);
                    });
                }
                return;
            }

            var baseType = BaseHeapType(declaringType);
            if (declaringType.BaseType is not null && baseType is null)
            {
                AddDiagnostic(
                    "SS6006",
                    $"The base type '{declaringType.BaseType.Name}' for '{declaringType.Name}' is not available in the heap model.",
                    creation.Source);
            }
            if (baseType is null)
            {
                EvaluateInstanceInitializer(0);
                return;
            }

            var baseConstructor = current.InitializerKind == IrConstructorInitializerKind.Base &&
                                  !current.InitializerConstructorId.IsNone
                ? baseType.Constructors.FirstOrDefault(candidate =>
                    candidate.Id == current.InitializerConstructorId)
                : baseType.Constructors.FirstOrDefault(candidate =>
                    (candidate.Parameters.Count > 0 ? candidate.Parameters.Count : candidate.TargetFields.Count) ==
                    (current.InitializerKind == IrConstructorInitializerKind.Base
                        ? current.InitializerArguments.Count
                        : 0));
            if (baseConstructor is null)
            {
                AddDiagnostic(
                    "SS6006",
                    $"The base constructor for '{declaringType.Name}' could not be resolved on '{baseType.Name}'.",
                    creation.Source);
                EvaluateInstanceInitializer(0);
                return;
            }

            var baseArguments = new List<string>();
            EvaluateBaseArgument(0);

            void EvaluateBaseArgument(int index)
            {
                if (index == current.InitializerArguments.Count)
                {
                    ExecuteConstructor(
                        baseType,
                        baseConstructor,
                        baseArguments,
                        objectScope,
                        objectSql,
                        active,
                        () => EvaluateInstanceInitializer(0));
                    return;
                }
                var argument = current.InitializerArguments[index];
                var parameterType = index < baseConstructor.Parameters.Count
                    ? baseConstructor.Parameters[index].Type
                    : argument.Type;
                EmitVmExpression(argument, constructorScope, context, value =>
                {
                    baseArguments.Add(CaptureHeapValue(argument, parameterType, value, context));
                    EvaluateBaseArgument(index + 1);
                });
            }

            void EvaluateInstanceInitializer(int index)
            {
                var fields = declaringType.Fields.Values
                    .Where(field => !field.IsStatic && field.Initializer is not null)
                    .OrderBy(field => field.Source.Span.Start)
                    .ToArray();
                if (index == fields.Length)
                {
                    EmitBody();
                    return;
                }

                var field = fields[index];
                EmitVmExpression(field.Initializer!, typeScope, context, value =>
                {
                    _sql.Line($"UPDATE {declaringType.TableName} SET {field.SqlName} = {value} WHERE {HeapExecutionFilter()}__object_id = {objectSql};");
                    EvaluateInstanceInitializer(index + 1);
                });
            }

            void EmitBody()
            {
                if (current.Body is null)
                {
                    EmitMappedAssignments();
                    Finish();
                    return;
                }

                var endLabel = _names.AllocateLabel($"ctor_{declaringType.Name}_end");
                var previousContext = _proceduralVmContext;
                _proceduralVmContext = context;
                try
                {
                    EmitProceduralStatementSequence(
                        current.Body.Statements,
                        constructorScope,
                        new InlineReturn(null, endLabel),
                        loop: null,
                        namePrefix: $"_ctor_{declaringType.Name}_{++_inlineId}");
                }
                finally
                {
                    _proceduralVmContext = previousContext;
                }
                EmitLabel(endLabel);
                Finish();
            }

            void EmitMappedAssignments()
            {
                for (var index = 0; index < current.TargetFields.Count && index < arguments.Count; index++)
                {
                    var name = current.TargetFields[index];
                    if (string.IsNullOrEmpty(name) || !declaringType.Fields.TryGetValue(name, out var field))
                        continue;
                    _sql.Line($"UPDATE {declaringType.TableName} SET {field.SqlName} = {arguments[index]} WHERE {HeapExecutionFilter()}__object_id = {objectSql};");
                }
            }

            void Finish()
            {
                if (!current.Id.IsNone)
                    active.Remove(current.Id);
                completed();
            }
        }
    }

    private void EmitNewList(
        IrObjectCreationExpression creation,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (creation.Arguments.Count != 0)
        {
            AddDiagnostic("SS6102", "List construction currently expects no arguments.", creation.Source);
            continuation("NULL");
            return;
        }

        var elementType = GenericArguments(creation.CreatedType.Name)[0];
        var captured = new List<string>();
        EvaluateItem(0);

        void EvaluateItem(int index)
        {
            if (index == creation.Initializers.Count)
            {
                var list = AllocateHeapHeader(ListHeapTypeId, "__count", "0");
                InsertIndexedItems(list, elementType, captured);
                if (captured.Count > 0)
                    _sql.Line($"UPDATE {HeapObjects} SET __count = {captured.Count} WHERE {HeapExecutionFilter()}__id = {list};");
                continuation(list);
                return;
            }

            var item = creation.Initializers[index];
            EmitVmExpression(item, scope, context, value =>
            {
                captured.Add(item is IrObjectCreationExpression
                    ? value
                    : CaptureHeapValue(item, elementType, value, context));
                EvaluateItem(index + 1);
            });
        }
    }

    private void EmitRecordWith(
        IrWithExpression expression,
        HeapType heapType,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var initializers = expression.Initializers
            .Select(initializer =>
            {
                var name = AssignmentMemberName(initializer.Target);
                var memberId = initializer.Target switch
                {
                    IrMemberExpression member => member.MemberId,
                    IrVariableExpression variable => variable.Symbol.ReferencedMemberId,
                    _ => IrMemberId.None
                };
                HeapType? declaringType = null;
                HeapField? field = null;
                if (name is not null)
                    TryResolveHeapField(
                        new IrType(heapType.Name, IsReference: true),
                        name,
                        memberId,
                        out declaringType,
                        out field);
                return (Name: name, DeclaringType: declaringType, Field: field, initializer.Value);
            })
            .ToArray();
        var invalid = initializers.FirstOrDefault(initializer => initializer.Field is null);
        if (invalid != default)
        {
            AddDiagnostic(
                "SS6004",
                $"Unknown member '{invalid.Name}' on '{heapType.Name}'.",
                invalid.Value.Source);
            continuation("NULL");
            return;
        }

        var hierarchy = HeapHierarchyBaseFirst(heapType);
        var fields = hierarchy
            .SelectMany(type => type.Fields.Values.Select(field => (DeclaringType: type, Field: field)))
            .ToArray();
        EmitVmExpression(expression.Receiver, scope, context, receiver =>
        {
            var receiverStorage = AllocateVmTemporary(expression.Receiver.Type, context);
            StoreVmTemporary(receiverStorage, receiver);
            var savedReceiver = ReadVmTemporary(receiverStorage);
            var assignments = new List<(HeapType DeclaringType, HeapField Field, string ValueSql)>();
            CaptureField(0);

            void CaptureField(int index)
            {
                if (index == fields.Length)
                {
                    EvaluateInitializer(0);
                    return;
                }
                var (declaringType, field) = fields[index];
                if (!initializers.Any(initializer =>
                        ReferenceEquals(initializer.DeclaringType, declaringType) &&
                        ReferenceEquals(initializer.Field, field)))
                {
                    var storage = AllocateVmTemporary(field.Type, context);
                    StoreVmTemporary(
                        storage,
                        $"(SELECT {field.SqlName} FROM {declaringType.TableName} WHERE {HeapExecutionFilter()}__object_id = {savedReceiver})");
                    assignments.Add((declaringType, field, ReadVmTemporary(storage)));
                }
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
                var declaringType = initializer.DeclaringType!;
                var field = initializer.Field!;
                EmitVmExpression(initializer.Value, scope, context, value =>
                {
                    assignments.Add((
                        declaringType,
                        field,
                        CaptureHeapValue(initializer.Value, field.Type, value, context)));
                    EvaluateInitializer(index + 1);
                });
            }

            void AllocateClone()
            {
                var objectSql = _names.Allocate("_object");
                _sql.Line($"DECLARE {objectSql} INT;");
                _sql.Line($"INSERT INTO {HeapObjects} ({HeapInsertColumns("__type_id")}) VALUES ({HeapInsertValues($"{heapType.Id}")});");
                _sql.Line($"SET {objectSql} = CONVERT(INT, SCOPE_IDENTITY());");
                foreach (var allocatedType in hierarchy)
                {
                    var columns = new[] { "__object_id" }
                        .Concat(allocatedType.Fields.Values.Select(field => field.SqlName));
                    var values = new[] { objectSql }.Concat(
                        allocatedType.Fields.Values.Select(field =>
                            assignments.Single(item =>
                                ReferenceEquals(item.DeclaringType, allocatedType) &&
                                ReferenceEquals(item.Field, field)).ValueSql));
                    _sql.Line($"INSERT INTO {allocatedType.TableName} ({HeapInsertColumns(string.Join(", ", columns))}) VALUES ({HeapInsertValues(string.Join(", ", values))});");
                }
                continuation(objectSql);
            }
        });
    }

    private void EmitNewDictionary(
        IrObjectCreationExpression creation,
        Action<string> continuation)
    {
        if (creation.Arguments.Count != 0)
            AddDiagnostic("SS6202", "Dictionary construction currently expects no arguments.", creation.Source);
        if (creation.Initializers.Count > 0)
            AddDiagnostic("SS6005", "Dictionary collection initializers are not supported yet; use Add calls.", creation.Source);
        continuation(AllocateHeapHeader(DictionaryHeapTypeId, "__count", "0"));
    }

    private static string? AssignmentMemberName(IrExpression expression) => expression switch
    {
        IrMemberExpression member => member.MemberName,
        IrVariableExpression variable => variable.Symbol.Name,
        _ => null
    };

    private string CaptureHeapValue(
        IrExpression expression,
        IrType type,
        string value,
        VmMethod? context)
    {
        if (expression.Facts.HasConstantValue)
            return value;
        var storage = AllocateVmTemporary(type, context);
        StoreVmTemporary(storage, value);
        return ReadVmTemporary(storage);
    }

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
            _sql.Line($"INSERT INTO {HeapObjects} ({HeapInsertColumns("__type_id")}) VALUES ({HeapInsertValues($"{heapType.Id}")});");
            _sql.Line($"SET {objectSql} = CONVERT(INT, SCOPE_IDENTITY());");
            var columns = new List<string> { "__object_id" };
            var values = new List<string> { objectSql };
            foreach (var field in heapType.Fields.Values)
            {
                columns.Add(field.SqlName);
                var assigned = assignments.LastOrDefault(item => item.Field.Name == field.Name);
                values.Add(assigned is null ? DefaultSql(field.Type) : assigned.ValueSql);
            }
            _sql.Line($"INSERT INTO {heapType.TableName} ({HeapInsertColumns(string.Join(", ", columns))}) VALUES ({HeapInsertValues(string.Join(", ", values))});");
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
                    $"(SELECT {field.SqlName} FROM {heapType.TableName} WHERE {HeapExecutionFilter()}__object_id = {savedReceiver})");
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
                _sql.Line($"INSERT INTO {HeapObjects} ({HeapInsertColumns("__type_id")}) VALUES ({HeapInsertValues($"{heapType.Id}")});");
                _sql.Line($"SET {objectSql} = CONVERT(INT, SCOPE_IDENTITY());");
                var columns = new List<string> { "__object_id" };
                var values = new List<string> { objectSql };
                foreach (var field in heapType.Fields.Values)
                {
                    columns.Add(field.SqlName);
                    values.Add(assignments.Single(item => item.Field.Name == field.Name).ValueSql);
                }
                _sql.Line($"INSERT INTO {heapType.TableName} ({HeapInsertColumns(string.Join(", ", columns))}) VALUES ({HeapInsertValues(string.Join(", ", values))});");
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
            InsertIndexedItems(
                listSql,
                elementType,
                captured);
            if (captured.Count > 0)
                _sql.Line($"UPDATE {HeapObjects} SET __count = {captured.Count} WHERE {HeapExecutionFilter()}__id = {listSql};");
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
        _sql.Line($"INSERT INTO {HeapObjects} ({HeapInsertColumns(string.Join(", ", columns))}) VALUES ({HeapInsertValues(string.Join(", ", values))});");
        _sql.Line($"SET {objectSql} = CONVERT(INT, SCOPE_IDENTITY());");
        return objectSql;
    }

}

