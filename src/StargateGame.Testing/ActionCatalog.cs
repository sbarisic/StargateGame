using System.Text.Json;

namespace StargateGame.Testing;

public sealed record ActionDescriptor(TestAction Action, string Target, string Classification, string Completion,
    string[] Prerequisites, JsonElement Schema, string[] Scenarios, JsonElement Defaults);
public sealed record RecipeDescriptor(string Id, string[] Scenarios, string Verification, string[] Prerequisites, JsonElement Schema);
public static class RecipeCatalog
{
    public static RecipeDescriptor[] All => ScenarioCatalog.Scenarios.SelectMany(s => s.Recipes).Distinct()
        .Select(id => new RecipeDescriptor(id, ScenarioCatalog.Scenarios.Where(s => s.Recipes.Contains(id)).Select(s => s.Id).ToArray(),
            id is "visual-check" or "environment" or "gate-effects" or "lantern-lighting" or "deferred-ao-compare" ? "evidence-only" : "assertions",
            ["playable", "rendered-client"], TestJson.Element(new { type = "object", properties = new { }, additionalProperties = false }))).ToArray();
}

public static class ActionCatalog
{
    static readonly HashSet<TestAction> Setup = [TestAction.Teleport, TestAction.SetupProp, TestAction.ResetPlayer, TestAction.ResetStation, TestAction.ResetScenario, TestAction.SwitchScenario, TestAction.Relocate, TestAction.PrepareDestination];
    static object Field(string type) => type.StartsWith("nullable-", StringComparison.Ordinal) ? new { anyOf = new[] { Field(type[9..]), Field("null") } } : type switch
    {
        "vector" => new { type = "object", properties = new { x = new { type = "number" }, y = new { type = "number" }, z = new { type = "number" } }, required = new[] { "x", "y", "z" }, additionalProperties = false },
        "buildCommand" => Schema("requestId:string revision:integer action:string kind:string target:string socket:integer anchor:vector rotation:integer category:string sceneryId:integer chunk:chunk prefab:string hitPoint:nullable-vector", "action"),
        "npcCommand" => Schema("request:string action:string target:string order:nullable-npcOrder origin:vector point:vector cellSize:number hostilePlayer:integer", "action"),
        "npcOrder" => Schema("kind:string points:vectors followPlayer:integer", "kind", "points"),
        "chunk" => Schema("x:integer z:integer", "x", "z"),
        "vectors" => new { type = "array", items = Field("vector"), maxItems = 64 },
        "strings" => new { type = "array", items = new { type = "string" }, maxItems = 64 },
        "address" => new { type = "array", items = new { type = "integer" }, minItems = 6, maxItems = 6 },
        "any" => new { },
        _ => new { type }
    };
    static JsonElement Schema(string fields, params string[] required)
    {
        var properties = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Split(':')).ToDictionary(f => f[0], f => Field(f[1]));
        return TestJson.Element(new { type = "object", properties, required, additionalProperties = false });
    }
    static JsonElement Shape(TestAction action) => action switch
    {
        TestAction.Observe => Schema("sections:strings offset:integer limit:integer filter:string radius:number pickX:number pickY:number"),
        TestAction.Camera => Schema("bookmark:string eye:vector target:vector"),
        TestAction.Aim => Schema("target:vector entity:string"),
        TestAction.Move => Schema("seconds:number forward:number strafe:number yaw:number pitch:number jump:boolean crouch:boolean walk:boolean", "seconds"),
        TestAction.Input => Schema("seconds:number hold:boolean keys:strings buttons:strings x:number y:number dx:number dy:number scroll:number text:string controlId:string uiRevision:string", "seconds"),
        TestAction.Capture => Schema("name:string includeUi:boolean retain:boolean immediate:boolean"),
        TestAction.Settings => Schema(string.Join(' ', ScenarioCatalog.RenderSettings.Select(n => n + ":" + (n switch { "ao" or "aoView" or "exposure" or "quality" or "weather" or "meadowMaterial" or "propMode" => "string", "hour" or "exposureEv" => "number", "grassDensity" => "integer", "visualTime" => "nullable-number", _ => "boolean" })))),
        TestAction.Teleport => Schema("station:string position:vector yaw:number pitch:number"),
        TestAction.SetupProp => Schema("item:string position:vector", "item", "position"),
        TestAction.ResetStation => Schema("station:string", "station"),
        TestAction.Dial => Schema("dhd:string key:integer action:string", "key"),
        TestAction.Carry => Schema("action:string item:string", "action"),
        TestAction.Weapon => Schema("action:string item:string container:string", "action"),
        TestAction.Build => Schema("command:buildCommand", "command"),
        TestAction.Npc => Schema("command:npcCommand debug:boolean", "command"),
        TestAction.PrepareDestination or TestAction.Relocate => Schema("address:address", "address"),
        TestAction.GatePreview => Schema("phase:string seconds:number"),
        TestAction.AudioRecord => Schema("name:string stop:boolean"),
        TestAction.Sample => Schema("seconds:number"),
        TestAction.Window => Schema("state:string", "state"),
        TestAction.Assert or TestAction.Wait => Schema("condition:string path:string equals:any near:number tolerance:number present:boolean position:vector distance:number"),
        TestAction.Operation => Schema("id:string waitSeconds:integer", "id"),
        TestAction.Cancel => Schema("id:string", "id"),
        TestAction.Recipe => Schema("name:string", "name"),
        TestAction.SwitchScenario => Schema("scenario:string preset:string seed:integer biome:string landscape:string settlement:string style:string hour:number weather:string quality:string cold:boolean delayed:boolean terrainBytesPerSecond:integer destinationDelayMilliseconds:integer overrides:nullable-strings", "scenario"),
        TestAction.Connect => Schema("host:string port:integer options:object generation:integer regionalView:string root:string"),
        _ => Schema("")
    };
    public static ActionDescriptor[] All => Enum.GetValues<TestAction>().Select(a => new ActionDescriptor(a,
        a is TestAction.SwitchScenario or TestAction.ResetScenario or TestAction.AddClient or TestAction.Recipe ? "scenario" : "client",
        Setup.Contains(a) ? "setup" : a is TestAction.Status or TestAction.Observe or TestAction.Capture or TestAction.Operation ? "observation" : "gameplay/control",
        a == TestAction.Capture ? "frame captured; visual correctness not asserted" : a == TestAction.Assert ? "assertion satisfied" : "command completed; applied gameplay effects are not rolled back on cancellation",
        a is TestAction.Status or TestAction.Observe or TestAction.Stop or TestAction.Operation or TestAction.Cancel ? [] : ["playable", "control-granted for attached AI"], Shape(a), ScenarioCatalog.Scenarios.Select(s => s.Id).ToArray(), Defaults(a))).ToArray();
    static JsonElement Defaults(TestAction action) => TestJson.Element(action switch
    {
        TestAction.Observe => (object)new { sections = Array.Empty<string>(), offset = 0, limit = 20, filter = "", radius = 25 },
        TestAction.Move => new { forward = 0, strafe = 0, jump = false, crouch = false, walk = false },
        TestAction.Input => new { keys = Array.Empty<string>(), buttons = Array.Empty<string>(), hold = false, x = .5, y = .5, dx = 0, dy = 0, scroll = 0, text = "" },
        TestAction.Camera => new { bookmark = "player" }, TestAction.Capture => new { name = "capture.png", includeUi = true, retain = true, immediate = false },
        TestAction.Wait => new { condition = "captureReady" }, TestAction.Recipe => new { name = "visual-check" },
        TestAction.Operation => new { waitSeconds = 0 }, TestAction.SwitchScenario => new ScenarioOptions(),
        _ => new { }
    });
    public static void Validate(TestRequest request)
    {
        if (!Enum.IsDefined(request.Action)) throw new ArgumentException("Unsupported action.");
        if (request.TimeoutSeconds is < 1 or > 300) throw new ArgumentException("Timeout must be 1–300 seconds.");
        ValidateValue(request.Data, Shape(request.Action), "data");
    }
    static void ValidateValue(JsonElement value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("anyOf", out var choices))
        {
            foreach (var choice in choices.EnumerateArray()) try { ValidateValue(value, choice, path); return; } catch (ArgumentException) { }
            throw new ArgumentException(path + " does not match an allowed type.");
        }
        if (!schema.TryGetProperty("type", out var t)) return;
        string type = t.GetString()!;
        bool valid = type switch { "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null, "string" => value.ValueKind == JsonValueKind.String, "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.TryGetInt64Safe(), "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n), _ => false };
        if (!valid) throw new ArgumentException(path + " must be " + type);
        if (type == "object" && schema.TryGetProperty("properties", out var properties))
        {
            foreach (var required in schema.GetProperty("required").EnumerateArray()) if (!value.TryGetProperty(required.GetString()!, out _)) throw new ArgumentException(path + "." + required.GetString() + " is required");
            foreach (var property in value.EnumerateObject()) { if (!properties.TryGetProperty(property.Name, out var field)) throw new ArgumentException("Unknown field: " + path + "." + property.Name); ValidateValue(property.Value, field, path + "." + property.Name); }
        }
        if (type == "array")
        {
            if (schema.TryGetProperty("minItems", out var min) && value.GetArrayLength() < min.GetInt32() || schema.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) throw new ArgumentException(path + " array length out of range");
            foreach (var item in value.EnumerateArray()) ValidateValue(item, schema.GetProperty("items"), path + "[]");
        }
    }
    static bool TryGetInt64Safe(this JsonElement value) => value.ValueKind == JsonValueKind.Number && (value.TryGetInt64(out _) || value.TryGetUInt64(out _));
    public static object Discovery() => new { version = 2, scenarios = ScenarioCatalog.Scenarios, presets = ScenarioCatalog.Presets,
        advancedPresets = ScenarioCatalog.AdvancedPresets, defaults = new ScenarioOptions(), actions = All, recipes = RecipeCatalog.All,
        groups = CheckCatalog.Groups, renderingSettings = ScenarioCatalog.RenderSettings, capabilities = new { platform = "Windows x64", typedLocalControl = true, arbitraryCode = false, maxClients = 8, ringsGameplay = false, zpmGameplay = false } };
}
