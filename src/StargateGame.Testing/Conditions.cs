using System.Text.Json;

namespace StargateGame.Testing;

public static class Conditions
{
    public static bool Matches(JsonElement state, JsonElement condition, out object evidence)
    {
        string path = condition.GetProperty("path").GetString()!;
        JsonElement actual = state; bool found = true;
        foreach (string part in path.Split('.'))
        {
            if (actual.ValueKind == JsonValueKind.Object && actual.TryGetProperty(part, out var field)) actual = field;
            else if (actual.ValueKind == JsonValueKind.Array && int.TryParse(part, out int index) && index >= 0 && index < actual.GetArrayLength()) actual = actual[index];
            else { found = false; break; }
        }
        evidence = new { path, found, actual = found ? actual : TestJson.Element(null), expected = condition };
        if (condition.TryGetProperty("present", out var present)) return found == present.GetBoolean();
        if (!found) return false;
        if (condition.TryGetProperty("equals", out var equal)) return JsonElement.DeepEquals(actual, equal);
        if (condition.TryGetProperty("near", out var near)) return actual.ValueKind == JsonValueKind.Number && Math.Abs(actual.GetDouble() - near.GetDouble()) <= condition.GetProperty("tolerance").GetDouble();
        if (condition.TryGetProperty("position", out var position))
        {
            double squared = new[] { "x", "y", "z" }.Sum(k => Math.Pow(actual.GetProperty(k).GetDouble() - position.GetProperty(k).GetDouble(), 2));
            return squared <= Math.Pow(condition.GetProperty("distance").GetDouble(), 2);
        }
        throw new ArgumentException("Condition requires equals, near/tolerance, present, or position/distance.");
    }
}
