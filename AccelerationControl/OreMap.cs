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
    // Ore map data: deposits (marked by camera scan or logged while mining),
    // known obstacles (asteroids, planets with their gravity wells) and
    // selection / filtering for the map screens.
    partial class Program
    {
        const double AsteroidRadiusFactor = 0.75;   // asteroid voxel boxes are larger than the rock itself
        const double GravityCutoff = 0.05 * 9.81;   // m/s^2 - roughly where planet gravity ends
        const double MinLoggedOre = 1.0;            // kg of new ore needed to log a deposit
        const string MapSection = "Map";

        static readonly string[] StandardOres = { "Iron", "Nickel", "Cobalt", "Magnesium", "Silicon",
            "Silver", "Gold", "Platinum", "Uranium", "Ice", "Stone" };

        class Deposit
        {
            public string Ore;
            public int Number;
            public Vector3D Position;
            public bool Mined;          // logged while drilling (otherwise marked by scan)
            public double Distance;     // from the ship, updated for display
            public string Label { get { return Ore + " #" + Number; } }
        }

        class Obstacle
        {
            public long EntityId;
            public bool Planet;
            public Vector3D Center;
            public double Radius;
            public double GravityRadius; // planets: approximate extent of the gravity well
        }

        readonly List<Deposit> _deposits = new List<Deposit>();
        readonly List<Obstacle> _obstacles = new List<Obstacle>();
        readonly List<Deposit> _visibleDeposits = new List<Deposit>();
        readonly List<IMyShipDrill> _drills = new List<IMyShipDrill>();
        readonly Dictionary<string, double> _previousOre = new Dictionary<string, double>();
        readonly List<string> _filterOptions = new List<string>();

        Deposit _selected;
        string _filter;                 // null = all ores
        string _pendingMarkOre = "";
        bool _mapChanged = true;        // GPS export pending
        bool _oreBaseline;              // _previousOre holds a reading to compare with

        // -----------------------------------------------------------------
        //  Commands
        // -----------------------------------------------------------------

        // mark <ore>        scan straight ahead and map the hit point
        // mark <ore> here   map the current position (e.g. while drilling)
        void HandleMarkCommand(string[] parts)
        {
            if (parts.Length < 2)
            {
                _message = "Usage: mark <ore> [here]";
                return;
            }
            string ore = NormalizeOre(parts[1]);
            if (parts.Length > 2 && parts[2].ToLowerInvariant() == "here")
                AddDeposit(ore, DrillPosition(), false);
            else
                MarkByScan(ore);
        }

        void MarkByScan(string ore)
        {
            _pendingMarkOre = ore;
            StartScan(ScanPurpose.Mark);
            if (_scanPending)
                _message = "Scanning for " + ore + "...";
        }

        // map clear confirm | map gps
        void HandleMapCommand(string value, string extra)
        {
            if (value == "clear" && extra == "confirm")
            {
                _deposits.Clear();
                _obstacles.Clear();
                _selected = null;
                _mapChanged = true;
                _message = "Map cleared";
            }
            else if (value == "clear")
                _message = "Run 'map clear confirm' to delete all entries";
            else
                _message = "Usage: map clear confirm";
        }

        string NormalizeOre(string name)
        {
            foreach (string ore in StandardOres)
                if (string.Equals(ore, name, StringComparison.OrdinalIgnoreCase))
                    return ore;
            foreach (string ore in _oreAmounts.Keys)
                if (string.Equals(ore, name, StringComparison.OrdinalIgnoreCase))
                    return ore;
            return char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant();
        }

        // -----------------------------------------------------------------
        //  Deposits
        // -----------------------------------------------------------------

        Deposit AddDeposit(string ore, Vector3D position, bool mined)
        {
            int number = 0;
            foreach (Deposit d in _deposits)
            {
                if (d.Ore != ore)
                    continue;
                if (Vector3D.DistanceSquared(d.Position, position) < _mergeDistance * _mergeDistance)
                {
                    if (!mined)
                        _message = "Already mapped as " + d.Label;
                    return d;
                }
                number = Math.Max(number, d.Number);
            }

            var deposit = new Deposit { Ore = ore, Number = number + 1, Position = position, Mined = mined };
            _deposits.Add(deposit);
            if (!mined)
                _selected = deposit;   // logging while mining must not move the menu selection
            _mapChanged = true;
            _message = (mined ? "Logged " : "Mapped ") + deposit.Label;
            return deposit;
        }

        void DeleteSelected()
        {
            if (_selected == null)
                return;
            _message = "Deleted " + _selected.Label;
            int index = _visibleDeposits.IndexOf(_selected);
            _deposits.Remove(_selected);
            _visibleDeposits.Remove(_selected);
            _selected = _visibleDeposits.Count == 0 ? null
                : _visibleDeposits[Math.Min(Math.Max(index, 0), _visibleDeposits.Count - 1)];
            _mapChanged = true;
        }

        // Logs a deposit when new ore arrives while the drills are running.
        void AutoLogMining()
        {
            bool drilling = false;
            foreach (IMyShipDrill d in _drills)
                if (d.IsWorking)
                    drilling = true;

            if (drilling && _autoLog && _oreBaseline)
            {
                Vector3D position = DrillPosition();
                foreach (KeyValuePair<string, double> ore in _oreAmounts)
                {
                    if (ore.Key == "Stone" && !_logStone)
                        continue;
                    double before;
                    _previousOre.TryGetValue(ore.Key, out before);
                    if (ore.Value > before + MinLoggedOre)
                        AddDeposit(ore.Key, position, true);
                }
            }

            _previousOre.Clear();
            foreach (KeyValuePair<string, double> ore in _oreAmounts)
                _previousOre[ore.Key] = ore.Value;
            _oreBaseline = true;
        }

        // Center of the working drills, or the ship if none are running.
        Vector3D DrillPosition()
        {
            Vector3D sum = Vector3D.Zero;
            int count = 0;
            foreach (IMyShipDrill d in _drills)
            {
                if (!d.IsWorking)
                    continue;
                sum += d.GetPosition();
                count++;
            }
            return count > 0 ? sum / count : ReferencePosition();
        }

        void GoToSelected()
        {
            if (_selected == null)
            {
                _message = "No deposit selected";
                return;
            }
            Vector3D from = ReferencePosition();
            Obstacle blocking = FirstObstacleOnPath(from, _selected.Position);
            if (blocking != null)
            {
                _message = "Direct path blocked by " + (blocking.Planet ? "a planet" : "an asteroid");
                return;
            }
            if (StartApproach(_selected.Position, _selected.Label))
                _message = "Flying to " + _selected.Label;
        }

        // -----------------------------------------------------------------
        //  Obstacles and gravity wells
        // -----------------------------------------------------------------

        void RegisterObstacle(MyDetectedEntityInfo hit)
        {
            bool planet = hit.Type == MyDetectedEntityType.Planet;
            if (!planet && hit.Type != MyDetectedEntityType.Asteroid)
                return;

            BoundingBoxD box = hit.BoundingBox;
            Vector3D size = box.Max - box.Min;
            double halfSize = Math.Max(size.X, Math.Max(size.Y, size.Z)) / 2;

            Obstacle obstacle = null;
            foreach (Obstacle o in _obstacles)
                if (o.EntityId == hit.EntityId)
                    obstacle = o;
            if (obstacle == null)
            {
                obstacle = new Obstacle { EntityId = hit.EntityId, Planet = planet };
                _obstacles.Add(obstacle);
            }

            obstacle.Center = planet ? hit.Position : box.Center;
            obstacle.Radius = planet ? halfSize : halfSize * AsteroidRadiusFactor;
            if (planet && obstacle.GravityRadius <= 0)
                obstacle.GravityRadius = obstacle.Radius * _gravityWellFactor;
        }

        // In gravity, measure the planet's position and gravity well directly.
        void UpdatePlanet()
        {
            IMyShipController reference = _controller ?? _layoutController;
            if (reference == null)
                return;
            double gravity = reference.GetNaturalGravity().Length();
            Vector3D center;
            if (gravity < 0.01 || !reference.TryGetPlanetPosition(out center))
                return;

            double distance = Vector3D.Distance(center, reference.GetPosition());
            double elevation;
            double radius = reference.TryGetPlanetElevation(MyPlanetElevation.Sealevel, out elevation)
                ? distance - elevation : distance;
            // Planet gravity falls off with distance^7 outside the surface.
            double wellRadius = distance * Math.Pow(gravity / GravityCutoff, 1.0 / 7);

            Obstacle planet = null;
            foreach (Obstacle o in _obstacles)
                if (o.Planet && Vector3D.DistanceSquared(o.Center, center) < 1e6)
                    planet = o;
            if (planet == null)
            {
                planet = new Obstacle { Planet = true };
                _obstacles.Add(planet);
                _mapChanged = true;
            }
            planet.Center = center;
            planet.Radius = radius;
            planet.GravityRadius = wellRadius;
        }

        // First asteroid or planet the straight line from -> to passes through.
        // The body the target itself belongs to is ignored.
        Obstacle FirstObstacleOnPath(Vector3D from, Vector3D to)
        {
            Obstacle first = null;
            double firstDistance = double.MaxValue;
            foreach (Obstacle o in _obstacles)
            {
                double clearance = o.Radius + _approachBuffer;
                if (Vector3D.Distance(o.Center, to) < o.Radius + 2 * _approachBuffer)
                    continue;
                if (DistanceToSegment(o.Center, from, to) >= clearance)
                    continue;
                double d = Vector3D.Distance(from, o.Center);
                if (d < firstDistance)
                {
                    first = o;
                    firstDistance = d;
                }
            }
            return first;
        }

        static double DistanceToSegment(Vector3D point, Vector3D a, Vector3D b)
        {
            Vector3D ab = b - a;
            double lengthSquared = ab.LengthSquared();
            double t = lengthSquared > 0 ? MathHelper.Clamp(Vector3D.Dot(point - a, ab) / lengthSquared, 0, 1) : 0;
            return Vector3D.Distance(point, a + ab * t);
        }

        // -----------------------------------------------------------------
        //  Selection and filter
        // -----------------------------------------------------------------

        // Rebuilds the filtered deposit list, sorted by distance to the ship.
        void UpdateVisibleDeposits()
        {
            Vector3D position = ReferencePosition();
            _visibleDeposits.Clear();
            foreach (Deposit d in _deposits)
            {
                d.Distance = Vector3D.Distance(position, d.Position);
                if (_filter == null || d.Ore == _filter)
                    _visibleDeposits.Add(d);
            }
            _visibleDeposits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (_selected == null || !_visibleDeposits.Contains(_selected))
                _selected = _visibleDeposits.Count > 0 ? _visibleDeposits[0] : null;
        }

        void MoveSelection(int step)
        {
            if (_visibleDeposits.Count == 0)
                return;
            int index = _selected != null ? _visibleDeposits.IndexOf(_selected) : 0;
            index = (index + step + _visibleDeposits.Count) % _visibleDeposits.Count;
            _selected = _visibleDeposits[index];
        }

        void CycleFilter()
        {
            _filterOptions.Clear();
            foreach (Deposit d in _deposits)
                if (!_filterOptions.Contains(d.Ore))
                    _filterOptions.Add(d.Ore);
            _filterOptions.Sort();

            int index = _filter == null ? -1 : _filterOptions.IndexOf(_filter);
            _filter = index + 1 < _filterOptions.Count ? _filterOptions[index + 1] : null;
            _selected = null;
            UpdateVisibleDeposits();
        }

        // -----------------------------------------------------------------
        //  Persistence and GPS export
        // -----------------------------------------------------------------

        void SaveMap(MyIni state)
        {
            for (int i = 0; i < _deposits.Count; i++)
            {
                Deposit d = _deposits[i];
                state.Set(MapSection, "D" + i, string.Join(";", d.Ore, d.Number.ToString(),
                    Num(d.Position.X), Num(d.Position.Y), Num(d.Position.Z), d.Mined ? "1" : "0"));
            }
            for (int i = 0; i < _obstacles.Count; i++)
            {
                Obstacle o = _obstacles[i];
                state.Set(MapSection, "O" + i, string.Join(";", o.Planet ? "P" : "A", o.EntityId.ToString(),
                    Num(o.Center.X), Num(o.Center.Y), Num(o.Center.Z), Num(o.Radius), Num(o.GravityRadius)));
            }
        }

        void LoadMap(MyIni state)
        {
            _deposits.Clear();
            _obstacles.Clear();
            for (int i = 0; ; i++)
            {
                string[] p = state.Get(MapSection, "D" + i).ToString("").Split(';');
                double x, y, z;
                int number;
                if (p.Length < 6 || !int.TryParse(p[1], out number) || !TryParseNumber(p[2], out x)
                    || !TryParseNumber(p[3], out y) || !TryParseNumber(p[4], out z))
                    break;
                _deposits.Add(new Deposit { Ore = p[0], Number = number, Position = new Vector3D(x, y, z), Mined = p[5] == "1" });
            }
            for (int i = 0; ; i++)
            {
                string[] p = state.Get(MapSection, "O" + i).ToString("").Split(';');
                double x, y, z, r, g;
                long id;
                if (p.Length < 7 || !long.TryParse(p[1], out id) || !TryParseNumber(p[2], out x) || !TryParseNumber(p[3], out y)
                    || !TryParseNumber(p[4], out z) || !TryParseNumber(p[5], out r) || !TryParseNumber(p[6], out g))
                    break;
                _obstacles.Add(new Obstacle { Planet = p[0] == "P", EntityId = id, Center = new Vector3D(x, y, z), Radius = r, GravityRadius = g });
            }
            _mapChanged = true;
        }

        // GPS lines ("GPS:name:x:y:z:#color:") that can be copied into the
        // game's GPS list with "Paste from clipboard".
        string BuildGpsList()
        {
            var sb = new StringBuilder();
            foreach (Deposit d in _deposits)
            {
                Color c = OreColor(d.Ore);
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "GPS:{0}:{1:0.0}:{2:0.0}:{3:0.0}:#{4:X2}{5:X2}{6:X2}:\n", d.Label,
                    d.Position.X, d.Position.Y, d.Position.Z, c.R, c.G, c.B);
            }
            return sb.ToString();
        }
    }
}
