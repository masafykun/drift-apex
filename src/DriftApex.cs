using System.Collections.Generic;
using UnityEngine;

// DRIFT APEX — low-poly drift-circuit time attack with a ghost rival.
// One control: STEER (arrows / A,D / hold-drag pointer & touch). Throttle is automatic.
// The car auto-accelerates around a closed circuit; steer hard at speed to break traction and
// DRIFT through the corners — long slides chain a style combo for big SCORE. Each lap is timed;
// beat your BEST lap and a translucent GHOST of that lap races alongside you. Stay on the asphalt
// (grass is slow & grippy-less). 30 seconds in you should already be sliding the hairpin for points.
//
// Built entirely in code (CreatePrimitive + a couple of procedural meshes) so it renders reliably
// in WebGL with engine-code stripping disabled. NO Rigidbody/colliders: the car is pure
// Transform-driven (arcade drift model integrated by hand) and all track tests are distance/projection
// checks against a sampled centerline. Coexists with Juice (sfx/bgm/particles) & AutoShot.
public class DriftApex : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__DriftApex");
        go.AddComponent<DriftApex>();
        DontDestroyOnLoad(go);
    }

    // ---- scene refs ----
    Transform carT;       // root: position + yaw(heading)
    Transform carVisual;  // child: cosmetic roll/pitch
    Transform ghostT;     // ghost car root
    Transform cam; Camera camComp;
    TextMesh hudTime, hudBest, hudSpeed, hudScore, driftText, bannerText, dbg;

    // ---- track (sampled closed centerline) ----
    Vector3[] pts;        // centerline points (y=0)
    Vector3[] leftN;      // unit left-normal per point
    float[] cum;          // cumulative arc length
    float trackLen;
    int N;
    const float HALF_W = 6.0f;        // road half-width (asphalt)
    const float SOFT_W = 7.6f;        // beyond this = full grass penalty

    // ---- car state ----
    enum State { Playing }
    State state = State.Playing;
    Vector3 pos;            // car ground position (y=0)
    float heading;          // body yaw, deg (0 = +Z)
    float velAngle;         // travel direction, deg
    float speed;            // m/s, >=0
    int nearIdx;            // current nearest centerline index
    bool halfFlag;          // passed track midpoint this lap (lap-gate guard)
    float steerInput;       // -1..1 resolved each frame
    float camYaw;           // smoothed camera yaw
    float fovPunch;

    // ---- drift scoring ----
    float driftChain;       // accumulating points in the current slide
    float driftMult;        // grows the longer you hold a slide
    float driftHold;        // time since drift last active (to end a chain)
    bool drifting;
    int score, best;        // best = best lap score? no -> score is style total
    float comboFlash;

    // ---- timing / laps ----
    float lapTime, lastLap, bestLap;
    int lapCount = 1;
    float bannerTimer;
    float smokeT, sessionT;

    // ---- ghost recording ----
    struct Sample { public Vector3 pos; public float yaw; }
    readonly List<Sample> recCur = new List<Sample>();
    List<Sample> ghost;     // best lap's recording (this session)
    float recT; const float REC_DT = 0.045f;

    // ---- decorations ----
    class Cone { public Transform t; public Vector3 p; public bool knocked; }
    readonly List<Cone> cones = new List<Cone>();

    // ---- HUD layout (aspect-adaptive, like the rest of the studio's games) ----
    float hudScale = 1f, halfH = 2.7f, halfW = 4.6f;
    const float HUD_Z = 6.5f;

    // ---- tuning ----
    const float MAX_SPEED = 33f, GRASS_MAX = 14f, ACCEL = 26f;
    const float TURN_RATE = 145f;       // deg/s heading change at full steer & speed
    const float DRIFT_DEG = 14f;        // |drift angle| above this counts as a slide

    bool attract = true;
    bool showDbg;

    // ===================================================================== boot
    void Start()
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);

        bestLap = PlayerPrefs.GetFloat("driftapex_bestlap", 0f);
        best = PlayerPrefs.GetInt("driftapex_bestscore", 0);

        BuildEnvironment();
        BuildTrack();
        BuildCamera();
        BuildCar();
        BuildGhost();
        BuildCones();
        BuildHud();

        // place car on the start line, aimed down the track
        pos = pts[0];
        heading = velAngle = HeadingFromTo(pts[0], pts[1 % N]);
        nearIdx = 0;
        camYaw = heading;
        SyncCar();
        UpdateCamera(0.0001f, true);
    }

    // ===================================================================== materials / meshes
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.2f, bool emissive = false)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * 0.7f);
        }
        return m;
    }

    static GameObject Prim(PrimitiveType pt, Transform parent, Vector3 lpos, Vector3 lscale, Color c, Material shared = null)
    {
        var g = GameObject.CreatePrimitive(pt);
        var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos;
        g.transform.localScale = lscale;
        g.GetComponent<Renderer>().sharedMaterial = shared != null ? shared : Mat(c);
        return g;
    }

    static Mesh _cone;
    static Mesh ConeMesh()
    {
        if (_cone != null) return _cone;
        int seg = 10; var v = new List<Vector3>(); var tri = new List<int>();
        v.Add(new Vector3(0, 1f, 0));
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            v.Add(new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f));
        }
        int baseC = v.Count; v.Add(Vector3.zero);
        for (int i = 0; i < seg; i++)
        {
            int a = 1 + i, b = 1 + (i + 1) % seg;
            tri.Add(0); tri.Add(b); tri.Add(a);
            tri.Add(baseC); tri.Add(a); tri.Add(b);
        }
        _cone = new Mesh(); _cone.SetVertices(v); _cone.SetTriangles(tri, 0);
        _cone.RecalculateNormals(); _cone.RecalculateBounds();
        return _cone;
    }

    GameObject MeshObj(Mesh m, Transform parent, Vector3 lpos, Vector3 lscale, Color c, Material shared = null)
    {
        var g = new GameObject("m");
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos; g.transform.localScale = lscale;
        g.AddComponent<MeshFilter>().sharedMesh = m;
        g.AddComponent<MeshRenderer>().sharedMaterial = shared != null ? shared : Mat(c);
        return g;
    }

    // ===================================================================== world
    Material asphaltMat, lineMat, grassMat, bodyMat, ghostMat, coneMat, treeMat, trunkMat, tireMat;

    void BuildEnvironment()
    {
        asphaltMat = Mat(new Color(0.16f, 0.17f, 0.20f), 0.05f, 0.35f);
        lineMat    = Mat(new Color(0.95f, 0.95f, 0.98f), 0f, 0.1f);
        grassMat   = Mat(new Color(0.20f, 0.42f, 0.22f), 0f, 0.05f);
        bodyMat    = Mat(new Color(1.0f, 0.27f, 0.18f), 0.3f, 0.7f);
        ghostMat   = Mat(new Color(0.45f, 0.85f, 1f), 0f, 0.4f, true);
        coneMat    = Mat(new Color(1f, 0.55f, 0.08f), 0f, 0.3f, true);
        treeMat    = Mat(new Color(0.16f, 0.50f, 0.30f), 0f, 0.05f);
        trunkMat   = Mat(new Color(0.34f, 0.22f, 0.13f), 0f, 0.05f);
        tireMat    = Mat(new Color(0.07f, 0.07f, 0.08f), 0f, 0.2f);

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.96f, 0.86f);
        sun.intensity = 1.2f;
        sun.transform.rotation = Quaternion.Euler(50f, 35f, 0f);
        sun.shadows = LightShadows.Soft;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = new Color(0.55f, 0.66f, 0.85f);
        RenderSettings.ambientEquatorColor = new Color(0.50f, 0.55f, 0.58f);
        RenderSettings.ambientGroundColor  = new Color(0.22f, 0.26f, 0.22f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.62f, 0.72f, 0.86f);
        RenderSettings.fogStartDistance = 120f;
        RenderSettings.fogEndDistance = 320f;

        // grass ground
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var gc = g.GetComponent<Collider>(); if (gc != null) Destroy(gc);
        g.name = "Grass";
        g.transform.localScale = new Vector3(900f, 1f, 900f);
        g.transform.position = new Vector3(0, -0.55f, 0);
        g.GetComponent<Renderer>().sharedMaterial = grassMat;
    }

    // ---- closed loop control points: ellipse + per-corner radial variation (always simple, no
    //      self-intersection), then a Catmull-Rom spline sampled densely for a smooth circuit. ----
    void BuildTrack()
    {
        float[] var12 = { 0.06f, -0.16f, 0.12f, -0.22f, 0.16f, -0.10f, 0.22f, -0.26f, 0.06f, 0.18f, -0.13f, -0.02f };
        int K = var12.Length;
        float baseR = 74f;
        var cp = new Vector3[K];
        for (int i = 0; i < K; i++)
        {
            float a = i * Mathf.PI * 2f / K;
            float r = baseR * (1f + var12[i]);
            cp[i] = new Vector3(Mathf.Cos(a) * r * 1.15f, 0f, Mathf.Sin(a) * r * 0.92f);
        }

        const int SEG = 16;
        N = K * SEG;
        pts = new Vector3[N];
        int idx = 0;
        for (int s = 0; s < K; s++)
        {
            Vector3 p0 = cp[(s - 1 + K) % K], p1 = cp[s], p2 = cp[(s + 1) % K], p3 = cp[(s + 2) % K];
            for (int j = 0; j < SEG; j++)
            {
                float t = (float)j / SEG;
                pts[idx++] = CatmullRom(p0, p1, p2, p3, t);
            }
        }

        // normals + cumulative length
        leftN = new Vector3[N];
        cum = new float[N];
        float acc = 0f;
        for (int i = 0; i < N; i++)
        {
            Vector3 fwd = (pts[(i + 1) % N] - pts[(i - 1 + N) % N]); fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            leftN[i] = new Vector3(-fwd.z, 0f, fwd.x);   // left of travel direction
            cum[i] = acc;
            acc += (pts[(i + 1) % N] - pts[i]).magnitude;
        }
        trackLen = acc;

        BuildRoadMesh();
        BuildStartLine();
    }

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    void BuildRoadMesh()
    {
        // asphalt ribbon
        var rv = new Vector3[N * 2];
        var rt = new int[N * 6];
        for (int i = 0; i < N; i++)
        {
            rv[i * 2 + 0] = pts[i] + leftN[i] * HALF_W;
            rv[i * 2 + 1] = pts[i] - leftN[i] * HALF_W;
        }
        for (int i = 0; i < N; i++)
        {
            int a = i * 2, b = i * 2 + 1, c = ((i + 1) % N) * 2, d = ((i + 1) % N) * 2 + 1;
            int o = i * 6;
            rt[o + 0] = a; rt[o + 1] = c; rt[o + 2] = b;
            rt[o + 3] = b; rt[o + 4] = c; rt[o + 5] = d;
        }
        var road = new Mesh { name = "road" }; road.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        road.vertices = rv; road.triangles = rt; road.RecalculateNormals(); road.RecalculateBounds();
        var rgo = new GameObject("Road");
        rgo.transform.position = new Vector3(0, 0.02f, 0);
        rgo.AddComponent<MeshFilter>().sharedMesh = road;
        rgo.AddComponent<MeshRenderer>().sharedMaterial = asphaltMat;

        // white edge ribbons (both sides) + dashed centerline
        BuildEdgeRibbon(HALF_W - 0.35f, 0.30f, lineMat, 0.03f);
        BuildEdgeRibbon(-(HALF_W - 0.35f), 0.30f, lineMat, 0.03f);
        BuildDashedCenter();
    }

    void BuildEdgeRibbon(float offset, float width, Material mat, float y)
    {
        var rv = new Vector3[N * 2];
        var rt = new int[N * 6];
        for (int i = 0; i < N; i++)
        {
            rv[i * 2 + 0] = pts[i] + leftN[i] * (offset + width * 0.5f);
            rv[i * 2 + 1] = pts[i] + leftN[i] * (offset - width * 0.5f);
        }
        for (int i = 0; i < N; i++)
        {
            int a = i * 2, b = i * 2 + 1, c = ((i + 1) % N) * 2, d = ((i + 1) % N) * 2 + 1;
            int o = i * 6;
            rt[o + 0] = a; rt[o + 1] = c; rt[o + 2] = b;
            rt[o + 3] = b; rt[o + 4] = c; rt[o + 5] = d;
        }
        var m = new Mesh { name = "edge" }; m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.vertices = rv; m.triangles = rt; m.RecalculateNormals(); m.RecalculateBounds();
        var go = new GameObject("Edge");
        go.transform.position = new Vector3(0, y, 0);
        go.AddComponent<MeshFilter>().sharedMesh = m;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
    }

    void BuildDashedCenter()
    {
        var dashMat = Mat(new Color(0.85f, 0.78f, 0.3f), 0f, 0.1f);
        for (int i = 0; i < N; i += 6)
        {
            Vector3 fwd = (pts[(i + 1) % N] - pts[i]); fwd.y = 0; fwd.Normalize();
            var q = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var col = q.GetComponent<Collider>(); if (col) Destroy(col);
            q.transform.position = pts[i] + Vector3.up * 0.04f;
            q.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            q.transform.localScale = new Vector3(0.22f, 0.02f, 2.2f);
            q.GetComponent<Renderer>().sharedMaterial = dashMat;
        }
    }

    void BuildStartLine()
    {
        // checkered band across the road at index 0
        Vector3 fwd = (pts[1 % N] - pts[0]); fwd.y = 0; fwd.Normalize();
        var black = Mat(new Color(0.05f, 0.05f, 0.05f), 0f, 0.1f);
        int cells = 8;
        for (int c = 0; c < cells; c++)
        {
            float f = (c / (float)cells - 0.5f) * 2f;
            for (int row = 0; row < 2; row++)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var col = q.GetComponent<Collider>(); if (col) Destroy(col);
                q.transform.position = pts[0] + leftN[0] * (f * HALF_W) + fwd * (row * 0.9f - 0.45f) + Vector3.up * 0.05f;
                q.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
                q.transform.localScale = new Vector3(HALF_W * 2f / cells, 0.02f, 0.9f);
                q.GetComponent<Renderer>().sharedMaterial = ((c + row) % 2 == 0) ? lineMat : black;
            }
        }
        // start gantry posts
        for (int side = -1; side <= 1; side += 2)
        {
            var p = new GameObject("post");
            p.transform.position = pts[0] + leftN[0] * (side * (HALF_W + 1.2f));
            Prim(PrimitiveType.Cylinder, p.transform, new Vector3(0, 3f, 0), new Vector3(0.4f, 3f, 0.4f), new Color(0.9f, 0.9f, 0.95f));
            Prim(PrimitiveType.Cube, p.transform, new Vector3(side * -1.5f, 6.2f, 0), new Vector3(3.2f, 1f, 0.3f), new Color(0.95f, 0.2f, 0.2f), null);
        }
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.60f, 0.72f, 0.88f);
        camComp.fieldOfView = 60f;
        camComp.farClipPlane = 600f;
        cgo.AddComponent<AudioListener>();
        cam = cgo.transform;
    }

    void BuildCar()
    {
        carT = new GameObject("Car").transform;
        carVisual = new GameObject("CarVisual").transform;
        carVisual.SetParent(carT, false);
        BuildCarBody(carVisual, bodyMat, 1f);
    }

    void BuildGhost()
    {
        ghostT = new GameObject("Ghost").transform;
        var gv = new GameObject("GhostVisual").transform;
        gv.SetParent(ghostT, false);
        BuildCarBody(gv, ghostMat, 1f);
        ghostT.gameObject.SetActive(false);
    }

    // a chunky low-poly hot-hatch: body, cabin, spoiler, 4 wheels, headlights
    void BuildCarBody(Transform root, Material body, float a)
    {
        Prim(PrimitiveType.Cube, root, new Vector3(0, 0.55f, 0.1f), new Vector3(1.9f, 0.6f, 4.0f), default, body);   // lower body
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.05f, -0.25f), new Vector3(1.6f, 0.55f, 2.1f), default, body); // cabin
        // windshield (dark)
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.07f, 0.85f), new Vector3(1.45f, 0.5f, 0.12f), new Color(0.1f, 0.13f, 0.18f));
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.07f, -1.32f), new Vector3(1.45f, 0.5f, 0.12f), new Color(0.1f, 0.13f, 0.18f));
        // spoiler
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.15f, -2.0f), new Vector3(1.7f, 0.12f, 0.45f), default, body);
        Prim(PrimitiveType.Cube, root, new Vector3(0.6f, 0.95f, -1.95f), new Vector3(0.12f, 0.35f, 0.2f), default, body);
        Prim(PrimitiveType.Cube, root, new Vector3(-0.6f, 0.95f, -1.95f), new Vector3(0.12f, 0.35f, 0.2f), default, body);
        // headlights
        Prim(PrimitiveType.Cube, root, new Vector3(0.55f, 0.6f, 2.02f), new Vector3(0.5f, 0.28f, 0.06f), new Color(1f, 0.95f, 0.7f), Mat(new Color(1f, 0.95f, 0.7f), 0, 0.5f, true));
        Prim(PrimitiveType.Cube, root, new Vector3(-0.55f, 0.6f, 2.02f), new Vector3(0.5f, 0.28f, 0.06f), new Color(1f, 0.95f, 0.7f), Mat(new Color(1f, 0.95f, 0.7f), 0, 0.5f, true));
        // wheels
        float wx = 1.02f, wz = 1.35f, wy = 0.38f;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                var w = Prim(PrimitiveType.Cylinder, root, new Vector3(sx * wx, wy, sz * wz), new Vector3(0.42f, 0.16f, 0.42f), default, tireMat);
                w.transform.localRotation = Quaternion.Euler(0, 0, 90f);
            }
    }

    void BuildCones()
    {
        // a handful of orange cones just inside the apex of the sharper corners
        for (int i = 0; i < N; i += 11)
        {
            // sharper where curvature high: place every Mth, alternate inside edge
            float side = ((i / 11) % 2 == 0) ? 1f : -1f;
            Vector3 p = pts[i] + leftN[i] * side * (HALF_W - 1.1f);
            var go = new GameObject("cone");
            go.transform.position = p;
            MeshObj(ConeMesh(), go.transform, Vector3.zero, new Vector3(0.5f, 0.85f, 0.5f), default, coneMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 0.18f, 0), new Vector3(0.7f, 0.12f, 0.7f), Color.white);
            cones.Add(new Cone { t = go.transform, p = p });
        }
        // a ring of trees outside the track for depth
        for (int i = 0; i < N; i += 9)
        {
            Vector3 p = pts[i] + leftN[i] * ((i % 18 == 0) ? 1f : -1f) * Random.Range(16f, 34f);
            var go = new GameObject("tree");
            go.transform.position = p;
            Prim(PrimitiveType.Cylinder, go.transform, new Vector3(0, 1f, 0), new Vector3(0.5f, 1f, 0.5f), default, trunkMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 1.5f, 0), new Vector3(3.2f, 3.2f, 3.2f), default, treeMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 3.2f, 0), new Vector3(2.4f, 2.6f, 2.4f), default, treeMat);
        }
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(cam, false);
        t.transform.localRotation = Quaternion.identity;
        return t;
    }

    void BuildHud()
    {
        hudTime  = MakeText(0.085f, Color.white, TextAnchor.UpperLeft);
        hudScore = MakeText(0.060f, new Color(1f, 0.85f, 0.3f), TextAnchor.UpperLeft);
        hudBest  = MakeText(0.060f, new Color(0.7f, 0.9f, 1f), TextAnchor.UpperRight);
        hudSpeed = MakeText(0.060f, new Color(0.9f, 0.95f, 1f), TextAnchor.LowerRight);
        driftText= MakeText(0.11f, new Color(1f, 0.85f, 0.3f), TextAnchor.MiddleCenter);
        bannerText=MakeText(0.14f, Color.white, TextAnchor.MiddleCenter);
        dbg = MakeText(0.040f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft);
        dbg.gameObject.SetActive(false);
        driftText.text = ""; bannerText.text = "";
        AdjustHud();
        RefreshHud();
    }

    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        const float REF_HALFW = 6.0f;
        hudScale = Mathf.Clamp(halfW / REF_HALFW, 0.16f, 1.3f);
        float ix = halfW * 0.95f, iy = halfH * 0.93f;

        hudTime.transform.localPosition  = new Vector3(-ix, iy, HUD_Z); hudTime.characterSize  = 0.085f * hudScale;
        hudScore.transform.localPosition = new Vector3(-ix, iy - 0.7f * hudScale, HUD_Z); hudScore.characterSize = 0.060f * hudScale;
        hudBest.transform.localPosition  = new Vector3( ix, iy, HUD_Z); hudBest.characterSize  = 0.060f * hudScale;
        hudSpeed.transform.localPosition = new Vector3( ix, -iy, HUD_Z); hudSpeed.characterSize = 0.060f * hudScale;
        dbg.transform.localPosition      = new Vector3(-ix, -iy * 0.55f, HUD_Z); dbg.characterSize = 0.040f * hudScale;
        driftText.transform.localPosition = new Vector3(0, halfH * 0.52f, HUD_Z);
        if (comboFlash <= 0f) driftText.characterSize = 0.11f * hudScale;
    }

    void RefreshHud()
    {
        if (hudTime)  hudTime.text  = "LAP " + lapCount + "   " + Fmt(lapTime);
        if (hudScore) hudScore.text = "SCORE  " + score;
        if (hudBest)  hudBest.text  = (bestLap > 0f ? "BEST " + Fmt(bestLap) : "BEST  --:--")
                                    + (lastLap > 0f ? "\nLAST " + Fmt(lastLap) : "");
        if (hudSpeed) hudSpeed.text = Mathf.RoundToInt(speed * 3.6f) + " km/h";
    }
    static string Fmt(float t) { int m = (int)(t / 60f); float s = t - m * 60f; return string.Format("{0}:{1:00.00}", m, s); }

    // ===================================================================== input
    void GatherInput()
    {
        float key = Input.GetAxisRaw("Horizontal");
        float pointer = 0f; bool pressed = false; float px = 0f;
        if (Input.touchCount > 0) { pressed = true; px = Input.GetTouch(0).position.x; }
        else if (Input.GetMouseButton(0)) { pressed = true; px = Input.mousePosition.x; }
        if (pressed)
        {
            float n = (px / Mathf.Max(1f, Screen.width)) * 2f - 1f;
            pointer = Mathf.Clamp(n * 1.7f, -1f, 1f);
        }
        float raw = Mathf.Abs(key) > 0.01f ? key : pointer;

        if (Mathf.Abs(raw) > 0.01f || Input.anyKeyDown || Input.GetMouseButtonDown(0)) attract = false;
        if (attract) raw = AutoSteer();

        steerInput = Mathf.Clamp(raw, -1f, 1f);
    }

    float AutoSteer()
    {
        // aim at a centerline point a few samples ahead (keeps the demo on track & drifting)
        int look = (nearIdx + 7) % N;
        float want = HeadingFromTo(pos, pts[look]);
        float diff = Mathf.DeltaAngle(heading, want);
        return Mathf.Clamp(diff / 22f, -1f, 1f);
    }

    // ===================================================================== main loop
    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0.05f) dt = 0.05f;
        sessionT += dt;

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        GatherInput();

        // ---- locate on track: windowed nearest search (car only moves forward) ----
        int prevIdx = nearIdx;
        UpdateTrackPosition();
        float lateral = Vector3.Dot(pos - pts[nearIdx], leftN[nearIdx]);   // signed, + = left
        float absLat = Mathf.Abs(lateral);
        bool onRoad = absLat < HALF_W;
        float grassFactor = Mathf.Clamp01((absLat - HALF_W) / (SOFT_W - HALF_W)); // 0 road .. 1 deep grass

        // ---- lap detection: wrapped forward across the start line, after passing midpoint ----
        if (nearIdx > N / 2 - 4 && nearIdx < N / 2 + 4) halfFlag = true;
        if (halfFlag && prevIdx > (int)(N * 0.78f) && nearIdx < (int)(N * 0.22f))
            CompleteLap();

        lapTime += dt;

        // ================= arcade drift model =================
        // throttle: ease speed toward a cap (lower off-road / when sliding hard)
        float driftAngle = Mathf.DeltaAngle(velAngle, heading);     // + = nose points left of travel
        float absDrift = Mathf.Abs(driftAngle);
        float cap = Mathf.Lerp(MAX_SPEED, GRASS_MAX, grassFactor);
        cap *= 1f - Mathf.Clamp01(absDrift / 70f) * 0.35f;          // big slides scrub speed
        if (speed < cap) speed = Mathf.MoveTowards(speed, cap, ACCEL * dt);
        else             speed = Mathf.MoveTowards(speed, cap, ACCEL * 1.4f * dt);

        // steering -> heading. authority grows from a standstill, eases at very high speed.
        float spAuth = Mathf.Clamp01(speed / 7f) * Mathf.Lerp(1f, 0.78f, Mathf.Clamp01((speed - 18f) / 18f));
        heading += steerInput * TURN_RATE * spAuth * dt;
        heading = Norm(heading);

        // grip: how fast travel direction chases the nose. Gentle steer = high grip (tight carve);
        // hard steer at speed = low grip (the car breaks away and slides). Grass loses grip too.
        float grip = Mathf.Lerp(240f, 78f, Mathf.Abs(steerInput));
        grip *= Mathf.Lerp(1f, 0.6f, grassFactor);
        velAngle = Mathf.MoveTowardsAngle(velAngle, heading, grip * dt);
        // clamp the slip so we never spin out
        driftAngle = Mathf.DeltaAngle(velAngle, heading);
        if (Mathf.Abs(driftAngle) > 52f) { velAngle = Norm(heading - Mathf.Sign(driftAngle) * 52f); }

        // integrate position along travel direction
        Vector3 dir = Dir(velAngle);
        pos += dir * speed * dt;
        pos.y = 0f;

        // soft world boundary: the whole circuit fits within ~110m of origin, so a player who
        // wanders far into the grass is gently stopped at a 150m ring (never drives off the world).
        float distO = Mathf.Sqrt(pos.x * pos.x + pos.z * pos.z);
        if (distO > 150f) { pos *= 150f / distO; speed *= 0.5f; }

        SyncCar();
        UpdateDriftScore(driftAngle, onRoad, dt);
        UpdateCones();
        UpdateGhost();
        RecordGhost(dt);
        UpdateCamera(dt, false);
        TickHud(dt);
        if (showDbg) UpdateDbg(lateral, driftAngle, grassFactor);
    }

    void UpdateTrackPosition()
    {
        float bestD = float.MaxValue; int bi = nearIdx;
        for (int k = -2; k <= 14; k++)
        {
            int i = ((nearIdx + k) % N + N) % N;
            float d = (pos - pts[i]).sqrMagnitude;
            if (d < bestD) { bestD = d; bi = i; }
        }
        nearIdx = bi;
    }

    void SyncCar()
    {
        carT.position = pos;
        carT.rotation = Quaternion.Euler(0, heading, 0);
        // cosmetic: roll into the turn, pitch with accel, squat during big slides
        float driftA = Mathf.DeltaAngle(velAngle, heading);
        float roll = Mathf.Clamp(-steerInput * 7f - driftA * 0.12f, -16f, 16f);
        float pitch = -Mathf.Clamp01(speed / MAX_SPEED) * 2.5f;
        carVisual.localRotation = Quaternion.Slerp(carVisual.localRotation, Quaternion.Euler(pitch, 0, roll), 1f - Mathf.Exp(-10f * Time.deltaTime));
    }

    // ===================================================================== drift scoring
    void UpdateDriftScore(float driftAngle, bool onRoad, float dt)
    {
        float absDrift = Mathf.Abs(driftAngle);
        bool slideNow = onRoad && speed > 9f && absDrift > DRIFT_DEG;

        if (slideNow)
        {
            if (!drifting) { drifting = true; driftMult = 1f; }
            driftHold = 0f;
            driftMult = Mathf.Min(driftMult + dt * 0.55f, 6f);
            float gain = absDrift * speed * 0.05f * driftMult * dt;
            driftChain += gain;
            // tire smoke from the rear
            smokeT -= dt;
            if (smokeT <= 0f)
            {
                smokeT = 0.03f;
                Vector3 rear = pos - Dir(heading) * 1.6f + Vector3.up * 0.3f;
                Juice.Pop(rear, new Color(0.85f, 0.85f, 0.88f, 0.9f), 4);
            }
            driftText.text = "DRIFT  " + Mathf.RoundToInt(driftChain) + (driftMult > 1.5f ? "  x" + driftMult.ToString("0.0") : "");
            driftText.color = driftMult > 4f ? new Color(1f, 0.35f, 0.5f) : driftMult > 2.5f ? new Color(1f, 0.6f, 0.2f) : new Color(1f, 0.85f, 0.3f);
            Juice.Shake(Mathf.Min(0.04f + absDrift * 0.002f, 0.18f));
        }
        else if (drifting)
        {
            driftHold += dt;
            if (driftHold > 0.35f) BankDrift();
        }
    }

    void BankDrift()
    {
        drifting = false;
        int gained = Mathf.RoundToInt(driftChain);
        if (gained >= 30)
        {
            score += gained;
            comboFlash = 1f;
            string tier = gained > 1500 ? "INSANE!" : gained > 700 ? "AWESOME!" : gained > 250 ? "GREAT!" : "NICE";
            FloatText("+" + gained + "  " + tier, new Color(1f, 0.85f, 0.3f));
            Juice.Score(pos + Vector3.up * 1.2f);
            Juice.Blip(680f + Mathf.Min(gained, 1500) * 0.15f, 0.07f, 0.4f);
            if (score > best) { best = score; PlayerPrefs.SetInt("driftapex_bestscore", best); PlayerPrefs.Save(); }
            RefreshHud();
        }
        driftChain = 0f; driftMult = 1f; driftText.text = "";
    }

    // ===================================================================== laps
    void CompleteLap()
    {
        halfFlag = false;
        if (drifting) BankDrift();
        float t = lapTime;
        lastLap = t;
        bool nb = bestLap <= 0f || t < bestLap;
        if (nb)
        {
            bestLap = t;
            PlayerPrefs.SetFloat("driftapex_bestlap", bestLap); PlayerPrefs.Save();
            // store this lap's ghost recording
            ghost = new List<Sample>(recCur);
            ghostT.gameObject.SetActive(ghost.Count > 1);
        }
        lapCount++;
        lapTime = 0f;
        recCur.Clear(); recT = 0f;
        Juice.Score(pos + Vector3.up * 1.5f);
        Juice.Blip(900f, 0.09f, 0.45f); Juice.Blip(1350f, 0.08f, 0.35f);
        Juice.Shake(0.2f);
        fovPunch = Mathf.Max(fovPunch, 6f);
        Banner((nb ? "NEW BEST LAP!\n" : "LAP " + (lapCount - 1) + "\n") + Fmt(t), nb ? new Color(1f, 0.85f, 0.3f) : Color.white, 2.2f);
        RefreshHud();
    }

    // ===================================================================== ghost
    void RecordGhost(float dt)
    {
        recT += dt;
        if (recT >= REC_DT)
        {
            recT = 0f;
            if (recCur.Count < 4000) recCur.Add(new Sample { pos = pos, yaw = heading });
        }
    }

    void UpdateGhost()
    {
        if (ghost == null || ghost.Count < 2) { if (ghostT.gameObject.activeSelf) ghostT.gameObject.SetActive(false); return; }
        if (!ghostT.gameObject.activeSelf) ghostT.gameObject.SetActive(true);
        float f = lapTime / REC_DT;
        int i = Mathf.Clamp((int)f, 0, ghost.Count - 2);
        float frac = Mathf.Clamp01(f - i);
        Vector3 gp = Vector3.Lerp(ghost[i].pos, ghost[i + 1].pos, frac);
        float gy = Mathf.LerpAngle(ghost[i].yaw, ghost[i + 1].yaw, frac);
        ghostT.position = gp;
        ghostT.rotation = Quaternion.Euler(0, gy, 0);
    }

    // ===================================================================== cones
    void UpdateCones()
    {
        for (int i = 0; i < cones.Count; i++)
        {
            var c = cones[i];
            if (c.knocked || c.t == null) continue;
            Vector3 d = c.p - pos; d.y = 0f;
            if (d.sqrMagnitude < 2.4f * 2.4f && speed > 8f)
            {
                c.knocked = true;
                var fl = c.t.gameObject.AddComponent<Flyer>();
                Vector3 push = (d.sqrMagnitude > 0.01f ? d.normalized : Dir(velAngle));
                fl.Init(push * (3f + speed * 0.15f) + Vector3.up * 4f + Dir(velAngle) * speed * 0.2f);
                Juice.Blip(220f, 0.08f, 0.3f);
                Juice.Pop(c.p + Vector3.up * 0.4f, new Color(1f, 0.6f, 0.1f), 6);
                score += 25; comboFlash = 0.6f;
                RefreshHud();
            }
        }
    }

    // ===================================================================== camera / hud tick
    void UpdateCamera(float dt, bool snap)
    {
        if (cam == null) return;
        // camera yaw follows travel direction so a slide kicks the car visibly sideways on screen
        float targetYaw = velAngle;
        camYaw = snap ? targetYaw : Mathf.LerpAngle(camYaw, targetYaw, 1f - Mathf.Exp(-6f * dt));
        Vector3 back = Dir(camYaw);
        float dist = 10.5f + Mathf.Clamp01(speed / MAX_SPEED) * 2.5f;
        Vector3 want = pos - back * dist + Vector3.up * 4.8f;
        cam.position = snap ? want : Vector3.Lerp(cam.position, want, 1f - Mathf.Exp(-8f * dt));
        Vector3 look = pos + back * 6f + Vector3.up * 1.0f;
        Quaternion q = Quaternion.LookRotation(look - cam.position, Vector3.up);
        cam.rotation = snap ? q : Quaternion.Slerp(cam.rotation, q, 1f - Mathf.Exp(-9f * dt));

        fovPunch = Mathf.Lerp(fovPunch, 0f, 6f * dt);
        float baseFov = 58f + Mathf.Clamp01(speed / MAX_SPEED) * 14f;
        camComp.fieldOfView = Mathf.Clamp(baseFov + fovPunch, 50f, 86f);
        AdjustHud();
    }

    void TickHud(float dt)
    {
        if (comboFlash > 0f)
        {
            comboFlash -= dt * 2.0f;
            float s = 0.11f * hudScale * (1f + Mathf.Max(0f, comboFlash) * 0.5f);
            if (driftText) driftText.characterSize = s;
        }
        if (bannerTimer > 0f)
        {
            bannerTimer -= dt;
            if (bannerTimer <= 0f) { bannerText.text = ""; bannerText.color = Color.white; }
        }
        hudTime.text = "LAP " + lapCount + "   " + Fmt(lapTime);
        hudSpeed.text = Mathf.RoundToInt(speed * 3.6f) + " km/h";
    }

    void FloatText(string s, Color c)
    {
        bannerText.transform.localPosition = new Vector3(0f, -halfH * 0.40f, HUD_Z);
        bannerText.characterSize = 0.12f * hudScale;
        bannerText.text = s; bannerText.color = c; bannerTimer = 1.1f;
    }

    void Banner(string s, Color c, float dur)
    {
        bannerText.transform.localPosition = new Vector3(0f, halfH * 0.15f, HUD_Z);
        bannerText.characterSize = 0.14f * hudScale;
        bannerText.text = s; bannerText.color = c; bannerTimer = dur;
    }

    void UpdateDbg(float lateral, float driftAngle, float grass)
    {
        dbg.text = string.Format(
            "spd {0:0.0}  steer {1:0.00}  drift {2:0.0}\nhead {3:0.0} vel {4:0.0}\nidx {5}/{6} lat {7:0.0} grass {8:0.00}\nlap {9} t {10:0.00} best {11:0.00}\nscore {12} chain {13:0} mult {14:0.0}\ncones {15} ghost {16} fps {17:0}",
            speed, steerInput, driftAngle, heading, velAngle, nearIdx, N, lateral, grass,
            lapCount, lapTime, bestLap, score, driftChain, driftMult,
            cones.Count, ghost != null ? ghost.Count : 0, 1f / Mathf.Max(0.0001f, Time.smoothDeltaTime));
    }

    // ===================================================================== math helpers
    static float Norm(float deg) { deg %= 360f; if (deg > 180f) deg -= 360f; else if (deg < -180f) deg += 360f; return deg; }
    static Vector3 Dir(float deg) { float r = deg * Mathf.Deg2Rad; return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r)); }
    static float HeadingFromTo(Vector3 a, Vector3 b) { Vector3 d = b - a; return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }
}

// short-lived tumbling object (knocked cone) — pure transform, self-destructs.
public class Flyer : MonoBehaviour
{
    Vector3 vel; Vector3 spin; float age, life = 1.6f;
    public void Init(Vector3 v) { vel = v; spin = new Vector3(Random.Range(-400f, 400f), Random.Range(-400f, 400f), Random.Range(-400f, 400f)); }
    void Update()
    {
        float dt = Time.deltaTime; age += dt;
        vel.y -= 16f * dt;
        transform.position += vel * dt;
        transform.Rotate(spin * dt, Space.World);
        if (transform.position.y < 0f && vel.y < 0f) { vel.y = -vel.y * 0.4f; vel.x *= 0.6f; vel.z *= 0.6f; }
        if (age >= life) Destroy(gameObject);
    }
}
