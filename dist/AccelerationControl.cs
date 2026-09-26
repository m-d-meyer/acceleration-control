// Acceleration Control - Space Engineers programmable block script
// https://github.com/m-d-meyer/acceleration-control
//
// Limits the acceleration of the movement keys, adds drive assists
// (cruise, approach) and shows cargo, fuel and delta-v status.
// See README.md in the repository for setup and commands.

// ---- Program.cs ----
const int BlockRefreshTicks = 600;          // rescan blocks every 10 s
const int DisplayTicks = 10;
const int DisplayTickOffset = 5;            // keeps display updates off the status update tick
const double DrawBudget = 0.6;              // share of the instruction limit screens may use
const double TicksPerSecond = 60.0;

int _ticks;
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
    Runtime.UpdateFrequency = UpdateFrequency.Update1;
}

public void Save()
{
    SaveState();
}

public void Main(string argument, UpdateType updateSource)
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

    UpdateScan();
    ControlThrust();

    SampleFuelUse(1 / TicksPerSecond);
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
    GridTerminalSystem.GetBlocksOfType(_cameras, c => c.IsSameConstructAs(Me));
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
            GoToSelected();
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

// ---- Config.cs ----
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

// ---- Displays.cs ----
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

// ---- DriveAssists.cs ----
const double ArrivalDistance = 2.0;         // m - approach is finished within this distance...
const double ArrivalSpeed = 0.3;            // m/s - ...and below this speed

enum Mode { Manual, Cruise, Approach }
enum ScanPurpose { Approach, Mark }

readonly List<IMyCameraBlock> _cameras = new List<IMyCameraBlock>();
IMyCameraBlock _camera;
Mode _mode = Mode.Manual;
bool _scanPending;
ScanPurpose _scanPurpose;
Vector3D _approachTarget;
string _targetName = "";
double _targetDistance;

// Returns true when a drive assist wants a target velocity. Movement
// input that conflicts with the assist cancels it.
bool UpdateDriveAssist(MatrixD matrix, Vector3D velocity, Vector3 move,
    out Vector3D targetVelocity, out double maxAccel)
{
    targetVelocity = Vector3D.Zero;
    maxAccel = _limit;

    if (_mode == Mode.Cruise)
    {
        if (Math.Abs(move.Z) > InputDeadzone)
        {
            _mode = Mode.Manual;
            _message = "Cruise cancelled";
            return false;
        }
        targetVelocity = matrix.Forward * _cruiseSpeed;
        return true;
    }

    if (_mode == Mode.Approach)
    {
        if (move.LengthSquared() > InputDeadzone * InputDeadzone)
        {
            _mode = Mode.Manual;
            _message = "Approach cancelled";
            return false;
        }
        if (_approachFullThrust)
            maxAccel = double.MaxValue;
        return ApproachVelocity(velocity, out targetVelocity);
    }

    return false;
}

// -----------------------------------------------------------------
//  Cruise
// -----------------------------------------------------------------

// cruise           toggle cruise at the current cruise speed
// cruise on|off    enable / disable
// cruise up|down   change the cruise speed by CruiseStep
// cruise <m/s>     set the cruise speed and enable
void HandleCruiseCommand(string value)
{
    double speed;
    if (value == null)
        SetCruise(_mode != Mode.Cruise);
    else if (value == "on")
        SetCruise(true);
    else if (value == "off")
        SetCruise(false);
    else if (value == "up" || value == "+")
        _cruiseSpeed = Math.Min(_cruiseSpeed + _cruiseStep, _maxSpeed);
    else if (value == "down" || value == "-")
        _cruiseSpeed = Math.Max(_cruiseSpeed - _cruiseStep, _cruiseStep);
    else if (TryParseNumber(value, out speed) && speed > 0)
    {
        _cruiseSpeed = Math.Min(speed, _maxSpeed);
        SetCruise(true);
    }
    else
        _message = "Usage: cruise [on|off|up|down|<m/s>]";
}

void SetCruise(bool active)
{
    if (active)
    {
        _enabled = true;
        _mode = Mode.Cruise;
    }
    else if (_mode == Mode.Cruise)
        _mode = Mode.Manual;
}

// -----------------------------------------------------------------
//  Approach
// -----------------------------------------------------------------

void StartScan(ScanPurpose purpose)
{
    _camera = FindCamera();
    if (_camera == null)
    {
        _message = "No camera facing forward (or tagged " + _cameraTag + ")";
        return;
    }
    _camera.EnableRaycast = true;
    _scanPending = true;
    _scanPurpose = purpose;
}

// Called every tick while a scan is pending: fires the raycast as soon
// as the camera has charged enough range.
void UpdateScan()
{
    if (!_scanPending)
        return;
    if (_camera == null || !_camera.IsWorking)
    {
        _message = "Camera not working";
        _scanPending = false;
        return;
    }
    if (!_camera.CanScan(_scanRange))
        return;

    _scanPending = false;
    MyDetectedEntityInfo hit = _camera.Raycast(_scanRange);
    if (hit.IsEmpty() || !hit.HitPosition.HasValue)
    {
        _message = "Nothing found within " + FormatDistance(_scanRange);
        return;
    }

    RegisterObstacle(hit);
    Vector3D hitPos = hit.HitPosition.Value;
    if (_scanPurpose == ScanPurpose.Mark)
    {
        AddDeposit(_pendingMarkOre, hitPos, false);
        return;
    }

    string name = hit.Type == MyDetectedEntityType.Asteroid ? "Asteroid"
        : hit.Type == MyDetectedEntityType.Planet ? "Planet"
        : hit.Name;
    if (StartApproach(hitPos, name))
        _message = name + " at " + FormatDistance(Vector3D.Distance(hitPos, ReferencePosition()));
}

// Charge of the scan camera relative to the configured scan range (0..1).
double ScanCharge()
{
    return _camera != null ? Math.Min(_camera.AvailableScanRange / _scanRange, 1) : 0;
}

// Flies to a point ApproachBuffer meters before the given surface point.
bool StartApproach(Vector3D surfacePoint, string name)
{
    Vector3D ray = surfacePoint - ReferencePosition();
    double distance = ray.Length();
    if (distance <= _approachBuffer)
    {
        _message = "Target is closer than " + FormatDistance(_approachBuffer);
        return false;
    }
    _approachTarget = surfacePoint - ray / distance * _approachBuffer;
    _targetName = name;
    _enabled = true;
    _mode = Mode.Approach;
    return true;
}

// Point of the ship used for distances: the scan camera, else the cockpit.
Vector3D ReferencePosition()
{
    if (_camera != null && _camera.IsFunctional)
        return _camera.GetPosition();
    IMyShipController reference = _controller ?? _layoutController;
    return reference != null ? reference.GetPosition() : Me.GetPosition();
}

IMyCameraBlock FindCamera()
{
    IMyShipController reference = _controller ?? _layoutController;
    IMyCameraBlock facing = null;
    foreach (IMyCameraBlock c in _cameras)
    {
        if (!c.IsFunctional)
            continue;
        if (c.CustomName.Contains(_cameraTag))
            return c;
        if (facing == null && reference != null
            && Vector3D.Dot(c.WorldMatrix.Forward, reference.WorldMatrix.Forward) > 0.99)
            facing = c;
    }
    return facing;
}

// Velocity that brings the ship to the approach target and still lets it
// stop in time: v = sqrt(2 * a_brake * distance), capped at MaxSpeed.
bool ApproachVelocity(Vector3D velocity, out Vector3D targetVelocity)
{
    targetVelocity = Vector3D.Zero;
    Vector3D toTarget = _approachTarget - ReferencePosition();
    _targetDistance = toTarget.Length();

    if (_targetDistance < ArrivalDistance && velocity.Length() < ArrivalSpeed)
    {
        _mode = Mode.Manual;
        _message = "Arrived";
        return false;
    }

    Vector3D direction = toTarget / Math.Max(_targetDistance, 1e-3);
    double brake = MaxAccelAlong(-direction) * _brakeSafety;
    if (!_approachFullThrust)
        brake = Math.Min(brake, _limit * _brakeSafety);

    double speed = Math.Min(_maxSpeed, Math.Sqrt(2 * brake * _targetDistance));
    // Close in: approach proportionally so the ship settles instead of oscillating.
    speed = Math.Min(speed, _targetDistance * _velocityGain * 0.5);
    targetVelocity = direction * speed;
    return true;
}

// ---- MapDisplay.cs ----
const float RadarRadius = 220;      // units for the full zoom range
const float PlaneTilt = 0.52f;      // vertical squash of the radar plane
const float HeightScale = 0.85f;    // height stems relative to plane scale
const float RowHeight = 22;
const int CircleSegments = 48;

static readonly Color BgColor = new Color(8, 18, 24);
static readonly Color PanelColor = new Color(14, 32, 42);
static readonly Color GridColor = new Color(32, 78, 96);
static readonly Color GridFaint = new Color(22, 52, 64);
static readonly Color Cyan = new Color(70, 205, 235);
static readonly Color TextColor = new Color(215, 240, 250);
static readonly Color DimColor = new Color(120, 160, 175);
static readonly Color RouteColor = new Color(255, 190, 60);
static readonly Color WarnColor = new Color(255, 110, 80);
static readonly Color GravityColor = new Color(150, 90, 255);
static readonly Color RockColor = new Color(70, 82, 90);

readonly StringBuilder _measure = new StringBuilder();
readonly List<MapItem> _mapItems = new List<MapItem>();

MySpriteDrawFrame _frame;
IMyTextSurface _surface;
Vector2 _origin;
float _u;                  // pixels per layout unit
bool _frameToggle;

// A deposit or asteroid prepared for depth-sorted drawing.
struct MapItem
{
    public Deposit Deposit;
    public Obstacle Rock;
    public Vector3D Local;  // x = right, y = up, z = forward (m)
}

void DrawMapSurface(IMyTextSurface surface, MapView view)
{
    surface.ContentType = ContentType.SCRIPT;
    surface.Script = "";
    surface.ScriptBackgroundColor = BgColor;

    RectangleF viewport = new RectangleF((surface.TextureSize - surface.SurfaceSize) / 2f, surface.SurfaceSize);
    _surface = surface;
    using (MySpriteDrawFrame frame = surface.DrawFrame())
    {
        _frame = frame;
        // Sprites are cached by the game; changing the list forces a redraw.
        if (_frameToggle)
            frame.Add(new MySprite());

        float width = viewport.Width, height = viewport.Height;
        bool active = view == _view;
        if (view == MapView.Radar)
        {
            _u = Math.Min(width, height) / 512f;
            _origin = viewport.Position + new Vector2((width - 512 * _u) / 2, (height - 512 * _u) / 2);
            DrawRadar();
            DrawButtons(RadarButtons, 462, active);
            if (active)
                DrawDialog(512);
        }
        else
        {
            _u = width / 512f;
            _origin = viewport.Position;
            float h = height / _u;
            DrawList(h);
            DrawButtons(ListButtons, h - 46, active);
            if (active)
                DrawDialog(h);
        }
    }
}

// -----------------------------------------------------------------
//  Radar view
// -----------------------------------------------------------------

void DrawRadar()
{
    IMyShipController reference = _controller ?? _layoutController;
    float cx = 256, cy = 198;
    float scale = RadarRadius / (float)MapRange;       // units per meter
    MatrixD ship = reference != null ? reference.WorldMatrix : Me.WorldMatrix;
    Vector3D shipPos = ReferencePosition();
    bool inGravity = false;

    // Gravity wells: where the gravity sphere cuts the ship's plane.
    foreach (Obstacle o in _obstacles)
    {
        if (!o.Planet || o.GravityRadius <= 0)
            continue;
        Vector3D c = ToLocal(o.Center, shipPos, ship);
        if (c.Length() < o.GravityRadius)
            inGravity = true;
        if (Math.Abs(c.Y) >= o.GravityRadius)
            continue;
        double r = Math.Sqrt(o.GravityRadius * o.GravityRadius - c.Y * c.Y);
        Vector2 center = PlanePoint(c, cx, cy, scale);
        float rx = (float)(r * scale), ry = rx * PlaneTilt;
        if (rx > 20000)
            continue;
        Ellipse(center.X, center.Y, rx, ry, GravityColor * 0.15f, false);
        EllipseOutline(center.X, center.Y, rx, ry, 2, GravityColor * 0.8f, true);

        // Label at the edge of the well that is closest to the ship.
        Vector2 toShip = new Vector2(cx, cy) - center;
        if (!inGravity && toShip.Length() > 1)
        {
            toShip = new Vector2(toShip.X / rx, toShip.Y / ry);
            toShip.Normalize();
            Vector2 edge = center + new Vector2(toShip.X * rx, toShip.Y * ry);
            if (Math.Abs(edge.X - cx) < RadarRadius && Math.Abs(edge.Y - cy) < RadarRadius * PlaneTilt + 20)
            {
                Text("GRAVITY WELL", edge.X, edge.Y + 4, 0.42f, GravityColor, TextAlignment.CENTER);
                Text("Planet " + FormatDistance(Vector3D.Distance(o.Center, shipPos) - o.Radius), edge.X, edge.Y + 18,
                    0.36f, GravityColor, TextAlignment.CENTER, "Monospace");
            }
        }
    }

    // Range rings and axes
    EllipseOutline(cx, cy, RadarRadius, RadarRadius * PlaneTilt, 1.5f, GridColor, false);
    EllipseOutline(cx, cy, RadarRadius / 2, RadarRadius / 2 * PlaneTilt, 1, GridFaint, false);
    EllipseOutline(cx, cy, RadarRadius / 4, RadarRadius / 4 * PlaneTilt, 1, GridFaint, false);
    Line(cx - RadarRadius, cy, cx + RadarRadius, cy, 1, GridFaint);
    Line(cx, cy - RadarRadius * PlaneTilt, cx, cy + RadarRadius * PlaneTilt, 1, GridFaint);
    Text("FORWARD", cx, cy - RadarRadius * PlaneTilt - 16, 0.36f, DimColor, TextAlignment.CENTER, "Monospace");
    Text(FormatDistance(MapRange), cx + RadarRadius * 0.72f, cy - RadarRadius * PlaneTilt * 0.72f - 14, 0.36f, DimColor, TextAlignment.LEFT, "Monospace");
    Text(FormatDistance(MapRange / 2), cx + RadarRadius * 0.36f, cy - RadarRadius * PlaneTilt * 0.36f - 12, 0.32f, DimColor, TextAlignment.LEFT, "Monospace");

    // Asteroids and deposits, far ones first
    _mapItems.Clear();
    foreach (Obstacle o in _obstacles)
        if (!o.Planet)
            _mapItems.Add(new MapItem { Rock = o, Local = ToLocal(o.Center, shipPos, ship) });
    foreach (Deposit d in _visibleDeposits)
        _mapItems.Add(new MapItem { Deposit = d, Local = ToLocal(d.Position, shipPos, ship) });
    _mapItems.Sort((a, b) => b.Local.Z.CompareTo(a.Local.Z));

    // Current approach as a dashed line
    if (_mode == Mode.Approach)
    {
        Vector2 target = ProjectedPoint(ToLocal(_approachTarget, shipPos, ship), cx, cy, scale);
        Dashed(cx, cy, target.X, target.Y, 2.5f, RouteColor);
        DiamondOutline(target.X, target.Y, 6, RouteColor);
    }

    foreach (MapItem item in _mapItems)
    {
        if (item.Rock != null)
            DrawRock(item, cx, cy, scale);
        else
            DrawDeposit(item, cx, cy, scale);
    }

    // Ship
    _frame.Add(new MySprite(SpriteType.TEXTURE, "Triangle", P(cx, cy), new Vector2(14, 18) * _u, Cyan));

    // Header and info panel are drawn last so they cover anything that
    // sticks out of the radar area.
    Rect(0, 0, 512, 32, PanelColor);
    Text("ORE MAP", 12, 4, 0.75f, Cyan);
    string header = "Range " + FormatDistance(MapRange) + "   Filter: " + (_filter ?? "all");
    if (inGravity)
        header = "IN GRAVITY   " + header;
    Text(header, 500, 9, 0.42f, inGravity ? GravityColor : DimColor, TextAlignment.RIGHT, "Monospace");

    Rect(0, 336, 512, 176, BgColor);
    DrawSelectionPanel(8, 342, 496);
}

void DrawRock(MapItem item, float cx, float cy, float scale)
{
    Vector3D p = item.Local;
    if (new Vector2D(p.X, p.Z).Length() > MapRange * 1.1)
        return;
    Vector2 foot = PlanePoint(p, cx, cy, scale);
    Vector2 pos = ProjectedPoint(p, cx, cy, scale);
    float r = Math.Max((float)(item.Rock.Radius * scale) * 0.8f, 3);
    Line(foot.X, foot.Y, pos.X, pos.Y, 1, DimColor * 0.5f);
    Ellipse(foot.X, foot.Y, r * 0.6f, r * 0.6f * PlaneTilt, RockColor * 0.4f, false);
    Ellipse(pos.X, pos.Y, r, r, RockColor * 0.85f, false);
    Ellipse(pos.X - r * 0.22f, pos.Y - r * 0.3f, r * 0.35f, r * 0.3f, new Color(110, 125, 135) * 0.5f, false);
}

void DrawDeposit(MapItem item, float cx, float cy, float scale)
{
    Deposit d = item.Deposit;
    Vector3D p = item.Local;
    bool selected = d == _selected;
    Color color = OreColor(d.Ore);

    double horizontal = new Vector2D(p.X, p.Z).Length();
    bool outside = horizontal > MapRange;
    if (outside)
        p = new Vector3D(p.X * MapRange / horizontal, p.Y * MapRange / horizontal, p.Z * MapRange / horizontal);

    Vector2 foot = PlanePoint(p, cx, cy, scale);
    Vector2 pos = ProjectedPoint(p, cx, cy, scale);
    if (outside)
    {
        // Beyond the range: small marker on the edge, pointing outwards.
        Diamond(foot.X, foot.Y, 4, color * 0.6f);
        if (!selected)
            return;
        pos = foot;
    }
    else
    {
        Line(foot.X, foot.Y, pos.X, pos.Y, 1.5f, color * 0.85f);
        Ellipse(foot.X, foot.Y, 2.5f, 1.3f, color * 0.8f, false);
        Diamond(pos.X, pos.Y, 5, color);
    }

    string name = ShortOre(d.Ore) + d.Number;
    string distance = FormatDistance(d.Distance);
    float tx = pos.X + 8, ty = pos.Y - 11;
    if (selected)
    {
        float w = Math.Max(MeasureText(name, 0.42f, "White"), MeasureText(distance, 0.36f, "Monospace"));
        Rect(tx - 3, ty - 2, w + 6, 25, new Color(40, 30, 10) * 0.9f);
        Box(tx - 3, ty - 2, w + 6, 25, 1, RouteColor);
    }
    Text(name, tx, ty, 0.42f, selected ? RouteColor : color);
    Text(distance, tx, ty + 11, 0.36f, selected ? RouteColor : DimColor, TextAlignment.LEFT, "Monospace");
}

// Info about the selected deposit and the current flight.
void DrawSelectionPanel(float x, float y, float width)
{
    Rect(x, y, width, 96, PanelColor);
    Box(x, y, width, 96, 1, GridColor);
    float left = x + 10, right = x + width - 10;

    if (_selected == null)
    {
        Text(_deposits.Count == 0 ? "No deposits mapped yet" : "No deposit matches the filter", left, y + 8, 0.5f, DimColor);
        Text("MARK: aim at ore and scan", left, y + 32, 0.38f, DimColor, TextAlignment.LEFT, "Monospace");
    }
    else
    {
        Text(_selected.Label, left, y + 6, 0.55f, RouteColor);
        Text(FormatDistance(_selected.Distance), right, y + 8, 0.45f, TextColor, TextAlignment.RIGHT, "Monospace");
        Text(DirectionText(_selected.Position) + "   " + (_selected.Mined ? "logged while mining" : "marked by scan"),
            left, y + 30, 0.36f, DimColor, TextAlignment.LEFT, "Monospace");

        string flight;
        Color flightColor = TextColor;
        if (_mode == Mode.Approach)
            flight = "Flying to " + _targetName + ": " + FormatDistance(_targetDistance) + "  " + _currentSpeed.ToString("0") + " m/s";
        else
        {
            Obstacle blocking = FirstObstacleOnPath(ReferencePosition(), _selected.Position);
            flight = blocking == null ? "Direct path clear" : "Direct path blocked by " + (blocking.Planet ? "planet" : "asteroid");
            flightColor = blocking == null ? TextColor : WarnColor;
        }
        Text(flight, left, y + 48, 0.38f, flightColor, TextAlignment.LEFT, "Monospace");

        double total = _deltaVHydrogen + _deltaVElectric;
        if (total > 0)
        {
            Text("dv", left, y + 68, 0.4f, TextColor, TextAlignment.LEFT, "Monospace");
            float barX = left + 24, barW = width - 200;
            Box(barX, y + 71, barW, 12, 1, GridColor);
            Rect(barX + 1, y + 72, (float)(barW - 2) * (float)Math.Min(TripDeltaV() / total, 1), 10, RouteColor);
            Text(string.Format("{0:0} / {1:0} m/s", TripDeltaV(), total), right, y + 68, 0.38f, TextColor, TextAlignment.RIGHT, "Monospace");
        }
    }

    if (_message.Length > 0)
        Text(_message, left, y + 100, 0.36f, DimColor, TextAlignment.LEFT, "Monospace");
}

// -----------------------------------------------------------------
//  List view
// -----------------------------------------------------------------

void DrawList(float height)
{
    Rect(0, 0, 512, 30, PanelColor);
    Text("ORE MAP", 10, 3, 0.72f, Cyan);
    Text(_visibleDeposits.Count + " deposits   Filter: " + (_filter ?? "all"), 502, 8, 0.4f, DimColor, TextAlignment.RIGHT, "Monospace");

    float y = 36;
    Text("ORE", 34, y, 0.38f, DimColor, TextAlignment.LEFT, "Monospace");
    Text("#", 150, y, 0.38f, DimColor, TextAlignment.LEFT, "Monospace");
    Text("DIST", 250, y, 0.38f, DimColor, TextAlignment.RIGHT, "Monospace");
    Text("DIRECTION", 268, y, 0.38f, DimColor, TextAlignment.LEFT, "Monospace");
    y += 20;

    int rows = Math.Max(1, (int)((height - y - 86) / RowHeight));
    int selectedIndex = _selected != null ? _visibleDeposits.IndexOf(_selected) : 0;
    int first = Math.Max(0, Math.Min(selectedIndex - rows / 2, _visibleDeposits.Count - rows));

    IMyShipController reference = _controller ?? _layoutController;
    MatrixD ship = reference != null ? reference.WorldMatrix : Me.WorldMatrix;
    Vector3D shipPos = ReferencePosition();

    for (int i = first; i < _visibleDeposits.Count && i < first + rows; i++)
    {
        Deposit d = _visibleDeposits[i];
        bool selected = d == _selected;
        Color color = OreColor(d.Ore);
        if (selected)
        {
            Rect(6, y - 2, 500, RowHeight - 2, new Color(60, 45, 12));
            Box(6, y - 2, 500, RowHeight - 2, 1, RouteColor);
            Text(">", 12, y, 0.45f, RouteColor, TextAlignment.LEFT, "Monospace");
        }
        Diamond(26, y + 8, 4.5f, color);
        Text(d.Ore, 34, y, 0.45f, selected ? RouteColor : TextColor);
        Text(d.Number.ToString(), 150, y + 1, 0.42f, TextColor, TextAlignment.LEFT, "Monospace");
        Text(FormatDistance(d.Distance), 250, y + 1, 0.42f, TextColor, TextAlignment.RIGHT, "Monospace");

        // Direction indicator: where the deposit is relative to the nose.
        Vector3D local = ToLocal(d.Position, shipPos, ship);
        double yaw = Math.Atan2(local.X, local.Z), pitch = Math.Atan2(local.Y, new Vector2D(local.X, local.Z).Length());
        bool behind = Math.Abs(yaw) > Math.PI / 2;
        float ix = 276, iy = y + 8;
        EllipseOutline(ix, iy, 7, 7, 1, GridColor, false);
        float dx = (float)(Math.Sign(yaw) * Math.Min(Math.Abs(yaw), Math.PI / 2) / (Math.PI / 2) * 6);
        float dy = (float)(-pitch / (Math.PI / 2) * 6);
        Ellipse(ix + dx, iy + dy, 2, 2, behind ? WarnColor : color, false);
        Text(DirectionText(d.Position), 290, y + 1, 0.42f, TextColor, TextAlignment.LEFT, "Monospace");

        y += RowHeight;
    }

    if (_visibleDeposits.Count == 0)
        Text("No deposits yet. MARK: aim at ore and scan.", 34, y, 0.42f, DimColor, TextAlignment.LEFT, "Monospace");

    // Summary of the selection
    float fy = height - 80;
    Line(6, fy, 506, fy, 1, GridColor);
    if (_selected != null)
    {
        Text(_selected.Label + "  " + FormatDistance(_selected.Distance), 10, fy + 6, 0.48f, RouteColor);
        double total = _deltaVHydrogen + _deltaVElectric;
        if (total > 0)
            Text(string.Format("dv {0:0} / {1:0} m/s", TripDeltaV(), total), 502, fy + 8, 0.4f, TextColor, TextAlignment.RIGHT, "Monospace");
    }
    else if (_message.Length > 0)
        Text(_message, 10, fy + 8, 0.38f, DimColor, TextAlignment.LEFT, "Monospace");
}

// -----------------------------------------------------------------
//  Buttons and dialogs
// -----------------------------------------------------------------

void DrawButtons(string[] buttons, float y, bool active)
{
    float gap = 5, width = (496 - gap * (buttons.Length - 1)) / buttons.Length;
    for (int i = 0; i < buttons.Length; i++)
    {
        float x = 8 + i * (width + gap);
        bool highlighted = active && i == _button && _dialog == Dialog.None;
        Rect(x, y, width, 40, highlighted ? Cyan : PanelColor);
        Box(x, y, width, 40, 1, active ? Cyan : GridColor);
        Text(buttons[i], x + width / 2, y + 11, 0.5f, highlighted ? BgColor : active ? Cyan : DimColor, TextAlignment.CENTER);
    }
}

void DrawDialog(float height)
{
    if (_dialog == Dialog.None)
        return;
    float x = 96, w = 320, y = 44;
    if (_dialog == Dialog.ConfirmDelete)
    {
        float h = 90;
        y = (height - h) / 2 - 20;
        Rect(x, y, w, h, PanelColor);
        Box(x, y, w, h, 2, WarnColor);
        Text("Delete " + (_selected != null ? _selected.Label : "") + "?", x + w / 2, y + 12, 0.6f, WarnColor, TextAlignment.CENTER);
        Text("OK = delete   BACK = cancel", x + w / 2, y + 52, 0.4f, TextColor, TextAlignment.CENTER, "Monospace");
        return;
    }

    // Ore picker for MARK
    int rows = Math.Max(3, Math.Min(_pickerOres.Count, (int)((height - y - 120) / RowHeight)));
    float boxH = 60 + rows * RowHeight;
    Rect(x, y, w, boxH, PanelColor);
    Box(x, y, w, boxH, 2, Cyan);
    Text("MARK ORE", x + 10, y + 6, 0.55f, Cyan);
    Text("aim at the ore, then OK", x + w - 10, y + 12, 0.34f, DimColor, TextAlignment.RIGHT, "Monospace");
    int first = Math.Max(0, Math.Min(_pickerIndex - rows / 2, _pickerOres.Count - rows));
    float ry = y + 34;
    for (int i = first; i < _pickerOres.Count && i < first + rows; i++)
    {
        bool selected = i == _pickerIndex;
        if (selected)
            Rect(x + 6, ry - 2, w - 12, RowHeight - 2, Cyan);
        Diamond(x + 20, ry + 8, 4.5f, OreColor(_pickerOres[i]));
        Text(_pickerOres[i], x + 32, ry, 0.45f, selected ? BgColor : TextColor);
        ry += RowHeight;
    }
    Text("UP/DOWN select   OK scan   BACK cancel", x + w / 2, y + boxH - 22, 0.34f, DimColor, TextAlignment.CENTER, "Monospace");
}

// -----------------------------------------------------------------
//  Geometry
// -----------------------------------------------------------------

// World position relative to the ship: x = right, y = up, z = forward.
static Vector3D ToLocal(Vector3D world, Vector3D origin, MatrixD ship)
{
    Vector3D v = world - origin;
    return new Vector3D(Vector3D.Dot(v, ship.Right), Vector3D.Dot(v, ship.Up), Vector3D.Dot(v, ship.Forward));
}

static Vector2 PlanePoint(Vector3D local, float cx, float cy, float scale)
{
    return new Vector2(cx + (float)local.X * scale, cy - (float)local.Z * scale * PlaneTilt);
}

static Vector2 ProjectedPoint(Vector3D local, float cx, float cy, float scale)
{
    float height = MathHelper.Clamp((float)local.Y * scale * HeightScale, -140, 140);
    Vector2 plane = PlanePoint(local, cx, cy, scale);
    return new Vector2(plane.X, plane.Y - height);
}

// "27° R  13° U" (+ "behind") relative to the ship's nose.
string DirectionText(Vector3D world)
{
    IMyShipController reference = _controller ?? _layoutController;
    MatrixD ship = reference != null ? reference.WorldMatrix : Me.WorldMatrix;
    Vector3D local = ToLocal(world, ReferencePosition(), ship);
    double yaw = Math.Atan2(local.X, local.Z) * 180 / Math.PI;
    double pitch = Math.Atan2(local.Y, new Vector2D(local.X, local.Z).Length()) * 180 / Math.PI;
    return string.Format("{0,3:0}° {1} {2,2:0}° {3}{4}", Math.Abs(yaw), yaw >= 0 ? "R" : "L",
        Math.Abs(pitch), pitch >= 0 ? "U" : "D", Math.Abs(yaw) > 90 ? " behind" : "");
}

static Color OreColor(string ore)
{
    switch (ore)
    {
        case "Iron": return new Color(225, 140, 95);
        case "Nickel": return new Color(130, 215, 130);
        case "Cobalt": return new Color(90, 140, 255);
        case "Magnesium": return new Color(255, 130, 200);
        case "Silicon": return new Color(170, 130, 255);
        case "Silver": return new Color(190, 205, 225);
        case "Gold": return new Color(255, 210, 75);
        case "Platinum": return new Color(235, 235, 245);
        case "Uranium": return new Color(160, 255, 60);
        case "Ice": return new Color(150, 230, 255);
        case "Stone": return new Color(150, 150, 150);
    }
    int hash = ore.GetHashCode();
    return new Color(128 + (hash & 127), 128 + ((hash >> 8) & 127), 128 + ((hash >> 16) & 127));
}

static string ShortOre(string ore)
{
    switch (ore)
    {
        case "Iron": return "Fe";
        case "Nickel": return "Ni";
        case "Cobalt": return "Co";
        case "Magnesium": return "Mg";
        case "Silicon": return "Si";
        case "Silver": return "Ag";
        case "Gold": return "Au";
        case "Platinum": return "Pt";
        case "Uranium": return "U";
    }
    return ore.Length > 3 ? ore.Substring(0, 3) : ore;
}

// -----------------------------------------------------------------
//  Sprite helpers (coordinates in layout units)
// -----------------------------------------------------------------

Vector2 P(float x, float y)
{
    return _origin + new Vector2(x, y) * _u;
}

void Rect(float x, float y, float w, float h, Color color)
{
    _frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", P(x + w / 2, y + h / 2), new Vector2(w, h) * _u, color));
}

void Box(float x, float y, float w, float h, float t, Color color)
{
    Rect(x, y, w, t, color);
    Rect(x, y + h - t, w, t, color);
    Rect(x, y, t, h, color);
    Rect(x + w - t, y, t, h, color);
}

void Line(float x1, float y1, float x2, float y2, float thickness, Color color)
{
    Vector2 d = new Vector2(x2 - x1, y2 - y1);
    float length = d.Length();
    if (length < 0.01f)
        return;
    _frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", P((x1 + x2) / 2, (y1 + y2) / 2),
        new Vector2(length, thickness) * _u, color, null, TextAlignment.CENTER, (float)Math.Atan2(d.Y, d.X)));
}

void Dashed(float x1, float y1, float x2, float y2, float thickness, Color color)
{
    float length = new Vector2(x2 - x1, y2 - y1).Length();
    int dashes = Math.Max(1, (int)(length / 12));
    for (int i = 0; i < dashes; i++)
    {
        float t0 = (float)i / dashes, t1 = t0 + 0.5f / dashes;
        Line(x1 + (x2 - x1) * t0, y1 + (y2 - y1) * t0, x1 + (x2 - x1) * t1, y1 + (y2 - y1) * t1, thickness, color);
    }
}

void Ellipse(float x, float y, float rx, float ry, Color color, bool hollow)
{
    _frame.Add(new MySprite(SpriteType.TEXTURE, hollow ? "CircleHollow" : "Circle", P(x, y), new Vector2(rx * 2, ry * 2) * _u, color));
}

// Outline made of line segments, so the thickness stays constant.
// Segments far outside the screen are skipped.
void EllipseOutline(float x, float y, float rx, float ry, float thickness, Color color, bool dashed)
{
    float px = x + rx, py = y;
    for (int i = 1; i <= CircleSegments; i++)
    {
        double a = Math.PI * 2 * i / CircleSegments;
        float nx = x + rx * (float)Math.Cos(a), ny = y + ry * (float)Math.Sin(a);
        bool visible = (px > -50 && px < 562 && py > -50 && py < 562) || (nx > -50 && nx < 562 && ny > -50 && ny < 562);
        if (visible && (!dashed || i % 2 == 0))
            Line(px, py, nx, ny, thickness, color);
        px = nx;
        py = ny;
    }
}

void Diamond(float x, float y, float r, Color color)
{
    _frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", P(x, y), new Vector2(r * 1.414f) * _u, color,
        null, TextAlignment.CENTER, MathHelper.PiOver4));
}

void DiamondOutline(float x, float y, float r, Color color)
{
    Line(x, y - r, x + r, y, 1.5f, color);
    Line(x + r, y, x, y + r, 1.5f, color);
    Line(x, y + r, x - r, y, 1.5f, color);
    Line(x - r, y, x, y - r, 1.5f, color);
}

void Text(string text, float x, float y, float scale, Color color,
    TextAlignment alignment = TextAlignment.LEFT, string font = "White")
{
    MySprite sprite = MySprite.CreateText(text, font, color, scale * _u, alignment);
    sprite.Position = P(x, y);
    _frame.Add(sprite);
}

float MeasureText(string text, float scale, string font)
{
    _measure.Clear().Append(text);
    return _surface.MeasureStringInPixels(_measure, font, scale * _u).X / _u;
}

// ---- Menu.cs ----
enum MapView { Radar, List }
enum Dialog { None, OrePicker, ConfirmDelete }

static readonly string[] RadarButtons = { "LIST", "ROUTE", "GO", "ZOOM", "FILTER" };
static readonly string[] ListButtons = { "MAP", "MARK", "ROUTE", "GO", "DELETE" };
static readonly double[] ZoomLevels = { 1000, 2000, 5000, 10000, 20000, 50000, 100000, 200000 };

readonly List<string> _pickerOres = new List<string>();

MapView _view = MapView.Radar;
Dialog _dialog = Dialog.None;
int _button;
int _pickerIndex;
int _zoomIndex = 4;

string[] CurrentButtons { get { return _view == MapView.Radar ? RadarButtons : ListButtons; } }
double MapRange { get { return ZoomLevels[_zoomIndex]; } }

void HandleUiCommand(string value)
{
    string[] buttons = CurrentButtons;
    switch (value)
    {
        case "left":
            if (_dialog == Dialog.None)
                _button = (_button + buttons.Length - 1) % buttons.Length;
            break;
        case "right":
            if (_dialog == Dialog.None)
                _button = (_button + 1) % buttons.Length;
            break;
        case "up":
        case "down":
            int step = value == "up" ? -1 : 1;
            if (_dialog == Dialog.OrePicker)
                _pickerIndex = (_pickerIndex + step + _pickerOres.Count) % _pickerOres.Count;
            else if (_dialog == Dialog.None)
                MoveSelection(step);
            break;
        case "ok":
            Confirm();
            break;
        case "back":
            _dialog = Dialog.None;
            break;
        default:
            _message = "Usage: ui left|right|up|down|ok|back";
            break;
    }
}

void Confirm()
{
    if (_dialog == Dialog.OrePicker)
    {
        _dialog = Dialog.None;
        MarkByScan(_pickerOres[_pickerIndex]);
        return;
    }
    if (_dialog == Dialog.ConfirmDelete)
    {
        _dialog = Dialog.None;
        DeleteSelected();
        return;
    }
    PressButton(CurrentButtons[_button]);
}

void PressButton(string button)
{
    switch (button)
    {
        case "LIST":
            SwitchView(MapView.List);
            break;
        case "MAP":
            SwitchView(MapView.Radar);
            break;
        case "MARK":
            OpenOrePicker();
            break;
        case "ROUTE":
            _message = "Route planning follows in the next update";
            break;
        case "GO":
            GoToSelected();
            break;
        case "ZOOM":
            _zoomIndex = (_zoomIndex + 1) % ZoomLevels.Length;
            break;
        case "FILTER":
            CycleFilter();
            break;
        case "DELETE":
            if (_selected != null)
                _dialog = Dialog.ConfirmDelete;
            break;
    }
}

// Keeps the highlight on the same button when it exists in both views.
void SwitchView(MapView view)
{
    string current = CurrentButtons[_button];
    _view = view;
    int index = Array.IndexOf(CurrentButtons, current);
    _button = index >= 0 ? index : 0;
}

void OpenOrePicker()
{
    _pickerOres.Clear();
    _pickerOres.AddRange(StandardOres);
    foreach (string ore in _oreAmounts.Keys)
        if (!_pickerOres.Contains(ore))
            _pickerOres.Add(ore);
    _pickerIndex = Math.Min(_pickerIndex, _pickerOres.Count - 1);
    _dialog = Dialog.OrePicker;
}

// zoom in | zoom out
void HandleZoomCommand(string value)
{
    if (value == "in")
        _zoomIndex = Math.Max(_zoomIndex - 1, 0);
    else if (value == "out")
        _zoomIndex = Math.Min(_zoomIndex + 1, ZoomLevels.Length - 1);
    else
        _message = "Usage: zoom in|out";
}

// ---- OreMap.cs ----
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

// ---- ShipStatus.cs ----
const int StatusTicks = 60;                 // full status update once per second
const double RateSmoothing = 30;            // s - averaging window for consumption rates
const double CalibrationLiters = 2000;      // L of hydrogen per calibration step
const double CalibrationUranium = 0.05;     // kg of uranium per calibration step
const string OreType = "MyObjectBuilder_Ore";

readonly List<IMyTerminalBlock> _cargoBlocks = new List<IMyTerminalBlock>();
readonly List<IMyBatteryBlock> _batteries = new List<IMyBatteryBlock>();
readonly List<IMyReactor> _reactors = new List<IMyReactor>();
readonly List<IMyGasTank> _hydrogenTanks = new List<IMyGasTank>();
readonly List<IMyJumpDrive> _jumpDrives = new List<IMyJumpDrive>();
readonly List<IMyThrust> _hydrogenThrusters = new List<IMyThrust>();
readonly List<IMyThrust> _electricThrusters = new List<IMyThrust>();
readonly List<MyInventoryItem> _itemBuffer = new List<MyInventoryItem>();
readonly Dictionary<string, double> _oreAmounts = new Dictionary<string, double>();

// Latest readings
double _cargoVolume, _cargoMaxVolume, _cargoMass;
double _batteryStored, _batteryMax, _batteryNetOutput;
double _uranium, _reactorOutput;
double _hydrogen, _hydrogenCapacity;
double _jumpStored, _jumpMax;
double _electricThrustPerMW;
double _deltaVHydrogen, _deltaVElectric;

// Consumption rates (per second, -1 = unknown yet)
double _hydrogenRate = -1, _uraniumRate = -1;
double _previousHydrogen = -1, _previousUranium = -1;

// Calibration accumulators
double _hydrogenImpulseInterval, _uraniumEnergyInterval;
double _hydrogenImpulseTotal, _hydrogenUsedTotal;
double _uraniumEnergyTotal, _uraniumUsedTotal;

void FindStatusBlocks()
{
    GridTerminalSystem.GetBlocksOfType(_cargoBlocks, b => b.IsSameConstructAs(Me) && b.HasInventory
        && (b is IMyCargoContainer || b is IMyShipConnector || b is IMyShipDrill));
    GridTerminalSystem.GetBlocksOfType(_batteries, b => b.IsSameConstructAs(Me));
    GridTerminalSystem.GetBlocksOfType(_reactors, b => b.IsSameConstructAs(Me));
    GridTerminalSystem.GetBlocksOfType(_hydrogenTanks, b => b.IsSameConstructAs(Me)
        && b.BlockDefinition.SubtypeId.Contains("Hydrogen"));
    GridTerminalSystem.GetBlocksOfType(_jumpDrives, b => b.IsSameConstructAs(Me));
    GridTerminalSystem.GetBlocksOfType(_drills, b => b.IsSameConstructAs(Me));

    _hydrogenThrusters.Clear();
    _electricThrusters.Clear();
    double electricThrust = 0, electricPower = 0;
    foreach (IMyThrust t in _allThrusters)
    {
        string subtype = t.BlockDefinition.SubtypeId;
        if (subtype.Contains("Hydrogen"))
            _hydrogenThrusters.Add(t);
        else if (!subtype.Contains("Atmospheric"))
        {
            _electricThrusters.Add(t);
            double power = ParseMaxPower(t.DetailedInfo);
            if (power > 0)
            {
                electricThrust += t.MaxThrust;
                electricPower += power;
            }
        }
    }
    _electricThrustPerMW = electricPower > 0 ? electricThrust / electricPower : _defaultElectricThrustPerMW;
}

// Highest power value (in MW) found in a block's detailed info, e.g.
// "Max Required Input: 33.60 MW". Works for any game language because
// only the number and unit are read.
double ParseMaxPower(string info)
{
    double best = 0;
    foreach (string line in info.Split('\n'))
    {
        string[] tokens = line.Trim().Split(' ');
        for (int i = 1; i < tokens.Length; i++)
        {
            string unit = tokens[i];
            double factor = unit == "W" ? 1e-6 : unit == "kW" ? 1e-3 : unit == "MW" ? 1 : unit == "GW" ? 1e3 : 0;
            double value;
            if (factor > 0 && TryParseNumber(tokens[i - 1], out value))
                best = Math.Max(best, value * factor);
        }
    }
    return best;
}

// Runs every tick: integrates what was produced from the fuel, for calibration.
void SampleFuelUse(double dt)
{
    foreach (IMyThrust t in _hydrogenThrusters)
        _hydrogenImpulseInterval += t.CurrentThrust * dt;
    foreach (IMyReactor r in _reactors)
        _uraniumEnergyInterval += r.CurrentOutput * dt / 3600.0;
}

void UpdateShipStatus(double dt)
{
    ReadCargo();
    ReadPower();
    AutoLogMining();
    UpdatePlanet();

    double hydrogenUsed = _previousHydrogen >= 0 ? _previousHydrogen - _hydrogen : -1;
    double uraniumUsed = _previousUranium >= 0 ? _previousUranium - _uranium : -1;
    UpdateRate(hydrogenUsed, dt, ref _hydrogenRate);
    UpdateRate(uraniumUsed, dt, ref _uraniumRate);
    _previousHydrogen = _hydrogen;
    _previousUranium = _uranium;

    CalibrateHydrogen(hydrogenUsed);
    CalibrateUranium(uraniumUsed);

    IMyShipController reference = _controller ?? _layoutController;
    double mass = reference != null ? reference.CalculateShipMass().PhysicalMass : 0;
    if (mass > 0)
    {
        _deltaVHydrogen = _hydrogenThrusters.Count > 0 ? _hydrogen * _hydrogenThrustPerLiter / mass : 0;
        double energyMWh = _batteryStored + (_reactors.Count > 0 ? _uranium * _uraniumMWhPerKg : 0);
        _deltaVElectric = _electricThrusters.Count > 0 ? energyMWh * 3600 * _electricThrustPerMW / mass : 0;
    }
}

void ReadCargo()
{
    _cargoVolume = _cargoMaxVolume = _cargoMass = 0;
    _oreAmounts.Clear();
    foreach (IMyTerminalBlock block in _cargoBlocks)
    {
        for (int i = 0; i < block.InventoryCount; i++)
        {
            IMyInventory inventory = block.GetInventory(i);
            _cargoVolume += (float)inventory.CurrentVolume;
            _cargoMaxVolume += (float)inventory.MaxVolume;
            _cargoMass += (float)inventory.CurrentMass;

            _itemBuffer.Clear();
            inventory.GetItems(_itemBuffer);
            foreach (MyInventoryItem item in _itemBuffer)
            {
                if (item.Type.TypeId != OreType)
                    continue;
                double amount;
                _oreAmounts.TryGetValue(item.Type.SubtypeId, out amount);
                _oreAmounts[item.Type.SubtypeId] = amount + (float)item.Amount;
            }
        }
    }
}

void ReadPower()
{
    _batteryStored = _batteryMax = _batteryNetOutput = 0;
    foreach (IMyBatteryBlock b in _batteries)
    {
        if (!b.IsFunctional)
            continue;
        _batteryStored += b.CurrentStoredPower;
        _batteryMax += b.MaxStoredPower;
        _batteryNetOutput += b.CurrentOutput - b.CurrentInput;
    }

    _uranium = _reactorOutput = 0;
    foreach (IMyReactor r in _reactors)
    {
        _uranium += (float)r.GetInventory(0).CurrentMass;
        _reactorOutput += r.CurrentOutput;
    }

    _hydrogen = _hydrogenCapacity = 0;
    foreach (IMyGasTank t in _hydrogenTanks)
    {
        _hydrogen += t.FilledRatio * t.Capacity;
        _hydrogenCapacity += t.Capacity;
    }

    _jumpStored = _jumpMax = 0;
    foreach (IMyJumpDrive j in _jumpDrives)
    {
        _jumpStored += j.CurrentStoredPower;
        _jumpMax += j.MaxStoredPower;
    }
}

// Exponential moving average of the consumption rate. Refuelling
// (negative use) is skipped instead of counted as negative consumption.
void UpdateRate(double used, double dt, ref double rate)
{
    if (used < 0)
        return;
    double current = used / dt;
    rate = rate < 0 ? current : rate + (current - rate) * Math.Min(1, dt / RateSmoothing);
}

void CalibrateHydrogen(double used)
{
    // Only count intervals in which hydrogen thrusters fired, so tanks
    // drained by hydrogen engines alone do not skew the result.
    if (used > 0 && _hydrogenImpulseInterval > 0)
    {
        _hydrogenImpulseTotal += _hydrogenImpulseInterval;
        _hydrogenUsedTotal += used;
    }
    _hydrogenImpulseInterval = 0;

    if (_hydrogenUsedTotal < CalibrationLiters)
        return;
    double measured = _hydrogenImpulseTotal / _hydrogenUsedTotal;
    if (measured > 10 && measured < 1e6)
        _hydrogenThrustPerLiter = _hydrogenCalibrated ? (_hydrogenThrustPerLiter * 3 + measured) / 4 : measured;
    _hydrogenCalibrated = true;
    _hydrogenImpulseTotal = _hydrogenUsedTotal = 0;
}

void CalibrateUranium(double used)
{
    if (used > 0 && _uraniumEnergyInterval > 0)
    {
        _uraniumEnergyTotal += _uraniumEnergyInterval;
        _uraniumUsedTotal += used;
    }
    _uraniumEnergyInterval = 0;

    if (_uraniumUsedTotal < CalibrationUranium)
        return;
    double measured = _uraniumEnergyTotal / _uraniumUsedTotal;
    if (measured > 1e-3 && measured < 1e6)
        _uraniumMWhPerKg = _uraniumCalibrated ? (_uraniumMWhPerKg * 3 + measured) / 4 : measured;
    _uraniumCalibrated = true;
    _uraniumEnergyTotal = _uraniumUsedTotal = 0;
}

// Seconds until a supply runs out, or -1 if it is not being used.
double TimeRemaining(double amount, double ratePerSecond)
{
    return ratePerSecond > 1e-9 ? amount / ratePerSecond : -1;
}

double BatteryTimeRemaining()
{
    return _batteryNetOutput > 1e-6 ? _batteryStored / _batteryNetOutput * 3600 : -1;
}

// Delta-v for one trip: accelerate to MaxSpeed and brake again.
double TripDeltaV()
{
    return 2 * _maxSpeed;
}

// ---- ThrustControl.cs ----
const float BlockOverride = 1e-6f;          // tiny override that takes a thruster away from the game's control
const double InputDeadzone = 0.01;
const double DampenerHandoverSpeed = 0.2;   // m/s - below this, braking is handed back to the game
const double AxisAlignment = 0.9;           // min dot product for a thruster to count for an axis

readonly List<IMyShipController> _controllers = new List<IMyShipController>();
readonly List<IMyThrust> _allThrusters = new List<IMyThrust>();
// [axis][0] pushes along +axis, [axis][1] pushes along -axis.
// Axes match IMyShipController.MoveIndicator: 0 = right, 1 = up, 2 = backward.
readonly List<IMyThrust>[,] _axisThrusters = new List<IMyThrust>[3, 2];
readonly bool[] _axisActive = new bool[3];

IMyShipController _controller;
IMyShipController _layoutController;
double _currentSpeed;
double _forwardSpeed;

void ControlThrust()
{
    _controller = FindActiveController();
    if (!_enabled || _controller == null)
    {
        ReleaseAll();
        return;
    }

    if (_controller != _layoutController)
    {
        ReleaseAll();
        SortThrusters(_controller);
    }

    MatrixD matrix = _controller.WorldMatrix;
    Vector3D gravity = _controller.GetNaturalGravity();
    Vector3D velocity = _controller.GetShipVelocities().LinearVelocity;
    double mass = _controller.CalculateShipMass().PhysicalMass;
    Vector3 move = _controller.MoveIndicator;
    bool dampeners = _controller.DampenersOverride;
    _currentSpeed = velocity.Length();
    _forwardSpeed = Vector3D.Dot(velocity, matrix.Forward);

    // Target velocity from the active drive assist, if any.
    Vector3D targetVelocity;
    double assistAccel;
    bool hasTarget = UpdateDriveAssist(matrix, velocity, move, out targetVelocity, out assistAccel);

    Vector3D[] axes = { matrix.Right, matrix.Up, matrix.Backward };
    for (int axis = 0; axis < 3; axis++)
    {
        Vector3D dir = axes[axis];
        double input = axis == 0 ? move.X : axis == 1 ? move.Y : move.Z;
        double targetAccel;

        if (Math.Abs(input) > InputDeadzone)
        {
            targetAccel = MathHelper.Clamp(input, -1, 1) * _limit;
        }
        else if (hasTarget)
        {
            double error = Vector3D.Dot(targetVelocity - velocity, dir);
            targetAccel = MathHelper.Clamp(error * _velocityGain, -assistAccel, assistAccel);
        }
        else if (_limitDampeners && dampeners)
        {
            double speed = Vector3D.Dot(velocity, dir);
            if (Math.Abs(speed) < DampenerHandoverSpeed)
            {
                ReleaseAxis(axis);
                continue;
            }
            targetAccel = MathHelper.Clamp(-speed * _dampenerGain, -_limit, _limit);
        }
        else
        {
            // No input: leave this axis to the game (normal dampeners / drift).
            ReleaseAxis(axis);
            continue;
        }

        // Net force needed along +axis, including the part that cancels gravity.
        double force = mass * (targetAccel - Vector3D.Dot(gravity, dir));
        ApplyAxisForce(axis, force);
    }
}

void ApplyAxisForce(int axis, double force)
{
    List<IMyThrust> push = _axisThrusters[axis, force >= 0 ? 0 : 1];
    List<IMyThrust> idle = _axisThrusters[axis, force >= 0 ? 1 : 0];

    double available = 0;
    foreach (IMyThrust t in push)
        if (t.IsWorking)
            available += t.MaxEffectiveThrust;

    float fraction = available > 0
        ? (float)MathHelper.Clamp(Math.Abs(force) / available, BlockOverride, 1.0)
        : BlockOverride;

    foreach (IMyThrust t in push)
        t.ThrustOverridePercentage = fraction;
    // Keep the opposing thrusters overridden (near zero) so the game's
    // dampeners cannot fight the controlled acceleration.
    foreach (IMyThrust t in idle)
        t.ThrustOverridePercentage = BlockOverride;

    _axisActive[axis] = true;
}

void ReleaseAxis(int axis)
{
    if (!_axisActive[axis])
        return;
    for (int s = 0; s < 2; s++)
        foreach (IMyThrust t in _axisThrusters[axis, s])
            t.ThrustOverride = 0f;
    _axisActive[axis] = false;
}

// Only touches thrusters when this script owns overrides, unless forced,
// so manual overrides stay intact while nobody is piloting.
void ReleaseAll(bool force = false)
{
    if (!force && !_axisActive[0] && !_axisActive[1] && !_axisActive[2])
        return;
    foreach (IMyThrust t in _allThrusters)
        t.ThrustOverride = 0f;
    for (int a = 0; a < 3; a++)
        _axisActive[a] = false;
}

IMyShipController FindActiveController()
{
    IMyShipController best = null;
    foreach (IMyShipController c in _controllers)
    {
        if (!c.IsFunctional || !c.CanControlShip || !c.IsUnderControl)
            continue;
        if (best == null || (c.IsMainCockpit && !best.IsMainCockpit))
            best = c;
    }
    return best;
}

// Groups thrusters by the controller axis they push along.
void SortThrusters(IMyShipController reference)
{
    for (int a = 0; a < 3; a++)
        for (int s = 0; s < 2; s++)
            _axisThrusters[a, s].Clear();

    MatrixD m = reference.WorldMatrix;
    Vector3D[] axes = { m.Right, m.Up, m.Backward };

    foreach (IMyThrust t in _allThrusters)
    {
        Vector3D thrustDir = t.WorldMatrix.Backward; // direction the thruster pushes the ship
        for (int a = 0; a < 3; a++)
        {
            double dot = Vector3D.Dot(thrustDir, axes[a]);
            if (dot > AxisAlignment) _axisThrusters[a, 0].Add(t);
            else if (dot < -AxisAlignment) _axisThrusters[a, 1].Add(t);
        }
    }

    _layoutController = reference;
}

// Highest acceleration (m/s^2) the thrusters on one axis side can produce.
double MaxAccel(int axis, int sign, double mass)
{
    double thrust = 0;
    foreach (IMyThrust t in _axisThrusters[axis, sign])
        if (t.IsWorking)
            thrust += t.MaxEffectiveThrust;
    return thrust / mass;
}

// Highest acceleration the thrusters can produce along a world direction.
double MaxAccelAlong(Vector3D worldDir)
{
    IMyShipController reference = _controller ?? _layoutController;
    if (reference == null)
        return 0;
    double mass = reference.CalculateShipMass().PhysicalMass;
    MatrixD m = reference.WorldMatrix;
    Vector3D[] axes = { m.Right, m.Up, m.Backward };
    double result = double.MaxValue;
    for (int a = 0; a < 3; a++)
    {
        double component = Vector3D.Dot(worldDir, axes[a]);
        if (Math.Abs(component) < 1e-3)
            continue;
        double capacity = MaxAccel(a, component > 0 ? 0 : 1, mass);
        result = Math.Min(result, capacity / Math.Abs(component));
    }
    return result == double.MaxValue ? 0 : result;
}
