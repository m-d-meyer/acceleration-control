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
    // Automatic docking at the base.
    //
    // Docking once by hand stores the pose: where the ship's connector was and
    // how the ship was oriented. 'dock' then flies to a point in front of that
    // connector, turns the ship into the stored orientation, moves in slowly
    // along the connector axis and connects. Works for bases that do not move.
    partial class Program
    {
        const double DockLateralGain = 0.6;
        const double DockMaxSpeed = 5;              // m/s on the final approach
        const double DockSlowDistance = 20;         // m - slower from here on
        const int DockWaitTicks = 60 * 120;         // give up after waiting 2 minutes

        enum DockPhase { Clearance, Align, Corridor, Final }

        readonly List<IMyShipConnector> _connectors = new List<IMyShipConnector>();
        readonly List<IMySensorBlock> _sensors = new List<IMySensorBlock>();
        readonly List<MyDetectedEntityInfo> _sensorBuffer = new List<MyDetectedEntityInfo>();
        readonly List<Vector3D> _dockScanPoints = new List<Vector3D>();
        readonly HashSet<long> _baseGrids = new HashSet<long>();
        readonly List<IMyTerminalBlock> _blockBuffer = new List<IMyTerminalBlock>();

        bool _dockKnown;
        string _dockZone = "";          // coordinate zone of the base (see Planets.cs)
        Vector3D _dockPosition, _dockAxis, _dockForward, _dockUp;
        long _dockConnectorId, _dockGridId;
        bool _wasConnected;
        bool _dockAfterRoute;           // current flight ends with docking
        DockPhase _dockPhase;
        int _dockWaitTicks, _dockScanIndex, _dockScanSeen;
        bool _dockScanAround;
        string _dockScanBlocker;
        Vector3D _dockGridPosition, _dockGridForward, _dockGridUp;   // ship grid pose when docked

        // Records the docking pose when a connector gets connected.
        void CheckDocking()
        {
            bool connected = false;
            foreach (IMyShipConnector c in _connectors)
            {
                if (c.Status != MyShipConnectorStatus.Connected || c.OtherConnector == null)
                    continue;
                connected = true;
                if (!_wasConnected)
                    RecordDock(c);
            }
            _wasConnected = connected;
        }

        void RecordDock(IMyShipConnector connector)
        {
            IMyShipController reference = _controller ?? _layoutController;
            if (reference == null)
                return;
            _dockPosition = connector.GetPosition();
            _dockAxis = -connector.WorldMatrix.Forward;     // from the base towards the ship
            _dockForward = reference.WorldMatrix.Forward;
            _dockUp = reference.WorldMatrix.Up;
            MatrixD grid = Me.CubeGrid.WorldMatrix;
            _dockGridPosition = grid.Translation;
            _dockGridForward = grid.Forward;
            _dockGridUp = grid.Up;
            _dockConnectorId = connector.EntityId;
            _dockGridId = connector.OtherConnector.CubeGrid.EntityId;
            _dockKnown = true;
            _dockZone = _zone;
            _dockProvisional = _zoneProvisional;
            RecordBase(connector.OtherConnector.CubeGrid);

            // All grids of the base (including rotor and piston parts), so the
            // docking checks do not treat them as other ships.
            _baseGrids.Clear();
            GridTerminalSystem.GetBlocksOfType(_blockBuffer, b => b.IsSameConstructAs(connector.OtherConnector));
            foreach (IMyTerminalBlock b in _blockBuffer)
                _baseGrids.Add(b.CubeGrid.EntityId);
            if (_mode == Mode.Dock)
                _mode = Mode.Manual;
            AddDeposit(BaseName, _dockPosition, false);
            _mapChanged = true;
            _message = HasDockPath ? "Docked. Dock position and the way in saved for 'dock'" : "Docked. Dock position saved for 'dock'";
        }

        IMyShipConnector DockConnector()
        {
            foreach (IMyShipConnector c in _connectors)
                if (c.EntityId == _dockConnectorId)
                    return c;
            return _connectors.Count > 0 ? _connectors[0] : null;
        }

        Vector3D DockApproachPoint
        {
            // Far enough out that the ship can turn without touching the base.
            get { return _dockPosition + _dockAxis * (ShipRadius * 2 + _dockApproach); }
        }

        // dock: fly to the base (jumping if far) and dock
        void StartDocking()
        {
            if (!_dockKnown)
            {
                _message = "Dock once by hand first, then 'dock' knows where to go";
                return;
            }
            if (DockConnector() == null)
            {
                _message = "No connector on this ship";
                return;
            }
            if (_dockZone != _zone)
            {
                StartZoneGoal(DockTarget, BaseName, _dockZone, true);
                return;
            }
            if (HasDockPath)
            {
                // The recorded way in: to its start, then along it into the dock.
                LoadDockPath(false);
                StartPathGoal(_path, BaseName, true);
                return;
            }
            StartGoal(DockApproachPoint, BaseName, true);
            if (_mode == Mode.Approach && !_departing)
                _message = "Flying to the base to dock";
        }

        // Is the connector close to the approach point or inside the path into the dock?
        bool NearDock()
        {
            IMyShipConnector connector = DockConnector();
            if (connector == null)
                return false;
            Vector3D offset = connector.GetPosition() - _dockPosition;
            double along = Vector3D.Dot(offset, _dockAxis);
            double sideways = (offset - _dockAxis * along).Length();
            return along > -2 && along < DockTravel + 50 && sideways < ShipRadius;
        }

        void StartDockAlign()
        {
            _dockPhase = DockPhase.Clearance;
            _dockWaitTicks = _dockScanIndex = _dockScanSeen = 0;
            _dockScanBlocker = null;
            _dockAfterRoute = false;
            _alignError = Math.PI;
            _enabled = true;
            _mode = Mode.Dock;
        }

        // undock: disconnect and back off along the connector axis.
        void Undock()
        {
            IMyShipConnector connector = DockConnector();
            if (connector == null || connector.Status != MyShipConnectorStatus.Connected)
            {
                _message = "Not docked";
                return;
            }
            Vector3D axis = -connector.WorldMatrix.Forward;
            connector.Disconnect();
            if (HasDockPath)
            {
                // Out the way the ship came in, backwards along the recorded poses.
                LoadDockPath(true);
                _pathDock = false;
                _pathReverseDock = true;
                _pathName = "the way out";
                StartPathFollow(0);
                return;
            }
            _route.Clear();
            _route.Add(ReferencePosition() + axis * (ShipRadius + _dockApproach));
            _temporaryObstacles.Clear();
            StartRoute("undock");
        }

        // Target velocity for the docking phases. Returns false when docking has ended.
        //   Clearance: holding at the approach point, checking the space the ship
        //              needs to turn in for other ships
        //   Align:     turning into the stored orientation
        //   Corridor:  checking the path to the connector
        //   Final:     moving in along the connector axis, still scanning
        bool DockVelocity(out Vector3D targetVelocity)
        {
            targetVelocity = Vector3D.Zero;
            IMyShipConnector connector = DockConnector();
            if (connector == null)
            {
                _mode = Mode.Manual;
                return false;
            }
            if (connector.Status == MyShipConnectorStatus.Connected)
            {
                _mode = Mode.Manual;
                _message = "Docked";
                return false;
            }
            if (connector.Status == MyShipConnectorStatus.Connectable)
            {
                connector.Connect();
                return true;
            }

            Vector3D position = connector.GetPosition();
            Vector3D toApproach = DockApproachPoint - position;
            // Hold at the approach point; while checking the way in, hold where the ship is
            // (it may have stopped on the way in because something showed up).
            if (_dockPhase == DockPhase.Clearance || _dockPhase == DockPhase.Align)
                targetVelocity = ClampLength(toApproach * 0.5, DockMaxSpeed);

            if (_dockPhase == DockPhase.Clearance || _dockPhase == DockPhase.Corridor)
            {
                if (_dockPhase == DockPhase.Clearance && toApproach.Length() > 3)
                    return true;    // get to the approach point first
                BuildDockScan(_dockPhase == DockPhase.Clearance, position);
                int result = DockScanStep();
                if (result == ScanClear)
                {
                    _dockPhase = _dockPhase == DockPhase.Clearance ? DockPhase.Align : DockPhase.Final;
                    _dockWaitTicks = 0;
                }
                // Only the check before turning gives up; on the way in the ship
                // waits in place until the way is clear or the pilot takes over.
                else if (result == ScanBlocked && _dockPhase == DockPhase.Clearance && ++_dockWaitTicks > DockWaitTicks)
                {
                    _mode = Mode.Manual;
                    _message = "Docking cancelled: the way stayed blocked";
                }
                return true;
            }

            if (_dockPhase == DockPhase.Align)
            {
                if (_gyrosActive && _alignError < 0.02 && toApproach.Length() < 3)
                    _dockPhase = DockPhase.Corridor;
                return true;
            }

            // Final approach: keep scanning the rest of the way; hold if blocked.
            BuildDockScan(false, position);
            if (DockScanStep() == ScanBlocked || SensorBlocked())
            {
                if (_dockScanBlocker == null)
                    _message = "Waiting: a sensor reports something nearby";
                _dockPhase = DockPhase.Corridor;
                return true;
            }
            Vector3D offset = _dockPosition - position;
            double along = Vector3D.Dot(offset, -_dockAxis);     // distance still to go
            Vector3D lateral = offset + _dockAxis * along;          // sideways error
            double speed = along > DockSlowDistance ? DockMaxSpeed : MathHelper.Clamp(along * 0.2, 0.3, DockMaxSpeed);
            if (lateral.Length() > 1.5)
                speed = Math.Min(speed, 0.5);   // straighten out first
            if (along < -1)
                speed = -0.5;                   // overshot: back off
            targetVelocity = -_dockAxis * speed + ClampLength(lateral * DockLateralGain, 2);
            _targetDistance = Math.Max(along, 0);
            return true;
        }

        // Scan targets. Around: points on a sphere around the ship's center (space
        // to turn in). Corridor: the corners and center of the ship's box in the
        // docked pose and half way out, so the rays cross the path the ship takes.
        void BuildDockScan(bool around, Vector3D connector)
        {
            _dockScanPoints.Clear();
            _dockScanAround = around;
            if (around)
            {
                Vector3D center = Me.CubeGrid.WorldVolume.Center;
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                            if (x != 0 || y != 0 || z != 0)
                                _dockScanPoints.Add(center + Vector3D.Normalize(new Vector3D(x, y, z)) * (ShipRadius + 10));
                return;
            }
            MatrixD docked = DockedGridMatrix();
            Vector3D min, max;
            GridBox(out min, out max);
            for (int half = 0; half < 2; half++)
            {
                Vector3D shift = _dockAxis * DockTravel * half * 0.5;
                for (int i = 0; i < 8; i++)
                {
                    Vector3D corner = new Vector3D((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
                    _dockScanPoints.Add(Vector3D.Transform(corner, docked) + shift);
                }
                _dockScanPoints.Add(Vector3D.Transform((min + max) / 2, docked) + shift);
            }
        }

        // The ship grid's position and orientation when it was docked.
        MatrixD DockedGridMatrix()
        {
            return MatrixD.CreateWorld(_dockGridPosition, _dockGridForward, _dockGridUp);
        }

        // Bounding box of the ship's blocks in grid coordinates (meters).
        void GridBox(out Vector3D min, out Vector3D max)
        {
            IMyCubeGrid grid = Me.CubeGrid;
            double half = grid.GridSize / 2;
            min = new Vector3D(grid.Min) * grid.GridSize - new Vector3D(half);
            max = new Vector3D(grid.Max) * grid.GridSize + new Vector3D(half);
        }

        // Travel distance from the docked pose out to the approach point.
        double DockTravel
        {
            get { return Vector3D.Distance(_dockPosition, DockApproachPoint); }
        }

        // True if a point lies in the space the ship sweeps through when it
        // moves from the approach point into the docked pose: the ship's box
        // in the docked pose, moved outwards along the connector axis.
        // margin: added around the ship's box (negative: shrinks it). 'to' only
        // counts the part of the path still ahead of the ship.
        bool InDockPath(Vector3D point, double margin)
        {
            MatrixD toDocked = MatrixD.Invert(DockedGridMatrix());
            Vector3D p = Vector3D.Transform(point, toDocked);
            Vector3D a = Vector3D.TransformNormal(_dockAxis, toDocked);
            Vector3D min, max;
            GridBox(out min, out max);
            IMyShipConnector connector = DockConnector();
            double ahead = connector != null ? Vector3D.Dot(connector.GetPosition() - _dockPosition, _dockAxis) : DockTravel;
            double from = 0, to = MathHelper.Clamp(ahead + 2, 0, DockTravel);
            for (int i = 0; i < 3; i++)
            {
                double pi = p.GetDim(i), ai = a.GetDim(i), lo = min.GetDim(i) - margin, hi = max.GetDim(i) + margin;
                if (Math.Abs(ai) < 1e-9)
                {
                    if (pi < lo || pi > hi)
                        return false;
                    continue;
                }
                // p - a * t must lie between lo and hi
                double t1 = (pi - hi) / ai, t2 = (pi - lo) / ai;
                from = Math.Max(from, Math.Min(t1, t2));
                to = Math.Min(to, Math.Max(t1, t2));
                if (from > to)
                    return false;
            }
            return true;
        }

        const int ScanPending = 0, ScanClear = 1, ScanBlocked = 2;

        // One raycast per tick towards the next scan point. After a full round
        // it reports whether something blocks the way. Points no camera can see
        // are skipped. Only hits inside the checked space count: within the
        // ship's turning radius (other ships and players), or in the path into
        // the dock. Parts of the base count only in the space to turn in.
        int DockScanStep()
        {
            if (_dockScanIndex >= _dockScanPoints.Count)
            {
                int result = _dockScanBlocker != null ? ScanBlocked : ScanClear;
                if (_dockScanSeen == 0 && result == ScanClear)
                    _message = "No camera can see the docking path, docking without check";
                else if (result == ScanBlocked)
                    _message = "Waiting: " + _dockScanBlocker + " is in the way";
                _dockScanIndex = _dockScanSeen = 0;
                _dockScanBlocker = null;
                return result;
            }
            Vector3D point = _dockScanPoints[_dockScanIndex++];
            foreach (IMyCameraBlock camera in _cameras)
            {
                if (!camera.IsWorking)
                    continue;
                camera.EnableRaycast = true;
                if (!camera.CanScan(point))
                    continue;
                MyDetectedEntityInfo hit = Cast(camera, point);
                _dockScanSeen++;
                // The base itself never blocks the way in, but it does count in the
                // space to turn in (wind turbines, antennas and the like stick out).
                if (!hit.IsEmpty() && hit.HitPosition.HasValue && (_dockScanAround || !IsBaseGrid(hit.EntityId)) && !IsOwnHit(hit))
                {
                    Vector3D at = hit.HitPosition.Value;
                    bool voxel = hit.Type == MyDetectedEntityType.Asteroid || hit.Type == MyDetectedEntityType.Planet;
                    // Rock counts only if it is clearly inside the ship's path (the
                    // docked pose itself was free), ships and players with a margin.
                    bool blocks = _dockScanAround
                        ? !voxel && Vector3D.Distance(at, Me.CubeGrid.WorldVolume.Center) < ShipRadius + 5
                        : InDockPath(at, voxel ? -1 : 1.5);
                    if (blocks)
                        _dockScanBlocker = (voxel ? "rock" : hit.Name) + " (" + FormatDistance(Vector3D.Distance(at, camera.GetPosition())) + ")";
                }
                break;
            }
            return ScanPending;
        }

        // Optional sensors (tagged like the camera, e.g. [Accel]) report other
        // ships and players near the ship during the final approach.
        bool SensorBlocked()
        {
            foreach (IMySensorBlock sensor in _sensors)
            {
                if (!sensor.IsWorking || !sensor.CustomName.Contains(_cameraTag))
                    continue;
                _sensorBuffer.Clear();
                sensor.DetectedEntities(_sensorBuffer);
                foreach (MyDetectedEntityInfo e in _sensorBuffer)
                    if (!IsBaseGrid(e.EntityId) && !IsOwnHit(e))
                        return true;
            }
            return false;
        }

        bool IsBaseGrid(long id)
        {
            return id == _dockGridId || _baseGrids.Contains(id);
        }

        // The base itself, or the rock the dock position lies on.
        bool IsBaseHit(MyDetectedEntityInfo hit)
        {
            if (IsBaseGrid(hit.EntityId))
                return true;
            Obstacle rock = hit.Type == MyDetectedEntityType.Asteroid ? FindObstacle(hit.EntityId) : null;
            return rock != null && Vector3D.Distance(_dockPosition, rock.Center) < rock.Radius + 50;
        }

        string DockTitle
        {
            get { return _mode == Mode.Path && !_pathDock ? "TO " + _pathName : "DOCKING"; }
        }

        string DockPhaseText()
        {
            if (_mode == Mode.Path)
                return PathStateText();
            switch (_dockPhase)
            {
                case DockPhase.Clearance: return "checking space to turn";
                case DockPhase.Align: return "turning into position";
                case DockPhase.Corridor: return "checking the way in";
                default: return FormatDistance(_targetDistance) + " to the connector";
            }
        }

        static Vector3D ClampLength(Vector3D v, double max)
        {
            double length = v.Length();
            return length > max ? v * (max / length) : v;
        }
    }
}
