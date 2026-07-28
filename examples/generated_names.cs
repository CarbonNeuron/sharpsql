using System.Linq;

var people = new List<Person>();
var random = new Random(12345);

for (int i = 0; i < 5; i++)
{
    people.Add(new Person(RandomString(8), random.Next(1, 100)));
}

foreach (var person in people)
{
    Console.WriteLine($"{person.Name} - {person.Age}");
}

var total = people.Sum(person => person.Age);
Console.WriteLine($"sum = {total}");

string RandomString(int length)
{
    var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    return new string(Enumerable.Repeat(chars, length)
        .Select(value => value[random.Next(value.Length)]).ToArray());
}

record Person(string Name, int Age);
