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
    // Planets:
    //  - Zones (Real Solar Systems mod): the real planets sit far away from the
    //    moving proxy planets, and a ship that gets close to a proxy is
    //    teleported to the real planet. Each planet zone has its own coordinates,
    //    so map entries remember the zone they were recorded in. A teleport ends
    //    the current flight, unless it brought the ship into the zone of the
    //    target (then the flight continues there).
    //  - Flights on a planet: climb to a cruise height, follow the curvature,
    //    descend vertically above the target. The ship is kept level.
    //  - Atmosphere: speed limit inside, braking in time before entering it.
    //  - Wind and aerodynamics: a disturbance observer measures the external
    //    acceleration (wind, drag, wing lift) and the thrust control compensates it.
    partial class Program
    {
        const double TeleportDistance = 200;        // m of unexplained movement in one tick = teleport
        const int JumpGraceTicks = 60 * 3;          // a jump drive jumping this recently explains a position jump
        const double InGravityLimit = 0.5;          // m/s^2 - above this the ship flies level and without flips
        const double DisturbanceTime = 1.0;         // s - time constant of the disturbance observer
        const double MaxDisturbance = 6;            // m/s^2 - larger estimates are clipped
        const double PlanetArcSag = 100;            // m - planet routes stay within this of the cruise sphere
        const double ZoneExitDistance = 1e6;        // m - leaving a planet zone: climb at most this far
        const double AirDetected = 0.02;            // thruster effectiveness change that counts as air

        string _zone = "";              // "" = space (proxy zone), else the zone of one real planet
        bool _planetZonesSeen;          // a teleport was seen: zones are in use (Real Solar Systems)
        readonly List<Obstacle> _otherObstacles = new List<Obstacle>();    // obstacles of other zones
        Vector3D _lastPosition, _lastVelocity;
        bool _haveLastPosition, _hadPlanet;
        int _jumpSeenTick = -100000;

        // Flight to an entry in another zone: waits for the zone change.
        bool _zoneGoal, _zoneGoalDock;
        Vector3D _zoneGoalTarget;
        string _zoneGoalZone = "", _zoneGoalName = "";

        Vector3D _gravity;              // natural gravity, updated every tick
        Obstacle _planet;               // planet whose gravity the ship is in (this zone)
        double _terrainRadius;          // highest terrain (distance from the planet center) seen on this flight

        readonly List<IMyThrust> _atmoThrusters = new List<IMyThrust>();
        readonly List<IMyThrust> _ionThrusters = new List<IMyThrust>();
        double _air = -1;               // air density estimate 0..1, -1 = unknown (no suitable thrusters)

        Vector3D _disturbance;          // external acceleration (wind, drag, lift), m/s^2
        Vector3D _thrustForce;          // total thrust force of the last tick, N
        Vector3D _observerVelocity;
        bool _observerReady;

        bool InGravity
        {
            get { return _gravity.LengthSquared() > InGravityLimit * InGravityLimit; }
        }

        bool PlanetZones
        {
            get { return _planetZonesConfig || _planetZonesSeen; }
        }

        static string ZoneKey(Vector3D planetCenter)
        {
            return "P" + Math.Round(planetCenter.X / 1000) + "," + Math.Round(planetCenter.Y / 1000) + "," + Math.Round(planetCenter.Z / 1000);
        }

        string ZoneName(string zone)
        {
            return zone == "" ? "space" : "planet zone";
        }

        // Every tick, before the thrust control: gravity, teleports and zone changes.
        void UpdateZone()
        {
            IMyShipController c = _controller ?? _layoutController;
            if (c == null)
                return;
            _gravity = c.GetNaturalGravity();
            Vector3D position = ReferencePosition(), velocity = c.GetShipVelocities().LinearVelocity;
            foreach (IMyJumpDrive d in _jumpDrives)
                if (d.Status == MyJumpDriveStatus.Jumping)
                    _jumpSeenTick = _ticks;

            bool teleported = false;
            if (_haveLastPosition)
            {
                double dt = MathHelper.Clamp(Runtime.TimeSinceLastRun.TotalSeconds, 1 / TicksPerSecond, 1);
                Vector3D predicted = _lastPosition + (_lastVelocity + velocity) * 0.5 * dt;
                teleported = Vector3D.Distance(position, predicted) > TeleportDistance && _ticks - _jumpSeenTick > JumpGraceTicks;
            }
            _lastPosition = position;
            _lastVelocity = velocity;
            _haveLastPosition = true;

            // A teleport into or out of a planet's gravity is how Real Solar Systems
            // moves ships between zones: from now on entries remember their zone.
            Vector3D center;
            bool hasPlanet = c.TryGetPlanetPosition(out center);
            if (teleported && hasPlanet != _hadPlanet)
                _planetZonesSeen = true;
            _hadPlanet = hasPlanet;
            string zone = PlanetZones && hasPlanet ? ZoneKey(center) : "";
            bool changed = zone != _zone;
            if (changed)
                SwitchZone(zone);
            if (teleported)
                OnTeleport(changed);
        }

        // Keeps only this zone's obstacles in _obstacles, so route planning,
        // the map and the jump checks see only what exists here.
        void SwitchZone(string zone)
        {
            _otherObstacles.AddRange(_obstacles);
            _obstacles.Clear();
            for (int i = _otherObstacles.Count - 1; i >= 0; i--)
                if (_otherObstacles[i].Zone == zone)
                {
                    _obstacles.Add(_otherObstacles[i]);
                    _otherObstacles.RemoveAt(i);
                }
            _zone = zone;
            _planet = null;
            _selected = null;
            _temporaryObstacles.Clear();
            _previewRoute.Clear();
        }

        void OnTeleport(bool zoneChanged)
        {
            _observerReady = false;
            _disturbance = Vector3D.Zero;
            _replanPending = false;
            if (!zoneChanged && _mode == Mode.Approach && !_probing && !_departing && !_zoneGoal)
            {
                // Same zone (e.g. between the orbit and the surface of a planet): the
                // target's coordinates still hold, only the way there is planned again.
                Replan();
                _message = "Teleported within the zone, route planned again";
                return;
            }
            if (!zoneChanged && _mode == Mode.Dock)
            {
                StartDocking();
                _message = "Teleported within the zone, docking again";
                return;
            }
            if (_zoneGoal && _zoneGoalZone == _zone)
            {
                _zoneGoal = false;
                if (_zoneGoalDock)
                    StartDocking();
                else
                    StartGoal(_zoneGoalTarget, _zoneGoalName, false);
                _message = "Zone changed, flying on to " + _zoneGoalName;
                return;
            }
            bool flying = _mode == Mode.Approach || _mode == Mode.Dock || _mode == Mode.Jump;
            if (flying)
            {
                _mode = Mode.Manual;
                _dockAfterRoute = _departing = _resumeGoal = false;
            }
            _message = "Teleported (" + ZoneName(_zone) + ")" + (flying ? ", flight stopped" : "")
                + (_zoneGoal ? ". " + _zoneGoalName + " is in another zone: fly there, the flight continues after the zone change" : "");
        }

        // GO to an entry recorded in another zone. In a planet zone the ship
        // climbs out first; in space it waits until the pilot has flown into
        // the target's zone (the proxy planets move, the script cannot see them).
        void StartZoneGoal(Vector3D target, string name, string zone, bool dock)
        {
            _zoneGoal = true;
            _zoneGoalTarget = target;
            _zoneGoalName = name;
            _zoneGoalZone = zone;
            _zoneGoalDock = dock;
            IMyShipController c = _controller ?? _layoutController;
            Vector3D center;
            if (_zone != "" && c != null && c.TryGetPlanetPosition(out center))
            {
                Vector3D up = Vector3D.Normalize(ReferencePosition() - center);
                _route.Clear();
                _route.Add(ReferencePosition() + up * ZoneExitDistance);
                _temporaryObstacles.Clear();
                _dockAfterRoute = false;
                StartRoute("leaving the planet");
                _zoneGoal = true;       // StartRoute does not touch it, StartGoal would
                _message = "Leaving the planet zone, then on to " + name;
            }
            else
                _message = name + " is in another zone: fly there, the flight continues after the zone change";
        }

        // -----------------------------------------------------------------
        //  Planet routes
        // -----------------------------------------------------------------

        // The planet (of this zone) whose gravity well contains from or to and
        // which the direct line passes lower than the cruise height.
        Obstacle PlanetOnRoute(Vector3D from, Vector3D to, out double cruise)
        {
            cruise = 0;
            foreach (Obstacle o in _obstacles)
            {
                if (!o.Planet)
                    continue;
                double rf = Vector3D.Distance(from, o.Center), rt = Vector3D.Distance(to, o.Center);
                if (rf > o.GravityRadius && rt > o.GravityRadius)
                    continue;
                cruise = CruiseRadius(o, from, to);
                if (DistanceToSegment(o.Center, from, to) < cruise - 1)
                    return o;
            }
            return null;
        }

        // Distance from the planet center to fly at: above the ground at the
        // ship, the target and any terrain the guard saw on this flight.
        double CruiseRadius(Obstacle planet, Vector3D from, Vector3D to)
        {
            double ground = planet.Radius, elevation;
            IMyShipController c = _controller ?? _layoutController;
            if (c != null && Vector3D.Distance(from, ReferencePosition()) < 100
                && c.TryGetPlanetElevation(MyPlanetElevation.Surface, out elevation))
                ground = Math.Max(ground, Vector3D.Distance(from, planet.Center) - elevation);
            double rt = Vector3D.Distance(to, planet.Center);
            if (rt < planet.GravityRadius)
                ground = Math.Max(ground, rt);
            ground = Math.Max(ground, _terrainRadius);
            double height = MathHelper.Clamp(Vector3D.Distance(from, to) * 0.25, 200, Math.Max(_planetCruiseHeight, 200));
            return ground + height + ShipRadius;
        }

        // Adds climb and arc waypoints around the planet to the route and
        // returns the point from where the rest is flown directly (above a
        // target on the surface, or where a target in space is in clear view).
        Vector3D PlanPlanetArc(Obstacle planet, double cruise, Vector3D from, Vector3D to, List<Vector3D> route)
        {
            Vector3D c = planet.Center;
            double rf = Vector3D.Distance(from, c), rt = Vector3D.Distance(to, c);
            Vector3D uf = (from - c) / Math.Max(rf, 1), ut = (to - c) / Math.Max(rt, 1);
            double r = Math.Max(cruise, rf);
            Vector3D point = from;
            if (r - rf > 20)
            {
                point = c + uf * r;         // climb straight up
                AddPlanWaypoint(route, point);
            }
            bool below = rt < r;
            double angle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(uf, ut), -1, 1));
            Vector3D axis = Vector3D.Cross(uf, ut);
            axis = axis.LengthSquared() > 1e-12 ? Vector3D.Normalize(axis) : Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(uf));
            Vector3D side = Vector3D.Cross(axis, uf);
            double step = 2 * Math.Acos(1 - Math.Min(PlanetArcSag / r, 1));
            int steps = (int)Math.Ceiling(angle / Math.Max(step, 0.01));
            for (int i = 1; i <= steps; i++)
            {
                // A target out in space: head straight for it once the line clears the planet.
                if (!below && DistanceToSegment(c, point, to) >= r - PlanetArcSag)
                    break;
                double a = angle * i / steps;
                point = c + (uf * Math.Cos(a) + side * Math.Sin(a)) * r;
                AddPlanWaypoint(route, point);
            }
            return point;
        }

        void AddPlanWaypoint(List<Vector3D> route, Vector3D point)
        {
            route.Add(point);
            _planOwners.Add(null);
        }

        // The planet the point lies in the gravity well of (this zone), or null.
        Obstacle PlanetAt(Vector3D point)
        {
            foreach (Obstacle o in _obstacles)
                if (o.Planet && Vector3D.Distance(point, o.Center) < o.GravityRadius)
                    return o;
            return null;
        }

        // -----------------------------------------------------------------
        //  Atmosphere
        // -----------------------------------------------------------------

        // Air density estimate from the thrusters: atmospheric thrusters gain
        // and ion thrusters lose effectiveness in air. -1 if the ship has neither.
        void UpdateAir()
        {
            _air = -1;
            foreach (IMyThrust t in _atmoThrusters)
                if (t.IsFunctional && t.MaxThrust > 0)
                {
                    _air = Math.Max(_air, t.MaxEffectiveThrust / t.MaxThrust);
                    break;
                }
            if (_air < 0)
                foreach (IMyThrust t in _ionThrusters)
                    if (t.IsFunctional && t.MaxThrust > 0)
                    {
                        _air = MathHelper.Clamp((1 - t.MaxEffectiveThrust / t.MaxThrust) / 0.8, 0, 1);
                        break;
                    }
            // Learn where the atmosphere of this planet starts.
            if (_planet != null && _air > AirDetected)
            {
                double r = Vector3D.Distance(ReferencePosition(), _planet.Center);
                if (r > _planet.AtmosphereRadius)
                {
                    _planet.AtmosphereRadius = r;
                    _mapChanged = true;
                }
            }
        }

        // Distance from the planet center where the atmosphere starts (0 = none known).
        double AtmosphereTop(Obstacle planet)
        {
            if (planet.AtmosphereRadius > 0)
                return planet.AtmosphereRadius;
            return _atmosphereHeight > 0 ? planet.Radius + _atmosphereHeight : 0;
        }

        // Speed limit from the atmosphere: AtmosphereSpeed inside it, and above
        // it (when the flight goes down into it) slow enough to brake to that
        // speed before entering.
        double AtmosphereSpeedLimit(Vector3D position, Vector3D end, double brake)
        {
            Obstacle planet = _planet ?? PlanetAt(position);
            if (planet == null || _atmosphereSpeed <= 0)
                return double.MaxValue;
            double top = AtmosphereTop(planet);
            if (top <= 0)
                return double.MaxValue;
            double r = Vector3D.Distance(position, planet.Center);
            if (r < top)
                return _atmosphereSpeed;
            if (Vector3D.Distance(end, planet.Center) >= top)
                return double.MaxValue;
            return Math.Sqrt(_atmosphereSpeed * _atmosphereSpeed + 2 * Math.Max(brake, 0.1) * (r - top));
        }

        // -----------------------------------------------------------------
        //  Disturbance observer
        // -----------------------------------------------------------------

        // External acceleration = measured acceleration - thrust / mass - gravity.
        // The thrust is the thrusters' actual output of the last tick, so thruster
        // spool-up does not look like a disturbance. Low-pass filtered. Only runs
        // while a drive assist holds a target velocity; reset otherwise.
        void UpdateDisturbance(bool active, Vector3D velocity, Vector3D gravity, double mass)
        {
            if (!active || !_compensateWind || mass <= 0)
            {
                _observerReady = false;
                _disturbance = Vector3D.Zero;
                return;
            }
            if (_observerReady)
            {
                double dt = MathHelper.Clamp(Runtime.TimeSinceLastRun.TotalSeconds, 1 / TicksPerSecond, 1);
                Vector3D external = (velocity - _observerVelocity) / dt - _thrustForce / mass - gravity;
                _disturbance += (external - _disturbance) * Math.Min(dt / DisturbanceTime, 1);
                if (_disturbance.Length() > MaxDisturbance)
                    _disturbance = Vector3D.Normalize(_disturbance) * MaxDisturbance;
            }
            _observerVelocity = velocity;
            _observerReady = true;
        }

        // Called after the overrides are set: the thrust the ship produces now.
        void MeasureThrust()
        {
            _thrustForce = Vector3D.Zero;
            if (!_compensateWind)
                return;
            foreach (IMyThrust t in _allThrusters)
                if (t.IsWorking)
                    _thrustForce += t.WorldMatrix.Backward * t.CurrentThrust;
        }
    }
}
