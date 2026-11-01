using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CscCoop;

// World machinery the owner wants shared (lifts, elevators, levers, switches, panels, conveyor knobs, ladders,
// TVs...): each job has its own mechanics, so this replays whitelisted method calls. When a player's action
// calls one of them on a scene object, the others call the same method on the same object (scene table by
// hierarchy path) with the same simple arguments (int, bool, float). Only the outermost whitelisted call is
// sent (a lever that moves a lift sends the lever, and the lift moves by itself on the other side too).
// Stream events: they repeat an action, they don't carry state; doors/drawers (Openable) and the old lift
// (MovingPlatform) are state events in Furniture.
public static class Actions
{
    // Type.Method. Interactable.Interact covers the many plain interactables (UnityEvent OnInteract). Types that
    // open a UI, a dialogue or a personal panel are left out on purpose (see Skip).
    static readonly string[] Whitelist =
    {
        "Interactable.Interact",
        // A click on a ShortcutDoors goes Interact -> TryOpen (first time) / Toggle, which tween by themselves, not
        // through OpenDoors (ISIL; owner's manual test). Interact itself is not replayed: it checks the clicking
        // player's side of the door, which the other player may not be on. Toggle is not here any more: replayed
        // as a toggle, any missed or doubled event left the door inverted for good (owner's test on Developers'
        // Hell); Doors shares the open/closed state after every door path instead. TryOpen only rattles.
        "ShortcutDoors.TryOpen",
        "LabLiftLever.Interact", "LabSecretLiftController.Interact", "LabLiftController.ChangeLiftPosition",
        "ExtendableLadder.Interact", "ElectrickShockBox.Interact", "ElectricShockLeaver.Interact",
        "ChangeObjectPositionQueue.Interact", "ElectronicPicture.Interact", "BathController.Interact",
        "RedButtonMethlab.Interact",
        "FloorButton.ActivateButton", "LiftPanelInside.OnPressButton", "WarehouseLift.TryAddingToQueue",
        "WaterElevator.OnElevatorCall", "WaterElevator.ManualOpen", "ElevatorDoor.SummonElevator",
        "WarehousePowerSwitch.TrySwitchingPower", "WarehousePowerSwitch.UseCardInput",
        "WarehouseConveyorPanel.OnLeftKnobSwitch", "WarehouseConveyorPanel.OnRightKnobSwitch", "WarehouseConveyorPanel.OnStopBelt",
        "CranePanel.PressDownButton", "CranePanel.PressUpButton",
        // Interactables that override Interact: the base patch never sees them (interactables audit after the
        // owner's three-mission test). Lamps: the scene's switchable lamps; the UV lamp is everyone's own sight.
        "TvController.Interact", "CableTvController.Interact", "RadioController.Interact", "MusicBoxController.Interact",
        "LampController.Interact", "SoundOnInteract.Interact", "Ozonator.Interact",
        // Mission machinery found by going through every class the game saves (2026-10-26 audit). None has a
        // direct caller in code (UnityEvents), so the ones fired from an Interactable are already covered;
        // these catch the other paths. All one-way (idempotent): a trigger both players set off is harmless.
        "BoxBoxCageController.PullDownCage", "CryoChamber.OnPanelClick", "VacuumChamberControllerOld.TryActivateChamber",
        "PoolController.TryDrainPool", "PoolController.TryFillPool", "UfoButton.Press", "LockdownController.EndLockdown",
        "ObjectDestroyer.Destroy", "DoorsCardReader.Unlock",
        // Knocked-over furniture (the owner's shelf on the first job) is stood up by holding the fix button:
        // FurnitureFixController's Load coroutine tweens it, then calls OnFix, which sets the fixed pose and state.
        "FixableObject.OnFix", "FixableObjectChild.OnFix",
        // Found by the census of all jobs: washable writing (CleanableQuote: hides itself and fires its quest
        // event on the hit that cleans it) and the Spa lockers.
        "CleanableQuote.OnHit", "SpaLockerStateSaver.OnOpen",
        // Census, round two: hacking terminals (8 jobs; the minigame is personal, its result opens things for
        // everyone), lift call buttons on the floors, hold-to-use controls (Warehouse), the Lab's fuse power.
        "UniversalHackingPresenter.OnHackingEnd",
        "LiftFloorBase.OnPressButton", "LiftFloor.OnPressButton", "LiftFloorSimple.OnPressButton",
        "HoldInteractable.HoldInteract",
        "MadScientistFusesController.ChangeElevatorPower", "MadScientistFusesController.ChangeCryoPower",
        "MadScientistFusesController.ChangeBridgePower", "MadScientistFusesController.ChangeVaccumPower",
        // Story objects (owner's test of 0.4.0: the attic hatch in the first job and the Spa's walkie-talkie had to
        // be used by each player, and the attic stayed an open objective on the host). InteractablePanel is not a
        // UI: a click that fires its OnInteract story event (Dealer's Apartment 3, Spa 5, 1-14 in every job);
        // InteractableWithModalPanel asks yes/no and fires its event with the answer. Not in the hub, where the
        // laptop is an InteractablePanel too (see Post).
        "InteractablePanel.Interact", "InteractableWithModalPanel.SendResponse",
        // A body joins its "get rid of the bodies" count when a player finds it (BodyQuest.AddBodyToQuest; the van
        // counts only bodies added): found by one player, the other's van collect of it didn't count (bodies
        // suite: the host 2, the client 1 for one body each).
        "BodyQuest.AddBodyToQuest",
    };
    static readonly HashSet<string> Skip = new() { "OpenUIPanelOnInteract", "BarmanInteraction", "MusicDisc", "UVLampController" };

    sealed class Act
    {
        public int Id;
        public string Name;
        public MethodInfo Method;
        public Type Type;
        public GameWorld.ITable Table;
        public Func<Component, int> IndexOf;
        public Func<int, Component> Get;
    }

    static readonly List<Act> acts = new();
    static readonly Dictionary<MethodBase, Act> byMethod = new();
    static int depth;
    public static int Sent, Applied;

    public static void Register(GameWorld w, Harmony harmony)
    {
        var asm = typeof(Openable).Assembly;
        var tables = new Dictionary<Type, Act>();
        var entries = new List<string>(Whitelist);
        if (Env.Get("CSCCOOP_MUTANT") == "door-old") entries.Add("ShortcutDoors.Toggle");   // the 0.4.2 rule
        foreach (var entry in entries)
        {
            var dot = entry.IndexOf('.');
            var type = asm.GetType(entry.Substring(0, dot));
            var method = type == null ? null : AccessTools.DeclaredMethod(type, entry.Substring(dot + 1));
            if (method == null) { Plugin.L.LogWarning($"COOP action {entry} not found"); continue; }
            bool simple = true;
            foreach (var p in method.GetParameters())
                if (p.ParameterType != typeof(int) && p.ParameterType != typeof(bool) && p.ParameterType != typeof(float)) simple = false;
            if (!simple) { Plugin.L.LogWarning($"COOP action {entry} has complex arguments, not shared"); continue; }
            if (!tables.TryGetValue(type, out var proto))
            {
                // One scene table per type: GameWorld.Register<T> via reflection.
                var table = (GameWorld.ITable)typeof(GameWorld).GetMethod(nameof(GameWorld.Register)).MakeGenericMethod(type).Invoke(w, new object[] { "actions:" + type.Name });
                var tt = table.GetType();
                var indexOf = tt.GetMethod("IndexOf");
                var item = tt.GetProperty("Item");
                proto = new Act
                {
                    Type = type, Table = table,
                    IndexOf = c => { var typed = CastTo(c, type); return typed == null ? -1 : (int)indexOf.Invoke(table, new object[] { typed }); },
                    Get = i => (Component)item.GetValue(table, new object[] { i }),
                };
                tables[type] = proto;
            }
            var act = new Act { Id = acts.Count, Name = entry, Method = method, Type = type, Table = proto.Table, IndexOf = proto.IndexOf, Get = proto.Get };
            acts.Add(act);
            byMethod[method] = act;
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(Actions).GetMethod(nameof(Pre), BindingFlags.NonPublic | BindingFlags.Static)),
                postfix: new HarmonyMethod(typeof(Actions).GetMethod(nameof(Post), BindingFlags.NonPublic | BindingFlags.Static)));
        }
        Plugin.L.LogInfo($"COOP {acts.Count} world actions shared");
    }

    // Il2Cpp objects: a Component wrapper is turned into the declared type's wrapper (same native object), or
    // null if it isn't one. IL2CPP folds identical method bodies into one native function, so a patch on
    // Interactable.Interact also runs for unrelated calls (friend's log: "Can't cast StatsTracker to
    // Interactable"); those must be skipped, not cast.
    static readonly Dictionary<Type, MethodInfo> tryCast = new();
    static object CastTo(Component c, Type t)
    {
        if (c == null) return null;
        if (!tryCast.TryGetValue(t, out var m)) tryCast[t] = m = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(t);
        return m.Invoke(c, null);
    }

    // A player's own tool (PortableItem: lamps, buckets...) is in the scene table too, at the same place on every
    // machine, so a replay by index switched the other player's own lamp (owner's test: turning on my lamp 1 turned
    // on the friend's lamp 1). Tools are shared by Dyn, with the tool's own state.
    static bool InTool(Component c) => c.GetComponentInParent<PortableItem>() != null && Env.Get("CSCCOOP_MUTANT") != "lamp-index";

    static void Pre() => depth++;

    static void Post(object __instance, MethodBase __originalMethod, object[] __args)
    {
        depth--;
        if (depth != 0 || !WorldEvents.Live || __instance is not Component c) return;
        if (byMethod.TryGetValue(__originalMethod, out var a0) && CastTo(c, a0.Type) == null) return;   // a folded call on another type
        if (Skip.Contains(c.GetIl2CppType().Name)) return;
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name.Contains("Hub")) return;   // each player's own apartment
        if (InTool(c)) return;
        if (!byMethod.TryGetValue(__originalMethod, out var act)) return;
        int i = act.IndexOf(c);
        if (i < 0) return;
        WorldEvents.Send(Ev.Action, w =>
        {
            w.Write((ushort)act.Id); w.Write((ushort)i); w.Write((byte)__args.Length);
            foreach (var a in __args)
                switch (a)
                {
                    case int v: w.Write((byte)0); w.Write(v); break;
                    case bool v: w.Write((byte)1); w.Write(v); break;
                    case float v: w.Write((byte)2); w.Write(v); break;
                }
        });
        Sent++;
        Plugin.L.LogInfo($"COOP action {act.Name} on {act.Type.Name} {i}");
    }

    public static void Apply(BinaryReader r)
    {
        int id = r.ReadUInt16(), i = r.ReadUInt16(), n = r.ReadByte();
        var args = new object[n];
        for (int k = 0; k < n; k++)
            args[k] = r.ReadByte() switch { 0 => r.ReadInt32(), 1 => r.ReadBoolean(), _ => r.ReadSingle() };
        if ((uint)id >= (uint)acts.Count) return;
        var act = acts[id];
        var target = act.Get(i);
        if (target == null || InTool(target)) return;
        Plugin.L.LogInfo($"COOP action {act.Name} on {act.Type.Name} {i} from another player");
        var typed = CastTo(target, act.Type);
        if (typed == null) return;
        depth++;
        try { act.Method.Invoke(typed, args); }
        finally { depth--; }
        Applied++;
    }
}
