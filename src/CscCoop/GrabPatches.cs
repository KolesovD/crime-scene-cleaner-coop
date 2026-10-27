using HarmonyLib;

namespace CscCoop;

// Every grab goes through GrabController.GrabItem. A grab of an item another player holds is refused;
// any other grab claims the item for this player (CoopCore.Session.OnLocalGrab).
[HarmonyPatch]
static class GrabPatches
{
    [HarmonyPatch(typeof(GrabController), nameof(GrabController.GrabItem))]
    [HarmonyPrefix]
    static bool GrabItem(GrabableItemController item)
    {
        var run = CoopRunner.Instance;
        if (run?.Session == null || item == null) return true;
        int i = run.World.IndexOf(item);
        if (i < 0) return Dyn.OnLocalGrab(item);   // bags and tools that appeared during the job
        bool ok = run.Session.OnLocalGrab(i) || Env.Get("CSCCOOP_MUTANT") == "grab-always";
        Plugin.L.LogInfo($"COOP grab {i} ({item.name}): {(ok ? "allowed" : "refused, another player holds it")}");
        return ok;
    }

    // A mop or hand hit on an item another machine simulates: take it over first (the session's claim), so
    // the hit moves it here (owner's manual test: on the client the mop only nudged items, they slid back).
    [HarmonyPatch(typeof(GrabableItemController), nameof(GrabableItemController.OnToolPunch))] [HarmonyPrefix]
    static void ToolPunch(GrabableItemController __instance) => Claim(__instance);

    [HarmonyPatch(typeof(GrabableItemController), nameof(GrabableItemController.OnHandPunch))] [HarmonyPrefix]
    static void HandPunch(GrabableItemController __instance) => Claim(__instance);

    static void Claim(GrabableItemController item)
    {
        var run = CoopRunner.Instance;
        if (run?.Session == null || item == null || WorldEvents.Applying) return;
        int i = run.World.IndexOf(item);
        if (i < 0 || run.Session.OwnerOf(i) == run.Session.Me) return;
        run.Session.OnLocalGrab(i);
    }
}
