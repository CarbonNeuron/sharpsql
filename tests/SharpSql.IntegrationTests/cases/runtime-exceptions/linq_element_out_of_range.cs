// sharpsql-expect-exception: ArgumentOutOfRangeException
using System.Linq;

var values = new List<int> { 4 };
Console.WriteLine("before invalid ElementAt");
Console.WriteLine(values.ElementAt(2));
