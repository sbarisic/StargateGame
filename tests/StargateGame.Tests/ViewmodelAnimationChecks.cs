using System.Numerics;
using StargateGame.Client;

static class ViewmodelAnimationChecks
{
    public static void Run()
    {
        var idle = new AnimationPose(Vector3.One, Quaternion.Identity, new(.09f, -.25f, -.158f));
        var walk = idle with { Translation = idle.Translation + new Vector3(0, 0, .11146f) };
        AnimationPose[] poses = [walk, idle, walk with { Translation = walk.Translation + new Vector3(.004f, .002f, 0) }, idle];
        var motion = poses[2].Translation - poses[0].Translation;
        AnimationPose.AlignOrigins(poses, [-1, 0], 0, idle.Translation);
        Verify.Check(Vector3.Distance(poses[0].Translation, idle.Translation) < 1e-6 && poses[1] == idle,
            "viewmodel origin correction aligns root without changing child-local grip");
        Verify.Check(Vector3.Distance(poses[2].Translation - poses[0].Translation, motion) < 1e-6,
            "viewmodel origin correction preserves locomotion displacement");
        var transition = new AnimationTransition();
        AnimationPose[] output = [idle]; transition.Apply("idle", output, 0);
        output[0] = walk; transition.Apply("walk", output, 1f / 60);
        Verify.Check(output[0] == idle, "walking starts from the last displayed pose");
        for (int i = 0; i < 4; i++) { output[0] = walk; transition.Apply("walk", output, 1f / 60); }
        var interrupted = output[0]; output[0] = idle; transition.Apply("idle", output, 1f / 60);
        Verify.Check(output[0] == interrupted, "rapid movement stop blends from the interrupted pose");
        for (int i = 0; i < 12; i++) { output[0] = idle; transition.Apply("idle", output, 1f / 60); }
        Verify.Check(output[0] == idle && transition.Blend == 1, "idle settles within the bounded transition");
        transition.Reset(); output[0] = walk; transition.Apply("draw", output, 0);
        Verify.Check(output[0] == walk && transition.Blend == 1, "hidden viewmodel resets without blending stale poses");
    }
}
