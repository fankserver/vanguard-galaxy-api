using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckForgeUiScale(IForgeUiService ui, Mouse mouse, List<ForgeSelectionSnapshot> calls)
    {
        var root = GameObject.Find("Mod API Forge actions");
        var canvas = root.GetComponentInParent<Canvas>().rootCanvas;
        var scaler = canvas.GetComponent<CanvasScaler>() ?? throw new InvalidOperationException("Native Forge canvas scaler missing.");
        var mode = scaler.uiScaleMode; var factor = scaler.scaleFactor; var canvasFactor = canvas.scaleFactor;
        var initialCalls = calls.Count;
        try
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize; scaler.scaleFactor = 1.25f;
            foreach (var frame in Wait(() => Math.Abs(canvas.scaleFactor - 1.25f) < .01f && ForgeGraphicsReady(), "Scaled Forge rendering")) yield return frame;
            CheckForgeBand();
            foreach (var frame in CaptureForgeActions("forge-ui-scaled")) yield return frame;
            var selected = ui.Current!.SelectedRecipe;
            foreach (var frame in ForgeClick(mouse, ForgeProbeButton("Forge probe first").transform)) yield return frame;
            Require(calls.Count == initialCalls + 1 && calls[calls.Count - 1].SelectedRecipe.Equals(selected), "Scaled Forge pointer did not activate its selection.");
            scaler.scaleFactor = 20f;
            foreach (var frame in Wait(() => Math.Abs(canvas.scaleFactor - 20f) < .01f && GameObject.Find("Mod API Forge actions") == null, "Undersized Forge band hidden")) yield return frame;
            Require(ui.Current != null, "Temporary geometry refusal disabled the Forge service.");
        }
        finally { scaler.scaleFactor = factor; scaler.uiScaleMode = mode; }
        foreach (var frame in Wait(() => Math.Abs(canvas.scaleFactor - canvasFactor) < .01f && ForgeGraphicsReady(), "Restored Forge rendering")) yield return frame;
        Require(scaler.uiScaleMode == mode && scaler.scaleFactor == factor, "Copied canvas scale was not restored.");
        CheckForgeBand();
        var restoredSelection = ui.Current!.SelectedRecipe;
        foreach (var frame in ForgeClick(mouse, ForgeProbeButton("Forge probe first").transform)) yield return frame;
        Require(calls.Count == initialCalls + 2 && calls[calls.Count - 1].SelectedRecipe.Equals(restoredSelection), "Forge registrations did not recover after temporary geometry refusal.");
        Passed("Scaled Forge input and non-overlap, temporary geometry refusal and registration recovery with scale restoration");
    }
}
