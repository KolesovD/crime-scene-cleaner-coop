using System;
using CoopCore;
using UnityEngine;

namespace CscCoop;

// breakhit suite (tools/run-pair.ps1 -Test breakhit): the client throws an item straight down onto a resting
// breakable that the host owns. On the client the falling item is real and the breakable a puppet; on the host it
// is the other way round, and both see the contact. The breakable must break once, its shards simulated by one
// machine only, and the shards must end at the same spots on both screens.
public partial class Bot
{
    int bhB = -1, bhItem = -1, bhX = -1;

    static string P3(Vector3 v) => FormattableString.Invariant($"{v.x:F2}/{v.y:F2}/{v.z:F2}");

    void BreakHitTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        switch (step)
        {
            case 0 when t > 3:
                // the same roles on both machines: the first active breakable item with shards, and the first
                // other active item that is not breakable
                for (int i = 0; i < Collectibles.Breakables.Count && bhB < 0; i++)
                {
                    var d = Collectibles.Breakables[i];
                    if (d == null || !d.gameObject.activeInHierarchy || d.destroyedModel == null || d.grabableItemController == null) continue;
                    int it = w.IndexOf(d.grabableItemController);
                    if (it >= 0) { bhB = i; bhItem = it; }
                }
                for (int i = 0; i < w.Items.Count && bhX < 0; i++)
                {
                    var g = w.Items[i];
                    if (g == null || i == bhItem || !g.gameObject.activeInHierarchy || g.GetComponent<DestroyableItem>() != null || g.Rb == null || g.Rb.mass < 0.5f) continue;
                    bhX = i;
                }
                Plugin.L.LogInfo($"BREAKHIT roles breakable {bhB} (item {bhItem}) striker {bhX}");
                step = 1; break;
            case 1 when t > 5:
                if (!host && bhX >= 0 && bhItem >= 0)
                {
                    var target = w.Items[bhItem].transform.position;
                    var cc = w.Player.GetComponent<CharacterController>();
                    if (cc != null) cc.enabled = false;
                    w.Player.transform.position = target + new Vector3(0.8f, 0.3f, 0f);
                    if (cc != null) cc.enabled = true;
                    var x = w.Items[bhX];
                    w.Grabber.GrabItem(x);
                    bool held = w.Grabber.GrabbedRb != null;
                    // straight down onto it, in the same frame (a held item is let go when the player is moved)
                    x.transform.position = target + Vector3.up * 0.6f;
                    if (held) w.Grabber.TryRelease(Vector3.down * 10f * (x.Rb != null ? x.Rb.mass : 1f));
                    if (x.Rb != null) { x.Rb.isKinematic = false; x.Rb.velocity = Vector3.down * 10f; }
                    Plugin.L.LogInfo($"BREAKHIT client threw striker {bhX} down onto breakable {bhB} (held {held}), owner of the breakable {ses.OwnerOf(bhItem)}, of the striker {ses.OwnerOf(bhX)}");
                }
                step = 3; break;
            case 3 when t > 18:
                Report("early");
                // the host picks up one of the shards the client simulates, with the trash can's own call
                if (host && bhB >= 0)
                {
                    var shards = Collectibles.Breakables[bhB].CreatedCracks;
                    var s2 = shards != null && shards.Count > 2 ? shards[2] : null;
                    var ci = s2 != null ? s2.GetComponent<CollectableItem>() : null;
                    bool other = s2 != null && Dyn.IsPuppet(s2.gameObject);
                    if (ci != null) { ci.Collect(); s2.gameObject.SetActive(false); }
                    Plugin.L.LogInfo($"BREAKHIT host collected shard 2 (simulated by the other player {other}): {(ci != null ? "yes" : "no")}");
                }
                step = 4; break;
            case 4 when t > 28:
                Report("final");
                Plugin.L.LogInfo("BREAKHIT dump done");
                step = 5; break;
        }
    }

    void Report(string when)
    {
        if (bhB < 0) { Plugin.L.LogInfo($"BREAKHIT {when} no roles"); return; }
        var d = Collectibles.Breakables[bhB];
        var shards = d != null ? d.CreatedCracks : null;
        int n = shards?.Count ?? 0, mine = 0;
        var pos = new System.Text.StringBuilder();
        for (int k = 0; k < n; k++)
        {
            var s = shards[k];
            if (s == null) { pos.Append(k == 0 ? "" : ";").Append("gone"); continue; }
            if (s.gameObject.activeInHierarchy && !Dyn.IsPuppet(s.gameObject)) mine++;
            pos.Append(k == 0 ? "" : ";").Append(s.gameObject.activeInHierarchy ? P3(s.transform.position) : "off");
        }
        Plugin.L.LogInfo($"BREAKHIT {when} cracked {(d != null && d.WasCracked)} shards {n} simulatedHere {mine} at {(n == 0 ? "-" : pos.ToString())}");
    }
}
