using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CscCoop;

// UV lamps light blood through a shader matrix of a few sources: each UVLampController takes a slot when enabled
// (CleaningSenseController.TryGetSenseIndex into cleanerSenseIndex, -1 when none is left) and returns it when
// disabled (ReturnIndex, only for slots above 0). The pool has 3 slots (1..3, lamps suite). The other player's UV
// lamps are copies here, so with two lamps each one of the four got none, and whichever lamps came out first kept
// theirs (owner's test: lamps over the limit didn't light blood). Slots go to the lamps that matter most here:
// own lamps that are on, then the other player's that are on, then own that are off, then the other's.
static class UvSlots
{
    static float next;
    static CleaningSenseController sense;
    public static int Moved;

    static int Rank(UVLampController u, bool own) => (u.isOn ? 2 : 0) + (own ? 1 : 0);

    public static void Tick()
    {
        if (Time.unscaledTime < next || Env.Get("CSCCOOP_MUTANT") == "uv-first") return;
        next = Time.unscaledTime + 0.5f;
        if (sense == null) sense = Object.FindObjectOfType<CleaningSenseController>();
        if (sense == null) return;
        var lamps = new List<(UVLampController u, int rank)>();
        foreach (var u in Object.FindObjectsOfType<UVLampController>())
        {
            if (u == null || !u.isActiveAndEnabled) continue;
            var pi = u.GetComponentInParent<PortableItem>();
            bool own = pi == null || !Dyn.IsPuppet(pi.gameObject);
            lamps.Add((u, Rank(u, own)));
        }
        foreach (var (u, rank) in lamps.OrderByDescending(x => x.rank))
        {
            if (u.cleanerSenseIndex >= 0) continue;
            int idx = -1;
            if (!sense.TryGetSenseIndex(out idx))
            {
                // none free: take one from the lowest-ranked lamp below this one that holds a slot above 0
                var from = lamps.Where(x => x.rank < rank && x.u.cleanerSenseIndex > 0).OrderBy(x => x.rank).Select(x => x.u).FirstOrDefault();
                if (from == null) continue;
                sense.ReturnIndex(from.cleanerSenseIndex);
                from.cleanerSenseIndex = -1;
                if (!sense.TryGetSenseIndex(out idx)) continue;
            }
            u.cleanerSenseIndex = idx;
            Moved++;
            Plugin.L.LogInfo($"COOP UV lamp '{u.transform.root.name}' (rank {rank}) now has light slot {idx}");
        }
    }
}
