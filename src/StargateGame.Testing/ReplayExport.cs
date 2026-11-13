using System.Text.Json;
using System.Text.Json.Nodes;

namespace StargateGame.Testing;

public static class ReplayExport
{
    public static JsonElement Selectors(JsonElement state)
    {
        var map = new Dictionary<string, object>();
        foreach (var (collection, identity, discriminator) in new[] { ("props", "persistentId", "kind"), ("pieces", "id", "prefab"), ("structures", "id", "prefab") })
        {
            JsonElement values;
            if (!state.TryGetProperty(collection, out values) && !(state.TryGetProperty("changes", out var changes) && changes.TryGetProperty(collection, out values))) continue;
            if (values.ValueKind != JsonValueKind.Array) continue;
            var counts = new Dictionary<string, int>();
            foreach (var item in values.EnumerateArray())
            {
                if (!item.TryGetProperty(identity, out var id)) continue;
                string kind = item.TryGetProperty(discriminator, out var d) ? d.ToString() : "";
                int index = counts.GetValueOrDefault(kind); counts[kind] = index + 1;
                map[id.ToString()] = new { collection, identity, discriminator, kind, index };
            }
        }
        if (state.TryGetProperty("npcs", out var npcs) && npcs.ValueKind == JsonValueKind.Object && npcs.TryGetProperty("records", out var records))
            foreach (var (item, index) in records.EnumerateArray().Select((item, index) => (item, index))) map[item.GetProperty("id").GetString()!] = new { collection = "npcs.records", identity = "id", discriminator = "definition", kind = item.GetProperty("definition").GetString(), index };
        return TestJson.Element(map);
    }
    public static JsonElement Transform(JsonElement data, JsonElement selectors, bool resolve, JsonElement state = default)
    {
        JsonNode? Visit(JsonNode? node, string key = "")
        {
            if (resolve && node is JsonObject obj && obj["$select"] is JsonObject selection)
            {
                var current = state;
                foreach (string part in selection["collection"]!.GetValue<string>().Split('.'))
                {
                    if (!current.TryGetProperty(part, out var child)) child = current.GetProperty("changes").GetProperty(part);
                    current = child;
                }
                string discriminator = selection["discriminator"]!.GetValue<string>(), kind = selection["kind"]!.GetValue<string>();
                var items = current.EnumerateArray().Where(i => (i.TryGetProperty(discriminator, out var d) ? d.ToString() : "") == kind).ToArray();
                int index = selection["index"]!.GetValue<int>();
                if (index < 0 || index >= items.Length) throw new InvalidOperationException("Replay selector no longer resolves: " + selection);
                return JsonNode.Parse(items[index].GetProperty(selection["identity"]!.GetValue<string>()).GetRawText());
            }
            if (node is JsonObject o) { var output = new JsonObject(); foreach (var p in o) output[p.Key] = Visit(p.Value, p.Key); return output; }
            if (node is JsonArray a) return new JsonArray(a.Select(n => Visit(n)).ToArray());
            if (!resolve && node is JsonValue v && v.TryGetValue<string>(out var text) && selectors.TryGetProperty(text, out var selector)) return new JsonObject { ["$select"] = JsonNode.Parse(selector.GetRawText()) };
            if (!resolve && node is JsonValue id && id.TryGetValue<string>(out var value) && Guid.TryParse(value, out var guid) && guid != Guid.Empty && key is not ("requestId" or "request"))
                throw new InvalidOperationException("Cannot export an unbound generated ID in " + key + "; retain the trace and add a semantic selector.");
            return node?.DeepClone();
        }
        return TestJson.Element(Visit(JsonNode.Parse(data.GetRawText())));
    }
    public static object Export(SessionManifest manifest)
    {
        var steps = new List<RecipeStep>();
        foreach (string line in File.ReadLines(Path.Combine(manifest.Root, "events.jsonl")))
        {
            var data = JsonDocument.Parse(line).RootElement.GetProperty("data");
            if (!data.TryGetProperty("request", out var raw) || data.GetProperty("kind").GetString() != "request") continue;
            var request = raw.Deserialize<TestRequest>(TestJson.Options)!;
            if (request.Action is TestAction.Status or TestAction.Observe or TestAction.Recipe or TestAction.Stop) continue;
            var selectors = data.TryGetProperty("selectors", out var s) ? s : TestJson.Element(new { });
            steps.Add(new(request.Action, Transform(request.Data, selectors, false), request.Client, request.TimeoutSeconds));
        }
        if (steps.Count == 0) throw new InvalidOperationException("No replayable actions recorded.");
        string path = Path.Combine(manifest.Root, "replay.json");
        TestJson.Write(path, new RecipeDefinition("recorded-session", steps.ToArray(), TestJson.Read<LaunchOptions>(Path.Combine(manifest.Root, "launch.json")).Options, true));
        return new { path, steps = steps.Count, resetBefore = true, verification = "execution replay; add explicit assertions" };
    }
}
