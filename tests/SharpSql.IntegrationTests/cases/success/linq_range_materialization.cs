using System.Linq;

var numbers = Enumerable.Range(1, 10);

var filtered = numbers.Where(x => x > 2).ToList().Take(2).ToList();

var sum = filtered.Sum();
var average = filtered.Average();
Console.WriteLine($"The sum of numbers is {sum}");
Console.WriteLine($"The average of numbers is {average}");
