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
    // Recorded approach paths, like dedicated docking scripts use them:
    //  - While the ship moves, its recent poses (position, forward, up) are kept.
    //  - Docking by hand stores the last PathLength meters of the way in,
    //    relative to the base grid; mining stores the way to a deposit.
    //  - 'dock' / GO fly to the start of the path, then follow it slowly in the
    //    recorded orientation, scanning ahead; 'undock' follows it backwards.
    //  - The base's position and orientation are updated whenever a camera ray
    //    hits the base grid, so a base in another zone frame, or one that moved,
    //    is still docked at correctly.
    partial class Program
    {
        const double CrumbSpacing = 5;          // m between recorded poses
        const double CrumbTurn = 0.26;          // rad (15 degrees) of turning also records a pose
        const double PathLength = 300;          // m of the way kept for a dock or a deposit
        const int MaxCrumbs = 100;
        const double PathSpeed = 8;             // m/s along a recorded path
        const double PathReach = 3;             // m - a path point counts as passed within this
        const double PathEndMargin = 5;         // m - deposits: stop this far before the end of the way
        const int PathGiveUpTicks = 60 * 10;    // blocked this long: the pilot takes over

        class PathPoint
        {
            public Vector3D P, F, U;
        }

        readonly List<PathPoint> _crumbs = new List<PathPoint>();
        readonly List<PathPoint> _path = new List<PathPoint>();         // being followed (world)
        List<PathPoint> _dockPathLocal = new List<PathPoint>();         // way into the dock (base coordinates)
        int _pathIndex, _pathHoldUntil, _pathBlockedSince, _pathScan, _pathEndTicks;
        bool _pathDock, _pathAfterRoute, _pathReverseDock;
        string _pathName = "";
        Vector3D _pathF, _pathU;

        // Base grid pose: world matrix and its bounding box centre in grid
        // coordinates (a raycast reports the box centre and the orientation).
        MatrixD _baseMatrix = MatrixD.Identity;
        Vector3D _baseCenterLocal, _baseHalf;   // box centre and half size in grid coordinates
        bool _baseKnown;
        readonly Vector3D[] _dockLocal = new Vector3D[7];   // connector pos/axis, ship forward/up, ship grid pos/forward/up

        // -----------------------------------------------------------------
        //  Recording
        // -----------------------------------------------------------------

        // Every 10 ticks: a new pose after CrumbSpacing meters or CrumbTurn of turning.
        void RecordCrumbs()
        {
            IMyShipController c = _controller ?? _layoutController;
            if (c == null || _wasConnected)
                return;
            Vector3D p = ReferencePosition();
            MatrixD m = c.WorldMatrix;
            if (_crumbs.Count > 0)
            {
                PathPoint last = _crumbs[_crumbs.Count - 1];
                double moved = Vector3D.Distance(last.P, p);
                if (moved > 1000)
                    _crumbs.Clear();        // jumped or teleported: the way is broken
                else if (moved < CrumbSpacing && Vector3D.Dot(last.F, m.Forward) > Math.Cos(CrumbTurn)
                    && Vector3D.Dot(last.U, m.Up) > Math.Cos(CrumbTurn))
                    return;
            }
            _crumbs.Add(new PathPoint { P = p, F = m.Forward, U = m.Up });
            if (_crumbs.Count > MaxCrumbs)
                _crumbs.RemoveAt(0);
        }

        // The last PathLength meters of the way, ending with the current pose;
        // null if the ship came less than 20 m.
        List<PathPoint> RecentPath()
        {
            IMyShipController c = _controller ?? _layoutController;
            if (c == null)
                return null;
            var path = new List<PathPoint>();
            PathPoint now = new PathPoint { P = ReferencePosition(), F = c.WorldMatrix.Forward, U = c.WorldMatrix.Up };
            path.Add(now);
            double length = 0;
            Vector3D next = now.P;
            for (int i = _crumbs.Count - 1; i >= 0 && length < PathLength; i--)
            {
                length += Vector3D.Distance(_crumbs[i].P, next);
                next = _crumbs[i].P;
                if (Vector3D.Distance(_crumbs[i].P, now.P) > 1)
                    path.Insert(0, _crumbs[i]);
            }
            return length >= 20 && path.Count >= 2 ? path : null;
        }

        // -----------------------------------------------------------------
        //  Base pose
        // -----------------------------------------------------------------

        // At docking: the base grid's pose, and all dock data relative to it.
        void RecordBase(IMyCubeGrid baseGrid)
        {
            // Connected again at the same dock without a new way in (script
            // restarted or world loaded while docked): keep the recorded way.
            List<PathPoint> path = RecentPath();
            List<PathPoint> keep = path == null && HasDockPath && Vector3D.Distance(FromBase(_dockLocal[0]), _dockPosition) < 20
                ? _dockPathLocal : null;
            _baseMatrix = baseGrid.WorldMatrix;
            _baseHalf = new Vector3D(baseGrid.Max.X - baseGrid.Min.X + 1, baseGrid.Max.Y - baseGrid.Min.Y + 1,
                baseGrid.Max.Z - baseGrid.Min.Z + 1) * (baseGrid.GridSize * 0.5);
            _baseCenterLocal = ToBaseDir(baseGrid.WorldAABB.Center - _baseMatrix.Translation);
            _baseKnown = true;
            _dockLocal[0] = ToBase(_dockPosition);
            _dockLocal[1] = ToBaseDir(_dockAxis);
            _dockLocal[2] = ToBaseDir(_dockForward);
            _dockLocal[3] = ToBaseDir(_dockUp);
            _dockLocal[4] = ToBase(_dockGridPosition);
            _dockLocal[5] = ToBaseDir(_dockGridForward);
            _dockLocal[6] = ToBaseDir(_dockGridUp);
            if (keep != null)
                return;
            _dockPathLocal.Clear();
            if (path != null)
                foreach (PathPoint p in path)
                    _dockPathLocal.Add(new PathPoint { P = ToBase(p.P), F = ToBaseDir(p.F), U = ToBaseDir(p.U) });
        }

        Vector3D ToBase(Vector3D world)
        {
            return ToBaseDir(world - _baseMatrix.Translation);
        }

        Vector3D ToBaseDir(Vector3D world)
        {
            return Vector3D.TransformNormal(world, MatrixD.Transpose(_baseMatrix));
        }

        Vector3D FromBase(Vector3D local)
        {
            return _baseMatrix.Translation + Vector3D.TransformNormal(local, _baseMatrix);
        }

        // Called with every camera ray: a hit on the base grid tells where the
        // base is now (another zone's frame, or it moved).
        void NoteHit(MyDetectedEntityInfo hit)
        {
            if (!_baseKnown || hit.IsEmpty() || !hit.HitPosition.HasValue || hit.EntityId != _dockGridId)
                return;
            MatrixD m = hit.Orientation;
            // Plausibility: the reported orientation must give the reported box
            // size, and the hit point must lie in the grid's box.
            if (_baseHalf.X > 0)
            {
                Vector3D size = AbsVec(m.Right) * _baseHalf.X + AbsVec(m.Up) * _baseHalf.Y + AbsVec(m.Backward) * _baseHalf.Z
                    - hit.BoundingBox.HalfExtents;
                Vector3D local = Vector3D.TransformNormal(hit.HitPosition.Value - hit.Position, MatrixD.Transpose(m));
                if (AbsVec(size).Max() > 3 || Math.Abs(local.X) > _baseHalf.X + 3
                    || Math.Abs(local.Y) > _baseHalf.Y + 3 || Math.Abs(local.Z) > _baseHalf.Z + 3)
                    return;
            }
            m.Translation = hit.Position - Vector3D.TransformNormal(_baseCenterLocal, m);
            if (Vector3D.DistanceSquared(m.Translation, _baseMatrix.Translation) < 0.25
                && Vector3D.Dot(m.Forward, _baseMatrix.Forward) > 0.99999 && Vector3D.Dot(m.Up, _baseMatrix.Up) > 0.99999)
                return;
            Vector3D oldStart = DockTarget;
            _baseMatrix = m;
            ApplyBase();
            _mapChanged = true;
            // Flying to the dock: the way in starts somewhere else now.
            if (_mode == Mode.Approach && _pathAfterRoute && _pathDock && _route.Count > 0
                && Vector3D.Distance(oldStart, DockTarget) > 20)
            {
                _route[_route.Count - 1] = DockTarget;
                _replanPending = true;
            }
        }

        static Vector3D AbsVec(Vector3D v)
        {
            return new Vector3D(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));
        }

        // World dock data from the base-relative copy.
        void ApplyBase()
        {
            if (!_baseKnown)
                return;
            _dockPosition = FromBase(_dockLocal[0]);
            _dockAxis = Vector3D.TransformNormal(_dockLocal[1], _baseMatrix);
            _dockForward = Vector3D.TransformNormal(_dockLocal[2], _baseMatrix);
            _dockUp = Vector3D.TransformNormal(_dockLocal[3], _baseMatrix);
            _dockGridPosition = FromBase(_dockLocal[4]);
            _dockGridForward = Vector3D.TransformNormal(_dockLocal[5], _baseMatrix);
            _dockGridUp = Vector3D.TransformNormal(_dockLocal[6], _baseMatrix);
            if (_pathDock && (_mode == Mode.Path || _pathAfterRoute))
                LoadDockPath(_pathReverseDock);
        }

        bool HasDockPath
        {
            get { return _baseKnown && _dockPathLocal.Count >= 2; }
        }

        // Where a dock flight goes first: the start of the recorded way in, or the
        // point in front of the connector.
        Vector3D DockTarget
        {
            get { return HasDockPath ? FromBase(_dockPathLocal[0].P) : DockApproachPoint; }
        }

        void LoadDockPath(bool reverse)
        {
            _path.Clear();
            for (int i = 0; i < _dockPathLocal.Count; i++)
            {
                PathPoint p = _dockPathLocal[reverse ? _dockPathLocal.Count - 1 - i : i];
                _path.Add(new PathPoint { P = FromBase(p.P), F = Vector3D.TransformNormal(p.F, _baseMatrix), U = Vector3D.TransformNormal(p.U, _baseMatrix) });
            }
        }

        // -----------------------------------------------------------------
        //  Following a path
        // -----------------------------------------------------------------

        // Flies to the start of the path (planned route), then follows it. Close
        // to the path already: follows it from the nearest point.
        void StartPathGoal(List<PathPoint> path, string name, bool dock)
        {
            if (path != _path)
            {
                _path.Clear();
                _path.AddRange(path);
            }
            _pathDock = dock;
            _pathReverseDock = false;
            _pathName = name;
            Vector3D position = ReferencePosition();
            int nearest = 0;
            for (int i = 1; i < _path.Count; i++)
                if (Vector3D.DistanceSquared(position, _path[i].P) < Vector3D.DistanceSquared(position, _path[nearest].P))
                    nearest = i;
            if (Vector3D.Distance(position, _path[nearest].P) < Math.Max(ShipRadius, 15))
            {
                StartPathFollow(nearest);
                return;
            }
            StartGoal(_path[0].P, name, false, false, true);
            _pathAfterRoute = _mode == Mode.Approach || _pendingStart;
            if (_mode == Mode.Approach && !_departing)
                _message = "Flying to the start of the recorded way to " + name;
        }

        void StartPathFollow(int index)
        {
            _pathIndex = index;
            _pathHoldUntil = _pathBlockedSince = _pathEndTicks = 0;
            _pathAfterRoute = false;
            if (_pathDock)
                Gate("open");
            _alignError = Math.PI;
            _enabled = true;
            _mode = Mode.Path;
            _message = "Following the recorded way to " + _pathName;
        }

        double PathRemaining(Vector3D position)
        {
            double remaining = Vector3D.Distance(position, _path[_pathIndex].P);
            for (int i = _pathIndex; i < _path.Count - 1; i++)
                remaining += Vector3D.Distance(_path[i].P, _path[i + 1].P);
            return remaining;
        }

        // Target velocity along the path; the gyroscopes hold the recorded pose of
        // the next point. The ship waits while it is turning (tight spaces) and
        // while the cameras see something in the way. Returns false when done.
        bool PathVelocity(out Vector3D targetVelocity)
        {
            targetVelocity = Vector3D.Zero;
            if (_pathDock)
            {
                IMyShipConnector connector = DockConnector();
                if (connector != null && connector.Status == MyShipConnectorStatus.Connected)
                {
                    Gate("close");
                    _mode = Mode.Manual;
                    _message = "Docked";
                    return false;
                }
                if (connector != null && connector.Status == MyShipConnectorStatus.Connectable)
                {
                    connector.Connect();
                    return true;
                }
            }
            if (GateWait())
                return true;
            if (_undockPending)
            {
                _undockPending = false;
                IMyShipConnector c = DockConnector();
                if (c != null)
                    c.Disconnect();
            }
            if (_pathReverseDock)
                UnlockLandingGear();    // auto-lock could catch the base again
            Vector3D position = ReferencePosition();
            int last = _path.Count - 1;
            while (_pathIndex < last)
            {
                Vector3D a = _path[_pathIndex].P, b = _path[_pathIndex + 1].P;
                // Passed a: within reach, or beyond it and close to the segment
                // (off to the side, e.g. above a hangar roof, is not "passed").
                Vector3D ab = Vector3D.Normalize(b - a);
                double along = Vector3D.Dot(position - a, ab);
                if (Vector3D.Distance(position, a) < PathReach
                    || along > 0 && (position - a - ab * along).Length() < PathReach * 2)
                    _pathIndex++;
                else
                    break;
            }
            PathPoint target = _path[_pathIndex];
            _pathF = target.F;
            _pathU = target.U;
            double remaining = PathRemaining(position);
            double togo = remaining - (_pathDock ? 0 : PathEndMargin);
            _targetDistance = _remainingDistance = Math.Max(togo, 0);
            // Last meters into the dock: the connector to its recorded place
            // (a little into the other connector, so it can lock).
            IMyShipConnector own = DockConnector();
            if (_pathDock && remaining < 3 && own != null)
            {
                if (++_pathEndTicks > 60 * 15)
                {
                    _mode = Mode.Manual;
                    _message = "At the dock, but the connector does not lock";
                    return false;
                }
                targetVelocity = ClampLength((_dockPosition - _dockAxis * 0.3 - own.GetPosition()) * 0.5, 0.5);
                return true;
            }
            if (togo <= 0.5)
            {
                if (_currentSpeed < 0.5 && _alignError < 0.05)
                {
                    _mode = Mode.Manual;
                    if (_pathReverseDock)
                    {
                        Gate("close");
                        _message = "Undocked";
                        AfterUndock();
                        return false;
                    }
                    _message = "At " + _pathName + ", aligned as recorded";
                    return false;
                }
                return true;    // hold still and finish turning
            }

            double speed = Math.Min(PathSpeed, Math.Max(togo * 0.4, 0.3));
            if (_pathDock && remaining < DockSlowDistance)
                speed = Math.Min(speed, 1.5);
            if (_alignError > 0.35)
                speed = 0;              // turn into the recorded pose first
            else if (_alignError > 0.1)
                speed *= 0.3;
            if (PathBlocked(position, target.P, remaining))
            {
                if (_pathBlockedSince == 0)
                    _pathBlockedSince = _ticks;
                // Docking and undocking wait longer: a gate may be opening.
                if (_ticks - _pathBlockedSince > PathGiveUpTicks * (_pathDock || _pathReverseDock ? 6 : 1))
                {
                    _mode = Mode.Manual;
                    _message = "The recorded way stays blocked: please take over";
                    _afterUndock = null;
                    return false;
                }
                speed = 0;
            }
            else
                _pathBlockedSince = 0;
            Vector3D to = target.P - position;
            if (to.LengthSquared() > 0.01)
                targetVelocity = Vector3D.Normalize(to) * speed;
            return true;
        }

        // One ray every other tick, parallel to the way from a camera facing it,
        // as far as the next point plus a margin. The base (docking) or the rock
        // (a deposit) at the end of the way does not count there.
        bool PathBlocked(Vector3D position, Vector3D next, double remaining)
        {
            if (_ticks < _pathHoldUntil)
                return true;
            if (_ticks % 2 != 0 || _cameras.Count == 0)
                return false;
            Vector3D to = next - position;
            double distance = to.Length();
            if (distance < 0.5)
                return false;
            Vector3D direction = to / distance;
            for (int i = 0; i < _cameras.Count; i++)
            {
                IMyCameraBlock camera = _cameras[(_pathScan + i) % _cameras.Count];
                if (!camera.IsWorking || Vector3D.Dot(camera.WorldMatrix.Forward, direction) < 0.75)
                    continue;
                camera.EnableRaycast = true;
                double length = Math.Min(distance + 10, remaining + 2);
                Vector3D target = camera.GetPosition() + direction * length;
                if (!camera.CanScan(target))
                    continue;
                _pathScan = (_pathScan + i + 1) % _cameras.Count;
                MyDetectedEntityInfo hit = Cast(camera, target);
                if (hit.IsEmpty() || !hit.HitPosition.HasValue || IsOwnHit(hit))
                    return false;
                bool voxel = hit.Type == MyDetectedEntityType.Asteroid || hit.Type == MyDetectedEntityType.Planet;
                bool atEnd = _pathDock || _pathReverseDock ? IsBaseGrid(hit.EntityId) && InDockedBox(hit.HitPosition.Value)
                    : voxel && remaining < ShipRadius + PathEndMargin + 10;
                if (atEnd)
                    return false;
                _pathHoldUntil = _ticks + 60;   // wait a second, then look again
                _message = "Waiting: " + (voxel ? "rock" : hit.Name) + " on the recorded way";
                return true;
            }
            return false;
        }

        // The base around the docked ship (1.5 m margin): what the ship touches
        // when docked, not a closed gate on the way.
        bool InDockedBox(Vector3D point)
        {
            Vector3D p = Vector3D.Transform(point, MatrixD.Invert(DockedGridMatrix())), min, max;
            GridBox(out min, out max);
            for (int i = 0; i < 3; i++)
                if (p.GetDim(i) < min.GetDim(i) - 1.5 || p.GetDim(i) > max.GetDim(i) + 1.5)
                    return false;
            return true;
        }

        // -----------------------------------------------------------------
        //  Gates: 'open' / 'close' to the base's DockGate script over the
        //  antennas; it answers "busy" while opening and "ready" when open.
        // -----------------------------------------------------------------

        const string GateTag = "AccelDock";
        long _dockBaseConnectorId;
        int _gateSent, _gateState;      // 0 nothing asked, 1 asked, 2 opening, 3 open

        void Gate(string command)
        {
            if (_dockBaseConnectorId == 0)
                return;
            IGC.SendBroadcastMessage(GateTag, command + "|" + _dockBaseConnectorId);
            _gateSent = _ticks;
            _gateState = command == "open" ? 1 : 0;
        }

        // Holds the ship while the base opens the gate: two seconds for an
        // answer (no DockGate script: go on), at most a minute for opening.
        bool GateWait()
        {
            while (IGC.UnicastListener.HasPendingMessage)
            {
                MyIGCMessage m = IGC.UnicastListener.AcceptMessage();
                if (m.Tag == GateTag && _gateState > 0)
                    _gateState = m.Data as string == "ready" ? 3 : 2;
            }
            int waited = _ticks - _gateSent;
            if (_gateState == 1 && waited < 120 || _gateState == 2 && waited < 3600)
            {
                _message = "Waiting for the gate of " + BaseName;
                return true;
            }
            return false;
        }

        // Raycast that also updates the base pose.
        MyDetectedEntityInfo Cast(IMyCameraBlock camera, Vector3D target)
        {
            MyDetectedEntityInfo hit = camera.Raycast(target);
            NoteHit(hit);
            return hit;
        }

        string PathStateText()
        {
            if (_ticks < _pathHoldUntil)
                return "waiting, way blocked";
            if (_alignError > 0.35)
                return "turning into the recorded pose";
            return FormatDistance(_remainingDistance) + " along the recorded way";
        }

        // -----------------------------------------------------------------
        //  Storage: "x,y,z,fx,fy,fz,ux,uy,uz|..."
        // -----------------------------------------------------------------

        static string PathText(List<PathPoint> path)
        {
            var sb = new StringBuilder();
            foreach (PathPoint p in path)
                sb.Append(Num(p.P.X)).Append(',').Append(Num(p.P.Y)).Append(',').Append(Num(p.P.Z)).Append(',')
                    .Append(Dir(p.F)).Append(',').Append(Dir(p.U)).Append('|');
            return sb.ToString();
        }

        static string Dir(Vector3D d)
        {
            return d.X.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ","
                + d.Y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ","
                + d.Z.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

        List<PathPoint> ParsePath(string text)
        {
            var path = new List<PathPoint>();
            foreach (string point in text.Split('|'))
            {
                string[] v = point.Split(',');
                if (v.Length != 9)
                    continue;
                path.Add(new PathPoint { P = ParseVec(v, 0), F = ParseVec(v, 3), U = ParseVec(v, 6) });
            }
            return path;
        }
    }
}
