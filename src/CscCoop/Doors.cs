using System.IO;
using HarmonyLib;

namespace CscCoop;

// Doors, keys and evidence. All state events, so a late joiner gets the current state.
//  - Door (plain tweened doors): only menu/hub have them; handler kept for OnPlain, not patched (see below).
//  - ShortcutDoors (locked, key and one-side doors, card readers): UnlockDoors, OpenDoors, CloseDoors.
//  - DoorKey.CollectKey: the key goes to everyone's KeyHolder, so anyone can open its door.
//  - InspectableItem.Collect (evidence): collected for everyone.
[HarmonyPatch]
public static class Doors
{
    public static GameWorld.Table<Door> Plain;
    public static GameWorld.Table<ShortcutDoors> Shortcuts;
    public static GameWorld.Table<DoorKey> Keys;
    public static GameWorld.Table<InspectableItem> Evidence;
    public static int Applied;

    public static void Register(GameWorld w)
    {
        Plain = w.Register<Door>("doors");
        Shortcuts = w.Register<ShortcutDoors>("locked doors");
        Keys = w.Register<DoorKey>("keys");
        Evidence = w.Register<InspectableItem>("evidence");
    }

    const uint PlainKey = 0x30000, UnlockKey = 0x40000, ShortcutOpenKey = 0x50000, KeyKey = 0x60000, EvidenceKey = 0x70000;

    // ---- plain doors ----

    // Not patched: no job scene has a plain Door (Dealer's Apartment: 0); they are in the menu and hub, where
    // the game's own Door.CrackOpen throws a NullReferenceException, and a Harmony patch on it turned that
    // into an exception swallowed at the interop trampoline (153 of them per start), changing what runs after.

    static void SendPlain(Door d)
    {
        if (!WorldEvents.Live) return;
        int i = Plain.IndexOf(d);
        if (i < 0) return;
        bool closed = d.isClosed;
        WorldEvents.Send(Ev.DoorPlain, w => { w.Write((ushort)i); w.Write(closed); }, PlainKey + (uint)i);
    }

    public static void OnPlain(BinaryReader r)
    {
        int i = r.ReadUInt16(); bool closed = r.ReadBoolean();
        var d = Plain[i];
        if (d == null || d.isClosed == closed) return;
        if (closed) d.Close(); else d.Open();
        Applied++;
    }

    // ---- locked / shortcut doors ----

    [HarmonyPatch(typeof(ShortcutDoors), nameof(ShortcutDoors.UnlockDoors))] [HarmonyPostfix]
    static void Unlock(ShortcutDoors __instance)
    {
        if (!WorldEvents.Live) return;
        int i = Shortcuts.IndexOf(__instance);
        if (i >= 0) WorldEvents.Send(Ev.DoorUnlock, w => w.Write((ushort)i), UnlockKey + (uint)i);
    }

    // A player's click is Interact -> OnOpen -> Toggle, which animates the door itself; pushing a door is
    // TryForceOpen/TryForceClose, a switch ExternalToggle. None of them goes through OpenDoors/CloseDoors, so only
    // scripted opens were shared and the doors drifted apart, after which a shared open/close landed on a door that
    // was the other way here (owner's test on Developers' Hell: open on the host, closed on the client, and the
    // host closing it opened it there). Every path sets isClosed at once (ISIL, field +224), so the state after each
    // is sent, once per change, and the others set it with OpenDoors/CloseDoors (never a toggle).
    [HarmonyPatch(typeof(ShortcutDoors), nameof(ShortcutDoors.OpenDoors))] [HarmonyPostfix] static void OpenS(ShortcutDoors __instance) => SendShortcut(__instance);
    [HarmonyPatch(typeof(ShortcutDoors), nameof(ShortcutDoors.CloseDoors))] [HarmonyPostfix] static void CloseS(ShortcutDoors __instance) => SendShortcut(__instance);
    [HarmonyPatch(typeof(ShortcutDoors), nameof(ShortcutDoors.Toggle))] [HarmonyPostfix] static void ToggleS(ShortcutDoors __instance) { if (!OldRule) SendShortcut(__instance); }
    [HarmonyPatch(typeof(ShortcutDoors), nameof(ShortcutDoors.TryForceOpen))] [HarmonyPostfix] static void ForceOpenS(ShortcutDoors __instance) { if (!OldRule) SendShortcut(__instance); }
    [HarmonyPatch(typeof(ShortcutDoors), nameof(ShortcutDoors.TryForceClose))] [HarmonyPostfix] static void ForceCloseS(ShortcutDoors __instance) { if (!OldRule) SendShortcut(__instance); }
    [HarmonyPatch(typeof(ShortcutDoors), nameof(ShortcutDoors.ExternalToggle))] [HarmonyPostfix] static void ExternalS(ShortcutDoors __instance) { if (!OldRule) SendShortcut(__instance); }
    [HarmonyPatch(typeof(ShortcutDoors), nameof(ShortcutDoors.ResetDoors))] [HarmonyPostfix] static void ResetS(ShortcutDoors __instance) { if (!OldRule) SendShortcut(__instance); }

    static bool OldRule => Env.Get("CSCCOOP_MUTANT") == "door-old";   // only OpenDoors/CloseDoors, as in 0.4.2
    static readonly System.Collections.Generic.Dictionary<int, bool> sentOpen = new();

    static void SendShortcut(ShortcutDoors d)
    {
        if (!WorldEvents.Live) return;
        int i = Shortcuts.IndexOf(d);
        if (i < 0) return;
        bool open = !d.isClosed;
        if (sentOpen.TryGetValue(i, out var was) && was == open) return;
        sentOpen[i] = open;
        WorldEvents.Send(Ev.DoorShortcut, w => { w.Write((ushort)i); w.Write(open); }, ShortcutOpenKey + (uint)i);
        Plugin.L.LogInfo($"COOP door {i} {(open ? "opened" : "closed")}");
    }

    public static void OnScene() => sentOpen.Clear();

    public static void OnUnlock(BinaryReader r)
    {
        var d = Shortcuts[r.ReadUInt16()];
        if (d == null || !d.IsLocked) return;
        d.UnlockDoors();
        Applied++;
    }

    public static void OnShortcut(BinaryReader r)
    {
        int i = r.ReadUInt16(); bool open = r.ReadBoolean();
        var d = Shortcuts[i];
        Plugin.L.LogInfo($"COOP door {i} {(open ? "opened" : "closed")} by another player{(d == null ? ", not here" : "")}");
        if (d == null) return;
        sentOpen[i] = open;
        if (d.isClosed != open) return;   // already that way here
        if (open) d.OpenDoors(); else d.CloseDoors();
        Applied++;
    }

    // ---- keys and evidence ----

    // A key reaches the player through KeyHolder.CollectKey (ItemInspector.OnKeyCollected); DoorKey.CollectKey
    // alone doesn't put it in the holder (doors suite). Both are watched; the event gives the others both.
    [HarmonyPatch(typeof(DoorKey), nameof(DoorKey.CollectKey))] [HarmonyPostfix]
    static void CollectKey(DoorKey __instance) => SendKey(__instance);

    [HarmonyPatch(typeof(KeyHolder), nameof(KeyHolder.CollectKey))] [HarmonyPostfix]
    static void HolderCollect(DoorKey key) => SendKey(key);

    static void SendKey(DoorKey k)
    {
        if (!WorldEvents.Live) return;
        int i = Keys.IndexOf(k);
        if (i >= 0) WorldEvents.Send(Ev.KeyCollect, w => w.Write((ushort)i), KeyKey + (uint)i);
    }

    public static void OnKey(BinaryReader r)
    {
        var k = Keys[r.ReadUInt16()];
        var holder = KeyHolder.Instance;
        if (k == null || holder == null) return;
        if (holder.keys == null || !holder.keys.Contains(k)) holder.CollectKey(k);
        if (k.gameObject.activeInHierarchy) k.CollectKey();
        Applied++;
    }

    [HarmonyPatch(typeof(InspectableItem), nameof(InspectableItem.Collect))] [HarmonyPostfix]
    static void CollectEvidence(InspectableItem __instance)
    {
        if (!WorldEvents.Live) return;
        int i = Evidence.IndexOf(__instance);
        if (i >= 0) WorldEvents.Send(Ev.EvidenceCollect, w => w.Write((ushort)i), EvidenceKey + (uint)i);
    }

    public static void OnEvidence(BinaryReader r)
    {
        var e = Evidence[r.ReadUInt16()];
        if (e == null || e.isCollected) return;
        e.Collect();
        Applied++;
    }
}
