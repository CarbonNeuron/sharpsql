// sharpsql-expect-exception: ArgumentOutOfRangeException
var values = new List<int> { 4 };
Console.WriteLine("before invalid list index");
Console.WriteLine(values[2]);
