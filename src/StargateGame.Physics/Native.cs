using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Numerics;
using JoltPhysicsSharp;

namespace StargateGame.Physics;

internal static class Native
{
    // JoltPhysicsSharp 2.23.0 constructors that assign Handle directly leave
    // OwnsHandle false. Set ownership explicitly for objects created by this game.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_OwnsHandle")]
    static extern void SetOwnership(NativeObject instance, bool owned);
    internal static T Own<T>(T instance) where T : NativeObject
    {
        SetOwnership(instance, true);
        return instance;
    }
    internal static void TransferOwnership(NativeObject instance) => SetOwnership(instance, false);
    static readonly object Gate = new();
    static bool initialized;
    public static void Initialize()
    {
        lock (Gate)
        {
            if (initialized)
                return;
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("Jolt requires an x64 process.");
            try
            {
                if (SG_ABIVersion() != 2)
                    throw new InvalidOperationException("Incorrect StargateGame Jolt runtime. Rebuild client, server and the native bridge together for tactical movement.");
                using (var source = typeof(Native).Assembly.GetManifestResourceStream("SimulationSource.rollback.inl")!)
                using (var reader = new StreamReader(source))
                {
                    string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reader.ReadToEnd().Replace("\r\n", "\n"))));
                    string actual = Marshal.PtrToStringUTF8(SG_BridgeSourceIdentity()) ?? "unknown";
                    Console.WriteLine($"Native bridge source: loaded={actual} expected={expected}");
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Stale native physics library. Run python tools/build_native.py, then rebuild matching client and server binaries.");
                }
                if (!Foundation.Init())
                    throw new InvalidOperationException("Jolt initialization failed.");
                initialized = true;
                // Process-global state is retained; worlds own all simulation resources.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Foundation.Shutdown();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw new InvalidOperationException("The packaged StargateGame Jolt x64 runtime is missing or stale. Run python tools/build_native.py, then rebuild client and server.", ex);
            }
        }
    }
    // The pinned managed wrapper fails this call for our float-position native ABI.
    // Declare the C API directly; the swimming regression verifies an actual submerged body.
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl, EntryPoint = "JPH_BodyInterface_ApplyBuoyancyImpulse")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool Buoyancy(nint bodyInterface, BodyID id, in Vector3 surface, in Vector3 normal, float buoyancy, float linearDrag, float angularDrag, in Vector3 flow, in Vector3 gravity, float dt);
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)] static extern uint SG_ABIVersion();
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)] static extern nint SG_BridgeSourceIdentity();
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SG_CharacterShape(nint character, nint shape, nint system, nint allocator, uint layer, [MarshalAs(UnmanagedType.I1)] bool checkClearance);
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)] internal static extern nint SG_Save(nint system, nint[] characters, uint count);
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SG_Restore(nint state, nint system, nint[] characters, uint count);
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)] internal static extern void SG_Free(nint state);
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)] internal static extern nuint SG_Size(nint state);
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)] internal static extern nint SG_AllocatorCreate();
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)] internal static extern void SG_AllocatorDestroy(nint allocator);
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)] internal static extern uint SG_Step(nint system, float dt, nint allocator, nint jobs);
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)] internal static extern void SG_CharacterStep(nint character, nint system, nint allocator, float dt, uint layer, [MarshalAs(UnmanagedType.I1)] bool suppressStepping);
    [DllImport("joltc", CallingConvention = CallingConvention.Cdecl)] internal static extern void SG_Refresh(nint character, nint system, nint allocator, uint layer);
}
