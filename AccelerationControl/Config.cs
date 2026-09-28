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
    // Configuration (programmable block Custom Data) and state that survives
    // world reloads (programmable block Storage).
    partial class Program
    {
        const string IniSection = "AccelerationControl";
        const string StateSection = "State";

        readonly MyIni _ini = new MyIni();

        // ---- configuration ----
        double _step = 1.0;
        double _minLimit = 0.5;
        double _maxLimit = 50.0;
        double _defaultLimit = 5.0;
        bool _limitDampenersDefault = false;
        double _dampenerGain = 1.5;
        string _lcdTag = "[Accel]";
        string _statusTag = "[Accel Status]";
        int _cockpitSurface = -1;
        int _statusCockpitSurface = -1;
        string _mapTag = "[Accel Map]";
        string _listTag = "[Accel List]";
        int _mapCockpitSurface = -1;
        int _listCockpitSurface = -1;
        bool _autoLog = true;
        bool _logStone = false;
        double _mergeDistance = 150;
        double _gravityWellFactor = 1.7;
        bool _survey = true;
        double _surveyRange = 6000;
        double _probeRange = 50000;
        bool _alignShip = true;
        bool _guard = true;
        bool _avoidGravity = true;
        string _importTag = "[Accel Import]";
        bool _useJump = true;
        double _jumpThreshold = 20000;
        double _jumpArrival = 3000;
        double _jumpClearance = 1000;
        double _dockApproach = 30;
        bool _useBestThrust = true;
        double _flipTime = 30;
        double _defaultCruiseSpeed = 0.75;
        double _cruiseStep = 0.25;
        double _velocityGain = 2.0;
        string _cameraTag = "[Accel]";
        double _scanRange = 15000;
        double _approachBuffer = 75;
        bool _approachFullThrust = true;
        double _maxSpeed = 100;
        double _brakeSafety = 0.8;
        double _defaultHydrogenThrustPerLiter = 1400;
        double _defaultUraniumMWhPerKg = 1.0;
        double _defaultElectricThrustPerMW = 120000;
        bool _planetZonesConfig = false;
        double _planetCruiseHeight = 1500;
        double _atmosphereHeight = 12000;
        double _atmosphereSpeed = 100;
        double _gravityFalloff = 7;
        bool _compensateWind = true;
        double _zoneEntrySpeed = 100;
        double _waterLevel = 0;
        float _screenTextScale = 1;
        double _zoneRadiusGuess = 200000;

        // ---- state ----
        double _limit;
        bool _enabled = true;
        bool _limitDampeners;
        double _cruiseSpeed;
        double _hydrogenThrustPerLiter;     // N*s per liter of hydrogen
        double _uraniumMWhPerKg;            // reactor energy per kg of fuel
        bool _hydrogenCalibrated;
        bool _uraniumCalibrated;

        // Reads an option from Custom Data and writes it back, so every option is
        // visible and editable there.
        double Option(string key, double value)
        {
            value = _ini.Get(IniSection, key).ToDouble(value);
            _ini.Set(IniSection, key, value);
            return value;
        }

        bool Option(string key, bool value)
        {
            value = _ini.Get(IniSection, key).ToBoolean(value);
            _ini.Set(IniSection, key, value);
            return value;
        }

        string Option(string key, string value)
        {
            value = _ini.Get(IniSection, key).ToString(value);
            _ini.Set(IniSection, key, value);
            return value;
        }

        int Option(string key, int value)
        {
            value = _ini.Get(IniSection, key).ToInt32(value);
            _ini.Set(IniSection, key, value);
            return value;
        }

        void LoadConfig()
        {
            MyIniParseResult result;
            if (!_ini.TryParse(Me.CustomData, out result))
            {
                _message = "Custom Data error line " + result.LineNo + ": " + result.Error;
                return;
            }

            _defaultLimit = Option("DefaultAcceleration", _defaultLimit);
            _step = Option("Step", _step);
            _minLimit = Option("MinAcceleration", _minLimit);
            _maxLimit = Option("MaxAcceleration", _maxLimit);
            _limitDampenersDefault = Option("LimitDampeners", _limitDampenersDefault);
            _dampenerGain = Option("DampenerGain", _dampenerGain);
            _lcdTag = Option("LcdTag", _lcdTag);
            _statusTag = Option("StatusTag", _statusTag);
            _cockpitSurface = Option("CockpitSurface", _cockpitSurface);
            _statusCockpitSurface = Option("StatusCockpitSurface", _statusCockpitSurface);
            _mapTag = Option("MapTag", _mapTag);
            _listTag = Option("ListTag", _listTag);
            _mapCockpitSurface = Option("MapCockpitSurface", _mapCockpitSurface);
            _listCockpitSurface = Option("ListCockpitSurface", _listCockpitSurface);
            _autoLog = Option("AutoLogMining", _autoLog);
            _logStone = Option("LogStone", _logStone);
            _mergeDistance = Option("MergeDistance", _mergeDistance);
            _gravityWellFactor = Option("GravityWellFactor", _gravityWellFactor);
            _survey = Option("Survey", _survey);
            _surveyRange = Option("SurveyRange", _surveyRange);
            _probeRange = Option("SearchRange", _probeRange);
            _alignShip = Option("AlignShip", _alignShip);
            _guard = Option("CollisionGuard", _guard);
            _avoidGravity = Option("AvoidGravityWells", _avoidGravity);
            _importTag = Option("ImportTag", _importTag);
            _useJump = Option("UseJumpDrive", _useJump);
            _jumpThreshold = Option("JumpMinDistance", _jumpThreshold);
            _jumpArrival = Option("JumpArrival", _jumpArrival);
            _jumpClearance = Option("JumpClearance", _jumpClearance);
            _dockApproach = Option("DockApproach", _dockApproach);
            _useBestThrust = Option("UseStrongestThrusters", _useBestThrust);
            _flipTime = Option("FlipTime", _flipTime);
            _defaultCruiseSpeed = Option("CruiseSpeed", _defaultCruiseSpeed);
            _cruiseStep = Option("CruiseStep", _cruiseStep);
            _velocityGain = Option("VelocityGain", _velocityGain);
            _cameraTag = Option("CameraTag", _cameraTag);
            _scanRange = Option("ScanRange", _scanRange);
            _approachBuffer = Option("ApproachBuffer", _approachBuffer);
            _approachFullThrust = Option("ApproachFullThrust", _approachFullThrust);
            _maxSpeed = Option("MaxSpeed", _maxSpeed);
            _brakeSafety = Option("BrakeSafety", _brakeSafety);
            _defaultHydrogenThrustPerLiter = Option("HydrogenThrustPerLiter", _defaultHydrogenThrustPerLiter);
            _defaultUraniumMWhPerKg = Option("UraniumMWhPerKg", _defaultUraniumMWhPerKg);
            _defaultElectricThrustPerMW = Option("ElectricThrustPerMW", _defaultElectricThrustPerMW);
            _planetZonesConfig = Option("PlanetZones", _planetZonesConfig);
            _planetCruiseHeight = Option("PlanetCruiseHeight", _planetCruiseHeight);
            _atmosphereHeight = Option("AtmosphereHeight", _atmosphereHeight);
            _atmosphereSpeed = Option("AtmosphereSpeed", _atmosphereSpeed);
            _gravityFalloff = Option("GravityFalloff", _gravityFalloff);
            _compensateWind = Option("CompensateWind", _compensateWind);
            _zoneEntrySpeed = Option("ZoneEntrySpeed", _zoneEntrySpeed);
            _waterLevel = Option("WaterLevel", _waterLevel);
            _screenTextScale = (float)MathHelper.Clamp(Option("ScreenTextScale", _screenTextScale), 0.5, 2);
            _zoneRadiusGuess = Option("ZoneRadiusGuess", _zoneRadiusGuess);

            _ini.SetSectionComment(IniSection, " Units: m/s², m/s, m. Options: see README. Run 'reload' after editing.");
            Me.CustomData = _ini.ToString();

            if (_minLimit > _maxLimit)
                _minLimit = _maxLimit;
            _limit = MathHelper.Clamp(_limit, _minLimit, _maxLimit);
            _brakeSafety = MathHelper.Clamp(_brakeSafety, 0.1, 1.0);
            if (_cruiseStep <= 0)
                _cruiseStep = 0.25;
            if (!_hydrogenCalibrated)
                _hydrogenThrustPerLiter = _defaultHydrogenThrustPerLiter;
            if (!_uraniumCalibrated)
                _uraniumMWhPerKg = _defaultUraniumMWhPerKg;
        }

        void ResetCalibration()
        {
            _hydrogenCalibrated = _uraniumCalibrated = false;
            _hydrogenThrustPerLiter = _defaultHydrogenThrustPerLiter;
            _uraniumMWhPerKg = _defaultUraniumMWhPerKg;
            _hydrogenImpulseTotal = _hydrogenUsedTotal = 0;
            _uraniumEnergyTotal = _uraniumUsedTotal = 0;
            _gyroSign = Vector3D.One;
            _gyroCalibrated = new bool[3];
            _gyroEvidence = Vector3D.Zero;
        }

        void SaveState()
        {
            StoreDock();
            var state = new MyIni();
            state.Set(StateSection, "Limit", _limit);
            state.Set(StateSection, "Enabled", _enabled);
            state.Set(StateSection, "LimitDampeners", _limitDampeners);
            state.Set(StateSection, "CruiseSpeed", _cruiseSpeed);
            state.Set(StateSection, "HydrogenCalibrated", _hydrogenCalibrated);
            state.Set(StateSection, "HydrogenThrustPerLiter", _hydrogenThrustPerLiter);
            state.Set(StateSection, "UraniumCalibrated", _uraniumCalibrated);
            state.Set(StateSection, "UraniumMWhPerKg", _uraniumMWhPerKg);
            state.Set(StateSection, "Zoom", _zoomIndex);
            state.Set(StateSection, "GyroTorque", Vec(_gyroTorque));
            state.Set(StateSection, "GyroSign", string.Join(";", Num(_gyroSign.X), Num(_gyroSign.Y), Num(_gyroSign.Z),
                _gyroCalibrated[0] ? "1" : "0", _gyroCalibrated[1] ? "1" : "0", _gyroCalibrated[2] ? "1" : "0"));
            WriteDock(state, StateSection);
            state.Set(StateSection, "Zone", _zone);
            state.Set(StateSection, "PlanetZonesSeen", _planetZonesSeen);
            state.Set(StateSection, "ThrustersOff", _thrustersOff);
            if (_cameFromValid)
                state.Set(StateSection, "CameFrom", Vec(_cameFrom) + ";" + Vec(_cameFromAt));
            var radii = new StringBuilder();
            foreach (KeyValuePair<string, double> r in _zoneRadii)
                radii.Append(r.Key + "=" + Num(r.Value) + "|");
            state.Set(StateSection, "ZoneRadii", radii.ToString());
            SaveMap(state);
            Storage = state.ToString();
        }

        // The dock data (see Docking.cs, Paths.cs) in an ini section: the state,
        // or a base entry's own copy (one dock per base, OreMap.cs).
        static void CopySection(MyIni from, string a, MyIni to, string b)
        {
            var keys = new List<MyIniKey>();
            from.GetKeys(a, keys);
            foreach (MyIniKey k in keys)
                to.Set(b, k.Name, from.Get(k).ToString());
        }

        void WriteDock(MyIni ini, string section)
        {
            if (_dockKnown)
            {
                ini.Set(section, "Dock", string.Join(";", Vec(_dockPosition), Vec(_dockAxis), Vec(_dockForward), Vec(_dockUp),
                    _dockConnectorId.ToString(), _dockGridId.ToString(), Vec(_dockGridPosition), Vec(_dockGridForward), Vec(_dockGridUp)));
                ini.Set(section, "BaseGrids", string.Join(";", _baseGrids));
                ini.Set(section, "DockZone", _dockZone);
                if (_baseKnown)
                {
                    // Base pose and all dock data relative to it (see Paths.cs).
                    ini.Set(section, "DockBase", Vec(_baseMatrix.Translation) + ";" + Vec(_baseMatrix.Forward) + ";"
                        + Vec(_baseMatrix.Up) + ";" + Vec(_baseCenterLocal) + ";" + Vec(_baseHalf));
                    var local = new StringBuilder();
                    foreach (Vector3D v in _dockLocal)
                        local.Append(Vec(v)).Append(';');
                    ini.Set(section, "DockLocal", local.ToString());
                    ini.Set(section, "DockPath", PathText(_dockPathLocal));
                    ini.Set(section, "DockGate", _dockBaseConnectorId);
                }
            }
        }

        void ReadDock(MyIni ini, string section)
        {
            _dockKnown = _baseKnown = _dockProvisional = false;
            _baseGrids.Clear();
            _dockPathLocal = new List<PathPoint>();
            _dockBaseConnectorId = 0;
            _baseHalf = Vector3D.Zero;
            string[] dock = ini.Get(section, "Dock").ToString("").Split(';');
            if (dock.Length == 23)
            {
                _dockGridPosition = ParseVec(dock, 14);
                _dockGridForward = ParseVec(dock, 17);
                _dockGridUp = ParseVec(dock, 20);
                _dockPosition = ParseVec(dock, 0);
                _dockAxis = ParseVec(dock, 3);
                _dockForward = ParseVec(dock, 6);
                _dockUp = ParseVec(dock, 9);
                _dockKnown = long.TryParse(dock[12], out _dockConnectorId) && long.TryParse(dock[13], out _dockGridId);
            }
            string[] baseParts = ini.Get(section, "DockBase").ToString("").Split(';');
            string[] localParts = ini.Get(section, "DockLocal").ToString("").Split(';');
            if (_dockKnown && baseParts.Length >= 12 && localParts.Length >= 21)
            {
                _baseMatrix = MatrixD.CreateWorld(ParseVec(baseParts, 0), ParseVec(baseParts, 3), ParseVec(baseParts, 6));
                _baseCenterLocal = ParseVec(baseParts, 9);
                if (baseParts.Length >= 15)
                    _baseHalf = ParseVec(baseParts, 12);
                for (int i = 0; i < 7; i++)
                    _dockLocal[i] = ParseVec(localParts, i * 3);
                _dockPathLocal = ParsePath(ini.Get(section, "DockPath").ToString(""));
                _dockBaseConnectorId = ini.Get(section, "DockGate").ToInt64();
                _baseKnown = true;
            }
            foreach (string id in ini.Get(section, "BaseGrids").ToString("").Split(';'))
            {
                long grid;
                if (long.TryParse(id, out grid))
                    _baseGrids.Add(grid);
            }
            _dockZone = ini.Get(section, "DockZone").ToString("");
        }

        void LoadState()
        {
            var state = new MyIni();
            if (!state.TryParse(Storage) || !state.ContainsSection(StateSection))
            {
                LoadLegacyState();
                return;
            }

            _limit = MathHelper.Clamp(state.Get(StateSection, "Limit").ToDouble(_limit), _minLimit, _maxLimit);
            _enabled = state.Get(StateSection, "Enabled").ToBoolean(_enabled);
            _limitDampeners = state.Get(StateSection, "LimitDampeners").ToBoolean(_limitDampeners);
            _cruiseSpeed = Math.Max(state.Get(StateSection, "CruiseSpeed").ToDouble(_cruiseSpeed), 0.01);
            _hydrogenCalibrated = state.Get(StateSection, "HydrogenCalibrated").ToBoolean(false);
            if (_hydrogenCalibrated)
                _hydrogenThrustPerLiter = state.Get(StateSection, "HydrogenThrustPerLiter").ToDouble(_hydrogenThrustPerLiter);
            _uraniumCalibrated = state.Get(StateSection, "UraniumCalibrated").ToBoolean(false);
            if (_uraniumCalibrated)
                _uraniumMWhPerKg = state.Get(StateSection, "UraniumMWhPerKg").ToDouble(_uraniumMWhPerKg);
            _zoomIndex = MathHelper.Clamp(state.Get(StateSection, "Zoom").ToInt32(_zoomIndex), 0, ZoomLevels.Length - 1);
            string[] torque = state.Get(StateSection, "GyroTorque").ToString("").Split(';');
            if (torque.Length == 3)
                _gyroTorque = ParseVec(torque, 0);
            string[] gyro = state.Get(StateSection, "GyroSign").ToString("").Split(';');
            double x, y, z;
            if (gyro.Length == 6 && TryParseNumber(gyro[0], out x) && TryParseNumber(gyro[1], out y) && TryParseNumber(gyro[2], out z))
            {
                _gyroSign = new Vector3D(x, y, z);
                for (int i = 0; i < 3; i++)
                    _gyroCalibrated[i] = gyro[3 + i] == "1";
            }
            ReadDock(state, StateSection);
            _zone = state.Get(StateSection, "Zone").ToString("");
            _planetZonesSeen = state.Get(StateSection, "PlanetZonesSeen").ToBoolean(false);
            _thrustersOff = state.Get(StateSection, "ThrustersOff").ToBoolean(false);
            string[] came = state.Get(StateSection, "CameFrom").ToString("").Split(';');
            if (came.Length == 6)
            {
                _cameFrom = ParseVec(came, 0);
                _cameFromAt = ParseVec(came, 3);
                _cameFromValid = _cameFrom.LengthSquared() > 0.5;
            }
            foreach (string entry in state.Get(StateSection, "ZoneRadii").ToString("").Split('|'))
            {
                string[] kv = entry.Split('=');
                double radius;
                if (kv.Length == 2 && TryParseNumber(kv[1], out radius))
                    _zoneRadii[kv[0]] = radius;
            }
            LoadMap(state);
            foreach (Deposit d in _deposits)
                if (d.Ore == BaseName && d.Zone == _dockZone && _dockKnown
                    && Vector3D.Distance(d.Position, _dockPosition) < _mergeDistance * 2)
                    _dockEntry = d;
        }

        static string Vec(Vector3D v)
        {
            return Num(v.X) + ";" + Num(v.Y) + ";" + Num(v.Z);
        }

        Vector3D ParseVec(string[] parts, int index)
        {
            double x, y, z;
            TryParseNumber(parts[index], out x);
            TryParseNumber(parts[index + 1], out y);
            TryParseNumber(parts[index + 2], out z);
            return new Vector3D(x, y, z);
        }

        // State format of the first versions: "limit;enabled;limitDampeners[;cruiseSpeed]".
        void LoadLegacyState()
        {
            string[] parts = Storage.Split(';');
            if (parts.Length < 3)
                return;
            double value;
            if (TryParseNumber(parts[0], out value))
                _limit = MathHelper.Clamp(value, _minLimit, _maxLimit);
            _enabled = parts[1] == "1";
            _limitDampeners = parts[2] == "1";
            if (parts.Length > 3 && TryParseNumber(parts[3], out value) && value > 0)
                _cruiseSpeed = value;
        }
    }
}
