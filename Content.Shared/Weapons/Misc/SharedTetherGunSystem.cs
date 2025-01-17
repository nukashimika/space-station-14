using System.Diagnostics.CodeAnalysis;
using Content.Shared.ActionBlocker;
using Content.Shared.Buckle.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Events;
using Content.Shared.Throwing;
using Content.Shared.Toggleable;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Serialization;

namespace Content.Shared.Weapons.Misc;

public abstract class SharedTetherGunSystem : EntitySystem
{
    [Dependency] private   readonly INetManager _netManager = default!;
    [Dependency] private   readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private   readonly MobStateSystem _mob = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private   readonly SharedAudioSystem _audio = default!;
    [Dependency] private   readonly SharedJointSystem _joints = default!;
    [Dependency] private   readonly SharedPhysicsSystem _physics = default!;
    [Dependency] protected readonly SharedTransformSystem TransformSystem = default!;
    [Dependency] private   readonly ThrownItemSystem _thrown = default!;

    private const string TetherJoint = "tether";

    private const float SpinVelocity = MathF.PI;
    private const float AngularChange = 1f;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TetherGunComponent, ActivateInWorldEvent>(OnTetherActivate);
        SubscribeLocalEvent<TetherGunComponent, AfterInteractEvent>(OnTetherRanged);
        SubscribeAllEvent<RequestTetherMoveEvent>(OnTetherMove);

        SubscribeLocalEvent<TetheredComponent, BuckleAttemptEvent>(OnTetheredBuckleAttempt);
        SubscribeLocalEvent<TetheredComponent, UpdateCanMoveEvent>(OnTetheredUpdateCanMove);
    }

    private void OnTetheredBuckleAttempt(EntityUid uid, TetheredComponent component, ref BuckleAttemptEvent args)
    {
        args.Cancelled = true;
    }

    private void OnTetheredUpdateCanMove(EntityUid uid, TetheredComponent component, UpdateCanMoveEvent args)
    {
        args.Cancel();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Just to set the angular velocity due to joint funnies
        var tetheredQuery = EntityQueryEnumerator<TetheredComponent, PhysicsComponent>();

        while (tetheredQuery.MoveNext(out var uid, out _, out var physics))
        {
            var sign = Math.Sign(physics.AngularVelocity);

            if (sign == 0)
            {
                sign = 1;
            }

            var targetVelocity = MathF.PI * sign;

            var shortFall = Math.Clamp(targetVelocity - physics.AngularVelocity, -SpinVelocity, SpinVelocity);
            shortFall *= frameTime * AngularChange;

            _physics.ApplyAngularImpulse(uid, shortFall, body: physics);
        }
    }

    private void OnTetherMove(RequestTetherMoveEvent msg, EntitySessionEventArgs args)
    {
        var user = args.SenderSession.AttachedEntity;

        if (user == null)
            return;

        if (!TryGetTetherGun(user.Value, out var gunUid, out var gun) || gun.TetherEntity == null)
        {
            return;
        }

        if (!msg.Coordinates.TryDistance(EntityManager, TransformSystem, Transform(gunUid.Value).Coordinates,
                out var distance) ||
            distance > gun.MaxDistance)
        {
            return;
        }

        TransformSystem.SetCoordinates(gun.TetherEntity.Value, msg.Coordinates);
    }

    private void OnTetherRanged(EntityUid uid, TetherGunComponent component, AfterInteractEvent args)
    {
        if (args.Target == null || args.Handled)
            return;

        TryTether(uid, args.Target.Value, args.User, component);
    }

    protected bool TryGetTetherGun(EntityUid user, [NotNullWhen(true)] out EntityUid? gunUid, [NotNullWhen(true)] out TetherGunComponent? gun)
    {
        gunUid = null;
        gun = null;

        if (!TryComp<HandsComponent>(user, out var hands) ||
            !TryComp(hands.ActiveHandEntity, out gun))
        {
            return false;
        }

        gunUid = hands.ActiveHandEntity.Value;
        return true;
    }

    private void OnTetherActivate(EntityUid uid, TetherGunComponent component, ActivateInWorldEvent args)
    {
        StopTether(uid, component);
    }

    public void TryTether(EntityUid gun, EntityUid target, EntityUid? user, TetherGunComponent? component = null)
    {
        if (!Resolve(gun, ref component))
            return;

        if (!CanTether(gun, component, target, user))
            return;

        StartTether(gun, component, target, user);
    }

    protected virtual bool CanTether(EntityUid uid, TetherGunComponent component, EntityUid target, EntityUid? user)
    {
        if (HasComp<TetheredComponent>(target) || !TryComp<PhysicsComponent>(target, out var physics))
            return false;

        if (physics.BodyType == BodyType.Static && !component.CanUnanchor)
            return false;

        if (physics.Mass > component.MassLimit)
            return false;

        if (!component.CanTetherAlive && _mob.IsAlive(target))
            return false;

        if (TryComp<StrapComponent>(target, out var strap) && strap.BuckledEntities.Count > 0)
            return false;

        return true;
    }

    protected virtual void StartTether(EntityUid gunUid, TetherGunComponent component, EntityUid target, EntityUid? user,
        PhysicsComponent? targetPhysics = null, TransformComponent? targetXform = null)
    {
        if (!Resolve(target, ref targetPhysics, ref targetXform))
            return;

        if (component.Tethered != null)
        {
            StopTether(gunUid, component, true);
        }

        TryComp<AppearanceComponent>(gunUid, out var appearance);
        _appearance.SetData(gunUid, TetherVisualsStatus.Key, true, appearance);
        _appearance.SetData(gunUid, ToggleableLightVisuals.Enabled, true, appearance);

        // Target updates
        TransformSystem.Unanchor(target, targetXform);
        component.Tethered = target;
        var tethered = EnsureComp<TetheredComponent>(target);
        _physics.SetBodyStatus(targetPhysics, BodyStatus.InAir, false);
        _physics.SetSleepingAllowed(target, targetPhysics, false);
        tethered.Tetherer = gunUid;
        tethered.OriginalAngularDamping = targetPhysics.AngularDamping;
        _physics.SetAngularDamping(targetPhysics, 0f);
        _physics.SetLinearDamping(targetPhysics, 0f);
        _physics.SetAngularVelocity(target, SpinVelocity, body: targetPhysics);
        _physics.WakeBody(target, body: targetPhysics);
        var thrown = EnsureComp<ThrownItemComponent>(component.Tethered.Value);
        thrown.Thrower = gunUid;
        _blocker.UpdateCanMove(target);

        // Invisible tether entity
        var tether = Spawn("TetherEntity", Transform(target).MapPosition);
        var tetherPhysics = Comp<PhysicsComponent>(tether);
        component.TetherEntity = tether;
        _physics.WakeBody(tether);

        var joint = _joints.CreateMouseJoint(tether, target, id: TetherJoint);

        SharedJointSystem.LinearStiffness(component.Frequency, component.DampingRatio, tetherPhysics.Mass, targetPhysics.Mass, out var stiffness, out var damping);
        joint.Stiffness = stiffness;
        joint.Damping = damping;
        joint.MaxForce = component.MaxForce;

        // Sad...
        if (_netManager.IsServer && component.Stream == null)
            component.Stream = _audio.PlayPredicted(component.Sound, gunUid, null);

        Dirty(tethered);
        Dirty(component);
    }

    protected virtual void StopTether(EntityUid gunUid, TetherGunComponent component, bool transfer = false)
    {
        if (component.Tethered == null)
            return;

        if (component.TetherEntity != null)
        {
            _joints.RemoveJoint(component.TetherEntity.Value, TetherJoint);

            if (_netManager.IsServer)
                QueueDel(component.TetherEntity.Value);

            component.TetherEntity = null;
        }

        if (TryComp<PhysicsComponent>(component.Tethered, out var targetPhysics))
        {
            var thrown = EnsureComp<ThrownItemComponent>(component.Tethered.Value);
            _thrown.LandComponent(component.Tethered.Value, thrown, targetPhysics);

            _physics.SetBodyStatus(targetPhysics, BodyStatus.OnGround);
            _physics.SetSleepingAllowed(component.Tethered.Value, targetPhysics, true);
            _physics.SetAngularDamping(targetPhysics, Comp<TetheredComponent>(component.Tethered.Value).OriginalAngularDamping);
        }

        if (!transfer)
        {
            component.Stream?.Stop();
            component.Stream = null;
        }

        TryComp<AppearanceComponent>(gunUid, out var appearance);
        _appearance.SetData(gunUid, TetherVisualsStatus.Key, false, appearance);
        _appearance.SetData(gunUid, ToggleableLightVisuals.Enabled, false, appearance);

        RemCompDeferred<TetheredComponent>(component.Tethered.Value);
        _blocker.UpdateCanMove(component.Tethered.Value);
        component.Tethered = null;
        Dirty(component);
    }

    [Serializable, NetSerializable]
    protected sealed class RequestTetherMoveEvent : EntityEventArgs
    {
        public EntityCoordinates Coordinates;
    }

    [Serializable, NetSerializable]
    public enum TetherVisualsStatus : byte
    {
        Key,
    }
}
