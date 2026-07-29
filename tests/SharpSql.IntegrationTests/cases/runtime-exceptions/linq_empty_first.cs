// sharpsql-expect-exception: InvalidOperationException
using System.Linq;

var values = new List<int>();
Console.WriteLine("before empty First");
Console.WriteLine(values.First());
