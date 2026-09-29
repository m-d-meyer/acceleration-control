using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using VRageMath;

namespace IngameScript
{
    partial class Program
    {
        // -----------------------------------------------------------------
        //  Landing: 'land' sets down below the ship, 'land GPS:...' flies
        //  there first. The cameras scan the ground under the ship's whole
        //  footprint (its box seen from above, wings and all, plus a margin);
        //  a plane fitted through the hits gives the slope, the largest
        //  deviation from it the unevenness. A bad spot moves the search on a
        //  spiral around the target.
        // -----------------------------------------------------------------

        const double LandMargin = 3;        // m around the footprint
        const double LandLevel = 0.05;      // rad (3 degrees): flatter ground is landed on level
        const int LandTries = 12;
        const double LandStep = 1.2;        // m between rays: every boulder from 2.5 m across is hit (rocksim.py)

        bool _landAfterRoute;
        int _landPhase, _landTry, _landIndex, _landSeen, _landHoles, _landStill;   // phase: 0 to the spot, 1 scanning, 2 turning, 3 down
        Vector3D _landTarget, _landSpot, _landUp, _landE1, _landE2;
        double _landExt0, _landExt1, _landExt2, _landH, _landB, _landC;       // plane: height = H + B u + C v above the spot
        int _landN1, _landN2;
        readonly List<Vector3D> _landHits = new List<Vector3D>();
        double[] _landSum = new double[8];      // sums u, v, h, uu, uv, vv, uh, vh of the hits
        double _landRough;
        string _landState = "";

        // Height of the ship's centre above the ground while scanning: a camera
        // under the ship (45 degree cone) sees the footprint's edges. (1.5 ship
        // radii + 10 m put a large ship 100 m up: long rays, minutes of charge.)
        double LandHeight
        {
            get { return Math.Max(_landExt1, _landExt2) + _landExt0 + 5; }
        }

        Vector3D GroundUp
        {
            get { return -Vector3D.Normalize(_gravity); }
        }

        void Land(string gps)
        {
            if (gps != null)
            {
                GoToGps(gps);
                // Also while the start waits for camera charge (or an undock).
                _landAfterRoute = _mode > Mode.Cruise || _pendingStart || _afterUndock != null;
                return;
            }
            StartLanding();
        }

        void StartLanding()
        {
            _landAfterRoute = false;
            if (!InGravity || _cameras.Count * _landingGears.Count == 0)
            {
                _message = "Needs gravity, landing gear, down cameras";
                return;
            }
            if (!CanHover())
                return;
            _landTarget = ReferencePosition();
            _landTry = 0;
            _mode = Mode.Land;
            _enabled = true;
            NextSpot();
        }

        // Spot number _landTry on a spiral around the target, LandHeight above the ground.
        void NextSpot()
        {
            IMyShipController c = _controller ?? _layoutController;
            Vector3D up = GroundUp, f = c.WorldMatrix.Forward - up * Vector3D.Dot(c.WorldMatrix.Forward, up);
            _landE1 = f.LengthSquared() > 0.01 ? Vector3D.Normalize(f) : Vector3D.CalculatePerpendicularVector(up);
            _landE2 = Vector3D.Cross(up, _landE1);
            // The footprint: the ship's box seen from above, plus the margin;
            // _landExt0 its half height.
            Vector3D min, max;
            GridBox(out min, out max);
            _landExt0 = _landExt1 = _landExt2 = 0;
            for (int i = 0; i < 8; i++)
            {
                Vector3D p = Vector3D.Transform(Corner(i, min, max), Me.CubeGrid.WorldMatrix) - ReferencePosition();
                _landExt0 = Math.Max(_landExt0, Math.Abs(Vector3D.Dot(p, up)));
                _landExt1 = Math.Max(_landExt1, Math.Abs(Vector3D.Dot(p, _landE1)) + LandMargin);
                _landExt2 = Math.Max(_landExt2, Math.Abs(Vector3D.Dot(p, _landE2)) + LandMargin);
            }
            double a = _landTry * 2.4, r = ShipRadius * 0.8 * Math.Sqrt(_landTry), elevation;
            _landSpot = _landTarget + (_landE1 * Math.Cos(a) + _landE2 * Math.Sin(a)) * r;
            Vector3D position = ReferencePosition();
            if (c.TryGetPlanetElevation(MyPlanetElevation.Surface, out elevation))
                _landSpot += up * (LandHeight - elevation - Vector3D.Dot(_landSpot - position, up));
            _landPhase = 0;
            _landState = "to spot " + (_landTry + 1);
        }

        bool LandVelocity(out Vector3D targetVelocity)
        {
            Vector3D position = ReferencePosition(), up = GroundUp, toSpot = _landSpot - position;
            targetVelocity = ClampLength(toSpot * 0.5, Math.Sqrt(BrakeAlong(toSpot) * toSpot.Length()));
            if (_landPhase == 0 && toSpot.Length() < 5 && _currentSpeed < 1)
                StartGroundScan();
            else if (_landPhase == 1)
                GroundScanStep();
            else if (_landPhase == 2 && (_gyros.Count == 0 || _gyrosActive && _alignError < 0.03))
            {
                _landPhase = 3;
                _landState = "descending";
            }
            else if (_landPhase == 3)
            {
                // Height of the lowest landing gear above the plane.
                double gap = double.MaxValue, half = Me.CubeGrid.GridSize / 2;
                IMyShipController c = _controller ?? _layoutController;
                foreach (IMyLandingGear g in _landingGears)
                    gap = Math.Min(gap, PlaneGap(g.GetPosition() - c.WorldMatrix.Up * half));
                bool locked = false;
                foreach (IMyLandingGear g in _landingGears)
                {
                    if (g.LockMode == LandingGearMode.ReadyToLock)
                        g.Lock();
                    locked |= g.IsLocked;
                }
                _landStill = gap < 1.5 && _currentSpeed < 0.1 ? _landStill + 1 : 0;
                if (locked || _landStill > 120)
                {
                    _mode = Mode.Manual;
                    _message = locked ? "Landed" : "Down, gear not locked";
                    return false;
                }
                // Down with what the ship can brake from, near the ground at most
                // 0.3 m/s + 0.3 x the height (thruster lag), the last metre at
                // 0.3 m/s; sideways it holds the spot.
                Vector3D side = toSpot - up * Vector3D.Dot(toSpot, up);
                _targetDistance = gap;
                gap = Math.Max(gap - 1, 0);
                targetVelocity = -up * Math.Min(Math.Sqrt(BrakeAccel(-up) * gap) + 0.3, Math.Min(gap * 0.3 + 0.3, 20)) + ClampLength(side * 0.5, 1);
            }
            return true;
        }

        // Height of a point above the fitted ground plane.
        double PlaneGap(Vector3D p)
        {
            p -= _landSpot;
            return Vector3D.Dot(p, GroundUp) - _landH - _landB * Vector3D.Dot(p, _landE1) - _landC * Vector3D.Dot(p, _landE2);
        }

        void StartGroundScan()
        {
            // Rays LandStep apart, wider for large ships: at most about 2500.
            double step = Math.Max(LandStep, Math.Sqrt(_landExt1 * _landExt2 / 625));
            _landN1 = (int)(_landExt1 * 2 / step) + 2;
            _landN2 = (int)(_landExt2 * 2 / step) + 2;
            _landHits.Clear();
            _landSum = new double[8];
            _landIndex = _landSeen = _landHoles = 0;
            _landRough = 0;
            _landPhase = 1;
        }

        // Rays towards points 20 m below the expected ground, as many per tick as
        // the cameras have charged (waits while a camera that looks there is
        // charging); then the plane from the sums and, 40 points per tick, the
        // largest deviation from it (thousands of points in one tick would hit
        // the instruction limit).
        void GroundScanStep()
        {
            int total = _landN1 * _landN2, n = _landHits.Count;
            Vector3D up = GroundUp;
            for (int k = 0; k < 40; k++)
            {
                if (_landIndex < total)
                {
                    double u = _landExt1 * (2.0 * (_landIndex % _landN1) / (_landN1 - 1) - 1), v = _landExt2 * (2.0 * (_landIndex / _landN1) / (_landN2 - 1) - 1);
                    Vector3D point = _landSpot + _landE1 * u + _landE2 * v - up * (LandHeight + 20);
                    IMyCameraBlock use = null;
                    bool faced = false;
                    foreach (IMyCameraBlock camera in _cameras)
                        if (camera.IsWorking && LooksAt(camera, point))
                        {
                            faced = camera.EnableRaycast = true;
                            if (camera.CanScan(point))
                            {
                                use = camera;
                                break;
                            }
                        }
                    if (faced && use == null)
                        break;
                    if (use != null)
                    {
                        MyDetectedEntityInfo hit = Cast(use, point);
                        _landSeen++;
                        if (hit.IsEmpty() || !hit.HitPosition.HasValue)
                            _landHoles++;       // no ground: a drop or a hole
                        else if (!IsOwnHit(hit))
                        {
                            Vector3D d = hit.HitPosition.Value - _landSpot;
                            _landHits.Add(hit.HitPosition.Value);
                            u = Vector3D.Dot(d, _landE1);
                            v = Vector3D.Dot(d, _landE2);
                            double h = Vector3D.Dot(d, up);
                            double[] add = { u, v, h, u * u, u * v, v * v, u * h, v * h };
                            for (int i = 0; i < 8; i++)
                                _landSum[i] += add[i];
                        }
                    }
                    if (++_landIndex == total)
                    {
                        n = _landHits.Count;
                        if (_landSeen < total * 3 / 4 || n < 6)
                        {
                            _mode = Mode.Manual;
                            _message = string.Format("Down cameras saw {0} of {1} ground points", _landSeen, total);
                            return;
                        }
                        // Least squares plane height = H + B u + C v through the hits.
                        double[] m = _landSum;
                        double su = m[0] / n, sv = m[1] / n, sh = m[2] / n, suu = m[3] - n * su * su, suv = m[4] - n * su * sv,
                            svv = m[5] - n * sv * sv, suh = m[6] - n * su * sh, svh = m[7] - n * sv * sh, det = Math.Max(suu * svv - suv * suv, 1e-6);
                        _landB = (suh * svv - svh * suv) / det;
                        _landC = (svh * suu - suh * suv) / det;
                        _landH = sh - _landB * su - _landC * sv;
                    }
                }
                else if (_landIndex - total < n)
                    _landRough = Math.Max(_landRough, Math.Abs(PlaneGap(_landHits[_landIndex++ - total])));
                else
                {
                    Evaluate();
                    return;
                }
            }
            _landState = "scanning " + 100 * _landIndex / (total + n + 1) + "%";
        }

        void Evaluate()
        {
            Vector3D up = GroundUp;
            double rough = _landRough;
            double slope = Math.Atan(Math.Sqrt(_landB * _landB + _landC * _landC));
            // A tilted landing needs sideways thrust to hold the slope part of gravity.
            if (_landHoles == 0 && rough <= _maxBump && slope <= MathHelper.ToRadians(_maxSlope)
                && (slope < LandLevel || _weakestAccel > 1.2 * _gravity.Length() * Math.Sin(slope)))
            {
                _landUp = slope < LandLevel ? up : Vector3D.Normalize(up - _landE1 * _landB - _landE2 * _landC);
                _landPhase = 2;
                _landState = "turning";
                return;
            }
            _message = string.Format("Spot {0}: {1:0}°, uneven {2:0.0} m, {3} holes", _landTry + 1, MathHelper.ToDegrees(slope), rough, _landHoles);
            if (++_landTry >= LandTries)
            {
                _mode = Mode.Manual;
                _message += ". No spot found";
                return;
            }
            NextSpot();
        }
    }
}
