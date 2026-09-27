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
    // Entry point: block discovery, the update loop and command handling.
    // The features live in the other partial class files:
    //   ThrustControl.cs - acceleration limit via thruster overrides
    //   DriveAssists.cs  - cruise and approach
    //   ShipStatus.cs    - cargo, fuel and delta-v monitoring
    //   Displays.cs      - LCD / cockpit screen output
    //   Config.cs        - Custom Data configuration and saved state
    //   Planets.cs       - planet routes, atmosphere, wind compensation, zones
    partial class Program : MyGridProgram
    {
        const int BlockRefreshTicks = 600;          // rescan blocks every 10 s
        const int DisplayTicks = 10;
        const int DisplayTickOffset = 5;            // keeps display updates off the status update tick
        const double DrawBudget = 0.6;              // share of the instruction limit screens may use
        const double TicksPerSecond = 60.0;

        int _ticks;
        readonly HashSet<long> _ownGrids = new HashSet<long>();

        // A raycast result that hit this ship itself carries no information.
        bool IsOwnHit(MyDetectedEntityInfo hit)
        {
            return hit.EntityId == Me.CubeGrid.EntityId || _ownGrids.Contains(hit.EntityId);
        }
        string _message = "";

        public Program()
        {
            for (int a = 0; a < 3; a++)
                for (int s = 0; s < 2; s++)
                    _axisThrusters[a, s] = new List<IMyThrust>();

            LoadConfig();
            _limit = _defaultLimit;
            _limitDampeners = _limitDampenersDefault;
            _cruiseSpeed = _defaultCruiseSpeed;
            LoadState();
            RefreshBlocks();
            ReleaseAll(true); // clear overrides left behind by a previous run
            ReleaseGyros(true);
            Runtime.UpdateFrequency = UpdateFrequency.Update1;
        }

        public void Save()
        {
            SaveState();
        }

        public void Main(string argument, UpdateType updateSource)
        {
            // Safety net: an unexpected error must not leave thrusters and
            // gyroscopes overridden. Hand control back, show the error, keep running.
            try
            {
                Run(argument, updateSource);
            }
            catch (Exception e)
            {
                ReleaseAll(true);
                ReleaseGyros(true);
                _mode = Mode.Manual;
                _scanPending = false;
                _message = "Error: " + e.Message + " (control handed back)";
                Echo(_message + "\n" + e.StackTrace);
            }
        }

        void Run(string argument, UpdateType updateSource)
        {
            if ((updateSource & (UpdateType.Trigger | UpdateType.Terminal | UpdateType.Script)) != 0
                && !string.IsNullOrWhiteSpace(argument))
                HandleCommand(argument.Trim());

            if ((updateSource & UpdateType.Update1) == 0)
            {
                UpdateDisplays();
                return;
            }

            _ticks++;
            if (_ticks % BlockRefreshTicks == 0)
                RefreshBlocks();

            UpdateZone();
            RunPendingReplan();
            UpdateScan();
            ControlThrust();
            if (_ticks % 2 == 0)
                UpdateGuard();
            if (_ticks % SurveyTicks == 0)
                UpdateSurvey();

            SampleFuelUse(1 / TicksPerSecond);
            if (_ticks % StatusTicks == 30)
                ReceiveMaps();
            if (_ticks % StatusTicks == 0)
                UpdateShipStatus(StatusTicks / TicksPerSecond);

            if (_ticks % DisplayTicks == DisplayTickOffset)
                UpdateDisplays();
        }

        void RefreshBlocks()
        {
            ReleaseAll();

            GridTerminalSystem.GetBlocksOfType(_controllers, c => c.IsSameConstructAs(Me) && c.CanControlShip);
            GridTerminalSystem.GetBlocksOfType(_allThrusters, t => t.IsSameConstructAs(Me));
            _atmoThrusters.Clear();
            _ionThrusters.Clear();
            foreach (IMyThrust t in _allThrusters)
            {
                string subtype = t.BlockDefinition.SubtypeId;
                if (subtype.Contains("Atmospheric"))
                    _atmoThrusters.Add(t);
                else if (!subtype.Contains("Hydrogen"))
                    _ionThrusters.Add(t);
            }
            GridTerminalSystem.GetBlocksOfType(_cameras, c => c.IsSameConstructAs(Me));
            GridTerminalSystem.GetBlocksOfType(_gyros, g => g.IsSameConstructAs(Me));
            GridTerminalSystem.GetBlocksOfType(_connectors, c => c.IsSameConstructAs(Me));
            GridTerminalSystem.GetBlocksOfType(_sensors, s => s.IsSameConstructAs(Me));
            // Raycasts also hit the ship itself (a ray from a camera can pass
            // through the own hull), so all grids of this ship are remembered.
            _ownGrids.Clear();
            GridTerminalSystem.GetBlocksOfType(_blockBuffer, b => b.IsSameConstructAs(Me));
            foreach (IMyTerminalBlock b in _blockBuffer)
                _ownGrids.Add(b.CubeGrid.EntityId);
            FindDisplays();
            FindStatusBlocks();

            _layoutController = null;
            IMyShipController layout = FindActiveController();
            if (layout == null)
                foreach (IMyShipController c in _controllers)
                    if (layout == null || c.IsMainCockpit)
                        layout = c;
            if (layout != null)
                SortThrusters(layout);

            // Keep the approach camera charging so a scan is ready when needed.
            if (!_scanPending && _mode != Mode.Approach)
                _camera = FindCamera();
            if (_camera != null)
                _camera.EnableRaycast = true;
        }

        // -----------------------------------------------------------------
        //  Commands
        // -----------------------------------------------------------------

        void HandleCommand(string argument)
        {
            string[] parts = argument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();
            string value = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;
            string extra = parts.Length > 2 ? parts[2].ToLowerInvariant() : null;
            double parsed;
            _message = "";

            switch (cmd)
            {
                case "up":
                case "+":
                    SetLimit(_limit + ParseStep(value, _step));
                    break;
                case "down":
                case "-":
                    SetLimit(_limit - ParseStep(value, _step));
                    break;
                case "set":
                    if (value != null && TryParseAccel(value, out parsed))
                        SetLimit(parsed);
                    else
                        _message = "Usage: set <m/s2> or set <n>g";
                    break;
                case "reset":
                    SetLimit(_defaultLimit);
                    break;
                case "on":
                    _enabled = true;
                    break;
                case "off":
                    _enabled = false;
                    _mode = Mode.Manual;
                    ReleaseAll(true);
                    break;
                case "toggle":
                    _enabled = !_enabled;
                    if (!_enabled)
                    {
                        _mode = Mode.Manual;
                        ReleaseAll(true);
                    }
                    break;
                case "dampeners":
                    if (value == "on") _limitDampeners = true;
                    else if (value == "off") _limitDampeners = false;
                    else _limitDampeners = !_limitDampeners;
                    break;
                case "cruise":
                    HandleCruiseCommand(value);
                    break;
                case "approach":
                    StartScan(ScanPurpose.Approach);
                    break;
                case "stop":
                    _mode = Mode.Manual;
                    _scanPending = false;
                    break;
                case "mark":
                    HandleMarkCommand(parts);
                    break;
                case "goto":
                    if (parts.Length > 1)
                        GoToGps(argument.Substring(argument.IndexOf(' ') + 1));
                    else
                        GoToSelected();
                    break;
                case "route":
                    PreviewRoute();
                    break;
                case "dock":
                    StartDocking();
                    break;
                case "undock":
                    Undock();
                    break;
                case "select":
                    MoveSelection(value == "prev" ? -1 : 1);
                    break;
                case "delete":
                    DeleteSelected();
                    break;
                case "filter":
                    if (value == null)
                        CycleFilter();
                    else
                    {
                        _filter = value == "all" ? null : NormalizeOre(value);
                        _selected = null;
                    }
                    break;
                case "zoom":
                    HandleZoomCommand(value);
                    break;
                case "view":
                    SwitchView(value == "list" ? MapView.List : MapView.Radar);
                    break;
                case "ui":
                    HandleUiCommand(value);
                    break;
                case "map":
                    HandleMapCommand(value, extra);
                    break;
                case "calibrate":
                    if (value == "reset")
                    {
                        ResetCalibration();
                        _message = "Calibration reset";
                    }
                    else
                        _message = "Usage: calibrate reset";
                    break;
                case "reload":
                case "refresh":
                    LoadConfig();
                    RefreshBlocks();
                    if (_message.Length == 0)
                        _message = "Configuration reloaded";
                    break;
                default:
                    _message = "Unknown command: " + cmd;
                    break;
            }
        }

        double ParseStep(string value, double fallback)
        {
            double step;
            return value != null && TryParseAccel(value, out step) ? step : fallback;
        }

        // Accepts "5", "5.5", "0.5g" (multiples of 9.81 m/s^2).
        bool TryParseAccel(string text, out double result)
        {
            double factor = 1.0;
            text = text.Trim().ToLowerInvariant();
            if (text.EndsWith("g"))
            {
                factor = 9.81;
                text = text.Substring(0, text.Length - 1);
            }
            if (TryParseNumber(text, out result))
            {
                result *= factor;
                return true;
            }
            return false;
        }

        bool TryParseNumber(string text, out double result)
        {
            return double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result);
        }

        static string Num(double value)
        {
            return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        void SetLimit(double value)
        {
            _limit = MathHelper.Clamp(value, _minLimit, _maxLimit);
        }
    }
}
