using StargateGame.Client;

static class AddressBookChecks
{
    public static async Task Network()
    {
        string directory = Path.Combine(Path.GetTempPath(), "stargate-book-network-" + Guid.NewGuid().ToString("N"));
        await using var server = new StargateGame.Server.GameServer(System.Net.IPAddress.Loopback, 0, 8, 7319, "temperate", false, directory, cataloguePath: Path.GetFullPath("artifacts/galaxy/galaxy.bin"));
        server.Start();
        using var connection = await StargateGame.Shared.GameConnection.ConnectAsync("127.0.0.1", server.Port, "BookReader", StargateGame.Physics.PhysicsWorld.PhysicsHash, StargateGame.Physics.PhysicsWorld.ContentHash);
        using var client = new ClientSession(connection);
        for (int i = 0; i < 100 && client.ReturnAddress.Length != 6; i++)
        {
            client.Poll();
            await Task.Delay(20);
        }
        Verify.Check(!client.Developer && client.ReturnAddress.Length == 6 && client.CurrentWorldName == "Home Base", "ordinary player receives named current-world address");
        Verify.Check(client.Destinations.Length == 0, "address book does not expose developer catalogue to ordinary players");
    }
    public static void Run()
    {
        var a = new BookAddress(Guid.NewGuid(), Guid.NewGuid(), "Home Base", [2, 3, 4, 5, 6, 7]);
        var b = a with
        {
            Location = Guid.NewGuid(),
            Name = "Earth",
            Symbols = [28, 26, 5, 36, 11, 29]
        };
        var other = b with
        {
            Universe = Guid.NewGuid()
        };
        var book = new AddressBookState();
        Verify.Check(book.Visit(a) && book.Selection(a.Universe)?.Location == a.Location, "first visit selects current address");
        Verify.Check(book.Visit(b) && book.Selection(a.Universe)?.Location == a.Location, "new visit preserves pinned selection");
        Verify.Check(book.History(a.Universe).First().Location == b.Location, "latest visit sorts first");
        book.Select(b);
        book.Visit(a);
        Verify.Check(book.Selection(a.Universe)?.Location == b.Location, "returning home preserves selected destination");
        Verify.Check(!book.Visit(a) && book.History(a.Universe).Count() == 2, "repeated frames do not duplicate history");
        book.Visit(other);
        Verify.Check(book.History(other.Universe).Count() == 1 && book.History(a.Universe).Count() == 2, "different universes have isolated history");
        Verify.Check(!book.Visit(a with
        {
            Symbols = [1, 2, 3, 4, 5, 6]
        }) && !book.Visit(a with
        {
            Symbols = [2, 2, 3, 4, 5, 6]
        }), "origin and repeated destination glyphs are rejected");
        string folder = Path.Combine(Path.GetTempPath(), "stargate-address-book-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "addresses.json");
        try
        {
            book.Save(path);
            var loaded = AddressBookState.Load(path);
            Verify.Check(loaded.Selection(a.Universe)?.Location == b.Location && loaded.History(a.Universe).Count() == 2, "history and selection survive restart");
            Verify.Check(loaded.Selection(a.Universe)!.Symbols.SequenceEqual(b.Symbols), "saved glyph order is retained");
            File.WriteAllText(path, "{broken");
            Verify.Check(AddressBookState.Load(path).Entries.Count == 0, "corrupt book does not break joining");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            Directory.Delete(folder);
        }
    }
}
