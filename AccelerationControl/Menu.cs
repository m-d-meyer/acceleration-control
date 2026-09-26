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
    // Button menu of the map screens, operated from the toolbar:
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

        string[] CurrentButtons { get { return _view == MapView.Radar ? RadarButtons : ListButtons; } }
        double MapRange { get { return ZoomLevels[_zoomIndex]; } }

        void HandleUiCommand(string value)
        {
            string[] buttons = CurrentButtons;
            switch (value)
            {
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
                    _message = "Usage: ui left|right|up|down|ok|back";
                    break;
            }
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
