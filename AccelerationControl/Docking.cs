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

        readonly List<IMyShipConnector> _connectors = new List<IMyShipConnector>();

        bool _dockKnown;
        Vector3D _dockPosition, _dockAxis, _dockForward, _dockUp;
        long _dockConnectorId, _dockGridId;
        bool _wasConnected;
        bool _dockAfterRoute;           // current flight ends with docking
        bool _dockFinal;                // on the final approach (else aligning)

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
            get { return _dockPosition + _dockAxis * (ShipRadius + _dockApproach); }
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
            _dockFinal = false;
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

        // Target velocity while aligning in front of the connector and on the
        // final approach. Returns false when docking has ended.
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
            if (!_dockFinal)
            {
                // Hold the connector at the approach point until the ship is turned.
                Vector3D error = DockApproachPoint - position;
                targetVelocity = ClampLength(error * 0.5, DockMaxSpeed);
                if (_gyrosActive && _alignError < 0.02 && error.Length() < 3)
                    _dockFinal = true;
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

        static Vector3D ClampLength(Vector3D v, double max)
        {
            double length = v.Length();
            return length > max ? v * (max / length) : v;
        }
    }
}
