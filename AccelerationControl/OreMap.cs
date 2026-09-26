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
    // the base, known obstacles (asteroids, planets with their gravity wells,
    // found by scans and by the background survey) and selection / filtering
    // for the map screens.
    partial class Program
    {
        const double AsteroidRadiusFactor = 0.75;   // asteroid voxel boxes are larger than the rock itself
        const double GravityCutoff = 0.05 * 9.81;   // m/s^2 - roughly where planet gravity ends
        const double MinLoggedOre = 1.0;            // kg of new ore needed to log a deposit
        const string MapSection = "Map";
        const string BaseName = "Base";
        const int SurveyTicks = 10;                 // one survey scan attempt every 1/6 s
        const int SurveyPattern = 97;               // directions per sweep of the camera cone

        static readonly string[] StandardOres = { "Iron", "Nickel", "Cobalt", "Magnesium", "Silicon",
            "Silver", "Gold", "Platinum", "Uranium", "Ice", "Stone" };

        class Deposit
        {
            public string Ore;
            public int Number;
            public Vector3D Position;
            public bool Mined;          // logged while drilling (otherwise marked by scan)
            public double Distance;     // from the ship, updated for display
            // Number 0: a waypoint with its own name (e.g. imported GPS "Asteroid 12")
            public string Label { get { return Number > 0 ? Ore + " #" + Number : Ore; } }
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
        int _surveyCamera;
        int _surveyStep;

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

        // map import | map send | map clear confirm
        void HandleMapCommand(string value, string extra)
        {
            if (value == "import")
                ImportFromBlocks();
            else if (value == "send")
                SendMap();
            else if (value == "clear" && extra == "confirm")
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
                _message = "Usage: map import | map send | map clear confirm";
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

        Deposit AddDeposit(string ore, Vector3D position, bool mined, bool waypoint = false)
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

            var deposit = new Deposit { Ore = ore, Number = waypoint ? 0 : number + 1, Position = position, Mined = mined };
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

        // -----------------------------------------------------------------
        //  Background survey
        // -----------------------------------------------------------------

        // Cameras take turns scanning their field of view in a spiral pattern,
        // so asteroids the ship passes get onto the map without marking them.
        // The approach camera is left alone while it is needed.
        void UpdateSurvey()
        {
            if (!_survey || _cameras.Count == 0)
                return;
            for (int i = 0; i < _cameras.Count; i++)
            {
                IMyCameraBlock camera = _cameras[(_surveyCamera + i) % _cameras.Count];
                bool busy = camera == _camera && (_scanPending || (_mode == Mode.Approach && _probing));
                if (busy || !camera.IsWorking)
                    continue;
                camera.EnableRaycast = true;
                if (!camera.CanScan(_surveyRange))
                    continue;

                // Sunflower spiral: evenly spread directions within the cone.
                int step = _surveyStep++ % SurveyPattern;
                double radius = Math.Sqrt((step + 0.5) / SurveyPattern) * Math.Min(camera.RaycastConeLimit, 45);
                double angle = step * 2.39996;
                MyDetectedEntityInfo hit = camera.Raycast(_surveyRange, (float)(radius * Math.Sin(angle)), (float)(radius * Math.Cos(angle)));
                if (!hit.IsEmpty() && RegisterObstacle(hit))
                    _message = "Found " + (hit.Type == MyDetectedEntityType.Planet ? "planet" : "asteroid")
                        + " at " + FormatDistance(Vector3D.Distance(hit.Position, ReferencePosition()));
                _surveyCamera = (_surveyCamera + i + 1) % _cameras.Count;
                return;
            }
        }

        // -----------------------------------------------------------------
        //  Obstacles and gravity wells
        // -----------------------------------------------------------------

        // Returns true if the obstacle was not known before.
        bool RegisterObstacle(MyDetectedEntityInfo hit)
        {
            bool planet = hit.Type == MyDetectedEntityType.Planet;
            if (!planet && hit.Type != MyDetectedEntityType.Asteroid)
                return false;

            BoundingBoxD box = hit.BoundingBox;
            Vector3D size = box.Max - box.Min;
            double halfSize = Math.Max(size.X, Math.Max(size.Y, size.Z)) / 2;

            Obstacle obstacle = null;
            foreach (Obstacle o in _obstacles)
                if (o.EntityId == hit.EntityId)
                    obstacle = o;
            bool added = obstacle == null;
            if (added)
            {
                obstacle = new Obstacle { EntityId = hit.EntityId, Planet = planet };
                _obstacles.Add(obstacle);
                _mapChanged = true;
            }

            obstacle.Center = planet ? hit.Position : box.Center;
            obstacle.Radius = planet ? halfSize : halfSize * AsteroidRadiusFactor;
            if (planet && obstacle.GravityRadius <= 0)
                obstacle.GravityRadius = obstacle.Radius * _gravityWellFactor;
            if (added)
                CheckRouteAfterNewObstacle();
            return added;
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

        // Export for the Custom Data of the map screens:
        //   GPS:name:x:y:z:#color:   deposits and base; the game's GPS menu can
        //                            paste these ("Paste from clipboard")
        //   MAP:A|P:id:x:y:z:r:g:    known asteroids / planets, for 'map import'
        string BuildExport()
        {
            var sb = new StringBuilder();
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            foreach (Deposit d in _deposits)
            {
                Color c = OreColor(d.Ore);
                sb.AppendFormat(culture, "GPS:{0}:{1:0.0}:{2:0.0}:{3:0.0}:#{4:X2}{5:X2}{6:X2}:\n", d.Label,
                    d.Position.X, d.Position.Y, d.Position.Z, c.R, c.G, c.B);
            }
            foreach (Obstacle o in _obstacles)
                sb.AppendFormat(culture, "MAP:{0}:{1}:{2:0.0}:{3:0.0}:{4:0.0}:{5:0.0}:{6:0.0}:\n", o.Planet ? "P" : "A",
                    o.EntityId, o.Center.X, o.Center.Y, o.Center.Z, o.Radius, o.GravityRadius);
            return sb.ToString();
        }

        // Merges GPS and MAP lines into the map. Returns the number of new entries.
        int ImportMap(string text)
        {
            int added = 0;
            foreach (string raw in text.Split('\n'))
            {
                string[] p = raw.Trim().Split(':');
                double x, y, z, r, g;
                if (p.Length >= 5 && p[0] == "GPS" && TryParseNumber(p[2], out x) && TryParseNumber(p[3], out y) && TryParseNumber(p[4], out z))
                {
                    // "Iron #2" -> Iron; other names become waypoints with that name
                    int hash = p[1].LastIndexOf(" #");
                    string ore = hash > 0 ? p[1].Substring(0, hash) : p[1];
                    bool known = ore == BaseName || Array.IndexOf(StandardOres, ore) >= 0 || _oreAmounts.ContainsKey(ore);
                    int before = _deposits.Count;
                    AddDeposit(known ? ore : p[1], new Vector3D(x, y, z), false, !known);
                    added += _deposits.Count - before;
                }
                else if (p.Length >= 8 && p[0] == "MAP" && TryParseNumber(p[3], out x) && TryParseNumber(p[4], out y)
                    && TryParseNumber(p[5], out z) && TryParseNumber(p[6], out r) && TryParseNumber(p[7], out g))
                {
                    long id;
                    long.TryParse(p[2], out id);
                    Vector3D center = new Vector3D(x, y, z);
                    bool known = false;
                    foreach (Obstacle o in _obstacles)
                        if ((id != 0 && o.EntityId == id) || Vector3D.DistanceSquared(o.Center, center) < 1)
                            known = true;
                    if (!known)
                    {
                        _obstacles.Add(new Obstacle { Planet = p[1] == "P", EntityId = id, Center = center, Radius = r, GravityRadius = g });
                        added++;
                    }
                }
            }
            if (added > 0)
                _mapChanged = true;
            return added;
        }

        // map import: Custom Data of blocks tagged [Accel Import], and of the map
        // screens of docked ships (other constructs on the same grid network).
        void ImportFromBlocks()
        {
            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType(blocks, b => b.CustomName.Contains(_importTag)
                || (!b.IsSameConstructAs(Me) && (b.CustomName.Contains(_mapTag) || b.CustomName.Contains(_listTag))));
            int added = 0;
            foreach (IMyTerminalBlock b in blocks)
                added += ImportMap(b.CustomData);
            _message = blocks.Count == 0 ? "Nothing to import: tag a block " + _importTag + " or dock to a ship with a map"
                : "Imported " + added + " new entries";
        }

        // Map exchange over antennas (IGC): 'map send' broadcasts the map,
        // every ship or station running this script merges it automatically.
        const string MapChannel = "AccelerationControl.Map";
        IMyBroadcastListener _mapListener;

        void ReceiveMaps()
        {
            if (_mapListener == null)
                _mapListener = IGC.RegisterBroadcastListener(MapChannel);
            while (_mapListener.HasPendingMessage)
            {
                MyIGCMessage message = _mapListener.AcceptMessage();
                string text = message.Data as string;
                if (text != null)
                    _message = "Received map: " + ImportMap(text) + " new entries";
            }
        }

        void SendMap()
        {
            IGC.SendBroadcastMessage(MapChannel, BuildExport());
            _message = "Map sent (" + _deposits.Count + " entries, " + _obstacles.Count + " obstacles)";
        }
    }
}
