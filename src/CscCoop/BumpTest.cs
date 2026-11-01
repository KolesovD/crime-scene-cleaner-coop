using System;
using System.Globalization;
using System.Text;
using CoopCore;
using UnityEngine;

namespace CscCoop;

// bump suite (tools/run-pair.ps1 -Test bump [-Mission 11]): the client throws an item sideways into a resting item
// the host simulates. Before, the other player's resting items were kinematic here, so it bounced off as off a wall
// and nothing moved. Now: the target must be knocked away on both machines and end at the same spot, and every
// item in the scene must lie at the same spot on both (no copy left settled somewhere else).
public partial class Bot
{
    int bmTarget = -1, bmStriker = -1;
    Vector3 bmStart;

    void BumpTest(float t, Session ses, GameWorld w)
    {
        switch (step)
        {
            case 0 when t > 3:
                // the same roles on both machines: two active, loose, not breakable items of some weight, the
                // target with free floor around it
                for (int i = 0; i < w.Items.Count && bmStriker < 0; i++)
                {
                    var g = w.Items[i];
                    if (!Loose(g, w, i) || g.GetComponent<DestroyableItem>() != null) continue;
                    if (bmTarget < 0) { if (ses.OwnerOf(i) == Session.HostNumber && Clear(g)) bmTarget = i; }
                    else bmStriker = i;
                }
                if (bmTarget >= 0) bmStart = w.Items[bmTarget].transform.position;
                Plugin.L.LogInfo($"BUMP roles target {bmTarget} ({(bmTarget >= 0 ? w.Items[bmTarget].name : "-")}) striker {bmStriker} ({(bmStriker >= 0 ? w.Items[bmStriker].name : "-")}) at {P3(bmStart)}");
                step = 1; break;
            case 1 when t > 6:
                if (!ses.IsHost && bmTarget >= 0 && bmStriker >= 0)
                {
                    var tc = w.Items[bmTarget].GetComponentInChildren<Collider>();
                    var target = tc != null ? tc.bounds.center : w.Items[bmTarget].transform.position;
                    var sc = w.Items[bmStriker].GetComponentInChildren<Collider>();
                    float gap = (tc != null ? tc.bounds.extents.x : 0.2f) + (sc != null ? sc.bounds.extents.x : 0.2f) + 0.6f;
                    var cc = w.Player.GetComponent<CharacterController>();
                    if (cc != null) cc.enabled = false;
                    w.Player.transform.position = target + new Vector3(-1.5f, 0.1f, 0f);
                    if (cc != null) cc.enabled = true;
                    var x = w.Items[bmStriker];
                    w.Grabber.GrabItem(x);
                    bool held = w.Grabber.GrabbedRb != null;
                    // sideways into it, in the same frame (a held item is let go when the player is moved)
                    var off = sc != null ? x.transform.position - sc.bounds.center : Vector3.zero;
                    x.transform.position = target + off + new Vector3(-gap, 0f, 0f);
                    if (held) w.Grabber.TryRelease(Vector3.right * 6f * (x.Rb != null ? x.Rb.mass : 1f));
                    if (x.Rb != null) { x.Rb.isKinematic = false; x.Rb.velocity = Vector3.right * 6f; }
                    Plugin.L.LogInfo($"BUMP client threw striker {bmStriker} into target {bmTarget} (held {held}); owner of the target {ses.OwnerOf(bmTarget)}");
                }
                step = 10; break;
            case >= 10 and < 18 when t > 6 + (step - 9) * 0.25f:
                if (bmTarget >= 0) Plugin.L.LogInfo(FormattableString.Invariant($"BUMP t {t:F2} target {w.Describe(bmTarget)}"));
                step = step == 17 ? 2 : step + 1; break;
            case 2 when t > 8:
                if (bmTarget >= 0) Plugin.L.LogInfo(FormattableString.Invariant($"BUMP soon target moved {Vector3.Distance(w.Items[bmTarget].transform.position, bmStart):F2} m, owner {ses.OwnerOf(bmTarget)}, bumped here {GameWorld.Bumped}"));
                step = 3; break;
            case 3 when t > 30:
                BumpReport(ses, w);
                Plugin.L.LogInfo("BUMP dump done");
                step = 4; break;
        }
    }

    // The game's own kinematic state, not the session's (same roles on both).
    static bool Loose(GrabableItemController g, GameWorld w, int i) => g != null && g.gameObject.activeInHierarchy && g.Rb != null && !w.GameKinematic(i) && g.Rb.mass >= 0.5f && g.Rb.mass <= 20f;

    // No scenery on the side it gets pushed (+x), so it can move. Only static scenery counts: items and players
    // stand a little differently on each machine, and the two bots must pick the same roles.
    static bool Clear(GrabableItemController g)
    {
        var p = g.transform.position;
        foreach (var c in Physics.OverlapSphere(p + Vector3.right * 0.5f + Vector3.up * 0.1f, 0.35f))
            if (c != null && !c.isTrigger && c.attachedRigidbody == null && !(c is CharacterController)) return false;
        return Physics.Raycast(p + Vector3.up * 0.1f, Vector3.down, 0.6f);
    }

    void BumpReport(Session ses, GameWorld w)
    {
        var ci = CultureInfo.InvariantCulture;
        var all = new StringBuilder();
        for (int i = 0; i < w.Items.Count; i++)
        {
            var g = w.Items[i];
            if (g == null || !g.gameObject.activeInHierarchy) continue;
            var p = g.transform.position;
            all.Append(i).Append('=').Append(string.Format(ci, "{0:F2}/{1:F2}/{2:F2}", p.x, p.y, p.z)).Append(';');
        }
        var tp = bmTarget >= 0 ? w.Items[bmTarget].transform.position : Vector3.zero;
        var sp = bmStriker >= 0 ? w.Items[bmStriker].transform.position : Vector3.zero;
        Plugin.L.LogInfo(FormattableString.Invariant($"BUMP final target {bmTarget} at {P3(tp)} moved {Vector3.Distance(tp, bmStart):F2} striker at {P3(sp)} bumped {GameWorld.Bumped} woken {GameWorld.Woken} owner {ses.OwnerOf(bmTarget)}"));
        Plugin.L.LogInfo($"BUMP items {all}");
    }
}
