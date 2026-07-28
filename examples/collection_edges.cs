List<int> values = new List<int> { 3, 1, 4 };
values.Add(1);
bool containedFour = values.Contains(4);
values.RemoveAt(1);
values[1] = 9;

int listSum = 0;
foreach (int value in values)
{
    listSum += value;
}
int listCountBeforeClear = values.Count;
values.Clear();
int listCountAfterClear = values.Count;

Dictionary<string, int> scores = new Dictionary<string, int>();
scores.Add("Ada", 10);
scores.Add("ada", 20);
scores["Ada"] = scores["Ada"] + 5;
bool containsUpper = scores.ContainsKey("Ada");
bool containsLower = scores.ContainsKey("ada");
bool containsValue = scores.ContainsValue(15);
scores.Remove("ada");
bool containsRemoved = scores.ContainsKey("ada");
int dictionaryCountBeforeClear = scores.Count;
scores.Clear();
int dictionaryCountAfterClear = scores.Count;

Console.WriteLine($"list={listCountBeforeClear}:{listSum}:{containedFour}:{listCountAfterClear}; dictionary={containsUpper}:{containsLower}:{containsValue}:{containsRemoved}:{dictionaryCountBeforeClear}:{dictionaryCountAfterClear}");
