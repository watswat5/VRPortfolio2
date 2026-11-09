// Builds the venue, a small local club, from its floor plan (stage.dxf, an Onshape sketch in millimetres): the room
// lined with black curtains, a black concrete floor, a black industrial ceiling (deck, joists, beams, ducts), the
// wooden stage and its steps, the bar, the employees-only room, the fire door and the prep kitchen behind the bar.
// Everything is generated (meshes, materials) into Assets/Venue/Generated, then saved as Assets/Venue/Venue.prefab and
// the scene Assets/Scenes/Venue.unity. Change a number below and run Freestyle > Build Venue again.
//
// Plan x is x here, plan y is z (the stage is at +z); y is up; metres. The plan has no heights: those are guesses.
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Freestyle.Venue
{
    public static class VenueBuilder
    {
        // ---- the plan (inside faces of the room) ----
        internal const float X0 = -3.5456f, PlanX1 = 3.5456f, Z0 = -9.906f, Z1 = 9.906f;
        internal const float Widen = 0.6096f;       // the room is 2 ft wider than the plan on stage left (+x); the employees-only room isn't
        internal const float X1 = PlanX1 + Widen;
        internal const float StageX0 = -1.3312f, StageZ0 = 8.0772f;   // the stage runs to the right and back walls
        const float StepsX0 = -2.2456f;                      // the steps: StepsX0..StageX0, StageZ0..Z1, climbing +x
        const float BarX0 = -1.9244f, BarX1 = -1.2744f, BarZ1 = -5.9892f;   // the bar top; the bartenders' side is left of it
        const float RoomX0 = 1.0760f, RoomZ1 = -5.9892f;     // employees only: RoomX0..PlanX1, Z0..RoomZ1

        // ---- not in the plan: guesses ----
        const float H = 4.6f;              // floor to the underside of the roof deck
        const float Wall = 0.2f, Partition = 0.12f;
        internal const float StageH = 0.6f;         // 24"
        internal const float Recess = 2.0f;         // the stage runs this far back into the back wall, so the DJ fits behind the table
        internal const float StageZ1 = Z1 + Recess; // the stage's back wall
        const int StepCount = 3;           // treads below the stage, 15 cm risers
        const float BarH = 1.07f;          // 42"
        const float DoorH = 2.13f;         // 7'
        const float KitchenX1 = -0.6f, KitchenDepth = 3.0f, KitchenH = 3.0f;   // behind the near wall, behind the bar
        const float FireDoorX = -2.896f, FireDoorW = 1.0f;       // back wall, left of the steps
        const float KitchenDoorX = -2.75f, KitchenDoorW = 0.9f;  // near wall, behind the bar
        const float RoomDoorX = 1.75f, RoomDoorW = 0.9f;         // the employees-only room's wall facing the room
        const float EntranceX = (BarX1 + RoomX0) / 2, EntranceW = 1.8f;   // the main doors: the near wall between bar and room
        const float CurtainTop = H - 0.75f;                      // under the beams
        const float DuctEnd = 5.6f;                              // the ducts stop short of the stage's truss
        // The headset's renderer lights each object with at most 4 lights, so big meshes are cut into chunks this long
        // (along the room) and each chunk only gets the lights near it.
        const float ChunkZ = 3f;

        const string Dir = "Assets/Venue", Gen = Dir + "/Generated";
        const string WoodTex = "Assets/Textures/ChatGPT Image Oct 16, 2026, 08_23_22 PM.png";
        const string WoodNormal = "Assets/Textures/ChatGPT Image Oct 16, 2026, 08_23_31 PM.png";
        const string ConcreteTex = "Assets/Textures/Worn Charcoal Concrete Texture.png";

        [MenuItem("Freestyle/Build Venue")]
        public static void Build()
        {
            if (AssetDatabase.IsValidFolder(Gen)) AssetDatabase.DeleteAsset(Gen);
            AssetDatabase.CreateFolder(Dir, "Generated");

            var m = new Mats();
            var parts = new List<Part>();
            Part P(string name, Material mat, bool collider = false) { var p = new Part(name, mat, collider); parts.Add(p); return p; }

            var floor = P("Floor", m.concrete, true);
            var walls = P("Walls", m.wall, true);
            var curtains = P("Curtains", m.curtain);
            var cloth = P("DJCloth", m.cloth);
            var steel = P("Steel", m.steel);
            var deck = P("CeilingDeck", m.ceiling);
            var stage = P("Stage", m.stageWood, true);
            var barBody = P("BarBody", m.barBlack, true);
            var barTop = P("BarTop", m.barTop, true);
            var chrome = P("Chrome", m.chrome);
            var doors = P("Doors", m.door, true);
            var led = P("BarLed", m.led);
            var bulbs = P("Bulbs", m.bulb);
            var amber = P("BottlesAmber", m.amber);
            var green = P("BottlesGreen", m.green);
            var clear = P("BottlesClear", m.clear);
            var kWall = P("KitchenWalls", m.kitchenWall, true);
            var kFloor = P("KitchenFloor", m.kitchenFloor, true);
            var exitBox = P("ExitSign", m.exitSign);
            var screens = P("Screens", m.screen);
            var tape = P("Tape", m.tape);

            // ---- floor, walls, ceiling ----
            for (float z = Z0 - Wall; z < StageZ1 + Wall - 0.01f; z += 2f)   // tiles, so the chunks below can split it
                floor.Box(new Vector3(X0 - Wall, -0.1f, z), new Vector3(X1 + Wall, 0, Mathf.Min(z + 2f, StageZ1 + Wall)));
            walls.Box(new Vector3(X0 - Wall, 0, Z0 - Wall), new Vector3(X0, H, Z1 + Wall));    // left
            walls.Box(new Vector3(X1, 0, Z0 - Wall), new Vector3(X1 + Wall, H, StageZ1 + Wall));   // right, to the recess's back
            WallAlongX(walls, Z1, Z1 + Wall, X0, StageX0, H, (FireDoorX, FireDoorW));       // back, by the steps
            walls.Box(new Vector3(StageX0 - Wall, 0, Z1), new Vector3(StageX0, H, StageZ1 + Wall));   // the recess's side
            walls.Box(new Vector3(StageX0 - Wall, 0, StageZ1), new Vector3(X1, H, StageZ1 + Wall));   // the recess's back
            WallAlongX(walls, Z0 - Wall, Z0, X0, X1, H, (KitchenDoorX, KitchenDoorW), (EntranceX, EntranceW));   // near
            // employees-only room: two partitions to the deck, inside the room's lines
            WallAlongX(walls, RoomZ1 - Partition, RoomZ1, RoomX0, X1, H, (RoomDoorX, RoomDoorW));
            walls.Box(new Vector3(RoomX0, 0, Z0), new Vector3(RoomX0 + Partition, H, RoomZ1 - Partition));
            walls.Box(new Vector3(PlanX1, 0, Z0), new Vector3(X1, H, RoomZ1 - Partition));   // its far side: the outer wall, thickened by Widen
            walls.Box(new Vector3(RoomX0, 2.7f, Z0), new Vector3(X1, 2.75f, RoomZ1));          // its own ceiling
            Ceiling(deck, steel);

            // ---- curtains: every wall the room sees, but not behind the bar or over the doors ----
            float y0 = 0.02f;
            var cs = new List<(Vector3 a, Vector3 b, Vector3 inward)>
            {
                (new(X0, 0, BarZ1), new(X0, 0, Z1), Vector3.right),                                  // left, from the bar on
                (new(X0, 0, Z1), new(FireDoorX - FireDoorW / 2 - 0.12f, 0, Z1), Vector3.back),       // back, left of the fire door
                (new(FireDoorX + FireDoorW / 2 + 0.12f, 0, Z1), new(StageX0, 0, Z1), Vector3.back),  // back, behind the steps
                (new(StageX0, 0, Z1), new(StageX0, 0, StageZ1), Vector3.right),                      // the recess's side
                (new(StageX0, 0, StageZ1), new(X1, 0, StageZ1), Vector3.back),                       // behind the stage
                (new(X1, 0, RoomZ1), new(X1, 0, StageZ1), Vector3.left),                             // right
                (new(RoomX0, 0, Z0), new(RoomX0, 0, RoomZ1), Vector3.left),                          // the room's side
                (new(RoomX0, 0, RoomZ1), new(RoomDoorX - RoomDoorW / 2 - 0.1f, 0, RoomZ1), Vector3.forward),
                (new(RoomDoorX + RoomDoorW / 2 + 0.1f, 0, RoomZ1), new(X1, 0, RoomZ1), Vector3.forward),
            };
            for (int i = 0; i < cs.Count; i++)
            {
                curtains.Curtain(cs[i].a, cs[i].b, cs[i].inward, y0, CurtainTop, i);
                var a = cs[i].a + cs[i].inward * 0.13f; var b = cs[i].b + cs[i].inward * 0.13f;   // the track
                steel.Box(Vector3.Min(a, b) + new Vector3(-0.02f, CurtainTop, -0.02f), Vector3.Max(a, b) + new Vector3(0.02f, CurtainTop + 0.05f, 0.02f));
            }

            // ---- stage and steps ----
            stage.Box(new Vector3(StageX0, 0, StageZ0), new Vector3(X1, StageH, StageZ1));
            float rise = StageH / (StepCount + 1), run = (StageX0 - StepsX0) / StepCount;
            for (int i = 0; i < StepCount; i++)
                stage.Box(new Vector3(StepsX0 + i * run, 0, StageZ0), new Vector3(StepsX0 + (i + 1) * run, (i + 1) * rise, Z1));
            stage.Box(new Vector3(StageX0, StageH, StageZ0 - 0.02f), new Vector3(X1, StageH + 0.012f, StageZ0 + 0.05f));   // nosing

            Bar(barBody, barTop, chrome, steel, led, bulbs, amber, green, clear);
            Doors(doors, chrome, exitBox, steel, P("StreetGlow", m.street), P("DoorMat", m.doorMat));
            Kitchen(kWall, kFloor, steel, bulbs);
            StageRig(steel, bulbs);
            StageGear(barBody, steel, chrome, cloth, screens, tape, led, clear);
            Backstage(m, (n, mat, col) => P(n, mat, col), chrome, steel, bulbs, barBody, barTop, clear, doors);

            // ---- save ----
            var root = new GameObject("Venue");
            foreach (var p in parts)
            {
                if (p.Empty) continue;
                var group = new GameObject(p.name);
                group.transform.SetParent(root.transform, false);
                foreach (var mesh in p.ToMeshes())
                {
                    var go = new GameObject(mesh.name);
                    go.transform.SetParent(group.transform, false);
                    AssetDatabase.CreateAsset(mesh, $"{Gen}/{mesh.name}.asset");
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = p.mat;
                    if (p.collider) go.AddComponent<MeshCollider>().sharedMesh = mesh;
                    GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic |
                                                                StaticEditorFlags.OccludeeStatic);   // no static batching: it would merge the chunks again
                }
            }
            Signs(root.transform);
            Monitors(root.transform, m.monitor);
            Mirror(root.transform, m);
            Accessories(root.transform);
            Lights(root.transform);
            Spots(root.transform);
            BuildStageDoor(root.transform, m);

            PrefabUtility.SaveAsPrefabAsset(root, Dir + "/Venue.prefab");
            Object.DestroyImmediate(root);
            MakeScene();
            AssetDatabase.SaveAssets();
            Debug.Log("Venue built: Assets/Venue/Venue.prefab, Assets/Scenes/Venue.unity");
        }

        // ---------------------------------------------------------------- pieces

        /// <summary>A wall along x from xA to xB (z zA..zB), with door openings (centre, width).</summary>
        static void WallAlongX(Part p, float zA, float zB, float xA, float xB, float top, params (float c, float w)[] doors)
        {
            float x = xA;
            foreach (var (c, w) in doors)
            {
                p.Box(new Vector3(x, 0, zA), new Vector3(c - w / 2, top, zB));
                p.Box(new Vector3(c - w / 2, DoorH, zA), new Vector3(c + w / 2, top, zB));   // over the door
                x = c + w / 2;
            }
            p.Box(new Vector3(x, 0, zA), new Vector3(xB, top, zB));
        }

        /// <summary>Roof deck with its flutes, open-web joists along the room, steel beams across it, two ducts and a sprinkler main.</summary>
        static void Ceiling(Part deck, Part steel)
        {
            deck.Box(new Vector3(X0 - Wall, H, Z0 - Wall), new Vector3(X1 + Wall, H + 0.15f, StageZ1 + Wall));
            for (float x = X0 + 0.15f; x < X1; x += 0.3f)   // the deck's flutes
                deck.Box(new Vector3(x, H - 0.04f, Z0), new Vector3(x + 0.12f, H, x > StageX0 ? StageZ1 : Z1));

            // joists along z, on the beams; top and bottom chords and a zigzag of web rods
            for (float x = X0 + 0.9f; x < X1 - 0.5f; x += 1.2f)
            {
                float top = H - 0.06f, bot = H - 0.36f;
                float end = x > StageX0 ? StageZ1 : Z1;
                steel.Box(new Vector3(x - 0.04f, top - 0.04f, Z0), new Vector3(x + 0.04f, top, end));
                steel.Box(new Vector3(x - 0.04f, bot, Z0), new Vector3(x + 0.04f, bot + 0.04f, end));
                bool up = true;
                for (float z = Z0 + 0.1f; z < end - 0.6f; z += 0.6f, up = !up)
                    steel.Cylinder(new Vector3(x, up ? bot + 0.02f : top - 0.02f, z), new Vector3(x, up ? top - 0.02f : bot + 0.02f, z + 0.6f), 0.012f, 6, false);
            }
            // wide-flange beams across the room, under the joists
            for (float z = Z0 + 3.3f; z < Z1 - 1f; z += 3.3f)
            {
                float top = H - 0.36f, bot = top - 0.4f;
                steel.Box(new Vector3(X0, top - 0.016f, z - 0.09f), new Vector3(X1, top, z + 0.09f));
                steel.Box(new Vector3(X0, bot, z - 0.09f), new Vector3(X1, bot + 0.016f, z + 0.09f));
                steel.Box(new Vector3(X0, bot, z - 0.006f), new Vector3(X1, top, z + 0.006f));
            }
            // ducts, hung from the joists, and the sprinkler main
            foreach (float x in new[] { -2.3f, 2.2f })
            {
                float y = H - 1.05f;
                steel.Cylinder(new Vector3(x, y, Z0 + 0.3f), new Vector3(x, y, DuctEnd), 0.22f, 20, true);
                for (float z = Z0 + 0.3f; z < DuctEnd; z += 1.5f)   // seams
                    steel.Cylinder(new Vector3(x, y, z), new Vector3(x, y, z + 0.03f), 0.228f, 20, false);
                for (float z = Z0 + 1f; z < DuctEnd; z += 2.5f)
                    steel.Cylinder(new Vector3(x, y + 0.2f, z), new Vector3(x, H - 0.36f, z), 0.008f, 4, false);
            }
            steel.Cylinder(new Vector3(0.3f, H - 0.5f, Z0 + 0.2f), new Vector3(0.3f, H - 0.5f, Z1 - 0.2f), 0.04f, 10, true);
            for (float z = Z0 + 1.5f; z < Z1; z += 3f)   // sprinkler heads
                steel.Cylinder(new Vector3(0.3f, H - 0.54f, z), new Vector3(0.3f, H - 0.62f, z), 0.012f, 6, true);
        }

        static void Bar(Part body, Part top, Part chrome, Part steel, Part led, Part bulbs, Part amber, Part green, Part clear)
        {
            // the counter, with an overhang toward the room and a foot rail
            body.Box(new Vector3(BarX0 + 0.05f, 0, Z0), new Vector3(BarX1 - 0.12f, BarH - 0.05f, BarZ1 - 0.1f));
            top.Box(new Vector3(BarX0, BarH - 0.05f, Z0), new Vector3(BarX1, BarH, BarZ1));
            chrome.Cylinder(new Vector3(BarX1 + 0.05f, 0.22f, Z0 + 0.1f), new Vector3(BarX1 + 0.05f, 0.22f, BarZ1 - 0.2f), 0.025f, 12, true);
            for (float z = Z0 + 0.4f; z < BarZ1 - 0.2f; z += 1.2f)
                chrome.Box(new Vector3(BarX1 - 0.12f, 0.2f, z - 0.015f), new Vector3(BarX1 + 0.05f, 0.24f, z + 0.015f));
            // the bartenders' side: a back bar against the wall, shelves of bottles, LED strips under the shelves
            float bz0 = Z0 + 1.1f, bz1 = BarZ1 - 0.3f;   // clear of the kitchen door
            body.Box(new Vector3(X0, 0, bz0), new Vector3(X0 + 0.55f, 0.9f, bz1));
            steel.Box(new Vector3(X0, 0.9f, bz0), new Vector3(X0 + 0.57f, 0.93f, bz1));
            var rnd = new System.Random(7);
            var glass = new[] { amber, green, clear };
            foreach (float sy in new[] { 0.93f, 1.35f, 1.75f })
            {
                if (sy > 1f)
                {
                    top.Box(new Vector3(X0, sy - 0.03f, bz0), new Vector3(X0 + 0.28f, sy, bz1));
                    led.Box(new Vector3(X0 + 0.24f, sy - 0.045f, bz0 + 0.05f), new Vector3(X0 + 0.27f, sy - 0.03f, bz1 - 0.05f));
                }
                float depth = sy > 1f ? 0.14f : 0.3f;
                for (float z = bz0 + 0.1f; z < bz1 - 0.08f; z += 0.1f + (float)rnd.NextDouble() * 0.05f)
                {
                    var g = glass[rnd.Next(glass.Length)];
                    float h = 0.2f + (float)rnd.NextDouble() * 0.12f, r = 0.032f + (float)rnd.NextDouble() * 0.012f;
                    var b = new Vector3(X0 + depth, sy, z);
                    g.Cylinder(b, b + Vector3.up * h, r, 10, true);
                    g.Cylinder(b + Vector3.up * h, b + Vector3.up * (h + 0.09f), 0.012f, 8, true);
                }
            }
            // two pendants over the bar top
            foreach (float z in new[] { -8.8f, -7.0f })
            {
                var c = new Vector3((BarX0 + BarX1) / 2, 2.3f, z);
                steel.Cylinder(c + Vector3.up * 0.2f, new Vector3(c.x, H - 0.36f, z), 0.004f, 4, false);
                steel.Cylinder(c, c + Vector3.up * 0.2f, 0.09f, 14, true);
                bulbs.Cylinder(c - Vector3.up * 0.05f, c, 0.05f, 10, true);
            }
        }

        static void Doors(Part doors, Part chrome, Part exitBox, Part steel, Part glow, Part mat)
        {
            // the main doors: a pair of steel doors with push bars and tall vision panels (street light outside), an exit sign
            // over them, a mat inside
            float e0 = EntranceX - EntranceW / 2, e1 = EntranceX + EntranceW / 2;
            Frame(steel, e0, e1, Z0 - Wall, Z0);
            steel.Box(new Vector3(EntranceX - 0.02f, 0, Z0 - 0.12f), new Vector3(EntranceX + 0.02f, DoorH, Z0 - 0.05f));   // mullion
            foreach (var (a, b) in new[] { (e0 + 0.02f, EntranceX - 0.02f), (EntranceX + 0.02f, e1 - 0.02f) })
            {
                float vx0 = (a + b) / 2 - 0.08f, vx1 = (a + b) / 2 + 0.08f, vy0 = 0.95f, vy1 = 1.9f;   // the vision panel
                doors.Box(new Vector3(a, 0, Z0 - 0.11f), new Vector3(vx0, DoorH - 0.02f, Z0 - 0.06f));
                doors.Box(new Vector3(vx1, 0, Z0 - 0.11f), new Vector3(b, DoorH - 0.02f, Z0 - 0.06f));
                doors.Box(new Vector3(vx0, 0, Z0 - 0.11f), new Vector3(vx1, vy0, Z0 - 0.06f));
                doors.Box(new Vector3(vx0, vy1, Z0 - 0.11f), new Vector3(vx1, DoorH - 0.02f, Z0 - 0.06f));
                glow.Box(new Vector3(vx0, vy0, Z0 - 0.09f), new Vector3(vx1, vy1, Z0 - 0.085f));
                chrome.Box(new Vector3(a + 0.06f, 0.98f, Z0 - 0.06f), new Vector3(b - 0.06f, 1.04f, Z0 - 0.01f));
            }
            exitBox.Box(new Vector3(EntranceX - 0.18f, DoorH + 0.15f, Z0), new Vector3(EntranceX + 0.18f, DoorH + 0.34f, Z0 + 0.06f));
            mat.Box(new Vector3(e0 + 0.1f, 0, Z0 + 0.05f), new Vector3(e1 - 0.1f, 0.012f, Z0 + 1.25f));

            // fire door in the back wall: steel, push bar, exit sign over it
            float d0 = FireDoorX - FireDoorW / 2, d1 = FireDoorX + FireDoorW / 2;
            doors.Box(new Vector3(d0 + 0.02f, 0, Z1 + 0.06f), new Vector3(d1 - 0.02f, DoorH - 0.02f, Z1 + 0.11f));
            Frame(steel, d0, d1, Z1, Z1 + Wall);
            chrome.Box(new Vector3(d0 + 0.08f, 0.98f, Z1 + 0.01f), new Vector3(d1 - 0.12f, 1.04f, Z1 + 0.06f));
            exitBox.Box(new Vector3(FireDoorX - 0.18f, DoorH + 0.15f, Z1 - 0.06f), new Vector3(FireDoorX + 0.18f, DoorH + 0.34f, Z1));

            // kitchen door in the near wall, behind the bar: a swing door with a kick plate
            d0 = KitchenDoorX - KitchenDoorW / 2; d1 = KitchenDoorX + KitchenDoorW / 2;
            float wy0 = 1.3f, wy1 = 1.75f, wc = (d0 + d1) / 2;   // the window: the kitchen's light shows through it
            doors.Box(new Vector3(d0 + 0.02f, 0, Z0 - 0.11f), new Vector3(d1 - 0.02f, wy0, Z0 - 0.06f));
            doors.Box(new Vector3(d0 + 0.02f, wy1, Z0 - 0.11f), new Vector3(d1 - 0.02f, DoorH - 0.02f, Z0 - 0.06f));
            doors.Box(new Vector3(d0 + 0.02f, wy0, Z0 - 0.11f), new Vector3(wc - 0.15f, wy1, Z0 - 0.06f));
            doors.Box(new Vector3(wc + 0.15f, wy0, Z0 - 0.11f), new Vector3(d1 - 0.02f, wy1, Z0 - 0.06f));
            Frame(steel, d0, d1, Z0 - Wall, Z0);
            chrome.Box(new Vector3(wc - 0.17f, wy0 - 0.02f, Z0 - 0.06f), new Vector3(wc + 0.17f, wy0, Z0 - 0.05f));
            chrome.Box(new Vector3(wc - 0.17f, wy1, Z0 - 0.06f), new Vector3(wc + 0.17f, wy1 + 0.02f, Z0 - 0.05f));
            chrome.Box(new Vector3(d0 + 0.03f, 0.02f, Z0 - 0.06f), new Vector3(d1 - 0.03f, 0.3f, Z0 - 0.055f));

            // employees-only door
            d0 = RoomDoorX - RoomDoorW / 2; d1 = RoomDoorX + RoomDoorW / 2;
            doors.Box(new Vector3(d0 + 0.02f, 0, RoomZ1 - 0.085f), new Vector3(d1 - 0.02f, DoorH - 0.02f, RoomZ1 - 0.035f));
            Frame(steel, d0, d1, RoomZ1 - Partition, RoomZ1);
            chrome.Cylinder(new Vector3(d1 - 0.1f, 1.0f, RoomZ1 - 0.035f), new Vector3(d1 - 0.1f, 1.0f, RoomZ1 + 0.02f), 0.025f, 10, true);
            chrome.Box(new Vector3(d1 - 0.22f, 0.98f, RoomZ1 + 0.015f), new Vector3(d1 - 0.08f, 1.02f, RoomZ1 + 0.03f));
        }

        /// <summary>A steel door frame (jambs and head) in a wall opening d0..d1 between zA and zB.</summary>
        static void Frame(Part p, float d0, float d1, float zA, float zB)
        {
            p.Box(new Vector3(d0 - 0.05f, 0, zA - 0.01f), new Vector3(d0, DoorH + 0.05f, zB + 0.01f));
            p.Box(new Vector3(d1, 0, zA - 0.01f), new Vector3(d1 + 0.05f, DoorH + 0.05f, zB + 0.01f));
            p.Box(new Vector3(d0 - 0.05f, DoorH, zA - 0.01f), new Vector3(d1 + 0.05f, DoorH + 0.05f, zB + 0.01f));
        }

        /// <summary>The prep kitchen: only ever seen through the window in its door, so a lit white room and a shelf.</summary>
        static void Kitchen(Part walls, Part floor, Part steel, Part bulbs)
        {
            float kz1 = Z0 - Wall, kz0 = kz1 - KitchenDepth;
            floor.Box(new Vector3(X0 - Wall, -0.1f, kz0 - Wall), new Vector3(KitchenX1 + Wall, 0, kz1));
            walls.Box(new Vector3(X0 - Wall, 0, kz0 - Wall), new Vector3(X0, KitchenH, kz1));
            walls.Box(new Vector3(KitchenX1, 0, kz0 - Wall), new Vector3(KitchenX1 + Wall, KitchenH, kz1));
            walls.Box(new Vector3(X0, 0, kz0 - Wall), new Vector3(KitchenX1, KitchenH, kz0));
            walls.Box(new Vector3(X0 - Wall, KitchenH, kz0 - Wall), new Vector3(KitchenX1 + Wall, KitchenH + 0.1f, kz1));
            steel.Box(new Vector3(X0 + 0.05f, 1.5f, kz0), new Vector3(KitchenX1 - 0.05f, 1.53f, kz0 + 0.35f));
            var c = new Vector3((X0 + KitchenX1) / 2, KitchenH - 0.05f, (kz0 + kz1) / 2);
            steel.Box(c - new Vector3(0.6f, 0.02f, 0.08f), c + new Vector3(0.6f, 0.05f, 0.08f));
            bulbs.Box(c - new Vector3(0.58f, 0.025f, 0.06f), c + new Vector3(0.58f, -0.015f, 0.06f));
        }

        /// <summary>What the MCs see around them: floor wedges facing them, PA stacks at the stage front, the DJ's table at the
        /// back, tape marks where each MC stands, and cable runs.</summary>
        static void StageGear(Part black, Part steel, Part chrome, Part cloth, Part screens, Part tape, Part led, Part clear)
        {
            // confidence monitors: a TV on a low wedge in front of each MC, leaning back at them (the screens are Monitors())
            foreach (var (c, r, _) in MonitorPoses())
            {
                black.OBox(c - r * new Vector3(0, 0, 0.035f), r, new Vector3(MonitorW + 0.04f, MonitorH + 0.04f, 0.06f));      // the TV
                black.OBox(new Vector3(c.x, StageH + 0.06f, c.z - 0.12f), Quaternion.identity, new Vector3(0.45f, 0.12f, 0.28f));   // its stand
                steel.Cylinder(new Vector3(c.x + 0.15f, StageH + 0.008f, c.z - 0.2f), new Vector3(c.x + 0.25f, StageH + 0.008f, StageZ1 - 0.25f), 0.008f, 6, false);
            }
            // PA: a sub and a top on the floor at each end of the stage front, facing the room
            foreach (float x in new[] { StageX0 + 0.45f, X1 - 0.45f })
            {
                float z = StageZ0 - 0.45f;
                black.Box(new Vector3(x - 0.32f, 0, z - 0.35f), new Vector3(x + 0.32f, 0.58f, z + 0.35f));
                steel.Box(new Vector3(x - 0.29f, 0.04f, z - 0.36f), new Vector3(x + 0.29f, 0.54f, z - 0.35f));
                black.Box(new Vector3(x - 0.24f, 0.58f, z - 0.2f), new Vector3(x + 0.24f, 1.3f, z + 0.22f));
                steel.Box(new Vector3(x - 0.21f, 0.61f, z - 0.21f), new Vector3(x + 0.21f, 1.27f, z - 0.2f));
                chrome.Box(new Vector3(x - 0.03f, 1.2f, z - 0.215f), new Vector3(x + 0.03f, 1.215f, z - 0.21f));   // badge
            }
            // the DJ's table at the back, with room to stand behind it (DJSpot): a draped table, the controller with its platters
            // and mixer, a laptop on a stand facing the DJ, a booth monitor at each end turned in at the DJ, a water bottle
            var (dj, _) = DJ();
            float dx = dj.x, dz = DJTableZ, ty = 0.92f, top = StageH + ty, hw = DJTableW / 2, hd = DJTableD / 2;
            cloth.Box(new Vector3(dx - hw, StageH, dz - hd), new Vector3(dx + hw, top, dz + hd));
            black.Box(new Vector3(dx - 0.38f, top, dz - 0.21f), new Vector3(dx + 0.38f, top + 0.055f, dz + 0.21f));
            foreach (float px in new[] { -0.22f, 0.22f })
            {
                chrome.Cylinder(new Vector3(dx + px, top + 0.055f, dz + 0.03f), new Vector3(dx + px, top + 0.065f, dz + 0.03f), 0.105f, 24, true);
                black.Cylinder(new Vector3(dx + px, top + 0.065f, dz + 0.03f), new Vector3(dx + px, top + 0.07f, dz + 0.03f), 0.03f, 12, true);
            }
            led.Box(new Vector3(dx - 0.06f, top + 0.055f, dz - 0.16f), new Vector3(dx - 0.05f, top + 0.06f, dz + 0.14f));
            led.Box(new Vector3(dx + 0.05f, top + 0.055f, dz - 0.16f), new Vector3(dx + 0.06f, top + 0.06f, dz + 0.14f));
            // laptop stand (to the DJ's left), the laptop on it, its screen hinged at the far edge and facing the DJ
            float lx = dx + 0.53f;
            var tilt = Quaternion.Euler(12, 0, 0);   // the stand rises toward the room, so the keys slope down to the DJ
            steel.OBox(new Vector3(lx, top + 0.08f, dz - 0.02f), tilt, new Vector3(0.26f, 0.012f, 0.28f));
            steel.Box(new Vector3(lx - 0.12f, top, dz - 0.15f), new Vector3(lx - 0.1f, top + 0.1f, dz - 0.13f));
            steel.Box(new Vector3(lx + 0.1f, top, dz - 0.15f), new Vector3(lx + 0.12f, top + 0.1f, dz - 0.13f));
            black.OBox(new Vector3(lx, top + 0.095f, dz - 0.02f), tilt, new Vector3(0.32f, 0.018f, 0.22f));
            var lid = Quaternion.Euler(-100, 0, 0);   // open, leaning back a little: its +z runs up from the hinge
            var hinge = new Vector3(lx, top + 0.12f, dz - 0.13f);
            black.OBox(hinge + lid * new Vector3(0, 0, 0.11f), lid, new Vector3(0.32f, 0.006f, 0.22f));
            screens.OBox(hinge + lid * new Vector3(0, -0.004f, 0.11f), lid, new Vector3(0.29f, 0.002f, 0.18f));   // the DJ's side
            // booth monitors at the ends, turned in toward the DJ
            foreach (float side in new[] { -1f, 1f })
            {
                var c = new Vector3(dx + side * (hw - 0.1f), top + 0.15f, dz - 0.08f);
                var r = Quaternion.Euler(0, 180 + side * 25, 0);
                black.OBox(c, r, new Vector3(0.17f, 0.28f, 0.21f));
                steel.Cylinder(c + r * new Vector3(0, -0.04f, 0.12f), c + r * new Vector3(0, -0.04f, 0.125f), 0.075f, 16, true);
            }
            clear.Cylinder(new Vector3(dx - 0.55f, top, dz + 0.2f), new Vector3(dx - 0.55f, top + 0.2f, dz + 0.2f), 0.032f, 10, true);
            // cables down the back to a power strip
            steel.Cylinder(new Vector3(dx, top, dz + hd - 0.02f), new Vector3(dx + 0.1f, StageH + 0.01f, dz + hd + 0.05f), 0.007f, 6, false);
            black.Box(new Vector3(dx - 0.2f, StageH, dz + hd + 0.02f), new Vector3(dx + 0.2f, StageH + 0.04f, dz + hd + 0.08f));
            steel.Cylinder(new Vector3(dx + 0.2f, StageH + 0.01f, dz + hd + 0.05f), new Vector3(X1 - 0.25f, StageH + 0.01f, dz + hd + 0.1f), 0.008f, 6, false);

            // tape: an X where each MC stands, and the stage edge
            foreach (var (pos, _) in MCs())
                foreach (float yaw in new[] { 45f, -45f })
                    tape.OBox(new Vector3(pos.x, StageH + 0.001f, pos.z), Quaternion.Euler(0, yaw, 0), new Vector3(0.3f, 0.002f, 0.05f));
            tape.Box(new Vector3(StageX0, StageH + 0.012f, StageZ0 - 0.02f), new Vector3(X1, StageH + 0.014f, StageZ0 + 0.03f));
        }

        const float MonitorW = 0.53f, MonitorH = 0.3f;   // a 24" TV's screen
        const float MonitorTilt = 55;                    // leaning back from upright, so it reads from standing height

        /// <summary>Each MC's monitor: the screen's centre and turn (its front faces +z of the turn), and whose it is.</summary>
        static IEnumerable<(Vector3 centre, Quaternion turn, string who)> MonitorPoses()
        {
            string[] who = { "Player", "Opponent" };
            int i = 0;
            foreach (var (pos, _) in MCs())
                yield return (new Vector3(pos.x, StageH + 0.19f, StageZ0 + 0.35f), Quaternion.Euler(-MonitorTilt, 0, 0), who[i++]);
        }

        /// <summary>The monitors' screens, as their own objects, so the game can draw on them: PlayerMonitor/Screen is a quad
        /// (UVs 0-1 across the picture) with its own material for a RenderTexture, and its forward points into the screen,
        /// the way a world-space Canvas under it (0.53 x 0.30 m) faces the MC.</summary>
        static void Monitors(Transform root, Material screenMat)
        {
            var group = new GameObject("Monitors").transform;
            group.SetParent(root, false);
            var mesh = new Mesh { name = "MonitorScreen" };
            float w = MonitorW / 2, h = MonitorH / 2;
            mesh.SetVertices(new[] { new Vector3(-w, -h, 0), new Vector3(-w, h, 0), new Vector3(w, h, 0), new Vector3(w, -h, 0) });
            mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, $"{Gen}/MonitorScreen.asset");
            foreach (var (c, r, who) in MonitorPoses())
            {
                var mon = new GameObject(who + "Monitor").transform;
                mon.SetParent(group, false);
                var screen = new GameObject("Screen");
                screen.transform.SetParent(mon, false);
                screen.transform.SetPositionAndRotation(c, r * Quaternion.Euler(0, 180, 0));
                screen.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mat = new Material(screenMat) { name = who + "MonitorScreen" };
                AssetDatabase.CreateAsset(mat, $"{Gen}/{mat.name}.mat");
                screen.AddComponent<MeshRenderer>().sharedMaterial = mat;
            }
        }

        internal const float DJTableW = 1.8f, DJTableD = 0.7f;
        internal const float DJTableZ = StageZ1 - 1.45f;  // its centre: ~0.9 m clear behind it to the curtain (room for a robot), ~1.2 m in front to the MCs

        /// <summary>Where the DJ stands, behind the table, facing the room.</summary>
        static (Vector3 pos, float yaw) DJ() => (new Vector3((StageX0 + X1) / 2 + 0.2f, StageH, StageZ1 - 0.62f), 180);

        static IEnumerable<(Vector3 pos, float yaw)> MCs()
        {
            float mid = (StageX0 + X1) / 2;
            yield return (new Vector3(mid - 1.2f, StageH, 8.9f), 125);   // the player
            yield return (new Vector3(mid + 1.2f, StageH, 8.9f), 235);   // the opponent
        }

        // ---------------------------------------------------------------- backstage

        // The dressing room: its own small room, far off to the side of the venue, where the player dresses up before the
        // battle. A full-length mirror on the back wall, a shelf for the accessories beside it (its near end about half a
        // metre from where the player stands at the mirror), and the door to the stage: grab it and the screen fades,
        // the player is on the stage (StageDoor). Not in the plan. Inside faces: x BX0..BX1, z BZ0..BZ1, floor 0, ceiling BH.
        const float BOff = 25f;                           // how far along x from the venue
        internal const float BX0 = X0 + BOff, BX1 = BX0 + 3.4f;    // 3.4 m wide
        internal const float BZ0 = 0f, BZ1 = 3.6f, BH = 3.0f;
        const float BDoorX = BX1 - 0.7f, BDoorW = 0.95f;  // the door to the stage, in the front wall: where the player arrives
        // the mirror, on the back wall a little left of middle; the shelf against the wall to its left
        // the mirror: from just right of the shelf all the way to the right wall (its frame against the wall)
        const float MirrorX0 = BX0 + 1.35f, MirrorW = BX1 - 0.07f - MirrorX0, MirrorX = MirrorX0 + MirrorW / 2, MirrorH = 1.9f, MirrorY0 = 0.2f;
        static readonly Vector3 MirrorSpot = new(BX0 + 1.75f, 0, BZ1 - 0.9f);   // where the player stands at it, by the shelf
        const float ShelfW = 0.9f, ShelfD = 0.38f, ShelfH = 1.75f;
        static readonly float[] ShelfBoards = { 0.06f, 0.5f, 0.85f, 1.2f, 1.55f };   // heights of the boards' tops
        const float ShelfX1 = MirrorX0 - 0.15f;   // its right end, by the mirror

        static void Backstage(Mats m, System.Func<string, Material, bool, Part> P, Part chrome, Part steel, Part bulbs,
                              Part black, Part wood, Part clear, Part doors)
        {
            float F = 0;
            var walls = P("BackstageWalls", m.Get("BackstageWall", new Color(0.46f, 0.46f, 0.43f), 0.2f), true);   // painted block
            var floor = P("BackstageFloor", m.Get("BackstageFloor", new Color(0.3f, 0.3f, 0.31f), 0.45f), true);  // sealed concrete
            var ceiling = P("BackstageCeiling", m.Get("BackstageCeiling", new Color(0.12f, 0.12f, 0.12f), 0.2f), false);
            // its own parts, so its meshes only pick up its own lights
            steel = P("BackstageSteel", m.steelMat, false);
            bulbs = P("BackstageBulbs", m.bulbMat, false);
            black = P("BackstageBlack", m.blackMat, true);
            wood = P("BackstageWood", m.woodMat, true);

            // the shell: walls, floor, ceiling with a couple of pipes across it
            float t = 0.01f, w = 0.2f;
            walls.Box(new Vector3(BX0 - w, 0, BZ0 - w), new Vector3(BX0, BH, BZ1 + w));
            walls.Box(new Vector3(BX1, 0, BZ0 - w), new Vector3(BX1 + w, BH, BZ1 + w));
            walls.Box(new Vector3(BX0, 0, BZ1), new Vector3(BX1, BH, BZ1 + w));
            walls.Box(new Vector3(BX0, 0, BZ0 - w), new Vector3(BX1, BH, BZ0));
            floor.Box(new Vector3(BX0 - w, -0.1f, BZ0 - w), new Vector3(BX1 + w, 0, BZ1 + w));
            ceiling.Box(new Vector3(BX0 - w, BH, BZ0 - w), new Vector3(BX1 + w, BH + 0.1f, BZ1 + w));
            foreach (var (z, r) in new[] { (BZ0 + 0.9f, 0.06f), (BZ0 + 1.15f, 0.035f) })
                steel.Cylinder(new Vector3(BX0, BH - 0.2f, z), new Vector3(BX1, BH - 0.2f, z), r, 12, false);

            // the door's frame (the door itself is StageDoor(), its own object; its sign is in Signs())
            float d0 = BDoorX - BDoorW / 2, d1 = BDoorX + BDoorW / 2;
            steel.Box(new Vector3(d0 - 0.05f, 0, BZ0), new Vector3(d0, DoorH + 0.05f, BZ0 + 0.04f));
            steel.Box(new Vector3(d1, 0, BZ0), new Vector3(d1 + 0.05f, DoorH + 0.05f, BZ0 + 0.04f));
            steel.Box(new Vector3(d0 - 0.05f, DoorH, BZ0), new Vector3(d1 + 0.05f, DoorH + 0.05f, BZ0 + 0.04f));

            // the mirror's frame and vanity bulbs (the glass is Mirror())
            float mz = BZ1, x0 = MirrorX - MirrorW / 2, x1 = MirrorX + MirrorW / 2, y0 = F + MirrorY0, y1 = y0 + MirrorH;
            black.Box(new Vector3(x0 - 0.06f, y0 - 0.06f, mz - 0.04f), new Vector3(x1 + 0.06f, y0, mz));
            black.Box(new Vector3(x0 - 0.06f, y1, mz - 0.04f), new Vector3(x1 + 0.06f, y1 + 0.06f, mz));
            black.Box(new Vector3(x0 - 0.06f, y0, mz - 0.04f), new Vector3(x0, y1, mz));
            black.Box(new Vector3(x1, y0, mz - 0.04f), new Vector3(x1 + 0.06f, y1, mz));
            for (float y = y0 + 0.6f; y < y1 + 0.01f; y += 0.3f)
                foreach (float x in new[] { x0 - 0.03f, x1 + 0.03f })
                    bulbs.Sphere(new Vector3(x, y, mz - 0.05f), Vector3.one * 0.028f, 10);
            for (float x = x0 + 0.1f; x < x1; x += 0.2f)
                bulbs.Sphere(new Vector3(x, y1 + 0.03f, mz - 0.05f), Vector3.one * 0.028f, 10);

            // the shelf: a wooden unit against the back wall, left of the mirror, open at the front (Accessories marks it)
            float sx0 = ShelfX1 - ShelfW, sz0 = BZ1 - ShelfD;
            wood.Box(new Vector3(sx0, F, sz0), new Vector3(sx0 + 0.025f, F + ShelfH, BZ1));               // sides
            wood.Box(new Vector3(ShelfX1 - 0.025f, F, sz0), new Vector3(ShelfX1, F + ShelfH, BZ1));
            wood.Box(new Vector3(sx0, F, BZ1 - 0.012f), new Vector3(ShelfX1, F + ShelfH, BZ1));          // back
            wood.Box(new Vector3(sx0, F + ShelfH - 0.025f, sz0), new Vector3(ShelfX1, F + ShelfH, BZ1)); // top
            foreach (float y in ShelfBoards)
                wood.Box(new Vector3(sx0 + 0.025f, F + y - 0.02f, sz0), new Vector3(ShelfX1 - 0.025f, F + y, BZ1 - 0.012f));

            // the right wall holds the opponents' posters (OpponentPosters, hung in the scene)

            // work light: a fluorescent fitting hung from the joists (the light is in Lights())
            foreach (var c in BackstageLights())
            {
                steel.Box(c + new Vector3(-0.65f, 0.02f, -0.08f), c + new Vector3(0.65f, 0.08f, 0.08f));
                bulbs.Box(c + new Vector3(-0.6f, -0.01f, -0.05f), c + new Vector3(0.6f, 0.02f, 0.05f));
                foreach (float dx in new[] { -0.5f, 0.5f })
                    steel.Cylinder(c + new Vector3(dx, 0.08f, 0), new Vector3(c.x + dx, BH - 0.36f, c.z), 0.004f, 4, false);
            }
        }

        /// <summary>Where the dressing room's fluorescent fitting hangs: over the mirror and the shelf.</summary>
        static IEnumerable<Vector3> BackstageLights()
        {
            yield return new Vector3(MirrorSpot.x, BH - 0.45f, BZ1 - 1.4f);
        }

        /// <summary>The mirror's glass: Mirror/Glass is a quad (UVs 0-1, forward into the glass) with a StereoMirror on it.
        /// Spots/MirrorSpot is where the player stands at it.</summary>
        static void Mirror(Transform root, Mats m)
        {
            var go = new GameObject("Mirror");
            go.transform.SetParent(root, false);
            var glass = new GameObject("Glass");
            glass.transform.SetParent(go.transform, false);
            glass.transform.SetPositionAndRotation(new Vector3(MirrorX, MirrorY0 + MirrorH / 2, BZ1 - 0.012f), Quaternion.identity);
            var mesh = new Mesh { name = "MirrorGlass" };
            float w = MirrorW / 2, h = MirrorH / 2;
            mesh.SetVertices(new[] { new Vector3(-w, -h, 0), new Vector3(-w, h, 0), new Vector3(w, h, 0), new Vector3(w, -h, 0) });
            mesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, $"{Gen}/MirrorGlass.asset");
            glass.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mat = new Material(Shader.Find("AvatarDemo/StereoMirror")) { name = "StereoMirror" };
            AssetDatabase.CreateAsset(mat, $"{Gen}/StereoMirror.mat");
            var r = glass.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            // a working mirror (the class's StereoMirror): a camera per eye at its reflection; it reflects everything but
            // itself, the player's own head (MirrorOnly, which the headset leaves out) included
            PlayerBuilder.EnsureLayer("MirrorSurface");
            int surface = LayerMask.NameToLayer("MirrorSurface");
            glass.layer = surface;
            var sm = glass.AddComponent<StereoMirror>();
            sm.size = new Vector2(MirrorW, MirrorH);
            sm.reflect = ~(1 << surface);
        }

        /// <summary>Where the accessories go: Accessories, an empty object in the shelf's frame (origin at its front left
        /// corner on the floor, +x along it toward the mirror, +y up, +z into the wall), with a child per board at its top
        /// (Board0 the bottom). Parent your accessories (Wardrobe/Accessory) to a board.</summary>
        static void Accessories(Transform root)
        {
            var group = new GameObject("Accessories").transform;
            group.SetParent(root, false);
            group.localPosition = new Vector3(ShelfX1 - ShelfW, 0, BZ1 - ShelfD);
            for (int i = 0; i < ShelfBoards.Length; i++)
            {
                var b = new GameObject($"Board{i}").transform;
                b.SetParent(group, false);
                b.localPosition = new Vector3(ShelfW / 2, ShelfBoards[i], (ShelfD - 0.012f) / 2);
            }
        }

        /// <summary>The door to the stage, its own object: the door and its push bar, a box collider, an XR simple
        /// interactable and StageDoor (grab it: fade out, on the stage at Spots/PlayerSpot, fade in).</summary>
        static void BuildStageDoor(Transform root, Mats m)
        {
            var door = new Part("StageDoorLeaf", m.doorMat2, false);
            var bar = new Part("StageDoorBar", m.chromeMat, false);
            float hw = BDoorW / 2;
            door.Box(new Vector3(-hw, 0, 0), new Vector3(hw, DoorH, 0.03f));
            bar.Box(new Vector3(-hw + 0.08f, 0.98f, 0.03f), new Vector3(hw - 0.1f, 1.04f, 0.07f));
            Save(root, "StageDoor", new Vector3(BDoorX, 0, BZ0), true, door, bar);
            var go = root.Find("StageDoor").gameObject;
            go.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
            var sd = go.AddComponent<Freestyle.Venue.StageDoor>();
            sd.destination = root.Find("Spots/PlayerSpot");
            sd.fadeShader = AssetDatabase.LoadAssetAtPath<Shader>(Dir + "/ScreenFade.shader");
        }

        static void Save(Transform group, string name, Vector3 pos, bool collider, params Part[] parts)
        {
            var go = new GameObject(name);
            go.transform.SetParent(group, false);
            go.transform.localPosition = pos;
            var bounds = new Bounds();
            bool first = true;
            var meshes = new List<(Mesh mesh, Material mat)>();
            foreach (var p in parts)
                foreach (var mesh in p.ToMeshes()) meshes.Add((mesh, p.mat));
            foreach (var (mesh, mat) in meshes)
                {
                    var child = meshes.Count == 1 ? go : new GameObject(mesh.name);
                    if (child != go) child.transform.SetParent(go.transform, false);
                    AssetDatabase.CreateAsset(mesh, $"{Gen}/{mesh.name}.asset");
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    child.AddComponent<MeshRenderer>().sharedMaterial = mat;
                    if (first) { bounds = mesh.bounds; first = false; } else bounds.Encapsulate(mesh.bounds);
                }
            if (!collider) return;
            var box = go.AddComponent<BoxCollider>();
            box.center = bounds.center; box.size = bounds.size;
        }

        /// <summary>A box truss over the front of the stage with par cans, hung from the beams.</summary>
        static void StageRig(Part steel, Part bulbs)
        {
            float z = StageZ0 - 0.8f, y = H - 1.1f, x0 = StageX0 - 0.3f, x1 = X1 - 0.3f, s = 0.15f;
            foreach (var (dy, dz) in new[] { (-s, -s), (-s, s), (s, -s), (s, s) })
                steel.Cylinder(new Vector3(x0, y + dy, z + dz), new Vector3(x1, y + dy, z + dz), 0.024f, 8, true);
            bool flip = false;
            for (float x = x0; x < x1 - 0.3f; x += 0.3f, flip = !flip)
                foreach (var (ay, az, by, bz) in new[] { (-s, -s, s, -s), (-s, s, s, s), (-s, -s, -s, s), (s, -s, s, s) })
                    steel.Cylinder(new Vector3(x + (flip ? 0.3f : 0), y + ay, z + az), new Vector3(x + (flip ? 0 : 0.3f), y + by, z + bz), 0.01f, 5, false);
            foreach (float x in new[] { x0 + 0.3f, x1 - 0.3f })
                steel.Cylinder(new Vector3(x, y + s, z), new Vector3(x, H - 0.76f, z), 0.01f, 5, false);
            foreach (var t in ParCans())
            {
                steel.Box(t.pos + new Vector3(-0.015f, 0, -0.015f), new Vector3(t.pos.x + 0.015f, y - s, t.pos.z + 0.015f));   // clamp
                var dir = (t.target - t.pos).normalized;
                steel.Cylinder(t.pos - dir * 0.12f, t.pos + dir * 0.16f, 0.1f, 14, true);
                bulbs.Cylinder(t.pos + dir * 0.16f, t.pos + dir * 0.165f, 0.085f, 14, true);
            }
        }

        /// <summary>The cans on the truss: a warm key on each MC's mark, a colour wash on the back curtain, and one that is only
        /// for looks (lit: false), as the renderer gives each part of the stage 4 lights at most.</summary>
        static IEnumerable<(Vector3 pos, Vector3 target, Color color, bool lit)> ParCans()
        {
            float z = StageZ0 - 0.8f, y = H - 1.1f - 0.3f;
            var mc = new List<(Vector3 pos, float yaw)>(MCs());
            var warm = new Color(1f, 0.82f, 0.62f);
            yield return (new Vector3(StageX0 + 0.4f, y, z), mc[0].pos + Vector3.up * 0.9f, warm, true);
            yield return (new Vector3(StageX0 + 1.7f, y, z), new Vector3((StageX0 + X1) / 2, StageH + 2.2f, StageZ1), new Color(0.75f, 0.2f, 1f), true);
            yield return (new Vector3(X1 - 1.5f, y, z), new Vector3(X1 - 1f, StageH, StageZ1 - 0.5f), new Color(0.25f, 0.45f, 1f), false);
            yield return (new Vector3(X1 - 0.4f, y, z), mc[1].pos + Vector3.up * 0.9f, warm, true);
        }

        static void Signs(Transform root)
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            // the font's own material draws over everything, through walls: Freestyle/SignText is depth-tested and one-sided
            var textMat = new Material(Shader.Find("Freestyle/SignText")) { name = "SignText", mainTexture = font.material.mainTexture };
            AssetDatabase.CreateAsset(textMat, $"{Gen}/SignText.mat");
            void Text(string name, string text, Vector3 pos, float yaw, Color color, float size)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root, false);
                go.transform.SetLocalPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
                var tm = go.AddComponent<TextMesh>();
                tm.font = font; tm.text = text; tm.fontSize = 96; tm.characterSize = size; tm.color = color;
                tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
                go.GetComponent<MeshRenderer>().sharedMaterial = textMat;
            }
            Text("ExitText", "EXIT", new Vector3(FireDoorX, DoorH + 0.245f, Z1 - 0.062f), 0, new Color(1f, 0.1f, 0.05f), 0.02f);
            Text("EntranceExitText", "EXIT", new Vector3(EntranceX, DoorH + 0.245f, Z0 + 0.062f), 180, new Color(1f, 0.1f, 0.05f), 0.02f);
            Text("StageSign", "STAGE", new Vector3(BDoorX, DoorH + 0.22f, BZ0 + 0.012f), 180, new Color(0.9f, 0.9f, 0.9f), 0.014f);
            Text("EmployeesOnly", "EMPLOYEES\nONLY", new Vector3(RoomDoorX, 1.6f, RoomZ1 + 0.005f), 180, new Color(0.85f, 0.85f, 0.85f), 0.009f);
        }

        static void Lights(Transform root)
        {
            var group = new GameObject("Lights").transform;
            group.SetParent(root, false);
            Light L(string name, LightType type, Vector3 pos, Color color, float intensity, float range)
            {
                var go = new GameObject(name);
                go.transform.SetParent(group, false);
                go.transform.localPosition = pos;
                var l = go.AddComponent<Light>();
                l.type = type; l.color = color; l.intensity = intensity; l.range = range;
                return l;
            }
            int i = 0;
            foreach (var t in ParCans())
            {
                if (!t.lit) continue;
                bool key = t.color.g > 0.5f;
                var l = L($"Par{++i}", LightType.Spot, t.pos, t.color, key ? 14f : 10f, 10f);
                l.transform.rotation = Quaternion.LookRotation(t.target - t.pos);
                l.spotAngle = key ? 60 : 75; l.innerSpotAngle = key ? 30 : 45;
                l.shadows = i == 1 ? LightShadows.Soft : LightShadows.None;
            }
            var warm = new Color(1f, 0.72f, 0.45f);
            foreach (float z in new[] { -4f, 1f })
                L("House" + z, LightType.Point, new Vector3(0.3f, H - 1.3f, z), warm, 0.5f, 7f);
            foreach (float z in new[] { -8.8f, -7.0f })
                L("BarPendant" + z, LightType.Point, new Vector3((BarX0 + BarX1) / 2, 2.2f, z), warm, 1.6f, 3.5f);
            L("BackBar", LightType.Point, new Vector3(X0 + 0.5f, 1.5f, (Z0 + BarZ1) / 2 + 0.4f), new Color(1f, 0.6f, 0.3f), 1.2f, 2.5f);
            L("Exit", LightType.Point, new Vector3(FireDoorX, DoorH + 0.2f, Z1 - 0.3f), new Color(1f, 0.15f, 0.1f), 0.6f, 2f);
            int bi = 0;
            foreach (var c in BackstageLights())
                L($"Backstage{++bi}", LightType.Point, c - Vector3.up * 0.1f, new Color(0.95f, 0.97f, 1f), 3.6f, 5f);
            L("BackstageDoor", LightType.Point, new Vector3(BDoorX, 2.6f, BZ0 + 0.3f), new Color(1f, 0.85f, 0.6f), 1f, 2f);
            L("Kitchen", LightType.Point, new Vector3((X0 + KitchenX1) / 2, KitchenH - 0.3f, Z0 - Wall - KitchenDepth / 2), new Color(0.95f, 0.97f, 1f), 1.5f, 5f);
        }

        /// <summary>Where things go: the two MCs on the stage, angled toward the room, and the crowd's middle.</summary>
        static void Spots(Transform root)
        {
            var group = new GameObject("Spots").transform;
            group.SetParent(root, false);
            void S(string name, Vector3 pos, float yaw)
            {
                var go = new GameObject(name);
                go.transform.SetParent(group, false);
                go.transform.SetLocalPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            }
            var mc = new List<(Vector3 pos, float yaw)>(MCs());
            S("PlayerSpot", mc[0].pos, mc[0].yaw);
            S("OpponentSpot", mc[1].pos, mc[1].yaw);
            S("DJSpot", DJ().pos, DJ().yaw);
            S("CrowdCenter", new Vector3(0.6f, 0, 2.5f), 0);
            S("MirrorSpot", MirrorSpot, 0);                // backstage, facing the mirror
            S("BackstageArrival", new Vector3(BDoorX, 0, BZ0 + 0.8f), 0);   // inside its door, facing in
            S("Audience", new Vector3(0.6f, 0, -2.5f), 0);
        }

        static void MakeScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var venue = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/Venue.prefab"));
            // the player (Freestyle > Build VR Player) starts in the dressing room, inside its door; else a plain camera
            var player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerBuilder.PrefabPath);
            if (player)
            {
                var arrival = venue.transform.Find("Spots/BackstageArrival");
                var p = (GameObject)PrefabUtility.InstantiatePrefab(player);
                p.transform.SetPositionAndRotation(arrival.position, arrival.rotation);
                p.GetComponentInChildren<Camera>().clearFlags = CameraClearFlags.SolidColor;
                p.GetComponentInChildren<Camera>().backgroundColor = Color.black;
            }
            else
            {
                var cam = new GameObject("Main Camera") { tag = "MainCamera" };
                var c = cam.AddComponent<Camera>();
                c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = Color.black; c.nearClipPlane = 0.05f;
                cam.AddComponent<AudioListener>();
                cam.transform.SetPositionAndRotation(new Vector3(0.6f, 1.7f, -3.5f), Quaternion.LookRotation(new Vector3(0.4f, 0.9f, 9f) - new Vector3(0.6f, 1.7f, -3.5f)));
            }
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.1f, 0.1f, 0.11f);
            OpponentPosters.Hang();
            StageMoveArea.Add();
            BattleSetup.SetUp();
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Venue.unity");
        }

        // ---------------------------------------------------------------- previews (batch mode)

        /// <summary>Unity -batchmode -executeMethod Freestyle.Venue.VenueBuilder.BuildAndPreview; PNGs into $VENUE_PREVIEW_DIR.</summary>
        public static void BuildAndPreview()
        {
            Build();
            EditorSceneManager.OpenScene("Assets/Scenes/Venue.unity");
            string outDir = System.Environment.GetEnvironmentVariable("VENUE_PREVIEW_DIR") ?? "Temp";
            Directory.CreateDirectory(outDir);
            var views = new (string name, Vector3 pos, Vector3 at, float fov)[]
            {
                ("room", new Vector3(0.6f, 1.7f, -4f), new Vector3(0.6f, 1.2f, 9f), 75),
                ("stage", new Vector3(-0.5f, 1.7f, 3.5f), new Vector3(1.2f, 1.0f, 9.5f), 70),
                ("bar", new Vector3(1.8f, 1.7f, -3.5f), new Vector3(-2.2f, 1.1f, -8.5f), 75),
                ("from_stage", new Vector3(1.3f, StageH + 1.7f, 9.2f), new Vector3(0f, 1.0f, -6f), 80),
                ("player_eyes", new Vector3(0.1f, StageH + 1.65f, 8.95f), new Vector3(0.6f, StageH + 0.2f, 7.6f), 90),
                ("dj_front", new Vector3(1.0f, 1.7f, 4.5f), new Vector3(1.3f, StageH + 1.0f, StageZ1 - 0.8f), 55),
                ("dj_side", new Vector3(StageX0 + 0.25f, StageH + 1.5f, StageZ1 - 1.0f), new Vector3(X1, StageH + 0.8f, StageZ1 - 1.0f), 80),
                ("dj_eyes", DJ().pos + Vector3.up * 1.62f, new Vector3(1.1f, StageH + 0.6f, 7.5f), 85),
                ("dj_table", new Vector3(1.9f, StageH + 1.45f, StageZ1 - 0.35f), new Vector3(1.4f, StageH + 0.95f, DJTableZ), 70),
                ("ceiling", new Vector3(0f, 1.6f, 0f), new Vector3(0.5f, H, 4f), 85),
                ("steps", new Vector3(-3f, 1.6f, 5.5f), new Vector3(-1.6f, 0.4f, 9f), 70),
                ("entrance", new Vector3(0.6f, 1.7f, -5.5f), new Vector3(-0.1f, 1.2f, -9.9f), 70),
                ("backstage", new Vector3(BDoorX - 0.3f, 1.7f, BZ0 + 0.35f), new Vector3(BX0 + 1.6f, 0.8f, BZ1 - 1f), 95),
                ("backstage_mirror", MirrorSpot + Vector3.up * 1.6f, new Vector3(MirrorX - 0.3f, 1.15f, BZ1), 90),
                ("backstage_shelf", MirrorSpot + new Vector3(0.3f, 1.6f, 0.1f), new Vector3(ShelfX1 - ShelfW / 2, 1.0f, BZ1 - ShelfD / 2), 80),
            };
            // 1.75 m stand-ins, robot-thick: the DJ, and the opponent
            var standIns = new List<GameObject>();
            foreach (var (pos, _) in new[] { DJ(), (new List<(Vector3, float)>(MCs()))[1] })
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                c.transform.position = pos + Vector3.up * 0.875f;
                c.transform.localScale = new Vector3(0.6f, 0.875f, 0.45f);
                standIns.Add(c);
            }
            var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            var go = new GameObject("PreviewCam");
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black; cam.nearClipPlane = 0.05f;
            cam.targetTexture = rt;
            foreach (var v in views)
            {
                standIns[0].SetActive(v.name != "dj_eyes");
                go.transform.SetPositionAndRotation(v.pos, Quaternion.LookRotation(v.at - v.pos));
                cam.fieldOfView = v.fov;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(outDir, v.name + ".png"), tex.EncodeToPNG());
            }
            RenderTexture.active = null;
            Object.DestroyImmediate(go);
        }

        // ---------------------------------------------------------------- materials

        class Mats
        {
            public Material concrete, wall, curtain, cloth, steel, ceiling, stageWood, barBlack, barTop, chrome, door, led, bulb,
                            amber, green, clear, kitchenWall, kitchenFloor, exitSign, screen, tape, monitor, street, doorMat;

            readonly Dictionary<string, Material> extra = new();
            public Material chromeMat => chrome; public Material steelMat => steel; public Material bulbMat => bulb;
            public Material blackMat => barBlack; public Material woodMat => barTop; public Material clearMat => clear; public Material doorMat2 => door;

            /// <summary>A plain material by name, made once.</summary>
            public Material Get(string name, Color color, float smooth = 0.5f, float metal = 0, Color? emission = null)
            {
                if (!extra.TryGetValue(name, out var mat)) extra[name] = mat = Make(name, color, smooth, metal, null, null, 1, emission);
                return mat;
            }

            public Mats()
            {
                var wood = AssetDatabase.LoadAssetAtPath<Texture2D>(WoodTex);
                var woodN = AssetDatabase.LoadAssetAtPath<Texture2D>(WoodNormal);
                var conc = AssetDatabase.LoadAssetAtPath<Texture2D>(ConcreteTex);
                concrete = Make("Concrete", new Color(0.55f, 0.55f, 0.55f), 0.45f, 0, conc, null, 1 / 2.5f);    // painted black, a little worn
                wall = Make("WallBlack", new Color(0.03f, 0.03f, 0.03f), 0.2f);
                curtain = Make("CurtainVelvet", new Color(0.6f, 0.02f, 0.04f), 0.3f);   // theatre red
                cloth = Make("DJCloth", new Color(0.02f, 0.02f, 0.022f), 0.12f);
                steel = Make("SteelBlack", new Color(0.035f, 0.035f, 0.035f), 0.4f, 0.5f);
                ceiling = Make("CeilingBlack", new Color(0.025f, 0.025f, 0.025f), 0.15f, 0.2f);
                stageWood = Make("StageWood", Color.white, 0.35f, 0, wood, woodN, 1 / 1.2f);                 // boards ~14 cm
                barBlack = Make("BarBlack", new Color(0.04f, 0.04f, 0.04f), 0.3f);
                barTop = Make("BarTop", new Color(0.75f, 0.6f, 0.5f), 0.75f, 0, wood, woodN, 1 / 1.2f);      // the boards, varnished
                chrome = Make("Steel", new Color(0.75f, 0.75f, 0.75f), 0.7f, 1f);
                door = Make("DoorSteel", new Color(0.08f, 0.08f, 0.085f), 0.4f, 0.4f);
                led = Make("LedWarm", Color.black, 0.5f, 0, null, null, 1, new Color(1f, 0.55f, 0.25f) * 3f);
                bulb = Make("Bulb", Color.black, 0.5f, 0, null, null, 1, new Color(1f, 0.8f, 0.6f) * 4f);
                amber = Make("GlassAmber", new Color(0.35f, 0.15f, 0.03f), 0.92f, 0.1f);
                green = Make("GlassGreen", new Color(0.04f, 0.18f, 0.07f), 0.92f, 0.1f);
                clear = Make("GlassClear", new Color(0.55f, 0.6f, 0.6f), 0.95f, 0.3f);
                kitchenWall = Make("KitchenWall", new Color(0.8f, 0.8f, 0.78f), 0.35f);
                kitchenFloor = Make("KitchenFloor", new Color(0.35f, 0.17f, 0.12f), 0.35f);                  // quarry tile
                screen = Make("Screen", Color.black, 0.9f, 0, null, null, 1, new Color(0.3f, 0.5f, 1f) * 1.5f);
                monitor = Make("MonitorScreen", Color.black, 0.85f, 0, null, null, 1, new Color(0.05f, 0.09f, 0.16f));   // on, nothing showing
                street = Make("StreetGlow", Color.black, 0.9f, 0, null, null, 1, new Color(0.55f, 0.38f, 0.18f) * 0.6f);   // sodium light outside
                doorMat = Make("DoorMat", new Color(0.05f, 0.045f, 0.04f), 0.05f);
                tape = Make("GaffTape", new Color(0.85f, 0.85f, 0.8f), 0.3f);
                exitSign = Make("ExitSign", new Color(0.05f, 0.05f, 0.05f), 0.5f, 0, null, null, 1, new Color(0.25f, 0.02f, 0.01f));
            }

            static Material Make(string name, Color color, float smooth, float metal = 0, Texture2D tex = null, Texture2D normal = null,
                                 float scale = 1, Color? emission = null)
            {
                var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                m.SetColor("_BaseColor", color);
                m.SetFloat("_Smoothness", smooth);
                m.SetFloat("_Metallic", metal);
                if (tex)
                {
                    m.SetTexture("_BaseMap", tex);
                    m.SetTextureScale("_BaseMap", Vector2.one * scale);
                }
                if (normal)
                {
                    m.SetTexture("_BumpMap", normal);
                    m.SetTextureScale("_BumpMap", Vector2.one * scale);
                    m.EnableKeyword("_NORMALMAP");
                }
                if (emission is Color e)
                {
                    m.SetColor("_EmissionColor", e);
                    m.EnableKeyword("_EMISSION");
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }
                AssetDatabase.CreateAsset(m, $"{Gen}/{name}.mat");
                return m;
            }
        }

        // ---------------------------------------------------------------- mesh building

        /// <summary>One mesh with one material. UVs are in metres on each face, so textures tile at their real size.</summary>
        class Part
        {
            public readonly string name; public readonly Material mat; public readonly bool collider;
            readonly List<Vector3> v = new(), n = new();
            readonly List<Vector2> uv = new();
            readonly List<int> t = new();
            public Part(string name, Material mat, bool collider) { this.name = name; this.mat = mat; this.collider = collider; }
            public bool Empty => v.Count == 0;

            static Vector2 UV(Vector3 q, Vector3 nrm) =>
                Mathf.Abs(nrm.y) > 0.7f ? new Vector2(q.x, q.z) : Mathf.Abs(nrm.x) > Mathf.Abs(nrm.z) ? new Vector2(q.z, q.y) : new Vector2(q.x, q.y);

            /// <summary>The quad p, p+du, p+du+dv, p+dv, facing cross(du, dv).</summary>
            public void Quad(Vector3 p, Vector3 du, Vector3 dv)
            {
                var nrm = Vector3.Cross(du, dv).normalized;
                int i = v.Count;
                foreach (var q in new[] { p, p + du, p + du + dv, p + dv }) { v.Add(q); n.Add(nrm); uv.Add(UV(q, nrm)); }
                t.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }

            public void Box(Vector3 a, Vector3 b)
            {
                var X = new Vector3(b.x - a.x, 0, 0); var Y = new Vector3(0, b.y - a.y, 0); var Z = new Vector3(0, 0, b.z - a.z);
                Quad(a + Y, Z, X); Quad(a, X, Z);   // top, bottom
                Quad(a + X, Y, Z); Quad(a, Z, Y);   // +x, -x
                Quad(a + Z, X, Y); Quad(a, Y, X);   // +z, -z
            }

            /// <summary>A box of this size around c, turned by r.</summary>
            public void OBox(Vector3 c, Quaternion r, Vector3 size)
            {
                var X = r * new Vector3(size.x, 0, 0); var Y = r * new Vector3(0, size.y, 0); var Z = r * new Vector3(0, 0, size.z);
                var a = c - (X + Y + Z) / 2;
                Quad(a + Y, Z, X); Quad(a, X, Z);
                Quad(a + X, Y, Z); Quad(a, Z, Y);
                Quad(a + Z, X, Y); Quad(a, Y, X);
            }

            /// <summary>An ellipsoid around c (radii per axis), between latitudes lat0 and lat1 (degrees; 0..90 is a dome).</summary>
            public void Sphere(Vector3 c, Vector3 radii, int seg = 14, float lat0 = -90, float lat1 = 90)
            {
                int rings = Mathf.Max(2, Mathf.RoundToInt(seg * (lat1 - lat0) / 180f)), i0 = v.Count;
                for (int a = 0; a <= rings; a++)
                {
                    float lat = Mathf.Deg2Rad * Mathf.Lerp(lat0, lat1, a / (float)rings);
                    for (int b = 0; b <= seg; b++)
                    {
                        float lon = 2 * Mathf.PI * b / seg;
                        var dir = new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon));
                        v.Add(c + Vector3.Scale(dir, radii));
                        n.Add(new Vector3(dir.x / radii.x, dir.y / radii.y, dir.z / radii.z).normalized);
                        uv.Add(new Vector2(lon * radii.x, lat * radii.y));
                    }
                }
                for (int a = 0; a < rings; a++)
                    for (int b = 0; b < seg; b++)
                    {
                        int p = i0 + a * (seg + 1) + b, q = p + seg + 1;
                        t.AddRange(new[] { p, q, q + 1, p, q + 1, p + 1 });
                    }
            }

            public void Cylinder(Vector3 a, Vector3 b, float r, int sides, bool caps)
            {
                var ax = b - a; float len = ax.magnitude; var d = ax / len;
                var e1 = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                var e2 = Vector3.Cross(d, e1);
                int i0 = v.Count;
                for (int k = 0; k <= sides; k++)
                {
                    float ang = 2 * Mathf.PI * k / sides;
                    var rad = Mathf.Cos(ang) * e1 + Mathf.Sin(ang) * e2;
                    v.Add(a + rad * r); n.Add(rad); uv.Add(new Vector2(ang * r, 0));
                    v.Add(b + rad * r); n.Add(rad); uv.Add(new Vector2(ang * r, len));
                }
                for (int k = 0; k < sides; k++)
                {
                    int q = i0 + 2 * k;
                    t.AddRange(new[] { q, q + 2, q + 3, q, q + 3, q + 1 });
                }
                if (!caps) return;
                foreach (var (c, nrm) in new[] { (b, d), (a, -d) })
                {
                    int ci = v.Count;
                    v.Add(c); n.Add(nrm); uv.Add(Vector2.zero);
                    for (int k = 0; k < sides; k++)
                    {
                        float ang = 2 * Mathf.PI * k / sides;
                        var rad = Mathf.Cos(ang) * e1 + Mathf.Sin(ang) * e2;
                        v.Add(c + rad * r); n.Add(nrm); uv.Add(new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r);
                    }
                    for (int k = 0; k < sides; k++)
                    {
                        int p0 = ci + 1 + k, p1 = ci + 1 + (k + 1) % sides;
                        if (nrm == d) t.AddRange(new[] { ci, p0, p1 }); else t.AddRange(new[] { ci, p1, p0 });
                    }
                }
            }

            /// <summary>A pleated drape from a to b (on the floor), hanging off the wall toward inward, from y0 to y1.</summary>
            public void Curtain(Vector3 a, Vector3 b, Vector3 inward, float y0, float y1, int seed)
            {
                var dir = b - a; float len = dir.magnitude; dir /= len;
                const float pleat = 0.3f;
                int segs = Mathf.CeilToInt(len / 0.03f), i0 = v.Count;
                for (int k = 0; k <= segs; k++)
                {
                    float s = len * k / segs;
                    float ph = 2 * Mathf.PI * s / pleat + 2.5f * Mathf.PerlinNoise(s * 0.6f, seed * 13.1f);
                    float amp = 0.03f + 0.025f * Mathf.PerlinNoise(s * 1.3f, seed * 7.7f + 3);
                    float off = 0.12f + amp * Mathf.Sin(ph);
                    float slope = amp * Mathf.Cos(ph) * 2 * Mathf.PI / pleat;
                    var nrm = (inward - dir * slope).normalized;
                    var p = a + dir * s + inward * off;
                    p.y = y0; v.Add(p); n.Add(nrm); uv.Add(new Vector2(s, y0));
                    p.y = y1; v.Add(p); n.Add(nrm); uv.Add(new Vector2(s, y1));
                }
                bool facing = Vector3.Dot(Vector3.Cross(dir, Vector3.up), inward) > 0;
                for (int k = 0; k < segs; k++)
                {
                    int q = i0 + 2 * k;
                    if (facing) t.AddRange(new[] { q, q + 2, q + 3, q, q + 3, q + 1 });
                    else t.AddRange(new[] { q, q + 3, q + 2, q, q + 1, q + 3 });
                }
            }

            /// <summary>The meshes, one per ChunkZ of the room's length (by where each triangle is).</summary>
            public IEnumerable<Mesh> ToMeshes()
            {
                var chunks = new SortedDictionary<int, List<int>>();
                for (int i = 0; i < t.Count; i += 3)
                {
                    float z = (v[t[i]].z + v[t[i + 1]].z + v[t[i + 2]].z) / 3;
                    int k = Mathf.FloorToInt((z - Z0) / ChunkZ);
                    if (!chunks.TryGetValue(k, out var list)) chunks[k] = list = new List<int>();
                    list.Add(t[i]); list.Add(t[i + 1]); list.Add(t[i + 2]);
                }
                foreach (var (k, tris) in chunks)
                {
                    var map = new Dictionary<int, int>();
                    var cv = new List<Vector3>(); var cn = new List<Vector3>(); var cuv = new List<Vector2>(); var ct = new List<int>();
                    foreach (int i in tris)
                    {
                        if (!map.TryGetValue(i, out int j)) { map[i] = j = cv.Count; cv.Add(v[i]); cn.Add(n[i]); cuv.Add(uv[i]); }
                        ct.Add(j);
                    }
                    var m = new Mesh { name = chunks.Count == 1 ? name : $"{name}_{k}", indexFormat = IndexFormat.UInt32 };
                    m.SetVertices(cv); m.SetNormals(cn); m.SetUVs(0, cuv); m.SetTriangles(ct, 0);
                    m.RecalculateTangents();
                    m.RecalculateBounds();
                    yield return m;
                }
            }
        }
    }
}
