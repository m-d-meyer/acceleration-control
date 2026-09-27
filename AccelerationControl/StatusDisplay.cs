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
    // Graphical ship status page: a cargo card and a power / delta-v card,
    // side by side on wide screens and stacked on square ones.
    partial class Program
    {
        const float StatusRow = 38;

        static readonly Color BatteryColor = new Color(100, 220, 140);
        static readonly Color HydrogenColor = new Color(150, 200, 255);
        static readonly Color JumpColor = new Color(190, 130, 255);

        void DrawStatusSurface(IMyTextSurface surface)
        {
            RectangleF viewport = BeginSprites(surface);
            using (MySpriteDrawFrame frame = surface.DrawFrame())
            {
                _frame = frame;
                if (_frameToggle)
                    frame.Add(new MySprite());
                float width = viewport.Width, height = viewport.Height;
                _origin = viewport.Position;
                if (width >= height * 1.6f)
                {
                    _u = height / 256f;
                    float w = width / _u;
                    DrawCargoCard(6, 6, w / 2 - 9, 244);
                    DrawPowerCard(w / 2 + 3, 6, w / 2 - 9, 244);
                }
                else
                {
                    _u = width / 512f;
                    float half = (height / _u - 18) / 2;
                    DrawCargoCard(6, 6, 500, half);
                    DrawPowerCard(6, half + 12, 500, half);
                }
            }
        }

        // Switches a surface to sprite mode and returns the visible area.
        RectangleF BeginSprites(IMyTextSurface surface)
        {
            surface.ContentType = ContentType.SCRIPT;
            surface.Script = "";
            surface.ScriptBackgroundColor = BgColor;
            _surface = surface;
            return new RectangleF((surface.TextureSize - surface.SurfaceSize) / 2f, surface.SurfaceSize);
        }

        void Card(float x, float y, float w, float h, string title)
        {
            Rect(x, y, w, h, PanelColor);
            Box(x, y, w, h, 1, GridColor);
            Text(title, x + 10, y + 8, 0.55f, DimColor);
        }

        void DrawCargoCard(float x, float y, float w, float h)
        {
            Card(x, y, w, h, "CARGO");
            if (_cargoMaxVolume <= 0)
            {
                Text("No cargo containers", x + 10, y + 40, 0.6f, DimColor);
                return;
            }
            double fill = _cargoVolume / _cargoMaxVolume;
            Text((fill * 100).ToString("0") + "%", x + w - 10, y + 2, 0.95f, fill > 0.9 ? WarnColor : Cyan, TextAlignment.RIGHT);

            // Fill bar, split into the ores on board by mass
            float barX = x + 10, barW = w - 20, barY = y + 40;
            Box(barX, barY, barW, 22, 1, GridColor);
            _oreSorted.Clear();
            foreach (KeyValuePair<string, double> ore in _oreAmounts)
                _oreSorted.Add(ore);
            _oreSorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            float filled = (barW - 2) * (float)Math.Min(fill, 1), sx = barX + 1;
            double oreMass = 0;
            foreach (KeyValuePair<string, double> ore in _oreSorted)
            {
                float segment = _cargoMass > 0 ? filled * (float)(ore.Value / _cargoMass) : 0;
                Rect(sx, barY + 1, segment, 20, OreColor(ore.Key));
                sx += segment;
                oreMass += ore.Value;
            }
            Rect(sx, barY + 1, Math.Max(barX + 1 + filled - sx, 0), 20, DimColor * 0.6f);

            Text(FormatMass(_cargoMass), barX, y + 68, 0.6f, TextColor);
            Text(_cargoVolume.ToString("0.0") + " / " + _cargoMaxVolume.ToString("0") + " m³", x + w - 10, y + 68, 0.6f,
                DimColor, TextAlignment.RIGHT);

            float ry = y + 98;
            foreach (KeyValuePair<string, double> ore in _oreSorted)
            {
                if (ry + 24 > y + h)
                    break;
                Marker(x + 18, ry + 11, 5.5f, ore.Key, OreColor(ore.Key));
                Text(ore.Key, x + 30, ry, 0.62f, TextColor);
                Text(FormatMass(ore.Value), x + w - 10, ry, 0.62f, TextColor, TextAlignment.RIGHT);
                ry += 26;
            }
        }

        void DrawPowerCard(float x, float y, float w, float h)
        {
            Card(x, y, w, h, "POWER & FUEL");
            // Acceleration limit (up/down commands) in the card's title line.
            string limit = _enabled ? string.Format("limit {0:0.0} m/s² {1:0.00}g", _limit, _limit / 9.81) : "limit OFF";
            float room = w - 30 - MeasureText("POWER & FUEL", 0.55f, "White");
            float scale = Math.Min(0.55f, 0.55f * room / Math.Max(MeasureText(limit, 0.55f, "White"), 1));
            Text(limit, x + w - 10, y + 7, Math.Max(scale, 0.4f), _enabled ? Cyan : DimColor, TextAlignment.RIGHT);
            float ry = y + 34, bottom = y + h - 62;

            if (_batteryMax > 0 && ry < bottom)
            {
                bool draining = _batteryNetOutput > 1e-6;
                StatusLine(x, ry, w, "Battery", draining ? FormatTime(BatteryTimeRemaining()) : "+" + (-_batteryNetOutput).ToString("0.00") + " MW",
                    (_batteryStored / _batteryMax * 100).ToString("0") + "%", _batteryStored / _batteryMax, BatteryColor);
                ry += StatusRow;
            }
            if (_reactors.Count > 0 && ry < bottom)
            {
                StatusLine(x, ry, w, "Uranium", FormatTime(TimeRemaining(_uranium, _uraniumRate)), FormatMass(_uranium), -1, OreColor("Uranium"));
                ry += StatusRow;
            }
            if (_hydrogenCapacity > 0 && ry < bottom)
            {
                StatusLine(x, ry, w, "Hydrogen", FormatTime(TimeRemaining(_hydrogen, _hydrogenRate)),
                    (_hydrogen / _hydrogenCapacity * 100).ToString("0") + "%", _hydrogen / _hydrogenCapacity, HydrogenColor);
                ry += StatusRow;
            }
            if (_jumpMax > 0 && ry < bottom)
            {
                double charge = _jumpStored / _jumpMax;
                StatusLine(x, ry, w, "Jump", charge >= 0.999 ? "ready" : "charging", (charge * 100).ToString("0") + "%", charge, JumpColor);
                ry += StatusRow;
            }

            // Delta-v summary at the bottom of the card
            double total = _deltaVHydrogen + _deltaVElectric;
            float dy = y + h - 58;
            Line(x + 8, dy - 4, x + w - 8, dy - 4, 1, GridColor);
            Text("DELTA-V", x + 10, dy + 4, 0.5f, DimColor);
            bool calibrated = _hydrogenCalibrated && (_reactors.Count == 0 || _uraniumCalibrated);
            Text(total.ToString("0") + " m/s" + (calibrated ? "" : " *"), x + w - 10, dy, 0.8f, RouteColor, TextAlignment.RIGHT);
            Text(string.Format("{0:0.0} trips at {1:0} m/s", total / TripDeltaV(), _maxSpeed), x + 10, dy + 30, 0.52f,
                TextColor);
            if (!calibrated)
                Text("* estimate", x + w - 10, dy + 30, 0.5f, DimColor, TextAlignment.RIGHT);
        }

        // One line: label and detail on the left, value on the right, a thin bar below.
        void StatusLine(float x, float y, float w, string label, string detail, string value, double fraction, Color color)
        {
            Text(label, x + 10, y, 0.62f, TextColor);
            Text(detail, x + 10 + MeasureText(label, 0.62f, "White") + 10, y + 3, 0.5f, DimColor);
            Text(value, x + w - 10, y, 0.62f, color, TextAlignment.RIGHT);
            if (fraction < 0)
                return;
            Rect(x + 10, y + 25, w - 20, 6, GridFaint);
            Rect(x + 10, y + 25, (w - 20) * (float)MathHelper.Clamp(fraction, 0, 1), 6, color);
        }
    }
}
