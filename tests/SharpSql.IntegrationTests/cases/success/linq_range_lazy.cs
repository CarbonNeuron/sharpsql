using System.Linq;

var numbers = Enumerable.Range(1, 1_000_000_000);
var values = numbers.Take(5).ToList();

Console.WriteLine(values.Sum());
Console.WriteLine(Enumerable.Range(-2147483648, 0).Count());
