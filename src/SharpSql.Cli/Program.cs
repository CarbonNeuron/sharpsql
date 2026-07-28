using SharpSql;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: sharpsql [input.cs] [-o output.sql]");
    Console.WriteLine("With no input file, C# is read from standard input and SQL is written to standard output.");
    return 0;
}

string? inputPath = null;
string? outputPath = null;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "-o" && index + 1 < args.Length)
        outputPath = args[++index];
    else if (inputPath is null)
        inputPath = args[index];
    else
    {
        Console.Error.WriteLine($"Unexpected argument: {args[index]}");
        return 2;
    }
}

var source = inputPath is null
    ? await Console.In.ReadToEndAsync()
    : await File.ReadAllTextAsync(inputPath);
var result = new SharpSqlCompiler().Transpile(source);

foreach (var diagnostic in result.Diagnostics)
    Console.Error.WriteLine(diagnostic);

if (!result.Success)
    return 1;

if (outputPath is null)
    Console.Write(result.Sql);
else
    await File.WriteAllTextAsync(outputPath, result.Sql);

return 0;
