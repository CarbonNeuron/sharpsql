using System.Linq;

var numbers = Enumerable.Range(1, 100);
var filtered = numbers.Take(5).Where(x => x > 2).ToList().Take(2).ToList();

var sum = filtered.Sum();
var average = filtered.Average();
Console.WriteLine($"The sum of numbers is {sum}");
Console.WriteLine($"The average of numbers is {average}");

Console.WriteLine("Don't fret");
var person = new Person(2, "Bob");
Console.WriteLine(person);

var count = new Random(3).Next(0, 100);
var people = Enumerable.Repeat(person, count);

foreach (var item in people)
    Console.WriteLine(item);

Console.WriteLine($"I did that {count} times.");

record Person(int Id, string Name);
