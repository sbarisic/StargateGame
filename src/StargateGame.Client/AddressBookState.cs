using System.Text.Json;
namespace StargateGame.Client;

sealed record BookAddress(Guid Universe, Guid Location, string Name, int[] Symbols);
sealed class AddressBookState
{
    public int Version
    {
        get;
        set;
    } = 1;
    public List<BookAddress> Entries
    {
        get;
        set;
    } = [];
    public Dictionary<Guid, Guid> Selected
    {
        get;
        set;
    } = [];
    public static bool Valid(BookAddress a) => a.Universe != Guid.Empty && a.Location != Guid.Empty &&
        !string.IsNullOrWhiteSpace(a.Name) && a.Symbols is { Length: 6 } && a.Symbols.All(n => n is >= 2 and <= 39) && a.Symbols.Distinct().Count() == 6;
    public bool Visit(BookAddress address)
    {
        if (!Valid(address))
            return false;
        var old = Entries.FindIndex(e => e.Universe == address.Universe && e.Location == address.Location);
        if (old == 0 && Entries[old].Name == address.Name && Entries[old].Symbols.SequenceEqual(address.Symbols))
            return false;
        if (old >= 0)
            Entries.RemoveAt(old);
        Entries.Insert(0, address with
        {
            Symbols = address.Symbols.ToArray()
        });
        if (Entries.Count > 512)
            Entries.RemoveRange(512, Entries.Count - 512);
        if (!Selected.ContainsKey(address.Universe))
            Selected[address.Universe] = address.Location;
        return true;
    }
    public IEnumerable<BookAddress> History(Guid universe) => Entries.Where(e => e.Universe == universe);
    public BookAddress? Selection(Guid universe) => History(universe).FirstOrDefault(e => e.Location == Selected.GetValueOrDefault(universe)) ?? History(universe).FirstOrDefault();
    public void Select(BookAddress entry)
    {
        if (Entries.Any(e => e.Universe == entry.Universe && e.Location == entry.Location))
            Selected[entry.Universe] = entry.Location;
    }
    public static AddressBookState Load(string path)
    {
        try
        {
            var data = JsonSerializer.Deserialize<AddressBookState>(File.ReadAllText(path));
            if (data?.Version != 1)
                return new();
            data.Entries = (data.Entries ?? []).Where(e => e is not null && Valid(e)).DistinctBy(e => (e.Universe, e.Location)).Take(512).ToList();
            data.Selected ??= [];
            return data;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, true);
    }
}
