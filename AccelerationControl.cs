// =====================================================================
//  Acceleration Control - Space Engineers programmable block script
// ---------------------------------------------------------------------
//  Limits the acceleration your ship produces when you press
//  W/A/S/D/Space/C. Instead of firing the thrusters at 100 %, the script
//  sets thruster overrides so the ship accelerates at a configurable
//  rate (m/s^2), compensating for ship mass and natural gravity.
//
//  Copy everything in this file into a programmable block, then see
//  README.md for setup and commands.
// =====================================================================

const string IniSection = "AccelerationControl";
const float BlockOverride = 1e-6f;          // tiny override that takes a thruster away from the game's control
const double InputDeadzone = 0.01;
const double DampenerHandoverSpeed = 0.2;   // m/s - below this, braking is handed back to the game
const double AxisAlignment = 0.9;           // min dot product for a thruster to count for an axis
const int BlockRefreshTicks = 600;          // rescan blocks every 10 s
const int DisplayTicks = 10;

// ---- configuration (loaded from Custom Data) ----
double _step = 1.0;
double _minLimit = 0.5;
double _maxLimit = 50.0;
double _defaultLimit = 5.0;
bool _limitDampenersDefault = false;
double _dampenerGain = 1.5;
string _lcdTag = "[Accel]";
int _cockpitSurface = -1;

// ---- state (persisted in Storage) ----
double _limit;
bool _enabled = true;
bool _limitDampeners;

// ---- runtime ----
readonly MyIni _ini = new MyIni();
readonly List<IMyShipController> _controllers = new List<IMyShipController>();
readonly List<IMyThrust> _allThrusters = new List<IMyThrust>();
readonly List<IMyTextSurface> _surfaces = new List<IMyTextSurface>();
readonly List<IMyTextPanel> _panelBuffer = new List<IMyTextPanel>();
// [axis][0] pushes along +axis, [axis][1] pushes along -axis.
// Axes match IMyShipController.MoveIndicator: 0 = right, 1 = up, 2 = backward.
readonly List<IMyThrust>[,] _axisThrusters = new List<IMyThrust>[3, 2];
readonly bool[] _axisActive = new bool[3];
readonly double[] _axisMaxAccel = new double[6];
readonly StringBuilder _status = new StringBuilder();

IMyShipController _controller;
IMyShipController _layoutController;
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
    LoadState();
    RefreshBlocks();
    ReleaseAll(true); // clear overrides left behind by a previous run
    Runtime.UpdateFrequency = UpdateFrequency.Update1;
}

public void Save()
{
    Storage = string.Join(";", _limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _enabled ? "1" : "0", _limitDampeners ? "1" : "0");
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
    string value = parts.Length > 1 ? parts[1] : null;

    switch (cmd)
    {
        case "up":
        case "+":
            SetLimit(_limit + ParseStep(value));
            break;
        case "down":
        case "-":
            SetLimit(_limit - ParseStep(value));
            break;
        case "set":
            double parsed;
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
            ReleaseAll(true);
            break;
        case "toggle":
            _enabled = !_enabled;
            if (!_enabled) ReleaseAll(true);
            break;
        case "dampeners":
            if (value == "on") _limitDampeners = true;
            else if (value == "off") _limitDampeners = false;
            else _limitDampeners = !_limitDampeners;
            break;
        case "reload":
        case "refresh":
            LoadConfig();
            RefreshBlocks();
            _message = "Configuration reloaded";
            break;
        default:
            _message = "Unknown command: " + cmd;
            break;
    }
}

double ParseStep(string value)
{
    double step;
    return value != null && TryParseAccel(value, out step) ? step : _step;
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
    if (double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out result))
    {
        result *= factor;
        return true;
    }
    return false;
}

void SetLimit(double value)
{
    _limit = MathHelper.Clamp(value, _minLimit, _maxLimit);
    _message = "";
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

    for (int axis = 0; axis < 3; axis++)
    {
        Vector3D dir = axis == 0 ? matrix.Right : axis == 1 ? matrix.Up : matrix.Backward;
        double input = axis == 0 ? move.X : axis == 1 ? move.Y : move.Z;
        double targetAccel;

        if (Math.Abs(input) > InputDeadzone)
        {
            targetAccel = MathHelper.Clamp(input, -1, 1) * _limit;
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

// ---------------------------------------------------------------------
//  Block discovery
// ---------------------------------------------------------------------

void RefreshBlocks()
{
    ReleaseAll();

    GridTerminalSystem.GetBlocksOfType(_controllers, c => c.IsSameConstructAs(Me) && c.CanControlShip);
    GridTerminalSystem.GetBlocksOfType(_allThrusters, t => t.IsSameConstructAs(Me));

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

    // Write back so every option is visible and editable in Custom Data.
    _ini.Set(IniSection, "DefaultAcceleration", _defaultLimit);
    _ini.Set(IniSection, "Step", _step);
    _ini.Set(IniSection, "MinAcceleration", _minLimit);
    _ini.Set(IniSection, "MaxAcceleration", _maxLimit);
    _ini.Set(IniSection, "LimitDampeners", _limitDampenersDefault);
    _ini.Set(IniSection, "DampenerGain", _dampenerGain);
    _ini.Set(IniSection, "LcdTag", _lcdTag);
    _ini.Set(IniSection, "CockpitSurface", _cockpitSurface);
    _ini.SetSectionComment(IniSection,
        " Accelerations in m/s² (1 g = 9.81 m/s²).\n" +
        " CockpitSurface: screen index of the cockpit to show status on, -1 = off.\n" +
        " Run the PB with 'reload' after editing.");
    Me.CustomData = _ini.ToString();

    if (_minLimit > _maxLimit)
        _minLimit = _maxLimit;
    _limit = MathHelper.Clamp(_limit, _minLimit, _maxLimit);
}

void LoadState()
{
    string[] parts = Storage.Split(';');
    if (parts.Length < 3)
        return;
    double limit;
    if (double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out limit))
        _limit = MathHelper.Clamp(limit, _minLimit, _maxLimit);
    _enabled = parts[1] == "1";
    _limitDampeners = parts[2] == "1";
}
