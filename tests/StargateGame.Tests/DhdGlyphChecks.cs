using StargateGame.Client;

static class DhdGlyphChecks
{
    public static void Run()
    {
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
