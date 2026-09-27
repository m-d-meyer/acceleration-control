using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    // Drive assists that produce a target velocity for the thrust control:
    //   Cruise   - constant forward speed, no sideways drift
    //   Approach - camera scan, fast flight and a timely stop before the target
    partial class Program
    {
        const double ArrivalDistance = 2.0;         // m - approach is finished within this distance...
        const double ArrivalSpeed = 0.3;            // m/s - ...and below this speed
        const double ArrivalTolerance = 10;         // m - the end of a flight only has to be hit roughly:
        const double ArrivalRoughSpeed = 1.0;       // m/s - within max(ArrivalTolerance, ApproachBuffer / 4), slower than this
        const double WaypointRadius = 50;           // m - intermediate waypoints count as reached within this

        enum Mode { Manual, Cruise, Approach, Jump, Dock }
        enum ScanPurpose { Approach, Mark }

        readonly List<IMyCameraBlock> _cameras = new List<IMyCameraBlock>();
        IMyCameraBlock _camera;
        Mode _mode = Mode.Manual;
        bool _scanPending;
        ScanPurpose _scanPurpose;
        Vector3D _approachTarget;
        string _targetName = "";
        double _targetDistance;
        double _stopDistance;
        string _approachPhase = "";
        double _remainingDistance;      // to the end of the route
        bool _flipPlanned;              // final braking with the strongest thrusters after turning around
        bool _flipBraking;              // turned around for braking; kept until the flight ends
        const double FlipFactor = 1.3;              // turn around only if the strongest side is this much stronger
        const double FlipMinDistance = 1000;        // m - shorter legs are flown without turning around

        // Approach without a target in scan range: search along the line of sight.
        const double ProbeMinRange = 1000;
        bool _probing;
        Vector3D _probeDirection;
        Vector3D _clearUntil;

        // Returns true when a drive assist wants a target velocity. Movement
        // input that conflicts with the assist cancels it.
        bool UpdateDriveAssist(MatrixD matrix, Vector3D velocity, Vector3 move,
            out Vector3D targetVelocity, out double maxAccel)
        {
            targetVelocity = Vector3D.Zero;
            maxAccel = _limit;

            if (_mode == Mode.Cruise)
            {
                if (Math.Abs(move.Z) > InputDeadzone)
                {
                    _mode = Mode.Manual;
                    _message = "Cruise cancelled";
                    return false;
                }
                targetVelocity = matrix.Forward * _cruiseSpeed;
                return true;
            }

            if (_mode != Mode.Manual && _mode != Mode.Cruise && move.LengthSquared() > InputDeadzone * InputDeadzone)
            {
                _message = _mode == Mode.Jump ? "Jump cancelled" : _mode == Mode.Dock ? "Docking cancelled" : "Approach cancelled";
                _mode = Mode.Manual;
                _dockAfterRoute = _departing = _resumeGoal = _zoneGoal = false;
                return false;
            }

            if (_mode == Mode.Jump)
            {
                UpdateJump();       // the ship holds still (target velocity zero) while it turns and jumps
                return _mode == Mode.Jump || _mode == Mode.Approach;
            }

            if (_mode == Mode.Dock)
            {
                maxAccel = Math.Min(_limit, 2);
                return DockVelocity(out targetVelocity);
            }

            if (_mode == Mode.Approach)
            {
                if (_approachFullThrust)
                    maxAccel = double.MaxValue;
                return ApproachVelocity(velocity, out targetVelocity);
            }

            return false;
        }

        // -----------------------------------------------------------------
        //  Cruise
        // -----------------------------------------------------------------

        // cruise           toggle cruise at the current cruise speed
        // cruise on|off    enable / disable
        // cruise up|down   change the cruise speed by CruiseStep
        // cruise <m/s>     set the cruise speed and enable
        void HandleCruiseCommand(string value)
        {
            double speed;
            if (value == null)
                SetCruise(_mode != Mode.Cruise);
            else if (value == "on")
                SetCruise(true);
            else if (value == "off")
                SetCruise(false);
            else if (value == "up" || value == "+")
                _cruiseSpeed = Math.Min(_cruiseSpeed + _cruiseStep, _maxSpeed);
            else if (value == "down" || value == "-")
                _cruiseSpeed = Math.Max(_cruiseSpeed - _cruiseStep, _cruiseStep);
            else if (TryParseNumber(value, out speed) && speed > 0)
            {
                _cruiseSpeed = Math.Min(speed, _maxSpeed);
                SetCruise(true);
            }
            else
                _message = "Usage: cruise [on|off|up|down|<m/s>]";
        }

        void SetCruise(bool active)
        {
            if (active)
            {
                _enabled = true;
                _mode = Mode.Cruise;
            }
            else if (_mode == Mode.Cruise)
                _mode = Mode.Manual;
        }

        // -----------------------------------------------------------------
        //  Approach
        // -----------------------------------------------------------------

        void StartScan(ScanPurpose purpose)
        {
            _camera = FindCamera();
            if (_camera == null)
            {
                _message = "No camera facing forward (or tagged " + _cameraTag + ")";
                return;
            }
            _camera.EnableRaycast = true;
            _scanPending = true;
            _scanPurpose = purpose;
        }

        // Called every tick while a scan is pending: fires the raycast as soon
        // as the camera has charged enough range.
        void UpdateScan()
        {
            if (_mode == Mode.Approach && _probing)
                UpdateProbe();
            if (!_scanPending)
                return;
            if (_camera == null || !_camera.IsWorking)
            {
                _message = "Camera not working";
                _scanPending = false;
                return;
            }

            // Marking needs the full range. An approach starts with what is
            // charged and keeps scanning ahead while flying (see UpdateProbe).
            double range = _scanPurpose == ScanPurpose.Mark ? _scanRange
                : Math.Min(_scanRange, Math.Max(ProbeMinRange, _camera.AvailableScanRange));
            if (!_camera.CanScan(range))
                return;

            _scanPending = false;
            Vector3D origin = _camera.GetPosition();
            Vector3D direction = _camera.WorldMatrix.Forward;
            MyDetectedEntityInfo hit = _camera.Raycast(range);
            if (!hit.IsEmpty() && IsOwnHit(hit))
            {
                _message = "The scan hit the own ship: point the camera (" + _camera.CustomName + ") away from the hull";
                return;
            }
            if (hit.IsEmpty() || !hit.HitPosition.HasValue)
            {
                if (_scanPurpose == ScanPurpose.Mark)
                    _message = "Nothing found within " + FormatDistance(range) + ". Asteroids far away are often not detected, fly closer.";
                else
                    StartProbe(origin, direction, range);
                return;
            }

            RegisterObstacle(hit);
            if (_scanPurpose == ScanPurpose.Mark)
                AddDeposit(_pendingMarkOre, hit.HitPosition.Value, false);
            else
                ApproachHit(hit);
        }

        void ApproachHit(MyDetectedEntityInfo hit)
        {
            Vector3D hitPos = hit.HitPosition.Value;
            string name = hit.Type == MyDetectedEntityType.Asteroid ? "Asteroid"
                : hit.Type == MyDetectedEntityType.Planet ? "Planet"
                : hit.Name;
            _probing = false;
            if (StartApproach(hitPos, name))
                _message = name + " at " + FormatDistance(Vector3D.Distance(hitPos, ReferencePosition()));
        }

        // Nothing in range yet: fly along the camera's line of sight and keep
        // scanning ahead. The speed is limited so the ship can always stop
        // within the part of the line that has been scanned clear.
        void StartProbe(Vector3D origin, Vector3D direction, double clearRange)
        {
            _probeDirection = direction;
            _clearUntil = origin + direction * clearRange;
            _route.Clear();
            _route.Add(origin + direction * _probeRange);
            StartRoute("line of sight");
            _probing = true;
            _message = "Nothing within " + FormatDistance(clearRange) + ", searching ahead";
        }

        void UpdateProbe()
        {
            if (_camera == null || !_camera.IsWorking)
                return;
            Vector3D position = _camera.GetPosition();
            double brake = Math.Max(BrakeAccel(_probeDirection), 0.1);
            double stopDistance = _currentSpeed * _currentSpeed / (2 * brake);
            double look = MathHelper.Clamp(stopDistance * 1.5 + _approachBuffer * 2, ProbeMinRange, _scanRange);
            Vector3D lookTarget = position + _probeDirection * look;
            if (!_camera.CanScan(lookTarget))
                return;

            MyDetectedEntityInfo hit = _camera.Raycast(lookTarget);
            if (!hit.IsEmpty() && IsOwnHit(hit))
                return;     // the view ahead is blocked by the own hull: no information
            if (!hit.IsEmpty() && hit.HitPosition.HasValue)
            {
                RegisterObstacle(hit);
                ApproachHit(hit);
            }
            else if (Vector3D.Dot(lookTarget - _clearUntil, _probeDirection) > 0)
                _clearUntil = lookTarget;
        }

        // Charge of the scan camera relative to the configured scan range (0..1).
        double ScanCharge()
        {
            return _camera != null ? Math.Min(_camera.AvailableScanRange / _scanRange, 1) : 0;
        }

        // Flies to a point ApproachBuffer meters before the given surface point.
        bool StartApproach(Vector3D surfacePoint, string name)
        {
            Vector3D ray = surfacePoint - ReferencePosition();
            double distance = ray.Length();
            if (distance <= StopOffset)
            {
                _message = "Target is closer than " + FormatDistance(StopOffset);
                return false;
            }
            _route.Clear();
            _route.Add(surfacePoint - ray / distance * StopOffset);
            _temporaryObstacles.Clear();
            _dockAfterRoute = _zoneGoal = false;
            StartRoute(name);
            return true;
        }

        // Point of the ship used for navigation: the center of its bounding
        // sphere. Stop points keep ApproachBuffer plus the sphere radius from a
        // surface, so the ship can turn in place there, whatever its heading.
        Vector3D ReferencePosition()
        {
            return Me.CubeGrid.WorldVolume.Center;
        }

        double StopOffset
        {
            get { return _approachBuffer + ShipRadius; }
        }

        IMyCameraBlock FindCamera()
        {
            IMyShipController reference = _controller ?? _layoutController;
            IMyCameraBlock facing = null;
            foreach (IMyCameraBlock c in _cameras)
            {
                if (!c.IsFunctional)
                    continue;
                if (c.CustomName.Contains(_cameraTag))
                    return c;
                if (facing == null && reference != null
                    && Vector3D.Dot(c.WorldMatrix.Forward, reference.WorldMatrix.Forward) > 0.99)
                    facing = c;
            }
            return facing;
        }

        // Time for a trip from rest to rest: accelerate, coast 'coast' seconds
        // (e.g. to turn around), brake; the top speed is limited by MaxSpeed.
        double TripTime(double distance, double accel, double brake, double coast)
        {
            accel = Math.Max(accel, 0.01);
            brake = Math.Max(brake, 0.01);
            double k = 1 / (2 * accel) + 1 / (2 * brake);
            double top = Math.Min(_maxSpeed, (-coast + Math.Sqrt(coast * coast + 4 * k * distance)) / (2 * k));
            double used = k * top * top + coast * top;
            return top / accel + coast + top / brake + Math.Max(distance - used, 0) / Math.Max(top, 0.01);
        }

        // Deceleration the approach plans with when moving along a direction.
        // Gravity pulling along the direction (descending) takes its share.
        double BrakeAccel(Vector3D direction)
        {
            double brake = MaxAccelAlong(-direction) * _brakeSafety;
            if (!_approachFullThrust)
                brake = Math.Min(brake, _limit * _brakeSafety);
            return Math.Max(brake - Math.Max(Vector3D.Dot(_gravity, direction), 0), 0.05);
        }

        // Velocity that brings the ship to the approach target and still lets it
        // stop in time: v = sqrt(2 * a_brake * distance), capped at MaxSpeed.
        bool _settling;     // overshot the end point within the tolerance: only stopping

        bool ApproachVelocity(Vector3D velocity, out Vector3D targetVelocity)
        {
            targetVelocity = Vector3D.Zero;
            Vector3D position = ReferencePosition();
            Vector3D toTarget = _approachTarget - position;
            _targetDistance = toTarget.Length();

            // Intermediate waypoints are passed, not stopped at.
            double endSpeed = _probing ? 0 : CornerSpeed();
            if (!_probing && !OnLastLeg && WaypointReached(position))
            {
                NextWaypoint();
                toTarget = _approachTarget - position;
                _targetDistance = toTarget.Length();
                endSpeed = CornerSpeed();
            }

            // The end of the flight is hit roughly: close enough and slow counts as
            // arrived, and a ship that overshot within the tolerance just stops
            // instead of turning back for the exact point.
            bool atEnd = _probing || OnLastLeg;
            // Before docking the ship moves to the approach point on its own, unscanned: stay close.
            double tolerance = _dockAfterRoute ? ArrivalTolerance : Math.Max(ArrivalTolerance, _approachBuffer * 0.25);
            bool inZone = atEnd && _targetDistance < tolerance;
            if (inZone && Vector3D.Dot(velocity, toTarget) <= 0)
                _settling = true;
            else if (!inZone)
                _settling = false;
            if (inZone && (_currentSpeed < ArrivalRoughSpeed || (_targetDistance < ArrivalDistance && _currentSpeed < ArrivalSpeed)))
            {
                _settling = false;
                if (_departing)
                {
                    // Clear of the rock: now plan and head off.
                    _departing = false;
                    ContinueGoal();
                    return false;
                }
                if (_resumeGoal)
                {
                    // Stopped in front of an obstacle: start over from here.
                    StartGoal(_goalTarget, _goalName, _goalDock, true);
                    return false;
                }
                if (_dockAfterRoute)
                {
                    StartDockAlign();
                    return false;
                }
                _mode = Mode.Manual;
                _message = _probing ? "Nothing found along the line of sight" : "Arrived";
                _probing = false;
                return false;
            }

            if (_settling)
            {
                _approachPhase = "BRAKING";
                _remainingDistance = _targetDistance;
                _stopDistance = 0;
                return true;    // target velocity zero: stop where the ship is
            }

            Vector3D direction = toTarget / Math.Max(_targetDistance, 1e-3);
            double brake = BrakeAccel(direction);
            double distance = _targetDistance;
            double current = Vector3D.Dot(velocity, direction);

            // Flip and burn: the ship turns around for the final braking if that is
            // faster overall than braking with the weaker side. The turn is given
            // FlipTime seconds: braking starts earlier by the distance flown in
            // that time, which also lowers the top speed on short trips that never
            // reach MaxSpeed. Once turned, the ship stays turned (_flipBraking).
            _flipPlanned = _flipBraking;
            IMyShipController reference = _controller ?? _layoutController;
            if (_useBestThrust && !_probing && !_departing && OnLastLeg && _gyros.Count > 0 && reference != null
                && _targetDistance > FlipMinDistance && !InGravity)
            {
                Vector3D bestDirection;
                double reverse, best = BestThrust(reference.WorldMatrix, reference.CalculateShipMass().PhysicalMass, out bestDirection, out reverse);
                if (!_flipBraking)
                    _flipPlanned = best * _brakeSafety > brake * FlipFactor
                        && TripTime(_targetDistance, best, best * _brakeSafety, _flipTime) < TripTime(_targetDistance, best, brake, 0);
                if (_flipPlanned)
                {
                    brake = best * _brakeSafety;
                    distance = Math.Max(distance - Math.Max(current, 0) * _flipTime, 0);
                }
            }
            if (_probing)
            {
                // Only the scanned part of the line is known to be free.
                double clear = Vector3D.Dot(_clearUntil - position, direction) - StopOffset;
                distance = Math.Min(distance, Math.Max(clear, 0));
            }

            // Routes with turns only plan with BrakeShare of the braking (see PlanCornerSpeeds).
            double share = _route.Count > 1 && !_probing ? BrakeShare : 1;
            double speed = Math.Min(_departing ? DepartureSpeed : _maxSpeed, Math.Sqrt(endSpeed * endSpeed + 2 * brake * share * distance));
            // Close in: approach proportionally so the ship settles instead of oscillating.
            if (endSpeed <= 0)
                speed = Math.Min(speed, distance * _velocityGain * 0.5);
            // Atmosphere: limited speed inside, braked in time before entering.
            speed = Math.Min(speed, AtmosphereSpeedLimit(position, _route[_route.Count - 1], brake));
            targetVelocity = direction * speed;

            // For the display: stopping distance, distance to the end of the route and flight phase.
            _remainingDistance = _targetDistance + (_probing ? 0 : RouteLengthAfterWaypoint());
            _stopDistance = current > 0 ? current * current / (2 * Math.Max(brake, 0.01)) : 0;
            _approachPhase = speed < current - 1 ? "BRAKING" : speed > current + 1 ? "ACCELERATING" : "CRUISING";
            if (_flipPlanned && _approachPhase == "BRAKING")
                _flipBraking = true;
            return true;
        }
    }
}
