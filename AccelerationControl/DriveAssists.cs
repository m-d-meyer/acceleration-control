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

        enum Mode { Manual, Cruise, Scanning, Approach }

        readonly List<IMyCameraBlock> _cameras = new List<IMyCameraBlock>();
        IMyCameraBlock _camera;
        Mode _mode = Mode.Manual;
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

            if (_mode == Mode.Scanning)
                UpdateScan();

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

        void StartScan()
        {
            _camera = FindCamera();
            if (_camera == null)
            {
                _message = "No camera facing forward (or tagged " + _cameraTag + ")";
                return;
            }
            _camera.EnableRaycast = true;
            _enabled = true;
            _mode = Mode.Scanning;
        }

        // Called every tick while scanning: fires the raycast as soon as the
        // camera has charged enough range.
        void UpdateScan()
        {
            if (_camera == null || !_camera.IsWorking)
            {
                _message = "Camera not working";
                _mode = Mode.Manual;
                return;
            }
            if (!_camera.CanScan(_scanRange))
                return;

            MyDetectedEntityInfo hit = _camera.Raycast(_scanRange);
            if (hit.IsEmpty() || !hit.HitPosition.HasValue)
            {
                _message = "Nothing found within " + FormatDistance(_scanRange);
                _mode = Mode.Manual;
                return;
            }

            Vector3D origin = _camera.GetPosition();
            Vector3D hitPos = hit.HitPosition.Value;
            Vector3D ray = hitPos - origin;
            double surfaceDistance = ray.Length();
            if (surfaceDistance <= _approachBuffer)
            {
                _message = "Target is closer than " + FormatDistance(_approachBuffer);
                _mode = Mode.Manual;
                return;
            }

            _approachTarget = hitPos - ray / surfaceDistance * _approachBuffer;
            _targetName = hit.Type == MyDetectedEntityType.Asteroid ? "Asteroid"
                : hit.Type == MyDetectedEntityType.Planet ? "Planet"
                : hit.Name;
            _message = _targetName + " at " + FormatDistance(surfaceDistance);
            _mode = Mode.Approach;
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
            Vector3D position = _camera != null && _camera.IsFunctional ? _camera.GetPosition() : _controller.GetPosition();
            Vector3D toTarget = _approachTarget - position;
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
