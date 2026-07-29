// sharpsql-expect-exception: ArgumentOutOfRangeException
using System.Linq;

var values = Enumerable.Range(2147483647, 2);
Console.WriteLine(values.Count());
