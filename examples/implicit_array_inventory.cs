using System.Linq;

var names = new[] { "Potion", "Sword", "Shield", "Apple", "Torch", "Helmet" };
var inventory = new List<InvItem>();

var random = new Random(2);
for (var i = 0; i < 10; i++)
{
    inventory.Add(new InvItem(names[random.Next(names.Length)], random.Next(1, 6)));
}

foreach (var item in inventory
             .Where(candidate => candidate.Quantity > 2)
             .OrderByDescending(candidate => candidate.Quantity)
             .Take((int)(inventory.Count * 0.2)))
{
    Console.WriteLine(item);
}

record InvItem(string Name, int Quantity);
