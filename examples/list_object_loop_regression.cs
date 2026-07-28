var a = new List<Person>();

for (int i = 0; i < 5; i++)
{
    a.Add(new Person($"{i}", i));
}

foreach (var person in a)
{
    Console.WriteLine($"{person.Name} - {person.Age}");
}

var total = 0;
foreach (var person in a)
{
    total += person.Age;
}
Console.WriteLine($"sum = {total}");

record Person(string Name, int Age);
