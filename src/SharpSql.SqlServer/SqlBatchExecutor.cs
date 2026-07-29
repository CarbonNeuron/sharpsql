using Microsoft.Data.SqlClient;

namespace SharpSql.SqlServer;

public sealed record SqlBatchExecutionResult(
    bool Success,
    IReadOnlyList<string> Messages,
    int? ErrorNumber = null,
    string? ErrorMessage = null,
    int RowsAffected = -1);

public static class SqlBatchExecutor
{
    public static async Task<SqlBatchExecutionResult> ExecuteAsync(
        SqlConnection connection,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        SqlError? reportedError = null;
        void HandleInfoMessage(object sender, SqlInfoMessageEventArgs args)
        {
            foreach (SqlError error in args.Errors)
            {
                if (error.Class == 0)
                    messages.Add(error.Message);
                else
                    reportedError ??= error;
            }
        }

        connection.InfoMessage += HandleInfoMessage;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = commandTimeoutSeconds;
            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (reportedError is not null)
                return new SqlBatchExecutionResult(false, messages, reportedError.Number, reportedError.Message, rowsAffected);
            return new SqlBatchExecutionResult(true, messages, RowsAffected: rowsAffected);
        }
        catch (SqlException exception)
        {
            var error = exception.Errors.Cast<SqlError>().First();
            return new SqlBatchExecutionResult(false, messages, error.Number, error.Message);
        }
        finally
        {
            connection.InfoMessage -= HandleInfoMessage;
        }
    }
}
