Person ada = new Person("Ada", 36);
Person alias = ada;
ada.Age = 37;

List<Person> people = new List<Person> { ada };
people.Add(new Person("Grace", 30));

Dictionary<string, Person> byName = new Dictionary<string, Person>();
byName.Add("ada", ada);
byName["grace"] = people[1];

Person olderGrace = AddYears(byName["grace"], 2);
Console.WriteLine($"{alias.Name}:{alias.Age}; count={people.Count}; grace={olderGrace.Age}; known={byName.ContainsKey("ada")}");

Person AddYears(Person person, int years)
{
    if (years == 0) return person;
    person.Age = person.Age + 1;
    return AddYears(person, years - 1);
}

class Person
{
    public Person(string name, int age)
    {
        Name = name;
        Age = age;
    }

    public string Name { get; set; }
    public int Age { get; set; }
}
