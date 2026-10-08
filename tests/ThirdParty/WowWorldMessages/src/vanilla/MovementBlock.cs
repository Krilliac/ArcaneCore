using WowSrp.Header;
using WowWorldMessages.All;

namespace WowWorldMessages.Vanilla;

using UpdateFlagLivingMulti = OneOf.OneOf<MovementBlock.UpdateFlagHasPosition, MovementBlock.UpdateFlagLiving, UpdateFlag>;
using SplineFlagFinalAngleMulti = OneOf.OneOf<MovementBlock.SplineFlagFinalAngle, MovementBlock.SplineFlagFinalPoint, MovementBlock.SplineFlagFinalTarget, SplineFlag>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class MovementBlock {
    public class UpdateFlagHasPosition {
        public required float Orientation { get; set; }
        public required Vector3d Position { get; set; }
    }
    public class UpdateFlagLiving {
        public required float BackwardsRunningSpeed { get; set; }
        public required float BackwardsSwimmingSpeed { get; set; }
        public required float FallTime { get; set; }
        public required MovementFlagsType Flags { get; set; }
        public required float LivingOrientation { get; set; }
        public required Vector3d LivingPosition { get; set; }
        public required float RunningSpeed { get; set; }
        public required float SwimmingSpeed { get; set; }
        public required uint Timestamp { get; set; }
        public required float TurnRate { get; set; }
        public required float WalkingSpeed { get; set; }
    }
    public class SplineFlagFinalAngle {
        public required float Angle { get; set; }
    }
    public class SplineFlagFinalPoint {
        public required Vector3d SplineFinalPoint { get; set; }
    }
    public class SplineFlagFinalTarget {
        public required ulong Target { get; set; }
    }
    public class UpdateFlagType {
        public required UpdateFlag Inner;
        public UpdateFlagAll? All;
        public UpdateFlagHighGuid? HighGuid;
        public UpdateFlagLivingMulti Living;
        public UpdateFlagMeleeAttacking? MeleeAttacking;
        public UpdateFlagTransport? Transport;
    }
    public class UpdateFlagAll {
        /// <summary>
        /// vmangos sets statically to 1
        /// </summary>
        public required uint Unknown1 { get; set; }
    }
    public class UpdateFlagHighGuid {
        /// <summary>
        /// vmangos statically sets to 0
        /// </summary>
        public required uint Unknown0 { get; set; }
    }
    public class UpdateFlagMeleeAttacking {
        public required ulong Guid { get; set; }
    }
    public class UpdateFlagTransport {
        public required uint TransportProgressInMs { get; set; }
    }
    public class MovementFlagsType {
        public required MovementFlags Inner;
        public MovementFlagsJumping? Jumping;
        public MovementFlagsOnTransport? OnTransport;
        public MovementFlagsSplineElevation? SplineElevation;
        public MovementFlagsSplineEnabled? SplineEnabled;
        public MovementFlagsSwimming? Swimming;
    }
    public class MovementFlagsJumping {
        public required float CosAngle { get; set; }
        public required float SinAngle { get; set; }
        public required float XySpeed { get; set; }
        public required float ZSpeed { get; set; }
    }
    public class MovementFlagsOnTransport {
        public required ulong TransportGuid { get; set; }
        public required float TransportOrientation { get; set; }
        public required Vector3d TransportPosition { get; set; }
    }
    public class MovementFlagsSplineElevation {
        public required float SplineElevation { get; set; }
    }
    public class MovementFlagsSplineEnabled {
        public required uint Duration { get; set; }
        public required Vector3d FinalNode { get; set; }
        public required uint Id { get; set; }
        public required List<Vector3d> Nodes { get; set; }
        public required SplineFlagFinalAngleMulti SplineFlags { get; set; }
        internal SplineFlag SplineFlagsValue => SplineFlags.Match(
            _ => Vanilla.SplineFlag.FinalAngle,
            _ => Vanilla.SplineFlag.FinalPoint,
            _ => Vanilla.SplineFlag.FinalTarget,
            v => v
        );
        public required uint TimePassed { get; set; }
    }
    public class MovementFlagsSwimming {
        public required float Pitch { get; set; }
    }
    public required UpdateFlagType UpdateFlag { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)UpdateFlag.Inner, cancellationToken).ConfigureAwait(false);

        if (UpdateFlag.Living.Value is UpdateFlagLiving updateFlagLiving) {
            await w.WriteUInt((uint)updateFlagLiving.Flags.Inner, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(updateFlagLiving.Timestamp, cancellationToken).ConfigureAwait(false);

            await updateFlagLiving.LivingPosition.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

            await w.WriteFloat(updateFlagLiving.LivingOrientation, cancellationToken).ConfigureAwait(false);

            if (updateFlagLiving.Flags.OnTransport is {} movementFlagsOnTransport) {
                await w.WritePackedGuid(movementFlagsOnTransport.TransportGuid, cancellationToken).ConfigureAwait(false);

                await movementFlagsOnTransport.TransportPosition.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

                await w.WriteFloat(movementFlagsOnTransport.TransportOrientation, cancellationToken).ConfigureAwait(false);

            }

            if (updateFlagLiving.Flags.Swimming is {} movementFlagsSwimming) {
                await w.WriteFloat(movementFlagsSwimming.Pitch, cancellationToken).ConfigureAwait(false);

            }

            await w.WriteFloat(updateFlagLiving.FallTime, cancellationToken).ConfigureAwait(false);

            if (updateFlagLiving.Flags.Jumping is {} movementFlagsJumping) {
                await w.WriteFloat(movementFlagsJumping.ZSpeed, cancellationToken).ConfigureAwait(false);

                await w.WriteFloat(movementFlagsJumping.CosAngle, cancellationToken).ConfigureAwait(false);

                await w.WriteFloat(movementFlagsJumping.SinAngle, cancellationToken).ConfigureAwait(false);

                await w.WriteFloat(movementFlagsJumping.XySpeed, cancellationToken).ConfigureAwait(false);

            }

            if (updateFlagLiving.Flags.SplineElevation is {} movementFlagsSplineElevation) {
                await w.WriteFloat(movementFlagsSplineElevation.SplineElevation, cancellationToken).ConfigureAwait(false);

            }

            await w.WriteFloat(updateFlagLiving.WalkingSpeed, cancellationToken).ConfigureAwait(false);

            await w.WriteFloat(updateFlagLiving.RunningSpeed, cancellationToken).ConfigureAwait(false);

            await w.WriteFloat(updateFlagLiving.BackwardsRunningSpeed, cancellationToken).ConfigureAwait(false);

            await w.WriteFloat(updateFlagLiving.SwimmingSpeed, cancellationToken).ConfigureAwait(false);

            await w.WriteFloat(updateFlagLiving.BackwardsSwimmingSpeed, cancellationToken).ConfigureAwait(false);

            await w.WriteFloat(updateFlagLiving.TurnRate, cancellationToken).ConfigureAwait(false);

            if (updateFlagLiving.Flags.SplineEnabled is {} movementFlagsSplineEnabled) {
                await w.WriteUInt((uint)movementFlagsSplineEnabled.SplineFlagsValue, cancellationToken).ConfigureAwait(false);

                if (movementFlagsSplineEnabled.SplineFlags.Value is SplineFlagFinalAngle splineFlagFinalAngle) {
                    await w.WriteFloat(splineFlagFinalAngle.Angle, cancellationToken).ConfigureAwait(false);

                }
                else if (movementFlagsSplineEnabled.SplineFlags.Value is SplineFlagFinalTarget splineFlagFinalTarget) {
                    await w.WriteULong(splineFlagFinalTarget.Target, cancellationToken).ConfigureAwait(false);

                }
                else if (movementFlagsSplineEnabled.SplineFlags.Value is SplineFlagFinalPoint splineFlagFinalPoint) {
                    await splineFlagFinalPoint.SplineFinalPoint.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

                }

                await w.WriteUInt(movementFlagsSplineEnabled.TimePassed, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(movementFlagsSplineEnabled.Duration, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt(movementFlagsSplineEnabled.Id, cancellationToken).ConfigureAwait(false);

                await w.WriteUInt((uint)movementFlagsSplineEnabled.Nodes.Count, cancellationToken).ConfigureAwait(false);

                foreach (var v in movementFlagsSplineEnabled.Nodes) {
                    await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
                }

                await movementFlagsSplineEnabled.FinalNode.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

            }

        }
        else if (UpdateFlag.Living.Value is UpdateFlagHasPosition updateFlagHasPosition) {
            await updateFlagHasPosition.Position.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

            await w.WriteFloat(updateFlagHasPosition.Orientation, cancellationToken).ConfigureAwait(false);

        }

        if (UpdateFlag.HighGuid is UpdateFlagHighGuid updateFlagHighGuid) {
            await w.WriteUInt(updateFlagHighGuid.Unknown0, cancellationToken).ConfigureAwait(false);

        }

        if (UpdateFlag.All is UpdateFlagAll updateFlagAll) {
            await w.WriteUInt(updateFlagAll.Unknown1, cancellationToken).ConfigureAwait(false);

        }

        if (UpdateFlag.MeleeAttacking is UpdateFlagMeleeAttacking updateFlagMeleeAttacking) {
            await w.WritePackedGuid(updateFlagMeleeAttacking.Guid, cancellationToken).ConfigureAwait(false);

        }

        if (UpdateFlag.Transport is UpdateFlagTransport updateFlagTransport) {
            await w.WriteUInt(updateFlagTransport.TransportProgressInMs, cancellationToken).ConfigureAwait(false);

        }

    }

    public static async Task<MovementBlock> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var updateFlag = new UpdateFlagType {
            Inner = (UpdateFlag)await r.ReadByte(cancellationToken).ConfigureAwait(false),
        };

        if (updateFlag.Inner.HasFlag(Vanilla.UpdateFlag.Living)) {
            var flags = new MovementFlagsType {
                Inner = (MovementFlags)await r.ReadUInt(cancellationToken).ConfigureAwait(false),
            };

            var timestamp = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var livingPosition = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

            var livingOrientation = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            if (flags.Inner.HasFlag(Vanilla.MovementFlags.OnTransport)) {
                var transportGuid = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

                var transportPosition = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

                var transportOrientation = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

                flags.OnTransport = new MovementFlagsOnTransport {
                    TransportGuid = transportGuid,
                    TransportOrientation = transportOrientation,
                    TransportPosition = transportPosition,
                };
            }

            if (flags.Inner.HasFlag(Vanilla.MovementFlags.Swimming)) {
                var pitch = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

                flags.Swimming = new MovementFlagsSwimming {
                    Pitch = pitch,
                };
            }

            var fallTime = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            if (flags.Inner.HasFlag(Vanilla.MovementFlags.Jumping)) {
                var zSpeed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

                var cosAngle = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

                var sinAngle = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

                var xySpeed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

                flags.Jumping = new MovementFlagsJumping {
                    CosAngle = cosAngle,
                    SinAngle = sinAngle,
                    XySpeed = xySpeed,
                    ZSpeed = zSpeed,
                };
            }

            if (flags.Inner.HasFlag(Vanilla.MovementFlags.SplineElevation)) {
                var splineElevation = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

                flags.SplineElevation = new MovementFlagsSplineElevation {
                    SplineElevation = splineElevation,
                };
            }

            var walkingSpeed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            var runningSpeed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            var backwardsRunningSpeed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            var swimmingSpeed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            var backwardsSwimmingSpeed = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            var turnRate = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            if (flags.Inner.HasFlag(Vanilla.MovementFlags.SplineEnabled)) {
                SplineFlagFinalAngleMulti splineFlags = (Vanilla.SplineFlag)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                if (((Vanilla.SplineFlag)splineFlags.Value & Vanilla.SplineFlag.FinalAngle) != 0) {
                    var angle = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

                    splineFlags = new SplineFlagFinalAngle {
                        Angle = angle,
                    };
                }
                else if (((Vanilla.SplineFlag)splineFlags.Value & Vanilla.SplineFlag.FinalTarget) != 0) {
                    var target = await r.ReadULong(cancellationToken).ConfigureAwait(false);

                    splineFlags = new SplineFlagFinalTarget {
                        Target = target,
                    };
                }
                else if (((Vanilla.SplineFlag)splineFlags.Value & Vanilla.SplineFlag.FinalPoint) != 0) {
                    var splineFinalPoint = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

                    splineFlags = new SplineFlagFinalPoint {
                        SplineFinalPoint = splineFinalPoint,
                    };
                }

                var timePassed = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var duration = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var id = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                // ReSharper disable once UnusedVariable.Compiler
                var amountOfNodes = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

                var nodes = new List<Vector3d>();
                for (var i = 0; i < amountOfNodes; ++i) {
                    nodes.Add(await All.Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
                }

                var finalNode = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

                flags.SplineEnabled = new MovementFlagsSplineEnabled {
                    Duration = duration,
                    FinalNode = finalNode,
                    Id = id,
                    Nodes = nodes,
                    SplineFlags = splineFlags,
                    TimePassed = timePassed,
                };
            }

            updateFlag.Living = new UpdateFlagLiving {
                BackwardsRunningSpeed = backwardsRunningSpeed,
                BackwardsSwimmingSpeed = backwardsSwimmingSpeed,
                FallTime = fallTime,
                Flags = flags,
                LivingOrientation = livingOrientation,
                LivingPosition = livingPosition,
                RunningSpeed = runningSpeed,
                SwimmingSpeed = swimmingSpeed,
                Timestamp = timestamp,
                TurnRate = turnRate,
                WalkingSpeed = walkingSpeed,
            };
        }
        else if (updateFlag.Inner.HasFlag(Vanilla.UpdateFlag.HasPosition)) {
            var position = await Vector3d.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

            var orientation = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            updateFlag.Living = new UpdateFlagHasPosition {
                Orientation = orientation,
                Position = position,
            };
        }

        if (updateFlag.Inner.HasFlag(Vanilla.UpdateFlag.HighGuid)) {
            var unknown0 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            updateFlag.HighGuid = new UpdateFlagHighGuid {
                Unknown0 = unknown0,
            };
        }

        if (updateFlag.Inner.HasFlag(Vanilla.UpdateFlag.All)) {
            var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            updateFlag.All = new UpdateFlagAll {
                Unknown1 = unknown1,
            };
        }

        if (updateFlag.Inner.HasFlag(Vanilla.UpdateFlag.MeleeAttacking)) {
            var guid = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            updateFlag.MeleeAttacking = new UpdateFlagMeleeAttacking {
                Guid = guid,
            };
        }

        if (updateFlag.Inner.HasFlag(Vanilla.UpdateFlag.Transport)) {
            var transportProgressInMs = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            updateFlag.Transport = new UpdateFlagTransport {
                TransportProgressInMs = transportProgressInMs,
            };
        }

        return new MovementBlock {
            UpdateFlag = updateFlag,
        };
    }

    internal int Size() {
        var size = 0;

        // update_flag: Generator.Generated.DataTypeFlag
        size += 1;

        if (UpdateFlag.Living.Value is UpdateFlagLiving updateFlagLiving) {
            // flags: Generator.Generated.DataTypeFlag
            size += 4;

            // timestamp: Generator.Generated.DataTypeInteger
            size += 4;

            // living_position: Generator.Generated.DataTypeStruct
            size += 12;

            // living_orientation: Generator.Generated.DataTypeFloatingPoint
            size += 4;

            if (updateFlagLiving.Flags.OnTransport is {} movementFlagsOnTransport) {
                // transport_guid: Generator.Generated.DataTypePackedGuid
                size += movementFlagsOnTransport.TransportGuid.PackedGuidLength();

                // transport_position: Generator.Generated.DataTypeStruct
                size += 12;

                // transport_orientation: Generator.Generated.DataTypeFloatingPoint
                size += 4;

            }

            if (updateFlagLiving.Flags.Swimming is {} movementFlagsSwimming) {
                // pitch: Generator.Generated.DataTypeFloatingPoint
                size += 4;

            }

            // fall_time: Generator.Generated.DataTypeFloatingPoint
            size += 4;

            if (updateFlagLiving.Flags.Jumping is {} movementFlagsJumping) {
                // z_speed: Generator.Generated.DataTypeFloatingPoint
                size += 4;

                // cos_angle: Generator.Generated.DataTypeFloatingPoint
                size += 4;

                // sin_angle: Generator.Generated.DataTypeFloatingPoint
                size += 4;

                // xy_speed: Generator.Generated.DataTypeFloatingPoint
                size += 4;

            }

            if (updateFlagLiving.Flags.SplineElevation is {} movementFlagsSplineElevation) {
                // spline_elevation: Generator.Generated.DataTypeFloatingPoint
                size += 4;

            }

            // walking_speed: Generator.Generated.DataTypeFloatingPoint
            size += 4;

            // running_speed: Generator.Generated.DataTypeFloatingPoint
            size += 4;

            // backwards_running_speed: Generator.Generated.DataTypeFloatingPoint
            size += 4;

            // swimming_speed: Generator.Generated.DataTypeFloatingPoint
            size += 4;

            // backwards_swimming_speed: Generator.Generated.DataTypeFloatingPoint
            size += 4;

            // turn_rate: Generator.Generated.DataTypeFloatingPoint
            size += 4;

            if (updateFlagLiving.Flags.SplineEnabled is {} movementFlagsSplineEnabled) {
                // spline_flags: Generator.Generated.DataTypeFlag
                size += 4;

                if (movementFlagsSplineEnabled.SplineFlags.Value is SplineFlagFinalAngle splineFlagFinalAngle) {
                    // angle: Generator.Generated.DataTypeFloatingPoint
                    size += 4;

                }
                else if (movementFlagsSplineEnabled.SplineFlags.Value is SplineFlagFinalTarget splineFlagFinalTarget) {
                    // target: Generator.Generated.DataTypeInteger
                    size += 8;

                }
                else if (movementFlagsSplineEnabled.SplineFlags.Value is SplineFlagFinalPoint splineFlagFinalPoint) {
                    // spline_final_point: Generator.Generated.DataTypeStruct
                    size += 12;

                }

                // time_passed: Generator.Generated.DataTypeInteger
                size += 4;

                // duration: Generator.Generated.DataTypeInteger
                size += 4;

                // id: Generator.Generated.DataTypeInteger
                size += 4;

                // amount_of_nodes: Generator.Generated.DataTypeInteger
                size += 4;

                // nodes: Generator.Generated.DataTypeArray
                size += movementFlagsSplineEnabled.Nodes.Sum(e => 12);

                // final_node: Generator.Generated.DataTypeStruct
                size += 12;

            }

        }
        else if (UpdateFlag.Living.Value is UpdateFlagHasPosition updateFlagHasPosition) {
            // position: Generator.Generated.DataTypeStruct
            size += 12;

            // orientation: Generator.Generated.DataTypeFloatingPoint
            size += 4;

        }

        if (UpdateFlag.HighGuid is UpdateFlagHighGuid updateFlagHighGuid) {
            // unknown0: Generator.Generated.DataTypeInteger
            size += 4;

        }

        if (UpdateFlag.All is UpdateFlagAll updateFlagAll) {
            // unknown1: Generator.Generated.DataTypeInteger
            size += 4;

        }

        if (UpdateFlag.MeleeAttacking is UpdateFlagMeleeAttacking updateFlagMeleeAttacking) {
            // guid: Generator.Generated.DataTypePackedGuid
            size += updateFlagMeleeAttacking.Guid.PackedGuidLength();

        }

        if (UpdateFlag.Transport is UpdateFlagTransport updateFlagTransport) {
            // transport_progress_in_ms: Generator.Generated.DataTypeInteger
            size += 4;

        }

        return size;
    }

}

