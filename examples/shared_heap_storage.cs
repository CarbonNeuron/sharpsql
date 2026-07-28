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

for (int i = 0; i < names.Count; i++)
{
    people.Add(new Person(names[i], random.Next(1, 100)));
}

foreach (var person in people)
{
    Console.WriteLine($"{person.Name} - {person.Age}");
}

var total = 0;
foreach (var person in people)
{
    total += person.Age;
}
Console.WriteLine($"sum = {total}");

record Person(string Name, int Age);
