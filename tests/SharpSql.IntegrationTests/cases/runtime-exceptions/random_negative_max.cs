// sharpsql-expect-exception: ArgumentOutOfRangeException
var random = new Random(42);
Console.WriteLine("before invalid Random.Next");
Console.WriteLine(random.Next(-1));
