using System.Linq;

var people = new List<Person>
{
    new Person("Bob", 40),
    new Person("Jane", 20),
    new Person("Saul", 55)
};

IQueryable<Person> queryable = people.AsQueryable();
var adults = queryable.Where(person => person.Age >= 21);
var ages = adults.Select(person => person.Age);

int total = ages.Sum();
int count = adults.Count();
long longCount = adults.LongCount();
bool anyJane = queryable.Any(person => person.Name == "Jane");
bool allPositive = queryable.All(person => person.Age > 0);
bool contains55 = ages.Contains(55);
int firstAdultAge = ages.FirstOrDefault();
int missingAge = ages.Where(age => age > 100).FirstOrDefault();

var materialized = ages.ToList();
var materializedArray = ages.ToArray();
foreach (var age in ages)
{
    Console.WriteLine($"age={age}");
}

var querySyntax = from person in queryable
                  where person.Age >= 21
                  select person.Age;

Console.WriteLine($"summary={total}:{count}:{longCount}:{anyJane}:{allPositive}:{contains55}:{firstAdultAge}:{missingAge}:{materialized.Count}:{materializedArray.Length}:{querySyntax.Sum()}");

record Person(string Name, int Age);
