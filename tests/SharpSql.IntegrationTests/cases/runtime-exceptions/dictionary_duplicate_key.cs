// sharpsql-expect-exception: ArgumentException
var values = new Dictionary<string, int>();
values.Add("one", 1);
Console.WriteLine("before duplicate");
values.Add("one", 2);
