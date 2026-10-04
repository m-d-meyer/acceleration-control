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
    // Button menu of the map screens.
    //
    // UI mode ('ui' toggles it) uses the movement keys while the ship holds
    // its position: W/S = up/down, A/D = left/right, Space = OK, C = back
    // (C outside a dialog leaves UI mode). Keys repeat when held.
    //
    // The same actions are available as commands for toolbar slots:
    //   ui left / ui right   highlight the previous / next button
    //   ui up / ui down      select the previous / next deposit (or list entry)
    //   ui ok                press the highlighted button
    //   ui back              close a dialog
    partial class Program
    {
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
                    _message = "Usage: ui [on|off|left|right|up|down|ok|back]";
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
                    PreviewRoute();
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
    }
}
