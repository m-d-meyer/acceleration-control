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

        // ---- state ----
        double _limit;
        bool _enabled = true;
        bool _limitDampeners;
        double _cruiseSpeed;
        double _hydrogenThrustPerLiter;     // N*s per liter of hydrogen
        double _uraniumMWhPerKg;            // reactor energy per kg of fuel
        bool _hydrogenCalibrated;
        bool _uraniumCalibrated;

        void LoadConfig()
        {
            MyIniParseResult result;
            if (!_ini.TryParse(Me.CustomData, out result))
            {
                _message = "Custom Data error line " + result.LineNo + ": " + result.Error;
                return;
            }

            _defaultLimit = _ini.Get(IniSection, "DefaultAcceleration").ToDouble(_defaultLimit);
            _step = _ini.Get(IniSection, "Step").ToDouble(_step);
            _minLimit = _ini.Get(IniSection, "MinAcceleration").ToDouble(_minLimit);
            _maxLimit = _ini.Get(IniSection, "MaxAcceleration").ToDouble(_maxLimit);
            _limitDampenersDefault = _ini.Get(IniSection, "LimitDampeners").ToBoolean(_limitDampenersDefault);
            _dampenerGain = _ini.Get(IniSection, "DampenerGain").ToDouble(_dampenerGain);
            _lcdTag = _ini.Get(IniSection, "LcdTag").ToString(_lcdTag);
            _statusTag = _ini.Get(IniSection, "StatusTag").ToString(_statusTag);
            _cockpitSurface = _ini.Get(IniSection, "CockpitSurface").ToInt32(_cockpitSurface);
            _statusCockpitSurface = _ini.Get(IniSection, "StatusCockpitSurface").ToInt32(_statusCockpitSurface);
            _mapTag = _ini.Get(IniSection, "MapTag").ToString(_mapTag);
            _listTag = _ini.Get(IniSection, "ListTag").ToString(_listTag);
            _mapCockpitSurface = _ini.Get(IniSection, "MapCockpitSurface").ToInt32(_mapCockpitSurface);
            _listCockpitSurface = _ini.Get(IniSection, "ListCockpitSurface").ToInt32(_listCockpitSurface);
            _autoLog = _ini.Get(IniSection, "AutoLogMining").ToBoolean(_autoLog);
            _logStone = _ini.Get(IniSection, "LogStone").ToBoolean(_logStone);
            _mergeDistance = _ini.Get(IniSection, "MergeDistance").ToDouble(_mergeDistance);
            _gravityWellFactor = _ini.Get(IniSection, "GravityWellFactor").ToDouble(_gravityWellFactor);
            _survey = _ini.Get(IniSection, "Survey").ToBoolean(_survey);
            _surveyRange = _ini.Get(IniSection, "SurveyRange").ToDouble(_surveyRange);
            _probeRange = _ini.Get(IniSection, "SearchRange").ToDouble(_probeRange);
            _defaultCruiseSpeed = _ini.Get(IniSection, "CruiseSpeed").ToDouble(_defaultCruiseSpeed);
            _cruiseStep = _ini.Get(IniSection, "CruiseStep").ToDouble(_cruiseStep);
            _velocityGain = _ini.Get(IniSection, "VelocityGain").ToDouble(_velocityGain);
            _cameraTag = _ini.Get(IniSection, "CameraTag").ToString(_cameraTag);
            _scanRange = _ini.Get(IniSection, "ScanRange").ToDouble(_scanRange);
            _approachBuffer = _ini.Get(IniSection, "ApproachBuffer").ToDouble(_approachBuffer);
            _approachFullThrust = _ini.Get(IniSection, "ApproachFullThrust").ToBoolean(_approachFullThrust);
            _maxSpeed = _ini.Get(IniSection, "MaxSpeed").ToDouble(_maxSpeed);
            _brakeSafety = _ini.Get(IniSection, "BrakeSafety").ToDouble(_brakeSafety);
            _defaultHydrogenThrustPerLiter = _ini.Get(IniSection, "HydrogenThrustPerLiter").ToDouble(_defaultHydrogenThrustPerLiter);
            _defaultUraniumMWhPerKg = _ini.Get(IniSection, "UraniumMWhPerKg").ToDouble(_defaultUraniumMWhPerKg);
            _defaultElectricThrustPerMW = _ini.Get(IniSection, "ElectricThrustPerMW").ToDouble(_defaultElectricThrustPerMW);

            // Write back so every option is visible and editable in Custom Data.
            _ini.Set(IniSection, "DefaultAcceleration", _defaultLimit);
            _ini.Set(IniSection, "Step", _step);
            _ini.Set(IniSection, "MinAcceleration", _minLimit);
            _ini.Set(IniSection, "MaxAcceleration", _maxLimit);
            _ini.Set(IniSection, "LimitDampeners", _limitDampenersDefault);
            _ini.Set(IniSection, "DampenerGain", _dampenerGain);
            _ini.Set(IniSection, "LcdTag", _lcdTag);
            _ini.Set(IniSection, "StatusTag", _statusTag);
            _ini.Set(IniSection, "CockpitSurface", _cockpitSurface);
            _ini.Set(IniSection, "StatusCockpitSurface", _statusCockpitSurface);
            _ini.Set(IniSection, "MapTag", _mapTag);
            _ini.Set(IniSection, "ListTag", _listTag);
            _ini.Set(IniSection, "MapCockpitSurface", _mapCockpitSurface);
            _ini.Set(IniSection, "ListCockpitSurface", _listCockpitSurface);
            _ini.Set(IniSection, "AutoLogMining", _autoLog);
            _ini.Set(IniSection, "LogStone", _logStone);
            _ini.Set(IniSection, "MergeDistance", _mergeDistance);
            _ini.Set(IniSection, "GravityWellFactor", _gravityWellFactor);
            _ini.Set(IniSection, "Survey", _survey);
            _ini.Set(IniSection, "SurveyRange", _surveyRange);
            _ini.Set(IniSection, "SearchRange", _probeRange);
            _ini.Set(IniSection, "CruiseSpeed", _defaultCruiseSpeed);
            _ini.Set(IniSection, "CruiseStep", _cruiseStep);
            _ini.Set(IniSection, "VelocityGain", _velocityGain);
            _ini.Set(IniSection, "CameraTag", _cameraTag);
            _ini.Set(IniSection, "ScanRange", _scanRange);
            _ini.Set(IniSection, "ApproachBuffer", _approachBuffer);
            _ini.Set(IniSection, "ApproachFullThrust", _approachFullThrust);
            _ini.Set(IniSection, "MaxSpeed", _maxSpeed);
            _ini.Set(IniSection, "BrakeSafety", _brakeSafety);
            _ini.Set(IniSection, "HydrogenThrustPerLiter", _defaultHydrogenThrustPerLiter);
            _ini.Set(IniSection, "UraniumMWhPerKg", _defaultUraniumMWhPerKg);
            _ini.Set(IniSection, "ElectricThrustPerMW", _defaultElectricThrustPerMW);
            _ini.SetSectionComment(IniSection,
                " Accelerations in m/s² (1 g = 9.81 m/s²), speeds in m/s, distances in m.\n" +
                " *CockpitSurface: cockpit screen index for that page, -1 = off.\n" +
                " HydrogenThrustPerLiter, UraniumMWhPerKg: start values, calibrated in flight.\n" +
                " Run the PB with 'reload' after editing.");
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
        }

        void SaveState()
        {
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
            SaveMap(state);
            Storage = state.ToString();
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
            LoadMap(state);
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
