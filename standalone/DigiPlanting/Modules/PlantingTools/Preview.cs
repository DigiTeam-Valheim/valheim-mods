// Planting Tools, the preview: the grid's plants drawn as copies of the sapling, straight to the graphics card in batches
// of up to 1023 (GPU instancing): no game objects, so a 50 x 50 grid costs a few draw calls instead of 2,500 objects.
// Drawn with tinted copies of the plant's own materials (the game builds those shaders with instancing; a see-through
// Sprites/Default copy drew every plant on one spot, its instancing variant is not in the game): a green glow = plants,
// yellow = no seeds left for it, red = cannot grow there (the game's own red for a bad ghost), grey = queued and being
// planted. The first plant of the grid is the game's own ghost.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PlantingTools
{
    internal static class Preview
    {
        private enum Look { Ok, NoSeeds, Bad, Queued }

        private sealed class Part
        {
            public Mesh Mesh;
            public int Sub;
            public Matrix4x4 Local;
            public Material[] Materials; // per look
        }

        // per look: the colour (null = the plant's own) and the glow
        private static readonly Color?[] Colors = { null, new Color(1f, 0.8f, 0.2f), Color.red, new Color(0.45f, 0.45f, 0.45f) };
        private static readonly Color[] Glows = { new Color(0.08f, 0.3f, 0.08f), new Color(0.6f, 0.45f, 0f), Color.red * 0.7f, Color.black };
        private static readonly int ColorId = Shader.PropertyToID("_Color"), GlowId = Shader.PropertyToID("_EmissionColor");

        private static readonly List<Part> Parts = new List<Part>();
        private static readonly List<Material> Made = new List<Material>();
        private static readonly List<Matrix4x4>[] Cells = { new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>(), new List<Matrix4x4>() };
        private static Matrix4x4[] _batch = new Matrix4x4[1023];
        private static readonly Vector3 One = Vector3.one; // the parts carry the ghost's own scale
        private static string _for;
        private static int _gridVersion = -1, _planted = -1;
        private static bool _instancingFailed;
        public static float LastDrawMs;   // for tests
        public static int LastInstances;  // for tests

        // The ghost's visible meshes (the near look only), relative to the ghost.
        public static void Build(GameObject ghost)
        {
            if (ghost == null) return;
            if (_for == ghost.name && Parts.Count > 0) return;
            Clear();
            _for = ghost.name;
            var toRoot = Matrix4x4.TRS(ghost.transform.position, ghost.transform.rotation, Vector3.one).inverse;
            var lod = ghost.GetComponentInChildren<LODGroup>();
            HashSet<Renderer> near = null;
            if (lod != null && lod.GetLODs().Length > 0) near = new HashSet<Renderer>(lod.GetLODs()[0].renderers);
            var tinted = new Dictionary<Material, Material[]>();
            foreach (var r in ghost.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (near != null && r.GetComponentInParent<LODGroup>() == lod && !near.Contains(r)) continue;
                var filter = r.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                var mats = r.sharedMaterials;
                for (var sub = 0; sub < filter.sharedMesh.subMeshCount && sub < mats.Length; sub++)
                {
                    var original = mats[sub];
                    if (original == null) continue;
                    if (!tinted.TryGetValue(original, out var looks))
                    {
                        looks = new Material[Colors.Length];
                        for (var l = 0; l < Colors.Length; l++)
                        {
                            var m = new Material(original); // same shader and keywords, so the same instanced variant
                            if (Colors[l].HasValue && m.HasProperty(ColorId)) m.SetColor(ColorId, Colors[l].Value);
                            if (m.HasProperty(GlowId)) m.SetColor(GlowId, Glows[l]);
                            looks[l] = m;
                            Made.Add(m);
                        }
                        tinted[original] = looks;
                    }
                    Parts.Add(new Part { Mesh = filter.sharedMesh, Sub = sub, Local = toRoot * r.localToWorldMatrix, Materials = looks });
                }
            }
            _gridVersion = -1;
            _planted = -1;
        }

        public static void Clear()
        {
            foreach (var m in Made)
                if (m != null) UnityEngine.Object.Destroy(m);
            Made.Clear();
            Parts.Clear();
            _for = null;
            foreach (var list in Cells) list.Clear();
            _gridVersion = -1;
            _planted = -1;
        }

        // For tests: what the preview draws with.
        public static string Describe()
        {
            var parts = new List<string>();
            foreach (var p in Parts)
                parts.Add($"{p.Mesh.name} sub {p.Sub} ({p.Mesh.vertexCount} vertices, bounds {p.Mesh.bounds.size}, scale {p.Local.lossyScale}, " +
                          $"shader {p.Materials[0].shader.name}, instancing {p.Materials[0].enableInstancing})");
            return $"{Parts.Count} parts: {string.Join("; ", parts)}; instancing failed {_instancingFailed}";
        }

        public static void Draw()
        {
            LastInstances = 0;
            if (Parts.Count == 0 || !Plugin.ShowPreview) { LastDrawMs = 0f; return; }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            DrawAll();
            LastDrawMs = (float)watch.Elapsed.TotalMilliseconds;
        }

        private static void DrawAll()
        {
            if (Grid.Active && _gridVersion != Grid.Version)
            {
                _gridVersion = Grid.Version;
                Cells[(int)Look.Ok].Clear();
                Cells[(int)Look.NoSeeds].Clear();
                Cells[(int)Look.Bad].Clear();
                for (var i = 1; i < Grid.Count; i++)
                {
                    var spot = Grid.Spots[i];
                    if (spot == Spot.Taken) continue; // the queue draws those
                    var look = spot == Spot.Ok ? Look.Ok : spot == Spot.NoSeeds ? Look.NoSeeds : Look.Bad;
                    Cells[(int)look].Add(Matrix4x4.TRS(Grid.Positions[i], Grid.Rotation, One));
                }
            }
            if (_planted != Planter.Planted || (Planter.Left > 0 && Cells[(int)Look.Queued].Count != Planter.Left) || (Planter.Left == 0 && Cells[(int)Look.Queued].Count > 0))
            {
                _planted = Planter.Planted;
                Planter.CopyQueued(Cells[(int)Look.Queued], One);
            }
            for (var l = 0; l < Cells.Length; l++)
            {
                if (l != (int)Look.Queued && !Grid.Active) continue;
                var cells = Cells[l];
                if (cells.Count == 0) continue;
                foreach (var part in Parts) DrawPart(part, part.Materials[l], cells);
            }
        }

        private static void DrawPart(Part part, Material material, List<Matrix4x4> cells)
        {
            var rp = new RenderParams(material) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, layer = 0 };
            for (var start = 0; start < cells.Count; start += _batch.Length)
            {
                var n = Mathf.Min(_batch.Length, cells.Count - start);
                LastInstances += n;
                for (var k = 0; k < n; k++) _batch[k] = cells[start + k] * part.Local;
                if (!_instancingFailed && material.enableInstancing)
                {
                    try
                    {
                        Graphics.RenderMeshInstanced(rp, part.Mesh, part.Sub, _batch, n);
                        continue;
                    }
                    catch (Exception e)
                    {
                        _instancingFailed = true;
                        Plugin.Log?.LogWarning("Drawing the grid in batches failed, drawing one by one (at most 1500): " + e.Message);
                    }
                }
                for (var k = 0; k < n && start + k < 1500; k++) Graphics.RenderMesh(rp, part.Mesh, part.Sub, _batch[k]);
            }
        }
    }
}
