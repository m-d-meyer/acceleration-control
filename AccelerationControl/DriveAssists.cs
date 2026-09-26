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

        enum Mode { Manual, Cruise, Approach }
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

            if (_mode == Mode.Approach)
            {
                if (move.LengthSquared() > InputDeadzone * InputDeadzone)
                {
                    _mode = Mode.Manual;
                    _message = "Approach cancelled";
                    return false;
                }
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
            if (distance <= _approachBuffer)
            {
                _message = "Target is closer than " + FormatDistance(_approachBuffer);
                return false;
            }
            _route.Clear();
            _route.Add(surfacePoint - ray / distance * _approachBuffer);
            _temporaryObstacles.Clear();
            StartRoute(name);
            return true;
        }

        // Point of the ship used for distances: the scan camera, else the cockpit.
        Vector3D ReferencePosition()
        {
            if (_camera != null && _camera.IsFunctional)
                return _camera.GetPosition();
            IMyShipController reference = _controller ?? _layoutController;
            return reference != null ? reference.GetPosition() : Me.GetPosition();
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

        // Deceleration the approach plans with when moving along a direction.
        double BrakeAccel(Vector3D direction)
        {
            double brake = MaxAccelAlong(-direction) * _brakeSafety;
            return _approachFullThrust ? brake : Math.Min(brake, _limit * _brakeSafety);
        }

        // Velocity that brings the ship to the approach target and still lets it
        // stop in time: v = sqrt(2 * a_brake * distance), capped at MaxSpeed.
        bool ApproachVelocity(Vector3D velocity, out Vector3D targetVelocity)
        {
            targetVelocity = Vector3D.Zero;
            Vector3D position = ReferencePosition();
            Vector3D toTarget = _approachTarget - position;
            _targetDistance = toTarget.Length();

            // Intermediate waypoints are passed, not stopped at.
            double endSpeed = _probing ? 0 : CornerSpeed();
            if (!_probing && !OnLastLeg && _targetDistance < Math.Max(50, _currentSpeed))
            {
                NextWaypoint();
                toTarget = _approachTarget - position;
                _targetDistance = toTarget.Length();
                endSpeed = CornerSpeed();
            }

            if ((_probing || OnLastLeg) && _targetDistance < ArrivalDistance && velocity.Length() < ArrivalSpeed)
            {
                _mode = Mode.Manual;
                _message = _probing ? "Nothing found along the line of sight" : "Arrived";
                _probing = false;
                return false;
            }

            Vector3D direction = toTarget / Math.Max(_targetDistance, 1e-3);
            double brake = BrakeAccel(direction);
            double distance = _targetDistance;
            if (_probing)
            {
                // Only the scanned part of the line is known to be free.
                double clear = Vector3D.Dot(_clearUntil - position, direction) - _approachBuffer;
                distance = Math.Min(distance, Math.Max(clear, 0));
            }

            double speed = Math.Min(_maxSpeed, Math.Sqrt(endSpeed * endSpeed + 2 * brake * distance));
            // Close in: approach proportionally so the ship settles instead of oscillating.
            if (endSpeed <= 0)
                speed = Math.Min(speed, distance * _velocityGain * 0.5);
            targetVelocity = direction * speed;

            // For the display: stopping distance and flight phase.
            double current = Vector3D.Dot(velocity, direction);
            _stopDistance = current > 0 ? current * current / (2 * Math.Max(brake, 0.01)) : 0;
            _approachPhase = speed < current - 1 ? "BRAKING" : speed > current + 1 ? "ACCELERATING" : "CRUISING";
            return true;
        }
    }
}
