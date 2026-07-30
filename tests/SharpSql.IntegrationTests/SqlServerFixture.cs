using Testcontainers.MsSql;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SharpSql.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server parity";
}

public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string MemoryOptimizedDatabaseName = "SharpSqlMemoryOptimizedTests";
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();
    private readonly SemaphoreSlim _memoryOptimizedGate = new(1, 1);
    private string? _memoryOptimizedConnectionString;

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async Task<string> GetMemoryOptimizedConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (_memoryOptimizedConnectionString is not null)
            return _memoryOptimizedConnectionString;

        await _memoryOptimizedGate.WaitAsync(cancellationToken);
        try
        {
            if (_memoryOptimizedConnectionString is not null)
                return _memoryOptimizedConnectionString;

            await using (var master = new SqlConnection(ConnectionString))
            {
                await master.OpenAsync(cancellationToken);
                await using var command = master.CreateCommand();
                command.CommandTimeout = 120;
                command.CommandText = $"""
                    IF DB_ID(N'{MemoryOptimizedDatabaseName}') IS NULL
                    BEGIN
                        CREATE DATABASE [{MemoryOptimizedDatabaseName}];
                        ALTER DATABASE [{MemoryOptimizedDatabaseName}]
                            ADD FILEGROUP [SharpSqlMemoryOptimized] CONTAINS MEMORY_OPTIMIZED_DATA;
                        ALTER DATABASE [{MemoryOptimizedDatabaseName}]
                            ADD FILE
                            (
                                NAME = N'SharpSqlMemoryOptimized',
                                FILENAME = N'/var/opt/mssql/data/{MemoryOptimizedDatabaseName}_xtp'
                            )
                            TO FILEGROUP [SharpSqlMemoryOptimized];
                    END;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            _memoryOptimizedConnectionString = new SqlConnectionStringBuilder(ConnectionString)
            {
                InitialCatalog = MemoryOptimizedDatabaseName
            }.ConnectionString;
            await using var database = new SqlConnection(_memoryOptimizedConnectionString);
            await database.OpenAsync(cancellationToken);
            await using var provision = database.CreateCommand();
            provision.CommandTimeout = 120;
            provision.CommandText = SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql();
            await provision.ExecuteNonQueryAsync(cancellationToken);
            return _memoryOptimizedConnectionString;
        }
        finally
        {
            _memoryOptimizedGate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
