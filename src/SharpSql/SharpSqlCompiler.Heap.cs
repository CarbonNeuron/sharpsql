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
    private bool UsesSharedHeapObjectStorage => UsesDurableHeapStorage || UsesMemoryOptimizedRuntime;
    private string MemoryOptimizedHeapObjects =>
        $"{SqlIdentifier.Quote(_options.ApplicationSchema, nameof(TranspileOptions.ApplicationSchema))}." +
        $"[{MemoryOptimizedRuntimeSqlEmitter.HeapObjectsTableName(_effectiveRuntime.Durability)}]";
    private string HeapObjects => UsesMemoryOptimizedRuntime
        ? MemoryOptimizedHeapObjects
        : UsesDurableHeapStorage ? DurableHeapObjects : EphemeralHeapObjects;
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

    private string HeapObjectExecutionFilter(string? alias = null) => UsesSharedHeapObjectStorage
        ? $"{(alias is null ? string.Empty : alias + ".")}__execution_id = {RuntimeExecutionId} AND "
        : string.Empty;

    private string HeapObjectInsertColumns(string columns) => UsesSharedHeapObjectStorage
        ? $"__execution_id, {columns}"
        : columns;

    private string HeapObjectInsertValues(string values) => UsesSharedHeapObjectStorage
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
        if (UsesMemoryOptimizedRuntime)
        {
            EmitMemoryOptimizedHeapObjectsPrerequisite();
        }
        else
        {
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
        }

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
        if (UsesMemoryOptimizedRuntime)
        {
            EmitMemoryOptimizedHeapObjectsPrerequisite();
        }
        else
        {
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
        }

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

    private void EmitMemoryOptimizedHeapObjectsPrerequisite()
    {
        _sql.Line("-- SharpSql database-global memory-optimized heap object registry");
        _sql.Line($"IF OBJECT_ID({SqlIdentifier.UnicodeLiteral(MemoryOptimizedHeapObjects)}, N'U') IS NULL");
        using (_sql.Indent())
            _sql.Line($"THROW {MemoryOptimizedRuntimeSqlEmitter.MissingPhysicalTableErrorNumber}, 'Provision the SharpSql memory-optimized runtime before executing this program.', 1;");
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
            UsesSharedHeapObjectStorage
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
        if (UsesMemoryOptimizedRuntime)
            _sql.Line($"DELETE FROM {HeapObjects} WHERE __execution_id = {RuntimeExecutionId};");
        else
            _sql.Line($"DROP TABLE IF EXISTS {HeapObjects};");
    }

    private void EmitDurableHeapCleanup()
    {
        if (!UsesSharedHeapObjectStorage || !_heapRuntimeNeeded)
            return;

        if (UsesDurableHeapStorage)
        {
            foreach (var type in UsedHeapTypes().Reverse())
                _sql.Line($"DELETE FROM {type.TableName} WHERE __execution_id = {RuntimeExecutionId};");
            if (_usesDictionaries)
                _sql.Line($"DELETE FROM {HeapDictionaryEntries} WHERE __execution_id = {RuntimeExecutionId};");
            if (_usesIndexedItems)
                _sql.Line($"DELETE FROM {HeapIndexedItems} WHERE __execution_id = {RuntimeExecutionId};");
        }
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
