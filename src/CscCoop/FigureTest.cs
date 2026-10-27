using System.Collections.Generic;
using System.IO;
using CoopCore;
using UnityEngine;

namespace CscCoop;

// CSCCOOP_TEST=figure: how the client looks on the host's screen. The client walks, holds the mop and uses
// it, holds the sponge, pretends to hold the axe and the power washer lance (ToolLook.ForceTool), carries body 0 and throws it.
// The host logs what it sees (tools/figure_report.py) and saves pictures of the client's figure from a camera
// of its own (local/shot_figure_*.png): near plane 1.6 m, so walls between it and the figure don't show.
public partial class Bot
{
    float handMin = float.MaxValue, handMax = float.MinValue;
    readonly Queue<(string name, Vector3 at, Vector3 look)> shots = new();
    Camera shotCam; RenderTexture shotRt; int shotFrames; string shotName;

    void FigureTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        var g = CoopRunner.Instance.Ghost;
        var body = w.Bodies.Count > 0 ? w.Bodies[0] : null;
        var inv = InventoryController.Instance;
        switch (step)
        {
            case 0 when t > 2:
                walkFrom = w.Player.transform.position; walkDir = w.Player.transform.forward;
                step = 1; break;
            case 1 when t > 3.6f:
                if (host && g != null) { Plugin.L.LogInfo($"FIGURETEST figure: {g.Vertices} vertices, {g.Root.GetComponent<SkinnedMeshRenderer>()?.bones?.Length} bones"); Shot("walk", g, Vector3.right, 1.0f); }
                step = 2; break;
            case 2 when t > 5:
                if (!host && inv != null)
                {
                    inv.Equip(2);   // the save's hotbar: 2 = mop (tool probe)
                    var cam = w.Player.mainCamera != null ? w.Player.mainCamera.transform : null;
                    Plugin.L.LogInfo($"FIGURETEST client equipped '{inv.SelectedItem?.TryCast<UsableItem>()?.name}', eyes {(cam != null ? cam.position.y - w.Player.transform.position.y : -1):F2} m above the feet");
                }
                step = 3; break;
            case 3 when t > 8:
                if (host && g != null)
                {
                    LogTool(g, "mop");
                    Shot("mop_front", g, Vector3.forward, 1.1f); Shot("mop_side", g, Vector3.right, 1.1f);
                }
                if (!host) ToolLook.ForceUse = true;
                step = 4; break;
            case 4 when t > 9: step = 5; break;
            case 5 when t > 11:
                if (host && g != null) { Plugin.L.LogInfo($"FIGURETEST use swing: right hand travelled {handMax - handMin:F2} m forward and back"); Shot("mop_use", g, Vector3.right, 1.1f); }
                if (!host) { ToolLook.ForceUse = false; inv?.Equip(3); }   // 3 = sponge (tool probe)
                step = 50; break;
            case 50 when t > 13.5f:
                if (host && g != null) { LogTool(g, "sponge"); Shot("sponge", g, (Vector3.forward + Vector3.right).normalized, 1.1f); }
                if (!host) ToolLook.ForceTool = (short)ItemEquipper.AXE_TOOL_ID;
                step = 6; break;
            case 6 when t > 16:
                if (host && g != null) { LogTool(g, "axe"); Shot("axe", g, (Vector3.forward + Vector3.right).normalized, 1.1f); }
                if (!host) ToolLook.ForceTool = PlayerPose.ToolLance;
                step = 7; break;
            case 7 when t > 18.5f:
                if (host && g != null) { LogTool(g, "lance"); Shot("lance", g, (Vector3.forward + Vector3.right).normalized, 1.1f); }
                step = 8; break;
            case 8 when t > 20:   // its own step: the host's lance picture is taken before the client walks off
                if (!host)
                {
                    ToolLook.ForceTool = short.MinValue;
                    inv?.Equip(0);
                    if (body != null) ApproachPoint(w, body.RbToBones[0].bone.transform.position);
                }
                step = 9; break;
            case 9 when t > 22:
                if (!host && body != null)
                {
                    var part = body.GetComponentInChildren<BodyPartController>();
                    w.Grabber.BodyGrabber.GrabBody(part, w.Grabber.animator);
                    Plugin.L.LogInfo("FIGURETEST client grabs body 0");
                }
                step = 10; break;
            case 10 when t > 26:
                if (!host && body != null) Plugin.L.LogInfo($"FIGURETEST client holds body 0: {body.IsGrabbed}");
                if (host && g != null)
                {
                    Plugin.L.LogInfo($"FIGURETEST shoulder: shouldered {Bodies.IsShouldered(0)} gap {Bodies.LastShoulderGap:F2} m, bones ignored {Bodies.BonesIgnored}, shoulder frames {Bodies.ShoulderFrames}");
                    Shot("body_front", g, Vector3.forward, 1.2f); Shot("body_side", g, Vector3.right, 1.2f); Shot("body_back", g, -Vector3.forward, 1.2f);
                }
                step = 11; break;
            case 11 when t > 28:
                if (!host && body != null && body.IsGrabbed) { Plugin.L.LogInfo("FIGURETEST client throws body 0"); w.Grabber.TryReleaseBody(8); }
                step = 12; break;
            case 12 when t > 34.5f:
                if (body != null)
                {
                    var p = body.RbToBones[0].bone.transform.position;
                    Plugin.L.LogInfo($"FIGURETEST final body 0 root {p.x:F3} {p.y:F3} {p.z:F3} shouldered {Bodies.IsShouldered(0)} bones applied {Bodies.BonesApplied}");
                }
                step = 13; break;
        }
        if (!host && step >= 1 && step <= 2 && t < 5) Walk(w, Mathf.Max(0, t - 2) * 1.3f);
        // The use swing: the right hand's travel along the figure's forward while the client "uses" the mop.
        if (host && g != null && step == 5)
        {
            float z = g.Root.transform.InverseTransformPoint(g.HandPosition(1)).z;
            handMin = Mathf.Min(handMin, z); handMax = Mathf.Max(handMax, z);
        }
        TickShots();
    }

    void LogTool(PlayerFigure g, string what)
    {
        float gap = Vector3.Distance(g.HandPosition(1), g.Tool.Centre);
        Plugin.L.LogInfo($"FIGURETEST tool {what}: look '{g.Tool.SourceName}' renderers {g.Tool.Renderers} length {g.Tool.Length:F2} m, right hand {gap:F2} m from its centre");
    }

    // dir: from the figure toward the camera, in the figure's space. Turned in 45 degree steps until nothing
    // stands within 1.1 m of the figure that way (nearer to the camera, the near plane cuts it away).
    void Shot(string name, PlayerFigure g, Vector3 dir, float height)
    {
        var t = g.Root.transform;
        var look = t.position + Vector3.up * height;
        var d = t.TransformDirection(dir);
        for (int k = 0; k < 8; k++)
        {
            var dk = Quaternion.Euler(0, (k + 1) / 2 * 45 * (k % 2 == 0 ? 1 : -1), 0) * d;
            if (!Physics.Raycast(look, dk, 1.1f, ~0, QueryTriggerInteraction.Ignore)) { d = dk; break; }
        }
        shots.Enqueue((name, look + d * 2.7f + Vector3.up * 0.25f, look));
    }

    void TickShots()
    {
        if (shotCam == null)
        {
            if (shots.Count == 0) return;
            var s = shots.Dequeue();
            shotName = s.name;
            var go = new GameObject("FigureShotCam");
            go.transform.position = s.at;
            go.transform.rotation = Quaternion.LookRotation(s.look - s.at);
            shotCam = go.AddComponent<Camera>();
            shotCam.nearClipPlane = 1.6f; shotCam.farClipPlane = 60; shotCam.fieldOfView = 50;
            shotCam.cullingMask = ~((1 << 8) | (1 << 5));   // not the first-person arms, not UI
            shotRt = new RenderTexture(960, 720, 24);
            shotCam.targetTexture = shotRt;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point; light.range = 8; light.intensity = 1.5f;
            shotFrames = 0;
            return;
        }
        if (++shotFrames < 3) return;
        var prev = RenderTexture.active;
        RenderTexture.active = shotRt;
        var tex = new Texture2D(shotRt.width, shotRt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, shotRt.width, shotRt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        var png = ImageConversion.EncodeToPNG(tex);
        string dir = Path.GetDirectoryName(Env.Get("CSCCOOP_CSV"));
        string path = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, $"shot_figure_{shotName}.png");
        File.WriteAllBytes(path, (byte[])png);
        Plugin.L.LogInfo($"FIGURETEST shot written {path} ({png.Length} bytes)");
        shotCam.targetTexture = null;
        Object.Destroy(shotCam.gameObject); Object.Destroy(shotRt); Object.Destroy(tex);
        shotCam = null;
    }
}
