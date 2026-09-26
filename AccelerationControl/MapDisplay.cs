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
    // Sprite rendering of the ore map: radar view and deposit list.
    // Layouts are designed for a 512 unit wide screen and scaled to the surface.
    partial class Program
    {
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
    }
}
