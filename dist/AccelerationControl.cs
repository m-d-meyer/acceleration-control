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
    if (_ticks % SurveyTicks == 0)
        UpdateSurvey();

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
double _stopDistance;
string _approachPhase = "";

// Approach without a target in scan range: search along the line of sight.
const double ProbeMinRange = 1000;
bool _probing;
Vector3D _probeDirection;
Vector3D _clearUntil;

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
    if (_mode == Mode.Approach && _probing)
        UpdateProbe();
    if (!_scanPending)
        return;
    if (_camera == null || !_camera.IsWorking)
    {
        _message = "Camera not working";
        _scanPending = false;
        return;
    }

    // Marking needs the full range. An approach starts with what is
    // charged and keeps scanning ahead while flying (see UpdateProbe).
    double range = _scanPurpose == ScanPurpose.Mark ? _scanRange
        : Math.Min(_scanRange, Math.Max(ProbeMinRange, _camera.AvailableScanRange));
    if (!_camera.CanScan(range))
        return;

    _scanPending = false;
    Vector3D origin = _camera.GetPosition();
    Vector3D direction = _camera.WorldMatrix.Forward;
    MyDetectedEntityInfo hit = _camera.Raycast(range);
    if (hit.IsEmpty() || !hit.HitPosition.HasValue)
    {
        if (_scanPurpose == ScanPurpose.Mark)
            _message = "Nothing found within " + FormatDistance(range) + ". Asteroids far away are often not detected, fly closer.";
        else
            StartProbe(origin, direction, range);
        return;
    }

    RegisterObstacle(hit);
    if (_scanPurpose == ScanPurpose.Mark)
        AddDeposit(_pendingMarkOre, hit.HitPosition.Value, false);
    else
        ApproachHit(hit);
}

void ApproachHit(MyDetectedEntityInfo hit)
{
    Vector3D hitPos = hit.HitPosition.Value;
    string name = hit.Type == MyDetectedEntityType.Asteroid ? "Asteroid"
        : hit.Type == MyDetectedEntityType.Planet ? "Planet"
        : hit.Name;
    _probing = false;
    if (StartApproach(hitPos, name))
        _message = name + " at " + FormatDistance(Vector3D.Distance(hitPos, ReferencePosition()));
}

// Nothing in range yet: fly along the camera's line of sight and keep
// scanning ahead. The speed is limited so the ship can always stop
// within the part of the line that has been scanned clear.
void StartProbe(Vector3D origin, Vector3D direction, double clearRange)
{
    _probing = true;
    _probeDirection = direction;
    _clearUntil = origin + direction * clearRange;
    _approachTarget = origin + direction * _probeRange;
    _targetName = "line of sight";
    _enabled = true;
    _mode = Mode.Approach;
    _message = "Nothing within " + FormatDistance(clearRange) + ", searching ahead";
}

void UpdateProbe()
{
    if (_camera == null || !_camera.IsWorking)
        return;
    Vector3D position = _camera.GetPosition();
    double brake = Math.Max(BrakeAccel(_probeDirection), 0.1);
    double stopDistance = _currentSpeed * _currentSpeed / (2 * brake);
    double look = MathHelper.Clamp(stopDistance * 1.5 + _approachBuffer * 2, ProbeMinRange, _scanRange);
    Vector3D lookTarget = position + _probeDirection * look;
    if (!_camera.CanScan(lookTarget))
        return;

    MyDetectedEntityInfo hit = _camera.Raycast(lookTarget);
    if (!hit.IsEmpty() && hit.HitPosition.HasValue)
    {
        RegisterObstacle(hit);
        ApproachHit(hit);
    }
    else if (Vector3D.Dot(lookTarget - _clearUntil, _probeDirection) > 0)
        _clearUntil = lookTarget;
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
    _probing = false;
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

// Deceleration the approach plans with when moving along a direction.
double BrakeAccel(Vector3D direction)
{
    double brake = MaxAccelAlong(-direction) * _brakeSafety;
    return _approachFullThrust ? brake : Math.Min(brake, _limit * _brakeSafety);
}

// Velocity that brings the ship to the approach target and still lets it
// stop in time: v = sqrt(2 * a_brake * distance), capped at MaxSpeed.
bool ApproachVelocity(Vector3D velocity, out Vector3D targetVelocity)
{
    targetVelocity = Vector3D.Zero;
    Vector3D position = ReferencePosition();
    Vector3D toTarget = _approachTarget - position;
    _targetDistance = toTarget.Length();

    if (_targetDistance < ArrivalDistance && velocity.Length() < ArrivalSpeed)
    {
        _mode = Mode.Manual;
        _message = _probing ? "Nothing found along the line of sight" : "Arrived";
        _probing = false;
        return false;
    }

    Vector3D direction = toTarget / Math.Max(_targetDistance, 1e-3);
    double brake = BrakeAccel(direction);
    double distance = _targetDistance;
    if (_probing)
    {
        // Only the scanned part of the line is known to be free.
        double clear = Vector3D.Dot(_clearUntil - position, direction) - _approachBuffer;
        distance = Math.Min(distance, Math.Max(clear, 0));
    }

    double speed = Math.Min(_maxSpeed, Math.Sqrt(2 * brake * distance));
    // Close in: approach proportionally so the ship settles instead of oscillating.
    speed = Math.Min(speed, distance * _velocityGain * 0.5);
    targetVelocity = direction * speed;

    // For the display: stopping distance and flight phase.
    double current = Vector3D.Dot(velocity, direction);
    _stopDistance = current > 0 ? current * current / (2 * Math.Max(brake, 0.01)) : 0;
    _approachPhase = speed < current - 1 ? "BRAKING" : speed > current + 1 ? "ACCELERATING" : "CRUISING";
    return true;
}

// ---- MapDisplay.cs ----
const float RadarRadius = 220;      // units for the full zoom range
const float PlaneTilt = 0.5f;       // vertical squash of the radar plane
const float HeightScale = 0.85f;    // height stems relative to plane scale
const float MaxStem = 95;           // longest height stem (units)
const float ListRowHeight = 34;
const int RadarLabels = 5;          // labelled deposits besides the selection
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
            DrawButtons(RadarButtons, 452, active);
            if (active)
                DrawDialog(512);
        }
        else
        {
            _u = width / 512f;
            _origin = viewport.Position;
            float h = height / _u;
            DrawList(h);
            DrawButtons(ListButtons, h - 56, active);
            if (active)
                DrawDialog(h);
        }
    }
}

void DrawHeader(float height, string right)
{
    Rect(0, 0, 512, height, PanelColor);
    Text("ORE MAP", 12, height / 2 - 15, 0.95f, Cyan);
    float x = 500;
    if (_uiMode)
    {
        float w = MeasureText("UI MODE", 0.6f, "White") + 14;
        Rect(x - w, height / 2 - 13, w, 26, Cyan);
        Text("UI MODE", x - w / 2, height / 2 - 11, 0.6f, BgColor, TextAlignment.CENTER);
        x -= w + 10;
    }
    Text(right, x, height / 2 - 10, 0.6f, DimColor, TextAlignment.RIGHT);
}

// -----------------------------------------------------------------
//  Radar view
// -----------------------------------------------------------------

void DrawRadar()
{
    IMyShipController reference = _controller ?? _layoutController;
    float cx = 256, cy = 180;
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
                Text("GRAVITY " + FormatDistance(Vector3D.Distance(o.Center, shipPos) - o.GravityRadius),
                    edge.X, edge.Y + 4, 0.55f, GravityColor, TextAlignment.CENTER);
        }
    }

    // Range rings and axes
    EllipseOutline(cx, cy, RadarRadius, RadarRadius * PlaneTilt, 1.5f, GridColor, false);
    EllipseOutline(cx, cy, RadarRadius / 2, RadarRadius / 2 * PlaneTilt, 1, GridFaint, false);
    EllipseOutline(cx, cy, RadarRadius / 4, RadarRadius / 4 * PlaneTilt, 1, GridFaint, false);
    Line(cx - RadarRadius, cy, cx + RadarRadius, cy, 1, GridFaint);
    Line(cx, cy - RadarRadius * PlaneTilt, cx, cy + RadarRadius * PlaneTilt, 1, GridFaint);
    Text(FormatDistance(MapRange), cx + RadarRadius * 0.74f, cy - RadarRadius * PlaneTilt * 0.74f - 20, 0.5f, DimColor, TextAlignment.LEFT);
    Text(FormatDistance(MapRange / 2), cx + RadarRadius * 0.37f, cy - RadarRadius * PlaneTilt * 0.37f - 18, 0.45f, DimColor, TextAlignment.LEFT);

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
        Dashed(cx, cy, target.X, target.Y, 3, RouteColor);
        DiamondOutline(target.X, target.Y, 8, RouteColor);
    }

    foreach (MapItem item in _mapItems)
    {
        if (item.Rock != null)
            DrawRock(item, cx, cy, scale);
        else
            DrawDeposit(item, cx, cy, scale);
    }

    // Ship
    _frame.Add(new MySprite(SpriteType.TEXTURE, "Triangle", P(cx, cy), new Vector2(16, 20) * _u, Cyan));

    // Header and info panel are drawn last so they cover anything that
    // sticks out of the radar area.
    DrawHeader(44, (inGravity ? "IN GRAVITY  " : "") + FormatDistance(MapRange) + "  " + (_filter ?? "all"));
    Rect(0, 294, 512, 218, BgColor);
    DrawSelectionPanel(8, 298, 496, 146);
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
        // Beyond the range: small marker on the edge.
        Marker(foot.X, foot.Y, 5, d.Ore, color * 0.6f);
        if (!selected)
            return;
        pos = foot;
    }
    else
    {
        Line(foot.X, foot.Y, pos.X, pos.Y, 2, color * 0.85f);
        Ellipse(foot.X, foot.Y, 3, 1.6f, color * 0.8f, false);
        Marker(pos.X, pos.Y, 7, d.Ore, color);
    }

    // Labels only for the selection and the nearest deposits, so they stay readable.
    if (!selected && _visibleDeposits.IndexOf(d) >= RadarLabels)
        return;
    string name = ShortOre(d.Ore) + d.Number;
    string distance = FormatDistance(d.Distance);
    float tx = pos.X + 11, ty = pos.Y - 16;
    if (selected)
    {
        float w = Math.Max(MeasureText(name, 0.62f, "White"), MeasureText(distance, 0.52f, "White"));
        Rect(tx - 4, ty - 2, w + 8, 38, new Color(40, 30, 10) * 0.9f);
        Box(tx - 4, ty - 2, w + 8, 38, 1.5f, RouteColor);
    }
    Text(name, tx, ty, 0.62f, selected ? RouteColor : color);
    Text(distance, tx, ty + 17, 0.52f, selected ? RouteColor : DimColor);
}

// Info about the selected deposit and the current flight.
void DrawSelectionPanel(float x, float y, float width, float height)
{
    Rect(x, y, width, height, PanelColor);
    Box(x, y, width, height, 1, GridColor);
    float left = x + 12, right = x + width - 12;

    if (_mode == Mode.Approach)
    {
        Text("> " + _targetName, left, y + 6, 0.8f, RouteColor);
        Text(_currentSpeed.ToString("0") + " m/s", right, y + 10, 0.7f, TextColor, TextAlignment.RIGHT);
        DrawApproachGauge(left, y + 44, right - left);
    }
    else if (_selected == null)
    {
        Text(_deposits.Count == 0 ? "No deposits mapped yet" : "No deposit matches the filter", left, y + 8, 0.7f, DimColor);
        Text("MARK: aim at the ore and scan", left, y + 42, 0.6f, DimColor);
    }
    else
    {
        Text(_selected.Label, left, y + 6, 0.8f, RouteColor);
        Text(FormatDistance(_selected.Distance), right, y + 10, 0.7f, TextColor, TextAlignment.RIGHT);
        Text(DirectionText(_selected.Position, false), left, y + 40, 0.6f, DimColor);

        Obstacle blocking = FirstObstacleOnPath(ReferencePosition(), _selected.Position);
        Text(blocking == null ? "Direct path clear" : "Path blocked by " + (blocking.Planet ? "planet" : "asteroid"),
            left, y + 64, 0.6f, blocking == null ? TextColor : WarnColor);

        double total = _deltaVHydrogen + _deltaVElectric;
        if (total > 0)
        {
            float barW = width - 230;
            Text("dv", left, y + 90, 0.6f, TextColor);
            Box(left + 32, y + 94, barW, 14, 1, GridColor);
            Rect(left + 33, y + 95, (barW - 2) * (float)Math.Min(TripDeltaV() / total, 1), 12, RouteColor);
            Text(string.Format("{0:0} / {1:0} m/s", TripDeltaV(), total), right, y + 90, 0.55f, TextColor, TextAlignment.RIGHT);
        }
    }

    if (_message.Length > 0)
        Text(_message, left, y + height - 26, 0.52f, DimColor);
}

// Distance to the target with the stopping distance marked: braking
// starts when the orange mark reaches the end of the bar.
void DrawApproachGauge(float x, float y, float width)
{
    bool braking = _approachPhase == "BRAKING";
    Text(_approachPhase, x, y, 0.6f, braking ? RouteColor : Cyan);
    Text("stop " + FormatDistance(_stopDistance) + " / " + FormatDistance(_targetDistance),
        x + width, y, 0.6f, TextColor, TextAlignment.RIGHT);
    float by = y + 26, bh = 18;
    double full = Math.Max(Math.Max(_targetDistance, _stopDistance), 1);
    Box(x, by, width, bh, 1, GridColor);
    Rect(x + 1, by + 1, (width - 2) * (float)(_targetDistance / full), bh - 2, Cyan * 0.6f);
    float stop = x + (width - 2) * (float)Math.Min(_stopDistance / full, 1);
    Rect(stop - 2, by - 4, 4, bh + 8, RouteColor);
    if (_probing)
        Text("searching ahead, nothing found yet", x, by + bh + 4, 0.5f, DimColor);
}

// -----------------------------------------------------------------
//  List view
// -----------------------------------------------------------------

void DrawList(float height)
{
    DrawHeader(40, _visibleDeposits.Count + " entries  " + (_filter ?? "all"));

    float y = 46;
    int rows = Math.Max(1, (int)((height - y - 108) / ListRowHeight));
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
            Rect(4, y, 504, ListRowHeight - 3, new Color(60, 45, 12));
            Box(4, y, 504, ListRowHeight - 3, 1.5f, RouteColor);
        }
        float ty = y + 3;
        Marker(20, y + 15, 6.5f, d.Ore, color);
        Text(d.Label, 34, ty, 0.72f, selected ? RouteColor : TextColor);
        Text(FormatDistance(d.Distance), 322, ty + 2, 0.66f, TextColor, TextAlignment.RIGHT);

        // Direction indicator: where the entry is relative to the nose.
        Vector3D local = ToLocal(d.Position, shipPos, ship);
        double yaw = Math.Atan2(local.X, local.Z), pitch = Math.Atan2(local.Y, new Vector2D(local.X, local.Z).Length());
        bool behind = Math.Abs(yaw) > Math.PI / 2;
        float ix = 342, iy = y + 15;
        EllipseOutline(ix, iy, 10, 10, 1.5f, GridColor, false);
        float dx = (float)(Math.Sign(yaw) * Math.Min(Math.Abs(yaw), Math.PI / 2) / (Math.PI / 2) * 8);
        float dy = (float)(-pitch / (Math.PI / 2) * 8);
        Ellipse(ix + dx, iy + dy, 3, 3, behind ? WarnColor : color, false);
        Text(DirectionText(d.Position, true), 358, ty + 2, 0.6f, behind ? WarnColor : TextColor);

        y += ListRowHeight;
    }

    if (_visibleDeposits.Count == 0)
    {
        Text("No entries yet.", 20, y + 4, 0.7f, DimColor);
        Text("MARK: aim at the ore and scan", 20, y + 34, 0.6f, DimColor);
    }

    // Selection or flight summary
    float fy = height - 100;
    Line(6, fy, 506, fy, 1, GridColor);
    if (_mode == Mode.Approach)
    {
        Text(_approachPhase + "  " + FormatDistance(_targetDistance), 10, fy + 6, 0.66f, _approachPhase == "BRAKING" ? RouteColor : Cyan);
        Text("stop " + FormatDistance(_stopDistance), 502, fy + 8, 0.6f, TextColor, TextAlignment.RIGHT);
    }
    else if (_selected != null)
    {
        Text(_selected.Label + "  " + FormatDistance(_selected.Distance), 10, fy + 6, 0.66f, RouteColor);
        double total = _deltaVHydrogen + _deltaVElectric;
        if (total > 0)
            Text(string.Format("dv {0:0}/{1:0}", TripDeltaV(), total), 502, fy + 8, 0.6f, TextColor, TextAlignment.RIGHT);
    }
    if (_message.Length > 0)
        Text(_message, 10, fy + 30, 0.5f, DimColor);
}

// -----------------------------------------------------------------
//  Buttons and dialogs
// -----------------------------------------------------------------

void DrawButtons(string[] buttons, float y, bool active)
{
    float gap = 5, width = (504 - gap * (buttons.Length - 1)) / buttons.Length;
    for (int i = 0; i < buttons.Length; i++)
    {
        float x = 4 + i * (width + gap);
        bool highlighted = active && i == _button && _dialog == Dialog.None;
        Rect(x, y, width, 52, highlighted ? Cyan : PanelColor);
        Box(x, y, width, 52, 1.5f, active ? Cyan : GridColor);
        Text(buttons[i], x + width / 2, y + 12, 0.72f, highlighted ? BgColor : active ? Cyan : DimColor, TextAlignment.CENTER);
    }
}

void DrawDialog(float height)
{
    if (_dialog == Dialog.None)
        return;
    float x = 40, w = 432, y = 48;
    if (_dialog == Dialog.ConfirmDelete)
    {
        float h = 110;
        y = (height - h) / 2 - 20;
        Rect(x, y, w, h, PanelColor);
        Box(x, y, w, h, 2, WarnColor);
        Text("Delete " + (_selected != null ? _selected.Label : "") + "?", x + w / 2, y + 14, 0.85f, WarnColor, TextAlignment.CENTER);
        Text("OK = delete    BACK = cancel", x + w / 2, y + 64, 0.6f, TextColor, TextAlignment.CENTER);
        return;
    }

    // Ore picker for MARK
    int rows = Math.Max(2, Math.Min(_pickerOres.Count, (int)((height - y - 150) / ListRowHeight)));
    float boxH = 80 + rows * ListRowHeight;
    Rect(x, y, w, boxH, PanelColor);
    Box(x, y, w, boxH, 2, Cyan);
    Text("MARK", x + 12, y + 6, 0.85f, Cyan);
    Text("aim at the ore first", x + w - 12, y + 12, 0.55f, DimColor, TextAlignment.RIGHT);
    int first = Math.Max(0, Math.Min(_pickerIndex - rows / 2, _pickerOres.Count - rows));
    float ry = y + 42;
    for (int i = first; i < _pickerOres.Count && i < first + rows; i++)
    {
        bool selected = i == _pickerIndex;
        if (selected)
            Rect(x + 6, ry, w - 12, ListRowHeight - 3, Cyan);
        Marker(x + 24, ry + 15, 6.5f, _pickerOres[i], OreColor(_pickerOres[i]));
        Text(_pickerOres[i], x + 40, ry + 3, 0.72f, selected ? BgColor : TextColor);
        ry += ListRowHeight;
    }
    Text("OK = scan    BACK = cancel", x + w / 2, y + boxH - 30, 0.55f, DimColor, TextAlignment.CENTER);
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
    float height = MathHelper.Clamp((float)local.Y * scale * HeightScale, -MaxStem, MaxStem);
    Vector2 plane = PlanePoint(local, cx, cy, scale);
    return new Vector2(plane.X, plane.Y - height);
}

// "27° R  13° U" (+ "behind") relative to the ship's nose; compact: "27°R 13°U".
string DirectionText(Vector3D world, bool compact)
{
    IMyShipController reference = _controller ?? _layoutController;
    MatrixD ship = reference != null ? reference.WorldMatrix : Me.WorldMatrix;
    Vector3D local = ToLocal(world, ReferencePosition(), ship);
    double yaw = Math.Atan2(local.X, local.Z) * 180 / Math.PI;
    double pitch = Math.Atan2(local.Y, new Vector2D(local.X, local.Z).Length()) * 180 / Math.PI;
    if (compact)
        return string.Format("{0:0}°{1} {2:0}°{3}", Math.Abs(yaw), yaw >= 0 ? "R" : "L", Math.Abs(pitch), pitch >= 0 ? "U" : "D");
    return string.Format("{0:0}° {1}   {2:0}° {3}{4}", Math.Abs(yaw), yaw >= 0 ? "right" : "left",
        Math.Abs(pitch), pitch >= 0 ? "up" : "down", Math.Abs(yaw) > 90 ? "   behind" : "");
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
        case BaseName: return Cyan;
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

// Deposit marker: a diamond, the base a square.
void Marker(float x, float y, float r, string ore, Color color)
{
    if (ore == BaseName)
    {
        Rect(x - r, y - r, r * 2, r * 2, color);
        Rect(x - r * 0.45f, y - r * 0.45f, r * 0.9f, r * 0.9f, BgColor);
    }
    else
        Diamond(x, y, r, color);
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
bool _uiMode;
string _uiKey;              // key held in UI mode
int _uiHoldTicks;

string[] CurrentButtons { get { return _view == MapView.Radar ? RadarButtons : ListButtons; } }
double MapRange { get { return ZoomLevels[_zoomIndex]; } }

void HandleUiCommand(string value)
{
    string[] buttons = CurrentButtons;
    switch (value)
    {
        case null:
        case "toggle":
            SetUiMode(!_uiMode);
            break;
        case "on":
            SetUiMode(true);
            break;
        case "off":
            SetUiMode(false);
            break;
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
            _message = "Usage: ui [on|off] or ui left|right|up|down|ok|back";
            break;
    }
}

void SetUiMode(bool on)
{
    _uiMode = on;
    _uiKey = null;
    _message = on ? "UI mode: W/S/A/D select, Space OK, C back" : "UI mode off";
}

// Reads the movement keys as menu keys (called every tick in UI mode).
void UpdateUiInput(Vector3 move)
{
    string key = move.Z < -0.5f ? "up" : move.Z > 0.5f ? "down"
        : move.X < -0.5f ? "left" : move.X > 0.5f ? "right"
        : move.Y > 0.5f ? "ok" : move.Y < -0.5f ? "back" : null;
    if (key != _uiKey)
    {
        _uiKey = key;
        _uiHoldTicks = 0;
        if (key != null)
            UiKey(key);
        return;
    }
    // Held arrow keys repeat after 0.4 s, 10 times per second.
    if (key != null && key != "ok" && key != "back" && ++_uiHoldTicks >= 24 && _uiHoldTicks % 6 == 0)
        UiKey(key);
}

void UiKey(string key)
{
    if (key == "back" && _dialog == Dialog.None)
        SetUiMode(false);
    else
        HandleUiCommand(key);
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
    _pickerOres.Add(BaseName);
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
    if (_controller == null)
        _uiMode = false;
    if ((!_enabled && !_uiMode) || _controller == null)
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
    if (_uiMode)
    {
        // The movement keys operate the menu; the ship must not react to them.
        UpdateUiInput(move);
        move = Vector3.Zero;
    }
    _currentSpeed = velocity.Length();
    _forwardSpeed = Vector3D.Dot(velocity, matrix.Forward);

    // Target velocity from the active drive assist, if any.
    Vector3D targetVelocity;
    double assistAccel;
    bool hasTarget = UpdateDriveAssist(matrix, velocity, move, out targetVelocity, out assistAccel);
    if (_uiMode && !hasTarget)
    {
        // Hold the ship (or keep drifting with dampeners off), since the
        // game would otherwise fire the thrusters for the pressed keys.
        hasTarget = true;
        targetVelocity = dampeners ? Vector3D.Zero : velocity;
    }

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
