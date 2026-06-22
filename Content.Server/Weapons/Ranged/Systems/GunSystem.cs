using System.Linq;
using System.Numerics;
using Content.Server.Cargo.Systems;
using Content.Shared.Contests;
using Content.Server.Power.EntitySystems;
using Content.Server.Weapons.Ranged.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Database;
using Content.Shared.Effects;
using Content.Shared.Projectiles;
using Content.Shared._Misfits.Special;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Weapons.Reflect;
using Content.Shared.Damage.Components;
using Content.Shared._Misfits.Weapons; // #Misfits Add - GunDamageBonusComponent support
using Content.Server.Weapons.Ranged.Events;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using Robust.Shared.Containers;

namespace Content.Server.Weapons.Ranged.Systems;

public sealed partial class GunSystem : SharedGunSystem
{
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly BatterySystem _battery = default!;
    [Dependency] private readonly DamageExamineSystem _damageExamine = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly SharedColorFlashEffectSystem _color = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly StaminaSystem _stamina = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly ContestsSystem _contests = default!;

    private const float DamagePitchVariation = 0.05f;
    private const float MountedHitscanVisualMaxLeadSeconds = 0.45f;
    private const float MountedHitscanVisualMaxLeadTiles = 3.5f;
    private const float MountedHitscanVisualHitPaddingTiles = 0.25f;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BallisticAmmoProviderComponent, PriceCalculationEvent>(OnBallisticPrice);
    }

    private void OnBallisticPrice(EntityUid uid, BallisticAmmoProviderComponent component, ref PriceCalculationEvent args)
    {
        if (string.IsNullOrEmpty(component.Proto) || component.UnspawnedCount == 0)
            return;

        if (!ProtoManager.TryIndex<EntityPrototype>(component.Proto, out var proto))
        {
            Log.Error($"Unable to find fill prototype for price on {component.Proto} on {ToPrettyString(uid)}");
            return;
        }

        // Probably good enough for most.
        var price = _pricing.GetEstimatedPrice(proto);
        args.Price += price * component.UnspawnedCount;
    }

    public override void Shoot(EntityUid gunUid, GunComponent gun, List<(EntityUid? Entity, IShootable Shootable)> ammo,
        EntityCoordinates fromCoordinates, EntityCoordinates toCoordinates, out bool userImpulse, EntityUid? user = null, bool throwItems = false)
    {
        userImpulse = true;

        if (user != null)
        {
            var selfEvent = new SelfBeforeGunShotEvent(user.Value, (gunUid, gun), ammo);
            RaiseLocalEvent(user.Value, selfEvent);
            if (selfEvent.Cancelled)
            {
                userImpulse = false;
                return;
            }
        }

        var fromMap = fromCoordinates.ToMap(EntityManager, TransformSystem);
        var toMap = toCoordinates.ToMapPos(EntityManager, TransformSystem);
        var mapDirection = toMap - fromMap.Position;
        var mapAngle = mapDirection.ToAngle();
        var angle = GetRecoilAngle(Timing.CurTime, gun, mapDirection.ToAngle(), user);

        // If applicable, this ensures the projectile is parented to grid on spawn, instead of the map.
        var fromEnt = MapManager.TryFindGridAt(fromMap, out var gridUid, out var grid)
            ? fromCoordinates.WithEntityId(gridUid, EntityManager)
            : new EntityCoordinates(MapManager.GetMapEntityId(fromMap.MapId), fromMap.Position);

        // Update shot based on the recoil
        toMap = fromMap.Position + angle.ToVec() * mapDirection.Length();
        mapDirection = toMap - fromMap.Position;
        var gunVelocity = GetShooterMapLinearVelocity(user, fromEnt);

        // I must be high because this was getting tripped even when true.
        // DebugTools.Assert(direction != Vector2.Zero);
        var shotProjectiles = new List<EntityUid>(ammo.Count);

        foreach (var (ent, shootable) in ammo)
        {
            // pneumatic cannon doesn't shoot bullets it just throws them, ignore ammo handling
            if (throwItems && ent != null)
            {
                ShootOrThrow(ent.Value, mapDirection, gunVelocity, gun, gunUid, user);
                continue;
            }

            switch (shootable)
            {
                // Cartridge shoots something else
                case CartridgeAmmoComponent cartridge:
                    if (!cartridge.Spent)
                    {
                        var uid = Spawn(cartridge.Prototype, fromEnt);
                        CreateAndFireProjectiles(uid, cartridge);

                        RaiseLocalEvent(ent!.Value, new AmmoShotEvent()
                        {
                            FiredProjectiles = shotProjectiles,
                        });

                        SetCartridgeSpent(ent.Value, cartridge, true);

                        if (cartridge.DeleteOnSpawn)
                            Del(ent.Value);
                    }
                    else
                    {
                        userImpulse = false;
                        Audio.PlayPredicted(gun.SoundEmpty, gunUid, user);
                    }

                    // Something like ballistic might want to leave it in the container still
                    if (!cartridge.DeleteOnSpawn && !Containers.IsEntityInContainer(ent!.Value))
                        EjectCartridge(ent.Value, angle);

                    Dirty(ent!.Value, cartridge);
                    break;
                // Ammo shoots itself
                case AmmoComponent newAmmo:
                    if (ent == null)
                        break;
                    CreateAndFireProjectiles(ent.Value, newAmmo);

                    break;
                case HitscanPrototype hitscan:

                    // #Misfits Add - Override hitscan proto if gun specifies one.
                    // This lets weapons control their own beam color and base damage
                    // independently of which cell is inserted.
                    if (TryComp<GunDamageBonusComponent>(gunUid, out var gunOverride) &&
                        gunOverride.HitscanProtoOverride != null)
                    {
                        hitscan = ProtoManager.Index<HitscanPrototype>(gunOverride.HitscanProtoOverride);
                    }

                    EntityUid? lastHit = null;

                    var from = fromMap;
                    // can't use map coords above because funny FireEffects
                    var fromEffect = fromCoordinates;
                    var dir = mapDirection.Normalized();
                    var visualOriginLead = GetMountedHitscanVisualLead(user);

                    //in the situation when user == null, means that the cannon fires on its own (via signals). And we need the gun to not fire by itself in this case
                    var lastUser = user ?? gunUid;
                    var rayExtraIgnore = GetShotExtraIgnoredEntity(user);

                    if (hitscan.Reflective != ReflectType.None)
                    {
                        for (var reflectAttempt = 0; reflectAttempt < 3; reflectAttempt++)
                        {
                            var ray = new CollisionRay(from.Position, dir, hitscan.CollisionMask);
                            var rayCastResults =
                                Physics.IntersectRayWithPredicate(
                                    from.MapId,
                                    ray,
                                    (Source: lastUser, Extra: rayExtraIgnore),
                                    static (hit, ignored) => hit == ignored.Source || hit == ignored.Extra,
                                    hitscan.MaxLength,
                                    false).ToList();
                            if (!rayCastResults.Any())
                                break;

                            var raycastEvent = new HitScanAfterRayCastEvent(rayCastResults);
                            RaiseLocalEvent(lastUser, ref raycastEvent);

                            if (raycastEvent.RayCastResults == null)
                                break;

                            var result = raycastEvent.RayCastResults[0];

                            // Check if laser is shot from in a container
                            if (!_container.IsEntityOrParentInContainer(lastUser))
                            {
                                // Checks if the laser should pass over unless targeted by its user
                                foreach (var collide in rayCastResults)
                                {
                                    if (collide.HitEntity != gun.Target &&
                                        CompOrNull<RequireProjectileTargetComponent>(collide.HitEntity)?.Active == true)
                                    {
                                        continue;
                                    }

                                    result = collide;
                                    break;
                                }
                            }

                            var hit = result.HitEntity;
                            lastHit = hit;

                            FireEffects(fromEffect, result.Distance, dir.Normalized().ToAngle(), hitscan, hit, visualOriginLead);
                            visualOriginLead = default;

                            var ev = new HitScanReflectAttemptEvent(user, gunUid, hitscan.Reflective, dir, false);
                            RaiseLocalEvent(hit, ref ev);

                            if (!ev.Reflected)
                                break;

                            fromEffect = Transform(hit).Coordinates;
                            from = fromEffect.ToMap(EntityManager, _transform);
                            dir = ev.Direction;
                            lastUser = hit;
                        }
                    }

                    if (lastHit != null)
                    {
                        var hitEntity = lastHit.Value;
                        if (hitscan.StaminaDamage > 0f)
                            _stamina.TakeStaminaDamage(hitEntity, hitscan.StaminaDamage, source: user);

                        var dmg = hitscan.Damage;

                        // #Misfits Add - Apply gun-side bonus damage from GunDamageBonusComponent
                        if (dmg != null && TryComp<GunDamageBonusComponent>(gunUid, out var gunBonus) && gunBonus.BonusDamage != null)
                        {
                            dmg = new DamageSpecifier(dmg);
                            dmg += gunBonus.BonusDamage;
                        }

                        if (dmg != null && user != null)
                        {
                            var modifyDamage = new SpecialModifyHitscanDamageEvent(gunUid, dmg);
                            RaiseLocalEvent(user.Value, ref modifyDamage);
                            dmg = modifyDamage.Damage;
                        }

                        var hitName = ToPrettyString(hitEntity);
                        if (dmg != null)
                            dmg = Damageable.TryChangeDamage(hitEntity, dmg, origin: user);

                        // check null again, as TryChangeDamage returns modified damage values
                        if (dmg != null)
                        {
                            if (!Deleted(hitEntity))
                            {
                                if (dmg.AnyPositive())
                                {
                                    _color.RaiseEffect(Color.Red, new List<EntityUid>() { hitEntity }, Filter.Pvs(hitEntity, entityManager: EntityManager));
                                }

                                // TODO get fallback position for playing hit sound.
                                PlayImpactSound(hitEntity, dmg, hitscan.Sound, hitscan.ForceSound);
                            }

                            if (user != null)
                            {
                                Logs.Add(LogType.HitScanHit,
                                    $"{ToPrettyString(user.Value):user} hit {hitName:target} using hitscan and dealt {dmg.GetTotal():damage} damage");
                            }
                            else
                            {
                                Logs.Add(LogType.HitScanHit,
                                    $"{hitName:target} hit by hitscan dealing {dmg.GetTotal():damage} damage");
                            }
                        }
                    }
                    else
                    {
                        FireEffects(fromEffect, hitscan.MaxLength, dir.ToAngle(), hitscan, visualOriginLead: visualOriginLead);
                    }

                    if (lastHit != null && user != null)
                    {
                        var hitEv = new HitscanHitEntityEvent(lastHit.Value);
                        RaiseLocalEvent(user.Value, ref hitEv);
                    }

                    Audio.PlayPredicted(gun.SoundGunshotModified, gunUid, user);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        RaiseLocalEvent(gunUid, new AmmoShotEvent()
        {
            FiredProjectiles = shotProjectiles,
        });

        void CreateAndFireProjectiles(EntityUid ammoEnt, AmmoComponent ammoComp)
        {
            if (TryComp<ProjectileSpreadComponent>(ammoEnt, out var ammoSpreadComp))
            {
                var spreadEvent = new GunGetAmmoSpreadEvent(ammoSpreadComp.Spread);
                RaiseLocalEvent(gunUid, ref spreadEvent);

                var angles = LinearSpread(mapAngle - spreadEvent.Spread / 2,
                    mapAngle + spreadEvent.Spread / 2, ammoSpreadComp.Count);

                ShootOrThrow(ammoEnt, angles[0].ToVec(), gunVelocity, gun, gunUid, user);
                shotProjectiles.Add(ammoEnt);

                for (var i = 1; i < ammoSpreadComp.Count; i++)
                {
                    var newuid = Spawn(ammoSpreadComp.Proto, fromEnt);
                    ShootOrThrow(newuid, angles[i].ToVec(), gunVelocity, gun, gunUid, user);
                    shotProjectiles.Add(newuid);
                }
            }
            else
            {
                ShootOrThrow(ammoEnt, mapDirection, gunVelocity, gun, gunUid, user);
                shotProjectiles.Add(ammoEnt);
            }

            MuzzleFlash(gunUid, ammoComp, mapDirection.ToAngle(), user);
            Audio.PlayPredicted(gun.SoundGunshotModified, gunUid, user);
        }
    }

    private void ShootOrThrow(EntityUid uid, Vector2 mapDirection, Vector2 gunVelocity, GunComponent gun, EntityUid gunUid, EntityUid? user)
    {
        if (gun.Target is { } target && !TerminatingOrDeleted(target))
        {
            var targeted = EnsureComp<TargetedProjectileComponent>(uid);
            targeted.Target = target;
            Dirty(uid, targeted);
        }

        // Do a throw
        if (!HasComp<ProjectileComponent>(uid))
        {
            RemoveShootable(uid);
            // TODO: Someone can probably yeet this a billion miles so need to pre-validate input somewhere up the call stack.
            ThrowingSystem.TryThrow(uid, mapDirection, gun.ProjectileSpeedModified, user);
            return;
        }

        ShootProjectile(uid, mapDirection, gunVelocity, gunUid, user, gun.ProjectileSpeedModified);
    }

    /// <summary>
    /// Gets a linear spread of angles between start and end.
    /// </summary>
    /// <param name="start">Start angle in degrees</param>
    /// <param name="end">End angle in degrees</param>
    /// <param name="intervals">How many shots there are</param>
    private Angle[] LinearSpread(Angle start, Angle end, int intervals)
    {
        var angles = new Angle[intervals];
        DebugTools.Assert(intervals > 1);

        for (var i = 0; i <= intervals - 1; i++)
        {
            angles[i] = new Angle(start + (end - start) * i / (intervals - 1));
        }

        return angles;
    }

    private Angle GetRecoilAngle(TimeSpan curTime, GunComponent component, Angle direction, EntityUid? user)
    {
        var timeSinceLastFire = (curTime - component.LastFire).TotalSeconds;
        var newTheta = MathHelper.Clamp(component.CurrentAngle.Theta + component.AngleIncreaseModified.Theta - component.AngleDecayModified.Theta * timeSinceLastFire, component.MinAngleModified.Theta, component.MaxAngleModified.Theta);
        component.CurrentAngle = new Angle(newTheta);
        component.LastFire = component.NextFire;

        // Convert it so angle can go either side.
        var random = Random.NextFloat(-0.5f, 0.5f) / _contests.MassContest(user);
        var spread = component.CurrentAngle.Theta * random;
        var angle = new Angle(direction.Theta + component.CurrentAngle.Theta * random);
        DebugTools.Assert(spread <= component.MaxAngleModified.Theta);
        return angle;
    }

    protected override void Popup(string message, EntityUid? uid, EntityUid? user) { }

    protected override float GetMountedShotOriginLeadSeconds(RequestShootEvent msg, EntitySessionEventArgs args)
    {
        var pingSeconds = Math.Max(0f, args.SenderSession.Ping / 1000f);
        var leadSeconds = pingSeconds + MountedShotOriginLeadCushionSeconds;
        return Math.Clamp(leadSeconds, 0f, MountedShotOriginMaxLeadSeconds);
    }

    protected override void CreateEffect(EntityUid gunUid, MuzzleFlashEvent message, EntityUid? user = null)
    {
        var filter = Filter.Pvs(gunUid, entityManager: EntityManager);

        if (TryComp<ActorComponent>(user, out var actor))
            filter.RemovePlayer(actor.PlayerSession);

        RaiseNetworkEvent(message, filter);
    }

    public void PlayImpactSound(EntityUid otherEntity, DamageSpecifier? modifiedDamage, SoundSpecifier? weaponSound, bool forceWeaponSound)
    {
        DebugTools.Assert(!Deleted(otherEntity), "Impact sound entity was deleted");

        // Like projectiles and melee,
        // 1. Entity specific sound
        // 2. Ammo's sound
        // 3. Nothing
        var playedSound = false;

        if (!forceWeaponSound && modifiedDamage != null && modifiedDamage.GetTotal() > 0 && TryComp<RangedDamageSoundComponent>(otherEntity, out var rangedSound))
        {
            var type = SharedMeleeWeaponSystem.GetHighestDamageSound(modifiedDamage, ProtoManager);

            if (type != null && rangedSound.SoundTypes?.TryGetValue(type, out var damageSoundType) == true)
            {
                Audio.PlayPvs(damageSoundType, otherEntity, AudioParams.Default.WithVariation(DamagePitchVariation));
                playedSound = true;
            }
            else if (type != null && rangedSound.SoundGroups?.TryGetValue(type, out var damageSoundGroup) == true)
            {
                Audio.PlayPvs(damageSoundGroup, otherEntity, AudioParams.Default.WithVariation(DamagePitchVariation));
                playedSound = true;
            }
        }

        if (!playedSound && weaponSound != null)
        {
            Audio.PlayPvs(weaponSound, otherEntity);
        }
    }

    // TODO: Pseudo RNG so the client can predict these.
    #region Hitscan effects

    private Vector2 GetMountedHitscanVisualLead(EntityUid? user)
    {
        if (user is not { } shooter)
            return Vector2.Zero;

        if (!TryGetMovingBuckledParent(shooter, out var parent) ||
            !TryComp<ActorComponent>(shooter, out var actor))
        {
            return Vector2.Zero;
        }

        var velocity = Physics.GetMapLinearVelocity(parent.Value);
        if (velocity.LengthSquared() == 0f)
            return Vector2.Zero;

        // Visual-only latency cover for mounted hitscans. The authoritative ray origin
        // remains capped in SharedGunSystem; this only moves the beam effect toward the
        // rider's current client-visible bike position at high ping.
        var leadSeconds = Math.Clamp(
            actor.PlayerSession.Ping / 1000f + MountedShotOriginLeadCushionSeconds,
            0f,
            MountedHitscanVisualMaxLeadSeconds);

        var lead = velocity * leadSeconds;
        var leadLength = lead.Length();

        if (leadLength > MountedHitscanVisualMaxLeadTiles)
            lead = lead / leadLength * MountedHitscanVisualMaxLeadTiles;

        return lead;
    }

    private void FireEffects(EntityCoordinates fromCoordinates, float distance, Angle mapDirection, HitscanPrototype hitscan, EntityUid? hitEntity = null, Vector2 visualOriginLead = default)
    {
        // Lord
        // Forgive me for the shitcode I am about to do
        // Effects tempt me not
        var sprites = new List<(NetCoordinates coordinates, Angle angle, SpriteSpecifier sprite, float scale)>();
        var fromMap = fromCoordinates.ToMap(EntityManager, TransformSystem);
        var direction = mapDirection.ToVec().Normalized();
        var visualLead = visualOriginLead;
        if (hitEntity != null)
        {
            var maxHitLead = Math.Max(0f, distance - MountedHitscanVisualHitPaddingTiles);
            var hitLeadLength = visualLead.Length();

            if (hitLeadLength > maxHitLead)
                visualLead = hitLeadLength > 0f ? visualLead / hitLeadLength * maxHitLead : Vector2.Zero;
        }

        var visualStart = fromMap.Position + visualLead;
        var visualEnd = fromMap.Position + direction * distance;

        if (hitEntity == null)
            visualEnd += visualLead;

        var visualDirection = visualEnd - visualStart;
        var visualDistance = visualDirection.Length();

        if (visualDistance <= 0f)
            return;

        var angle = visualDirection.ToAngle();
        var effectCoordinates = TransformSystem.ToCoordinates(new MapCoordinates(visualStart, fromMap.MapId));

        if (visualDistance >= 1f)
        {
            if (hitscan.MuzzleFlash != null)
            {
                var coords = effectCoordinates.Offset(angle.ToVec().Normalized() / 2);
                var netCoords = GetNetCoordinates(coords);

                sprites.Add((netCoords, angle, hitscan.MuzzleFlash, 1f));
            }

            if (hitscan.TravelFlash != null)
            {
                var coords = effectCoordinates.Offset(angle.ToVec() * (visualDistance + 0.5f) / 2);
                var netCoords = GetNetCoordinates(coords);

                sprites.Add((netCoords, angle, hitscan.TravelFlash, visualDistance - 1.5f));
            }
        }

        if (hitscan.ImpactFlash != null)
        {
            var coords = effectCoordinates.Offset(angle.ToVec() * visualDistance);
            var netCoords = GetNetCoordinates(coords);

            sprites.Add((netCoords, angle.FlipPositive(), hitscan.ImpactFlash, 1f));
        }

        if (sprites.Count > 0)
        {
            RaiseNetworkEvent(new HitscanEvent
            {
                Sprites = sprites,
                TintColor = hitscan.TintColor, // #Misfits Add: forward beam customisation
                BeamWidth = hitscan.BeamWidth,
                BeamDuration = hitscan.BeamDuration,
            }, Filter.Pvs(effectCoordinates, entityMan: EntityManager));
        }
    }

    #endregion
}
