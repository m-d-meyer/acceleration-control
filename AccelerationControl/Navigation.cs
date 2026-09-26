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
    // Navigation: route planning around known obstacles, pointing the ship
    // along its route with the gyroscopes, and a collision guard that scans the
    // path ahead during a flight.
    partial class Program
    {
        const int MaxDetourDepth = 6;
        const double PlanBudget = 0.7;              // share of the instruction limit route planning may use
        const double DetourFactor = 1.3;            // detour points lie this far outside the inflated obstacle
        const double GyroGain = 2.0;                // rad/s per rad of heading error
        const double GyroMaxRate = 1.5;             // rad/s
        const double GyroCalibrationRate = 0.4;     // rad/s while the rotation directions are not known yet
        const double GyroEvidence = 0.03;           // rad^2/s of evidence needed to decide a direction
        const double AlignDistance = 300;           // m - closer than this, the heading is held instead of turned
        const int GuardPattern = 9;                 // center ray plus a ring of 8

        readonly List<IMyGyro> _gyros = new List<IMyGyro>();
        readonly List<Vector3D> _route = new List<Vector3D>();
        readonly List<Vector3D> _previewRoute = new List<Vector3D>();
        readonly List<Obstacle> _temporaryObstacles = new List<Obstacle>();

        int _routeIndex;
        string _previewName;
        double _routeDeltaV, _routeLength, _routeTime;
        bool _gyrosActive;
        Vector3D _gyroSign = Vector3D.One;          // gyro Pitch/Yaw/Roll direction relative to right-hand rotation
        Vector3D _gyroEvidence;
        bool[] _gyroCalibrated = new bool[3];
        int _guardStep;
        int _guardCamera;

        double ShipRadius
        {
            get { return Me.CubeGrid.WorldVolume.Radius; }
        }

        // -----------------------------------------------------------------
        //  Route planning
        // -----------------------------------------------------------------

        // Plans from -> to around known obstacles. Returns false if a detour
        // could not be found within the depth limit.
        bool PlanRoute(Vector3D from, Vector3D to, List<Vector3D> route)
        {
            route.Clear();
            bool ok = PlanSegment(from, to, route, 0, true, true);
            route.Add(to);
            EstimateRoute(from, route);
            return ok;
        }

        // Splits a blocked segment at a detour point and plans both halves.
        // Only the real start (the ship) and the real target may lie inside an
        // obstacle's clearance, never a detour point.
        bool PlanSegment(Vector3D a, Vector3D b, List<Vector3D> route, int depth, bool atStart, bool atTarget)
        {
            Obstacle o = BlockingObstacle(a, b, atStart, atTarget);
            if (o == null)
                return true;
            if (depth >= MaxDetourDepth || Runtime.CurrentInstructionCount > Runtime.MaxInstructionCount * PlanBudget)
                return false;
            Vector3D detour = DetourPoint(o, a, b);
            bool ok = PlanSegment(a, detour, route, depth + 1, atStart, false);
            route.Add(detour);
            return PlanSegment(detour, b, route, depth + 1, false, atTarget) && ok;
        }

        // Radius the path has to keep from an obstacle's center.
        double Clearance(Obstacle o, Vector3D target)
        {
            bool avoidWell = o.Planet && _avoidGravity && Vector3D.Distance(target, o.Center) > o.GravityRadius;
            return (avoidWell ? o.GravityRadius : o.Radius) + ShipRadius + _approachBuffer;
        }

        // First obstacle on the segment, nearest to a. Obstacles containing an
        // end point are ignored (the ship leaving an asteroid, or the target's own rock).
        Obstacle BlockingObstacle(Vector3D a, Vector3D b, bool skipAtA = true, bool skipAtB = true)
        {
            Obstacle first = null;
            double firstDistance = double.MaxValue;
            for (int i = 0; i < _obstacles.Count + _temporaryObstacles.Count; i++)
            {
                Obstacle o = i < _obstacles.Count ? _obstacles[i] : _temporaryObstacles[i - _obstacles.Count];
                double clearance = Clearance(o, b);
                if ((skipAtA && Vector3D.Distance(a, o.Center) < clearance) || (skipAtB && Vector3D.Distance(b, o.Center) < clearance))
                    continue;
                if (DistanceToSegment(o.Center, a, b) >= clearance)
                    continue;
                double d = Vector3D.Distance(a, o.Center);
                if (d < firstDistance)
                {
                    first = o;
                    firstDistance = d;
                }
            }
            return first;
        }

        // A point beside the obstacle, preferably on the side the straight path
        // passes. If that point lies inside another obstacle, other directions
        // around the path and larger distances are tried.
        Vector3D DetourPoint(Obstacle o, Vector3D a, Vector3D b)
        {
            Vector3D ab = b - a;
            Vector3D direction = Vector3D.Normalize(ab);
            double t = MathHelper.Clamp(Vector3D.Dot(o.Center - a, ab) / ab.LengthSquared(), 0, 1);
            Vector3D offset = a + ab * t - o.Center;
            offset -= direction * Vector3D.Dot(offset, direction);
            if (offset.LengthSquared() < 1)
                offset = Vector3D.CalculatePerpendicularVector(direction);
            offset = Vector3D.Normalize(offset);
            Vector3D side = Vector3D.Cross(direction, offset);
            double clearance = Clearance(o, b);

            for (double scale = DetourFactor; scale < DetourFactor * 3; scale *= 1.6)
                for (int k = 0; k < 8; k++)
                {
                    // 0, +45, -45, +90, -90 ... degrees around the path direction
                    double angle = (k + 1) / 2 * Math.PI / 4 * (k % 2 == 1 ? 1 : -1);
                    Vector3D point = o.Center + (offset * Math.Cos(angle) + side * Math.Sin(angle)) * clearance * scale;
                    if (IsFree(point, b))
                        return point;
                }
            return o.Center + offset * clearance * DetourFactor;
        }

        bool IsFree(Vector3D point, Vector3D target)
        {
            foreach (Obstacle o in _obstacles)
                if (Vector3D.Distance(point, o.Center) < Clearance(o, target))
                    return false;
            foreach (Obstacle o in _temporaryObstacles)
                if (Vector3D.Distance(point, o.Center) < Clearance(o, target))
                    return false;
            return true;
        }

        // Delta-v, length and time of a route, flying each leg as fast as
        // MaxSpeed and the leg length allow and turning at the waypoints.
        void EstimateRoute(Vector3D from, List<Vector3D> route)
        {
            double accel = Math.Max(MaxAccelAlong((_controller ?? _layoutController) != null
                ? (_controller ?? _layoutController).WorldMatrix.Forward : Vector3D.Forward) * _brakeSafety, 0.1);
            _routeDeltaV = _routeLength = _routeTime = 0;
            Vector3D previous = from, velocity = Vector3D.Zero;
            foreach (Vector3D point in route)
            {
                Vector3D leg = point - previous;
                double length = leg.Length();
                if (length < 1)
                    continue;
                double speed = Math.Min(_maxSpeed, Math.Sqrt(accel * length));
                Vector3D legVelocity = leg / length * speed;
                _routeDeltaV += (legVelocity - velocity).Length();
                _routeLength += length;
                _routeTime += length / speed + speed / accel;
                velocity = legVelocity;
                previous = point;
            }
            _routeDeltaV += velocity.Length();
        }

        // ROUTE button: plan to the selected entry and show the route on the map.
        void PreviewRoute()
        {
            if (_selected == null)
            {
                _message = "No entry selected";
                return;
            }
            Vector3D from = ReferencePosition();
            bool ok = PlanRoute(from, StopPoint(from, _selected.Position), _previewRoute);
            _previewName = _selected.Label;
            _message = ok ? string.Format("Route: {0} legs, {1}", _previewRoute.Count, FormatDistance(_routeLength))
                : "No complete route found, check the map";
            if (ok && _useJump && _jumpDrives.Count > 0 && _routeLength > _jumpThreshold)
                _message += " (GO will jump first)";
        }

        // GO: plan and fly to the selected entry. The base is docked at if the
        // dock position is known; long distances start with a jump.
        void GoToSelected()
        {
            if (_selected == null)
            {
                _message = "No entry selected";
                return;
            }
            if (_selected.Ore == BaseName && _dockKnown && Vector3D.Distance(_selected.Position, _dockPosition) < _mergeDistance * 2)
            {
                StartDocking();
                return;
            }
            _dockAfterRoute = false;
            _temporaryObstacles.Clear();
            Vector3D from = ReferencePosition();
            if (TryStartJump(from, StopPoint(from, _selected.Position), _selected.Label))
                return;
            if (!PlanRoute(from, StopPoint(from, _selected.Position), _route))
            {
                _message = "No complete route found";
                return;
            }
            StartRoute(_selected.Label);
            _message = string.Format("Flying to {0}: {1} legs, {2}", _selected.Label, _route.Count, FormatDistance(_routeLength));
        }

        // The point ApproachBuffer meters before a target, seen from the ship.
        Vector3D StopPoint(Vector3D from, Vector3D target)
        {
            Vector3D ray = target - from;
            double distance = ray.Length();
            return distance > _approachBuffer ? target - ray / distance * _approachBuffer : from;
        }

        void StartRoute(string name)
        {
            _routeIndex = 0;
            _approachTarget = _route[0];
            _targetName = name;
            _probing = false;
            _enabled = true;
            _mode = Mode.Approach;
            _previewRoute.Clear();
            _guardStep = 0;
        }

        bool OnLastLeg
        {
            get { return _routeIndex >= _route.Count - 1; }
        }

        // Speed to pass an intermediate waypoint with: slower for sharper turns.
        double CornerSpeed()
        {
            if (OnLastLeg)
                return 0;
            Vector3D previous = _routeIndex > 0 ? _route[_routeIndex - 1] : ReferencePosition();
            Vector3D a = Vector3D.Normalize(_route[_routeIndex] - previous);
            Vector3D b = Vector3D.Normalize(_route[_routeIndex + 1] - _route[_routeIndex]);
            return _maxSpeed * MathHelper.Clamp(Vector3D.Dot(a, b), 0.1, 1);
        }

        void NextWaypoint()
        {
            _routeIndex++;
            _approachTarget = _route[_routeIndex];
        }

        // Plans again from the current position, e.g. after the guard found an obstacle.
        void Replan()
        {
            Vector3D target = _route[_route.Count - 1];
            if (PlanRoute(ReferencePosition(), target, _route))
            {
                _routeIndex = 0;
                _approachTarget = _route[0];
            }
            else
            {
                _mode = Mode.Manual;
                _message = "Path blocked, no way around found. Stopped.";
            }
        }

        // -----------------------------------------------------------------
        //  Collision guard
        // -----------------------------------------------------------------

        // Scans the ship's path ahead with a center ray and a ring of rays at
        // the ship's radius. A hit before the stop point either moves the stop
        // point closer (the target rock sticks out further than scanned) or
        // makes the route go around the new obstacle.
        void UpdateGuard()
        {
            if (_mode != Mode.Approach || _probing || !_guard || _cameras.Count == 0)
                return;
            Vector3D position = ReferencePosition();
            Vector3D toTarget = _approachTarget - position;
            double remaining = toTarget.Length();
            if (remaining < ArrivalDistance * 2)
                return;
            Vector3D direction = toTarget / remaining;
            double brake = Math.Max(BrakeAccel(direction), 0.1);
            double look = Math.Min(remaining + _approachBuffer * 2,
                Math.Max(_currentSpeed * _currentSpeed / (2 * brake) * 1.5 + _approachBuffer * 2, ProbeMinRange));

            // Ring point for this step (step 0 = center)
            int step = _guardStep % GuardPattern;
            Vector3D point = position + direction * look;
            if (step > 0)
            {
                Vector3D side = Vector3D.CalculatePerpendicularVector(direction);
                Vector3D up = Vector3D.Cross(direction, side);
                double angle = (step - 1) * Math.PI / 4;
                point += (side * Math.Cos(angle) + up * Math.Sin(angle)) * (ShipRadius + _approachBuffer * 0.5);
            }

            for (int i = 0; i < _cameras.Count; i++)
            {
                IMyCameraBlock camera = _cameras[(_guardCamera + i) % _cameras.Count];
                if (!camera.IsWorking)
                    continue;
                camera.EnableRaycast = true;
                if (!camera.CanScan(point))
                    continue;
                _guardCamera = (_guardCamera + i + 1) % _cameras.Count;
                _guardStep++;
                MyDetectedEntityInfo hit = camera.Raycast(point);
                if (!hit.IsEmpty() && hit.HitPosition.HasValue)
                    HandleGuardHit(hit, position, direction, remaining);
                return;
            }
        }

        void HandleGuardHit(MyDetectedEntityInfo hit, Vector3D position, Vector3D direction, double remaining)
        {
            if (_dockAfterRoute && hit.EntityId == _dockGridId)
                return;     // flying to the base: the base itself is expected ahead
            double along = Vector3D.Dot(hit.HitPosition.Value - position, direction);
            if (along > remaining + _approachBuffer)
                return;     // beyond the stop point plus buffer: no problem

            bool voxel = hit.Type == MyDetectedEntityType.Asteroid || hit.Type == MyDetectedEntityType.Planet;
            if (voxel)
                RegisterObstacle(hit);

            if (OnLastLeg && voxel)
            {
                // Most likely the target rock itself: stop earlier.
                double stop = Math.Max(along - _approachBuffer, 0);
                _approachTarget = position + direction * stop;
                _route[_route.Count - 1] = _approachTarget;
                _message = "Surface closer than scanned, stopping earlier";
                return;
            }

            if (!voxel)
            {
                // Grids move, so they are only avoided for this flight.
                BoundingBoxD box = hit.BoundingBox;
                _temporaryObstacles.Add(new Obstacle { EntityId = hit.EntityId, Center = box.Center, Radius = (box.Max - box.Min).Length() / 2 });
            }
            _message = "Obstacle ahead (" + (voxel ? "asteroid" : hit.Name) + "), going around";
            Replan();

            // If the ship is already so close that the new route still leads
            // past the hit point, stop before it instead.
            if (_mode == Mode.Approach && DistanceToSegment(hit.HitPosition.Value, position, _approachTarget) < ShipRadius + _approachBuffer)
            {
                _route.Clear();
                _route.Add(position + direction * Math.Max(along - _approachBuffer, 0));
                StartRoute(_targetName);
                _message = "Obstacle ahead, stopping before it";
            }
        }

        // Called when a new obstacle becomes known (survey, scans): plans the
        // rest of the flight again if the obstacle lies on it.
        void CheckRouteAfterNewObstacle()
        {
            if (_mode != Mode.Approach || _probing)
                return;
            Vector3D from = ReferencePosition();
            for (int i = _routeIndex; i < _route.Count; i++)
            {
                if (BlockingObstacle(from, _route[i], i == _routeIndex, i == _route.Count - 1) != null)
                {
                    _message = "New asteroid on the route, planning again";
                    Replan();
                    return;
                }
                from = _route[i];
            }
        }

        // -----------------------------------------------------------------
        //  Gyroscopes
        // -----------------------------------------------------------------

        // Turns the ship so its nose points along the flight direction. The
        // rotation directions of the gyroscope overrides are verified against
        // the measured rotation and corrected if needed.
        void UpdateGyros(IMyShipController controller, Vector3D velocity)
        {
            bool wanted = _gyros.Count > 0 && (_mode == Mode.Jump || _mode == Mode.Dock || (_alignShip && _mode == Mode.Approach));
            // The player turning the ship takes over the gyroscopes.
            if (controller.RotationIndicator.LengthSquared() > 0.01f || Math.Abs(controller.RollIndicator) > 0.01f)
                wanted = false;
            if (!wanted)
            {
                ReleaseGyros();
                return;
            }

            MatrixD matrix = controller.WorldMatrix;
            Vector3D desired, desiredUp = Vector3D.Zero;
            if (_mode == Mode.Jump)
                desired = _jumpDirection;
            else if (_mode == Mode.Dock)
            {
                desired = _dockForward;
                desiredUp = _dockUp;
            }
            else
            {
                Vector3D toTarget = _approachTarget - ReferencePosition();
                desired = toTarget.Length() > AlignDistance || !OnLastLeg ? Vector3D.Normalize(toTarget) : matrix.Forward;
            }
            Vector3D axis = Vector3D.Cross(matrix.Forward, desired);
            double sin = axis.Length(), cos = Vector3D.Dot(matrix.Forward, desired);
            _alignError = Math.Atan2(sin, cos);
            Vector3D rate = sin > 1e-6 ? axis / sin * _alignError * GyroGain
                : cos < 0 ? matrix.Up * Math.PI * GyroGain : Vector3D.Zero;
            if (desiredUp != Vector3D.Zero && cos > 0)
            {
                // Roll: bring the ship's up direction to the desired one as well.
                Vector3D rollAxis = Vector3D.Cross(matrix.Up, desiredUp);
                rate += rollAxis * GyroGain;
                _alignError = Math.Max(_alignError, Math.Acos(MathHelper.Clamp(Vector3D.Dot(matrix.Up, desiredUp), -1, 1)));
            }

            bool calibrated = _gyroCalibrated[0] && _gyroCalibrated[1] && _gyroCalibrated[2];
            double maxRate = calibrated ? GyroMaxRate : GyroCalibrationRate;
            if (rate.Length() > maxRate)
                rate = Vector3D.Normalize(rate) * maxRate;

            CalibrateGyros(controller);
            foreach (IMyGyro gyro in _gyros)
            {
                Vector3D local = Vector3D.TransformNormal(rate, MatrixD.Transpose(gyro.WorldMatrix));
                gyro.Pitch = (float)(local.X * _gyroSign.X);
                gyro.Yaw = (float)(local.Y * _gyroSign.Y);
                gyro.Roll = (float)(local.Z * _gyroSign.Z);
                gyro.GyroOverride = true;
            }
            _commandedRate = rate;
            _gyrosActive = true;
        }

        Vector3D _commandedRate;
        double _alignError;             // rad, remaining heading error of the gyroscope target

        // Compares the commanded rotation (right-hand rule, per gyro axis) with
        // the ship's measured rotation; a consistently opposite axis is flipped.
        void CalibrateGyros(IMyShipController controller)
        {
            if (!_gyrosActive || _gyros.Count == 0)
                return;
            MatrixD toGyro = MatrixD.Transpose(_gyros[0].WorldMatrix);
            Vector3D commanded = Vector3D.TransformNormal(_commandedRate, toGyro);
            Vector3D measured = Vector3D.TransformNormal(controller.GetShipVelocities().AngularVelocity, toGyro);
            for (int i = 0; i < 3; i++)
            {
                if (_gyroCalibrated[i])
                    continue;
                double c = commanded.GetDim(i), m = measured.GetDim(i);
                if (Math.Abs(c) < 0.1)
                    continue;
                double evidence = _gyroEvidence.GetDim(i) + c * m / TicksPerSecond;
                if (Math.Abs(evidence) > GyroEvidence)
                {
                    if (evidence < 0)
                        _gyroSign.SetDim(i, -_gyroSign.GetDim(i));
                    _gyroCalibrated[i] = true;
                    evidence = 0;
                }
                _gyroEvidence.SetDim(i, evidence);
            }
        }

        void ReleaseGyros()
        {
            if (!_gyrosActive)
                return;
            foreach (IMyGyro gyro in _gyros)
            {
                gyro.Pitch = gyro.Yaw = gyro.Roll = 0;
                gyro.GyroOverride = false;
            }
            _gyrosActive = false;
        }
    }
}
