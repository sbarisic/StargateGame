// StargateGame extensions to the pinned joltc translation unit. MIT.
#include <Jolt/Physics/StateRecorderImpl.h>
#include <cfloat>

struct SG_Checkpoint { JPH::StateRecorderImpl data; };
extern "C" {
JPH_CAPI uint32_t SG_ABIVersion() { return 2; }
JPH_CAPI bool SG_CharacterShape(JPH_CharacterVirtual* character, const JPH_Shape* shape,
    JPH_PhysicsSystem* system, void* allocator, uint32_t layer, bool checkClearance) {
    auto* physics = system->physicsSystem;
    auto* c = AsCharacterVirtual(character);
    if (!c->SetShape(AsShape(shape), checkClearance ? 1.5f * physics->GetPhysicsSettings().mPenetrationSlop : FLT_MAX,
        physics->GetDefaultBroadPhaseLayerFilter(layer), physics->GetDefaultLayerFilter(layer), {}, {},
        *static_cast<JPH::TempAllocatorImpl*>(allocator))) return false;
    c->SetInnerBodyShape(AsShape(shape));
    return true;
}
JPH_CAPI SG_Checkpoint* SG_Save(JPH_PhysicsSystem* system, JPH_CharacterVirtual** characters, uint32_t count) {
    auto* result = new SG_Checkpoint();
    system->physicsSystem->SaveState(result->data);
    for (uint32_t i = 0; i < count; ++i) AsCharacterVirtual(characters[i])->SaveState(result->data);
    return result;
}
JPH_CAPI bool SG_Restore(SG_Checkpoint* state, JPH_PhysicsSystem* system, JPH_CharacterVirtual** characters, uint32_t count) {
    state->data.Rewind();
    if (!system->physicsSystem->RestoreState(state->data)) return false;
    for (uint32_t i = 0; i < count; ++i) AsCharacterVirtual(characters[i])->RestoreState(state->data);
    return !state->data.IsFailed();
}
JPH_CAPI void SG_Free(SG_Checkpoint* state) { delete state; }
JPH_CAPI size_t SG_Size(SG_Checkpoint* state) { return state->data.GetDataSize(); }
JPH_CAPI void* SG_AllocatorCreate() { return new JPH::TempAllocatorImpl(16 * 1024 * 1024); }
JPH_CAPI void SG_AllocatorDestroy(void* allocator) { delete static_cast<JPH::TempAllocatorImpl*>(allocator); }
JPH_CAPI uint32_t SG_Step(JPH_PhysicsSystem* system, float dt, void* allocator, JPH_JobSystem* jobs) {
    return static_cast<uint32_t>(system->physicsSystem->Update(dt, 1, static_cast<JPH::TempAllocatorImpl*>(allocator), reinterpret_cast<JPH::JobSystem*>(jobs)));
}
JPH_CAPI void SG_CharacterStep(JPH_CharacterVirtual* character, JPH_PhysicsSystem* system, void* allocator, float dt, uint32_t layer, bool jumping) {
    JPH::CharacterVirtual::ExtendedUpdateSettings settings;
    settings.mWalkStairsStepUp = JPH::Vec3(0, 0.30f, 0);
    settings.mStickToFloorStepDown = jumping ? JPH::Vec3::sZero() : JPH::Vec3(0, -0.35f, 0);
    auto* physics = system->physicsSystem;
    AsCharacterVirtual(character)->ExtendedUpdate(dt, physics->GetGravity(), settings,
        physics->GetDefaultBroadPhaseLayerFilter(layer), physics->GetDefaultLayerFilter(layer), {}, {},
        *static_cast<JPH::TempAllocatorImpl*>(allocator));
}
JPH_CAPI void SG_Refresh(JPH_CharacterVirtual* character, JPH_PhysicsSystem* system, void* allocator, uint32_t layer) {
    auto* physics = system->physicsSystem;
    AsCharacterVirtual(character)->RefreshContacts(physics->GetDefaultBroadPhaseLayerFilter(layer),
        physics->GetDefaultLayerFilter(layer), {}, {}, *static_cast<JPH::TempAllocatorImpl*>(allocator));
}
}
