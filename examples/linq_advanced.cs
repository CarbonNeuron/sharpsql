using System.Linq;

var values = new List<int> { 4, 1, 3, 1, 2, 4 };
var page = values.Distinct()
    .OrderByDescending(value => value)
    .ThenBy(value => value)
    .Skip(1)
    .Take(2)
    .ToList();

foreach (var value in page)
{
    Console.WriteLine($"page={value}");
}

int minimum = values.Min();
int maximum = values.Max();
double average = values.Average();
int first = values.OrderBy(value => value).First();
int last = values.OrderBy(value => value).Last();
int single = values.Where(value => value == 3).Single();
int singleDefault = values.Where(value => value == 99).SingleOrDefault();
int element = values.ElementAt(1);
int missing = values.ElementAtOrDefault(99);

var people = new List<Person>
{
    new Person("Bob", 40),
    new Person("Jane", 20),
    new Person("Saul", 40)
};
Person youngest = people.MinBy(person => person.Age);
Person oldest = people.MaxBy(person => person.Age);
Console.WriteLine($"by={youngest.Name}:{oldest.Name}");

var bands = new List<Band>
{
    new Band(20, "young"),
    new Band(40, "older")
};
var joined = people.Join(
    bands,
    person => person.Age,
    band => band.Age,
    (person, band) => person.Name + ":" + band.Label);
foreach (var match in joined)
{
    Console.WriteLine($"join={match}");
}

var groupKeys = people.GroupBy(person => person.Age)
    .Select(group => group.Key)
    .OrderBy(age => age);
foreach (var age in groupKeys)
{
    Console.WriteLine($"group={age}");
}

int threshold = 1;
Func<int, bool> predicate = value => value > threshold;
var filtered = Filter(values, predicate);
var limited = FilterAbove(filtered, 1);
Func<int, bool> returnedPredicate = AtLeast(threshold);
threshold = 2;
int deferredCount = CountMatches(limited, returnedPredicate);

var querySyntax = from value in values
                  orderby value descending
                  select value;
int queryFirst = querySyntax.First();
var queryGroups = from person in people
                  group person by person.Age;
int queryGroupCount = queryGroups.Count();

Console.WriteLine($"terminals={minimum}:{maximum}:{average}:{first}:{last}:{single}:{singleDefault}:{element}:{missing}:{deferredCount}:{queryFirst}:{queryGroupCount}");

IEnumerable<int> Filter(IEnumerable<int> source, Func<int, bool> test) => source.Where(test);
IEnumerable<int> FilterAbove(IEnumerable<int> source, int minimum) => source.Where(value => value > minimum);
int CountMatches(IEnumerable<int> source, Func<int, bool> test) => source.Count(test);
Func<int, bool> AtLeast(int minimum) => value => value >= minimum;

record Person(string Name, int Age);
record Band(int Age, string Label);
