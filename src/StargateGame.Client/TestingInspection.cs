using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    StaticEntity[] testingInspection = [];
    public void SetTestingInspection(bool enabled)
    {
        testingInspection = enabled ? [
            new("inspection-rings", "inspection-transport-rings", new Vector3(34, 0, 10)),
            new("inspection-zpm", "inspection-zpm-hub", new Vector3(40, 0, 10))
        ] : [];
        foreach (var entity in testingInspection) RequestModel(entity.Model);
    }
}
