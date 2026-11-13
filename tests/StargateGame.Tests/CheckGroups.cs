static class CheckGroups
{
    public static async Task Run(string group, string[] args)
    {
        bool delayed = args.Contains("--delayed");
        switch (group)
        {
            case "npcs": await NpcChecks.Run(); break;
            case "quick": DhdGlyphChecks.Run(); MovementPresentationChecks.Run(); AddressBookChecks.Run(); break;
            case "movement": MovementPresentationChecks.Run(); PhysicsChecks.Run(); await LatencyScenario.Run(); break;
            case "tactical-movement": TacticalMovementChecks.Run(); MovementPresentationChecks.Run(); break;
            case "fixture-gates": await FixtureGateChecks.Run(); break;
            case "character-transit": await CharacterTransitChecks.Run(); break;
            case "travel": TravelChecks.Run(); await TravelChecks.Persistence(); await TravelChecks.IdentityNetwork(); break;
            case "travel-network": await GalaxyChecks.Network(Path.GetFullPath("artifacts/galaxy/galaxy.bin"), delayed, false, true, args.Contains("--cold"), args.Contains("--blocked"), args.Contains("--player-push")); break;
            case "galaxy": await GalaxyChecks.Run(true, delayed, args.Contains("--expiry")); break;
            case "persistence": await PersistenceChecks.Run(); break;
            case "building": await BuildingChecks.Run(); break;
            case "prefab": await PrefabChecks.Run(); break;
            case "doors": PrefabChecks.HingedDoor(); await PrefabChecks.DoorPersistence(); break;
            case "zpm-props": ZpmPropChecks.Run(); break;
            case "rifle": await RifleChecks.Run(); break;
            case "settlements": SettlementChecks.Run(false); await SettlementChecks.Integration(); break;
            case "furnishings": FurnishingChecks.Run(); FurnishingChecks.Physics(); await FurnishingChecks.Persistence(); break;
            case "weapons": await WeaponChecks.Run(); break;
            case "weapon-network": await WeaponNetworkChecks.Run(); break;
            case "interaction": InteractionChecks.Run(); await InteractionChecks.Network(); break;
            case "streaming": await StreamingChecks.Run(args.Contains("--stress"), delayed, args.Contains("--explore"), args.Contains("--slow")); break;
            case "regional": await RegionalChecks.Run(); break;
            case "regional-hydrology": RegionalHydrologyChecks.Run(); break;
            case "regional-seams": RegionalChecks.Seams(); break;
            case "environment": EnvironmentChecks.Run(); break;
            case "atmosphere": await AtmosphereWaterChecks.Run(); break;
            case "horizon": EventHorizonChecks.Run(); break;
            case "address-book": AddressBookChecks.Run(); break;
            case "dhd": DhdGlyphChecks.Run(); break;
            case "trees": TreeChecks.Run(); break;
            case "rocks": RockChecks.Run(); break;
            case "worldgen": await WorldGenChecks.Run(); await RiverChecks.Run(); await TerrainDeliveryChecks.Run(); break;
            case "network": await NetworkChecks.Run(); break;
            case "grass-rendering": GrassRenderingChecks.Run(); break;
            case "canopy-lod": CanopyLodChecks.Run(); break;
            case "planet-debug": await PlanetDebugChecks.Run(); break;
            case "emergence": await EmergenceChecks.Run(); break;
            case "forest-fixture": ForestFixtureChecks.Run(); break;
            case "prop-exit": PropExitChecks.Run(); break;
            case "cooked-assets": await CookedAssetChecks.Run(); break;
            case "gate-physics": GatePhysicsChecks.Run(); await GatePhysicsChecks.Persistence(); break;
            case "palettes": PaletteChecks.Run(); break;
            case "mounted-palettes": PaletteChecks.Mounted(); break;
            case "buried-props": BuriedPropChecks.Run(); break;
            case "terrain-physics": TerrainPhysicsChecks.Run(); break;
            case "regional-integration": await RegionalIntegrationChecks.Run(); break;
            case "regional-profile": await RegionalChecks.Profile(); break;
            case "streaming-cache": await StreamingCacheChecks.Run(); break;
            case "horizon-network": await EventHorizonChecks.Network(); await EventHorizonChecks.PropNetwork(); break;
            case "environment-network": await EnvironmentChecks.Network(); break;
            case "atmosphere-network": await AtmosphereWaterChecks.Network(); break;
            case "audio-events": AudioEventChecks.Run(); break;
            case "gate-transition": GateTransitionChecks.Run(); break;
            case "settlement-network": await SettlementChecks.Network(delayed); break;
            case "prefab-network": await BuildingChecks.Network(delayed, true); break;
            case "gate-build": await BuildingChecks.Network(gateZone: true); break;
            case "prop-support": PropSupportChecks.Run(); break;
            case "meadow-props": MeadowPropChecks.Run(); await MeadowPropChecks.Network(); break;
            case "address-book-network": await AddressBookChecks.Network(); break;
            default: throw new ArgumentException("Unknown test group: " + group);
        }
        Console.WriteLine($"GROUP {group}: {Verify.Count} CHECKS PASSED");
    }
}
