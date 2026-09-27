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
        const double AirDetected = 0.02;
        const double AtmosphereMargin = 500;        // m - flights above the atmosphere stay this far above its top            // thruster effectiveness change that counts as air

        string _zone = "";              // "" = space (proxy zone), else the zone of one real planet
        bool _planetZonesSeen;          // a teleport was seen: zones are in use (Real Solar Systems)
        readonly List<Obstacle> _otherObstacles = new List<Obstacle>();    // obstacles of other zones
        Vector3D _lastPosition, _lastVelocity;
        bool _haveLastPosition, _hadPlanet;
        bool _jumped, _jumpUsed = true;     // the jump drive jumped this tick / the last countdown's jump happened
        int _jumpSeenTick = -100000;

        // Flight to an entry in another zone: waits for the zone change.
        bool _zoneGoal, _zoneGoalDock;
        Vector3D _zoneGoalTarget;
        string _zoneGoalZone = "", _zoneGoalName = "";

        Vector3D _gravity;              // natural gravity, updated every tick
        Obstacle _planet;               // planet whose gravity the ship is in (this zone)
        double _terrainRadius;          // highest terrain (distance from the planet center) seen on this flight

        readonly List<IMyLandingGear> _landingGears = new List<IMyLandingGear>();
        readonly List<IMyThrust> _atmoThrusters = new List<IMyThrust>();
        readonly List<IMyThrust> _ionThrusters = new List<IMyThrust>();
        double _air = -1;               // air density estimate 0..1, -1 = unknown (no suitable thrusters)

        // Rendezvous with a moving proxy planet in space: samples of its GPS
        // (Real Solar Systems keeps a moving copy of every GPS placed on a planet).
        const int MaxTrackSamples = 3;
        const double MinSampleSpacing = 3;          // s between samples
        readonly List<Vector3D> _trackPositions = new List<Vector3D>();
        readonly List<double> _trackTimes = new List<double>();
        readonly Dictionary<string, double> _zoneRadii = new Dictionary<string, double>();  // learned zone radius in proxy space
        double _clock;                  // s since the script started
        bool _tracking;
        string _trackName = "";
        Vector3D _trackVelocity;

        Vector3D _disturbance;          // external acceleration (wind, drag, lift), m/s^2
        Vector3D _thrustForce;          // total thrust force of the last tick, N
        Vector3D _observerVelocity;
        bool _observerReady;

        bool InGravity
        {
            get { return _gravity.LengthSquared() > InGravityLimit * InGravityLimit; }
        }

        // Level flight (up side against gravity, no strongest-thruster orientation or
        // flips): in an atmosphere, or where gravity is more than LevelGravityShare of
        // what the ship's weakest side can push. Weaker gravity (high orbit of a planet
        // with a wide gravity well) is flown like space.
        const double LevelGravityShare = 0.3;
        double _weakestAccel;           // m/s^2, weakest of the six thrust directions (updated each second)

        bool LevelFlight
        {
            get
            {
                return InGravity && (InAtmosphere || _gravity.Length() > _weakestAccel * LevelGravityShare);
            }
        }

        bool InAtmosphere
        {
            get
            {
                return _air > AirDetected
                    || (_planet != null && Vector3D.Distance(ReferencePosition(), _planet.Center) < AtmosphereTop(_planet));
            }
        }

        void UpdateWeakestAccel()
        {
            IMyShipController c = _controller ?? _layoutController;
            if (c == null)
                return;
            double mass = c.CalculateShipMass().PhysicalMass, weakest = double.MaxValue;
            for (int a = 0; a < 3; a++)
                for (int s = 0; s < 2; s++)
                    weakest = Math.Min(weakest, MaxAccel(a, s, mass));
            _weakestAccel = weakest;
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
            _clock += MathHelper.Clamp(Runtime.TimeSinceLastRun.TotalSeconds, 0, 1);
            Vector3D position = ReferencePosition(), velocity = c.GetShipVelocities().LinearVelocity;
            Vector3D before = _lastPosition;
            foreach (IMyJumpDrive d in _jumpDrives)
                if (d.Status == MyJumpDriveStatus.Jumping)
                {
                    _jumpSeenTick = _ticks;
                    _jumpUsed = false;      // counting down: the next position jump is this jump
                }

            // A position jump the velocity does not explain is the jump drive's jump
            // (one per countdown), or else a teleport. A teleport right after a jump
            // (e.g. into a planet zone) is a separate jump and counts as teleport.
            bool teleported = false;
            _jumped = false;
            if (_haveLastPosition)
            {
                double dt = MathHelper.Clamp(Runtime.TimeSinceLastRun.TotalSeconds, 1 / TicksPerSecond, 1);
                Vector3D predicted = _lastPosition + (_lastVelocity + velocity) * 0.5 * dt;
                if (Vector3D.Distance(position, predicted) > TeleportDistance)
                {
                    if (!_jumpUsed && _ticks - _jumpSeenTick <= JumpGraceTicks)
                        _jumped = _jumpUsed = true;
                    else
                        teleported = true;
                }
            }
            _lastPosition = position;
            _lastVelocity = velocity;
            _haveLastPosition = true;
            if (!teleported)
                TrackCameFrom(velocity);
            else
                _cameFromValid = false;

            // A teleport into or out of a planet's gravity is how Real Solar Systems
            // moves ships between zones: from now on entries remember their zone.
            Vector3D center;
            bool hasPlanet = c.TryGetPlanetPosition(out center);
            if (teleported && hasPlanet != _hadPlanet)
                _planetZonesSeen = true;
            _hadPlanet = hasPlanet;
            // A planet zone reaches beyond the gravity: leaving the gravity keeps the
            // zone, only a teleport leads back to space.
            // Teleported to where there is no gravity: the zone of a known planet close
            // by (zones reach beyond the gravity, e.g. a base near a moon), else space.
            // Space found that way is provisional: if gravity shows a planet later
            // without another teleport, it was that planet's zone after all.
            string zone = !PlanetZones ? "" : hasPlanet ? ZoneKey(center) : teleported ? NearbyPlanetZone(position) : _zone;
            if (zone != _zone && hasPlanet && !teleported && _zoneProvisional)
                RelabelProvisional(zone);
            if (teleported || hasPlanet)
                _zoneProvisional = teleported && !hasPlanet && zone == "";
            bool changed = zone != _zone;
            if (changed)
                SwitchZone(zone);
            if (teleported && _zoneProvisional)
            {
                _provisionalDeposits = _deposits.Count;
                _provisionalObstacles = _obstacles.Count;
            }
            if (teleported)
            {
                if (_tracking)
                    LearnZoneRadius(before);
                OnTeleport(changed);
            }
        }

        bool _zoneProvisional, _dockProvisional;
        int _provisionalDeposits, _provisionalObstacles;

        // The zone of the nearest known planet whose gravity well (with a wide
        // margin: zones reach further) contains the point, or "" (space).
        string NearbyPlanetZone(Vector3D point)
        {
            Obstacle best = null;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < _obstacles.Count + _otherObstacles.Count; i++)
            {
                Obstacle o = i < _obstacles.Count ? _obstacles[i] : _otherObstacles[i - _obstacles.Count];
                double d = Vector3D.Distance(point, o.Center);
                if (o.Planet && d < o.GravityRadius * 1.5 + 50000 && d < bestDistance)
                {
                    best = o;
                    bestDistance = d;
                }
            }
            return best == null ? "" : best.Zone != "" ? best.Zone : ZoneKey(best.Center);
        }

        // Entries recorded since the ship arrived in a zone taken for space belong
        // to the planet zone found now.
        void RelabelProvisional(string zone)
        {
            for (int i = _provisionalDeposits; i < _deposits.Count; i++)
                if (_deposits[i].Zone == _zone)
                    _deposits[i].Zone = zone;
            for (int i = _provisionalObstacles; i < _obstacles.Count; i++)
                _obstacles[i].Zone = zone;
            if (_dockProvisional)
                _dockZone = zone;
            _dockProvisional = false;
            _mapChanged = true;
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
            HandleTeleport(zoneChanged);
            TeleportSpeedWarning();
        }

        void HandleTeleport(bool zoneChanged)
        {
            _tracking = false;
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

        // The zone change can leave the ship falling towards the planet fast. The
        // thrust control brakes at full thrust anyway; this only warns if the
        // upward thrust cannot stop the fall above the ground.
        void TeleportSpeedWarning()
        {
            IMyShipController c = _controller ?? _layoutController;
            double altitude;
            if (c == null || !InGravity || !c.TryGetPlanetElevation(MyPlanetElevation.Surface, out altitude))
                return;
            Vector3D down = Vector3D.Normalize(_gravity);
            double falling = Vector3D.Dot(c.GetShipVelocities().LinearVelocity, down);
            double brake = MaxAccel(1, 0, c.CalculateShipMass().PhysicalMass) - _gravity.Length();
            if (falling > 0 && (brake <= 0 || falling * falling / (2 * brake) > altitude * 0.8))
                _message = string.Format("WARNING: falling at {0:0} m/s, upward thrust cannot stop in {1}: brake now!", falling, FormatDistance(altitude));
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
            if (!CanHover())
            {
                _zoneGoal = false;
                return;
            }
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
                _message = name + " is in another zone: run 'track' with its moving GPS (twice, 10 s apart), or fly there yourself";
        }

        // -----------------------------------------------------------------
        //  Rendezvous with a moving planet (proxy space)
        // -----------------------------------------------------------------

        // track GPS:...   add a sample of the moving GPS and fly to it once two are known
        // track           continue with the samples known
        // track clear     forget the samples
        void HandleTrackCommand(string text)
        {
            if (text == "clear")
            {
                _trackPositions.Clear();
                _trackTimes.Clear();
                _tracking = false;
                _message = "Tracking samples cleared";
                return;
            }
            if (text != null)
            {
                int start = text.IndexOf("GPS:", StringComparison.OrdinalIgnoreCase);
                string[] p = start >= 0 ? text.Substring(start).Split(':') : new string[0];
                double x, y, z;
                if (p.Length < 5 || !TryParseNumber(p[2], out x) || !TryParseNumber(p[3], out y) || !TryParseNumber(p[4], out z))
                {
                    _message = "Usage: track GPS:name:x:y:z:  (the planet's moving GPS, twice)";
                    return;
                }
                if (p[1] != _trackName)
                {
                    _trackPositions.Clear();
                    _trackTimes.Clear();
                    _trackName = p[1];
                }
                int last = _trackTimes.Count - 1;
                if (last >= 0 && _clock - _trackTimes[last] < MinSampleSpacing)
                {
                    _trackPositions.RemoveAt(last);     // pasted twice quickly: keep the newer one
                    _trackTimes.RemoveAt(last);
                }
                _trackPositions.Add(new Vector3D(x, y, z));
                _trackTimes.Add(_clock);
                if (_trackPositions.Count > MaxTrackSamples)
                {
                    _trackPositions.RemoveAt(0);
                    _trackTimes.RemoveAt(0);
                }
            }
            if (_trackPositions.Count < 2)
            {
                _message = _trackPositions.Count == 0 ? "No samples: track GPS:..." : "Sample stored: paste the same GPS again in 10-30 s";
                return;
            }
            if (_zone != "")
            {
                _message = "Tracking works in space only (the planets move there)";
                return;
            }
            Vector3D position, velocity;
            PredictTrack(_clock, out position, out velocity);
            _route.Clear();
            _route.Add(position);
            _temporaryObstacles.Clear();
            _dockAfterRoute = false;
            bool waiting = _zoneGoal;
            StartRoute(_trackName);
            _zoneGoal = waiting;
            _tracking = true;
            _message = string.Format("Following {0}: moving at {1:0} m/s", _trackName, velocity.Length())
                + (velocity.Length() > _maxSpeed * 0.9 ? ", faster than MaxSpeed allows to match!" : "");
        }

        // Position and velocity of the tracked GPS at time t: linear through the
        // last two samples, a parabola through three (curved orbits). Pasting
        // the GPS again during the flight refreshes the prediction.
        void PredictTrack(double t, out Vector3D position, out Vector3D velocity)
        {
            int n = _trackPositions.Count;
            if (n < 3)
            {
                Vector3D a = _trackPositions[n - 2], b = _trackPositions[n - 1];
                double dt = Math.Max(_trackTimes[n - 1] - _trackTimes[n - 2], 1e-3);
                velocity = (b - a) / dt;
                position = b + velocity * (t - _trackTimes[n - 1]);
                return;
            }
            // Lagrange polynomial through the three samples and its derivative.
            position = velocity = Vector3D.Zero;
            for (int i = 0; i < 3; i++)
            {
                double ti = _trackTimes[i], den = 1, num = 1, dnum = 0;
                for (int j = 0; j < 3; j++)
                {
                    if (j == i)
                        continue;
                    den *= ti - _trackTimes[j];
                    dnum = dnum * (t - _trackTimes[j]) + num;   // product rule
                    num *= t - _trackTimes[j];
                }
                position += _trackPositions[i] * (num / den);
                velocity += _trackPositions[i] * (dnum / den);
            }
        }

        // Target velocity while following the moving planet: its own velocity plus
        // an approach towards it. The speed relative to the planet is braked to
        // ZoneEntrySpeed at the edge of its zone (learned at the first entry,
        // ZoneRadiusGuess until then), because the zone change keeps that speed.
        bool TrackVelocity(Vector3D velocity, out Vector3D targetVelocity)
        {
            Vector3D planetPosition, planetVelocity;
            PredictTrack(_clock, out planetPosition, out planetVelocity);
            _trackVelocity = planetVelocity;
            _approachTarget = _route[0] = planetPosition;
            Vector3D toTarget = planetPosition - ReferencePosition();
            double distance = toTarget.Length();
            _targetDistance = _remainingDistance = distance;
            Vector3D direction = toTarget / Math.Max(distance, 1e-3);
            double brake = BrakeAccel(direction), radius;
            if (!_zoneRadii.TryGetValue(_zoneGoal ? _zoneGoalZone : "", out radius))
                radius = _zoneRadiusGuess;
            double entry = Math.Max(_zoneEntrySpeed, 1);
            double speed = Math.Min(_maxSpeed * 2, Math.Sqrt(entry * entry + 2 * brake * Math.Max(distance - radius, 0)));
            speed = Math.Min(speed, Math.Max(distance * _velocityGain * 0.5, distance > radius ? entry : 0));
            targetVelocity = planetVelocity + direction * speed;
            if (targetVelocity.Length() > _maxSpeed)
                targetVelocity = Vector3D.Normalize(targetVelocity) * _maxSpeed;
            double closing = Vector3D.Dot(velocity - planetVelocity, direction);
            _stopDistance = closing > 0 ? closing * closing / (2 * Math.Max(brake, 0.01)) : 0;
            _approachPhase = "FOLLOWING";
            return true;
        }

        // At the zone change: how far from the tracked GPS the zone began.
        void LearnZoneRadius(Vector3D before)
        {
            Vector3D planetPosition, planetVelocity;
            PredictTrack(_clock, out planetPosition, out planetVelocity);
            double radius = Vector3D.Distance(before, planetPosition);
            string zone = _zone;
            if (zone != "" && radius > 100)
            {
                _zoneRadii[zone] = radius;
                _mapChanged = true;
            }
        }

        // Flights need enough upward thrust to hover (with some margin to climb and brake).
        bool CanHover()
        {
            IMyShipController c = _controller ?? _layoutController;
            if (!InGravity || c == null)
                return true;
            double up = MaxAccel(1, 0, c.CalculateShipMass().PhysicalMass), g = _gravity.Length();
            if (up >= g * 1.1)
                return true;
            _mode = Mode.Manual;
            _message = string.Format("Not enough upward thrust: {0:0.0} m/s² for {1:0.0} m/s² of gravity", up, g);
            return false;
        }

        // A flight starting from the ground (or a rock) must not pull against locked landing gear.
        void UnlockLandingGear()
        {
            foreach (IMyLandingGear g in _landingGears)
                if (g.IsLocked)
                    g.Unlock();
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
            // Geodetic: all heights are distances from the planet center. Never below
            // the water surface (water mod): stored per planet ('water here'), else
            // WaterLevel above sea level. The ground under the ship may be the sea
            // floor; it only raises the cruise height, never lowers it.
            double ground = Math.Max(planet.Radius + Math.Max(_waterLevel, 0), planet.WaterRadius), elevation;
            IMyShipController c = _controller ?? _layoutController;
            if (c != null && Vector3D.Distance(from, ReferencePosition()) < 100
                && c.TryGetPlanetElevation(MyPlanetElevation.Surface, out elevation))
                ground = Math.Max(ground, Vector3D.Distance(from, planet.Center) - elevation);
            // A target on or near the ground raises the cruise height; one high up
            // (orbit, asteroids in a large gravity well) is flown to directly.
            double rt = Vector3D.Distance(to, planet.Center);
            if (rt < NearGroundRadius(planet))
                ground = Math.Max(ground, rt);
            ground = Math.Max(ground, _terrainRadius);
            double height = MathHelper.Clamp(Vector3D.Distance(from, to) * 0.25, 200, Math.Max(_planetCruiseHeight, 200));
            double cruise = ground + height + ShipRadius;
            // Long flights: above the atmosphere (no speed limit there) if that is faster,
            // counting the climb and the descent at AtmosphereSpeed.
            double top = AtmosphereTop(planet) + AtmosphereMargin;
            if (_atmosphereSpeed > 0 && top > cruise && _maxSpeed > _atmosphereSpeed)
            {
                double angle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(Vector3D.Normalize(from - planet.Center),
                    Vector3D.Normalize(to - planet.Center)), -1, 1));
                if (2 * (top - cruise) / _atmosphereSpeed + angle * top / _maxSpeed < angle * cruise / _atmosphereSpeed)
                    cruise = top;
            }
            return cruise;
        }

        // Adds climb and arc waypoints around the planet to the route and
        // returns the point from where the rest is flown directly (above a
        // target on the surface, or where a target in space is in clear view).
        Vector3D PlanPlanetArc(Obstacle planet, double cruise, Vector3D from, Vector3D to, List<Vector3D> route)
        {
            Vector3D c = planet.Center;
            double rf = Vector3D.Distance(from, c), rt = Vector3D.Distance(to, c), r = cruise;
            Vector3D uf = (from - c) / Math.Max(rf, 1), ut = (to - c) / Math.Max(rt, 1);
            Vector3D point = from;
            if (rf < r - 20)
            {
                point = c + uf * r;         // climb straight up
                AddPlanWaypoint(route, point);
            }
            bool below = rt < r;            // target on the ground: end above it, then descend
            double angle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(uf, ut), -1, 1));
            Vector3D axis = Vector3D.Cross(uf, ut);
            axis = axis.LengthSquared() > 1e-12 ? Vector3D.Normalize(axis) : Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(uf));
            Vector3D side = Vector3D.Cross(axis, uf);
            double step = 2 * Math.Acos(1 - Math.Min(PlanetArcSag / r, 1));
            int steps = Math.Max((int)Math.Ceiling(angle / Math.Max(step, 0.01)), 1);
            // Starting high above (far out in space, or in orbit after a zone change):
            // straight down to the farthest point of the arc that is in clear view
            // (a tangent), instead of circling at the start's height.
            int first = 1;
            if (rf >= r - 20)
                for (int i = steps; i > 1; i--)
                    if (DistanceToSegment(c, from, ArcPoint(c, uf, side, angle * i / steps, r)) >= r - PlanetArcSag)
                    {
                        first = i;
                        break;
                    }
            for (int i = first; i <= steps; i++)
            {
                // Head straight for the goal (the point above a ground target, or a
                // target in space) as soon as the line clears the planet.
                Vector3D goal = below ? c + ut * r : to;
                if (i > first && DistanceToSegment(c, point, goal) >= r - PlanetArcSag)
                {
                    if (below)
                    {
                        point = goal;
                        AddPlanWaypoint(route, point);
                    }
                    return point;
                }
                point = ArcPoint(c, uf, side, angle * i / steps, r);
                AddPlanWaypoint(route, point);
            }
            return point;
        }

        static Vector3D ArcPoint(Vector3D center, Vector3D from, Vector3D side, double angle, double radius)
        {
            return center + (from * Math.Cos(angle) + side * Math.Sin(angle)) * radius;
        }

        void AddPlanWaypoint(List<Vector3D> route, Vector3D point)
        {
            route.Add(point);
            _planOwners.Add(null);
        }

        // water here   store the ship's current distance from the planet center as the
        //              water surface of this planet (float on the water, or hover just above)
        // water off    forget it
        void HandleWaterCommand(string value)
        {
            UpdatePlanet();
            if (_planet == null)
            {
                _message = "Not in a planet's gravity";
                return;
            }
            if (value == "here")
            {
                _planet.WaterRadius = Vector3D.Distance(ReferencePosition(), _planet.Center);
                _message = "Water surface stored: " + FormatDistance(_planet.WaterRadius - _planet.Radius) + " above sea level";
            }
            else if (value == "off")
            {
                _planet.WaterRadius = 0;
                _message = "Water surface of this planet forgotten";
            }
            else
            {
                _message = _planet.WaterRadius > 0
                    ? "Water surface: " + FormatDistance(_planet.WaterRadius - _planet.Radius) + " above sea level (water here|off)"
                    : "Usage: water here | water off";
                return;
            }
            _mapChanged = true;
        }

        // Below this distance from the center a target counts as on the planet:
        // inside the atmosphere, or within 10 % of the radius above sea level.
        double NearGroundRadius(Obstacle planet)
        {
            return Math.Max(AtmosphereTop(planet), planet.Radius * 1.1);
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
        // it (when the current leg ends inside it) slow enough to brake to that
        // speed before entering. The radial distance to the atmosphere is used,
        // which is shorter than the way along a slanted leg (safe side).
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
            // Aim at 85 %: thruster and control lag let the speed trail the profile
            // (simulated: up to 18 % over the limit when aiming at 100 %).
            double entry = _atmosphereSpeed * 0.85;
            return Math.Sqrt(entry * entry + 2 * Math.Max(brake, 0.1) * (r - top));
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
