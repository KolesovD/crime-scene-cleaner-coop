using System.Collections.Generic;
using UnityEngine;

namespace CscCoop;

// Another player as one smooth body: a skinned mesh built here (no player model is loaded in a job, NOTES
// "model probe"), dressed like the cleaner: overalls in the player's colour, dark gloves, boots and belt,
// a respirator and goggles that show where they look. 15 bones posed by code every frame:
//  - walking by speed (legs, knees, arm swing, hip bob), the head and chest following the view pitch;
//  - arms forward while carrying an item; the right arm up holding a body on the shoulder (Bodies);
//  - with a tool (ToolLook): the tool where the owner's first-person view holds it, both hands on it (IK),
//    swinging forward and back while it is used.
// Parts are lofted rings along the bones, weighted to one or two bones, so joints bend without gaps.
// No colliders. URP Lit (the game's own tools use it; a built-in shader draws only the shadow, PR #176).
public sealed class PlayerFigure
{
    public readonly GameObject Root;
    public readonly Transform Shoulder;   // where a carried body's pelvis rests (right shoulder)
    readonly Transform hips, spine, chest, neck, head;
    readonly Transform[] upperArm = new Transform[2], foreArm = new Transform[2], hand = new Transform[2];
    readonly Transform[] thigh = new Transform[2], shin = new Transform[2], foot = new Transform[2];
    readonly Vector3 hipsBind;
    public readonly ToolLook Tool;
    public readonly int Vertices;
    Vector3 lastPos;
    float phase, speed, useTime;

    public enum Carry { None, Item, Body }

    // Bind pose, figure space (feet at the origin, +z forward), metres. Arms hang slightly out (A pose).
    static readonly Vector3 HipsP = new(0, 0.98f, 0), SpineP = new(0, 1.10f, 0), ChestP = new(0, 1.30f, 0.01f), NeckP = new(0, 1.50f, 0.0f), HeadP = new(0, 1.58f, 0.01f);
    static readonly Vector3 ShoulderP = new(0.19f, 1.45f, 0), ElbowP = new(0.225f, 1.17f, 0), WristP = new(0.245f, 0.92f, 0.01f);
    static readonly Vector3 HipP = new(0.095f, 0.93f, 0), KneeP = new(0.10f, 0.52f, 0.01f), AnkleP = new(0.10f, 0.095f, -0.01f);

    public PlayerFigure(byte n, Color color)
    {
        Root = new GameObject($"CoopPlayer{n}");
        Object.DontDestroyOnLoad(Root);
        var r = Root.transform;
        hips = Bone("hips", r, HipsP, Vector3.zero);
        spine = Bone("spine", hips, SpineP, HipsP);
        chest = Bone("chest", spine, ChestP, SpineP);
        neck = Bone("neck", chest, NeckP, ChestP);
        head = Bone("head", neck, HeadP, NeckP);
        for (int s = 0; s < 2; s++)
        {
            var m = s == 0 ? new Vector3(-1, 1, 1) : Vector3.one;   // 0 = left, 1 = right
            upperArm[s] = Bone("upperArm" + s, chest, Vector3.Scale(ShoulderP, m), ChestP);
            foreArm[s] = Bone("foreArm" + s, upperArm[s], Vector3.Scale(ElbowP, m), Vector3.Scale(ShoulderP, m));
            hand[s] = Bone("hand" + s, foreArm[s], Vector3.Scale(WristP, m), Vector3.Scale(ElbowP, m));
            thigh[s] = Bone("thigh" + s, hips, Vector3.Scale(HipP, m), HipsP);
            shin[s] = Bone("shin" + s, thigh[s], Vector3.Scale(KneeP, m), Vector3.Scale(HipP, m));
            foot[s] = Bone("foot" + s, shin[s], Vector3.Scale(AnkleP, m), Vector3.Scale(KneeP, m));
        }
        hipsBind = hips.localPosition;
        Shoulder = new GameObject("shoulder").transform;
        Shoulder.SetParent(chest, false);
        Shoulder.localPosition = new Vector3(0.12f, 0.27f, -0.03f);

        var bones = new[] { hips, spine, chest, neck, head, upperArm[0], foreArm[0], hand[0], upperArm[1], foreArm[1], hand[1], thigh[0], shin[0], foot[0], thigh[1], shin[1], foot[1] };
        var mesh = BuildMesh(bones, out Vertices);
        var smr = Root.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = bones;
        smr.rootBone = hips;
        smr.localBounds = new Bounds(Vector3.zero, new Vector3(3, 3, 3));
        smr.updateWhenOffscreen = false;
        smr.materials = new[]
        {
            MakeMaterial(color, 0.25f),                              // overalls
            MakeMaterial(new Color(0.09f, 0.09f, 0.1f, 1), 0.35f),   // gloves, boots, belt
            MakeMaterial(new Color(0.22f, 0.23f, 0.25f, 1), 0.3f),   // respirator
            MakeMaterial(new Color(0.04f, 0.06f, 0.09f, 1), 0.9f),   // goggles
        };
        Tool = new ToolLook(Root.transform);
    }

    // Bones start world-aligned (identity rotation), so a bone's limb axis is the direction to its child.
    static Transform Bone(string name, Transform parent, Vector3 at, Vector3 parentAt)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = at - parentAt;
        return t;
    }

    // ---- mesh ----

    // A ring station of a loft: centre (figure space), half widths across (x) and front-back (z), skin weights.
    struct Ring
    {
        public Vector3 C; public float Rx, Rz; public int B0, B1; public float W1;
        public Ring(Vector3 c, float rx, float rz, int b0, int b1 = -1, float w1 = 0) { C = c; Rx = rx; Rz = rz; B0 = b0; B1 = b1; W1 = w1; }
    }

    sealed class Builder
    {
        public readonly List<Vector3> V = new();
        public readonly List<BoneWeight> W = new();
        public readonly List<int>[] T = { new(), new(), new(), new() };
        const int Seg = 18;

        // Rings ordered top to bottom (or along the limb), each perpendicular to the loft's direction there.
        public void Loft(int sub, IList<Ring> rings)
        {
            int start = V.Count;
            for (int i = 0; i < rings.Count; i++)
            {
                var a = rings[Mathf.Max(0, i - 1)].C; var b = rings[Mathf.Min(rings.Count - 1, i + 1)].C;
                var dir = (b - a).sqrMagnitude > 1e-8f ? (b - a).normalized : Vector3.down;
                var u = Vector3.right - dir * Vector3.Dot(Vector3.right, dir);
                if (u.sqrMagnitude < 1e-4f) u = Vector3.forward - dir * Vector3.Dot(Vector3.forward, dir);
                u.Normalize();
                var v = Vector3.Cross(dir, u);
                if (Vector3.Dot(Vector3.Cross(u, v), dir) > 0) v = -v;   // keep the winding outward whatever the direction
                var r = rings[i];
                var bw = new BoneWeight { boneIndex0 = r.B0, weight0 = 1 - (r.B1 >= 0 ? r.W1 : 0), boneIndex1 = r.B1 >= 0 ? r.B1 : 0, weight1 = r.B1 >= 0 ? r.W1 : 0 };
                for (int k = 0; k < Seg; k++)
                {
                    float ang = k * Mathf.PI * 2 / Seg;
                    V.Add(r.C + u * (Mathf.Cos(ang) * r.Rx) + v * (Mathf.Sin(ang) * r.Rz));
                    W.Add(bw);
                }
            }
            var t = T[sub];
            for (int i = 0; i + 1 < rings.Count; i++)
                for (int k = 0; k < Seg; k++)
                {
                    int a = start + i * Seg + k, b = start + i * Seg + (k + 1) % Seg, c = a + Seg, d = b + Seg;
                    t.Add(a); t.Add(b); t.Add(c);
                    t.Add(b); t.Add(d); t.Add(c);
                }
        }

        // An ellipsoid as a loft from its top pole to its bottom pole.
        public void Blob(int sub, Vector3 c, Vector3 r, int b0, int b1 = -1, float w1 = 0, int rings = 10)
        {
            var l = new List<Ring>();
            for (int i = 0; i <= rings; i++)
            {
                float a = i * Mathf.PI / rings, s = Mathf.Max(Mathf.Sin(a), 0.02f);
                l.Add(new Ring(c + new Vector3(0, r.y * Mathf.Cos(a), 0), r.x * s, r.z * s, b0, b1, w1));
            }
            Loft(sub, l);
        }
    }

    static Mesh BuildMesh(Transform[] bones, out int vertices)
    {
        const int Hips = 0, Spine = 1, Chest = 2, Neck = 3, Head = 4;
        var b = new Builder();
        // Torso from the neck down to the crotch.
        b.Loft(0, new[]
        {
            new Ring(new(0, 1.60f, 0.0f), 0.01f, 0.01f, Head),
            new Ring(new(0, 1.59f, 0.0f), 0.052f, 0.052f, Neck, Head, 0.5f),
            new Ring(new(0, 1.53f, 0.0f), 0.058f, 0.056f, Neck),
            new Ring(new(0, 1.495f, 0.0f), 0.085f, 0.068f, Chest, Neck, 0.35f),
            new Ring(new(0, 1.478f, 0.0f), 0.145f, 0.09f, Chest, Neck, 0.1f),
            new Ring(new(0, 1.455f, 0.0f), 0.19f, 0.108f, Chest),
            new Ring(new(0, 1.42f, 0.01f), 0.205f, 0.118f, Chest),
            new Ring(new(0, 1.36f, 0.015f), 0.195f, 0.122f, Chest),
            new Ring(new(0, 1.29f, 0.012f), 0.178f, 0.116f, Chest, Spine, 0.3f),
            new Ring(new(0, 1.22f, 0.008f), 0.166f, 0.11f, Spine, Chest, 0.4f),
            new Ring(new(0, 1.14f, 0.0f), 0.158f, 0.105f, Spine),
            new Ring(new(0, 1.05f, 0.0f), 0.163f, 0.108f, Hips, Spine, 0.4f),
            new Ring(new(0, 0.97f, -0.005f), 0.172f, 0.116f, Hips),
            new Ring(new(0, 0.90f, -0.01f), 0.17f, 0.114f, Hips),
            new Ring(new(0, 0.85f, -0.01f), 0.135f, 0.092f, Hips),
            new Ring(new(0, 0.83f, -0.01f), 0.02f, 0.02f, Hips),
        });
        b.Loft(1, new[]   // belt
        {
            new Ring(new(0, 1.01f, -0.004f), 0.16f, 0.106f, Hips, Spine, 0.15f),
            new Ring(new(0, 1.0f, -0.004f), 0.18f, 0.124f, Hips, Spine, 0.15f),
            new Ring(new(0, 0.95f, -0.006f), 0.182f, 0.125f, Hips),
            new Ring(new(0, 0.94f, -0.006f), 0.165f, 0.11f, Hips),
        });
        // Head in the hood, respirator with two filters, goggles.
        b.Blob(0, new(0, 1.685f, 0.0f), new(0.098f, 0.118f, 0.112f), Head, rings: 12);
        b.Blob(2, new(0, 1.628f, 0.078f), new(0.058f, 0.048f, 0.05f), Head);
        b.Blob(2, new(-0.058f, 1.615f, 0.085f), new(0.03f, 0.03f, 0.03f), Head);
        b.Blob(2, new(0.058f, 1.615f, 0.085f), new(0.03f, 0.03f, 0.03f), Head);
        b.Blob(3, new(0, 1.705f, 0.072f), new(0.088f, 0.032f, 0.05f), Head);
        for (int s = 0; s < 2; s++)
        {
            float x = s == 0 ? -1 : 1;
            int ua = 5 + s * 3, fa = 6 + s * 3, ha = 7 + s * 3, th = 11 + s * 3, sh = 12 + s * 3, ft = 13 + s * 3;
            Vector3 M(Vector3 p) => new(p.x * x, p.y, p.z);
            Vector3 L(Vector3 a, Vector3 c, float t) => M(Vector3.Lerp(a, c, t));
            // Arm: shoulder ball to wrist, the elbow blended half and half.
            b.Loft(0, new[]
            {
                new Ring(L(ShoulderP, ElbowP, -0.12f) + new Vector3(-0.03f * x, 0, 0), 0.012f, 0.012f, Chest, ua, 0.5f),
                new Ring(L(ShoulderP, ElbowP, -0.08f) + new Vector3(-0.012f * x, 0, 0), 0.042f, 0.045f, Chest, ua, 0.55f),
                new Ring(L(ShoulderP, ElbowP, 0.0f), 0.058f, 0.058f, Chest, ua, 0.75f),
                new Ring(L(ShoulderP, ElbowP, 0.3f), 0.055f, 0.055f, ua),
                new Ring(L(ShoulderP, ElbowP, 0.75f), 0.048f, 0.048f, ua),
                new Ring(L(ShoulderP, ElbowP, 1.0f), 0.044f, 0.044f, ua, fa, 0.5f),
                new Ring(L(ElbowP, WristP, 0.25f), 0.046f, 0.045f, fa),
                new Ring(L(ElbowP, WristP, 0.75f), 0.04f, 0.038f, fa),
                new Ring(L(ElbowP, WristP, 0.9f), 0.036f, 0.034f, fa, ha, 0.3f),
                new Ring(L(ElbowP, WristP, 0.95f), 0.012f, 0.012f, fa, ha, 0.3f),
            });
            b.Loft(1, new[]   // glove cuff and hand
            {
                new Ring(L(ElbowP, WristP, 0.7f), 0.044f, 0.042f, fa),
                new Ring(L(ElbowP, WristP, 0.78f), 0.047f, 0.045f, fa),
                new Ring(L(ElbowP, WristP, 1.0f), 0.04f, 0.036f, fa, ha, 0.5f),
            });
            b.Blob(1, M(WristP + new Vector3(0, -0.075f, 0.005f)), new(0.034f, 0.08f, 0.048f), ha);
            // Leg: hip to ankle, the knee blended.
            b.Loft(0, new[]
            {
                new Ring(M(HipP) + new Vector3(0, 0.06f, 0), 0.03f, 0.03f, 0),
                new Ring(M(HipP) + new Vector3(0, 0.02f, 0), 0.08f, 0.088f, 0, th, 0.5f),
                new Ring(L(HipP, KneeP, 0.2f), 0.086f, 0.09f, th),
                new Ring(L(HipP, KneeP, 0.55f), 0.078f, 0.08f, th),
                new Ring(L(HipP, KneeP, 0.85f), 0.064f, 0.066f, th, sh, 0.25f),
                new Ring(L(HipP, KneeP, 1.0f), 0.06f, 0.064f, th, sh, 0.5f),
                new Ring(L(KneeP, AnkleP, 0.15f), 0.06f, 0.063f, sh),
                new Ring(L(KneeP, AnkleP, 0.45f), 0.056f, 0.058f, sh),
                new Ring(L(KneeP, AnkleP, 0.75f), 0.046f, 0.048f, sh),
                new Ring(L(KneeP, AnkleP, 0.9f), 0.02f, 0.02f, sh),
            });
            b.Loft(1, new[]   // boot shaft
            {
                new Ring(L(KneeP, AnkleP, 0.42f), 0.059f, 0.061f, sh),
                new Ring(L(KneeP, AnkleP, 0.47f), 0.063f, 0.065f, sh),
                new Ring(L(KneeP, AnkleP, 0.9f), 0.054f, 0.058f, sh, ft, 0.4f),
                new Ring(L(KneeP, AnkleP, 1.05f), 0.04f, 0.04f, ft),
            });
            b.Blob(1, M(AnkleP + new Vector3(0, -0.045f, 0.055f)), new(0.058f, 0.05f, 0.135f), ft);
        }
        vertices = b.V.Count;
        var mesh = new Mesh { name = "CoopPlayerBody" };
        mesh.vertices = b.V.ToArray();
        mesh.boneWeights = b.W.ToArray();
        mesh.subMeshCount = b.T.Length;
        for (int i = 0; i < b.T.Length; i++) mesh.SetTriangles(b.T[i].ToArray(), i);
        // Bind poses: the bones' world-to-local with the root at the origin (it still is at this point).
        var bind = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++) bind[i] = bones[i].worldToLocalMatrix;
        mesh.bindposes = bind;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    static Material MakeMaterial(Color c, float smooth)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) return null;
        var m = new Material(sh);
        m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        return m;
    }

    // ---- pose ----

    // feet: the player's position on the ground; yaw/pitch in degrees; toolRot: the tool root's rotation in
    // figure space from the owner's first-person hold (only its twist is kept); toolPos is no longer used to place it.
    public void Pose(Vector3 feet, float yaw, float pitch, Carry carry, short tool, Vector3 toolPos, Quaternion toolRot, float toolScale, bool use, float dt)
    {
        var flat = new Vector3(feet.x - lastPos.x, 0, feet.z - lastPos.z);
        float v = dt > 0 ? flat.magnitude / dt : 0;
        if (flat.magnitude > 2f) v = 0;   // a teleport, not a step
        speed = Mathf.Lerp(speed, Mathf.Min(v, 6f), 1 - Mathf.Exp(-dt * 8));
        lastPos = feet;
        phase += speed * dt * 3.2f;
        float amp = Mathf.Clamp01(speed / 3f), sin = Mathf.Sin(phase);
        float swing = sin * amp * 32f;
        if (pitch > 180) pitch -= 360;
        pitch = Mathf.Clamp(pitch, -70, 70);

        Root.transform.SetPositionAndRotation(feet, Quaternion.Euler(0, yaw, 0));
        hips.localPosition = hipsBind + new Vector3(0, Mathf.Abs(Mathf.Cos(phase)) * 0.025f * amp - 0.01f * amp, 0);
        hips.localRotation = Quaternion.Euler(0, sin * 6f * amp, 0);
        spine.localRotation = Quaternion.Euler(4f * amp, -sin * 4f * amp, 0);
        chest.localRotation = Quaternion.Euler(pitch * 0.2f, -sin * 4f * amp, 0);
        neck.localRotation = Quaternion.Euler(pitch * 0.3f, 0, 0);
        head.localRotation = Quaternion.Euler(pitch * 0.5f, 0, 0);
        for (int s = 0; s < 2; s++)
        {
            float sw = s == 0 ? swing : -swing, side = s == 0 ? -1 : 1;
            float knee = Mathf.Max(0, Mathf.Sin(phase + (s == 0 ? 0.6f : 0.6f + Mathf.PI))) * 55f * amp + 3f;
            thigh[s].localRotation = Quaternion.Euler(-sw, 0, 0);
            shin[s].localRotation = Quaternion.Euler(knee, 0, 0);
            foot[s].localRotation = Quaternion.Euler(sw - knee * 0.6f, 0, 0);
            upperArm[s].localRotation = Quaternion.Euler(sw * 0.8f, 0, side * 4f);
            foreArm[s].localRotation = Quaternion.Euler(-12f - Mathf.Max(0, sw) * 0.6f, 0, 0);
            hand[s].localRotation = Quaternion.identity;
        }

        bool holdsTool = carry == Carry.None && tool != CoopCore.PlayerPose.ToolNone && Tool.Show(tool);
        if (!holdsTool) Tool.Hide();
        if (carry == Carry.Item)
            for (int s = 0; s < 2; s++) { upperArm[s].localRotation = Quaternion.Euler(-70f - pitch * 0.3f, 0, 0); foreArm[s].localRotation = Quaternion.Euler(-25f, 0, 0); }
        else if (carry == Carry.Body)
        {
            // Right hand on the back of the carried body's thighs, in front of the shoulder.
            var f = Root.transform.forward; var up = Root.transform.up; var rt = Root.transform.right;
            Reach(1, Shoulder.position + f * 0.26f - up * 0.06f + rt * 0.02f, -up * 0.6f + rt * 0.8f);
            upperArm[0].localRotation = Quaternion.Euler(sin * 18f * amp, 0, -6f);
        }
        else if (holdsTool)
        {
            useTime = use ? useTime + dt : 0;
            // Owner's manual test of 0.4.0: the first-person hold copied as is put the mop across the chest and the
            // sponge in front of the shoulder (first-person models sit where the camera sees them, not where a body
            // would hold them). So the grip goes where this figure's hands are, and the tool is turned from its
            // first-person rotation so it points the way a held tool does: long ones (mop, lance) forward and down,
            // mid ones (axe) up, small ones (sponge) centred in the hand. The tool root is its grip (attach point).
            var axis = Tool.CentreLocal.sqrMagnitude > 1e-6f ? Tool.CentreLocal.normalized : Vector3.forward;
            float wave = Mathf.Sin(useTime * Mathf.PI * 2 * 1.8f);
            Vector3 grip, dir, stroke = Vector3.zero;
            float swingDeg = 0;
            if (Tool.Length > 0.7f) { grip = new(0.2f, 1.0f - pitch * 0.004f, 0.3f); dir = Quaternion.Euler(32 + pitch * 0.4f, -8, 0) * Vector3.forward; if (use) stroke = new Vector3(0, -0.03f, 0.16f) * wave; }
            else if (Tool.Length > 0.25f) { grip = new(0.22f, 1.08f - pitch * 0.003f, 0.32f); dir = Quaternion.Euler(-55 + pitch * 0.3f, 0, 0) * Vector3.forward; if (use) swingDeg = 40 * wave; }
            else { grip = new(0.18f, 1.15f - pitch * 0.004f, 0.38f); dir = Quaternion.Euler(pitch * 0.5f, 0, 0) * Vector3.forward; if (use) stroke = new Vector3(0.1f * wave, 0.05f * Mathf.Cos(useTime * Mathf.PI * 2 * 1.8f), 0.04f); }
            var rotL = Quaternion.AngleAxis(swingDeg, Vector3.right) * Quaternion.FromToRotation(toolRot * axis, dir) * toolRot;
            var posL = grip + stroke;
            if (Tool.Length <= 0.25f) posL -= rotL * (Tool.CentreLocal * (toolScale > 0.01f ? toolScale : 1));
            var t = Root.transform;
            var pos = t.TransformPoint(posL);
            var rot = t.rotation * rotL;
            var gripW = t.TransformPoint(grip + stroke);
            Tool.Place(pos, rot, toolScale);
            Tool.SetUsing(use);
            var f = t.forward; var up = t.up; var rt = t.right;
            Reach(1, gripW, -up * 0.5f + rt * 0.8f - f * 0.3f);
            // Long tools take the other hand further along the shaft.
            if (Tool.Length > 0.7f) Reach(0, pos + (Tool.Centre - pos).normalized * Mathf.Min(0.4f, Tool.Length * 0.35f), -up * 0.5f - rt * 0.8f - f * 0.3f);
        }
    }

    // Two-bone IK: the hand's grip (a little past the wrist) to target, the elbow bent toward pole.
    void Reach(int s, Vector3 target, Vector3 pole)
    {
        var sh = upperArm[s].position;
        float a = Vector3.Distance(sh, foreArm[s].position), b = Vector3.Distance(foreArm[s].position, hand[s].position) + 0.06f * Root.transform.lossyScale.y;
        var to = target - sh;
        float d = Mathf.Clamp(to.magnitude, 0.05f, a + b - 0.001f);
        var n = to.normalized;
        float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2 * a * d), -1, 1);
        var m = pole - n * Vector3.Dot(pole, n);
        if (m.sqrMagnitude < 1e-6f) m = -Root.transform.up;
        var elbow = sh + n * (a * cosA) + m.normalized * (a * Mathf.Sqrt(1 - cosA * cosA));
        Aim(upperArm[s], foreArm[s].position, elbow);
        Aim(foreArm[s], hand[s].position, sh + n * d);
    }

    static void Aim(Transform bone, Vector3 childNow, Vector3 childWanted)
    {
        var from = childNow - bone.position; var to = childWanted - bone.position;
        if (from.sqrMagnitude < 1e-8f || to.sqrMagnitude < 1e-8f) return;
        bone.rotation = Quaternion.FromToRotation(from, to) * bone.rotation;
    }

    public Vector3 HandPosition(int s) => hand[s].position;
    public void SetActive(bool on) { if (Root.activeSelf != on) Root.SetActive(on); }
    public void Destroy() { if (Root != null) Object.Destroy(Root); }
}
