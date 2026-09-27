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
        const double GyroMinRate = 0.03;            // rad/s
        const double GyroCalibrationRate = 0.4;     // rad/s while the rotation directions are not known yet
        const double GyroEvidence = 0.03;           // rad^2/s of evidence needed to decide a direction
        const double AlignDistance = 300;           // m - closer than this, the heading is held instead of turned
        const int GuardPattern = 9;                 // center ray plus a ring of 8

        readonly List<IMyGyro> _gyros = new List<IMyGyro>();
        readonly List<Vector3D> _route = new List<Vector3D>();
        readonly List<Vector3D> _previewRoute = new List<Vector3D>();
        readonly List<Obstacle> _temporaryObstacles = new List<Obstacle>();

        int _routeIndex;
        readonly List<double> _cornerLimits = new List<double>();
        const double BrakeShare = 0.7;              // share of the braking planned for routes with turns
        Vector3D _goalTarget;
        string _goalName = "";
        bool _goalDock, _departing, _resumeGoal, _replanPending;
        const double DepartureSpeed = 10;           // m/s while moving away from a rock
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
            GoToPoint(_selected.Position, _selected.Label);
        }

        // Flies to a point and stops ApproachBuffer before it. If the point lies
        // inside a rock (e.g. GPS of an asteroid's center), the collision guard
        // stops the ship in front of the surface instead.
        void GoToPoint(Vector3D target, string name)
        {
            StartGoal(target, name, false);
        }

        // Every flight (GO, goto, dock) starts here:
        //  1. Next to an asteroid or a deposit, first move straight out without
        //     turning (backwards, the way the ship came in, if possible).
        //  2. Plan the route around known obstacles.
        //  3. Jump along the first leg if it is long enough, else fly it.
        void StartGoal(Vector3D target, string name, bool dock, bool resume = false)
        {
            _goalTarget = target;
            _goalName = name;
            _goalDock = dock;
            _dockAfterRoute = dock;
            if (!resume)
                _temporaryObstacles.Clear();    // ships seen on the way stay avoided when resuming
            _replanPending = _resumeGoal = false;
            Vector3D departure;
            if (NeedsDeparture(ReferencePosition(), out departure))
            {
                _route.Clear();
                _route.Add(departure);
                StartRoute("leaving");
                _departing = true;
                _message = "Moving away from the rock first";
                return;
            }
            ContinueGoal();
        }

        void ContinueGoal()
        {
            Vector3D from = ReferencePosition();
            Vector3D stop = _goalDock ? _goalTarget : StopPoint(from, _goalTarget);
            if (_goalDock && Vector3D.Distance(from, stop) < 20)
            {
                StartDockAlign();
                return;
            }
            if (!PlanRoute(from, stop, _route))
            {
                _mode = Mode.Manual;
                _dockAfterRoute = false;
                _message = "No complete route found";
                return;
            }
            if (TryStartJump(from, _route[0], stop, _goalName))
                return;
            StartRoute(_goalName);
            _message = string.Format("Flying to {0}: {1} legs, {2}", _goalName, _route.Count, FormatDistance(_routeLength));
        }

        // Is the ship so close to an asteroid (or a deposit, i.e. a rock face)
        // that turning or heading off could hit it? Then returns a point to
        // move to first: straight back if that leads away, else directly away.
        bool NeedsDeparture(Vector3D from, out Vector3D point)
        {
            point = from;
            IMyShipController reference = _controller ?? _layoutController;
            if (reference == null)
                return false;
            Vector3D back = reference.WorldMatrix.Backward, away = back;
            double need = 0;
            foreach (Obstacle o in _obstacles)
            {
                if (o.Planet)
                    continue;
                double clearance = o.Radius + ShipRadius + _approachBuffer, d = Vector3D.Distance(from, o.Center);
                if (d < clearance && clearance - d > need)
                {
                    need = clearance - d;
                    away = Vector3D.Normalize(from - o.Center);
                }
            }
            foreach (Deposit d in _deposits)
            {
                double clearance = ShipRadius + _approachBuffer * 2, distance = Vector3D.Distance(from, d.Position);
                if (distance < clearance && clearance - distance > need)
                {
                    need = clearance - distance;
                    if (distance > 1)
                        away = Vector3D.Normalize(from - d.Position);
                }
            }
            if (need <= 0)
                return false;
            Vector3D direction = Vector3D.Dot(back, away) > 0.3 ? back : away;
            point = from + direction * (need + _approachBuffer);
            return true;
        }

        // goto GPS:name:x:y:z:...  (as copied from the game's GPS list)
        void GoToGps(string text)
        {
            int start = text.IndexOf("GPS:", StringComparison.OrdinalIgnoreCase);
            string[] p = start >= 0 ? text.Substring(start).Split(':') : new string[0];
            double x, y, z;
            if (p.Length < 5 || !TryParseNumber(p[2], out x) || !TryParseNumber(p[3], out y) || !TryParseNumber(p[4], out z))
            {
                _message = "Usage: goto GPS:name:x:y:z:";
                return;
            }
            GoToPoint(new Vector3D(x, y, z), p[1]);
        }

        // The point where the ship's center stops in front of a target: ApproachBuffer plus the ship's radius.
        Vector3D StopPoint(Vector3D from, Vector3D target)
        {
            Vector3D ray = target - from;
            double distance = ray.Length();
            return distance > StopOffset ? target - ray / distance * StopOffset : from;
        }

        void StartRoute(string name)
        {
            _departing = _resumeGoal = _replanPending = _flipBraking = false;
            _routeIndex = 0;
            PlanCornerSpeeds();
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

        // Speed to pass the current waypoint with (0 at the end of the route).
        double CornerSpeed()
        {
            return _routeIndex < _cornerLimits.Count ? _cornerLimits[_routeIndex] : 0;
        }

        // Backward planning of the waypoint speeds: starting with 0 at the end
        // of the route, each waypoint may only be passed as fast as the ship can
        // still slow down to the next limit on the following leg. Sharper turns
        // lower the limit further (cos^2 of the turn angle), because the speed
        // across the new leg has to be removed after the turn. Only BrakeShare
        // of the planned braking is used, as margin for cutting corners.
        // Simulated with thousands of random routes ending in turns.
        void PlanCornerSpeeds()
        {
            _cornerLimits.Clear();
            for (int i = 0; i < _route.Count; i++)
                _cornerLimits.Add(0);
            double brake = PlanningBrake();
            Vector3D start = ReferencePosition();
            for (int i = _route.Count - 2; i >= 0; i--)
            {
                Vector3D previous = i > 0 ? _route[i - 1] : start;
                Vector3D a = _route[i] - previous, b = _route[i + 1] - _route[i];
                double la = a.Length(), lb = b.Length();
                double c = la > 1e-3 && lb > 1e-3 ? Math.Max(Vector3D.Dot(a, b) / (la * lb), 0) : 0;
                double next = _cornerLimits[i + 1];
                _cornerLimits[i] = Math.Min(_maxSpeed * Math.Max(c, 0.1), Math.Sqrt(next * next + 2 * brake * BrakeShare * lb) * c * c);
            }
        }

        // Braking the planning can count on: with the strongest thrusters pointing
        // along the flight, the opposite (braking) side; otherwise the thrusters
        // pushing backwards while the nose points along the flight.
        double PlanningBrake()
        {
            IMyShipController reference = _controller ?? _layoutController;
            if (reference == null)
                return 0.1;
            double mass = reference.CalculateShipMass().PhysicalMass, brake;
            if (_useBestThrust && _gyros.Count > 0)
            {
                Vector3D direction;
                BestThrust(reference.WorldMatrix, mass, out direction, out brake);
            }
            else
                brake = MaxAccel(2, 0, mass);
            return Math.Max(brake * _brakeSafety, 0.1);
        }

        // Length of the route after the current waypoint.
        double RouteLengthAfterWaypoint()
        {
            double length = 0;
            for (int i = _routeIndex; i < _route.Count - 1; i++)
                length += Vector3D.Distance(_route[i], _route[i + 1]);
            return length;
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
                PlanCornerSpeeds();
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
            if (_mode != Mode.Approach || _probing || _departing || _replanPending || !_guard || _cameras.Count == 0)
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
                if (!hit.IsEmpty() && hit.HitPosition.HasValue && !IsOwnHit(hit))
                    HandleGuardHit(hit, position, direction, remaining);
                return;
            }
        }

        void HandleGuardHit(MyDetectedEntityInfo hit, Vector3D position, Vector3D direction, double remaining)
        {
            double along = Vector3D.Dot(hit.HitPosition.Value - position, direction);
            double stopDistance = _currentSpeed * _currentSpeed / (2 * Math.Max(BrakeAccel(direction), 0.1));
            if (_dockAfterRoute && IsBaseGrid(hit.EntityId))
            {
                // Flying to the base: the base is expected ahead and is ignored,
                // unless the ship could no longer stop in front of it.
                if (along - ShipRadius > stopDistance * 1.3 + _approachBuffer)
                    return;
                _route.Clear();
                _route.Add(position + direction * Math.Max(along - StopOffset, 0));
                StartRoute(_targetName);
                _resumeGoal = true;
                _message = "Too fast towards the base, emergency stop";
                return;
            }
            if (along > remaining + StopOffset + 5)
                return;     // beyond the stop point plus buffer: no problem

            bool voxel = hit.Type == MyDetectedEntityType.Asteroid || hit.Type == MyDetectedEntityType.Planet;
            if (voxel)
                RegisterObstacle(hit);

            if (OnLastLeg && voxel)
            {
                // Most likely the target rock itself: stop earlier.
                double stop = Math.Max(along - StopOffset, 0);
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
            // Too close to go around at this speed: stop in front of it, then
            // start over from there (moving away first). Otherwise plan a way
            // around in the next tick.
            if (along - _approachBuffer - ShipRadius < stopDistance * 1.3)
            {
                _route.Clear();
                _route.Add(position + direction * Math.Max(along - _approachBuffer - ShipRadius, 0));
                StartRoute(_targetName);
                _resumeGoal = true;
                _message = "Obstacle ahead (" + (voxel ? "rock" : hit.Name) + "), stopping in front of it";
            }
            else
            {
                _replanPending = true;
                _message = "Obstacle ahead (" + (voxel ? "rock" : hit.Name) + "), going around";
            }
        }

        // Runs at the start of a tick, so planning has the full instruction budget.
        void RunPendingReplan()
        {
            if (!_replanPending)
                return;
            _replanPending = false;
            if (_mode == Mode.Approach && !_probing && !_departing && _route.Count > 0)
                Replan();
        }

        // Called when a new obstacle becomes known (survey, scans): plans the
        // rest of the flight again if the obstacle lies on it.
        void CheckRouteAfterNewObstacle()
        {
            if (_mode != Mode.Approach || _probing || _departing)
                return;
            Vector3D from = ReferencePosition();
            for (int i = _routeIndex; i < _route.Count; i++)
            {
                if (BlockingObstacle(from, _route[i], i == _routeIndex, i == _route.Count - 1) != null)
                {
                    _message = "New asteroid on the route, planning again";
                    _replanPending = true;     // next tick, with the full instruction budget
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
            Vector3D pointing = matrix.Forward;     // ship direction that is turned towards 'desired'
            if (_mode == Mode.Jump)
                desired = _jumpDirection;
            else if (_mode == Mode.Dock)
            {
                desired = _dockForward;
                desiredUp = _dockUp;
            }
            else if (_departing)
                desired = matrix.Forward;       // leaving a rock: hold the heading, do not turn
            else
            {
                Vector3D toTarget = _approachTarget - ReferencePosition();
                double distance = toTarget.Length();
                Vector3D toward = distance > 1e-3 ? toTarget / distance : matrix.Forward;
                if (OnLastLeg && !_probing && distance < AlignDistance)
                    // At the stop point: hold the heading while moving, then turn the
                    // nose to the target (safe: the stop point keeps the ship's radius).
                    desired = _currentSpeed < 3 ? toward : matrix.Forward;
                else
                {
                    desired = toward;
                    if (_useBestThrust && !_probing)
                    {
                        // Point the strongest thrusters along the flight; for the
                        // final braking of a flip, against the velocity.
                        double reverse;
                        BestThrust(matrix, controller.CalculateShipMass().PhysicalMass, out pointing, out reverse);
                        if (_flipBraking)
                            desired = velocity.LengthSquared() > 1 ? -Vector3D.Normalize(velocity) : -toward;
                    }
                }
            }
            Vector3D axis = Vector3D.Cross(pointing, desired);
            double sin = axis.Length(), cos = Vector3D.Dot(pointing, desired);
            _alignError = Math.Atan2(sin, cos);
            // At least GyroMinRate while not aligned, so small errors do not linger.
            Vector3D rate = sin > 1e-6 ? axis / sin * Math.Max(_alignError * GyroGain, _alignError > 0.002 ? GyroMinRate : 0)
                : cos < 0 ? Vector3D.CalculatePerpendicularVector(pointing) * Math.PI * GyroGain : Vector3D.Zero;
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

        void ReleaseGyros(bool force = false)
        {
            if (!_gyrosActive && !force)
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
