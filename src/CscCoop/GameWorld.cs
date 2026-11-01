using System;
using System.Collections.Generic;
using CoopCore;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CscCoop;

// IWorld over the real game: the local FirstPersonController and the scene's grabbable things.
// Session index space: [0, Items.Count) are GrabableItemControllers, then the bodies
// (BodyRagdollController, moved by their root bone; the other bones go as events, see Bodies).
public sealed class GameWorld : IWorld
{
    public FirstPersonController Player;
    public GrabController Grabber;
    public readonly List<GrabableItemController> Items = new();
    readonly Dictionary<IntPtr, int> indexOf = new();
    uint key;
    string scene = "";

    // Remote-driven items: target from the owner, and what the game had before we took over.
    struct Remote
    {
        public bool On; public bool WasKinematic, AnimWasOn; public Vector3 Pos; public Quaternion Rot; public Vector3 Vel; public bool Fresh;
        // Items only: Held from the owner; StillSince = when the owner's samples stopped moving; Local = the owner has
        // it at rest and this machine runs its physics (see Smooth); LocalAt/ClaimAt = when that started / we claimed it.
        public bool Held, Local; public float StillSince, LocalAt, ClaimAt;
        public int Claims; public float ClaimsSince, AwakeUntil;   // bump claims in the current minute; physics here until then
    }
    Remote[] remote = Array.Empty<Remote>();

    public uint SceneKey => key;
    // Hubs (HubResidence, Hub02): every player's own apartment from their own save. Shared by scene name only,
    // with no tables, so players see each other there and nothing else is synced (see WorldEvents).
    public bool InHub => scene.Contains("Hub");
    public string Scene => scene;
    public int ItemCount => Items.Count + Bodies.Count;

    // Called every frame by CoopRunner: finds the player and builds the item table once
    // the player can move (the job's intro is over, the scene is stable).
    public void Refresh()
    {
        string s = SceneManager.GetActiveScene().name;
        if (s != scene) { scene = s; Clear(); }
        if (Player == null || !Player.gameObject.activeInHierarchy)
        {
            if (key != 0) Clear();
            Player = UnityEngine.Object.FindObjectOfType<FirstPersonController>();
            Grabber = Player != null ? Player.GetComponentInChildren<GrabController>() : null;
            if (Grabber == null) Grabber = UnityEngine.Object.FindObjectOfType<GrabController>();
            return;
        }
        if (key == 0 && Player.CanMove) BuildTable();
    }

    void Clear()
    {
        key = 0; Items.Clear(); indexOf.Clear(); remote = Array.Empty<Remote>();
        lastPos = Array.Empty<Vector3>(); movedAt = Array.Empty<float>();
        Taints.Clear(); taintIndex.Clear(); Bodies.Clear(); bodyIndex.Clear(); Garbage.Clear(); garbageIndex.Clear();
        foreach (var t in tables) t.Clear();
        // The old job's fingerprints too: compared with the host's new job under the new scene name they "differed"
        // all 31 tables on the client while it was still loading (harmless, BuildTable reset it, but logged).
        Parts.Clear(); Off.Clear();
        Player = null; Grabber = null;
    }

    void BuildTable()
    {
        if (InHub)
        {
            key = Fnv("hub|" + scene, 2166136261);
            if (key == 0) key = 1;
            remote = Array.Empty<Remote>(); lastPos = Array.Empty<Vector3>(); movedAt = Array.Empty<float>();
            Plugin.L.LogInfo($"COOP table {scene}: hub, players only, key {key:X8}");
            return;
        }
        var all = UnityEngine.Object.FindObjectsOfType<GrabableItemController>(true);
        var rows = new List<(string k, GrabableItemController g)>();
        foreach (var g in all)
        {
            if (g == null || g.gameObject.scene.name != scene) continue;   // skips DontDestroyOnLoad objects
            // Each player's own tools (the bucket is a PortableItem under Player) differ with upgrades and skins;
            // in the table they would make the two keys differ and switch off the whole job's sync.
            // They are shared as dynamic objects instead (Dyn).
            if (g.GetComponent<PortableItem>() != null) continue;
            var p = g.startingWorldPosition;
            rows.Add(($"{Path(g.transform)}|{Mathf.RoundToInt(p.x * 100)},{Mathf.RoundToInt(p.y * 100)},{Mathf.RoundToInt(p.z * 100)}", g));
        }
        rows.Sort((a, b) => string.CompareOrdinal(a.k, b.k));
        uint h = Fnv(scene, 2166136261);
        foreach (var (k, g) in rows) { indexOf[g.Pointer] = Items.Count; Items.Add(g); h = Fnv(k, h); }
        if (DumpPath != "") System.IO.File.AppendAllLines(DumpPath, rows.ConvertAll(r => "Item " + r.k + (r.g.GetComponent<PortableItem>() != null ? " [portable]" : "")));
        // The scene key is the entity space (items and bodies), which positions and ownership are indexed by. Every
        // other table gets a fingerprint of its own, compared with the other players' (TableCheck): one that
        // differs is switched off alone. It used to go into the key, and one differing table switched off the whole
        // job (friend's test of 0.4.1, Villa: 535 items on both, keys 85C12853 / 155AA62C, nothing shared).
        Collect(Bodies, bodyIndex, null, ref h);
        Parts.Clear(); Off.Clear();
        uint th = 2166136261; Collect(Taints, taintIndex, null, ref th); Parts["stains"] = th;
        th = 2166136261; Collect(Garbage, garbageIndex, null, ref th); Parts["van items"] = th;
        foreach (var t in tables)
        {
            th = 2166136261; t.Build(this, ref th);
            if (Env.Get("CSCCOOP_MUTANT") == "table-differ" && Env.Get("CSCCOOP_ROLE") == "client" && t.Name == "cassettes") { th ^= 0x5A5A; Plugin.L.LogInfo("COOP mutant table-differ: cassettes made different"); }
            Parts[t.Name] = th;
        }
        if (h == 0) h = 1;
        key = h;
        remote = new Remote[ItemCount];
        lastPos = new Vector3[ItemCount]; movedAt = new float[ItemCount];
        bodyLetGo.Clear(); bodyHeld.Clear();
        Bodies_OnTable();
        TableCheck.OnBuilt(this);
        var more = new System.Text.StringBuilder();
        foreach (var t in tables) more.Append($", {t.Count} {t.Name}");
        Plugin.L.LogInfo($"COOP table {scene}: {Items.Count} items, {Taints.Count} stains, {Bodies.Count} bodies, {Garbage.Count} van items{more}, key {key:X8}");
    }

    static void Bodies_OnTable() { CscCoop.Bodies.OnTable(); Furniture.OnScene(); Doors.OnScene(); Objectives.OnScene(); Cctv.OnScene(); CscCoop.Bodies.OnScene(); }
    // Scene tables of the other shared things, identified by indexed hierarchy path.
    public interface ITable { string Name { get; } int Count { get; } void Build(GameWorld w, ref uint h); void Clear(); }
    public sealed class Table<T> : ITable where T : Component
    {
        public string Name { get; }
        public readonly List<T> List = new();
        readonly Dictionary<IntPtr, int> index = new();
        public Table(string name) { Name = name; }
        public int Count => List.Count;
        GameWorld world;
        bool IsOff => world != null && world.Off.Contains(Name);
        public int IndexOf(T c) => !IsOff && c != null && index.TryGetValue(c.Pointer, out var i) ? i : -1;
        public T this[int i] => !IsOff && (uint)i < (uint)List.Count ? List[i] : null;
        public void Build(GameWorld w, ref uint h) { world = w; w.Collect(List, index, null, ref h); }
        public void Clear() { List.Clear(); index.Clear(); }
    }
    readonly List<ITable> tables = new();
    public Table<T> Register<T>(string name) where T : Component { var t = new Table<T>(name); tables.Add(t); return t; }

    // Fingerprints of the tables outside the scene key, and the ones another player's differ in (not shared).
    public readonly Dictionary<string, uint> Parts = new();
    public readonly HashSet<string> Off = new();
    public bool IsOff(string table) => Off.Contains(table);

    public readonly List<BodyRagdollController> Bodies = new();
    readonly Dictionary<IntPtr, int> bodyIndex = new();
    public int BodyIndex(BodyRagdollController b) => b != null && bodyIndex.TryGetValue(b.Pointer, out var i) ? i : -1;
    public int BodyEntity(BodyRagdollController b) { int i = BodyIndex(b); return i < 0 ? -1 : Items.Count + i; }
    public BodyRagdollController BodyOf(int entity) => entity >= Items.Count && entity < ItemCount ? Bodies[entity - Items.Count] : null;

    public readonly List<GarbageItem> Garbage = new();
    readonly Dictionary<IntPtr, int> garbageIndex = new();
    public int GarbageIndex(GarbageItem g) => !Off.Contains("van items") && g != null && garbageIndex.TryGetValue(g.Pointer, out var i) ? i : -1;

    public readonly List<Taint> Taints = new();
    readonly Dictionary<IntPtr, int> taintIndex = new();
    public int TaintIndex(Taint t) => !Off.Contains("stains") && t != null && taintIndex.TryGetValue(t.Pointer, out var i) ? i : -1;

    // pos == null: identify by hierarchy path with sibling numbers only. Stains need that: the same 383 stains
    // came out with different positions on two copies (some sit on things physics moves), keys differed.
    internal void Collect<T>(List<T> into, Dictionary<IntPtr, int> index, Func<T, Vector3> pos, ref uint h) where T : Component
    {
        into.Clear(); index.Clear();
        var rows = new List<(string k, T c)>();
        foreach (var c in UnityEngine.Object.FindObjectsOfType<T>(true))
        {
            if (c == null || c.gameObject.scene.name != scene) continue;
            // Each player's own tools (lamps, buckets: PortableItem) are no scene objects: they differ per player,
            // so a table with them differed and was switched off, or matched and a replay by index switched the other
            // player's own lamp (owner's test). Dyn shares them.
            if (c.GetComponentInParent<PortableItem>() != null && Env.Get("CSCCOOP_MUTANT") != "lamp-index") continue;
            if (pos == null) { rows.Add((IndexedPath(c.transform), c)); continue; }
            var p = pos(c);
            rows.Add(($"{Path(c.transform)}|{Mathf.RoundToInt(p.x * 100)},{Mathf.RoundToInt(p.y * 100)},{Mathf.RoundToInt(p.z * 100)}", c));
        }
        rows.Sort((a, b) => string.CompareOrdinal(a.k, b.k));
        foreach (var (k, c) in rows) { index[c.Pointer] = into.Count; into.Add(c); h = Fnv(k, h); }
        if (DumpPath != "") System.IO.File.AppendAllLines(DumpPath, rows.ConvertAll(r => typeof(T).Name + " " + r.k));
    }

    public static string IndexedPathOf(Transform t) => IndexedPath(t);

    // The object at an IndexedPath in the active scene (null if this machine's hierarchy differs).
    // Segments end in "#<sibling index>": split there, not at every '/', since object names can hold one
    // (Developers' Hell: the floors are under "WALLS/WINDOWS/FLOORS", so no stain on them was found there:
    // footprints and blood on those floors never appeared for the other player).
    static readonly System.Text.RegularExpressions.Regex Segment = new(@"(.+?)#(\d+)(?:/|$)");

    public static GameObject FindByIndexedPath(string path)
    {
        Transform cur = null;
        foreach (System.Text.RegularExpressions.Match m in Segment.Matches(path))
        {
            int idx = int.Parse(m.Groups[2].Value);
            string name = m.Groups[1].Value;
            Transform next = null;
            if (cur == null)
            {
                foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                    if (root.transform.GetSiblingIndex() == idx && root.name == name) { next = root.transform; break; }
            }
            else if (idx < cur.childCount && cur.GetChild(idx).name == name) next = cur.GetChild(idx);
            if (next == null) return null;
            cur = next;
        }
        return cur != null ? cur.gameObject : null;
    }

    static string IndexedPath(Transform t)
    {
        var s = $"{t.name}#{t.GetSiblingIndex()}";
        for (var p = t.parent; p != null; p = p.parent) s = $"{p.name}#{p.GetSiblingIndex()}/" + s;
        return s;
    }

    // CSCCOOP_DUMP_KEYS=<file>: writes every table row, to find what differs between two machines.
    public static string DumpPath = Env.Get("CSCCOOP_DUMP_KEYS");

    static string Path(Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    static uint Fnv(string s, uint h)
    {
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    public int IndexOf(GrabableItemController g) => g != null && indexOf.TryGetValue(g.Pointer, out var i) ? i : -1;

    public bool LocalPose(out PlayerPose pose)
    {
        pose = default;
        if (Player == null || key == 0) return false;
        var t = Player.transform;
        var cam = Player.mainCamera != null ? Player.mainCamera.transform : t;
        pose.Pos = V(t.position);
        pose.Yaw = t.eulerAngles.y;
        pose.Pitch = cam.eulerAngles.x;
        pose.HeldItem = (short)(Grabber != null ? IndexOf(Grabber.GrabbedRb) : -1);
        // A body: its entity, so the others can put it on this player's shoulder (Bodies.Shoulder).
        // grabbedBody, not IsGrabbingBody: that stays false while the grab tweens in.
        var body = Grabber != null && Grabber.BodyGrabber != null ? Grabber.BodyGrabber.grabbedBody : null;
        int be = body != null ? BodyEntity(body) : -1;
        if (pose.HeldItem < 0 && be >= 0 && be < short.MaxValue) pose.HeldItem = (short)be;
        if (pose.HeldItem < 0 && Grabber != null && (Grabber.GrabbedRb != null || Grabber.IsGrabbingBody)) pose.HeldItem = short.MaxValue;   // carrying a bag
        ToolLook.Sample(t, ref pose);
        return true;
    }

    static Rigidbody RootRb(BodyRagdollController b)
    {
        var bones = b.RbToBones;
        return bones != null && bones.Count > 0 ? bones[0].rb : null;
    }

    // What the session moves: an item's transform, or a body's root bone.
    Transform EntityTransform(int i)
    {
        if (i < Items.Count) return Items[i] != null ? Items[i].transform : null;
        var b = BodyOf(i);
        var rb = b != null ? RootRb(b) : null;
        return rb != null ? rb.transform : null;
    }

    // When each entity last moved (> 1 mm in a frame). "At rest" for a kinematic body or item means not moving
    // for StillTime, not "kinematic": the game holds a thrown item or body kinematic for a moment after the
    // throw, and treating that as rest handed it back mid-air (owner's manual test: thrown boxes jumped back,
    // thrown bodies hung in the air).
    Vector3[] lastPos = Array.Empty<Vector3>();
    float[] movedAt = Array.Empty<float>();
    const float StillTime = 0.3f;

    public void Track()
    {
        float now = Time.unscaledTime;
        for (int i = 0; i < lastPos.Length; i++)
        {
            var t = EntityTransform(i);
            if (t == null) continue;
            var p = t.position;
            if ((p - lastPos[i]).sqrMagnitude > 1e-6f) { lastPos[i] = p; movedAt[i] = now; }
        }
    }

    bool StillFor(int i) => (uint)i < (uint)movedAt.Length && Time.unscaledTime - movedAt[i] >= StillTime;

    public ItemSample ReadItem(int i)
    {
        if (i >= Items.Count) return ReadBody(BodyOf(i), i);
        var g = Items[i];
        if (g == null) return default;
        var rb = g.Rb;
        var t = g.transform;
        return new ItemSample
        {
            Pos = V(t.position), Rot = Q(t.rotation),
            Vel = rb != null && !rb.isKinematic ? V(rb.velocity) : default,
            Held = g.IsHeld,
            Sleeping = rb == null || !g.gameObject.activeInHierarchy || (rb.isKinematic ? StillFor(i) : rb.IsSleeping()),
        };
    }

    // A thrown body's bones report sleeping while it still hangs in the air (they had been put to sleep while
    // kinematic): the owner took it for at rest, handed it over, and it froze mid-air on both machines (owner's
    // two-PC test; bodies suite with CSCCOOP_THROW=25: root at 1.52 m, 0.59 m/s, 11 of 11 bones "sleeping").
    // For a few seconds after it is let go its bones are kept awake, and at rest means sleeping AND not moving.
    readonly Dictionary<int, float> bodyLetGo = new();
    readonly HashSet<int> bodyHeld = new();
    const float WakeAfterThrow = 4f;

    ItemSample ReadBody(BodyRagdollController b, int i)
    {
        var rb = b != null ? RootRb(b) : null;
        if (rb == null) return default;
        if (b.IsGrabbed) bodyHeld.Add(i);
        else if (bodyHeld.Remove(i)) bodyLetGo[i] = Time.unscaledTime;
        bool justThrown = bodyLetGo.TryGetValue(i, out var at) && Time.unscaledTime - at < WakeAfterThrow;
        bool asleep = true, anyDynamic = false;
        foreach (var bone in b.RbToBones)
        {
            if (bone.rb == null || bone.rb.isKinematic) continue;
            anyDynamic = true;
            if (justThrown) bone.rb.WakeUp();
            if (!bone.rb.IsSleeping()) asleep = false;
        }
        asleep = !justThrown && (anyDynamic ? asleep : true) && StillFor(i);   // sleeping and not moving
        var t = rb.transform;
        return new ItemSample
        {
            Pos = V(t.position), Rot = Q(t.rotation),
            Vel = !rb.isKinematic ? V(rb.velocity) : default,
            Held = b.IsGrabbed,
            Sleeping = asleep || !b.gameObject.activeInHierarchy,
        };
    }

    public void ApplyItem(int i, in ItemSample s)
    {
        if ((uint)i >= (uint)remote.Length) return;
        ref var r = ref remote[i];
        var pos = U(s.Pos);
        if (!r.Fresh || (pos - r.Pos).sqrMagnitude > 4e-6f || Quaternion.Angle(r.Rot, U(s.Rot)) > 0.5f || s.Held) r.StillSince = Time.unscaledTime;
        r.Pos = pos; r.Rot = U(s.Rot); r.Vel = U(s.Vel); r.Held = s.Held;
        if (!r.Fresh) { r.Fresh = true; Snap(i); }
    }

    public void SetItemAuthority(int i, bool local)
    {
        if (i >= Items.Count) { SetBodyAuthority(i, local); return; }
        var g = Items[i];
        if (g == null) return;
        ref var r = ref remote[i];
        var rb = g.Rb;
        if (!local && !r.On)
        {
            r.On = true; r.Fresh = false; r.Local = false; r.StillSince = Time.unscaledTime;
            r.Pos = g.transform.position; r.Rot = g.transform.rotation;
            if (rb != null) { r.WasKinematic = rb.isKinematic; rb.isKinematic = true; }
        }
        else if (local && r.On && r.Local)
        {
            // Our own physics already runs it (we bumped it and claimed it): keep it where it is, moving as it moves.
            r.On = false; r.Local = false;
        }
        else if (local && r.On)
        {
            r.On = false;
            // Start from the owner's last state, not from the smoothed copy, which trails it: taking over a
            // moving item from there made it jump back (owner's manual test).
            if (r.Fresh) Snap(i);
            if (rb != null)
            {
                rb.isKinematic = r.WasKinematic;
                if (!rb.isKinematic) { rb.velocity = r.Vel; rb.WakeUp(); }
            }
        }
    }

    // Bodies: the game's own SetKinematic switches every bone; remote-driven bodies are kinematic, their
    // root follows the session and the other bones follow Bodies' bone events.
    void SetBodyAuthority(int i, bool local)
    {
        var b = BodyOf(i);
        if (b == null) return;
        ref var r = ref remote[i];
        var rb = RootRb(b);
        if (rb != null && local != !r.On)
        {
            int dyn = 0, sleeping = 0;
            foreach (var bone in b.RbToBones) if (bone.rb != null && !bone.rb.isKinematic) { dyn++; if (bone.rb.IsSleeping()) sleeping++; }
            Plugin.L.LogInfo($"COOP body {i - Items.Count} {(local ? "now ours" : "now the other's")}: root y {rb.position.y:F2} vel {(rb.isKinematic ? 0 : rb.velocity.magnitude):F2}, dynamic bones {dyn} (sleeping {sleeping}) of {b.RbToBones.Count}, grabbed {b.IsGrabbed}, still {StillFor(i)}");
        }
        if (!local && !r.On)
        {
            r.On = true; r.Fresh = false;
            var t = EntityTransform(i);
            if (t != null) { r.Pos = t.position; r.Rot = t.rotation; }
            r.WasKinematic = rb == null || rb.isKinematic;
            b.SetKinematic(true);
            // The body's Animator would keep posing some bones here (bodies suite: the same 2 of 55 bones
            // ended 13-14 cm off), so the bone events alone pose a remote-driven body.
            r.AnimWasOn = b.animator != null && b.animator.enabled;
            if (b.animator != null) b.animator.enabled = false;
            // Blood from a body comes from its owner (Stains shares new stains); a puppet must not make its own.
            if (b.bloodSpreader != null) b.bloodSpreader.enabled = false;
        }
        else if (local && r.On)
        {
            if (r.Fresh) Snap(i);
            // Stays kinematic: a body handed back after it came to rest would otherwise settle again on
            // this machine only (bodies suite: 2 bones ended 13-14 cm apart). Grabbing it unfreezes it.
            r.On = false;
            if (b.bloodSpreader != null) b.bloodSpreader.enabled = true;
        }
    }

    // A body this machine is about to carry gets its physics back. A body taken over (or handed back at rest)
    // stays kinematic otherwise, and one grabbed from that state was thrown with physics off: it hung in the air
    // where it was let go (owner's two-PC test, bodies thrown by the client).
    public void ReleaseBodyPhysics(int i)
    {
        var b = BodyOf(i);
        if (b == null) return;
        bool wasRemote = (uint)i < (uint)remote.Length && remote[i].On;
        if (wasRemote) remote[i].On = false;
        if (b.animator != null && (uint)i < (uint)remote.Length && remote[i].AnimWasOn) b.animator.enabled = true;
        if (b.bloodSpreader != null) b.bloodSpreader.enabled = true;
        b.SetKinematic(false);
        foreach (var bone in b.RbToBones) bone.rb?.WakeUp();
    }

    public void DropLocal(int i)
    {
        var body = BodyOf(i);
        if (body != null)
        {
            if (Grabber != null && Grabber.IsGrabbingBody && Grabber.BodyGrabber?.grabbedBody != null && Grabber.BodyGrabber.grabbedBody.Pointer == body.Pointer)
            {
                Plugin.L.LogInfo($"COOP body {i - Items.Count} taken by another player: letting go");
                Grabber.TryReleaseBody();
            }
            return;
        }
        if (Grabber != null && Grabber.GrabbedRb != null && IndexOf(Grabber.GrabbedRb) == i)
        {
            Plugin.L.LogInfo($"COOP item {i} taken by another player: dropping it");
            Grabber.TryRelease();
        }
    }

    // Moves remote-driven items toward their owner's latest state.
    // Near the action, items the owner has at rest run on this machine's physics too (like the earlier csc-coop
    // prototype). Kept kinematic, a client's thrown item bounced off every resting item as off a wall and nothing
    // could be knocked over (owner's test on Developers' Hell). Everywhere at once they didn't work either: started
    // from the owner's pose, vases and bottles tipped over by themselves and piles settled apart (bump suite). So a
    // resting item wakes only within reach of this machine's player or of an item this machine is moving, and when
    // its physics here knocks it away (fast), we claim it and the others follow. Once all is still again it goes
    // back to the owner's spot, kinematic. Carried or moving items follow the owner kinematically as before.
    const float RestAfter = 0.4f, BumpDistance = 0.03f, BumpAngle = 6f, LocalGrace = 0.3f, ClaimRetry = 1f, BumpSpeed = 0.5f, BumpSpin = 3f,
        ClaimWindow = 60f, ClaimMax = 3, WakeEvery = 0.1f, WakeFor = 1.5f, PlayerReach = 1.2f, ItemReach = 0.8f;
    public static int Bumped, Woken;
    float nextWake;

    public void Smooth(float dt)
    {
        float k = 1 - Mathf.Exp(-dt * 30);
        float now = Time.unscaledTime;
        WakeNearby(now, now >= nextWake);
        if (now >= nextWake) nextWake = now + WakeEvery;
        for (int i = 0; i < remote.Length; i++)
        {
            if (!remote[i].On) continue;
            if (i < Items.Count && LocalPhysics(i, now)) continue;
            KeepKinematic(i);
            if (!remote[i].Fresh || i >= Items.Count && CscCoop.Bodies.IsShouldered(i - Items.Count)) continue;
            var t = EntityTransform(i);
            if (t == null) continue;
            var pos = Vector3.Distance(t.position, remote[i].Pos) > 3 ? remote[i].Pos : Vector3.Lerp(t.position, remote[i].Pos, k);
            t.SetPositionAndRotation(pos, Quaternion.Slerp(t.rotation, remote[i].Rot, k));
        }
    }

    // Remote items within reach of our player (every WakeEvery) or of an item we move (every frame, reaching as far
    // as it flies in 0.3 s: a thrown item hits within a frame or two) get physics here for a moment.
    readonly List<int> movers = new();
    void WakeNearby(float now, bool player)
    {
        var s = CoopRunner.Instance?.Session;
        if (s == null || remote.Length == 0 || Env.Get("CSCCOOP_MUTANT") == "kinematic-rest") return;
        if (player && Player != null) Wake(Player.transform.position + Vector3.up * 0.9f, PlayerReach, now);
        movers.Clear();
        for (int i = 0; i < Items.Count && i < remote.Length; i++)
            if (!remote[i].On && s.OwnerOf(i) == s.Me && Items[i] != null && !StillFor(i)) movers.Add(i);
        foreach (var i in movers)
        {
            var rb = Items[i].Rb;
            float speed = rb != null && !rb.isKinematic ? rb.velocity.magnitude : 0;
            Wake(Items[i].transform.position, ItemReach + speed * 0.3f, now);
        }
    }

    void Wake(Vector3 at, float radius, float now)
    {
        foreach (var c in Physics.OverlapSphere(at, radius))
        {
            if (c == null || c.attachedRigidbody == null) continue;
            int i = IndexOf(c.attachedRigidbody.GetComponentInParent<GrabableItemController>());
            if (i < 0 || i >= Items.Count || !remote[i].On) continue;
            if (remote[i].AwakeUntil < now) Woken++;
            remote[i].AwakeUntil = now + WakeFor;
        }
    }

    // True when item i runs on this machine's physics this frame (the owner has it at rest, and it is near us).
    bool LocalPhysics(int i, float now)
    {
        ref var r = ref remote[i];
        var g = Items[i];
        var rb = g != null ? g.Rb : null;
        bool rest = r.Fresh && !r.Held && r.Vel.sqrMagnitude < 0.01f && now - r.StillSince >= RestAfter;
        bool near = now < r.AwakeUntil || (r.Local && !StillFor(i));
        if (rb == null || !rest || !near || g.IsHeld) { r.Local = false; return false; }
        var t = g.transform;
        if (!r.Local)
        {
            r.Local = true; r.LocalAt = now;
            t.SetPositionAndRotation(r.Pos, r.Rot);
            rb.isKinematic = r.WasKinematic;
            if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.Sleep(); }
            return true;
        }
        if (rb.isKinematic || StillFor(i)) return true;   // the game keeps it kinematic (until a hit frees it), or nothing moved it
        // Moving or not by Track's per-frame movement, not IsSleeping: a knocked item moved 20 cm here while its
        // rigidbody still said asleep. A knock is fast; settling a little is not. At most ClaimMax claims a minute.
        float d = Vector3.Distance(t.position, r.Pos), a = Quaternion.Angle(t.rotation, r.Rot);
        bool fast = rb.velocity.magnitude > BumpSpeed || rb.angularVelocity.magnitude > BumpSpin;
        if (now - r.ClaimsSince > ClaimWindow) { r.ClaimsSince = now; r.Claims = 0; }
        if (fast && (d > BumpDistance || a > BumpAngle) && now - r.LocalAt > LocalGrace && now - r.ClaimAt > ClaimRetry && r.Claims < ClaimMax)
        {
            r.ClaimAt = now; r.Claims++; Bumped++;
            if (Bumped <= 60) Plugin.L.LogInfo(FormattableString.Invariant($"COOP bumped item {i} ({g.name}) {d:F2} m {a:F0} deg from the owner's spot"));
            CoopRunner.Instance?.Session?.OnLocalGrab(i);
        }
        return true;
    }

    // The game turns physics back on by itself (a punch, a bump, a body's Unfreeze); for something another
    // player simulates that made two physics fight (owner's manual test: a body kept twitching on the client).
    void KeepKinematic(int i)
    {
        if (i < Items.Count)
        {
            var rb = Items[i] != null ? Items[i].Rb : null;
            if (rb != null && !rb.isKinematic) rb.isKinematic = true;
            return;
        }
        var b = BodyOf(i);
        var root = b != null ? RootRb(b) : null;
        if (root != null && !root.isKinematic) b.SetKinematic(true);
    }

    void Snap(int i)
    {
        var t = EntityTransform(i);
        if (t != null) t.SetPositionAndRotation(remote[i].Pos, remote[i].Rot);
    }

    public string Describe(int i)
    {
        if ((uint)i >= (uint)remote.Length || i >= Items.Count) return "-";
        var r = remote[i]; var rb = Items[i] != null ? Items[i].Rb : null;
        return FormattableString.Invariant($"remote {r.On} local {r.Local} fresh {r.Fresh} held {r.Held} vel {r.Vel.magnitude:F2} still {Time.unscaledTime - r.StillSince:F1}s kinematic {rb != null && rb.isKinematic} sleeping {rb != null && rb.IsSleeping()} off {(Items[i] != null ? Vector3.Distance(Items[i].transform.position, r.Pos) : -1):F2}");
    }

    // The item's kinematic state as the game has it (remote-driven items are kinematic here for the session).
    public bool GameKinematic(int i)
    {
        var rb = (uint)i < (uint)Items.Count && Items[i] != null ? Items[i].Rb : null;
        if (rb == null) return true;
        return (uint)i < (uint)remote.Length && remote[i].On ? remote[i].WasKinematic : rb.isKinematic;
    }

    public bool IsRemoteDriven(int i) => (uint)i < (uint)remote.Length && remote[i].On;

    public static V3 V(Vector3 v) => new(v.x, v.y, v.z);
    public static Vector3 U(V3 v) => new(v.X, v.Y, v.Z);
    public static Q4 Q(Quaternion q) => new(q.x, q.y, q.z, q.w);
    public static Quaternion U(Q4 q) => new(q.X, q.Y, q.Z, q.W);
}
