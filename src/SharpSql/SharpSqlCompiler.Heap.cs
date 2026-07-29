using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private readonly Dictionary<string, HeapType> _heapTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedHeapTypes = new(StringComparer.Ordinal);
    private bool _heapRuntimeNeeded;
    private bool _usesIndexedItems;
    private bool _usesDictionaries;
    private bool _usesScalarDictionaryKeys;
    private bool _usesStringOrBinaryDictionaryKeys;
    private bool _usesReferenceDictionaryKeys;
    private int _nextHeapTypeId;
    private int _nextHeapAliasId;
    private string _durableHeapProgramId = string.Empty;

    private const string EphemeralHeapObjects = "#__sharpsql_objects";
    private const string EphemeralHeapIndexedItems = "#__sharpsql_indexed_items";
    private const string EphemeralHeapDictionaryEntries = "#__sharpsql_dictionary_entries";
    private const string DurableHeapObjects = "[SharpSql].[__sharpsql_objects]";
    private const string DurableHeapIndexedItems = "[SharpSql].[__sharpsql_indexed_items]";
    private const string DurableHeapDictionaryEntries = "[SharpSql].[__sharpsql_dictionary_entries]";
    private const int ListHeapTypeId = 1001;
    private const int DictionaryHeapTypeId = 1002;
    private const int ArrayHeapTypeId = 1003;
    private const int RandomHeapTypeId = 1004;

    private bool UsesDurableHeapStorage => UsesDurableRuntime;
    private string HeapObjects => UsesDurableHeapStorage ? DurableHeapObjects : EphemeralHeapObjects;
    private string HeapIndexedItems => UsesDurableHeapStorage ? DurableHeapIndexedItems : EphemeralHeapIndexedItems;
    private string HeapDictionaryEntries => UsesDurableHeapStorage ? DurableHeapDictionaryEntries : EphemeralHeapDictionaryEntries;

    private string HeapExecutionFilter(string? alias = null) => UsesDurableHeapStorage
        ? $"{(alias is null ? string.Empty : alias + ".")}__execution_id = {RuntimeExecutionId} AND "
        : string.Empty;

    private string HeapInsertColumns(string columns) => UsesDurableHeapStorage
        ? $"__execution_id, {columns}"
        : columns;

    private string HeapInsertValues(string values) => UsesDurableHeapStorage
        ? $"{RuntimeExecutionId}, {values}"
        : values;

    private void PrepareHeapRuntime(IrProgram program)
    {
        if (UsesDurableHeapStorage)
            _durableHeapProgramId = ComputeDurableHeapProgramId(program.HeapTypes);

        IEnumerable<IrHeapTypeDefinition> heapDefinitions = UsesDurableHeapStorage
            ? program.HeapTypes.OrderBy(item => item.Name, StringComparer.Ordinal)
            : program.HeapTypes;
        foreach (var definition in heapDefinitions)
            AddHeapType(definition);

        VisitStatement(program.EntryPoint);
        foreach (var method in program.Methods)
        {
            if (method.Body is not null)
                VisitStatement(method.Body);
            if (method.ExpressionBody is not null)
                VisitExpression(method.ExpressionBody);
        }

        var preparedTypes = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var pendingTypes = _usedHeapTypes
                .Where(name => !preparedTypes.Contains(name))
                .Select(name => _heapTypes[name])
                .ToArray();
            if (pendingTypes.Length == 0)
                break;
            foreach (var type in pendingTypes)
            {
                preparedTypes.Add(type.Name);
                foreach (var field in type.Fields.Values)
                    if (!field.IsStatic && field.Initializer is not null)
                        VisitExpression(field.Initializer);
                foreach (var constructor in type.Constructors)
                {
                    foreach (var argument in constructor.InitializerArguments)
                        VisitExpression(argument);
                    if (constructor.Body is not null)
                        VisitStatement(constructor.Body);
                }
            }
        }

        void VisitStatement(ProceduralStatement statement)
        {
            switch (statement)
            {
                case ProceduralBlock block:
                    foreach (var child in block.Statements)
                        VisitStatement(child);
                    break;
                case ProceduralDeclarationStatement declaration:
                    foreach (var variable in declaration.Declaration.Variables)
                        if (variable.Initializer is not null)
                            VisitExpression(variable.Initializer);
                    break;
                case ProceduralExpressionStatement expression:
                    VisitExpression(expression.Expression);
                    break;
                case ProceduralIf @if:
                    VisitExpression(@if.Condition);
                    VisitStatement(@if.Then);
                    if (@if.Else is not null)
                        VisitStatement(@if.Else);
                    break;
                case ProceduralWhile @while:
                    VisitExpression(@while.Condition);
                    VisitStatement(@while.Body);
                    break;
                case ProceduralDo @do:
                    VisitStatement(@do.Body);
                    VisitExpression(@do.Condition);
                    break;
                case ProceduralFor @for:
                    if (@for.Declaration is not null)
                        foreach (var variable in @for.Declaration.Variables)
                            if (variable.Initializer is not null)
                                VisitExpression(variable.Initializer);
                    foreach (var initializer in @for.Initializers)
                        VisitExpression(initializer);
                    if (@for.Condition is not null)
                        VisitExpression(@for.Condition);
                    foreach (var incrementor in @for.Incrementors)
                        VisitExpression(incrementor);
                    VisitStatement(@for.Body);
                    break;
                case ProceduralForEach forEach:
                    VisitExpression(forEach.SourceExpression);
                    VisitStatement(forEach.Body);
                    break;
                case ProceduralTry @try:
                    VisitStatement(@try.Body);
                    foreach (var @catch in @try.Catches)
                    {
                        if (@catch.Filter is not null)
                            VisitExpression(@catch.Filter);
                        VisitStatement(@catch.Body);
                    }
                    break;
                case ProceduralThrow { Expression: not null } @throw:
                    VisitExpression(@throw.Expression);
                    break;
                case ProceduralReturn { Expression: not null } @return:
                    VisitExpression(@return.Expression);
                    break;
            }
        }

        void VisitExpression(IrExpression expression)
        {
            switch (expression)
            {
                case IrArrayCreationExpression array:
                    if (array.ElementType.Name != "byte")
                    {
                        _heapRuntimeNeeded = true;
                        _usesIndexedItems = true;
                    }
                    if (array.Length is not null)
                        VisitExpression(array.Length);
                    foreach (var element in array.Elements)
                        VisitExpression(element);
                    break;
                case IrObjectCreationExpression creation:
                    PrepareObjectCreation(creation.CreatedType);
                    foreach (var argument in creation.Arguments)
                        VisitExpression(argument);
                    foreach (var initializer in creation.Initializers)
                        VisitExpression(initializer);
                    break;
                case IrWithExpression withExpression:
                    PrepareObjectCreation(withExpression.Type);
                    VisitExpression(withExpression.Receiver);
                    foreach (var initializer in withExpression.Initializers)
                        VisitExpression(initializer);
                    break;
                case IrInvocationExpression invocation:
                    if (invocation.Dispatch is IrCallDispatch.Virtual or IrCallDispatch.Interface &&
                        !invocation.TargetMethodId.IsNone)
                        _runtimeDispatchRequests.Add((invocation.TargetMethodId, invocation.Dispatch));
                    if (IntrinsicCatalog.IsMaterializer(invocation.MethodName ?? string.Empty))
                    {
                        _heapRuntimeNeeded = true;
                        _usesIndexedItems = true;
                    }
                    VisitExpression(invocation.Target);
                    foreach (var argument in invocation.Arguments)
                        VisitExpression(argument);
                    break;
                case IrBinaryExpression binary:
                    VisitExpression(binary.Left);
                    VisitExpression(binary.Right);
                    break;
                case IrUnaryExpression unary:
                    VisitExpression(unary.Operand);
                    break;
                case IrConversionExpression conversion:
                    VisitExpression(conversion.Operand);
                    break;
                case IrAwaitExpression awaitExpression:
                    VisitExpression(awaitExpression.Operand);
                    break;
                case IrConditionalExpression conditional:
                    VisitExpression(conditional.Condition);
                    VisitExpression(conditional.WhenTrue);
                    VisitExpression(conditional.WhenFalse);
                    break;
                case IrMemberExpression member:
                    if (_heapTypes.ContainsKey(member.Receiver.Type.Name))
                    {
                        MarkHeapTypeUsed(member.Receiver.Type.Name);
                        _heapRuntimeNeeded = true;
                    }
                    VisitExpression(member.Receiver);
                    break;
                case IrElementExpression element:
                    VisitExpression(element.Receiver);
                    foreach (var argument in element.Arguments)
                        VisitExpression(argument);
                    break;
                case IrInterpolatedStringExpression interpolated:
                    foreach (var item in interpolated.Parts.OfType<IrInterpolation>())
                        VisitExpression(item.Expression);
                    break;
                case IrAssignmentExpression assignment:
                    VisitExpression(assignment.Target);
                    VisitExpression(assignment.Value);
                    break;
                case IrLambdaExpression lambda:
                    if (lambda.ExpressionBody is not null)
                        VisitExpression(lambda.ExpressionBody);
                    if (lambda.StatementBody is not null)
                        VisitStatement(lambda.StatementBody);
                    break;
                case IrQueryExpression query:
                    VisitExpression(query.SourceExpression);
                    foreach (var clause in query.Clauses)
                    {
                        switch (clause)
                        {
                            case IrWhereClause where:
                                VisitExpression(where.Predicate);
                                break;
                            case IrOrderClause order:
                                VisitExpression(order.Key);
                                break;
                            case IrSelectClause select:
                                VisitExpression(select.Projection);
                                break;
                            case IrGroupClause group:
                                VisitExpression(group.Element);
                                VisitExpression(group.Key);
                                break;
                        }
                    }
                    break;
            }
        }

        void PrepareObjectCreation(IrType type)
        {
            if (IsListType(type.Name))
            {
                _usesIndexedItems = true;
            }
            else if (IsDictionaryType(type.Name))
            {
                _usesDictionaries = true;
                var keyType = GenericArguments(type.Name).FirstOrDefault();
                if (keyType is not null)
                {
                    if (keyType.IsString || keyType.Name == "byte[]")
                        _usesStringOrBinaryDictionaryKeys = true;
                    else if (keyType.IsReference)
                        _usesReferenceDictionaryKeys = true;
                    else
                        _usesScalarDictionaryKeys = true;
                }
            }
            else if (IsRandomType(type.Name))
            {
                _usesIndexedItems = true;
            }
            else if (!_heapTypes.ContainsKey(type.Name))
            {
                return;
            }
            else
            {
                MarkHeapTypeUsed(type.Name);
            }
            _heapRuntimeNeeded = true;
        }
    }

    private void AddHeapType(IrHeapTypeDefinition definition)
    {
        if (_heapTypes.ContainsKey(definition.Name))
            return;

        var typeId = ++_nextHeapTypeId;
        var tableName = UsesDurableHeapStorage
            ? $"{DurableRuntimeSchema}.[__sharpsql_type_{_durableHeapProgramId}_{typeId}]"
            : $"#__sharpsql_type_{typeId}";
        var heapType = new HeapType(
            definition.Name,
            typeId,
            tableName,
            definition.IsValueType,
            definition.IsRecord,
            definition.Source)
        {
            BaseType = definition.BaseType
        };
        foreach (var field in definition.Fields)
            heapType.Fields.Add(
                field.Name,
                new HeapField(
                    field.Id,
                    field.Name,
                    field.Type,
                    QuoteIdentifier(field.Name),
                    field.Source,
                    field.IsStatic,
                    field.Initializer));
        foreach (var constructor in definition.Constructors)
            heapType.Constructors.Add(new HeapConstructor(
                constructor.Id,
                constructor.TargetFields,
                constructor.Parameters,
                constructor.Body,
                constructor.InitializerKind,
                constructor.InitializerConstructorId,
                constructor.InitializerArguments));
        if (heapType.Constructors.Count == 0)
            heapType.Constructors.Add(new HeapConstructor(
                IrConstructorId.None,
                [],
                [],
                null,
                IrConstructorInitializerKind.None,
                IrConstructorId.None,
                []));
        _heapTypes.Add(heapType.Name, heapType);
    }

    private static string ComputeDurableHeapProgramId(IReadOnlyList<IrHeapTypeDefinition> definitions)
    {
        var identity = new StringBuilder();
        foreach (var definition in definitions.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            identity.Append(definition.Name.Length).Append(':').Append(definition.Name).Append('|');
            identity.Append(definition.BaseType?.Name ?? string.Empty).Append('|');
            foreach (var field in definition.Fields.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                identity.Append(field.Name.Length).Append(':').Append(field.Name).Append(':')
                    .Append(field.Type.SqlType()).Append(':').Append(field.IsStatic ? 'S' : 'I').Append('|');
            }
            identity.Append(';');
        }

        using var algorithm = SHA256.Create();
        var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(identity.ToString()));
        return string.Concat(hash.Take(16).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private void EmitHeapPreamble()
    {
        if (!_heapRuntimeNeeded)
            return;

        if (UsesDurableHeapStorage)
        {
            EmitDurableHeapPreamble();
            return;
        }

        _sql.Line("-- SharpSql ephemeral managed heap");
        foreach (var type in UsedHeapTypes().Reverse())
            _sql.Line($"DROP TABLE IF EXISTS {type.TableName};");
        if (_usesDictionaries)
            _sql.Line($"DROP TABLE IF EXISTS {HeapDictionaryEntries};");
        if (_usesIndexedItems)
            _sql.Line($"DROP TABLE IF EXISTS {HeapIndexedItems};");
        _sql.Line($"DROP TABLE IF EXISTS {HeapObjects};");

        _sql.Line($"CREATE TABLE {HeapObjects} (");
        using (_sql.Indent())
        {
            _sql.Line("__id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,");
            _sql.Line("__type_id INT NOT NULL,");
            _sql.Line("__count INT NULL,");
            _sql.Line("__state0 INT NULL,");
            _sql.Line("__state1 INT NULL");
        }
        _sql.Line(");");

        foreach (var type in UsedHeapTypes())
        {
            EmitLeadingComments(type.Source);
            _sql.Line($"CREATE TABLE {type.TableName} (");
            using (_sql.Indent())
            {
                _sql.Line("__object_id INT NOT NULL PRIMARY KEY" + (type.Fields.Count == 0 ? string.Empty : ","));
                var fields = type.Fields.Values.ToArray();
                for (var index = 0; index < fields.Length; index++)
                {
                    EmitLeadingComments(fields[index].Source);
                    _sql.Line($"{fields[index].SqlName} {fields[index].Type.SqlType()} NULL{(index + 1 == fields.Length ? string.Empty : ",")}");
                }
            }
            _sql.Line(");");
        }

        if (_usesIndexedItems)
            EmitIndexedItemsTable();
        if (_usesDictionaries)
            EmitDictionaryTables();
        _sql.Line();
    }

    private void EmitDurableHeapPreamble()
    {
        _sql.Line("-- SharpSql durable managed heap");
        _sql.Line($"IF OBJECT_ID(N'{DurableHeapObjects}', N'U') IS NULL");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line($"CREATE TABLE {DurableHeapObjects} (");
            using (_sql.Indent())
            {
                _sql.Line("__execution_id UNIQUEIDENTIFIER NOT NULL,");
                _sql.Line("__id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,");
                _sql.Line("__type_id INT NOT NULL,");
                _sql.Line("__count INT NULL,");
                _sql.Line("__state0 INT NULL,");
                _sql.Line("__state1 INT NULL");
            }
            _sql.Line(");");
        }
        _sql.Line("END;");
        _sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'{DurableHeapObjects}') AND name = N'IX___sharpsql_objects_execution')");
        using (_sql.Indent())
            _sql.Line($"CREATE INDEX [IX___sharpsql_objects_execution] ON {DurableHeapObjects} (__execution_id);");

        foreach (var type in UsedHeapTypes())
        {
            EmitLeadingComments(type.Source);
            _sql.Line($"IF OBJECT_ID(N'{type.TableName}', N'U') IS NULL");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                _sql.Line($"CREATE TABLE {type.TableName} (");
                using (_sql.Indent())
                {
                    _sql.Line("__execution_id UNIQUEIDENTIFIER NOT NULL,");
                    _sql.Line("__object_id INT NOT NULL,");
                    var fields = type.Fields.Values.ToArray();
                    for (var index = 0; index < fields.Length; index++)
                    {
                        EmitLeadingComments(fields[index].Source);
                        _sql.Line($"{fields[index].SqlName} {fields[index].Type.SqlType()} NULL,");
                    }
                    _sql.Line("PRIMARY KEY (__execution_id, __object_id)");
                }
                _sql.Line(");");
            }
            _sql.Line("END;");
        }

        if (_usesIndexedItems)
            EmitDurableIndexedItemsTable();
        if (_usesDictionaries)
            EmitDurableDictionaryTable();
        _sql.Line();
    }

    private void EmitDurableIndexedItemsTable()
    {
        _sql.Line($"IF OBJECT_ID(N'{DurableHeapIndexedItems}', N'U') IS NULL");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line($"CREATE TABLE {DurableHeapIndexedItems} (");
            using (_sql.Indent())
            {
                _sql.Line("__execution_id UNIQUEIDENTIFIER NOT NULL,");
                _sql.Line("__owner_id INT NOT NULL,");
                _sql.Line("__index INT NOT NULL,");
                _sql.Line("__value SQL_VARIANT NULL,");
                _sql.Line("__text_value NVARCHAR(MAX) NULL,");
                _sql.Line("__binary_value VARBINARY(MAX) NULL,");
                _sql.Line("__reference_value INT NULL,");
                _sql.Line("PRIMARY KEY (__execution_id, __owner_id, __index)");
            }
            _sql.Line(");");
        }
        _sql.Line("END;");
    }

    private void EmitDurableDictionaryTable()
    {
        _sql.Line($"IF OBJECT_ID(N'{DurableHeapDictionaryEntries}', N'U') IS NULL");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line($"CREATE TABLE {DurableHeapDictionaryEntries} (");
            using (_sql.Indent())
            {
                _sql.Line("__execution_id UNIQUEIDENTIFIER NOT NULL,");
                _sql.Line("__id INT IDENTITY(1,1) NOT NULL,");
                _sql.Line("__dictionary_id INT NOT NULL,");
                _sql.Line("__key SQL_VARIANT NULL,");
                _sql.Line("__key_text NVARCHAR(MAX) NULL,");
                _sql.Line("__key_binary VARBINARY(MAX) NULL,");
                _sql.Line("__key_reference INT NULL,");
                _sql.Line("__key_hash BINARY(32) NULL,");
                _sql.Line("__value SQL_VARIANT NULL,");
                _sql.Line("__text_value NVARCHAR(MAX) NULL,");
                _sql.Line("__binary_value VARBINARY(MAX) NULL,");
                _sql.Line("__reference_value INT NULL,");
                _sql.Line("PRIMARY KEY (__execution_id, __dictionary_id, __id)");
            }
            _sql.Line(");");
        }
        _sql.Line("END;");
        _sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'{DurableHeapDictionaryEntries}') AND name = N'IX___sharpsql_dictionary_scalar_key')");
        using (_sql.Indent())
            _sql.Line($"CREATE INDEX [IX___sharpsql_dictionary_scalar_key] ON {DurableHeapDictionaryEntries} (__execution_id, __dictionary_id, __key) WHERE __key IS NOT NULL;");
        _sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'{DurableHeapDictionaryEntries}') AND name = N'IX___sharpsql_dictionary_reference_key')");
        using (_sql.Indent())
            _sql.Line($"CREATE INDEX [IX___sharpsql_dictionary_reference_key] ON {DurableHeapDictionaryEntries} (__execution_id, __dictionary_id, __key_reference) WHERE __key_reference IS NOT NULL;");
        _sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'{DurableHeapDictionaryEntries}') AND name = N'IX___sharpsql_dictionary_hash_key')");
        using (_sql.Indent())
            _sql.Line($"CREATE INDEX [IX___sharpsql_dictionary_hash_key] ON {DurableHeapDictionaryEntries} (__execution_id, __dictionary_id, __key_hash) WHERE __key_hash IS NOT NULL;");
    }

    private void EmitIndexedItemsTable()
    {
        _sql.Line($"CREATE TABLE {HeapIndexedItems} (");
        using (_sql.Indent())
        {
            _sql.Line("__owner_id INT NOT NULL,");
            _sql.Line("__index INT NOT NULL,");
            _sql.Line("__value SQL_VARIANT NULL,");
            _sql.Line("__text_value NVARCHAR(MAX) NULL,");
            _sql.Line("__binary_value VARBINARY(MAX) NULL,");
            _sql.Line("__reference_value INT NULL,");
            _sql.Line("PRIMARY KEY (__owner_id, __index)");
        }
        _sql.Line(");");
    }

    private void EmitDictionaryTables()
    {
        _sql.Line($"CREATE TABLE {HeapDictionaryEntries} (");
        using (_sql.Indent())
        {
            _sql.Line("__id INT IDENTITY(1,1) NOT NULL,");
            _sql.Line("__dictionary_id INT NOT NULL,");
            _sql.Line("__key SQL_VARIANT NULL,");
            _sql.Line("__key_text NVARCHAR(MAX) NULL,");
            _sql.Line("__key_binary VARBINARY(MAX) NULL,");
            _sql.Line("__key_reference INT NULL,");
            if (_usesStringOrBinaryDictionaryKeys)
                _sql.Line("__key_hash BINARY(32) NULL,");
            _sql.Line("__value SQL_VARIANT NULL,");
            _sql.Line("__text_value NVARCHAR(MAX) NULL,");
            _sql.Line("__binary_value VARBINARY(MAX) NULL,");
            _sql.Line("__reference_value INT NULL,");
            _sql.Line("PRIMARY KEY (__dictionary_id, __id)");
        }
        _sql.Line(");");
        if (_usesScalarDictionaryKeys)
            _sql.Line($"CREATE INDEX __sharpsql_dictionary_scalar_key ON {HeapDictionaryEntries} (__dictionary_id, __key) WHERE __key IS NOT NULL;");
        if (_usesReferenceDictionaryKeys)
            _sql.Line($"CREATE INDEX __sharpsql_dictionary_reference_key ON {HeapDictionaryEntries} (__dictionary_id, __key_reference) WHERE __key_reference IS NOT NULL;");
        if (_usesStringOrBinaryDictionaryKeys)
            _sql.Line($"CREATE INDEX __sharpsql_dictionary_hash_key ON {HeapDictionaryEntries} (__dictionary_id, __key_hash) WHERE __key_hash IS NOT NULL;");
    }

    private void EmitHeapEpilogue()
    {
        if (!_heapRuntimeNeeded)
        {
            EmitHeapDiagnostics(objects: "0", indexedItems: "0", dictionaryEntries: "0");
            return;
        }

        EmitHeapDiagnostics(
            UsesDurableHeapStorage
                ? $"(SELECT COUNT_BIG(*) FROM {HeapObjects} WHERE __execution_id = {RuntimeExecutionId})"
                : $"(SELECT COUNT_BIG(*) FROM {HeapObjects})",
            _usesIndexedItems
                ? UsesDurableHeapStorage
                    ? $"(SELECT COUNT_BIG(*) FROM {HeapIndexedItems} WHERE __execution_id = {RuntimeExecutionId})"
                    : $"(SELECT COUNT_BIG(*) FROM {HeapIndexedItems})"
                : "0",
            _usesDictionaries
                ? UsesDurableHeapStorage
                    ? $"(SELECT COUNT_BIG(*) FROM {HeapDictionaryEntries} WHERE __execution_id = {RuntimeExecutionId})"
                    : $"(SELECT COUNT_BIG(*) FROM {HeapDictionaryEntries})"
                : "0");

        if (UsesDurableHeapStorage)
        {
            EmitDurableHeapCleanup();
            return;
        }

        foreach (var type in UsedHeapTypes().Reverse())
            _sql.Line($"DROP TABLE IF EXISTS {type.TableName};");
        if (_usesDictionaries)
            _sql.Line($"DROP TABLE IF EXISTS {HeapDictionaryEntries};");
        if (_usesIndexedItems)
            _sql.Line($"DROP TABLE IF EXISTS {HeapIndexedItems};");
        _sql.Line($"DROP TABLE IF EXISTS {HeapObjects};");
    }

    private void EmitDurableHeapCleanup()
    {
        if (!UsesDurableHeapStorage || !_heapRuntimeNeeded)
            return;

        foreach (var type in UsedHeapTypes().Reverse())
            _sql.Line($"DELETE FROM {type.TableName} WHERE __execution_id = {RuntimeExecutionId};");
        if (_usesDictionaries)
            _sql.Line($"DELETE FROM {HeapDictionaryEntries} WHERE __execution_id = {RuntimeExecutionId};");
        if (_usesIndexedItems)
            _sql.Line($"DELETE FROM {HeapIndexedItems} WHERE __execution_id = {RuntimeExecutionId};");
        _sql.Line($"DELETE FROM {HeapObjects} WHERE __execution_id = {RuntimeExecutionId};");
    }

    private IEnumerable<HeapType> UsedHeapTypes() =>
        _heapTypes.Values.Where(type => _usedHeapTypes.Contains(type.Name));

    private void MarkHeapTypeUsed(string typeName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (_heapTypes.TryGetValue(typeName, out var type) && visited.Add(typeName))
        {
            _usedHeapTypes.Add(typeName);
            if (type.BaseType is null)
                break;
            typeName = type.BaseType.Name;
        }
    }

    private HeapType? BaseHeapType(HeapType type) =>
        type.BaseType is not null && _heapTypes.TryGetValue(type.BaseType.Name, out var baseType)
            ? baseType
            : null;

    private IReadOnlyList<HeapType> HeapHierarchyBaseFirst(HeapType type)
    {
        var hierarchy = new List<HeapType>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (HeapType? current = type; current is not null && visited.Add(current.Name); current = BaseHeapType(current))
            hierarchy.Add(current);
        hierarchy.Reverse();
        return hierarchy;
    }

    private bool TryResolveHeapField(
        IrType receiverType,
        string name,
        IrMemberId memberId,
        out HeapType declaringType,
        out HeapField field)
    {
        if (!_heapTypes.TryGetValue(receiverType.Name, out var receiverHeapType))
        {
            declaringType = null!;
            field = null!;
            return false;
        }

        var hierarchy = HeapHierarchyBaseFirst(receiverHeapType);
        if (!memberId.IsNone)
        {
            foreach (var candidateType in hierarchy)
                foreach (var candidateField in candidateType.Fields.Values)
                {
                    if (candidateField.Id == memberId)
                    {
                        declaringType = candidateType;
                        field = candidateField;
                        return true;
                    }
                }
        }

        for (var index = hierarchy.Count - 1; index >= 0; index--)
        {
            var candidateType = hierarchy[index];
            if (candidateType.Fields.TryGetValue(name, out field!))
            {
                declaringType = candidateType;
                return true;
            }
        }

        declaringType = null!;
        field = null!;
        return false;
    }

    private void EmitHeapDiagnostics(string objects, string indexedItems, string dictionaryEntries)
    {
        if (!_options.EmitRuntimeDiagnostics)
            return;
        var objectCount = _names.Allocate("_debug_heap_objects");
        var indexedItemCount = _names.Allocate("_debug_indexed_items");
        var dictionaryEntryCount = _names.Allocate("_debug_dictionary_entries");
        _sql.Line($"DECLARE {objectCount} BIGINT = {objects};");
        _sql.Line($"DECLARE {indexedItemCount} BIGINT = {indexedItems};");
        _sql.Line($"DECLARE {dictionaryEntryCount} BIGINT = {dictionaryEntries};");
        _sql.Line(
            "PRINT CONCAT(N'__SHARPSQL_DEBUG_HEAP__|objects=', " + objectCount +
            ", N'|indexed_items=', " + indexedItemCount +
            ", N'|dictionary_entries=', " + dictionaryEntryCount + ");");
    }

    private bool ContainsRuntimeExpression(ExpressionSyntax expression) =>
        ContainsVmCall(expression) || ContainsHeapEffect(expression) || ContainsGuardedLinqExpression(expression);

    private bool ContainsHeapEffect(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf().Any(node => node switch
        {
            BaseObjectCreationExpressionSyntax creation => IsHeapCreation(creation),
            WithExpressionSyntax withExpression =>
                _heapTypes.ContainsKey(InferType(withExpression.Expression, new VariableScope()).Name),
            InvocationExpressionSyntax invocation =>
                IsRandomInvocation(invocation) || IsLinqMaterialization(invocation),
            ElementAccessExpressionSyntax => true,
            ArrayCreationExpressionSyntax array => array.Type.ElementType.ToString() != "byte",
            ImplicitArrayCreationExpressionSyntax array =>
                InferType(array, new VariableScope()).Name != "byte[]",
            _ => false
        });

    private bool IsHeapCreation(BaseObjectCreationExpressionSyntax creation)
    {
        var name = CreationTypeName(creation);
        return _heapTypes.ContainsKey(name) || IsListType(name) || IsDictionaryType(name) || IsRandomType(name);
    }

    private string CreationTypeName(BaseObjectCreationExpressionSyntax creation)
    {
        if (creation is ObjectCreationExpressionSyntax explicitCreation)
            return NormalizeTypeName(explicitCreation.Type.ToString());
        var type = SemanticModelFor(creation)?.GetTypeInfo(creation).Type;
        return type is null ? string.Empty : NormalizeTypeName(CSharpTypeFactory.From(type).Name);
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

    private sealed class HeapType(
        string name,
        int id,
        string tableName,
        bool isValueType,
        bool isRecord,
        IrSource source)
    {
        public string Name { get; } = name;
        public int Id { get; } = id;
        public string TableName { get; } = tableName;
        public bool IsValueType { get; } = isValueType;
        public bool IsRecord { get; } = isRecord;
        public IrType? BaseType { get; init; }
        public IrSource Source { get; } = source;
        public Dictionary<string, HeapField> Fields { get; } = new(StringComparer.Ordinal);
        public List<HeapConstructor> Constructors { get; } = [];
    }

    private sealed record HeapField(
        IrMemberId Id,
        string Name,
        IrType Type,
        string SqlName,
        IrSource Source,
        bool IsStatic,
        IrExpression? Initializer);
    private sealed record HeapConstructor(
        IrConstructorId Id,
        IReadOnlyList<string> TargetFields,
        IReadOnlyList<ParameterDefinition> Parameters,
        ProceduralBlock? Body,
        IrConstructorInitializerKind InitializerKind,
        IrConstructorId InitializerConstructorId,
        IReadOnlyList<IrExpression> InitializerArguments);
    private sealed record HeapValueAssignment(HeapField Field, string ValueSql);
}
