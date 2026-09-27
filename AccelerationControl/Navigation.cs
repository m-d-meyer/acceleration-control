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
        readonly List<double> _limitBuffer = new List<double>();
        readonly List<Obstacle> _planOwners = new List<Obstacle>();    // obstacle each planned waypoint goes around
        const double MaxDetourWiden = 5000;         // m - detour waypoints are moved out at most this far
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
        bool _guardParallel;
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
        // Near a planet the route climbs to a cruise height, follows the
        // curvature and ends above the target (see Planets.cs).
        bool PlanRoute(Vector3D from, Vector3D to, List<Vector3D> route)
        {
            route.Clear();
            _planOwners.Clear();
            if (InGravity)
                UpdatePlanet();
            double cruise;
            Obstacle planet = PlanetOnRoute(from, to, out cruise);
            Vector3D start = planet != null ? PlanPlanetArc(planet, cruise, from, to, route) : from;
            bool ok = PlanSegment(start, to, route, 0, true, true);
            route.Add(to);
            _planOwners.Add(null);
            if (ok)
                WidenDetours(from, route);
            EstimateRoute(from, route);
            return ok;
        }

        // Moves detour waypoints further out if the ship cannot make the turn
        // there within the planned distance to the rock. The drift in a turn is
        // estimated from the planned corner speed and the ship's actual weakest
        // sideways acceleration, plus the corner the ship cuts when it switches
        // to the next waypoint early. Moving the waypoint out keeps the speed.
        void WidenDetours(Vector3D from, List<Vector3D> route)
        {
            double side = Math.Max(SideAccel() * _brakeSafety, 0.05), brake = PlanningBrake();
            Vector3D target = route[route.Count - 1];
            for (int iteration = 0; iteration < 3; iteration++)
            {
                ComputeCornerLimits(from, route, _limitBuffer, brake);
                bool moved = false;
                for (int i = 0; i < route.Count - 1; i++)
                {
                    Obstacle o = _planOwners[i];
                    if (o == null)
                        continue;
                    Vector3D a = route[i] - (i > 0 ? route[i - 1] : from), b = route[i + 1] - route[i];
                    double la = a.Length(), lb = b.Length();
                    if (la < 1 || lb < 1)
                        continue;
                    double cos = MathHelper.Clamp(Vector3D.Dot(a, b) / (la * lb), -1, 1), sin = Math.Sqrt(1 - cos * cos);
                    double v = _limitBuffer[i];
                    double drift = v * sin * v * sin / (2 * side) + 0.5 * v * Math.Sqrt((1 - cos) / 2);
                    double margin = Vector3D.Distance(route[i], o.Center) - Clearance(o, target);
                    double need = Math.Min(drift + _approachBuffer * 0.5 - margin, MaxDetourWiden - margin);
                    Vector3D widened = route[i] + Vector3D.Normalize(route[i] - o.Center) * need;
                    if (need > 1 && IsFree(widened, target))
                    {
                        route[i] = widened;
                        moved = true;
                    }
                }
                if (!moved)
                    break;
            }
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
            _planOwners.Add(o);
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
            if (_selected.Zone != _zone)
            {
                bool dock = _selected.Ore == BaseName && _dockKnown && _dockZone == _selected.Zone;
                StartZoneGoal(dock ? DockApproachPoint : _selected.Position, _selected.Label, _selected.Zone, dock);
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
            _zoneGoal = false;
            if (!resume)
            {
                _temporaryObstacles.Clear();    // ships seen on the way stay avoided when resuming
                _terrainRadius = 0;
            }
            _replanPending = _resumeGoal = false;
            if (!resume && !CanHover())
                return;
            // Already near the dock (at the approach point or on the way in):
            // go straight to the slow docking manoeuvre.
            if (dock && NearDock())
            {
                StartDockAlign();
                return;
            }
            if (!resume)
                _exitAttempts = _waitAttempts = 0;
            _pendingStart = false;
            int confined = TryLeaveConfined();
            if (confined == 2)
            {
                WaitForCameras();
                return;
            }
            if (confined == 1)
                return;
            Vector3D departure;
            int leave = NeedsDeparture(ReferencePosition(), out departure);
            if (leave == 2)
            {
                WaitForCameras();
                return;
            }
            if (leave < 0)
            {
                _mode = Mode.Manual;
                _message = "Close to a rock and no way out seen by the cameras: move away by hand";
                return;
            }
            if (leave > 0)
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
        // that turning or heading off could hit it? Then returns 1 and a point to
        // move to first, straight and without turning: backwards if that leads
        // away, else away from the rock or along another ship axis, whichever the
        // cameras see clear and the map allows (another rock may be right behind
        // the ship). Without any verified way, the way the ship came in is used.
        // Returns 0 if no departure is needed, -1 if no way out was found.
        int NeedsDeparture(Vector3D from, out Vector3D point)
        {
            point = from;
            IMyShipController reference = _controller ?? _layoutController;
            if (reference == null)
                return 0;
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
                if (d.Zone != _zone)
                    continue;
                double clearance = ShipRadius + _approachBuffer * 2, distance = Vector3D.Distance(from, d.Position);
                if (distance < clearance && clearance - distance > need)
                {
                    need = clearance - distance;
                    if (distance > 1)
                        away = Vector3D.Normalize(from - d.Position);
                }
            }
            if (need <= 0)
                return 0;
            double move = need + _approachBuffer;
            // In gravity straight up (the ship is level, a mine is below or beside it).
            if (InGravity)
            {
                point = from - Vector3D.Normalize(_gravity) * move;
                return 1;
            }
            MatrixD m = reference.WorldMatrix;
            _wayCandidates.Clear();
            if (Vector3D.Dot(back, away) > 0.3)
                _wayCandidates.Add(back);
            _wayCandidates.Add(away);
            foreach (Vector3D axis in new[] { m.Backward, m.Up, m.Down, m.Left, m.Right, m.Forward })
                if (Vector3D.Dot(axis, away) > -0.2)
                    _wayCandidates.Add(axis);
            Vector3D direction;
            _blindValid = _cameFromValid && Vector3D.Distance(from, _cameFromAt) < ShipRadius;
            _blindWay = _cameFrom;
            int way = ChooseWayOut(from, move, away, out direction);
            if (way != 1)
                return way == 2 ? 2 : -1;
            point = from + direction * move;
            return 1;
        }

        // The ship waits (a second at a time, up to ten times) for cameras that look
        // the right way but have not charged enough range yet, instead of moving
        // without seeing. The background survey pauses meanwhile.
        bool _pendingStart, _noWait, _blindValid;
        int _pendingStartTick, _waitAttempts;
        Vector3D _blindWay;

        void WaitForCameras()
        {
            _waitAttempts++;
            _pendingStart = true;
            _pendingStartTick = _ticks + 60;
            _mode = Mode.Manual;
            _message = "Charging the cameras to check the way out";
        }

        void RunPendingStart()
        {
            if (!_pendingStart || _ticks < _pendingStartTick)
                return;
            _noWait = _waitAttempts >= 10;     // give up waiting: treat uncharged as unseen
            StartGoal(_goalTarget, _goalName, _goalDock, true);
            _noWait = false;
        }

        readonly List<Vector3D> _wayCandidates = new List<Vector3D>();
        Vector3D _cameFrom, _cameFromAt;        // direction the ship last came from, and where it stopped
        bool _cameFromValid;

        // Every tick: while moving, remember where the ship came from. That way
        // is known to be free (the ship just passed there), even where no
        // camera can look.
        void TrackCameFrom(Vector3D velocity)
        {
            if (velocity.LengthSquared() < 0.25)
                return;
            _cameFrom = -Vector3D.Normalize(velocity);
            _cameFromAt = ReferencePosition();
            _cameFromValid = true;
        }

        // First candidate direction (in _wayCandidates) that the map allows and
        // the cameras see clear for the move plus the ship's radius and a buffer;
        // else the way the ship came in, if the map allows it and it does not lead
        // towards the rock. False if neither.
        // Returns 1 (found), 0 (none) or 2 (wait for cameras to charge). Without a
        // way seen clear, the blind way (_blindWay: where the ship came from, or up
        // off the ground) is used if the map allows it and no camera sees it blocked.
        int ChooseWayOut(Vector3D from, double distance, Vector3D away, out Vector3D direction)
        {
            double length = distance + ShipRadius + _approachBuffer;
            bool wait = false;
            foreach (Vector3D candidate in _wayCandidates)
            {
                if (!MapClear(from, candidate, distance))
                    continue;
                int seen = CheckPath(from, candidate, length);
                if (seen > 0)
                {
                    direction = candidate;
                    return 1;
                }
                wait |= seen == -2;
            }
            direction = _blindWay;
            if (wait)
                return 2;
            return _blindValid && Vector3D.Dot(_blindWay, away) > -0.2 && MapClear(from, _blindWay, distance)
                && CheckPath(from, _blindWay, length) == 0 ? 1 : 0;
        }

        // Known rocks along a straight move: none may be entered, and a rock the
        // ship is already too close to may not get closer.
        bool MapClear(Vector3D from, Vector3D direction, double distance)
        {
            Vector3D to = from + direction * distance;
            foreach (Obstacle o in _obstacles)
            {
                if (o.Planet)
                    continue;
                double now = Vector3D.Distance(from, o.Center), limit = o.Radius + ShipRadius + TurnMargin;
                double closest = DistanceToSegment(o.Center, from, to);
                if (now < limit ? closest < now - 1 : closest < limit)
                    return false;
            }
            return true;
        }

        // Rays along a straight move: from every camera facing that way, and to
        // the centre line and four lines at 60 % of the ship's radius around it.
        // 1 = seen clear, 0 = could not be seen (no camera looks that way),
        // -1 = something is in the way, -2 = a camera looks that way but is still charging.
        int CheckPath(Vector3D from, Vector3D direction, double length)
        {
            // Cameras facing the way (they may sit anywhere on the hull, e.g. offset
            // to the sides) look straight along it from where they are, so their
            // rays run parallel to the path the hull takes.
            int parallel = 0;
            bool charging = false;
            foreach (IMyCameraBlock c in _cameras)
            {
                if (!c.IsWorking || Vector3D.Dot(c.WorldMatrix.Forward, direction) < 0.75)
                    continue;
                c.EnableRaycast = true;
                Vector3D start = c.GetPosition();
                Vector3D target = start + direction * Math.Max(length - Vector3D.Dot(start - from, direction), 10);
                if (!c.CanScan(target))
                {
                    charging |= !_noWait && LooksAt(c, target);
                    continue;
                }
                MyDetectedEntityInfo hit = c.Raycast(target);
                if (!hit.IsEmpty() && hit.HitPosition.HasValue)
                {
                    if (!IsOwnHit(hit))
                        return -1;
                    continue;       // looking along the own hull: no information
                }
                parallel++;
            }
            Vector3D side = Vector3D.CalculatePerpendicularVector(direction), up = Vector3D.Cross(direction, side);
            int result = 1;
            for (int i = 0; i < 5; i++)
            {
                Vector3D offset = i == 0 ? Vector3D.Zero : (i < 3 ? side : up) * (i % 2 == 0 ? -0.6 : 0.6) * ShipRadius;
                double hit = ScanFrom(from + offset, direction, length);
                if (hit >= 0 && hit < double.MaxValue)
                    return -1;
                if (hit < 0)
                    result = 0;
                charging |= hit == -2;
            }
            // Seen clear along the way by at least one camera facing it: good enough
            // where the rays towards the centre line could not all be cast.
            return parallel > 0 ? 1 : charging ? -2 : result;
        }

        // -----------------------------------------------------------------
        //  Confined spaces (hangars, docking bays)
        // -----------------------------------------------------------------

        const double TurnMargin = 5;        // m beyond the ship's radius that must be free to turn
        const int MaxExitAttempts = 4;
        int _exitAttempts;

        // Before a flight turns the ship (towards the route or for a jump): is
        // there room to turn? Walls of a hangar are grids and not on the map, so
        // the cameras check a sphere around the ship. If something is inside,
        // the ship first moves straight out (without turning) in the ship
        // direction that is clear far enough, preferably backwards. Returns true
        // if it took over (moving out, or stopped because no way out was found).
        // Returns 0 (room to turn, go on), 1 (took over: moving out, or stopped) or
        // 2 (wait for the cameras to charge).
        int TryLeaveConfined()
        {
            IMyShipController reference = _controller ?? _layoutController;
            if (reference == null || _cameras.Count == 0 || _exitAttempts >= MaxExitAttempts)
                return 0;
            MatrixD m = reference.WorldMatrix;
            Vector3D center = ReferencePosition();
            double r = ShipRadius;
            bool confined = false, onlyBelow = InGravity, wait = false;
            Vector3D down = InGravity ? Vector3D.Normalize(_gravity) : Vector3D.Zero;
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 && y == 0 && z == 0)
                            continue;
                        Vector3D direction = Vector3D.Normalize(m.Right * x + m.Up * y + m.Backward * z);
                        double hit = ScanFrom(center, direction, r + TurnMargin);
                        wait |= hit == -2;
                        if (hit >= 0 && hit < r + TurnMargin)
                        {
                            confined = true;
                            onlyBelow &= Vector3D.Dot(direction, down) > 0.3;    // the ground under a landed ship
                        }
                    }
            if (!confined)
                return wait ? 2 : 0;

            // A straight way out along a ship axis that the cameras see clear
            // (preferably backwards), else the way the ship came in, or straight up
            // if only the ground below is close (landed on a planet).
            _wayCandidates.Clear();
            if (onlyBelow)
                _wayCandidates.Add(-down);
            _wayCandidates.AddRange(new[] { m.Backward, m.Forward, m.Up, m.Down, m.Left, m.Right });
            _blindValid = onlyBelow || (_cameFromValid && Vector3D.Distance(center, _cameFromAt) < r);
            _blindWay = onlyBelow ? -down : _cameFrom;
            Vector3D axis;
            int way = ChooseWayOut(center, 2 * r + _approachBuffer, _blindWay, out axis);
            if (way == 2)
                return 2;
            if (way == 1)
            {
                _exitAttempts++;
                _route.Clear();
                _route.Add(center + axis * (2 * r + _approachBuffer));
                StartRoute("leaving");
                _departing = true;
                _message = "Too tight to turn: moving out straight first";
                return 1;
            }
            _mode = Mode.Manual;
            _message = "Too tight to turn and no straight way out seen: fly out by hand";
            return 1;
        }

        // Is the point inside the camera's raycast cone?
        static bool LooksAt(IMyCameraBlock camera, Vector3D point)
        {
            Vector3D to = point - camera.GetPosition();
            double length = to.Length();
            return length < 1e-3 || Vector3D.Dot(to / length, camera.WorldMatrix.Forward) >= Math.Cos(MathHelper.ToRadians(camera.RaycastConeLimit));
        }

        // Distance from 'from' to the first foreign object along a ray of the given
        // length (MaxValue: clear), -1 if no camera can look there or only the own
        // hull is in the way, -2 if a camera looks there but has not charged enough.
        double ScanFrom(Vector3D from, Vector3D direction, double length)
        {
            Vector3D point = from + direction * length;
            IMyCameraBlock best = null;
            double bestDistance = double.MaxValue;
            bool charging = false;
            foreach (IMyCameraBlock c in _cameras)
            {
                if (!c.IsWorking)
                    continue;
                c.EnableRaycast = true;
                double d = Vector3D.DistanceSquared(c.GetPosition(), point);
                if (d >= bestDistance)
                    continue;
                if (c.CanScan(point))
                {
                    best = c;
                    bestDistance = d;
                }
                else
                    charging |= !_noWait && LooksAt(c, point);
            }
            if (best == null)
                return charging ? -2 : -1;
            MyDetectedEntityInfo hit = best.Raycast(point);
            if (hit.IsEmpty() || !hit.HitPosition.HasValue)
                return double.MaxValue;
            return IsOwnHit(hit) ? -1 : Vector3D.Distance(from, hit.HitPosition.Value);
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

        // The point where the ship's center stops in front of a target: ApproachBuffer
        // plus the ship's radius. On a planet above the target.
        Vector3D StopPoint(Vector3D from, Vector3D target)
        {
            Obstacle planet = PlanetAt(target);
            if (planet != null && Vector3D.Distance(target, planet.Center) < NearGroundRadius(planet))
                return target + Vector3D.Normalize(target - planet.Center) * StopOffset;
            Vector3D ray = target - from;
            double distance = ray.Length();
            return distance > StopOffset ? target - ray / distance * StopOffset : from;
        }

        void StartRoute(string name)
        {
            _departing = _resumeGoal = _replanPending = _flipBraking = _tracking = false;
            _routeIndex = 0;
            _jumpCheckedLeg = -1;
            _legStart = ReferencePosition();
            PlanCornerSpeeds();
            _approachTarget = _route[0];
            _targetName = name;
            _probing = false;
            _enabled = true;
            _mode = Mode.Approach;
            _previewRoute.Clear();
            _guardStep = 0;
        }

        Vector3D _legStart;     // where the current leg began (previous waypoint or start)

        // An intermediate waypoint only has to be hit roughly: it counts as reached
        // within a radius that grows with speed, or as soon as the ship has crossed
        // the plane through the waypoint halfway between the incoming and the
        // outgoing leg, so the ship never turns back to it. Collisions are the
        // guard's job, and WidenDetours already allows for cut corners.
        bool WaypointReached(Vector3D position)
        {
            if (_targetDistance < Math.Max(WaypointRadius, _currentSpeed * 0.5))
                return true;
            Vector3D incoming = _approachTarget - _legStart, outgoing = _route[_routeIndex + 1] - _approachTarget;
            if (incoming.Length() < 1e-3 || outgoing.Length() < 1e-3)
                return false;
            Vector3D normal = Vector3D.Normalize(incoming) + Vector3D.Normalize(outgoing);
            if (normal.Length() < 1e-3)
                normal = Vector3D.Normalize(incoming);      // turning straight back
            return Vector3D.Dot(position - _approachTarget, normal) > 0;
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
            ComputeCornerLimits(ReferencePosition(), _route, _cornerLimits, PlanningBrake());
        }

        void ComputeCornerLimits(Vector3D start, List<Vector3D> route, List<double> limits, double brake)
        {
            limits.Clear();
            for (int i = 0; i < route.Count; i++)
                limits.Add(0);
            for (int i = route.Count - 2; i >= 0; i--)
            {
                Vector3D previous = i > 0 ? route[i - 1] : start;
                Vector3D a = route[i] - previous, b = route[i + 1] - route[i];
                double la = a.Length(), lb = b.Length();
                double c = la > 1e-3 && lb > 1e-3 ? Math.Max(Vector3D.Dot(a, b) / (la * lb), 0) : 0;
                double next = limits[i + 1];
                limits[i] = Math.Min(_maxSpeed * Math.Max(c, 0.1), Math.Sqrt(next * next + 2 * brake * BrakeShare * lb) * c * c);
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
            if (LevelFlight)
                // Level flight: braking forward, or holding a descent against gravity.
                brake = Math.Min(MaxAccel(2, 0, mass), MaxAccel(1, 0, mass) - _gravity.Length());
            else if (_useBestThrust && _gyros.Count > 0)
            {
                Vector3D direction;
                BestThrust(reference.WorldMatrix, mass, out direction, out brake);
                brake -= _gravity.Length();     // weak gravity may still pull the wrong way
            }
            else
                brake = MaxAccel(2, 0, mass) - _gravity.Length();
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
            _legStart = _route[_routeIndex];
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
                _legStart = ReferencePosition();
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

            // Every other scan: a ray parallel to the path from a camera facing the
            // way (cameras sit anywhere on the hull, so these rays cover the space
            // the hull sweeps through; thin things beside the centre line, e.g. wind
            // turbine blades, slip between the rays towards the look point).
            _guardParallel = !_guardParallel;
            if (_guardParallel)
                for (int i = 0; i < _cameras.Count; i++)
                {
                    IMyCameraBlock camera = _cameras[(_guardCamera + i) % _cameras.Count];
                    if (!camera.IsWorking || Vector3D.Dot(camera.WorldMatrix.Forward, direction) < 0.75)
                        continue;
                    camera.EnableRaycast = true;
                    Vector3D target = camera.GetPosition() + direction * look;
                    if (!camera.CanScan(target))
                        continue;
                    _guardCamera = (_guardCamera + i + 1) % _cameras.Count;
                    MyDetectedEntityInfo parallel = camera.Raycast(target);
                    if (!parallel.IsEmpty() && parallel.HitPosition.HasValue && !IsOwnHit(parallel))
                        HandleGuardHit(parallel, position, direction, remaining);
                    return;
                }

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
            if (_tracking)
            {
                // Following a moving planet: no route to plan around, stop in front.
                if (hit.Type == MyDetectedEntityType.Asteroid)
                    RegisterObstacle(hit);
                bool waiting = _zoneGoal;
                _route.Clear();
                _route.Add(position + direction * Math.Max(along - StopOffset, 0));
                StartRoute(_targetName);
                _zoneGoal = waiting;
                _message = "Obstacle ahead, stopping: steer past it, then run 'track' to continue";
                return;
            }
            if (_dockAfterRoute && IsBaseHit(hit))
            {
                // Flying to the base: the base and the rock it stands on are
                // expected close to the approach point (docking has its own checks).
                // Behind the stop point they do not matter.
                if (along - ShipRadius > remaining + 5)
                    return;
                if (along - ShipRadius > stopDistance * 1.2 + 5)
                {
                    // In front of the stop point, but the ship can still stop: stop there.
                    _approachTarget = position + direction * (along - ShipRadius - 10);
                    _route[_routeIndex] = _approachTarget;
                    PlanCornerSpeeds();
                    return;
                }
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
            // Terrain ahead on a planet: planning again flies higher.
            if (hit.Type == MyDetectedEntityType.Planet && _planet != null)
                _terrainRadius = Math.Max(_terrainRadius, Vector3D.Distance(hit.HitPosition.Value, _planet.Center) + _approachBuffer);

            // The rock the target lies on: stop earlier. Any other rock (e.g. one the
            // route goes around, larger than known) is an obstacle.
            Obstacle rock = voxel ? FindObstacle(hit.EntityId) : null;
            Vector3D final = _route[_route.Count - 1];
            if (OnLastLeg && rock != null && (rock.Planet ? PlanetAt(final) == rock : Vector3D.Distance(final, rock.Center) < rock.Radius + StopOffset))
            {
                // Most likely the target rock itself: stop earlier. Small corrections
                // are ignored (the end point is only hit roughly anyway), and the new
                // stop point is not put closer than the ship can brake while half the
                // buffer is left, so it does not overshoot and come back.
                double stop = Math.Max(along - StopOffset, Math.Min(stopDistance * 1.1, along - ShipRadius - _approachBuffer * 0.5));
                stop = Math.Max(stop, 0);
                if (stop > remaining - Math.Max(ArrivalTolerance, _approachBuffer * 0.25))
                    return;
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
            else if (_mode == Mode.Dock && _dockPhase == DockPhase.Clearance)
                desired = matrix.Forward;       // hold until the space to turn is checked
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
                    // Not before docking: docking turns into the stored pose itself.
                    desired = _currentSpeed < 3 && !_dockAfterRoute ? toward : matrix.Forward;
                else
                {
                    desired = toward;
                    if (_useBestThrust && !_probing && !LevelFlight)
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
            if (LevelFlight && _mode != Mode.Jump && desiredUp == Vector3D.Zero)
            {
                // In gravity the ship stays level: its up against gravity, the nose
                // turned only horizontally (climbs and descents use the up thrusters).
                desiredUp = -Vector3D.Normalize(_gravity);
                pointing = matrix.Forward;
                desired -= desiredUp * Vector3D.Dot(desired, desiredUp);
                if (desired.LengthSquared() < 0.01)
                    desired = matrix.Forward - desiredUp * Vector3D.Dot(matrix.Forward, desiredUp);
                desired = desired.LengthSquared() > 1e-6 ? Vector3D.Normalize(desired) : Vector3D.CalculatePerpendicularVector(desiredUp);
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
