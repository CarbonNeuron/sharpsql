using System.Linq;

var people = new List<Person>();
var random = new Random(12345);
var names = new List<string>
{
    "Bob",
    "Jane",
    "Billy",
    "James",
    "Saul"
};

for (int i = 0; i < 5; i++)
{
    people.Add(new Person(names[i], random.Next(1, 100)));
}

foreach (var person in people)
{
    Console.WriteLine($"{person.Name} - {person.Age}");
}

var total = people.Sum(person => person.Age);
Console.WriteLine($"sum = {total}");

record Person(string Name, int Age);
