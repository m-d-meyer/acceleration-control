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
        const double JumpAlignTolerance = 0.035;    // rad (2 degrees: about 700 m off after 20 km, corrected afterwards)
        const int JumpAlignTicks = 60 * 20;         // after aligning this long, a looser tolerance is accepted
        const double JumpLooseTolerance = 0.087;    // rad (5 degrees)
        const int JumpTimeoutTicks = 60 * 90;
        const int JumpManualTicks = 60 * 3;         // no jump after this: ask the pilot to press Jump

        Vector3D _jumpDirection, _afterJumpTarget;
        double _jumpDistance;
        string _afterJumpName = "";
        bool _jumpTriggered;
        int _jumpTicks, _jumpTriggerTick;
        string _jumpState = "";

        // Starts a jump if it is worth it and possible. Returns false otherwise,
        // then the caller flies the whole way.
        // Jumps along the first leg of the planned route (legEnd), which is
        // known to be clear, and flies the rest to stopPoint afterwards.
        bool TryStartJump(Vector3D from, Vector3D legEnd, Vector3D stopPoint, string name)
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

            Vector3D ray = legEnd - from;
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
                _afterJumpTarget = stopPoint;
                _afterJumpName = name;
                _jumpTriggered = false;
                _jumpTicks = 0;
                _alignError = Math.PI;
                _enabled = true;
                _mode = Mode.Jump;
                _jumpState = "aligning";
                // Set the distance right away, so the pilot can also jump by hand.
                foreach (IMyJumpDrive d in _jumpDrives)
                    d.JumpDistanceMeters = (float)jump;
                _message = "Jump " + FormatDistance(jump) + " towards " + name;
                return true;
            }
            return false;
        }

        // Once a second during a flight: a flight that started in gravity (no jumps
        // there) or whose target changed may be long enough for a jump now. Tried
        // once per leg, only out of gravity, with the drive charged and if the leg
        // takes more than two minutes to fly.
        int _jumpCheckedLeg = -1;

        void CheckJumpOnRoute()
        {
            if (_mode != Mode.Approach || _probing || _departing || !_useJump || _jumpDrives.Count == 0
                || _jumpCheckedLeg == _routeIndex || _gravity.LengthSquared() > 0.01 || _jumpMax <= 0 || _jumpStored < _jumpMax * 0.99)
                return;
            _jumpCheckedLeg = _routeIndex;
            Vector3D from = ReferencePosition();
            if (Vector3D.Distance(from, _route[_routeIndex]) > Math.Max(_jumpThreshold, _currentSpeed * 120)
                && TryStartJump(from, _route[_routeIndex], _route[_route.Count - 1], _targetName))
                _message = "Out of the gravity: jumping on towards " + _targetName;
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
            // The jump happened, started by the script or by the pilot: fly the rest.
            // (a sudden position jump, not the distance flown: the ship may still be
            // moving when the jump is planned, e.g. when the target changed in flight).
            if (_jumped)
            {
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
                return;
            }

            IMyJumpDrive ready = null;
            bool counting = false;
            foreach (IMyJumpDrive d in _jumpDrives)
            {
                if (d.IsWorking && d.Status == MyJumpDriveStatus.Ready)
                    ready = d;
                if (d.Status == MyJumpDriveStatus.Jumping)
                    counting = true;
            }
            if (counting)
            {
                _jumpState = "jumping";
                return;
            }
            if (ready == null)
            {
                _jumpState = "charging";
                return;
            }

            // Ready: align (good enough within JumpAlignTolerance, or after
            // JumpAlignTicks at the latest), then jump.
            _jumpTicks++;
            double tolerance = _jumpTicks < JumpAlignTicks ? JumpAlignTolerance : JumpLooseTolerance;
            bool aligned = _gyrosActive && _alignError < tolerance && _currentSpeed < 1;
            if (!_jumpTriggered && !aligned)
            {
                _jumpState = "aligning";
                return;
            }
            if (!_jumpTriggered)
            {
                // The game may not offer the Jump action to scripts; then the pilot jumps.
                ITerminalAction jump = ready.GetActionWithName("Jump");
                if (jump != null)
                    jump.Apply(ready);
                _jumpTriggered = true;
                _jumpTriggerTick = jump != null ? _jumpTicks : _jumpTicks - JumpManualTicks + 1;
                _jumpState = "jumping";
            }
            else if (_jumpTicks - _jumpTriggerTick == JumpManualTicks)
            {
                _jumpState = "press JUMP on your toolbar";
                _message = "Aligned: press the jump drive's Jump action";
            }
            else if (_jumpTicks > JumpTimeoutTicks)
            {
                _mode = Mode.Manual;
                _message = "No jump within 90 s, cancelled";
            }
        }
    }
}
