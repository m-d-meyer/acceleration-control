// Dock Gate - companion script for the base, for Acceleration Control
// https://github.com/m-d-meyer/acceleration-control
//
// Opens and closes gates when a ship with Acceleration Control docks or undocks
// (requests over the antennas). See "Gates" in README.md.
// MIT License, see LICENSE in the repository.

// ---- Program.cs ----
const string Tag = "AccelDock";
const int TimeoutRuns = 6 * 60;     // runs at 6 per second: give up waiting after a minute

readonly IMyBroadcastListener _listener;
readonly HashSet<long> _opened = new HashSet<long>();
readonly List<IMyTerminalBlock> _blocks = new List<IMyTerminalBlock>();
readonly List<IMyBlockGroup> _groups = new List<IMyBlockGroup>();
readonly Dictionary<long, float> _angles = new Dictionary<long, float>();
long _pending, _replyTo;
string _pendingName = "";
int _waited;
string _status = "Idle";

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
    _listener = IGC.RegisterBroadcastListener(Tag);
}

public void Main(string argument, UpdateType updateSource)
{
    // By hand (for testing): 'open' / 'close' runs the timers of all docks.
    if (argument == "open" || argument == "close")
        Trigger(argument == "open" ? "Dock Open" : "Dock Close", null);

    while (_listener.HasPendingMessage)
    {
        MyIGCMessage message = _listener.AcceptMessage();
        string[] parts = (message.Data as string ?? "").Split('|');
        long id;
        if (parts.Length == 2 && long.TryParse(parts[1], out id))
            Request(parts[0], id, message.Source);
    }

    if (_pending != 0 && ++_waited > 6 && (GateStopped() || _waited > TimeoutRuns))
    {
        IGC.SendUnicastMessage(_replyTo, Tag, "ready");
        _status = (_waited > TimeoutRuns ? "Timed out opening " : "Open: ") + _pendingName;
        _pending = 0;
    }
    Echo("Dock Gate\n" + _status);
}

void Request(string command, long id, long sender)
{
    GridTerminalSystem.GetBlocksOfType(_blocks, b => b.EntityId == id && b.IsSameConstructAs(Me) && b is IMyShipConnector);
    if (_blocks.Count == 0)
        return;     // another base
    string name = _blocks[0].CustomName;
    if (command == "open")
    {
        if (_opened.Contains(id))
        {
            IGC.SendUnicastMessage(sender, Tag, "ready");
            return;
        }
        _opened.Add(id);
        Trigger("Dock Open", name);
        IGC.SendUnicastMessage(sender, Tag, "busy");
        _pending = id;
        _pendingName = name;
        _replyTo = sender;
        _waited = 0;
        _angles.Clear();
        _status = "Opening for " + name;
    }
    else if (command == "close" && _opened.Remove(id))
    {
        Trigger("Dock Close", name);
        _status = "Closed: " + name;
    }
}

// Does a name like "Dock Open Hangar A" belong to this connector?
static bool Matches(string name, string key, string connector)
{
    int at = name.IndexOf(key, StringComparison.OrdinalIgnoreCase);
    if (at < 0)
        return false;
    string rest = (name.Substring(0, at) + name.Substring(at + key.Length)).Trim();
    return connector == null || rest == "" || connector.IndexOf(rest, StringComparison.OrdinalIgnoreCase) >= 0;
}

void Trigger(string key, string connector)
{
    var timers = new List<IMyTimerBlock>();
    GridTerminalSystem.GetBlocksOfType(timers, t => t.IsSameConstructAs(Me) && Matches(t.CustomName, key, connector));
    foreach (IMyTimerBlock t in timers)
        t.Trigger();
}

// All gate blocks of the pending dock at rest: doors open, pistons not
// moving, rotors and hinges no longer turning.
bool GateStopped()
{
    _blocks.Clear();
    GridTerminalSystem.GetBlockGroups(_groups, g => Matches(g.Name, "Dock Gate", _pendingName));
    var buffer = new List<IMyTerminalBlock>();
    foreach (IMyBlockGroup g in _groups)
    {
        g.GetBlocks(buffer);
        _blocks.AddRange(buffer);
    }
    GridTerminalSystem.GetBlocksOfType(buffer, b => b.IsSameConstructAs(Me) && Matches(b.CustomName, "Dock Gate", _pendingName));
    _blocks.AddRange(buffer);
    bool stopped = true;
    foreach (IMyTerminalBlock b in _blocks)
    {
        IMyDoor door = b as IMyDoor;
        IMyPistonBase piston = b as IMyPistonBase;
        IMyMotorStator rotor = b as IMyMotorStator;
        if (door != null && door.Status != DoorStatus.Open)
            stopped = false;
        if (piston != null && (piston.Status == PistonStatus.Extending || piston.Status == PistonStatus.Retracting))
            stopped = false;
        if (rotor != null)
        {
            float last;
            if (!_angles.TryGetValue(b.EntityId, out last) || Math.Abs(rotor.Angle - last) > 0.002f)
                stopped = false;
            _angles[b.EntityId] = rotor.Angle;
        }
    }
    return stopped;
}
