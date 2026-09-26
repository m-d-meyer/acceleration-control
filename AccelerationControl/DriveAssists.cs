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
            if (!_scanPending)
                return;
            if (_camera == null || !_camera.IsWorking)
            {
                _message = "Camera not working";
                _scanPending = false;
                return;
            }
            if (!_camera.CanScan(_scanRange))
                return;

            _scanPending = false;
            MyDetectedEntityInfo hit = _camera.Raycast(_scanRange);
            if (hit.IsEmpty() || !hit.HitPosition.HasValue)
            {
                _message = "Nothing found within " + FormatDistance(_scanRange);
                return;
            }

            RegisterObstacle(hit);
            Vector3D hitPos = hit.HitPosition.Value;
            if (_scanPurpose == ScanPurpose.Mark)
            {
                AddDeposit(_pendingMarkOre, hitPos, false);
                return;
            }

            string name = hit.Type == MyDetectedEntityType.Asteroid ? "Asteroid"
                : hit.Type == MyDetectedEntityType.Planet ? "Planet"
                : hit.Name;
            if (StartApproach(hitPos, name))
                _message = name + " at " + FormatDistance(Vector3D.Distance(hitPos, ReferencePosition()));
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
            _approachTarget = surfacePoint - ray / distance * _approachBuffer;
            _targetName = name;
            _enabled = true;
            _mode = Mode.Approach;
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

        // Velocity that brings the ship to the approach target and still lets it
        // stop in time: v = sqrt(2 * a_brake * distance), capped at MaxSpeed.
        bool ApproachVelocity(Vector3D velocity, out Vector3D targetVelocity)
        {
            targetVelocity = Vector3D.Zero;
            Vector3D toTarget = _approachTarget - ReferencePosition();
            _targetDistance = toTarget.Length();

            if (_targetDistance < ArrivalDistance && velocity.Length() < ArrivalSpeed)
            {
                _mode = Mode.Manual;
                _message = "Arrived";
                return false;
            }

            Vector3D direction = toTarget / Math.Max(_targetDistance, 1e-3);
            double brake = MaxAccelAlong(-direction) * _brakeSafety;
            if (!_approachFullThrust)
                brake = Math.Min(brake, _limit * _brakeSafety);

            double speed = Math.Min(_maxSpeed, Math.Sqrt(2 * brake * _targetDistance));
            // Close in: approach proportionally so the ship settles instead of oscillating.
            speed = Math.Min(speed, _targetDistance * _velocityGain * 0.5);
            targetVelocity = direction * speed;
            return true;
        }
    }
}
