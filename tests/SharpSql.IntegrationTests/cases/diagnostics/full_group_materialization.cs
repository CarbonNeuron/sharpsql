// sharpsql-expect-diagnostics: SS6411
using System.Linq;

var values = new List<int> { 1, 2, 1 };
var groups = values.GroupBy(value => value).ToList();
