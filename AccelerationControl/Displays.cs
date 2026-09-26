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
            Echo(control + "\n" + status);

            foreach (IMyTextPanel p in _controlPanels)
                WriteSurface(p, control);
            foreach (IMyTextPanel p in _statusPanels)
                WriteSurface(p, status);

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
                WriteCockpitSurface(provider, _statusCockpitSurface, status);
                if (_mapCockpitSurface >= 0 && _mapCockpitSurface < provider.SurfaceCount && WithinDrawBudget())
                    DrawMapSurface(provider.GetSurface(_mapCockpitSurface), _view);
                if (_listCockpitSurface >= 0 && _listCockpitSurface < provider.SurfaceCount && WithinDrawBudget())
                    DrawMapSurface(provider.GetSurface(_listCockpitSurface), MapView.List);
            }

            // Map screens carry the deposits as GPS lines in their Custom Data.
            if (_mapChanged)
            {
                string gps = BuildGpsList();
                foreach (IMyTextPanel p in _mapPanels)
                    p.CustomData = gps;
                foreach (IMyTextPanel p in _listPanels)
                    p.CustomData = gps;
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
            _text.AppendLine("Acceleration Control");
            _text.AppendLine(_enabled ? "Status: ON" : "Status: OFF (vanilla thrust)");
            _text.AppendFormat("Limit: {0:0.00} m/s² ({1:0.00} g)\n", _limit, _limit / 9.81);
            _text.AppendLine("Dampeners: " + (_limitDampeners ? "limited" : "full thrust"));
            AppendModeStatus();

            if (mass > 0)
            {
                _text.AppendLine("Max. acceleration:");
                _text.AppendFormat(" Fwd {0:0.0}  Back {1:0.0}\n", MaxAccel(2, 1, mass), MaxAccel(2, 0, mass));
                _text.AppendFormat(" Up  {0:0.0}  Down {1:0.0}\n", MaxAccel(1, 0, mass), MaxAccel(1, 1, mass));
                _text.AppendFormat(" Left {0:0.0}  Right {1:0.0}\n", MaxAccel(0, 1, mass), MaxAccel(0, 0, mass));
            }
            else
            {
                _text.AppendLine("No ship controller found");
            }

            if (_message.Length > 0)
                _text.AppendLine(_message);
            return _text.ToString();
        }

        void AppendModeStatus()
        {
            if (_scanPending)
                _text.AppendFormat("Scanning... camera {0:0}%\n", ScanCharge() * 100);
            switch (_mode)
            {
                case Mode.Cruise:
                    _text.AppendFormat("Cruise: {0:0.00} m/s (now {1:0.00})\n", _cruiseSpeed, _forwardSpeed);
                    break;
                case Mode.Approach:
                    _text.AppendFormat("Approach {0}: {1}, {2:0} m/s\n", _targetName, FormatDistance(_targetDistance), _currentSpeed);
                    _text.AppendFormat("{0}, stopping distance {1}\n", _approachPhase, FormatDistance(_stopDistance));
                    break;
                default:
                    _text.AppendFormat("Cruise speed: {0:0.00} m/s (off)\n", _cruiseSpeed);
                    break;
            }
        }

        string BuildStatusText()
        {
            _text.Clear();
            _text.AppendLine("Ship Status");

            if (_cargoMaxVolume > 0)
            {
                double fill = _cargoVolume / _cargoMaxVolume;
                _text.AppendFormat("Cargo {0} {1,4:0}%  {2}\n", Bar(fill, 10), fill * 100, FormatMass(_cargoMass));
                AppendOres();
            }

            if (_batteryMax > 0)
            {
                _text.AppendFormat("Battery  {0,4:0}%  ", _batteryStored / _batteryMax * 100);
                if (_batteryNetOutput > 1e-6)
                    _text.AppendFormat("{0}  -{1:0.00} MW\n", FormatTime(BatteryTimeRemaining()), _batteryNetOutput);
                else
                    _text.AppendFormat("charging +{0:0.00} MW\n", -_batteryNetOutput);
            }

            if (_reactors.Count > 0)
                _text.AppendFormat("Uranium  {0}  {1}\n", FormatMass(_uranium), FormatTime(TimeRemaining(_uranium, _uraniumRate)));

            if (_hydrogenCapacity > 0)
                _text.AppendFormat("Hydrogen {0,4:0}%  {1}  {2}\n", _hydrogen / _hydrogenCapacity * 100,
                    FormatVolume(_hydrogen), FormatTime(TimeRemaining(_hydrogen, _hydrogenRate)));

            if (_jumpMax > 0)
                _text.AppendFormat("Jump     {0,4:0}%\n", _jumpStored / _jumpMax * 100);

            double total = _deltaVHydrogen + _deltaVElectric;
            if (total > 0)
            {
                _text.AppendLine("Delta-v (estimate):");
                if (_deltaVHydrogen > 0)
                    _text.AppendFormat(" Hydrogen {0,7:0} m/s{1}\n", _deltaVHydrogen, _hydrogenCalibrated ? "" : " *");
                if (_deltaVElectric > 0)
                    _text.AppendFormat(" Electric {0,7:0} m/s{1}\n", _deltaVElectric,
                        _reactors.Count > 0 && !_uraniumCalibrated ? " *" : "");
                _text.AppendFormat(" Total    {0,7:0} m/s\n", total);
                _text.AppendFormat(" = {0:0.#} trips at {1:0} m/s\n", total / TripDeltaV(), _maxSpeed);
                if (!_hydrogenCalibrated || (_reactors.Count > 0 && !_uraniumCalibrated))
                    _text.AppendLine(" * not calibrated yet");
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
                _text.AppendFormat("  {0,-10} {1,9}\n", _oreSorted[i].Key, FormatMass(_oreSorted[i].Value));
            if (_oreSorted.Count > MaxOreLines)
                _text.AppendFormat("  +{0} more\n", _oreSorted.Count - MaxOreLines);
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
