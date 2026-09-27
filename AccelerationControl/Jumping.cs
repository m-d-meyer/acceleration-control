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
    // Jump drive: for long distances GO first jumps towards the target
    // ("blind jump" along the ship's nose with a set distance), then flies the
    // rest. The jump ends JumpArrival meters before the target, in free space.
    //
    // The game may not accept a jump started by a script. Then the script
    // keeps the ship aligned with the distance set and asks the pilot to press
    // the jump drive's Jump action on the toolbar.
    partial class Program
    {
        const double JumpAlignTolerance = 0.01;     // rad (about 0.6 degrees)
        const double JumpDetected = 1000;           // m moved at once = the jump happened
        const int JumpTimeoutTicks = 60 * 90;
        const int JumpManualTicks = 60 * 3;         // no jump after this: ask the pilot to press Jump

        Vector3D _jumpDirection, _jumpFrom, _afterJumpTarget;
        double _jumpDistance;
        string _afterJumpName = "";
        bool _jumpTriggered;
        int _jumpTicks;
        string _jumpState = "";

        // Starts a jump if it is worth it and possible. Returns false otherwise,
        // then the caller flies the whole way.
        bool TryStartJump(Vector3D from, Vector3D stopPoint, string name)
        {
            IMyShipController c = _controller ?? _layoutController;
            if (!_useJump || _jumpDrives.Count == 0 || c == null || c.GetNaturalGravity().LengthSquared() > 0.01)
                return false;
            double min = 0, max = 0;
            foreach (IMyJumpDrive d in _jumpDrives)
                if (d.IsFunctional)
                {
                    min = Math.Max(min, d.MinJumpDistanceMeters);
                    max = Math.Max(max, d.MaxJumpDistanceMeters);
                }

            Vector3D ray = stopPoint - from;
            double distance = ray.Length();
            double jump = Math.Min(max, distance - _jumpArrival);
            if (jump < Math.Max(min, _jumpThreshold))
                return false;

            // Shorten the jump until the destination is clear of known obstacles.
            Vector3D direction = ray / distance;
            for (int i = 0; i < 8 && jump >= min; i++, jump *= 0.85)
            {
                if (!IsFreeForJump(from + direction * jump))
                    continue;
                _jumpDirection = direction;
                _jumpDistance = jump;
                _jumpFrom = from;
                _afterJumpTarget = stopPoint;
                _afterJumpName = name;
                _jumpTriggered = false;
                _jumpTicks = 0;
                _alignError = Math.PI;
                _enabled = true;
                _mode = Mode.Jump;
                _message = "Jump " + FormatDistance(jump) + " towards " + name;
                return true;
            }
            return false;
        }

        bool IsFreeForJump(Vector3D point)
        {
            foreach (Obstacle o in _obstacles)
                if (Vector3D.Distance(point, o.Center) < (o.Planet ? o.GravityRadius : o.Radius) + ShipRadius + _jumpClearance)
                    return false;
            return true;
        }

        // Runs every tick in jump mode, while the thrust control holds the ship still.
        void UpdateJump()
        {
            if (!_jumpTriggered)
            {
                IMyJumpDrive ready = null;
                foreach (IMyJumpDrive d in _jumpDrives)
                    if (d.IsWorking && d.Status == MyJumpDriveStatus.Ready)
                        ready = d;
                if (ready == null)
                    _jumpState = "charging";
                else if (!_gyrosActive || _alignError > JumpAlignTolerance || _currentSpeed > 1)
                    _jumpState = "aligning";
                else
                {
                    foreach (IMyJumpDrive d in _jumpDrives)
                        d.JumpDistanceMeters = (float)_jumpDistance;
                    ready.ApplyAction("Jump");
                    _jumpTriggered = true;
                    _jumpState = "jumping";
                }
                return;
            }

            _jumpTicks++;
            bool counting = false;
            foreach (IMyJumpDrive d in _jumpDrives)
                if (d.Status == MyJumpDriveStatus.Jumping)
                    counting = true;
            if (counting)
                _jumpState = "jumping";
            else if (_jumpTicks == JumpManualTicks)
            {
                _jumpState = "press JUMP on your toolbar";
                _message = "Ship aligned, distance set: press the jump drive's Jump action";
            }

            if (Vector3D.Distance(ReferencePosition(), _jumpFrom) > JumpDetected)
            {
                // Arrived: fly the rest.
                Vector3D from = ReferencePosition();
                if (PlanRoute(from, _afterJumpTarget, _route))
                {
                    StartRoute(_afterJumpName);
                    _message = "Jump complete, " + FormatDistance(_routeLength) + " to go";
                }
                else
                {
                    _mode = Mode.Manual;
                    _message = "Jump complete, no route for the rest found";
                }
            }
            else if (_jumpTicks > JumpTimeoutTicks)
            {
                _mode = Mode.Manual;
                _message = "No jump within 90 s, cancelled";
            }
        }
    }
}
