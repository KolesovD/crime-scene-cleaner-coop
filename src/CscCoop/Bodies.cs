using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CscCoop;

// Bodies (BodyRagdollController) and everything that goes into the van (GarbageItem).
//  - A body is a session entity like an item (GameWorld): grabbing claims it, the owner simulates it,
//    the root bone travels as the entity state. The other bones go from the owner 10 times a second as a
//    state event per body, so a late joiner gets the last pose too.
//  - The van: TrunkController ends in GarbageItem.CollectAndDestroy (the object is switched off and
//    counted). That call is a state event; the others show the van prop and collect the same item.
[HarmonyPatch]
public static class Bodies
{
    static float nextBones;
    static readonly MemoryStream tmp = new();
    public static int BonesSent, BonesApplied, Collected, BonesIgnored;

    static GameWorld W => CoopRunner.Instance.World;

    [HarmonyPatch(typeof(BodyGrabber), nameof(BodyGrabber.GrabBody))]
    [HarmonyPrefix]
    static bool GrabBody(BodyPartController bodyPart)
    {
        var s = CoopRunner.Instance?.Session;
        if (s == null || bodyPart == null) return true;
        int e = W.BodyEntity(bodyPart.RagdollController);
        if (e < 0) return true;
        bool ok = s.OnLocalGrab(e) || Env.Get("CSCCOOP_MUTANT") == "grab-always";
        Plugin.L.LogInfo($"COOP grab body {e - W.Items.Count}: {(ok ? "allowed" : "refused, another player holds it")}");
        if (ok) W.ReleaseBodyPhysics(e);
        return ok;
    }

    // Owner side: bone poses of bodies this machine simulates and that move.
    public static void Tick()
    {
        var s = CoopRunner.Instance?.Session;
        if (s == null || !s.Synced || Time.unscaledTime < nextBones) return;
        nextBones = Time.unscaledTime + 0.1f;
        for (int b = 0; b < W.Bodies.Count; b++)
        {
            int e = W.Items.Count + b;
            if (s.OwnerOf(e) != s.Me) continue;
            var smp = W.ReadItem(e);
            // Once per body at the start: bodies lying since the job began were never sent, so a client kept its
            // own pose under the owner's root (owner's manual test: a body half inside the floor).
            if (smp.Sleeping && !smp.Held && !moving[b] && sentOnce[b]) continue;
            sentOnce[b] = true;
            moving[b] = !smp.Sleeping || smp.Held;   // one more send after it stops, so the rest pose arrives
            SendBones(b);
        }
    }

    static bool[] moving = new bool[0], sentOnce = new bool[0];
    public static void OnTable() { moving = new bool[W.Bodies.Count]; sentOnce = new bool[W.Bodies.Count]; }

    static void SendBones(int b)
    {
        var body = W.Bodies[b];
        if (body == null || body.RbToBones == null) return;
        int n = body.RbToBones.Count;
        var root = body.RbToBones[0].bone != null ? body.RbToBones[0].bone.transform : null;
        if (root == null) return;
        // Relative to the root bone, not to each bone's parent: some bones hang under parents without a
        // rigidbody (not in RbToBones), so local poses left 2 of 55 bones 14 cm off (bodies suite).
        var inv = Quaternion.Inverse(root.rotation);
        WorldEvents.Send(Ev.BodyBones, w =>
        {
            w.Write((ushort)b); w.Write((byte)n);
            for (int k = 0; k < n; k++)
            {
                var t = body.RbToBones[k].bone != null ? body.RbToBones[k].bone.transform : null;
                var p = t != null ? root.InverseTransformPoint(t.position) : Vector3.zero;
                var q = t != null ? inv * t.rotation : Quaternion.identity;
                w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
            }
        }, 0x10000u + (uint)b);
        BonesSent++;
    }

    public static void OnBones(BinaryReader r)
    {
        int b = r.ReadUInt16(); int n = r.ReadByte();
        var body = (uint)b < (uint)W.Bodies.Count ? W.Bodies[b] : null;
        var s = CoopRunner.Instance.Session;
        if (body == null || body.RbToBones == null || s.OwnerOf(W.Items.Count + b) == s.Me) return;
        if (IsShouldered(b)) { BonesIgnored++; return; }   // its carrier's first-person pose: shown on the shoulder instead
        var root = body.RbToBones[0].bone != null ? body.RbToBones[0].bone.transform : null;
        if (root == null) return;
        for (int k = 0; k < n; k++)
        {
            var p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (k == 0 || k >= body.RbToBones.Count) continue;   // the root comes with the entity state
            var bone = body.RbToBones[k].bone;
            if (bone != null) bone.transform.SetPositionAndRotation(root.TransformPoint(p), root.rotation * q);
        }
        BonesApplied++;
    }

    // ---- on another player's shoulder ----
    // The bones a carrier sends are its first-person carry: the body hangs in front of the camera, so on the
    // other screens it hung in a T pose in front of the figure (owner's manual test). While another player
    // carries a body, this machine ignores its root and bone stream and drapes it over the carrier's right
    // shoulder: pelvis on the shoulder, legs forward over the chest, torso and head down the back, arms
    // hanging. Let go (HeldItem no longer the body), the normal stream takes over again (GameWorld.Smooth).

    sealed class Rig
    {
        public Transform Hips, Spine, Chest, Neck, Head;
        public Transform[] UpLeg = new Transform[2], LowLeg = new Transform[2], Foot = new Transform[2], UpArm = new Transform[2], LowArm = new Transform[2], Hand = new Transform[2];
        public Quaternion Frame;   // the pelvis' own up/right axes as a rotation in its local space
    }

    static readonly System.Collections.Generic.Dictionary<System.IntPtr, Rig> rigs = new();
    static bool[] shouldered = new bool[0];
    public static int ShoulderFrames;
    public static float LastShoulderGap = -1;   // tests: carried pelvis to the carrier's shoulder point, metres

    public static bool IsShouldered(int b) => (uint)b < (uint)shouldered.Length && shouldered[b];

    public static void BeginShoulders()
    {
        if (shouldered.Length != W.Bodies.Count) shouldered = new bool[W.Bodies.Count];
        else System.Array.Clear(shouldered, 0, shouldered.Length);
    }

    // carrier: the player whose pose says it holds entity e; fig: its figure, already posed this frame.
    public static bool Shoulder(int e, byte carrier, PlayerFigure fig)
    {
        var s = CoopRunner.Instance.Session;
        int b = e - W.Items.Count;
        var body = (uint)b < (uint)W.Bodies.Count ? W.Bodies[b] : null;
        if (body == null || s.OwnerOf(e) != carrier || !W.IsRemoteDriven(e)) return false;
        var rig = RigOf(body, b);
        if (rig == null) return false;
        if ((uint)b < (uint)shouldered.Length) shouldered[b] = true;
        PoseOnShoulder(rig, fig);
        ShoulderFrames++;
        LastShoulderGap = Vector3.Distance(rig.Hips.position, fig.Shoulder.position);
        return true;
    }

    static Rig RigOf(BodyRagdollController body, int b)
    {
        if (rigs.TryGetValue(body.Pointer, out var r)) return r;
        r = null;
        var a = body.animator;
        if (a != null && a.isHuman)
        {
            r = new Rig
            {
                Hips = a.GetBoneTransform(HumanBodyBones.Hips), Spine = a.GetBoneTransform(HumanBodyBones.Spine),
                Chest = a.GetBoneTransform(HumanBodyBones.Chest), Neck = a.GetBoneTransform(HumanBodyBones.Neck), Head = a.GetBoneTransform(HumanBodyBones.Head),
            };
            for (int k = 0; k < 2; k++)
            {
                bool left = k == 0;
                r.UpLeg[k] = a.GetBoneTransform(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
                r.LowLeg[k] = a.GetBoneTransform(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
                r.Foot[k] = a.GetBoneTransform(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
                r.UpArm[k] = a.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                r.LowArm[k] = a.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                r.Hand[k] = a.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            }
            if (r.Hips == null || r.Spine == null || r.Head == null || r.UpLeg[0] == null || r.UpLeg[1] == null) r = null;
        }
        if (r != null)
        {
            var up = r.Hips.InverseTransformDirection(r.Spine.position - r.Hips.position).normalized;
            var right = r.Hips.InverseTransformDirection(r.UpLeg[1].position - r.UpLeg[0].position);
            right = (right - up * Vector3.Dot(right, up)).normalized;
            r.Frame = Quaternion.LookRotation(Vector3.Cross(right, up), up);
        }
        rigs[body.Pointer] = r;
        string what = r == null ? "none (no humanoid bones), it stays on the bone stream"
            : $"hips '{r.Hips.name}' (root bone '{body.RbToBones?[0]?.bone?.name}'), chest {r.Chest != null}, neck {r.Neck != null}";
        Plugin.L.LogInfo($"COOP body {b} shoulder rig: {what}");
        return r;
    }

    static void PoseOnShoulder(Rig r, PlayerFigure fig)
    {
        var t = fig.Root.transform;
        Vector3 f = t.forward, u = t.up, x = t.right;
        // Torso down the carrier's back, belly against it: body up = down and back, body right = carrier's left.
        var up = (-u * 0.9f - f * 0.42f).normalized;
        var right = -x;
        r.Hips.rotation = Quaternion.LookRotation(Vector3.Cross(right, up), up) * Quaternion.Inverse(r.Frame);
        r.Hips.position = fig.Shoulder.position + u * 0.1f - f * 0.04f;
        var spineTo = r.Chest ?? r.Neck ?? r.Head;
        AimDir(r.Spine, spineTo, up);
        if (r.Chest != null) AimDir(r.Chest, r.Neck ?? r.Head, (up - f * 0.12f).normalized);
        if (r.Neck != null) AimDir(r.Neck, r.Head, (-u - f * 0.15f).normalized);
        for (int k = 0; k < 2; k++)
        {
            float side = k == 0 ? 1 : -1;   // the body's left is the carrier's right here
            AimDir(r.UpLeg[k], r.LowLeg[k], (f * 0.8f - u * 0.5f + x * side * 0.08f).normalized);
            AimDir(r.LowLeg[k], r.Foot[k], (-u * 0.95f + f * 0.12f).normalized);
            AimDir(r.UpArm[k], r.LowArm[k], (-u - f * 0.12f + x * side * 0.06f).normalized);
            AimDir(r.LowArm[k], r.Hand[k], (-u - f * 0.05f).normalized);
        }
    }

    static void AimDir(Transform bone, Transform child, Vector3 dir)
    {
        if (bone == null || child == null) return;
        var from = child.position - bone.position;
        if (from.sqrMagnitude < 1e-8f) return;
        bone.rotation = Quaternion.FromToRotation(from, dir) * bone.rotation;
    }

    // ---- van ----

    // The game calls CollectAndDestroy a second time right after a replayed van collect, and CollectBody adds 1
    // without looking at wasCollected: one body counted 2 on the other player, both ways (friend's test of 0.4.1).
    // (BodyQuest.wasCollected is no guard: the bodies suite then counted the second victim on neither machine.)
    // So the same van item is collected once: a second CollectAndDestroy of it does nothing.
    static readonly HashSet<System.IntPtr> collectedOnce = new();
    public static void OnScene() => collectedOnce.Clear();

    [HarmonyPatch(typeof(GarbageItem), nameof(GarbageItem.CollectAndDestroy))]
    [HarmonyPrefix]
    static bool CollectOnce(GarbageItem __instance)
    {
        if (collectedOnce.Add(__instance.Pointer)) return true;
        Plugin.L.LogInfo($"COOP van item {W.GarbageIndex(__instance)} ({__instance.name}) collected again: skipped");
        return false;
    }

    [HarmonyPatch(typeof(GarbageItem), nameof(GarbageItem.CollectAndDestroy))]
    [HarmonyPostfix]
    static void CollectAndDestroy(GarbageItem __instance)
    {
        if (!WorldEvents.Live) return;
        int i = W.GarbageIndex(__instance);
        if (i < 0) { Dyn.OnCollected(__instance); return; }   // a bag made during the job
        Plugin.L.LogInfo($"COOP van item {i} ({__instance.name}) collected");
        WorldEvents.Send(Ev.VanCollect, w => w.Write((ushort)i), 0x20000u + (uint)i);
    }

    public static void OnCollect(BinaryReader r)
    {
        int i = r.ReadUInt16();
        var g = !W.IsOff("van items") && (uint)i < (uint)W.Garbage.Count ? W.Garbage[i] : null;
        if (g == null || g.isCollected) return;
        // Let go of it if this machine holds it, then the same path the van takes.
        var grab = g.grabController;
        if (grab != null && grab.IsHeld && W.Grabber != null) W.Grabber.TryRelease();
        if (g.bodyController != null && W.Grabber != null && W.Grabber.IsGrabbingBody) W.Grabber.TryReleaseBody();
        var trunk = Object.FindObjectOfType<TrunkController>();
        if (trunk != null) trunk.TryShowProp(g);
        // The van counts a body only once it was added to its objective (found); the finder may have been the
        // other player, and its AddBodyToQuest may arrive after this.
        var bq = g.GetComponentInChildren<BodyQuest>(true) ?? (g.bodyController != null ? g.bodyController.GetComponentInChildren<BodyQuest>(true) : null);
        if (bq != null && !bq.wasAddedToMax) bq.AddBodyToQuest();
        g.CollectAndDestroy();
        Collected++;
        Plugin.L.LogInfo($"COOP van item {i} ({g.name}) collected by another player");
    }
}
