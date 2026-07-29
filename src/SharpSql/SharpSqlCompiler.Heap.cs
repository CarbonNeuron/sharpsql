using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private readonly Dictionary<string, HeapType> _heapTypes = new(StringComparer.Ordinal);
    private bool _heapRuntimeNeeded;
    private bool _usesLists;
    private bool _usesDictionaries;
    private int _nextHeapTypeId;

    private const string HeapObjects = "#__sharpsql_objects";
    private const string HeapIndexedItems = "#__sharpsql_indexed_items";
    private const string HeapDictionaryEntries = "#__sharpsql_dictionary_entries";

    private void PrepareHeapRuntime(CompilationUnitSyntax root)
    {
        foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (declaration is not (ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax))
                continue;
            AddHeapType(declaration);
        }

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var name = NormalizeTypeName(creation.Type.ToString());
            if (IsListType(name))
                _usesLists = true;
            else if (IsDictionaryType(name))
                _usesDictionaries = true;
            else if (IsRandomType(name))
                _usesRandom = true;
            else if (!_heapTypes.ContainsKey(name))
                continue;
            _heapRuntimeNeeded = true;
        }

        if (root.DescendantNodes().OfType<ArrayCreationExpressionSyntax>()
            .Any(creation => creation.Type.ElementType.ToString() != "byte"))
        {
            _usesLists = true;
            _heapRuntimeNeeded = true;
        }

        if (root.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsLinqMaterialization))
        {
            _usesLists = true;
            _heapRuntimeNeeded = true;
        }
    }

    private void AddHeapType(TypeDeclarationSyntax declaration)
    {
        var name = declaration.Identifier.ValueText;
        if (_heapTypes.ContainsKey(name))
        {
            AddDiagnostic("SS6001", $"Duplicate heap type '{name}' is not supported.", declaration);
            return;
        }

        var heapType = new HeapType(
            name,
            ++_nextHeapTypeId,
            $"#__sharpsql_type_{_nextHeapTypeId}",
            declaration is StructDeclarationSyntax,
            declaration);

        if (declaration is RecordDeclarationSyntax { ParameterList: not null } record)
        {
            var targets = new List<string>();
            foreach (var parameter in record.ParameterList.Parameters)
            {
                var fieldName = parameter.Identifier.ValueText;
                var fieldType = parameter.Type is null ? IrType.Unknown : CSharpTypeFactory.From(parameter.Type);
                AddHeapField(heapType, fieldName, fieldType, parameter);
                targets.Add(fieldName);
            }
            heapType.Constructors.Add(new HeapConstructor(targets));
        }

        foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
            AddHeapField(heapType, property.Identifier.ValueText, CSharpTypeFactory.From(property.Type), property);

        foreach (var field in declaration.Members.OfType<FieldDeclarationSyntax>())
            foreach (var variable in field.Declaration.Variables)
                AddHeapField(heapType, variable.Identifier.ValueText, CSharpTypeFactory.From(field.Declaration.Type), variable);

        foreach (var constructor in declaration.Members.OfType<ConstructorDeclarationSyntax>())
        {
            var targets = new List<string>();
            foreach (var parameter in constructor.ParameterList.Parameters)
            {
                var parameterName = parameter.Identifier.ValueText;
                var assignment = constructor.Body?.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .FirstOrDefault(candidate =>
                        candidate.Right is IdentifierNameSyntax identifier &&
                        identifier.Identifier.ValueText == parameterName);
                var targetName = assignment is null ? null : AssignmentMemberName(assignment.Left);
                targetName ??= heapType.Fields.Values
                    .FirstOrDefault(field => string.Equals(field.Name, parameterName, StringComparison.OrdinalIgnoreCase))?.Name;
                targets.Add(targetName ?? string.Empty);
            }
            heapType.Constructors.Add(new HeapConstructor(targets));

            var supportedAssignments = constructor.Body?.Statements
                .All(statement => statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax }) ?? true;
            if (!supportedAssignments)
                AddDiagnostic("SS6002", $"Constructor '{name}' contains behavior beyond field assignment.", constructor);
        }

        if (heapType.Constructors.Count == 0)
            heapType.Constructors.Add(new HeapConstructor([]));
        _heapTypes.Add(name, heapType);
    }

    private void AddHeapField(HeapType type, string name, IrType fieldType, SyntaxNode node)
    {
        if (type.Fields.ContainsKey(name))
            return; // Positional record properties also appear as declared members in some syntax forms.
        type.Fields.Add(name, new HeapField(name, fieldType, QuoteIdentifier(name), node));
    }

    private void EmitHeapPreamble()
    {
        if (!_heapRuntimeNeeded)
            return;

        _sql.Line("-- SharpSql ephemeral managed heap");
        foreach (var type in _heapTypes.Values.Reverse())
            _sql.Line($"DROP TABLE IF EXISTS {type.TableName};");
        if (_usesDictionaries)
            _sql.Line($"DROP TABLE IF EXISTS {HeapDictionaryEntries};");
        if (_usesLists || _usesRandom)
            _sql.Line($"DROP TABLE IF EXISTS {HeapIndexedItems};");
        _sql.Line($"DROP TABLE IF EXISTS {HeapObjects};");

        _sql.Line($"CREATE TABLE {HeapObjects} (");
        using (_sql.Indent())
        {
            _sql.Line("__id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,");
            _sql.Line("__type_id INT NOT NULL,");
            _sql.Line("__count INT NULL,");
            _sql.Line("__random_inext INT NULL,");
            _sql.Line("__random_inextp INT NULL");
        }
        _sql.Line(");");

        foreach (var type in _heapTypes.Values)
        {
            EmitLeadingComments(type.Syntax);
            _sql.Line($"CREATE TABLE {type.TableName} (");
            using (_sql.Indent())
            {
                _sql.Line("__object_id BIGINT NOT NULL PRIMARY KEY" + (type.Fields.Count == 0 ? string.Empty : ","));
                var fields = type.Fields.Values.ToArray();
                for (var index = 0; index < fields.Length; index++)
                {
                    EmitLeadingComments(fields[index].Syntax);
                    _sql.Line($"{fields[index].SqlName} {fields[index].Type.SqlType()} NULL{(index + 1 == fields.Length ? string.Empty : ",")}");
                }
            }
            _sql.Line(");");
        }

        if (_usesLists || _usesRandom)
            EmitIndexedItemsTable();
        if (_usesDictionaries)
            EmitDictionaryTables();
        _sql.Line();
    }

    private void EmitIndexedItemsTable()
    {
        _sql.Line($"CREATE TABLE {HeapIndexedItems} (");
        using (_sql.Indent())
        {
            _sql.Line("__owner_id BIGINT NOT NULL,");
            _sql.Line("__index INT NOT NULL,");
            _sql.Line("__value SQL_VARIANT NULL,");
            _sql.Line("__text_value NVARCHAR(MAX) NULL,");
            _sql.Line("__binary_value VARBINARY(MAX) NULL,");
            _sql.Line("__reference_value BIGINT NULL,");
            _sql.Line("PRIMARY KEY (__owner_id, __index)");
        }
        _sql.Line(");");
    }

    private void EmitDictionaryTables()
    {
        _sql.Line($"CREATE TABLE {HeapDictionaryEntries} (");
        using (_sql.Indent())
        {
            _sql.Line("__id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,");
            _sql.Line("__dictionary_id BIGINT NOT NULL,");
            _sql.Line("__key SQL_VARIANT NULL,");
            _sql.Line("__key_text NVARCHAR(MAX) NULL,");
            _sql.Line("__key_binary VARBINARY(MAX) NULL,");
            _sql.Line("__key_reference BIGINT NULL,");
            _sql.Line("__value SQL_VARIANT NULL,");
            _sql.Line("__text_value NVARCHAR(MAX) NULL,");
            _sql.Line("__binary_value VARBINARY(MAX) NULL,");
            _sql.Line("__reference_value BIGINT NULL");
        }
        _sql.Line(");");
    }

    private void EmitHeapEpilogue()
    {
        if (!_heapRuntimeNeeded)
            return;

        foreach (var type in _heapTypes.Values.Reverse())
            _sql.Line($"DROP TABLE IF EXISTS {type.TableName};");
        if (_usesDictionaries)
            _sql.Line($"DROP TABLE IF EXISTS {HeapDictionaryEntries};");
        if (_usesLists || _usesRandom)
            _sql.Line($"DROP TABLE IF EXISTS {HeapIndexedItems};");
        _sql.Line($"DROP TABLE IF EXISTS {HeapObjects};");
    }

    private bool ContainsRuntimeExpression(ExpressionSyntax expression) =>
        ContainsVmCall(expression) || ContainsHeapEffect(expression) || ContainsGuardedLinqExpression(expression);

    private bool ContainsHeapEffect(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>().Any(IsHeapCreation) ||
        expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(IsRandomInvocation) ||
        expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(IsLinqMaterialization) ||
        expression.DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>().Any() ||
        expression.DescendantNodesAndSelf().OfType<ArrayCreationExpressionSyntax>()
            .Any(creation => creation.Type.ElementType.ToString() != "byte");

    private bool IsHeapCreation(ObjectCreationExpressionSyntax creation)
    {
        var name = NormalizeTypeName(creation.Type.ToString());
        return _heapTypes.ContainsKey(name) || IsListType(name) || IsDictionaryType(name) || IsRandomType(name);
    }

    private bool TryEmitHeapExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (expression is ElementAccessExpressionSyntax element &&
            TryEmitHeapElementExpression(element, scope, context, continuation))
            return true;

        if (TryEmitLinqMaterialization(expression, scope, context, continuation))
            return true;

        if (TryEmitRandomExpression(expression, scope, context, continuation))
            return true;

        if (expression is ArrayCreationExpressionSyntax arrayCreation &&
            arrayCreation.Type.ElementType.ToString() != "byte")
        {
            EmitNewArray(arrayCreation, scope, context, continuation);
            return true;
        }

        if (expression is not ObjectCreationExpressionSyntax creation || !IsHeapCreation(creation))
            return false;

        var typeName = NormalizeTypeName(creation.Type.ToString());
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
            var captured = new List<VmTemporary>();
            EvaluateItem(0);

            void EvaluateItem(int index)
            {
                if (index == creation.Initializer.Expressions.Count)
                {
                    var array = AllocateHeapHeader(1003, "__count", captured.Count.ToString());
                    InsertListItems(
                        array,
                        elementType,
                        captured.Select(ReadVmTemporary).ToArray());
                    continuation(array);
                    return;
                }
                EmitVmExpression(creation.Initializer.Expressions[index], scope, context, value =>
                {
                    var storage = AllocateVmTemporary(elementType, context);
                    StoreVmTemporary(storage, value);
                    captured.Add(storage);
                    EvaluateItem(index + 1);
                });
            }
            return;
        }

        var sizeExpression = creation.Type.RankSpecifiers[0].Sizes[0];
        EmitVmExpression(sizeExpression, scope, context, size =>
        {
            var sizeTemporary = _names.Allocate("_array_size");
            var indexTemporary = _names.Allocate("_array_index");
            _sql.Line($"DECLARE {sizeTemporary} INT = {size};");
            var array = AllocateHeapHeader(1003, "__count", sizeTemporary);
            _sql.Line($"DECLARE {indexTemporary} INT = 0;");
            _sql.Line($"WHILE {indexTemporary} < {sizeTemporary}");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                InsertListItem(array, indexTemporary, elementType, DefaultSql(elementType));
                _sql.Line($"SET {indexTemporary} = {indexTemporary} + 1;");
            }
            _sql.Line("END;");
            continuation(array);
        });
    }

    private void EmitNewObject(
        ObjectCreationExpressionSyntax creation,
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
                var storage = AllocateVmTemporary(field.Type, context);
                StoreVmTemporary(storage, value);
                assignments.Add(new HeapValueAssignment(field, storage));
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
                var storage = AllocateVmTemporary(field.Type, context);
                StoreVmTemporary(storage, value);
                assignments.RemoveAll(item => item.Field.Name == field.Name);
                assignments.Add(new HeapValueAssignment(field, storage));
                EvaluateInitializers(index + 1, initializers);
            });
        }

        void Allocate()
        {
            var objectSql = _names.Allocate("_object");
            _sql.Line($"DECLARE {objectSql} BIGINT;");
            _sql.Line($"INSERT INTO {HeapObjects} (__type_id) VALUES ({heapType.Id});");
            _sql.Line($"SET {objectSql} = CONVERT(BIGINT, SCOPE_IDENTITY());");
            var columns = new List<string> { "__object_id" };
            var values = new List<string> { objectSql };
            foreach (var field in heapType.Fields.Values)
            {
                columns.Add(field.SqlName);
                var assigned = assignments.LastOrDefault(item => item.Field.Name == field.Name);
                values.Add(assigned is null ? DefaultSql(field.Type) : ReadVmTemporary(assigned.Value));
            }
            _sql.Line($"INSERT INTO {heapType.TableName} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)});");
            continuation(objectSql);
        }
    }

    private void EmitNewList(
        ObjectCreationExpressionSyntax creation,
        string typeName,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var elementType = GenericArguments(typeName)[0];
        var items = creation.Initializer?.Expressions ?? default;
        var captured = new List<VmTemporary>();
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
                var storage = AllocateVmTemporary(elementType, context);
                StoreVmTemporary(storage, value);
                captured.Add(storage);
                EvaluateItem(index + 1);
            });
        }

        void Allocate()
        {
            var listSql = AllocateHeapHeader(1001, "__count", "0");
            InsertListItems(
                listSql,
                elementType,
                captured.Select(ReadVmTemporary).ToArray());
            if (captured.Count > 0)
                _sql.Line($"UPDATE {HeapObjects} SET __count = {captured.Count} WHERE __id = {listSql};");
            continuation(listSql);
        }
    }

    private void EmitNewDictionary(
        ObjectCreationExpressionSyntax creation,
        string typeName,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (creation.Initializer is { Expressions.Count: > 0 })
            AddDiagnostic("SS6005", "Dictionary collection initializers are not supported yet; use Add calls.", creation.Initializer);
        continuation(AllocateHeapHeader(1002, "__count", "0"));
    }

    private string AllocateHeapHeader(int typeId, string column, string value)
    {
        var objectSql = _names.Allocate("_object");
        _sql.Line($"DECLARE {objectSql} BIGINT;");
        _sql.Line($"INSERT INTO {HeapObjects} (__type_id, {column}) VALUES ({typeId}, {value});");
        _sql.Line($"SET {objectSql} = CONVERT(BIGINT, SCOPE_IDENTITY());");
        return objectSql;
    }

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
                    _sql.Line($"DELETE FROM {HeapIndexedItems} WHERE __owner_id = {receiver};");
                    _sql.Line($"UPDATE {HeapObjects} SET __count = 0 WHERE __id = {receiver};");
                    return true;
                }
                if (IsDictionaryType(receiverType.Name))
                {
                    _sql.Line($"DELETE FROM {HeapDictionaryEntries} WHERE __dictionary_id = {receiver};");
                    _sql.Line($"UPDATE {HeapObjects} SET __count = 0 WHERE __id = {receiver};");
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
                    _sql.Line($"DELETE FROM {HeapIndexedItems} WHERE __owner_id = {receiver} AND __index = {index};");
                    _sql.Line($"UPDATE {HeapIndexedItems} SET __index = __index - 1 WHERE __owner_id = {receiver} AND __index > {index};");
                    _sql.Line($"UPDATE {HeapObjects} SET __count = __count - 1 WHERE __id = {receiver};");
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
                    _sql.Line($"IF EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE __dictionary_id = {receiver} AND {predicate})");
                    _sql.Line("BEGIN");
                    using (_sql.Indent())
                    {
                        _sql.Line($"DELETE FROM {HeapDictionaryEntries} WHERE __dictionary_id = {receiver} AND {predicate};");
                        _sql.Line($"UPDATE {HeapObjects} SET __count = __count - 1 WHERE __id = {receiver};");
                    }
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
                var currentValue = $"(SELECT {field.SqlName} FROM {heapType.TableName} WHERE __object_id = {receiver})";
                EmitVmExpression(assignment.Right, scope, context, value =>
                    _sql.Line($"UPDATE {heapType.TableName} SET {field.SqlName} = {HeapAssignmentValue(assignment, field.Type, currentValue, value)} WHERE __object_id = {receiver};"));
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
                var currentValue = $"(SELECT {implicitMember.SqlName} FROM {implicitType.TableName} WHERE __object_id = {implicitReceiver})";
                EmitVmExpression(assignment.Right, scope, context, value =>
                    _sql.Line($"UPDATE {implicitType.TableName} SET {implicitMember.SqlName} = {HeapAssignmentValue(assignment, implicitMember.Type, currentValue, value)} WHERE __object_id = {implicitReceiver};"));
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
            var index = $"(SELECT __count FROM {HeapObjects} WHERE __id = {list})";
            InsertListItem(list, index, elementType, value);
            _sql.Line($"UPDATE {HeapObjects} SET __count = __count + 1 WHERE __id = {list};");
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
                _sql.Line($"UPDATE {HeapIndexedItems} SET {CollectionValueColumn(elementType, false)} = {CollectionStoredValue(elementType, value)} WHERE __owner_id = {list} AND __index = {index};");
            }));
    }

    private void InsertListItem(string list, string index, IrType type, string value) =>
        _sql.Line($"INSERT INTO {HeapIndexedItems} (__owner_id, __index, {CollectionValueColumn(type, false)}) VALUES ({list}, {index}, {CollectionStoredValue(type, value)});");

    private void InsertListItems(string list, IrType type, IReadOnlyList<string> values)
    {
        const int maximumRowsPerValuesClause = 1000;
        var column = CollectionValueColumn(type, key: false);
        for (var start = 0; start < values.Count; start += maximumRowsPerValuesClause)
        {
            var count = Math.Min(maximumRowsPerValuesClause, values.Count - start);
            _sql.Line($"INSERT INTO {HeapIndexedItems} (__owner_id, __index, {column}) VALUES");
            using (_sql.Indent())
            {
                for (var offset = 0; offset < count; offset++)
                {
                    var index = start + offset;
                    var terminator = offset + 1 == count ? ";" : ",";
                    _sql.Line($"({list}, {index}, {CollectionStoredValue(type, values[index])}){terminator}");
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
                _sql.Line($"IF EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE __dictionary_id = {dictionary} AND {DictionaryKeyPredicate(types[0], savedKey)}) THROW 51001, 'Duplicate dictionary key.', 1;");
                InsertDictionaryEntry(dictionary, types[0], savedKey, types[1], value);
                _sql.Line($"UPDATE {HeapObjects} SET __count = __count + 1 WHERE __id = {dictionary};");
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
                _sql.Line($"IF EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE __dictionary_id = {dictionary} AND {predicate})");
                _sql.Line($"    UPDATE {HeapDictionaryEntries} SET {CollectionValueColumn(types[1], false)} = {CollectionStoredValue(types[1], value)} WHERE __dictionary_id = {dictionary} AND {predicate};");
                _sql.Line("ELSE");
                _sql.Line("BEGIN");
                using (_sql.Indent())
                {
                    InsertDictionaryEntry(dictionary, types[0], savedKey, types[1], value);
                    _sql.Line($"UPDATE {HeapObjects} SET __count = __count + 1 WHERE __id = {dictionary};");
                }
                _sql.Line("END;");
            });
        });
    }

    private void InsertDictionaryEntry(string dictionary, IrType keyType, string key, IrType valueType, string value) =>
        _sql.Line($"INSERT INTO {HeapDictionaryEntries} (__dictionary_id, {CollectionValueColumn(keyType, true)}, {CollectionValueColumn(valueType, false)}) VALUES ({dictionary}, {CollectionStoredValue(keyType, key)}, {CollectionStoredValue(valueType, value)});");

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
                return SqlScalarExpression.Primary($"(SELECT __count FROM {HeapObjects} WHERE __id = {receiver})");
        }
        else if (IsDictionaryType(receiverType.Name))
        {
            if (member.Name.Identifier.ValueText == "Count")
                return SqlScalarExpression.Primary($"(SELECT __count FROM {HeapObjects} WHERE __id = {receiver})");
        }
        else if (TryResolveHeapField(member, scope, substitutions, out var type, out var field))
        {
            return SqlScalarExpression.Primary($"(SELECT {field.SqlName} FROM {type.TableName} WHERE __object_id = {receiver})");
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
                    _sql.Line($"IF NOT EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE __dictionary_id = {receiver} AND {DictionaryKeyPredicate(keyType, key)}) THROW 51010, 'The given key was not present in the dictionary.', 1;");
                }

                if (TryGetHeapElementSql(receiverType, receiver, key, out var value))
                    continuation(value);
                else
                    continuation(UnsupportedExpression(element));
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

    private static bool TryGetHeapElementSql(
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
            value = $"(SELECT {CollectionReadValue(types[1], false)} FROM {HeapDictionaryEntries} WHERE __dictionary_id = {receiver} AND {DictionaryKeyPredicate(types[0], key)})";
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
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE __dictionary_id = {dictionary} AND {DictionaryKeyPredicate(types[0], key)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsListType(receiverType.Name) && member.Name.Identifier.ValueText == "Contains" &&
                invocation.ArgumentList.Arguments.Count == 1)
            {
                var itemType = SequenceElementType(receiverType.Name);
                var list = EmitScalar(member.Expression, scope);
                var value = EmitScalar(invocation.ArgumentList.Arguments[0].Expression, scope);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapIndexedItems} WHERE __owner_id = {list} AND {CollectionValuePredicate(itemType, value, false)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            }
            if (IsDictionaryType(receiverType.Name) && member.Name.Identifier.ValueText == "ContainsValue" &&
                invocation.ArgumentList.Arguments.Count == 1)
            {
                var valueType = GenericArguments(receiverType.Name)[1];
                var dictionary = EmitScalar(member.Expression, scope);
                var value = EmitScalar(invocation.ArgumentList.Arguments[0].Expression, scope);
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM {HeapDictionaryEntries} WHERE __dictionary_id = {dictionary} AND {CollectionValuePredicate(valueType, value, false)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
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
        if (_heapTypes.TryGetValue(receiver.Name, out var heapType) &&
            heapType.Fields.TryGetValue(member.Name.Identifier.ValueText, out var field))
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
        if (_heapTypes.TryGetValue(receiverType.Name, out type!) &&
            type.Fields.TryGetValue(member.Name.Identifier.ValueText, out field!))
            return true;
        type = null!;
        field = null!;
        return false;
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
                $"(SELECT {field.SqlName} FROM {type.TableName} WHERE __object_id = {receiver})");
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
        out string receiver)
    {
        IrType? receiverType = null;
        receiver = string.Empty;
        if (substitutions is not null && substitutions.TryGetValue("this", out var replacement))
        {
            receiverType = replacement.Type;
            receiver = replacement.Expression.Sql;
        }
        else if (scope.Find("this") is { } binding)
        {
            receiverType = binding.Type;
            receiver = binding.SqlName;
        }

        if (receiverType is not null &&
            _heapTypes.TryGetValue(receiverType.Name, out type!) &&
            type.Fields.TryGetValue(name, out field!))
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
        var column = CollectionReadValue(type, true);
        if (type.IsString)
            return $"{column} COLLATE Latin1_General_100_BIN2 = {value} COLLATE Latin1_General_100_BIN2";
        return $"{column} = {value}";
    }

    private static string CollectionValuePredicate(IrType type, string value, bool key)
    {
        var column = CollectionReadValue(type, key);
        if (type.IsString)
            return $"{column} COLLATE Latin1_General_100_BIN2 = {value} COLLATE Latin1_General_100_BIN2";
        return $"{column} = {value}";
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
        name.StartsWith("List<", StringComparison.Ordinal) ||
        name.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal);

    private static bool IsArrayType(string name) => name.EndsWith("[]", StringComparison.Ordinal) && name != "byte[]";

    private static bool IsSequenceType(string name) => IsListType(name) || IsArrayType(name);

    private static IrType SequenceElementType(string name) => IsArrayType(name)
        ? CSharpTypeFactory.From(SyntaxFactory.ParseTypeName(name[..^2]))
        : GenericArguments(name)[0];

    private static string SequenceCountSql(string collection) =>
        $"(SELECT __count FROM {HeapObjects} WHERE __id = {collection})";

    private static string SequenceElementSql(string collection, string index, IrType itemType) =>
        $"(SELECT {CollectionReadValue(itemType, false)} FROM {HeapIndexedItems} WHERE __owner_id = {collection} AND __index = {index})";

    private static bool IsDictionaryType(string name) =>
        name.StartsWith("Dictionary<", StringComparison.Ordinal) ||
        name.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal);

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
        return arguments.Select(argument => CSharpTypeFactory.From(SyntaxFactory.ParseTypeName(argument))).ToArray();
    }

    private static string NormalizeTypeName(string name) =>
        name.Replace("global::", string.Empty, StringComparison.Ordinal)
            .Replace("System.Collections.Generic.", string.Empty, StringComparison.Ordinal);

    private static string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private sealed class HeapType(
        string name,
        int id,
        string tableName,
        bool isValueType,
        TypeDeclarationSyntax syntax)
    {
        public string Name { get; } = name;
        public int Id { get; } = id;
        public string TableName { get; } = tableName;
        public bool IsValueType { get; } = isValueType;
        public TypeDeclarationSyntax Syntax { get; } = syntax;
        public Dictionary<string, HeapField> Fields { get; } = new(StringComparer.Ordinal);
        public List<HeapConstructor> Constructors { get; } = [];
    }

    private sealed record HeapField(string Name, IrType Type, string SqlName, SyntaxNode Syntax);
    private sealed record HeapConstructor(IReadOnlyList<string> TargetFields);
    private sealed record HeapValueAssignment(HeapField Field, VmTemporary Value);
}
