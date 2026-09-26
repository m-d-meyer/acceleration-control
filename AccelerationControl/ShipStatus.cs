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
    // Cargo, fuel and delta-v monitoring.
    //
    // Delta-v is estimated from the fuel on board:
    //   hydrogen: liters * (thrust per liter/s) / mass
    //   electric: stored energy (batteries + uranium) * (thrust per MW) / mass
    // Thrust per liter and energy per kg of uranium are calibrated while
    // flying, so modded thrusters and reactors are handled as well.
    partial class Program
    {
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
    }
}
