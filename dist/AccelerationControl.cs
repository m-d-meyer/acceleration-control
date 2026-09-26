// Acceleration Control - Space Engineers programmable block script
// https://github.com/m-d-meyer/acceleration-control
//
// Limits the acceleration of the movement keys, adds drive assists
// (cruise, approach) and shows cargo, fuel and delta-v status.
// See README.md in the repository for setup and commands.

// ---- Program.cs ----
const int BlockRefreshTicks = 600;          // rescan blocks every 10 s
const int DisplayTicks = 10;
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

    ControlThrust();

    SampleFuelUse(1 / TicksPerSecond);
    if (_ticks % StatusTicks == 0)
        UpdateShipStatus(StatusTicks / TicksPerSecond);

    if (_ticks % DisplayTicks == 0)
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
    if (_mode != Mode.Scanning && _mode != Mode.Approach)
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
            StartScan();
            break;
        case "stop":
            _mode = Mode.Manual;
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
        " CockpitSurface / StatusCockpitSurface: cockpit screen index, -1 = off.\n" +
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
readonly StringBuilder _text = new StringBuilder();
readonly List<KeyValuePair<string, double>> _oreSorted = new List<KeyValuePair<string, double>>();

void FindDisplays()
{
    GridTerminalSystem.GetBlocksOfType(_controlPanels, p => p.IsSameConstructAs(Me) && p.CustomName.Contains(_lcdTag));
    GridTerminalSystem.GetBlocksOfType(_statusPanels, p => p.IsSameConstructAs(Me) && p.CustomName.Contains(_statusTag));
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

    var provider = info as IMyTextSurfaceProvider;
    if (provider != null)
    {
        WriteCockpitSurface(provider, _cockpitSurface, control);
        WriteCockpitSurface(provider, _statusCockpitSurface, status);
    }
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
    switch (_mode)
    {
        case Mode.Cruise:
            _text.AppendFormat("Cruise: {0:0.00} m/s (now {1:0.00})\n", _cruiseSpeed, _forwardSpeed);
            break;
        case Mode.Scanning:
            double charge = _camera != null ? _camera.AvailableScanRange / _scanRange * 100 : 0;
            _text.AppendFormat("Scanning... camera {0:0}%\n", Math.Min(charge, 100));
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

enum Mode { Manual, Cruise, Scanning, Approach }

readonly List<IMyCameraBlock> _cameras = new List<IMyCameraBlock>();
IMyCameraBlock _camera;
Mode _mode = Mode.Manual;
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

    if (_mode == Mode.Scanning)
        UpdateScan();

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

void StartScan()
{
    _camera = FindCamera();
    if (_camera == null)
    {
        _message = "No camera facing forward (or tagged " + _cameraTag + ")";
        return;
    }
    _camera.EnableRaycast = true;
    _enabled = true;
    _mode = Mode.Scanning;
}

// Called every tick while scanning: fires the raycast as soon as the
// camera has charged enough range.
void UpdateScan()
{
    if (_camera == null || !_camera.IsWorking)
    {
        _message = "Camera not working";
        _mode = Mode.Manual;
        return;
    }
    if (!_camera.CanScan(_scanRange))
        return;

    MyDetectedEntityInfo hit = _camera.Raycast(_scanRange);
    if (hit.IsEmpty() || !hit.HitPosition.HasValue)
    {
        _message = "Nothing found within " + FormatDistance(_scanRange);
        _mode = Mode.Manual;
        return;
    }

    Vector3D origin = _camera.GetPosition();
    Vector3D hitPos = hit.HitPosition.Value;
    Vector3D ray = hitPos - origin;
    double surfaceDistance = ray.Length();
    if (surfaceDistance <= _approachBuffer)
    {
        _message = "Target is closer than " + FormatDistance(_approachBuffer);
        _mode = Mode.Manual;
        return;
    }

    _approachTarget = hitPos - ray / surfaceDistance * _approachBuffer;
    _targetName = hit.Type == MyDetectedEntityType.Asteroid ? "Asteroid"
        : hit.Type == MyDetectedEntityType.Planet ? "Planet"
        : hit.Name;
    _message = _targetName + " at " + FormatDistance(surfaceDistance);
    _mode = Mode.Approach;
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
    Vector3D position = _camera != null && _camera.IsFunctional ? _camera.GetPosition() : _controller.GetPosition();
    Vector3D toTarget = _approachTarget - position;
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
