// sharpsql-expect-exception: InvalidOperationException
using System.Linq;

var values = new List<int> { 4, 4 };
Console.WriteLine("before multiple Single");
Console.WriteLine(values.Single());
