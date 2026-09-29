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
    // Screen output on LCD panels and cockpit screens:
    //   [Accel]         control page (text)
    //   [Accel Status]  ship status page (text)
    //   [Accel Map]     ore map with buttons, radar or list view (sprites)
    //   [Accel List]    ore map, always the list view (sprites)
    // plus the programmable block's own info.
    partial class Program
    {
        const int MaxOreLines = 6;

        readonly List<IMyTextPanel> _controlPanels = new List<IMyTextPanel>();
        readonly List<IMyTextPanel> _statusPanels = new List<IMyTextPanel>();
        readonly List<IMyTextPanel> _mapPanels = new List<IMyTextPanel>();
        readonly List<IMyTextPanel> _listPanels = new List<IMyTextPanel>();
        readonly StringBuilder _text = new StringBuilder();

        void Put(string format, params object[] args)
        {
            _text.AppendFormat(format, args);
        }

        void Line(string text = "")
        {
            _text.AppendLine(text);
        }
        readonly List<KeyValuePair<string, double>> _oreSorted = new List<KeyValuePair<string, double>>();

        void FindDisplays()
        {
            GridTerminalSystem.GetBlocksOfType(_controlPanels, p => p.IsSameConstructAs(Me) && p.CustomName.Contains(_lcdTag));
            GridTerminalSystem.GetBlocksOfType(_statusPanels, p => p.IsSameConstructAs(Me) && p.CustomName.Contains(_statusTag));
            GridTerminalSystem.GetBlocksOfType(_mapPanels, p => p.IsSameConstructAs(Me) && p.CustomName.Contains(_mapTag));
            GridTerminalSystem.GetBlocksOfType(_listPanels, p => p.IsSameConstructAs(Me) && p.CustomName.Contains(_listTag));
            _mapChanged = true;
        }

        void UpdateDisplays()
        {
            IMyShipController info = _controller ?? _layoutController;

            string control = BuildControlText(info);
            string status = BuildStatusText();
            Echo(control + "\n" + status + "\n" + _mapScreenInfo + "\nZone: " + (_zone == "" ? "space" : _zone) + "\n" + _teleportInfo);

            foreach (IMyTextPanel p in _controlPanels)
                WriteSurface(p, control);
            foreach (IMyTextPanel p in _statusPanels)
                if (WithinDrawBudget())
                    DrawStatusSurface(p);

            UpdateVisibleDeposits();
            _frameToggle = !_frameToggle;
            foreach (IMyTextPanel p in _mapPanels)
                if (WithinDrawBudget())
                    DrawMapSurface(p, _view);
            foreach (IMyTextPanel p in _listPanels)
                if (WithinDrawBudget())
                    DrawMapSurface(p, MapView.List);

            var provider = info as IMyTextSurfaceProvider;
            if (provider != null)
            {
                WriteCockpitSurface(provider, _cockpitSurface, control);
                if (_statusCockpitSurface >= 0 && _statusCockpitSurface < provider.SurfaceCount && WithinDrawBudget())
                    DrawStatusSurface(provider.GetSurface(_statusCockpitSurface));
                if (_mapCockpitSurface >= 0 && _mapCockpitSurface < provider.SurfaceCount && WithinDrawBudget())
                    DrawMapSurface(provider.GetSurface(_mapCockpitSurface), _view);
                if (_listCockpitSurface >= 0 && _listCockpitSurface < provider.SurfaceCount && WithinDrawBudget())
                    DrawMapSurface(provider.GetSurface(_listCockpitSurface), MapView.List);
            }

            // Map screens carry the deposits as GPS lines in their Custom Data.
            if (_mapChanged)
            {
                string gps = BuildExport();
                foreach (IMyTextPanel p in _mapPanels)
                    p.CustomData = gps;
                foreach (IMyTextPanel p in _listPanels)
                    p.CustomData = gps;
                // Store right away: the game only calls Save() when the world is saved,
                // so recompiling the script would otherwise lose recent entries.
                SaveState();
                _mapChanged = false;
            }
        }

        // Map screens are skipped for one round when the tick is already busy,
        // instead of risking the programmable block's instruction limit.
        bool WithinDrawBudget()
        {
            return Runtime.CurrentInstructionCount < Runtime.MaxInstructionCount * DrawBudget;
        }

        string BuildControlText(IMyShipController info)
        {
            double mass = info != null ? info.CalculateShipMass().PhysicalMass : 0;

            _text.Clear();
            Line("Acceleration Control");
            Line(_enabled ? "Status: ON" : "Status: OFF (vanilla thrust)");
            Put("Limit: {0:0.00} m/s² ({1:0.00} g)\n", _limit, _limit / 9.81);
            Line("Dampeners: " + (_limitDampeners ? "limited" : "full thrust"));
            AppendModeStatus();
            AppendPlanetStatus();

            if (mass > 0)
            {
                Line("Max. acceleration:");
                Put(" Fwd {0:0.0}  Back {1:0.0}\n", MaxAccel(2, 1, mass), MaxAccel(2, 0, mass));
                Put(" Up  {0:0.0}  Down {1:0.0}\n", MaxAccel(1, 0, mass), MaxAccel(1, 1, mass));
                Put(" Left {0:0.0}  Right {1:0.0}\n", MaxAccel(0, 1, mass), MaxAccel(0, 0, mass));
            }
            else
            {
                Line("No ship controller found");
            }

            if (_message.Length > 0)
                Line(_message);
            return _text.ToString();
        }

        // Gravity, height, air and the measured wind while near a planet.
        void AppendPlanetStatus()
        {
            if (_zoneGoal)
                Line("Waiting for the zone of " + _zoneGoalName);
            // Any measurable gravity is shown, also without a real planet (e.g. a
            // Real Solar Systems proxy).
            if (_planet == null && _gravity.LengthSquared() < 1e-6)
                return;
            Put("Gravity {0:0.000} g", _gravity.Length() / 9.81);
            if (_planet == null)
                _text.Append("  no planet");
            else
                _text.Append("  alt " + FormatDistance(Vector3D.Distance(ReferencePosition(), _planet.Center) - _planet.Radius));
            if (_air >= 0)
                Put("  air {0:0}%", _air * 100);
            Line();
            if (_disturbance.LengthSquared() > 0.01)
                Put("Wind/drag {0:0.0} m/s² compensated\n", _disturbance.Length());
        }

        void AppendModeStatus()
        {
            if (_scanPending)
                Put("Scanning... camera {0:0}%\n", ScanCharge() * 100);
            switch (_mode)
            {
                case Mode.Cruise:
                    Put("Cruise: {0:0.00} m/s (now {1:0.00})\n", _cruiseSpeed, _forwardSpeed);
                    break;
                case Mode.Jump:
                    Put("Jump {0}: {1}\n", FormatDistance(_jumpDistance), _jumpState);
                    break;
                case Mode.Dock:
                case Mode.Path:
                    Line(DockTitle + ": " + DockPhaseText());
                    break;
                case Mode.Land:
                    Line("Landing: " + _landState + (_landPhase > 2 ? ", " + FormatDistance(_targetDistance) : ""));
                    break;
                case Mode.Approach:
                    Put("Approach {0}: {1}, {2:0} m/s\n", _targetName, FormatDistance(_remainingDistance), _currentSpeed);
                    Put("{0}, stopping distance {1}, {2}\n", _approachPhase, FormatDistance(_stopDistance), EtaText());
                    break;
                default:
                    Put("Cruise speed: {0:0.00} m/s (off)\n", _cruiseSpeed);
                    break;
            }
        }

        string BuildStatusText()
        {
            _text.Clear();
            Line("Ship Status");

            if (_cargoMaxVolume > 0)
            {
                double fill = _cargoVolume / _cargoMaxVolume;
                Put("Cargo {0} {1,4:0}%  {2}\n", Bar(fill, 10), fill * 100, FormatMass(_cargoMass));
                AppendOres();
            }

            if (_batteryMax > 0)
            {
                Put("Battery  {0,4:0}%  ", _batteryStored / _batteryMax * 100);
                if (_batteryNetOutput > 1e-6)
                    Put("{0}  -{1:0.00} MW\n", FormatTime(BatteryTimeRemaining()), _batteryNetOutput);
                else
                    Put("charging +{0:0.00} MW\n", -_batteryNetOutput);
            }

            if (_reactors.Count > 0)
                Put("Uranium  {0}  {1}\n", FormatMass(_uranium), FormatTime(TimeRemaining(_uranium, _uraniumRate)));

            if (_hydrogenCapacity > 0)
                Put("Hydrogen {0,4:0}%  {1}  {2}\n", _hydrogen / _hydrogenCapacity * 100,
                    FormatVolume(_hydrogen), FormatTime(TimeRemaining(_hydrogen, _hydrogenRate)));

            if (_jumpMax > 0)
                Put("Jump     {0,4:0}%\n", _jumpStored / _jumpMax * 100);

            double total = _deltaVHydrogen + _deltaVElectric;
            if (total > 0)
            {
                Line("Delta-v (estimate):");
                if (_deltaVHydrogen > 0)
                    Put(" Hydrogen {0,7:0} m/s{1}\n", _deltaVHydrogen, _hydrogenCalibrated ? "" : " *");
                if (_deltaVElectric > 0)
                    Put(" Electric {0,7:0} m/s{1}\n", _deltaVElectric,
                        _reactors.Count > 0 && !_uraniumCalibrated ? " *" : "");
                Put(" Total    {0,7:0} m/s\n", total);
                Put(" = {0:0.#} trips at {1:0} m/s\n", total / TripDeltaV(), _maxSpeed);
                if (!_hydrogenCalibrated || (_reactors.Count > 0 && !_uraniumCalibrated))
                    Line(" * not calibrated yet");
            }

            return _text.ToString();
        }

        void AppendOres()
        {
            _oreSorted.Clear();
            foreach (KeyValuePair<string, double> ore in _oreAmounts)
                _oreSorted.Add(ore);
            _oreSorted.Sort((a, b) => b.Value.CompareTo(a.Value));

            for (int i = 0; i < _oreSorted.Count && i < MaxOreLines; i++)
                Put("  {0,-10} {1,9}\n", _oreSorted[i].Key, FormatMass(_oreSorted[i].Value));
            if (_oreSorted.Count > MaxOreLines)
                Put("  +{0} more\n", _oreSorted.Count - MaxOreLines);
        }

        void WriteCockpitSurface(IMyTextSurfaceProvider provider, int index, string text)
        {
            if (index >= 0 && index < provider.SurfaceCount)
                WriteSurface(provider.GetSurface(index), text);
        }

        void WriteSurface(IMyTextSurface surface, string text)
        {
            surface.ContentType = ContentType.TEXT_AND_IMAGE;
            surface.WriteText(text);
        }

        // -----------------------------------------------------------------
        //  Formatting helpers
        // -----------------------------------------------------------------

        string Bar(double fraction, int width)
        {
            int filled = (int)Math.Round(MathHelper.Clamp(fraction, 0, 1) * width);
            return "[" + new string('|', filled) + new string('.', width - filled) + "]";
        }

        string FormatDistance(double meters)
        {
            if (meters == double.MaxValue)
                return "other zone";    // entry recorded in another planet zone
            return meters >= 1000 ? (meters / 1000).ToString("0.00") + " km" : meters.ToString("0") + " m";
        }

        string FormatMass(double kg)
        {
            return kg >= 1000 ? (kg / 1000).ToString("0.0") + " t" : kg.ToString("0.0") + " kg";
        }

        string FormatVolume(double liters)
        {
            return liters >= 1e6 ? (liters / 1e6).ToString("0.00") + " ML"
                : liters >= 1000 ? (liters / 1000).ToString("0.0") + " kL"
                : liters.ToString("0") + " L";
        }

        string FormatTime(double seconds)
        {
            if (seconds < 0)
                return "idle";
            if (seconds >= 100 * 3600)
                return ">99h";
            int s = (int)seconds;
            return s >= 3600 ? string.Format("{0}h {1:00}m", s / 3600, s % 3600 / 60)
                : string.Format("{0}m {1:00}s", s / 60, s % 60);
        }
    }
}
