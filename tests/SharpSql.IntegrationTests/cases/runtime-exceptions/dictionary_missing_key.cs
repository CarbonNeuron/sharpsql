// sharpsql-expect-exception: KeyNotFoundException
var values = new Dictionary<string, int>();
Console.WriteLine("before missing dictionary key");
Console.WriteLine(values["missing"]);
