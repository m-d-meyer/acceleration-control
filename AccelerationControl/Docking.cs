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
        Vector3D _dockPosition, _dockAxis, _dockForward, _dockUp;
        long _dockConnectorId, _dockGridId;
        bool _wasConnected;
        bool _dockAfterRoute;           // current flight ends with docking
        DockPhase _dockPhase;
        int _dockWaitTicks, _dockScanIndex, _dockScanSeen;
        bool _dockScanHit;

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
            _dockConnectorId = connector.EntityId;
            _dockGridId = connector.OtherConnector.CubeGrid.EntityId;
            _dockKnown = true;

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
            _message = "Docked. Dock position saved for 'dock'";
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
            _temporaryObstacles.Clear();
            Vector3D from = ReferencePosition();
            Vector3D target = DockApproachPoint;
            _dockAfterRoute = true;
            if (TryStartJump(from, target, BaseName))
                return;
            if (Vector3D.Distance(from, target) < 20)
            {
                StartDockAlign();
                return;
            }
            if (!PlanRoute(from, target, _route))
            {
                _dockAfterRoute = false;
                _message = "No complete route to the base found";
                return;
            }
            StartRoute(BaseName);
            _message = "Flying to the base to dock";
        }

        void StartDockAlign()
        {
            _dockPhase = DockPhase.Clearance;
            _dockWaitTicks = _dockScanIndex = _dockScanSeen = 0;
            _dockScanHit = false;
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
                else if (result == ScanBlocked && ++_dockWaitTicks > DockWaitTicks)
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
                _dockPhase = DockPhase.Corridor;
                _message = "Something is in the way, waiting";
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

        // Scan targets: around the ship's center (space to turn in), or along the
        // corridor from the connector to the dock position, with a ring at the
        // ship's radius.
        void BuildDockScan(bool around, Vector3D connector)
        {
            _dockScanPoints.Clear();
            double r = ShipRadius;
            if (around)
            {
                Vector3D center = Me.CubeGrid.WorldVolume.Center;
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                            if (x != 0 || y != 0 || z != 0)
                                _dockScanPoints.Add(center + Vector3D.Normalize(new Vector3D(x, y, z)) * (r + 10));
                return;
            }
            Vector3D side = Vector3D.CalculatePerpendicularVector(_dockAxis);
            Vector3D up = Vector3D.Cross(_dockAxis, side);
            Vector3D middle = (connector + _dockPosition) / 2;
            _dockScanPoints.Add(_dockPosition);
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                Vector3D ring = (side * Math.Cos(angle) + up * Math.Sin(angle)) * r * 0.8;
                _dockScanPoints.Add(_dockPosition + ring);
                _dockScanPoints.Add(middle + ring);
            }
        }

        const int ScanPending = 0, ScanClear = 1, ScanBlocked = 2;

        // One raycast per tick towards the next scan point. After a full round
        // it reports whether anything other than the base was hit. Points no
        // camera can see are skipped.
        int DockScanStep()
        {
            if (_dockScanIndex >= _dockScanPoints.Count)
            {
                int result = _dockScanHit ? ScanBlocked : ScanClear;
                if (_dockScanSeen == 0 && !_dockScanHit)
                    _message = "No camera can see the docking path, docking without check";
                else if (_dockScanHit)
                    _message = "Something is in the way, waiting";
                _dockScanIndex = _dockScanSeen = 0;
                _dockScanHit = false;
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
                MyDetectedEntityInfo hit = camera.Raycast(point);
                _dockScanSeen++;
                if (!hit.IsEmpty() && !IsBaseGrid(hit.EntityId))
                    _dockScanHit = true;
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
                    if (!IsBaseGrid(e.EntityId) && e.EntityId != Me.CubeGrid.EntityId)
                        return true;
            }
            return false;
        }

        bool IsBaseGrid(long id)
        {
            return id == _dockGridId || _baseGrids.Contains(id);
        }

        string DockPhaseText()
        {
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
