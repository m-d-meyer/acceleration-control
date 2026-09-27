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
        const float PlaneTilt = 0.5f;       // vertical squash of the radar plane
        const float HeightScale = 0.85f;    // height stems relative to plane scale
        const float MaxStem = 95;           // longest height stem (units)
        const float ListRowHeight = 34;
        const int RadarLabels = 5;          // labelled deposits besides the selection
        const int CircleSegments = 48;

        // High contrast: the game adds glare and reflections on top of LCDs,
        // which washes out dark greys and mid tones.
        static readonly Color BgColor = new Color(0, 4, 8);
        static readonly Color PanelColor = new Color(6, 20, 28);
        static readonly Color GridColor = new Color(50, 125, 150);
        static readonly Color GridFaint = new Color(34, 88, 108);
        static readonly Color Cyan = new Color(90, 225, 255);
        static readonly Color TextColor = new Color(245, 252, 255);
        static readonly Color DimColor = new Color(170, 205, 220);
        const float MinLinePixels = 1.6f;   // thinner lines blur away on low-resolution screens
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
        float _layoutWidth = 512;  // width of the layout in units (wide screens: more than 512)
        float _maxStem = MaxStem;
        bool _compact;              // low-resolution wide screen
        const float CompactPixels = 200;    // wide screens lower than this (px) get the compact layout
        string _mapScreenInfo = "";
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
            RectangleF viewport = BeginSprites(surface);
            _mapScreenInfo = string.Format("Map screen: texture {0}x{1} px, used {2}x{3} px", surface.TextureSize.X, surface.TextureSize.Y,
                surface.SurfaceSize.X, surface.SurfaceSize.Y);
            using (MySpriteDrawFrame frame = surface.DrawFrame())
            {
                _frame = frame;
                // Sprites are cached by the game; changing the list forces a redraw.
                if (_frameToggle)
                    frame.Add(new MySprite());

                float width = viewport.Width, height = viewport.Height;
                bool active = view == _view;
                _layoutWidth = 512;
                _compact = false;
                if (view == MapView.Radar && width >= height * 1.5f && height < CompactPixels)
                {
                    // Wide and low resolution (e.g. cockpit screens with a 256 px texture):
                    // 200 units high, fewer and larger texts, only the selection labelled.
                    _compact = true;
                    _u = height / 200f;
                    _origin = viewport.Position;
                    float w = _layoutWidth = width / _u;
                    float radarWidth = Math.Min(200, w - 190), x = radarWidth + 6, panelWidth = w - x - 6;
                    DrawRadar(radarWidth / 2, 36 + (200 - 36) / 2f + 6, Math.Min(radarWidth / 2 - 6, 90), true);
                    Rect(x - 4, 37, w - x + 4, 163, BgColor);
                    DrawCompactPanel(x, 40, panelWidth, 80);
                    DrawButtons(RadarButtons, x, 126, panelWidth, 3, 34, active);
                    if (active)
                        DrawDialog(200);
                }
                else if (view == MapView.Radar && width >= height * 1.5f)
                {
                    // Wide screen: 300 units high, radar left, info and buttons right,
                    // so everything is drawn larger than the square layout fitted in.
                    _u = height / 300f;
                    _origin = viewport.Position;
                    float w = _layoutWidth = width / _u;
                    float radarWidth = Math.Min(300, w - 250), x = radarWidth + 6, panelWidth = w - x - 6;
                    DrawRadar(radarWidth / 2, 44 + (300 - 44) / 2f + 10, Math.Min(radarWidth / 2 - 10, 130), true);
                    Rect(x - 4, 45, w - x + 4, 255, BgColor);
                    DrawSelectionPanel(x, 50, panelWidth, 158);
                    DrawButtons(RadarButtons, x, 213, panelWidth, 3, 40, active);
                    if (active)
                        DrawDialog(300);
                }
                else if (view == MapView.Radar)
                {
                    _u = Math.Min(width, height) / 512f;
                    _origin = viewport.Position + new Vector2((width - 512 * _u) / 2, (height - 512 * _u) / 2);
                    DrawRadar(256, 180, RadarRadius, false);
                    DrawButtons(RadarButtons, 4, 452, 504, RadarButtons.Length, 52, active);
                    if (active)
                        DrawDialog(512);
                }
                else
                {
                    _u = width / 512f;
                    _origin = viewport.Position;
                    float h = height / _u;
                    DrawList(h);
                    DrawButtons(ListButtons, 4, h - 56, 504, ListButtons.Length, 52, active);
                    if (active)
                        DrawDialog(h);
                }
            }
        }

        void DrawHeader(float height, string right)
        {
            Rect(0, 0, _layoutWidth, height, PanelColor);
            Text("ORE MAP", 12, height / 2 - 15, 0.95f, Cyan);
            float x = _layoutWidth - 12;
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

        // cx, cy, radius: where the radar plane is drawn (layout units). Wide screens
        // put the info panel beside the radar instead of below it.
        void DrawRadar(float cx, float cy, float radius, bool wide)
        {
            IMyShipController reference = _controller ?? _layoutController;
            _maxStem = radius * MaxStem / RadarRadius;
            float scale = radius / (float)MapRange;       // units per meter
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
                    if (Math.Abs(edge.X - cx) < radius && Math.Abs(edge.Y - cy) < radius * PlaneTilt + 20)
                        Text("GRAVITY " + FormatDistance(Vector3D.Distance(o.Center, shipPos) - o.GravityRadius),
                            edge.X, edge.Y + 4, 0.55f, GravityColor, TextAlignment.CENTER);
                }
            }

            // Range rings and axes
            EllipseOutline(cx, cy, radius, radius * PlaneTilt, 1.5f, GridColor, false);
            EllipseOutline(cx, cy, radius / 2, radius / 2 * PlaneTilt, 1, GridFaint, false);
            EllipseOutline(cx, cy, radius / 4, radius / 4 * PlaneTilt, 1, GridFaint, false);
            Line(cx - radius, cy, cx + radius, cy, 1, GridFaint);
            Line(cx, cy - radius * PlaneTilt, cx, cy + radius * PlaneTilt, 1, GridFaint);
            if (!_compact)      // the header shows the range; small texts would be unreadable there
            {
                Text(FormatDistance(MapRange), cx + radius * 0.74f, cy - radius * PlaneTilt * 0.74f - 20, 0.5f, DimColor, TextAlignment.LEFT);
                Text(FormatDistance(MapRange / 2), cx + radius * 0.37f, cy - radius * PlaneTilt * 0.37f - 18, 0.45f, DimColor, TextAlignment.LEFT);
            }

            // Asteroids and deposits, far ones first
            _mapItems.Clear();
            foreach (Obstacle o in _obstacles)
                if (!o.Planet)
                    _mapItems.Add(new MapItem { Rock = o, Local = ToLocal(o.Center, shipPos, ship) });
            foreach (Deposit d in _visibleDeposits)
                if (d.Zone == _zone)
                    _mapItems.Add(new MapItem { Deposit = d, Local = ToLocal(d.Position, shipPos, ship) });
            _mapItems.Sort((a, b) => b.Local.Z.CompareTo(a.Local.Z));

            // Active route (or the previewed one) as a dashed line with waypoints
            bool flying = _mode == Mode.Approach;
            List<Vector3D> route = flying ? _route : _previewRoute;
            float px = cx, py = cy;
            for (int i = flying ? _routeIndex : 0; i < route.Count; i++)
            {
                Vector2 point = ProjectedPoint(ToLocal(route[i], shipPos, ship), cx, cy, scale);
                Dashed(px, py, point.X, point.Y, 3, flying ? RouteColor : RouteColor * 0.7f);
                DiamondOutline(point.X, point.Y, 8, RouteColor);
                if (i < route.Count - 1 && i < (flying ? _routeIndex : 0) + 4)     // planet routes have many
                    Text("W" + (i + 1), point.X - 12, point.Y - 8, 0.55f, RouteColor, TextAlignment.RIGHT);
                px = point.X;
                py = point.Y;
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
            DrawHeader(_compact ? 34 : 44, (inGravity ? "IN GRAVITY  " : "") + FormatDistance(MapRange) + "  " + (_filter ?? "all"));
            if (wide)
                return;
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
            if (!selected && (_compact || _visibleDeposits.IndexOf(d) >= RadarLabels))
                return;
            string name = d.Number > 0 ? ShortOre(d.Ore) + d.Number : d.Ore.Length > 10 ? d.Ore.Substring(0, 10) : d.Ore;
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

            if (_mode == Mode.Jump || _mode == Mode.Dock)
            {
                bool jump = _mode == Mode.Jump;
                Text(jump ? "JUMP  " + FormatDistance(_jumpDistance) : "DOCKING", left, y + 6, 0.8f, jump ? JumpColor : Cyan);
                Text(jump ? _jumpState : DockPhaseText(),
                    left, y + 40, 0.65f, TextColor);
                Text(string.Format("heading error {0:0.0}°", MathHelper.ToDegrees((float)_alignError)), left, y + 68, 0.55f, DimColor);
            }
            else if (_mode == Mode.Approach)
            {
                string leg = _route.Count > 1 && !_probing ? "  " + (_routeIndex + 1) + "/" + _route.Count : "";
                Text("> " + _targetName + leg, left, y + 6, 0.8f, RouteColor);
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
                Text(_selected.Zone == _zone ? DirectionText(_selected.Position, false) : "GO: flight via the zone change",
                    left, y + 40, 0.6f, DimColor);

                bool narrow = width < 400;     // info panel beside the radar on wide screens
                if (_previewRoute.Count > 0 && _previewName == _selected.Label)
                    Text(narrow ? string.Format("{0} legs  {1}  {2}", _previewRoute.Count, FormatDistance(_routeLength), FormatTime(_routeTime))
                        : string.Format("Route {0} legs  {1}  {2:0} m/s  {3}", _previewRoute.Count, FormatDistance(_routeLength),
                        _routeDeltaV, FormatTime(_routeTime)), left, y + 64, 0.6f, RouteColor);
                else
                {
                    Vector3D from = ReferencePosition();
                    Obstacle blocking = BlockingObstacle(from, StopPoint(from, _selected.Position));
                    Text(blocking == null ? "Direct path clear" : "Route goes around " + (blocking.Planet ? "a planet" : "an asteroid"),
                        left, y + 64, 0.6f, blocking == null ? TextColor : RouteColor);
                }

                double total = _deltaVHydrogen + _deltaVElectric;
                if (total > 0)
                {
                    // Narrow: the numbers go below the bar.
                    float barW = narrow ? width - 60 : width - 230;
                    Text("dv", left, y + 90, 0.6f, TextColor);
                    Box(left + 32, y + 94, barW, 14, 1, GridColor);
                    double needed = _previewRoute.Count > 0 && _previewName == _selected.Label ? _routeDeltaV : TripDeltaV();
                    Rect(left + 33, y + 95, (barW - 2) * (float)Math.Min(needed / total, 1), 12, needed > total ? WarnColor : RouteColor);
                    Text(string.Format("{0:0} / {1:0} m/s", needed, total), right, y + (narrow ? 110 : 90), 0.55f, TextColor, TextAlignment.RIGHT);
                }
            }

            if (_message.Length > 0)
                Text(_message, left, y + height - 26, 0.52f, DimColor);
        }

        // Three large lines for low-resolution screens: what is selected or flown
        // to, the most important detail, and the last message.
        void DrawCompactPanel(float x, float y, float width, float height)
        {
            Rect(x, y, width, height, PanelColor);
            Box(x, y, width, height, 1, GridColor);
            float left = x + 8, right = x + width - 8;
            string title = "", detail = "";
            Color color = RouteColor;
            if (_mode == Mode.Jump)
            {
                title = "JUMP " + FormatDistance(_jumpDistance);
                detail = _jumpState;
                color = JumpColor;
            }
            else if (_mode == Mode.Dock)
            {
                title = "DOCKING";
                detail = DockPhaseText();
                color = Cyan;
            }
            else if (_mode == Mode.Approach)
            {
                title = "> " + _targetName;
                detail = _approachPhase + "  " + FormatDistance(_remainingDistance) + "  " + _currentSpeed.ToString("0") + " m/s";
            }
            else if (_selected != null)
            {
                title = _selected.Label;
                detail = FormatDistance(_selected.Distance) + "  " + (_selected.Zone == _zone ? DirectionText(_selected.Position, true) : "");
            }
            else
                title = "No entries";
            TextFit(title, left, y + 4, 0.85f, right - left, color);
            TextFit(detail, left, y + 32, 0.68f, right - left, TextColor);
            if (_message.Length > 0)
                TextFit(_message, left, y + 56, 0.56f, right - left, DimColor);
        }

        // Text shrunk to fit a width (down to 75 %), then shortened.
        void TextFit(string text, float x, float y, float scale, float width, Color color)
        {
            float measured = MeasureText(text, scale, "White");
            if (measured > width)
            {
                float fitted = Math.Max(scale * width / measured, scale * 0.75f);
                string shown = text;
                for (int n = text.Length - 1; n > 3 && MeasureText(shown, fitted, "White") > width; n--)
                    shown = text.Substring(0, n) + "..";
                text = shown;
                scale = fitted;
            }
            Text(text, x, y, scale, color);
        }

        // Distance to the target with the stopping distance marked: braking
        // starts when the orange mark reaches the end of the bar.
        void DrawApproachGauge(float x, float y, float width)
        {
            bool braking = _approachPhase == "BRAKING";
            float scale = width < 380 ? 0.5f : 0.6f;       // narrow panel beside the radar
            Text(_approachPhase, x, y, scale, braking ? RouteColor : Cyan);
            Text("stop " + FormatDistance(_stopDistance) + " / " + FormatDistance(_remainingDistance),
                x + width, y, scale, TextColor, TextAlignment.RIGHT);
            float by = y + 26, bh = 18;
            double full = Math.Max(Math.Max(_remainingDistance, _stopDistance), 1);
            Box(x, by, width, bh, 1, GridColor);
            Rect(x + 1, by + 1, (width - 2) * (float)(_remainingDistance / full), bh - 2, Cyan * 0.6f);
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
                if (d.Zone != _zone)
                {
                    y += ListRowHeight;
                    continue;
                }
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
            if (_mode == Mode.Jump)
                Text("JUMP " + FormatDistance(_jumpDistance) + "  " + _jumpState, 10, fy + 6, 0.66f, JumpColor);
            else if (_mode == Mode.Dock)
                Text("DOCKING  " + DockPhaseText(), 10, fy + 6, 0.66f, Cyan);
            else if (_mode == Mode.Approach)
            {
                Text(_approachPhase + "  " + FormatDistance(_remainingDistance), 10, fy + 6, 0.66f, _approachPhase == "BRAKING" ? RouteColor : Cyan);
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

        // Buttons in rows of 'columns'; labels shrink if a button is too narrow.
        void DrawButtons(string[] buttons, float left, float top, float totalWidth, int columns, float height, bool active)
        {
            float gap = 5, width = (totalWidth - gap * (columns - 1)) / columns;
            for (int i = 0; i < buttons.Length; i++)
            {
                float x = left + i % columns * (width + gap), y = top + i / columns * (height + gap);
                bool highlighted = active && i == _button && _dialog == Dialog.None;
                Rect(x, y, width, height, highlighted ? Cyan : PanelColor);
                Box(x, y, width, height, 1.5f, active ? Cyan : GridColor);
                float scale = Math.Min(0.72f, 0.72f * (width - 10) / Math.Max(MeasureText(buttons[i], 0.72f, "White"), 1));
                Text(buttons[i], x + width / 2, y + height / 2 - 19 * scale, scale, highlighted ? BgColor : active ? Cyan : DimColor, TextAlignment.CENTER);
            }
        }

        void DrawDialog(float height)
        {
            if (_dialog == Dialog.None)
                return;
            float w = Math.Min(432, _layoutWidth - 16), x = (_layoutWidth - w) / 2, y = 48;
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

        Vector2 ProjectedPoint(Vector3D local, float cx, float cy, float scale)
        {
            float height = MathHelper.Clamp((float)local.Y * scale * HeightScale, -_maxStem, _maxStem);
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
            t = Math.Max(t, MinLinePixels / _u);
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
                new Vector2(length * _u, Math.Max(thickness * _u, MinLinePixels)), color, null, TextAlignment.CENTER, (float)Math.Atan2(d.Y, d.X)));
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
            MySprite sprite = MySprite.CreateText(text, font, color, TextPixelScale(scale), alignment);
            sprite.Position = P(x, y);
            _frame.Add(sprite);
        }

        float MeasureText(string text, float scale, string font)
        {
            _measure.Clear().Append(text);
            return _surface.MeasureStringInPixels(_measure, font, TextPixelScale(scale)).X / _u;
        }

        // Font scale on the surface, with ScreenTextScale from Custom Data applied.
        float TextPixelScale(float scale)
        {
            return scale * _u * _screenTextScale;
        }
    }
}
