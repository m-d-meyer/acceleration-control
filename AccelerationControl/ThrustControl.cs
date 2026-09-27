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
    // Acceleration limit: while a movement key is held (or a drive assist is
    // active), the thrusters on that axis get an override that produces the
    // target acceleration, compensating ship mass and natural gravity.
    partial class Program
    {
        const float BlockOverride = 1e-6f;          // tiny override that takes a thruster away from the game's control
        const double InputDeadzone = 0.01;
        const double DampenerHandoverSpeed = 0.2;   // m/s - below this, braking is handed back to the game
        const double AxisAlignment = 0.9;           // min dot product for a thruster to count for an axis

        readonly List<IMyShipController> _controllers = new List<IMyShipController>();
        readonly List<IMyThrust> _allThrusters = new List<IMyThrust>();
        // [axis][0] pushes along +axis, [axis][1] pushes along -axis.
        // Axes match IMyShipController.MoveIndicator: 0 = right, 1 = up, 2 = backward.
        readonly List<IMyThrust>[,] _axisThrusters = new List<IMyThrust>[3, 2];
        readonly bool[] _axisActive = new bool[3];

        IMyShipController _controller;
        IMyShipController _layoutController;
        double _currentSpeed;
        double _forwardSpeed;

        void ControlThrust()
        {
            _controller = FindActiveController();
            if (_controller == null)
            {
                _uiMode = false;
                // Nobody in a seat: a flight (approach, GO, docking, cruise) goes on
                // without a pilot, using any controller of the ship as reference.
                // Overrides of thrusters and gyroscopes work without a pilot.
                if (_mode != Mode.Manual && _layoutController != null && _layoutController.IsFunctional)
                    _controller = _layoutController;
            }
            if ((!_enabled && !_uiMode) || _controller == null)
            {
                ReleaseAll();
                ReleaseGyros();
                return;
            }

            if (_controller != _layoutController)
            {
                ReleaseAll();
                SortThrusters(_controller);
            }

            MatrixD matrix = _controller.WorldMatrix;
            Vector3D gravity = _controller.GetNaturalGravity();
            Vector3D velocity = _controller.GetShipVelocities().LinearVelocity;
            double mass = _controller.CalculateShipMass().PhysicalMass;
            Vector3 move = _controller.MoveIndicator;
            bool dampeners = _controller.DampenersOverride;
            if (_uiMode)
            {
                // The movement keys operate the menu; the ship must not react to them.
                UpdateUiInput(move);
                move = Vector3.Zero;
            }
            _currentSpeed = velocity.Length();
            _forwardSpeed = Vector3D.Dot(velocity, matrix.Forward);

            // Target velocity from the active drive assist, if any.
            Vector3D targetVelocity;
            double assistAccel;
            bool hasTarget = UpdateDriveAssist(matrix, velocity, move, out targetVelocity, out assistAccel);
            if (_uiMode && !hasTarget)
            {
                // Hold the ship (or keep drifting with dampeners off), since the
                // game would otherwise fire the thrusters for the pressed keys.
                hasTarget = true;
                targetVelocity = dampeners ? Vector3D.Zero : velocity;
            }
            // Wind, drag and lift: measured while an assist controls all axes and
            // compensated like gravity (see Planets.cs).
            // Not while cruising: drilling pushes back, and compensating that would
            // push the ship into the rock when the drills break through.
            bool observe = hasTarget && _mode != Mode.Cruise && move.LengthSquared() < InputDeadzone * InputDeadzone;
            if (observe)
                MeasureThrust();    // only while it is used: it reads every thruster
            UpdateDisturbance(observe, velocity, gravity, mass);
            Vector3D external = gravity + _disturbance;

            Vector3D[] axes = { matrix.Right, matrix.Up, matrix.Backward };
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3D dir = axes[axis];
                double input = axis == 0 ? move.X : axis == 1 ? move.Y : move.Z;
                double targetAccel;

                if (Math.Abs(input) > InputDeadzone)
                {
                    targetAccel = MathHelper.Clamp(input, -1, 1) * _limit;
                }
                else if (hasTarget)
                {
                    double error = Vector3D.Dot(targetVelocity - velocity, dir);
                    targetAccel = MathHelper.Clamp(error * _velocityGain, -assistAccel, assistAccel);
                }
                else if (_limitDampeners && dampeners)
                {
                    double speed = Vector3D.Dot(velocity, dir);
                    if (Math.Abs(speed) < DampenerHandoverSpeed)
                    {
                        ReleaseAxis(axis);
                        continue;
                    }
                    targetAccel = MathHelper.Clamp(-speed * _dampenerGain, -_limit, _limit);
                }
                else
                {
                    // No input: leave this axis to the game (normal dampeners / drift).
                    ReleaseAxis(axis);
                    continue;
                }

                // Net force needed along +axis, including the part that cancels
                // gravity (and, for the assists, the measured disturbance).
                double force = mass * (targetAccel - Vector3D.Dot(Math.Abs(input) > InputDeadzone ? gravity : external, dir));
                ApplyAxisForce(axis, force);
            }

            UpdateGyros(_controller, velocity);
        }

        void ApplyAxisForce(int axis, double force)
        {
            List<IMyThrust> push = _axisThrusters[axis, force >= 0 ? 0 : 1];
            List<IMyThrust> idle = _axisThrusters[axis, force >= 0 ? 1 : 0];

            double available = 0;
            foreach (IMyThrust t in push)
                if (t.IsWorking)
                    available += t.MaxEffectiveThrust;

            float fraction = available > 0
                ? (float)MathHelper.Clamp(Math.Abs(force) / available, BlockOverride, 1.0)
                : BlockOverride;

            foreach (IMyThrust t in push)
                t.ThrustOverridePercentage = fraction;
            // Keep the opposing thrusters overridden (near zero) so the game's
            // dampeners cannot fight the controlled acceleration.
            foreach (IMyThrust t in idle)
                t.ThrustOverridePercentage = BlockOverride;

            _axisActive[axis] = true;
        }

        void ReleaseAxis(int axis)
        {
            if (!_axisActive[axis])
                return;
            for (int s = 0; s < 2; s++)
                foreach (IMyThrust t in _axisThrusters[axis, s])
                    t.ThrustOverride = 0f;
            _axisActive[axis] = false;
        }

        // Only touches thrusters when this script owns overrides, unless forced,
        // so manual overrides stay intact while nobody is piloting.
        void ReleaseAll(bool force = false)
        {
            if (!force && !_axisActive[0] && !_axisActive[1] && !_axisActive[2])
                return;
            foreach (IMyThrust t in _allThrusters)
                t.ThrustOverride = 0f;
            for (int a = 0; a < 3; a++)
                _axisActive[a] = false;
        }

        IMyShipController FindActiveController()
        {
            IMyShipController best = null;
            foreach (IMyShipController c in _controllers)
            {
                if (!c.IsFunctional || !c.CanControlShip || !c.IsUnderControl)
                    continue;
                if (best == null || (c.IsMainCockpit && !best.IsMainCockpit))
                    best = c;
            }
            return best;
        }

        // Groups thrusters by the controller axis they push along.
        void SortThrusters(IMyShipController reference)
        {
            for (int a = 0; a < 3; a++)
                for (int s = 0; s < 2; s++)
                    _axisThrusters[a, s].Clear();

            MatrixD m = reference.WorldMatrix;
            Vector3D[] axes = { m.Right, m.Up, m.Backward };

            foreach (IMyThrust t in _allThrusters)
            {
                Vector3D thrustDir = t.WorldMatrix.Backward; // direction the thruster pushes the ship
                for (int a = 0; a < 3; a++)
                {
                    double dot = Vector3D.Dot(thrustDir, axes[a]);
                    if (dot > AxisAlignment) _axisThrusters[a, 0].Add(t);
                    else if (dot < -AxisAlignment) _axisThrusters[a, 1].Add(t);
                }
            }

            _layoutController = reference;
        }

        // Highest acceleration (m/s^2) the thrusters on one axis side can produce.
        double MaxAccel(int axis, int sign, double mass)
        {
            double thrust = 0;
            foreach (IMyThrust t in _axisThrusters[axis, sign])
                if (t.IsWorking)
                    thrust += t.MaxEffectiveThrust;
            return thrust / mass;
        }

        // The ship side with the strongest thrusters: returns its acceleration and
        // the world direction it pushes the ship in, plus the acceleration of the
        // opposite side.
        double BestThrust(MatrixD m, double mass, out Vector3D direction, out double reverse)
        {
            Vector3D[] axes = { m.Right, m.Up, m.Backward };
            double best = -1;
            direction = m.Forward;
            reverse = 0;
            for (int a = 0; a < 3; a++)
                for (int s = 0; s < 2; s++)
                {
                    double accel = MaxAccel(a, s, mass);
                    if (accel <= best)
                        continue;
                    best = accel;
                    direction = s == 0 ? axes[a] : -axes[a];
                    reverse = MaxAccel(a, 1 - s, mass);
                }
            return best;
        }

        // Weakest sideways acceleration, i.e. across the direction the ship flies in:
        // across its strongest side when flights use the strongest thrusters,
        // across the nose otherwise.
        double SideAccel()
        {
            IMyShipController reference = _controller ?? _layoutController;
            if (reference == null)
                return 0;
            double mass = reference.CalculateShipMass().PhysicalMass, best = -1;
            int flightAxis = 2;
            if (_useBestThrust && _gyros.Count > 0)
                for (int a = 0; a < 3; a++)
                    for (int s = 0; s < 2; s++)
                        if (MaxAccel(a, s, mass) > best)
                        {
                            best = MaxAccel(a, s, mass);
                            flightAxis = a;
                        }
            double side = double.MaxValue;
            for (int a = 0; a < 3; a++)
                if (a != flightAxis)
                    for (int s = 0; s < 2; s++)
                        side = Math.Min(side, MaxAccel(a, s, mass));
            return side == double.MaxValue ? 0 : side;
        }

        // Highest acceleration the thrusters can produce along a world direction.
        double MaxAccelAlong(Vector3D worldDir)
        {
            IMyShipController reference = _controller ?? _layoutController;
            if (reference == null)
                return 0;
            double mass = reference.CalculateShipMass().PhysicalMass;
            MatrixD m = reference.WorldMatrix;
            Vector3D[] axes = { m.Right, m.Up, m.Backward };
            double result = double.MaxValue;
            for (int a = 0; a < 3; a++)
            {
                double component = Vector3D.Dot(worldDir, axes[a]);
                if (Math.Abs(component) < 1e-3)
                    continue;
                double capacity = MaxAccel(a, component > 0 ? 0 : 1, mass);
                result = Math.Min(result, capacity / Math.Abs(component));
            }
            return result == double.MaxValue ? 0 : result;
        }
    }
}
