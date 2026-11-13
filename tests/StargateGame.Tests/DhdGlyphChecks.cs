using StargateGame.Client;
using StargateGame.Shared;
using System.Numerics;

static class DhdGlyphChecks
{
    static void ConsoleFocus()
    {
        var dhd = new StaticEntity("test", "dhd", Vector3.Zero);
        Vector3 eye = new(0, 1.7f, 1.8f);
        int keys = 0, gaps = 0;
        for (int z = -6; z <= 6; z++) for (int x = -8; x <= 8; x++)
        {
            var aim = Vector3.Normalize(new Vector3(x * .1f, .95f, z * .1f) - eye);
            var whole = DhdInteraction.PickConsole(dhd, eye, aim);
            var button = DhdInteraction.Pick(dhd, eye, aim);
            if (whole is not { } hit) continue;
            if (hit.Key >= 0) { keys++; Verify.Check(button == whole, "console focus retains exact button picking"); }
            else { gaps++; Verify.Check(button is null, "console gap keeps focus without becoming a button"); }
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f);
            var transformed = dhd with { Position = new(8, 2, -5), Rotation = rotation, Scale = .8f };
            var rotated = DhdInteraction.PickConsole(transformed, Vector3.Transform(eye * .8f, rotation) + transformed.Position, Vector3.Transform(aim, rotation));
            Verify.Check(rotated is { } h && h.Key == hit.Key && Math.Abs(h.Distance - hit.Distance * .8f) < .0001f,
                "console focus follows translated rotated scaled DHD");
        }
        Verify.Check(keys > 0 && gaps > 0, "panel sweep covers buttons and gaps");
        Verify.Check(DhdInteraction.PickConsole(dhd, eye, Vector3.UnitZ) is null, "looking away releases console focus");
        Verify.Check(DhdInteraction.PickConsole(dhd, new(0, 6, 0), -Vector3.UnitY) is null, "distant console does not hide weapon");
        Verify.Check(DhdInteraction.PickConsole(dhd, eye, new(float.NaN, 0, 0)) is null, "invalid console aim is rejected");
    }
    public static void Run()
    {
        ConsoleFocus();
        var hint = new DhdAddressHint();
        var world = Guid.NewGuid();
        int[] address = [2, 6, 9, 17, 26, 39];
        hint.Sample(world, address, 10);
        for (int i = 0; i < 6; i++)
        {
            var pulse = hint.Sample(world, address, 10 + (i + 1.5) * DhdAddressHint.KeySeconds);
            Verify.Check(pulse.Key == address[i] && pulse.Strength > .99f, "address hint follows selected order " + i);
        }
        var chain = hint.Sample(world, address, 12.625);
        Verify.Check(address.Count(k => chain.ForKey(k) > 0) == 3 && chain.ForKey(9) > .99f && Math.Abs(chain.ForKey(6) - .25f) < .001f && Math.Abs(chain.ForKey(17) - .25f) < .001f, "three-symbol chain has a bright centre and fading neighbours");
        Verify.Check(hint.Sample(world, address, 17).Strength == 0, "address hint pauses after the sixth symbol");
        Verify.Check(hint.Sample(world, address, 20.125).Key == 2 && hint.Sample(world, address, 20.125).Strength > .99f, "address hint repeats after three-second pause");
        Verify.Check(hint.Sample(Guid.NewGuid(), address, 21).Strength == 0, "location change restarts hint smoothly");
        Verify.Check(hint.Sample(world, [], 22).Strength == 0, "no selected address means no hint");
        Verify.Check(hint.Sample(world, address.Reverse().ToArray(), 23).Strength == 0 && hint.Sample(world, address.Reverse().ToArray(), 24.125).Key == 39, "selection change restarts hint in new order");
        var state = new DhdGlyphState();
        Verify.Check(!state.IsOrbLit("a") && !state.AnyEmission("a"), "DHD state defaults to no orb emission");
        state.SetOrb("a", true);
        Verify.Check(state.IsOrbLit("a") && state.AnyEmission("a") && !state.AnyLit("a") && !state.IsOrbLit("b"), "DHD orb is independent of glyphs and other DHDs");
        state.SetGlyphs("a", [2]);
        state.SetGlyphs("a", []);
        Verify.Check(state.IsOrbLit("a"), "DHD glyph replacement leaves orb state intact");
        state.SetOrb("a", false);
        Verify.Check(!state.AnyEmission("a"), "DHD orb switches off independently");
        state.OrbEnabledByDefault = true;
        Verify.Check(!state.IsOrbLit("a") && state.IsOrbLit("b"), "Explicit orb off overrides prototype default");
        state.Clear("a");
        Verify.Check(state.IsOrbLit("a"), "World reset restores configured orb default");
        state.SetOrb("a", false);
        state.Clear();
        Verify.Check(state.IsOrbLit("a"), "Global clear removes orb overrides");
        state.OrbEnabledByDefault = false;
        Verify.Check(!state.AnyLit("a"), "DHD defaults inactive");
        state.SetGlyph("a", 1, true);
        state.SetGlyph("a", 39, true);
        Verify.Check(state.IsLit("a", 1) && state.IsLit("a", 39) && !state.IsLit("a", 2), "DHD lights exact source keys including key 39");
        Verify.Check(!state.AnyLit("b"), "DHD entities have separate lighting state");
        state.SetGlyph("a", 1, false);
        Verify.Check(!state.IsLit("a", 1) && state.IsLit("a", 39), "DHD key can extinguish without clearing others");
        state.SetGlyphs("a", [2, 6, 2]);
        Verify.Check(state.IsLit("a", 2) && state.IsLit("a", 6) && !state.IsLit("a", 39), "DHD sequence replacement is idempotent");
        foreach (int key in new[] { 0, 13, 40, -1 })
        {
            bool rejected = false;
            try
            {
                state.SetGlyphs("a", [3, key]);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected = true;
            }
            Verify.Check(rejected && state.IsLit("a", 2) && !state.IsLit("a", 3), $"DHD invalid key {key} rejected atomically");
        }
        state.SetGlyph("b", 7, true);
        state.Clear("a");
        Verify.Check(!state.AnyLit("a") && state.IsLit("b", 7), "DHD per-entity clear");
        state.Clear();
        Verify.Check(!state.AnyLit("b"), "DHD clears across world/reconnect reset");
        Verify.Check(DhdGlyphState.ParsePreview("1, 6,39,1").SequenceEqual(new[] { 1, 6, 39 }) && DhdGlyphState.ParsePreview("off").Length == 0, "DHD preview parsing preserves source IDs");
    }
}
