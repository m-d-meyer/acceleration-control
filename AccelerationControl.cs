// =====================================================================
//  Acceleration Control - Space Engineers programmable block script
// ---------------------------------------------------------------------
//  Limits the acceleration your ship produces when you press
//  W/A/S/D/Space/C. Instead of firing the thrusters at 100 %, the script
//  sets thruster overrides so the ship accelerates at a configurable
//  rate (m/s^2), compensating for ship mass and natural gravity.
//
//  Drive assists:
//   - Cruise:   holds a constant forward speed and cancels sideways drift
//               (e.g. slow, steady drilling into an asteroid).
//   - Approach: scans a target with a camera, flies there as fast as
//               possible and stops a set distance before the surface.
//
//  Copy everything in this file into a programmable block, then see
//  README.md for setup and commands.
// =====================================================================

const string IniSection = "AccelerationControl";
const float BlockOverride = 1e-6f;          // tiny override that takes a thruster away from the game's control
const double InputDeadzone = 0.01;
const double DampenerHandoverSpeed = 0.2;   // m/s - below this, braking is handed back to the game
const double AxisAlignment = 0.9;           // min dot product for a thruster to count for an axis
const double ArrivalDistance = 2.0;         // m - approach is finished within this distance...
const double ArrivalSpeed = 0.3;            // m/s - ...and below this speed
const int BlockRefreshTicks = 600;          // rescan blocks every 10 s
const int DisplayTicks = 10;

enum Mode { Manual, Cruise, Scanning, Approach }

// ---- configuration (loaded from Custom Data) ----
double _step = 1.0;
double _minLimit = 0.5;
double _maxLimit = 50.0;
double _defaultLimit = 5.0;
bool _limitDampenersDefault = false;
double _dampenerGain = 1.5;
string _lcdTag = "[Accel]";
int _cockpitSurface = -1;
double _defaultCruiseSpeed = 0.75;
double _cruiseStep = 0.25;
double _velocityGain = 2.0;
string _cameraTag = "[Accel]";
double _scanRange = 15000;
double _approachBuffer = 75;
bool _approachFullThrust = true;
double _maxSpeed = 100;
double _brakeSafety = 0.8;

// ---- state (persisted in Storage) ----
double _limit;
bool _enabled = true;
bool _limitDampeners;
double _cruiseSpeed;

// ---- runtime ----
readonly MyIni _ini = new MyIni();
readonly List<IMyShipController> _controllers = new List<IMyShipController>();
readonly List<IMyThrust> _allThrusters = new List<IMyThrust>();
readonly List<IMyCameraBlock> _cameras = new List<IMyCameraBlock>();
readonly List<IMyTextSurface> _surfaces = new List<IMyTextSurface>();
readonly List<IMyTextPanel> _panelBuffer = new List<IMyTextPanel>();
// [axis][0] pushes along +axis, [axis][1] pushes along -axis.
// Axes match IMyShipController.MoveIndicator: 0 = right, 1 = up, 2 = backward.
readonly List<IMyThrust>[,] _axisThrusters = new List<IMyThrust>[3, 2];
readonly bool[] _axisActive = new bool[3];
readonly StringBuilder _status = new StringBuilder();

IMyShipController _controller;
IMyShipController _layoutController;
IMyCameraBlock _camera;
Mode _mode = Mode.Manual;
Vector3D _approachTarget;
string _targetName = "";
double _targetDistance;
double _currentSpeed;
double _forwardSpeed;
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
    Storage = string.Join(";", Num(_limit), _enabled ? "1" : "0", _limitDampeners ? "1" : "0", Num(_cruiseSpeed));
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

    if (_ticks % DisplayTicks == 0)
        UpdateDisplays();
}

// ---------------------------------------------------------------------
//  Commands
// ---------------------------------------------------------------------

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

string Num(double value)
{
    return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

void SetLimit(double value)
{
    _limit = MathHelper.Clamp(value, _minLimit, _maxLimit);
}

// ---------------------------------------------------------------------
//  Approach: camera scan
// ---------------------------------------------------------------------

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
        _message = "Nothing found within " + (_scanRange / 1000).ToString("0.#") + " km";
        _mode = Mode.Manual;
        return;
    }

    Vector3D origin = _camera.GetPosition();
    Vector3D hitPos = hit.HitPosition.Value;
    Vector3D ray = hitPos - origin;
    double surfaceDistance = ray.Length();
    if (surfaceDistance <= _approachBuffer)
    {
        _message = "Target is closer than " + _approachBuffer.ToString("0") + " m";
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

// ---------------------------------------------------------------------
//  Thrust control
// ---------------------------------------------------------------------

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
    bool hasTarget = false;
    Vector3D targetVelocity = Vector3D.Zero;
    double assistAccel = _limit;

    if (_mode == Mode.Scanning)
        UpdateScan();

    if (_mode == Mode.Cruise)
    {
        if (Math.Abs(move.Z) > InputDeadzone)
        {
            _mode = Mode.Manual;
            _message = "Cruise cancelled";
        }
        else
        {
            hasTarget = true;
            targetVelocity = matrix.Forward * _cruiseSpeed;
        }
    }
    else if (_mode == Mode.Approach)
    {
        if (move.LengthSquared() > InputDeadzone * InputDeadzone)
        {
            _mode = Mode.Manual;
            _message = "Approach cancelled";
        }
        else
        {
            hasTarget = ApproachVelocity(velocity, out targetVelocity);
            if (_approachFullThrust)
                assistAccel = double.MaxValue;
        }
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

// Highest acceleration the thrusters can produce along a world direction.
double MaxAccelAlong(Vector3D worldDir)
{
    if (_controller == null)
        return 0;
    double mass = _controller.CalculateShipMass().PhysicalMass;
    MatrixD m = _controller.WorldMatrix;
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

// ---------------------------------------------------------------------
//  Block discovery
// ---------------------------------------------------------------------

void RefreshBlocks()
{
    ReleaseAll();

    GridTerminalSystem.GetBlocksOfType(_controllers, c => c.IsSameConstructAs(Me) && c.CanControlShip);
    GridTerminalSystem.GetBlocksOfType(_allThrusters, t => t.IsSameConstructAs(Me));
    GridTerminalSystem.GetBlocksOfType(_cameras, c => c.IsSameConstructAs(Me));

    _surfaces.Clear();
    GridTerminalSystem.GetBlocksOfType(_panelBuffer, p => p.IsSameConstructAs(Me) && p.CustomName.Contains(_lcdTag));
    foreach (IMyTextPanel p in _panelBuffer)
        _surfaces.Add(p);

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

// ---------------------------------------------------------------------
//  Display
// ---------------------------------------------------------------------

void UpdateDisplays()
{
    IMyShipController info = _controller ?? _layoutController;
    double mass = info != null ? info.CalculateShipMass().PhysicalMass : 0;

    _status.Clear();
    _status.AppendLine("Acceleration Control");
    _status.AppendLine(_enabled ? "Status: ON" : "Status: OFF (vanilla thrust)");
    _status.AppendFormat("Limit: {0:0.00} m/s² ({1:0.00} g)\n", _limit, _limit / 9.81);
    _status.AppendLine("Dampeners: " + (_limitDampeners ? "limited" : "full thrust"));
    AppendModeStatus();

    if (mass > 0)
    {
        _status.AppendLine("Max. acceleration:");
        _status.AppendFormat(" Fwd {0:0.0}  Back {1:0.0}\n", MaxAccel(2, 1, mass), MaxAccel(2, 0, mass));
        _status.AppendFormat(" Up  {0:0.0}  Down {1:0.0}\n", MaxAccel(1, 0, mass), MaxAccel(1, 1, mass));
        _status.AppendFormat(" Left {0:0.0}  Right {1:0.0}\n", MaxAccel(0, 1, mass), MaxAccel(0, 0, mass));
    }
    else
    {
        _status.AppendLine("No ship controller found");
    }

    if (_message.Length > 0)
        _status.AppendLine(_message);

    string text = _status.ToString();
    Echo(text);

    foreach (IMyTextSurface s in _surfaces)
        WriteSurface(s, text);

    var provider = info as IMyTextSurfaceProvider;
    if (provider != null && _cockpitSurface >= 0 && _cockpitSurface < provider.SurfaceCount)
        WriteSurface(provider.GetSurface(_cockpitSurface), text);
}

void AppendModeStatus()
{
    switch (_mode)
    {
        case Mode.Cruise:
            _status.AppendFormat("Cruise: {0:0.00} m/s (now {1:0.00})\n", _cruiseSpeed, _forwardSpeed);
            break;
        case Mode.Scanning:
            double charge = _camera != null ? _camera.AvailableScanRange / _scanRange * 100 : 0;
            _status.AppendFormat("Scanning... camera {0:0}%\n", Math.Min(charge, 100));
            break;
        case Mode.Approach:
            _status.AppendFormat("Approach {0}: {1}, {2:0} m/s\n", _targetName, FormatDistance(_targetDistance), _currentSpeed);
            break;
        default:
            _status.AppendFormat("Cruise speed: {0:0.00} m/s (off)\n", _cruiseSpeed);
            break;
    }
}

string FormatDistance(double meters)
{
    return meters >= 1000 ? (meters / 1000).ToString("0.00") + " km" : meters.ToString("0") + " m";
}

double MaxAccel(int axis, int sign, double mass)
{
    double thrust = 0;
    foreach (IMyThrust t in _axisThrusters[axis, sign])
        if (t.IsWorking)
            thrust += t.MaxEffectiveThrust;
    return thrust / mass;
}

void WriteSurface(IMyTextSurface surface, string text)
{
    surface.ContentType = ContentType.TEXT_AND_IMAGE;
    surface.WriteText(text);
}

// ---------------------------------------------------------------------
//  Configuration & persistence
// ---------------------------------------------------------------------

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
    _cockpitSurface = _ini.Get(IniSection, "CockpitSurface").ToInt32(_cockpitSurface);
    _defaultCruiseSpeed = _ini.Get(IniSection, "CruiseSpeed").ToDouble(_defaultCruiseSpeed);
    _cruiseStep = _ini.Get(IniSection, "CruiseStep").ToDouble(_cruiseStep);
    _velocityGain = _ini.Get(IniSection, "VelocityGain").ToDouble(_velocityGain);
    _cameraTag = _ini.Get(IniSection, "CameraTag").ToString(_cameraTag);
    _scanRange = _ini.Get(IniSection, "ScanRange").ToDouble(_scanRange);
    _approachBuffer = _ini.Get(IniSection, "ApproachBuffer").ToDouble(_approachBuffer);
    _approachFullThrust = _ini.Get(IniSection, "ApproachFullThrust").ToBoolean(_approachFullThrust);
    _maxSpeed = _ini.Get(IniSection, "MaxSpeed").ToDouble(_maxSpeed);
    _brakeSafety = _ini.Get(IniSection, "BrakeSafety").ToDouble(_brakeSafety);

    // Write back so every option is visible and editable in Custom Data.
    _ini.Set(IniSection, "DefaultAcceleration", _defaultLimit);
    _ini.Set(IniSection, "Step", _step);
    _ini.Set(IniSection, "MinAcceleration", _minLimit);
    _ini.Set(IniSection, "MaxAcceleration", _maxLimit);
    _ini.Set(IniSection, "LimitDampeners", _limitDampenersDefault);
    _ini.Set(IniSection, "DampenerGain", _dampenerGain);
    _ini.Set(IniSection, "LcdTag", _lcdTag);
    _ini.Set(IniSection, "CockpitSurface", _cockpitSurface);
    _ini.Set(IniSection, "CruiseSpeed", _defaultCruiseSpeed);
    _ini.Set(IniSection, "CruiseStep", _cruiseStep);
    _ini.Set(IniSection, "VelocityGain", _velocityGain);
    _ini.Set(IniSection, "CameraTag", _cameraTag);
    _ini.Set(IniSection, "ScanRange", _scanRange);
    _ini.Set(IniSection, "ApproachBuffer", _approachBuffer);
    _ini.Set(IniSection, "ApproachFullThrust", _approachFullThrust);
    _ini.Set(IniSection, "MaxSpeed", _maxSpeed);
    _ini.Set(IniSection, "BrakeSafety", _brakeSafety);
    _ini.SetSectionComment(IniSection,
        " Accelerations in m/s² (1 g = 9.81 m/s²), speeds in m/s, distances in m.\n" +
        " CockpitSurface: screen index of the cockpit to show status on, -1 = off.\n" +
        " Run the PB with 'reload' after editing.");
    Me.CustomData = _ini.ToString();

    if (_minLimit > _maxLimit)
        _minLimit = _maxLimit;
    _limit = MathHelper.Clamp(_limit, _minLimit, _maxLimit);
    _brakeSafety = MathHelper.Clamp(_brakeSafety, 0.1, 1.0);
    if (_cruiseStep <= 0)
        _cruiseStep = 0.25;
}

void LoadState()
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
