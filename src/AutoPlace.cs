using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using System.Threading.Tasks;

namespace DressmakerAutoSew
{
    public static class AutoPlace
    {
        const float Cell = 0.003f;
        const int Gap = 1;
        const int NodeBudget = 8000;
        const int MaxGridCells = 64000000;
        static bool _solving;
        static bool _searchLimited;
        static int _lastBeat;
        static Task _searchTask;
        static bool _lengthCapped;
        static bool _mustFinish;
        
        static AutoSewRunner _runner;

        static readonly List<Collider2D> _hits = new List<Collider2D>();
        static ContactFilter2D _clearanceFilter;
        private static bool _filterReady;
       
        // True when no allowed orientation of the piece fits across the fabric's width,
        // so no amount of extra length or trimming will ever help.
        static bool TooWide(Piece piece, int rows)
        {
            foreach (Stamp stamp in piece.Orients)
                if (stamp.MaxY < rows)
                    return false;
            return true;
        }
        
        // The exact test PatternPanel.Update uses for the orange warning:
        // does this piece's outline overlap the fabric edges, another piece or a cut hole?
        static bool TooClose(object panel)
        {
            var col = GameAccess.Member(panel, "PatternCollider") as Collider2D;
            if (col == null)
                return false;
            if (!_filterReady)
            {
                _clearanceFilter = new ContactFilter2D();
                _clearanceFilter.useLayerMask = true;
                _clearanceFilter.layerMask = LayerMask.GetMask("Bounds", "Default", "Cutout");
                _filterReady = true;
            }

            Physics2D.SyncTransforms();
            int count = col.Overlap(_clearanceFilter, _hits);
            for (int i = 0; i < count; i++)
            {
                // The cover past the fabric's right end may still be mid-unroll. Snap only moves
                // pieces left or down, so it can never cross that end anyway.
                Transform parent = _hits[i].transform.parent;
                if (parent != null && parent.name == "FabricPieceLengthObscure")
                    continue;
                return true;
            }
            return false;
        }
        
        const int SnapMaxSteps = 40;

        // Directions a piece may slide. Every one moves it left or down,
        // so pieces only ever travel toward the start of the bolt.
        static readonly Vector3[] SnapDirs = BuildSnapDirs();

        static Vector3[] BuildSnapDirs()
        {
            var dirs = new List<Vector3> { Vector3.left };
            for (int a = 15; a <= 75; a += 15)
            {
                float r = a * Mathf.Deg2Rad;
                dirs.Add(new Vector3(-Mathf.Cos(r), -Mathf.Sin(r), 0f)); // below left
                dirs.Add(new Vector3(-Mathf.Cos(r),  Mathf.Sin(r), 0f)); // above left
            }
            dirs.Add(Vector3.down);
            return dirs.ToArray();
        }

        // Big steps first to cover distance quickly, then small ones to get really close.
        static readonly float[] SnapSteps = { 0.005f, 0.001f, 0.0005f };  // 5 mm, 1 mm, 0.5 mm

        // Slide one direction until the game says "too close", then step back once.
        static bool Slide(object panel, Transform t, Vector3 delta)
        {
            int steps = 0;
            while (steps < SnapMaxSteps)
            {
                t.position += delta;
                if (TooClose(panel))
                {
                    t.position -= delta;
                    break;
                }
                steps++;
            }
            return steps > 0;
        }

        // Pull a placed piece left and down as far as the game allows.
        static void Snap(object panel)
        {
            var world = GameAccess.Member(panel, "WorldObj") as GameObject;
            if (world == null)
                return;
            if (TooClose(panel))
            {
                Plugin.Log.LogDebug("Snap: blocked before moving, touching " + _hits[0].name
                                                                             + " (parent: " + (_hits[0].transform.parent != null ? _hits[0].transform.parent.name : "none")
                                                                             + ", own piece: " + _hits[0].transform.IsChildOf(world.transform) + ")");
                return;
            }
            Transform t = world.transform;
            for (int round = 0; round < 6; round++)
            {
                bool moved = false;
                foreach (float step in SnapSteps)
                foreach (Vector3 dir in SnapDirs)
                    moved |= Slide(panel, t, dir * step);
                if (!moved)
                    break;
            }
        }
        
        static MonoBehaviour Runner()
        {
            if (_runner == null)
            {
                var go = new GameObject("AutoSew Runner");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _runner = go.AddComponent<AutoSewRunner>();
            }
            return _runner;
        }

        static readonly Dictionary<int, object> Assign = new Dictionary<int, object>();
        static readonly HashSet<int> Decided = new HashSet<int>();
        static readonly List<Choice> Best = new List<Choice>();

        static string _notice = "";
        static bool _menuLogged;
        static int _nodes;
        static int _bestCount;
        static float _bestLength;
        static float _bestTop;
        static float _bestSpread;
        static Stopwatch _nestClock;
        static int NestBudgetMs = 4000;
        static readonly Stopwatch _paintClock = new Stopwatch();
        static readonly Stopwatch _poseClock = new Stopwatch();

        // The search uses only occupied/free state; piece IDs were never read.
        // One bit per cell keeps long fresh bolts affordable without coarsening.
        sealed class Grid
        {
            readonly ulong[] _bits;
            readonly int _cols;
            public int Length { get; private set; }
            public int LastOccupiedCol { get; private set; }

            public Grid(int length, int cols)
            {
                if (length <= 0 || cols <= 0 || length % cols != 0 || length > MaxGridCells)
                    throw new ArgumentOutOfRangeException(nameof(length));
                Length = length;
                _cols = cols;
                _bits = new ulong[(length + 63) / 64];
                LastOccupiedCol = -1;
            }

            public int this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)Length)
                        throw new IndexOutOfRangeException();
                    return (_bits[index >> 6] & (1UL << (index & 63))) == 0 ? 0 : 1;
                }
                set
                {
                    if ((uint)index >= (uint)Length)
                        throw new IndexOutOfRangeException();
                    ulong mask = 1UL << (index & 63);
                    if (value == 0)
                        _bits[index >> 6] &= ~mask;
                    else
                    {
                        _bits[index >> 6] |= mask;
                        LastOccupiedCol = Mathf.Max(LastOccupiedCol, index % _cols);
                    }
                }
            }

            public Grid Clone()
            {
                var copy = new Grid(Length, _cols);
                CopyTo(copy);
                return copy;
            }

            public void CopyTo(Grid target)
            {
                if (target.Length != Length || target._cols != _cols)
                    throw new ArgumentException("Packing grids must have matching dimensions.");
                Array.Copy(_bits, target._bits, _bits.Length);
                target.LastOccupiedCol = LastOccupiedCol;
            }
        }

        struct Choice
        {
            public int Piece;
            public int Orient;
            public int Col;
            public int Row;
        }

        sealed class Stamp
        {
            public Quaternion Rotation;
            public Vector2Int[] Cells;
            public int PadX;
            public int PadY;
            public float VisualW;
            public float VisualH;
            public int MaxX;
            public int MaxY;
            public Vector2Int[] Left;
            public Vector2Int[] Down;
            public Vector2Int[] Halo;
        }

        sealed class Piece
        {
            public object Panel;
            public GameObject World;
            public Stamp[] Orients;
        }

        public static void ClearCutHistory()
        {
            // Hole masks are owned and updated by CuttingRoom, not cached here.
        }

        sealed class SavedPose
        {
            public object Panel;
            public GameObject World;
            public Vector3 Position;
            public Quaternion Rotation;
            public object Fabric;
            public bool InWorld;
        }

        public static void Tick(MonoBehaviour __instance)
        {
            try
            {
                PlaceBoard.Tick(__instance);
            }
            catch (Exception ex)
            {
                if (_menuLogged)
                    return;
                _menuLogged = true;
                Plugin.Log.LogWarning("Pattern menu stopped: " + ex.Message);
            }
        }

        public static string Notice => _notice;

        public static void SetNotice(string message)
        {
            _notice = message ?? "";
        }

        public static void ClearVisit()
        {
            Assign.Clear();
            Decided.Clear();
            _notice = "";
        }

        public static object PatternFabric(object panel)
        {
            if (panel == null)
                return null;
            Assign.TryGetValue(Id(panel), out object fabric);
            return fabric;
        }

        public static void SetPatternFabric(object panel, object fabric)
        {
            if (!IsUncutPattern(panel))
                return;
            Assign[Id(panel)] = fabric;
            Decided.Add(Id(panel));
        }

        public static void UseOpenFabric(List<object> patterns, object fabric)
        {
            Remember(patterns, fabric);
        }

        public static void Run(MonoBehaviour room, object game, List<object> fabrics, List<object> patterns, object showFabric)
        {
            RunPlacement(room, game, fabrics, patterns, showFabric, false);
        }

        public static void Optimize(MonoBehaviour room, object game, List<object> fabrics, List<object> patterns, object showFabric)
        {
            RunPlacement(room, game, fabrics, patterns, showFabric, true);
        }

        static void RunPlacement(MonoBehaviour room, object game, List<object> fabrics, List<object> patterns, object showFabric, bool optimize)
        {
            // A routine that hasn't checked in for a few frames was stopped by Unity
            // (e.g. the player left the cutting room). Recover, but only after its
            // background search has finished, so two searches never run at once.
            if (_solving && Time.frameCount - _lastBeat > 2
                         && (_searchTask == null || _searchTask.IsCompleted))
            {
                Finish();
                _notice = "";
            }
            if (_solving)
                return;
            _solving = true;
            _searchLimited = false;
            _lengthCapped = false;
            var saved = new List<SavedPose>();
            try
            {
                Plugin.Log.LogInfo("Layout requested: " + (optimize ? "Optimize" : "Solve"));
                // A button may retain a stale list after the player cuts a piece.
                patterns = UncutPatterns(patterns);
                foreach (object panel in patterns)
                {
                    var world = GameAccess.Member(panel, "WorldObj") as GameObject;
                    saved.Add(new SavedPose { Panel = panel, World = world,
                            Position = world != null ? world.transform.position : Vector3.zero,
                            Rotation = world != null ? world.transform.rotation : Quaternion.identity,
                            Fabric = GameAccess.Member(panel, "lastPlacedOn"),
                            InWorld = GameAccess.Bool(panel, "IsWorld3D") });
                }
            }
            catch (Exception ex)
            {
                Restore(room, showFabric, saved, ex);
                Finish();
                return;
            }
            _notice = "Placing patterns...";
            Runner().StartCoroutine(Guarded(
                Solve(room, game, fabrics, patterns, showFabric, optimize),
                ex => Restore(room, showFabric, saved, ex),
                Finish));
        }

            // Runs a coroutine step by step, catching errors that a try/catch
            // around yield can't. Always calls onDone at the end.
            static IEnumerator Guarded(IEnumerator inner, Action<Exception> onError, Action onDone)
            {
                while (true)
                {
                    _lastBeat = Time.frameCount;
                    object current;
                    try
                    {
                        if (!inner.MoveNext())
                            break;
                        current = inner.Current;
                    }
                    catch (Exception ex)
                    {
                        onError(ex);
                        break;
                    }
                    yield return current;
                }
                onDone();
            }

            static void Restore(MonoBehaviour room, object showFabric, List<SavedPose> saved, Exception ex)
            {
                foreach (SavedPose pose in saved)
                {
                    // The player may have cut a piece while the search ran.
                    if (!IsUncutPattern(pose.Panel))
                        continue;
                    try
                    {
                        if (pose.InWorld)
                            EnsureWorld(pose.Panel);
                        else
                            ReturnToMenu(pose.Panel);
                        var restoredWorld = GameAccess.Member(pose.Panel, "WorldObj") as GameObject;
                        if (restoredWorld != null)
                        {
                            restoredWorld.transform.rotation = pose.Rotation;
                            restoredWorld.transform.position = pose.Position;
                        }
                        GameAccess.SetMember(pose.Panel, "lastPlacedOn", pose.Fabric);
                    }
                    catch (Exception restorePose)
                    {
                        Plugin.Log.LogWarning("Could not restore a pattern: " + restorePose.GetBaseException().Message);
                    }
                }
                _notice = "Pattern placement stopped: " + ex.GetBaseException().Message;
                Plugin.Log.LogWarning(Notice + "\n" + ex);
                try { ShowFabric(room, showFabric); }
                catch (Exception restore)
                {
                    Plugin.Log.LogWarning("Could not restore the displayed fabric: " + restore.GetBaseException().Message);
                }
            }

            static void Finish()
            {
                _nestClock = null;
                _solving = false;
                PlaceBoard.Refresh();
            }

        static IEnumerator Solve(MonoBehaviour room, object game, List<object> fabrics, List<object> patterns, object showFabric, bool optimize)
        {
            int placed = 0;
            int unplaced = 0;
            int needTrim = 0;
            int tooShort = 0;
            int tooWide = 0;
            int chosen = 0;
            int improvedFabrics = 0;
            int retainedFabrics = 0;
            int invalidLayouts = 0;
            var usedByFabric = new Dictionary<object, float>();

            foreach (object panel in patterns)
            {
                // A manual placement is the authoritative fabric for Optimize.
                object actual = GameAccess.Member(panel, "lastPlacedOn");
                if (optimize && GameAccess.Bool(panel, "IsWorld3D") && ContainsFabric(fabrics, actual))
                    SetPatternFabric(panel, actual);
                if (!Assign.TryGetValue(Id(panel), out object fabric) || fabric == null || !ContainsFabric(fabrics, fabric))
                    continue;
                chosen++;
                if (!optimize)
                    ReturnToMenu(panel);
            }

            if (chosen == 0)
            {
                _notice = "Choose the patterns to place on a fabric.";
                Plugin.Log.LogInfo(_notice);
                yield break;
            }

            foreach (object fabric in fabrics)
            {
                var mine = new List<object>();
                foreach (object panel in patterns)
                {
                    if (Assign.TryGetValue(Id(panel), out object assigned) && ReferenceEquals(assigned, fabric))
                        mine.Add(panel);
                }
                if (mine.Count == 0)
                    continue;
                
                _lengthCapped = false;
                ShowFabric(room, fabric);
                if (!Cloth(room, fabric, out float left, out float bottom, out float length, out float height))
                {
                    if (!optimize)
                        foreach (object panel in mine)
                            ReturnToMenu(panel);
                    unplaced += mine.Count;
                    continue;
                }
                
                bool capped = _lengthCapped;
                var pieces = new List<Piece>();
                var rejected = new List<object>();
                foreach (object panel in mine)
                {
                    if (TryMakePiece(panel, out Piece piece))
                        pieces.Add(piece);
                    else
                        rejected.Add(panel);
                }

                // Sort before capturing seed indices: Search must never reorder a seed.
                pieces.Sort((a, b) => (b.Orients[0].VisualW * b.Orients[0].VisualH).CompareTo(a.Orients[0].VisualW * a.Orients[0].VisualH));
                Grid grid = Blocked(room, fabric, patterns, left, bottom, length, height, out int cols, out int rows);
                List<Choice> seed = null;
                float baselineLength = 0f;
                bool validBaseline = true;
                if (optimize)
                {
                    bool unsupportedPlaced = false;
                    foreach (object panel in rejected)
                        if (GameAccess.Bool(panel, "IsWorld3D"))
                            unsupportedPlaced = true;
                    if (unsupportedPlaced)
                    {
                        _notice = "Current placements could not be validated on the packing grid; kept them unchanged.";
                        Plugin.Log.LogWarning(_notice);
                        retainedFabrics++;
                        invalidLayouts++;
                        foreach (object panel in mine)
                            if (!GameAccess.Bool(panel, "IsWorld3D"))
                                unplaced++;
                        continue;
                    }
                    validBaseline = CaptureSeed(pieces, fabric, grid, cols, rows, left, bottom, out seed, out baselineLength);
                    if (!validBaseline)
                        invalidLayouts++;
                }
                // Run the search on another thread so the game keeps drawing frames.
                List<Choice> layout = null;
                Task task = _searchTask = Task.Run(() =>
                {
                    layout = optimize
                        ? SearchFromSeed(pieces, grid, cols, rows, seed)
                        : Search(pieces, grid, cols, rows);
                });
                while (!task.IsCompleted)
                    yield return null;
                if (task.IsFaulted)
                    throw task.Exception.GetBaseException();
                
                Plugin.Log.LogDebug("Search finished; placing pieces.");
                if (room == null || !room.isActiveAndEnabled)
                {
                    _notice = "Placement cancelled because you left the cutting room.";
                    Plugin.Log.LogInfo("Left the cutting room; placement cancelled.");
                    yield break;
                }

                // The player kept playing during the search. Make sure nothing changed under us.
                foreach (Piece p in pieces)
                {
                    if (!IsUncutPattern(p.Panel))
                    {
                        _notice = "Patterns changed while placing. Press Solve again.";
                        Plugin.Log.LogInfo(_notice);
                        yield break;
                    }
                }
                // Re-read the cloth in case other pieces were moved, then validate against it.
                grid = Blocked(room, fabric, patterns, left, bottom, length, height, out cols, out rows);
                layout = ValidateLayout(pieces, grid, cols, rows, layout);
                
                if (optimize && validBaseline && !ImprovesBaseline(layout.Count, LayoutLength(pieces, layout), seed.Count, baselineLength))
                {
                    // Preserve exact manual transforms, including sub-cell offsets.
                    usedByFabric[fabric] = baselineLength;
                    unplaced += pieces.Count - seed.Count + rejected.Count;
                    retainedFabrics++;
                    continue;
                }
                if (optimize)
                    improvedFabrics++;
                var kept = new HashSet<int>();
                var placedPanels = new List<object>();
                float used = 0f;
                for (int i = 0; i < layout.Count; i++)
                {
                    Choice choice = layout[i];
                    Piece piece = pieces[choice.Piece];
                    Stamp stamp = piece.Orients[choice.Orient];
                    Put(piece, stamp, left + (choice.Col + stamp.PadX) * Cell, bottom + (choice.Row + stamp.PadY) * Cell, -0.002f - i * 0.0004f);
                    GameAccess.SetMember(piece.Panel, "lastPlacedOn", fabric);
                    AccessTools.Method(piece.Panel.GetType(), "SetGrainQuality")?.Invoke(piece.Panel, null);
                    if (!GameAccess.Bool(piece.Panel, "IsWorld3D")
                        || !(GameAccess.Member(piece.Panel, "WorldObj") is GameObject visibleWorld)
                        || !visibleWorld.activeInHierarchy)
                        throw new InvalidOperationException("A placed pattern became hidden; restoring the previous layout.");
                    kept.Add(choice.Piece);
                    placedPanels.Add(piece.Panel);
                    used = Mathf.Max(used, Edge(stamp, choice.Col));
                    placed++;
                }

                for (int i = 0; i < pieces.Count; i++)
                {
                    if (kept.Contains(i))
                        continue;
                    ReturnToMenu(pieces[i].Panel);
                    unplaced++;
                    if (TooWide(pieces[i], rows))
                        tooWide++;
                    else if (capped)
                        needTrim++;
                    else
                        tooShort++;
                }
                foreach (object panel in rejected)
                {
                    ReturnToMenu(panel);
                    unplaced++;
                }
                // Polish: pull pieces together as far as the game's own clearance check allows.
                // Left-most first, so each piece slides toward neighbours that have already settled.
                var snapClock = Stopwatch.StartNew();
                placedPanels.Sort((a, b) =>
                {
                    var wa = GameAccess.Member(a, "WorldObj") as GameObject;
                    var wb = GameAccess.Member(b, "WorldObj") as GameObject;
                    float xa = wa != null ? wa.transform.position.x : 0f;
                    float xb = wb != null ? wb.transform.position.x : 0f;
                    return xa.CompareTo(xb);
                });
                for (int pass = 0; pass < 2; pass++)
                    foreach (object panel in placedPanels)
                        Snap(panel);
                Plugin.Log.LogDebug("Snap " + snapClock.ElapsedMilliseconds + " ms");
                usedByFabric[fabric] = used;
            }

            ShowFabric(room, showFabric);
            if (showFabric != null && usedByFabric.TryGetValue(showFabric, out float showUsed))
                Unroll(room, showFabric, showUsed);

            if (optimize)
            {
                _notice = improvedFabrics > 0
                    ? "Optimized " + improvedFabrics + " fabric layouts."
                    : "Kept the current layout; no validated improvement was found.";
                if (retainedFabrics > 0)
                    _notice += " " + retainedFabrics + " layouts were retained.";
                if (unplaced > 0)
                    _notice += " " + unplaced + " patterns remain unplaced.";
                if (invalidLayouts > 0)
                    _notice += " Some pieces sit closer than the planner's grid, so their exact positions were re-planned.";
                if (_searchLimited)
                    _notice += " The search reached its time limit.";
            }
            else if (unplaced > 0)
            {
                var parts = new List<string>();
                if (needTrim > 0)
                    parts.Add(needTrim + " need more open fabric: cut the placed pieces, then trim the fabric");
                if (tooShort > 0)
                    parts.Add(tooShort + " don't fit: this fabric is too short");
                if (tooWide > 0)
                    parts.Add(tooWide + (tooWide == 1 ? " is" : " are") + " too wide for this fabric");
                int other = unplaced - needTrim - tooShort - tooWide;
                if (other > 0)
                    parts.Add(other + " couldn't be placed");
                _notice = "Placed " + placed + ". " + string.Join(". ", parts) + ".";
                if (_searchLimited)
                    _notice += " (Layout could be tighter: the search ran out of time.)";
            }
            else if (placed > 0)
                _notice = "Placed " + placed + " patterns.";
            else
                _notice = "Choose the patterns to place on a fabric.";

            Plugin.Log.LogInfo(_notice);
        }

        static List<Choice> Search(List<Piece> pieces, Grid blocked, int cols, int rows)
        {
            Best.Clear();
            _bestCount = 0;
            _bestLength = float.MaxValue;
            _bestTop = float.MaxValue;
            _bestSpread = float.MaxValue;
            if (pieces.Count == 0 || cols <= 0 || rows <= 0)
                return new List<Choice>();
            
            // Bigger dresses get more time, up to 10 seconds. The search runs in the background,
            // so the game stays responsive while it works.
            NestBudgetMs = Mathf.Clamp(4000 + 60 * (pieces.Count - 20), 4000, 10000);
            
            // Budget the entire search, including greedy orders and backtracking.
            _nestClock = Stopwatch.StartNew();

            // Every order starts from the same occupancy grid.
            List<Vector2Int> initialAnchors = Anchors(blocked, cols, rows);
            bool first = true;
            foreach (int[] order in Orders(pieces))
            {
                if (NestTimedOut())
                    break;
                _mustFinish = first;
                List<Choice> layout = Greedy(pieces, blocked, cols, rows, order, initialAnchors);
                _mustFinish = false;
                Plugin.Log.LogDebug("Greedy: placed " + layout.Count + "/" + pieces.Count + " at " + _nestClock.ElapsedMilliseconds + " ms");
                Consider(pieces, layout, layout.Count, LayoutLength(pieces, layout));
                // Try filling the first big-first layout before spending the budget on more orders.
                if (first && Best.Count < pieces.Count)
                {
                    List<Choice> filledOrder = FillHoles(pieces, blocked, cols, rows, layout);
                    Consider(pieces, filledOrder, filledOrder.Count, LayoutLength(pieces, filledOrder));
                }
                first = false; 
            }

            if (pieces.Count <= 3 && !NestTimedOut())
            {
                _nodes = 0;
                var used = new bool[pieces.Count];
                var undo = new List<int>();
                var current = new List<Choice>();
                Grid grid = blocked.Clone();
                Dive(pieces, grid, cols, rows, used, undo, current, 0f, 0);
            }

            // Anchors only touch bottom-left corners, so curves stay apart.
            // Settle drops each piece into the notch that shortens the bolt.
            Plugin.Log.LogDebug("Orders done at " + _nestClock.ElapsedMilliseconds + " ms");
            List<Choice> improved = Improve(pieces, blocked, cols, rows, Best);
            Plugin.Log.LogDebug("Improve done at " + _nestClock.ElapsedMilliseconds + " ms");
            _paintClock.Reset();
            _poseClock.Reset();
            List<Choice> settled = Settle(pieces, blocked, cols, rows, improved);
            List<Choice> filled = FillHoles(pieces, blocked, cols, rows, settled);
            List<Choice> packed = Settle(pieces, blocked, cols, rows, filled);
            Plugin.Log.LogDebug("Improve/Settle/Fill done at " + _nestClock.ElapsedMilliseconds + " ms (repaint " + _paintClock.ElapsedMilliseconds + " ms, pose search " + _poseClock.ElapsedMilliseconds + " ms)");
            _nestClock.Stop();
            Plugin.Log.LogDebug("Pattern nest " + _nestClock.ElapsedMilliseconds + " ms");
            _nestClock = null;
            Consider(pieces, packed, packed.Count, LayoutLength(pieces, packed));
            return new List<Choice>(Best);
        }

        static bool ImprovesBaseline(int count, float length, int baselineCount, float baselineLength)
        {
            return count > baselineCount || (count == baselineCount && length < baselineLength - Cell);
        }

        static List<Choice> SearchFromSeed(List<Piece> pieces, Grid blocked, int cols, int rows, List<Choice> seed)
        {
            Best.Clear();
            _bestCount = 0;
            _bestLength = _bestTop = _bestSpread = float.MaxValue;
            Consider(pieces, seed, seed.Count, LayoutLength(pieces, seed));
            _nestClock = Stopwatch.StartNew();
            try
            {
                // Add missing pieces first. Then improve that arrangement, not an empty board.
                List<Choice> filled = FillHoles(pieces, blocked, cols, rows, seed);
                Consider(pieces, filled, filled.Count, LayoutLength(pieces, filled));
                List<Choice> improved = Improve(pieces, blocked, cols, rows, filled);
                List<Choice> settled = Settle(pieces, blocked, cols, rows, improved);
                List<Choice> refilled = FillHoles(pieces, blocked, cols, rows, settled);
                Consider(pieces, refilled, refilled.Count, LayoutLength(pieces, refilled));
                return new List<Choice>(Best);
            }
            finally
            {
                _nestClock.Stop();
                _nestClock = null;
            }
        }

        static bool CaptureSeed(List<Piece> pieces, object fabric, Grid blocked, int cols, int rows,
            float left, float bottom, out List<Choice> seed, out float actualLength)
        {
            seed = new List<Choice>();
            actualLength = 0f;
            Grid grid = blocked.Clone();
            bool valid = true;
            var undo = new List<int>();
            for (int i = 0; i < pieces.Count; i++)
            {
                Piece piece = pieces[i];
                object placedOn = GameAccess.Member(piece.Panel, "lastPlacedOn");
                if (!GameAccess.Bool(piece.Panel, "IsWorld3D")
                    || (placedOn != null && !ReferenceEquals(placedOn, fabric)))
                    continue;
                Quaternion rotation = piece.World.transform.rotation;
                Stamp current = BuildStamp(piece.Panel, rotation);
                if (current == null)
                {
                    valid = false;
                    continue;
                }
                Extents(piece.Panel, piece.World.transform, out float minX, out float minY, out float maxX, out float maxY);
                int col = Mathf.RoundToInt((minX - left) / Cell);
                int row = Mathf.RoundToInt((minY - bottom) / Cell);
                if (minX < left || minY < bottom || maxX > left + cols * Cell || maxY > bottom + rows * Cell
                    || !Fits(grid, current, col, row, cols, rows))
                {
                    // Keep the valid placements as the starting point; re-pack invalid ones.
                    valid = false;
                    continue;
                }
                int orient = piece.Orients.Length;
                Array.Resize(ref piece.Orients, orient + 1);
                piece.Orients[orient] = current;
                Paint(grid, current, col, row, cols, i + 2, undo);
                seed.Add(new Choice { Piece = i, Orient = orient, Col = col, Row = row });
                actualLength = Mathf.Max(actualLength, maxX - left);
            }
            return valid;
        }

        // A coarse stride can jump over a narrow notch. This fallback visits every
        // raster anchor, bounded by the same search deadline and current best edge.
        static NestHit DenseSpot(Piece piece, Grid grid, int cols, int rows, float length, int top)
        {
            var best = new NestHit();
            for (int orient = 0; orient < piece.Orients.Length; orient++)
            {
                Stamp stamp = piece.Orients[orient];
                for (int col = 0; col + stamp.MaxX < cols; col++)
                {
                    if (NestTimedOut())
                        return best;
                    if (best.Found && Mathf.Max(length, Edge(stamp, col)) > best.Length + 0.0001f)
                        break;
                    for (int row = 0; row + stamp.MaxY < rows; row++)
                    {
                        if ((row & 63) == 0 && NestTimedOut())
                            return best;
                        if (!Probe(grid, stamp, col, row, cols, rows))
                            continue;
                        NestHit hit = ScoreOf(stamp, orient, col, row, length, top);
                        if (Better(hit.Length, hit.Top, hit.Pull, best))
                            best = hit;
                        break;
                    }
                }
            }
            return best;
        }

        static float LayoutLength(List<Piece> pieces, List<Choice> layout)
        {
            float length = 0f;
            foreach (Choice choice in layout)
            {
                Stamp stamp = pieces[choice.Piece].Orients[choice.Orient];
                length = Mathf.Max(length, Edge(stamp, choice.Col));
            }
            return length;
        }

        static float Edge(Stamp stamp, int col)
        {
            return (col + stamp.MaxX + 1) * Cell;
        }

        static void Dive(List<Piece> pieces, Grid grid, int cols, int rows, bool[] used, List<int> undo, List<Choice> current, float length, int placed)
        {
            if (_nodes > NodeBudget || NestTimedOut())
                return;
            Consider(pieces, current, placed, length);
            if (placed >= pieces.Count)
                return;
            int unused = 0;
            for (int i = 0; i < used.Length; i++)
            {
                if (!used[i])
                    unused++;
            }
            if (placed + unused < _bestCount)
                return;
            if (placed == _bestCount && length >= _bestLength - 0.0001f && _bestCount == pieces.Count)
                return;

            for (int piece = 0; piece < pieces.Count; piece++)
            {
                if (used[piece])
                    continue;
                List<Vector2Int> anchors = Anchors(grid, cols, rows, 8);
                for (int orient = 0; orient < pieces[piece].Orients.Length; orient++)
                {
                    Stamp stamp = pieces[piece].Orients[orient];
                    foreach (Vector2Int anchor in anchors)
                    {
                        if (!Fits(grid, stamp, anchor.x, anchor.y, cols, rows))
                            continue;
                        float next = Mathf.Max(length, Edge(stamp, anchor.x));
                        if (_bestCount == pieces.Count && next >= _bestLength - 0.0001f)
                            continue;
                        int mark = undo.Count;
                        Paint(grid, stamp, anchor.x, anchor.y, cols, piece + 2, undo);
                        used[piece] = true;
                        current.Add(new Choice { Piece = piece, Orient = orient, Col = anchor.x, Row = anchor.y });
                        _nodes++;
                        Dive(pieces, grid, cols, rows, used, undo, current, next, placed + 1);
                        current.RemoveAt(current.Count - 1);
                        used[piece] = false;
                        Unpaint(grid, undo, mark);
                        if (_nodes > NodeBudget)
                            return;
                    }
                    if (_nodes > NodeBudget)
                        return;
                }
            }
        }

        static List<Choice> Greedy(List<Piece> pieces, Grid blocked, int cols, int rows, int[] order, List<Vector2Int> initialAnchors)
        {
            Grid grid = blocked.Clone();
            var layout = new List<Choice>();
            float length = 0f;
            var undo = new List<int>();
            foreach (int piece in order)
            {
                if (NestTimedOut())
                    break;
                if (!BestSpot(pieces[piece], grid, cols, rows, length, false,
                    layout.Count == 0 ? initialAnchors : null, out int orient, out int col, out int row, out float next))
                    continue;
                Stamp stamp = pieces[piece].Orients[orient];
                Paint(grid, stamp, col, row, cols, piece + 2, undo);
                layout.Add(new Choice { Piece = piece, Orient = orient, Col = col, Row = row });
                length = next;
            }
            return layout;
        }

        static bool BestSpot(Piece piece, Grid grid, int cols, int rows, float length, bool everyCell, List<Vector2Int> initialAnchors, out int orient, out int col, out int row, out float next)
        {
            orient = 0;
            col = 0;
            row = 0;
            next = length;
            bool found = false;
            float bestLength = float.MaxValue;
            int bestRow = int.MaxValue;
            int bestCol = int.MaxValue;
            List<Vector2Int> anchors = initialAnchors ?? Anchors(grid, cols, rows);
            found = ScoreAnchors(piece, grid, cols, rows, length, anchors, ref orient, ref col, ref row, ref next, ref bestLength, ref bestRow, ref bestCol, true);
            if (!found && everyCell)
                found = ScoreAnchors(piece, grid, cols, rows, length, FreeCells(grid, cols, rows), ref orient, ref col, ref row, ref next, ref bestLength, ref bestRow, ref bestCol, false);
            return found;
        }

        static bool ScoreAnchors(Piece piece, Grid grid, int cols, int rows, float length, List<Vector2Int> anchors, ref int orient, ref int col, ref int row, ref float next, ref float bestLength, ref int bestRow, ref int bestCol, bool sortedByColumn)
        {
            bool found = false;
            for (int o = 0; o < piece.Orients.Length; o++)
            {
                Stamp stamp = piece.Orients[o];
                if (stamp.MaxX >= cols || stamp.MaxY >= rows)
                    continue;
                foreach (Vector2Int anchor in anchors)
                {
                    if (NestTimedOut())
                        return found;
                    float edge = Mathf.Max(length, Edge(stamp, anchor.x));
                    // Anchors are sorted by column; later ones cannot improve this edge.
                    if (sortedByColumn && edge > bestLength + 0.0001f)
                        break;
                    if (!Probe(grid, stamp, anchor.x, anchor.y, cols, rows))
                        continue;
                    if (edge < bestLength - 0.0001f || (Mathf.Abs(edge - bestLength) <= 0.0001f && (anchor.y < bestRow || (anchor.y == bestRow && anchor.x < bestCol))))
                    {
                        bestLength = edge;
                        bestRow = anchor.y;
                        bestCol = anchor.x;
                        orient = o;
                        col = anchor.x;
                        row = anchor.y;
                        next = edge;
                        found = true;
                    }
                }
            }
            return found;
        }

        static List<Choice> Improve(List<Piece> pieces, Grid blocked, int cols, int rows, List<Choice> layout)
        {
            var current = new List<Choice>(layout);
            for (int pass = 0; pass < 6; pass++)
            {
                if (NestTimedOut())
                    break;
                bool moved = false;
                for (int i = 0; i < current.Count; i++)
                {
                    if (NestTimedOut())
                        break;
                    Grid grid = blocked.Clone();
                    var undo = new List<int>();
                    float length = 0f;
                    for (int k = 0; k < current.Count; k++)
                    {
                        if (k == i)
                            continue;
                        Choice other = current[k];
                        Stamp stamp = pieces[other.Piece].Orients[other.Orient];
                        Paint(grid, stamp, other.Col, other.Row, cols, other.Piece + 2, undo);
                        length = Mathf.Max(length, Edge(stamp, other.Col));
                    }

                    Choice held = current[i];
                    if (!BestSpot(pieces[held.Piece], grid, cols, rows, 0f, false, null, out int orient, out int col, out int row, out float edge))
                        continue;
                    float before = Edge(pieces[held.Piece].Orients[held.Orient], held.Col);
                    float after = edge;
                    bool better = after < before - 0.0001f || (Mathf.Abs(after - before) <= 0.0001f && (row < held.Row || (row == held.Row && col < held.Col)));
                    if (!better || (orient == held.Orient && col == held.Col && row == held.Row))
                        continue;
                    current[i] = new Choice { Piece = held.Piece, Orient = orient, Col = col, Row = row };
                    moved = true;
                }
                if (!moved)
                    break;
            }
            return current;
        }

        struct NestHit
        {
            public bool Found;
            public int Orient;
            public int Col;
            public int Row;
            public float Length;
            public int Top;
            public int Pull;
        }

        static List<Choice> FillHoles(List<Piece> pieces, Grid blocked, int cols, int rows, List<Choice> layout)
        {
            Grid grid = blocked.Clone();
            var undo = new List<int>();
            var used = new bool[pieces.Count];
            float length = 0f;
            int top = 0;
            foreach (Choice choice in layout)
            {
                used[choice.Piece] = true;
                Stamp stamp = pieces[choice.Piece].Orients[choice.Orient];
                Paint(grid, stamp, choice.Col, choice.Row, cols, choice.Piece + 2, undo);
                length = Mathf.Max(length, Edge(stamp, choice.Col));
                top = Mathf.Max(top, choice.Row + stamp.MaxY);
            }

            var filled = new List<Choice>(layout);
            for (int i = 0; i < pieces.Count; i++)
            {
                if (NestTimedOut())
                    break;
                if (used[i])
                    continue;
                int orient, col, row;
                if (!BestSpot(pieces[i], grid, cols, rows, length, false, null,
                    out orient, out col, out row, out _))
                {
                    NestHit hit = DenseSpot(pieces[i], grid, cols, rows, length, top);
                    if (!hit.Found)
                        continue;
                    orient = hit.Orient;
                    col = hit.Col;
                    row = hit.Row;
                }
                Stamp stamp = pieces[i].Orients[orient];
                Paint(grid, stamp, col, row, cols, i + 2, undo);
                filled.Add(new Choice { Piece = i, Orient = orient, Col = col, Row = row });
                length = Mathf.Max(length, Edge(stamp, col));
                top = Mathf.Max(top, row + stamp.MaxY);
            }
            return filled;
        }

        // Lift each placed piece and sit it on the curve that keeps the most cloth on the bolt.
        static List<Choice> Settle(List<Piece> pieces, Grid blocked, int cols, int rows, List<Choice> layout)
        {
            if (layout.Count == 0)
                return layout;
            var current = new List<Choice>(layout);
            Grid grid = blocked.Clone();
            var undo = new List<int>();
            for (int pass = 0; pass < 4; pass++)
            {
                if (NestTimedOut())
                    break;
                int[] order = NestOrder(pieces, current, pass % 2 == 0);
                bool moved = false;
                for (int n = 0; n < order.Length; n++)
                {
                    if (NestTimedOut())
                        break;
                    if (NestOne(pieces, blocked, cols, rows, current, order[n], grid, undo))
                        moved = true;
                }
                if (!moved)
                    break;
            }
            return current;
        }

        static bool NestOne(List<Piece> pieces, Grid blocked, int cols, int rows, List<Choice> current, int index, Grid grid, List<int> undo)
        {
            Choice held = current[index];
            _paintClock.Start();
            blocked.CopyTo(grid);
            undo.Clear();
            float baseLength = 0f;
            int baseTop = 0;
            for (int k = 0; k < current.Count; k++)
            {
                if (k == index)
                    continue;
                Choice other = current[k];
                Stamp stamp = pieces[other.Piece].Orients[other.Orient];
                Paint(grid, stamp, other.Col, other.Row, cols, other.Piece + 2, undo);
                baseLength = Mathf.Max(baseLength, Edge(stamp, other.Col));
                baseTop = Mathf.Max(baseTop, other.Row + stamp.MaxY);
            }
            _paintClock.Stop();
            _poseClock.Start();
            NestHit best = SearchPose(pieces[held.Piece], grid, cols, rows, baseLength, baseTop, true, held.Orient, held.Col, held.Row);
            _poseClock.Stop();
            if (!best.Found || (best.Orient == held.Orient && best.Col == held.Col && best.Row == held.Row))
                return false;
            current[index] = new Choice { Piece = held.Piece, Orient = best.Orient, Col = best.Col, Row = best.Row };
            return true;
        }

        static int[] NestOrder(List<Piece> pieces, List<Choice> layout, bool smallFirst)
        {
            var order = new int[layout.Count];
            for (int i = 0; i < order.Length; i++)
                order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                float aa = pieces[layout[a].Piece].Orients[0].VisualW * pieces[layout[a].Piece].Orients[0].VisualH;
                float bb = pieces[layout[b].Piece].Orients[0].VisualW * pieces[layout[b].Piece].Orients[0].VisualH;
                return smallFirst ? aa.CompareTo(bb) : bb.CompareTo(aa);
            });
            return order;
        }

        static bool NestTimedOut()
        {
            if (_mustFinish)
                return false;
            bool expired = _nestClock != null && _nestClock.ElapsedMilliseconds >= NestBudgetMs;
            if (expired)
                _searchLimited = true;
            return expired;
        }

        static NestHit SearchPose(Piece piece, Grid grid, int cols, int rows, float baseLength, int baseTop, bool hasSeed, int seedOrient, int seedCol, int seedRow)
        {
            var best = new NestHit();
            if (hasSeed)
            {
                Stamp seed = piece.Orients[seedOrient];
                if (Probe(grid, seed, seedCol, seedRow, cols, rows))
                {
                    int col = seedCol;
                    int row = seedRow;
                    Nudge(grid, seed, cols, rows, ref col, ref row, int.MaxValue, 8);
                    best = ScoreOf(seed, seedOrient, col, row, baseLength, baseTop);
                }
            }

            for (int orient = 0; orient < piece.Orients.Length; orient++)
            {
                if (NestTimedOut())
                    return FinishPose(grid, cols, rows, piece, best, baseLength, baseTop);
                Stamp stamp = piece.Orients[orient];
                int stride = StrideFor(stamp);
                int roomCol = cols - stamp.MaxX - 1;
                int roomRow = rows - stamp.MaxY - 1;
                if (roomCol < 0 || roomRow < 0)
                    continue;
                for (int x = 0; x <= roomCol; x += stride)
                {
                    if (NestTimedOut())
                        return FinishPose(grid, cols, rows, piece, best, baseLength, baseTop);
                    int reach = x - stride * 3;
                    if (reach < 0)
                        reach = 0;
                    if (best.Found && Mathf.Max(baseLength, Edge(stamp, reach)) > best.Length + 0.0001f)
                        break;
                    for (int y = 0; y <= roomRow; y += stride)
                    {
                        bool locked = best.Found && baseLength >= best.Length - 0.0001f;
                        int reachY = y - stride * 3;
                        if (reachY < 0)
                            reachY = 0;
                        if (locked && reachY + stamp.MaxY > best.Top)
                            break;
                        // When length and top are already as low as possible, only pull (col + row) can win.
                        bool onlyPull = best.Found && best.Length <= baseLength + 0.0001f && best.Top <= baseTop;
                        if (onlyPull && reach + reachY >= best.Pull)
                            break;
                        if (!Probe(grid, stamp, x, y, cols, rows))
                            continue;
                        bool canDown = y > 0 && Clear(grid, stamp.Down, x, y - 1, cols, rows);
                        if (canDown && y >= stride && Probe(grid, stamp, x, y - stride, cols, rows))
                            continue;
                        int col = x;
                        int row = y;
                        Nudge(grid, stamp, cols, rows, ref col, ref row, stride, 3);
                        NestHit cand = ScoreOf(stamp, orient, col, row, baseLength, baseTop);
                        if (Better(cand.Length, cand.Top, cand.Pull, best))
                            best = cand;
                    }
                }
            }
            return FinishPose(grid, cols, rows, piece, best, baseLength, baseTop);
        }

        static NestHit FinishPose(Grid grid, int cols, int rows, Piece piece, NestHit best, float baseLength, int baseTop)
        {
            if (!best.Found)
                return best;
            Stamp stamp = piece.Orients[best.Orient];
            int col = best.Col;
            int row = best.Row;
            Nudge(grid, stamp, cols, rows, ref col, ref row, int.MaxValue, 8);
            return ScoreOf(stamp, best.Orient, col, row, baseLength, baseTop);
        }

        static NestHit ScoreOf(Stamp stamp, int orient, int col, int row, float baseLength, int baseTop)
        {
            return new NestHit
            {
                Found = true,
                Orient = orient,
                Col = col,
                Row = row,
                Length = Mathf.Max(baseLength, Edge(stamp, col)),
                Top = Mathf.Max(baseTop, row + stamp.MaxY),
                Pull = col + row
            };
        }

        static bool Better(float length, int top, int pull, NestHit best)
        {
            if (!best.Found)
                return true;
            if (length < best.Length - 0.0001f)
                return true;
            if (length > best.Length + 0.0001f)
                return false;
            if (top < best.Top)
                return true;
            if (top > best.Top)
                return false;
            return pull < best.Pull;
        }

        static int StrideFor(Stamp stamp)
        {
            int count = stamp.Cells.Length;
            if (count < 600)
                return 2;
            if (count < 5000)
                return 3;
            return 5;
        }

        static void Nudge(Grid grid, Stamp stamp, int cols, int rows, ref int col, ref int row, int steps, int rounds)
        {
            if (stamp.Left == null || stamp.Down == null)
                return;
            for (int round = 0; round < rounds; round++)
            {
                if (NestTimedOut())
                    return;
                bool moved = false;
                int left = steps;
                while (left > 0 && col > 0 && Clear(grid, stamp.Left, col - 1, row, cols, rows))
                {
                    if (NestTimedOut())
                        return;
                    col--;
                    left--;
                    moved = true;
                }
                int down = steps;
                while (down > 0 && row > 0 && Clear(grid, stamp.Down, col, row - 1, cols, rows))
                {
                    if (NestTimedOut())
                        return;
                    row--;
                    down--;
                    moved = true;
                }
                if (!moved)
                    break;
            }
        }

        static bool Clear(Grid grid, Vector2Int[] frontier, int col, int row, int cols, int rows)
        {
            if (frontier == null || frontier.Length == 0)
                return false;
            for (int i = 0; i < frontier.Length; i++)
            {
                int x = col + frontier[i].x;
                int y = row + frontier[i].y;
                if ((uint)x >= (uint)cols || (uint)y >= (uint)rows)
                    return false;
                if (grid[y * cols + x] != 0)
                    return false;
            }
            return true;
        }

        static bool Probe(Grid grid, Stamp stamp, int col, int row, int cols, int rows)
        {
            if (col < 0 || row < 0 || col + stamp.MaxX >= cols || row + stamp.MaxY >= rows)
                return false;
            Vector2Int[] cells = stamp.Cells;
            int step = cells.Length > 32 ? cells.Length / 16 : 1;
            for (int i = 0; i < cells.Length; i += step)
            {
                int x = col + cells[i].x;
                int y = row + cells[i].y;
                if (grid[y * cols + x] != 0)
                    return false;
            }
            if (step == 1)
                return true;
            return Fits(grid, stamp, col, row, cols, rows);
        }

        static void Consider(List<Piece> pieces, List<Choice> layout, int count, float length)
        {
            float top = 0f;
            float spread = 0f;
            for (int i = 0; i < layout.Count; i++)
            {
                Choice choice = layout[i];
                Stamp stamp = pieces[choice.Piece].Orients[choice.Orient];
                top = Mathf.Max(top, (choice.Row + stamp.MaxY + 1) * Cell);
                spread += choice.Col + choice.Row;
            }
            bool shorter = count == _bestCount && length < _bestLength - 0.0001f;
            bool flatter = count == _bestCount && Mathf.Abs(length - _bestLength) <= 0.0001f && top < _bestTop - 0.0001f;
            bool tighter = count == _bestCount && Mathf.Abs(length - _bestLength) <= 0.0001f && Mathf.Abs(top - _bestTop) <= 0.0001f && spread < _bestSpread;
            if (count > _bestCount || shorter || flatter || tighter)
            {
                _bestCount = count;
                _bestLength = length;
                _bestTop = top;
                _bestSpread = spread;
                Best.Clear();
                Best.AddRange(layout);
            }
        }

        static List<int[]> Orders(List<Piece> pieces)
        {
            int count = pieces.Count;
            var orders = new List<int[]>();
            var seen = new HashSet<string>();
            AddOrder(orders, seen, Range(count));

            int[] smallFirst = Range(count);
            Array.Reverse(smallFirst);
            AddOrder(orders, seen, smallFirst);

            int[] widest = Range(count);
            Array.Sort(widest, (a, b) => pieces[b].Orients[0].VisualW.CompareTo(pieces[a].Orients[0].VisualW));
            AddOrder(orders, seen, widest);

            int[] tallest = Range(count);
            Array.Sort(tallest, (a, b) => pieces[b].Orients[0].VisualH.CompareTo(pieces[a].Orients[0].VisualH));
            AddOrder(orders, seen, tallest);

            if (count <= 4)
                Permute(Range(count), 0, orders, seen);
            else
            {
                var random = new System.Random(17);
                int maxOrders = count > 40 ? 6 : count > 12 ? 12 : 80;
                int guard = 0;
                while (orders.Count < maxOrders && guard < 400)
                {
                    guard++;
                    int[] order = Range(count);
                    for (int i = count - 1; i > 0; i--)
                    {
                        int swap = random.Next(i + 1);
                        int tmp = order[i];
                        order[i] = order[swap];
                        order[swap] = tmp;
                    }
                    AddOrder(orders, seen, order);
                }
            }
            return orders;
        }

        static void Permute(int[] order, int start, List<int[]> orders, HashSet<string> seen)
        {
            if (start >= order.Length)
            {
                AddOrder(orders, seen, (int[])order.Clone());
                return;
            }
            for (int i = start; i < order.Length; i++)
            {
                int tmp = order[start];
                order[start] = order[i];
                order[i] = tmp;
                Permute(order, start + 1, orders, seen);
                tmp = order[start];
                order[start] = order[i];
                order[i] = tmp;
            }
        }

        static List<Vector2Int> FreeCells(Grid grid, int cols, int rows)
        {
            var cells = new List<Vector2Int>();
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    if (grid[y * cols + x] != 0)
                        continue;
                    cells.Add(new Vector2Int(x, y));
                    if (cells.Count >= 500)
                        return cells;
                }
            }
            return cells;
        }

        static void AddOrder(List<int[]> orders, HashSet<string> seen, int[] order)
        {
            if (seen.Add(Key(order)))
                orders.Add(order);
        }

        static int[] Range(int count)
        {
            var order = new int[count];
            for (int i = 0; i < count; i++)
                order[i] = i;
            return order;
        }

        static string Key(int[] order)
        {
            return string.Join(",", order);
        }

        static List<Vector2Int> Anchors(Grid grid, int cols, int rows, int limit = 0)
        {
            var anchors = new List<Vector2Int>();
            // Column order is also scoring order. Each cell is visited once,
            // so no deduplication set or sorting pass is needed.
            // Past the last occupied column plus one, no cell can have a
            // blocked left neighbor. A fresh bolt needs just column zero.
            int last = Mathf.Min(cols - 1, grid.LastOccupiedCol + 1);
            for (int x = 0; x <= last; x++)
            {
                if (NestTimedOut())
                    return anchors;
                for (int y = 0; y < rows; y++)
                {
                    int index = y * cols + x;
                    if (grid[index] != 0)
                        continue;
                    bool leftBlocked = x == 0 || grid[index - 1] != 0;
                    bool belowBlocked = y == 0 || grid[index - cols] != 0;
                    if (!leftBlocked || !belowBlocked)
                        continue;
                    anchors.Add(new Vector2Int(x, y));
                    if (limit > 0 && anchors.Count >= limit)
                        return anchors;
                }
            }
            // Always offer the bottom of the first fully empty column,
            // so a piece can go onto fresh cloth past everything placed.
            int open = grid.LastOccupiedCol + 1;
            if (open < cols && grid[open] == 0)
            {
                var spot = new Vector2Int(open, 0);
                if (!anchors.Contains(spot))
                    anchors.Add(spot);
            }
            return anchors;
        }

        static bool Fits(Grid grid, Stamp stamp, int col, int row, int cols, int rows)
        {
            if (col < 0 || row < 0 || col + stamp.MaxX >= cols || row + stamp.MaxY >= rows)
                return false;
            foreach (Vector2Int cell in stamp.Cells)
            {
                int x = col + cell.x;
                int y = row + cell.y;
                if (grid[y * cols + x] != 0)
                    return false;
            }
            return stamp.Cells.Length > 0;
        }

        static void Paint(Grid grid, Stamp stamp, int col, int row, int cols, int id, List<int> undo)
        {
            int rows = grid.Length / cols;
            foreach (Vector2Int cell in stamp.Cells)
            {
                int x = col + cell.x;
                int y = row + cell.y;
                if (x < 0 || y < 0 || x >= cols || y >= rows)
                    continue;
                int index = y * cols + x;
                undo.Add(index);
                grid[index] = id;
            }
            Expand(grid, cols, stamp, col, row, undo);
        }

        static void Expand(Grid grid, int cols, Stamp stamp, int col, int row, List<int> undo)
        {
            int rows = grid.Length / cols;
            foreach (Vector2Int cell in stamp.Halo)
            {
                int x = col + cell.x;
                int y = row + cell.y;
                if (x < 0 || y < 0 || x >= cols || y >= rows)
                    continue;
                int index = y * cols + x;
                if (grid[index] != 0)
                    continue;
                grid[index] = 1;
                undo.Add(index);
            }
        }

        static void Unpaint(Grid grid, List<int> undo, int mark)
        {
            for (int i = undo.Count - 1; i >= mark; i--)
                grid[undo[i]] = 0;
            undo.RemoveRange(mark, undo.Count - mark);
        }

        static Grid Blocked(MonoBehaviour room, object fabric, List<object> patterns, float left, float bottom, float length, float height, out int cols, out int rows)
        {
            if (float.IsNaN(length) || float.IsInfinity(length) || float.IsNaN(height)
                || float.IsInfinity(height) || length <= 0f || height <= 0f
                || length / Cell > MaxGridCells || height / Cell > MaxGridCells)
                throw new InvalidOperationException("Invalid fabric dimensions.");
            cols = Mathf.Max(1, Mathf.FloorToInt(length / Cell));
            rows = Mathf.Max(1, Mathf.FloorToInt(height / Cell));
            if ((long)cols * rows > MaxGridCells)
                throw new InvalidOperationException("The fabric exceeds the supported placement grid size.");
            var grid = new Grid(cols * rows, cols);
            foreach (object panel in patterns)
            {
                if (IsUncutPattern(panel) && Assign.TryGetValue(Id(panel), out object assigned) && ReferenceEquals(assigned, fabric))
                    continue;
                if (!GameAccess.Bool(panel, "IsWorld3D"))
                    continue;
                object placedOn = GameAccess.Member(panel, "lastPlacedOn");
                if (placedOn != null && !ReferenceEquals(placedOn, fabric))
                    continue;
                PaintPlaced(grid, cols, rows, panel, left, bottom);
            }

            Plugin.Log.LogDebug("Layout cloth bounds: left=" + left + ", bottom=" + bottom
                + ", length=" + length + ", height=" + height);
            // Assembly-CSharp: these are stationary hole masks created by
            // SetupCutout, not the movable PatternPanel fabric representation.
            // sourceState.cutoutActive is cleared when a slice is discarded.
            if (GameAccess.Member(room, "CutOutPieces") is IEnumerable cuts)
            {
                foreach (object cut in cuts)
                {
                    if (cut == null || !ReferenceEquals(GameAccess.Member(cut, "cutOutFrom"), fabric))
                        continue;
                    object state = GameAccess.Member(cut, "sourceState");
                    if (state != null && !GameAccess.Bool(state, "cutoutActive"))
                        continue;
                    var renderer = GameAccess.Member(cut, "PieceRenderer") as Renderer;
                    if (renderer == null)
                        throw new InvalidOperationException("A fabric hole mask has no renderer.");
                    Bounds bounds = renderer.bounds;
                    if (float.IsNaN(bounds.min.x) || float.IsNaN(bounds.min.y)
                        || float.IsNaN(bounds.max.x) || float.IsNaN(bounds.max.y)
                        || float.IsInfinity(bounds.min.x) || float.IsInfinity(bounds.min.y)
                        || float.IsInfinity(bounds.max.x) || float.IsInfinity(bounds.max.y))
                        throw new InvalidOperationException("A fabric hole mask has invalid geometry.");
                    if (bounds.size.x <= 0f || bounds.size.y <= 0f
                        || bounds.max.x < left || bounds.min.x > left + length
                        || bounds.max.y < bottom || bounds.min.y > bottom + height)
                        continue;
                    // PaintPoly clips to the grid. A hole may legitimately cross
                    // an edge or extend into cloth that has already been discarded.
                    PaintCutBounds(grid, cols, rows, bounds, left, bottom);
                }
            }
            return grid;
        }

        static void PaintCutBounds(Grid grid, int cols, int rows, Bounds bounds, float left, float bottom)
        {
            var poly = new[]
            {
                new Vector2(bounds.min.x, bounds.min.y), new Vector2(bounds.max.x, bounds.min.y),
                new Vector2(bounds.max.x, bounds.max.y), new Vector2(bounds.min.x, bounds.max.y)
            };
            PaintPoly(grid, cols, rows, poly, left, bottom, 1);
        }

        static void PaintPoly(Grid grid, int cols, int rows, Vector2[] poly, float left, float bottom, int id)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (Vector2 point in poly)
            {
                minX = Mathf.Min(minX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxX = Mathf.Max(maxX, point.x);
                maxY = Mathf.Max(maxY, point.y);
            }
            int x0 = Mathf.Max(0, Mathf.FloorToInt((minX - left) / Cell) - 1);
            int y0 = Mathf.Max(0, Mathf.FloorToInt((minY - bottom) / Cell) - 1);
            int x1 = Mathf.Min(cols - 1, Mathf.CeilToInt((maxX - left) / Cell) + 1);
            int y1 = Mathf.Min(rows - 1, Mathf.CeilToInt((maxY - bottom) / Cell) + 1);
            var filled = new bool[cols * rows];
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var center = new Vector2(left + (x + 0.5f) * Cell, bottom + (y + 0.5f) * Cell);
                    if (!Inside(poly, center))
                        continue;
                    filled[y * cols + x] = true;
                }
            }
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!filled[y * cols + x])
                        continue;
                    grid[y * cols + x] = id;
                }
            }
            ExpandFilled(grid, cols, filled);
        }

        static bool TryMakePiece(object panel, out Piece piece)
        {
            piece = null;
            if (!IsUncutPattern(panel))
                return false;
            var world = GameAccess.Member(panel, "WorldObj") as GameObject;
            if (world == null)
                return false;

            bool bias = false;
            object definition = GameAccess.Member(panel, "definition");
            if (definition != null)
                bias = GameAccess.Bool(definition, "isBiasCut");

            var orients = new List<Stamp>();
            AddOrient(orients, panel, FlatGrain(panel, world.transform, Vector3.right), bias);
            if (bias)
                AddOrient(orients, panel, FlatGrain(panel, world.transform, Vector3.up), bias);
            if (orients.Count == 0)
                return false;
            piece = new Piece { Panel = panel, World = world, Orients = orients.ToArray() };
            return true;
        }

        static void AddOrient(List<Stamp> orients, object panel, Quaternion rotation, bool bias)
        {
            AddOne(orients, panel, rotation, bias);
            AddOne(orients, panel, Quaternion.AngleAxis(180f, Vector3.forward) * rotation, bias);
        }

        static void AddOne(List<Stamp> orients, object panel, Quaternion rotation, bool bias)
        {
            if (!GrainOk(panel, rotation, bias))
                return;
            Stamp stamp = BuildStamp(panel, rotation);
            if (stamp != null)
                orients.Add(stamp);
        }

        static bool GrainOk(object panel, Quaternion rotation, bool bias)
        {
            if (!(GameAccess.Member(panel, "idealGrainDirection") is Vector3 grain) || grain.sqrMagnitude < 0.0001f)
                return true;
            Vector3 world = rotation * grain;
            world.z = 0f;
            if (world.sqrMagnitude < 0.0001f)
                return false;
            world.Normalize();
            float right = Mathf.Abs(Vector3.Dot(world, Vector3.right));
            float up = Mathf.Abs(Vector3.Dot(world, Vector3.up));
            if (bias)
                return right > 0.99f || up > 0.99f;
            return right > 0.99f;
        }

        static Stamp BuildStamp(object panel, Quaternion rotation)
        {
            if (!Sample(panel, rotation, out Vector2[] points, out int[] tris))
                return null;
            BoundsOf(points, out float minX, out float minY, out float maxX, out float maxY);
            int spanX = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / Cell - 0.0001f));
            int spanY = Mathf.Max(1, Mathf.CeilToInt((maxY - minY) / Cell - 0.0001f));
            if (spanX > 2000 || spanY > 2000)
                return null;
            var filled = new bool[spanX * spanY];
            Cover(filled, spanX, spanY, minX, minY, points, tris);
            var packed = new List<Vector2Int>();
            int maxCellX = 0, maxCellY = 0;
            for (int y = 0; y < spanY; y++)
            {
                for (int x = 0; x < spanX; x++)
                {
                    if (filled[y * spanX + x])
                    {
                        packed.Add(new Vector2Int(x, y));
                        maxCellX = Mathf.Max(maxCellX, x);
                        maxCellY = Mathf.Max(maxCellY, y);
                    }
                }
            }
            if (packed.Count == 0)
                return null;
            int width = maxCellX + 1;
            var solid = new bool[width * (maxCellY + 1)];
            for (int i = 0; i < packed.Count; i++)
                solid[packed[i].y * width + packed[i].x] = true;
            var left = new List<Vector2Int>();
            var down = new List<Vector2Int>();
            for (int i = 0; i < packed.Count; i++)
            {
                Vector2Int cell = packed[i];
                if (cell.x == 0 || !solid[cell.y * width + cell.x - 1])
                    left.Add(cell);
                if (cell.y == 0 || !solid[(cell.y - 1) * width + cell.x])
                    down.Add(cell);
            }
            var halo = new HashSet<Vector2Int>();
            int height = maxCellY + 1;
            foreach (Vector2Int c in packed)
            {
                for (int dy = -Gap; dy <= Gap; dy++)
                {
                    for (int dx = -Gap; dx <= Gap; dx++)
                    {
                        int x = c.x + dx;
                        int y = c.y + dy;
                        bool insidePiece = x >= 0 && y >= 0 && x < width && y < height && solid[y * width + x];
                        if (!insidePiece)
                            halo.Add(new Vector2Int(x, y));
                    }
                }
            }
            var haloArray = new Vector2Int[halo.Count];
            halo.CopyTo(haloArray);
            return new Stamp
            {
                Rotation = rotation,
                Cells = packed.ToArray(),
                MaxX = maxCellX,
                MaxY = maxCellY,
                Left = left.ToArray(),
                Down = down.ToArray(),
                Halo = haloArray,
                PadX = 0,
                PadY = 0,
                VisualW = Mathf.Max(Cell, maxX - minX),
                VisualH = Mathf.Max(Cell, maxY - minY)
            };
        }

        static GameObject EnsureWorld(object panel)
        {
            var world = GameAccess.Member(panel, "WorldObj") as GameObject;
            if (!GameAccess.Bool(panel, "IsWorld3D") || world == null || !world.activeInHierarchy)
                AutoMeasure.InvokeRequired(panel, "ShowRepresentation3D", true, true, true);
            // The game may replace WorldObj when changing representations.
            world = GameAccess.Member(panel, "WorldObj") as GameObject;
            if (world == null || !world.activeInHierarchy || !GameAccess.Bool(panel, "IsWorld3D"))
                throw new InvalidOperationException("A pattern did not become visible on the cloth.");
            return world;
        }

        static List<Choice> ValidateLayout(List<Piece> pieces, Grid blocked, int cols, int rows, List<Choice> layout)
        {
            Grid grid = blocked.Clone();
            var seen = new HashSet<int>();
            var undo = new List<int>();
            var accepted = new List<Choice>();
            foreach (Choice choice in layout)
            {
                if (choice.Piece < 0 || choice.Piece >= pieces.Count || seen.Contains(choice.Piece)
                    || choice.Orient < 0 || choice.Orient >= pieces[choice.Piece].Orients.Length)
                    continue;
                Stamp stamp = pieces[choice.Piece].Orients[choice.Orient];
                if (!Fits(grid, stamp, choice.Col, choice.Row, cols, rows))
                    continue;
                seen.Add(choice.Piece);
                Paint(grid, stamp, choice.Col, choice.Row, cols, choice.Piece + 2, undo);
                accepted.Add(choice);
            }
            if (accepted.Count != layout.Count)
                Plugin.Log.LogWarning("Skipped " + (layout.Count - accepted.Count)
                    + " invalid/conflicting placement candidates; keeping " + accepted.Count + " valid placements.");
            return accepted;
        }

        static void Put(Piece piece, Stamp stamp, float minX, float minY, float z)
        {
            piece.World = EnsureWorld(piece.Panel);
            Transform world = piece.World.transform;
            world.rotation = stamp.Rotation;
            Extents(piece.Panel, world, out float curMinX, out float curMinY, out _, out _);
            Vector3 position = world.position;
            position.x += minX - curMinX;
            position.y += minY - curMinY;
            position.z = z;
            world.position = position;
        }

        static void Extents(object panel, Transform world, out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = float.MaxValue;
            minY = float.MaxValue;
            maxX = float.MinValue;
            maxY = float.MinValue;
            Vector3[] verts = MeshVerts(panel);
            if (verts != null)
            {
                foreach (Vector3 vertex in verts)
                {
                    Vector3 worldPoint = world.TransformPoint(vertex);
                    minX = Mathf.Min(minX, worldPoint.x);
                    minY = Mathf.Min(minY, worldPoint.y);
                    maxX = Mathf.Max(maxX, worldPoint.x);
                    maxY = Mathf.Max(maxY, worldPoint.y);
                }
                return;
            }
            if (!(GameAccess.Member(panel, "ColliderPoints") is Vector2[] points))
                return;
            foreach (Vector2 point in points)
            {
                Vector3 worldPoint = world.TransformPoint(new Vector3(point.x, point.y, 0f));
                minX = Mathf.Min(minX, worldPoint.x);
                minY = Mathf.Min(minY, worldPoint.y);
                maxX = Mathf.Max(maxX, worldPoint.x);
                maxY = Mathf.Max(maxY, worldPoint.y);
            }
        }

        static void PaintPlaced(Grid grid, int cols, int rows, object panel, float left, float bottom)
        {
            var worldGo = GameAccess.Member(panel, "WorldObj") as GameObject;
            if (worldGo == null)
                return;
            if (!Sample(panel, worldGo.transform.rotation, out Vector2[] points, out int[] tris))
                return;
            var filled = new bool[cols * rows];
            Cover(filled, cols, rows, left, bottom, points, tris);
            for (int i = 0; i < filled.Length; i++)
            {
                if (filled[i])
                    grid[i] = 1;
            }
            ExpandFilled(grid, cols, filled);
        }

        static void ExpandFilled(Grid grid, int cols, bool[] filled)
        {
            int rows = grid.Length / cols;
            var extra = new List<int>();
            for (int i = 0; i < filled.Length; i++)
            {
                if (!filled[i])
                    continue;
                int x0 = i % cols;
                int y0 = i / cols;
                for (int dy = -Gap; dy <= Gap; dy++)
                {
                    for (int dx = -Gap; dx <= Gap; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        int x = x0 + dx;
                        int y = y0 + dy;
                        if (x < 0 || y < 0 || x >= cols || y >= rows)
                            continue;
                        int index = y * cols + x;
                        if (grid[index] == 0)
                            extra.Add(index);
                    }
                }
            }
            foreach (int index in extra)
                grid[index] = 1;
        }

        // The collider path is one boundary loop and can leave the paper interior empty, so the stamp is the mesh triangles.
        static bool Sample(object panel, Quaternion rotation, out Vector2[] points, out int[] tris)
        {
            points = null;
            tris = null;
            var worldGo = GameAccess.Member(panel, "WorldObj") as GameObject;
            if (worldGo == null)
                return false;
            Transform world = worldGo.transform;
            Vector3 pos = world.position;
            Quaternion rot = world.rotation;
            Vector3 scale = world.localScale;
            try
            {
                world.localScale = Vector3.one;
                world.rotation = rotation;
                Vector3[] verts = MeshVerts(panel);
                int[] indices = MeshTris(panel);
                if (verts != null && indices != null)
                {
                    points = new Vector2[verts.Length];
                    for (int i = 0; i < verts.Length; i++)
                    {
                        Vector3 p = world.TransformPoint(verts[i]);
                        points[i] = new Vector2(p.x, p.y);
                    }
                    tris = indices;
                    return true;
                }
                if (GameAccess.Member(panel, "ColliderPoints") is Vector2[] local && local.Length >= 3)
                {
                    points = new Vector2[local.Length];
                    for (int i = 0; i < local.Length; i++)
                    {
                        Vector3 p = world.TransformPoint(new Vector3(local[i].x, local[i].y, 0f));
                        points[i] = new Vector2(p.x, p.y);
                    }
                    return true;
                }
                return false;
            }
            finally
            {
                world.localScale = scale;
                world.rotation = rot;
                world.position = pos;
            }
        }

        static Vector3[] MeshVerts(object panel)
        {
            try
            {
                var mesh = GameAccess.Member(panel, "PatternMesh") as Mesh;
                if (mesh == null || mesh.vertexCount < 3)
                    return null;
                Vector3[] verts = mesh.vertices;
                return verts != null && verts.Length >= 3 ? verts : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static int[] MeshTris(object panel)
        {
            try
            {
                var mesh = GameAccess.Member(panel, "PatternMesh") as Mesh;
                if (mesh == null)
                    return null;
                int[] tris = mesh.triangles;
                return tris != null && tris.Length >= 3 ? tris : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static void BoundsOf(Vector2[] points, out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = float.MaxValue;
            minY = float.MaxValue;
            maxX = float.MinValue;
            maxY = float.MinValue;
            foreach (Vector2 point in points)
            {
                minX = Mathf.Min(minX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxX = Mathf.Max(maxX, point.x);
                maxY = Mathf.Max(maxY, point.y);
            }
        }

        static void Cover(bool[] filled, int width, int height, float originX, float originY, Vector2[] points, int[] tris)
        {
            if (points == null || points.Length < 3)
                return;
            if (tris != null)
            {
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int a = tris[i];
                    int b = tris[i + 1];
                    int c = tris[i + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= points.Length || b >= points.Length || c >= points.Length)
                        continue;
                    CoverTri(filled, width, height, originX, originY, points[a], points[b], points[c]);
                }
                return;
            }
            for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
                Walk(filled, width, height, originX, originY, points[j], points[i]);
            BoundsOf(points, out float minX, out float minY, out float maxX, out float maxY);
            int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - originX) / Cell), 0, width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((minY - originY) / Cell), 0, height - 1);
            int x1 = Mathf.Clamp(Mathf.FloorToInt((maxX - originX) / Cell), 0, width - 1);
            int y1 = Mathf.Clamp(Mathf.FloorToInt((maxY - originY) / Cell), 0, height - 1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var center = new Vector2(originX + (x + 0.5f) * Cell, originY + (y + 0.5f) * Cell);
                    if (Inside(points, center))
                        filled[y * width + x] = true;
                }
            }
        }

        static void CoverTri(bool[] filled, int width, int height, float originX, float originY, Vector2 a, Vector2 b, Vector2 c)
        {
            Walk(filled, width, height, originX, originY, a, b);
            Walk(filled, width, height, originX, originY, b, c);
            Walk(filled, width, height, originX, originY, c, a);
            float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
            float maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            float maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
            int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - originX) / Cell), 0, width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((minY - originY) / Cell), 0, height - 1);
            int x1 = Mathf.Clamp(Mathf.FloorToInt((maxX - originX) / Cell), 0, width - 1);
            int y1 = Mathf.Clamp(Mathf.FloorToInt((maxY - originY) / Cell), 0, height - 1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var center = new Vector2(originX + (x + 0.5f) * Cell, originY + (y + 0.5f) * Cell);
                    if (InTri(center, a, b, c))
                        filled[y * width + x] = true;
                }
            }
        }

        static void Walk(bool[] filled, int width, int height, float originX, float originY, Vector2 a, Vector2 b)
        {
            float dist = Vector2.Distance(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / (Cell * 0.5f)));
            for (int i = 0; i <= steps; i++)
            {
                Vector2 p = Vector2.Lerp(a, b, i / (float)steps);
                int x = Mathf.FloorToInt((p.x - originX) / Cell);
                int y = Mathf.FloorToInt((p.y - originY) / Cell);
                if (x < 0 || y < 0 || x >= width || y >= height)
                    continue;
                filled[y * width + x] = true;
            }
        }

        static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float c0 = Cross(b - a, p - a);
            float c1 = Cross(c - b, p - b);
            float c2 = Cross(a - c, p - c);
            const float e = 0.0000001f;
            bool neg = c0 < -e || c1 < -e || c2 < -e;
            bool pos = c0 > e || c1 > e || c2 > e;
            return !(neg && pos);
        }

        static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        static Quaternion FlatGrain(object panel, Transform world, Vector3 direction)
        {
            if (!(GameAccess.Member(panel, "idealGrainDirection") is Vector3 grain) || grain.sqrMagnitude < 0.0001f)
                return world.rotation;
            Vector3 current = world.TransformDirection(grain);
            current.z = 0f;
            direction.z = 0f;
            if (current.sqrMagnitude < 0.0001f || direction.sqrMagnitude < 0.0001f)
                return world.rotation;
            float turn = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg - Mathf.Atan2(current.y, current.x) * Mathf.Rad2Deg;
            return Quaternion.AngleAxis(turn, Vector3.forward) * world.rotation;
        }

        static bool Inside(Vector2[] poly, Vector2 point)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if ((poly[i].y > point.y) != (poly[j].y > point.y)
                    && point.x < (poly[j].x - poly[i].x) * (point.y - poly[i].y) / (poly[j].y - poly[i].y + 0.0000001f) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        static bool Cloth(MonoBehaviour room, object fabricPiece, out float left, out float bottom, out float length, out float height)
        {
            left = bottom = length = height = 0f;
            var fabric = GameAccess.Member(room, "Fabric") as GameObject;
            var renderer = GameAccess.Member(room, "fabricRenderer") as Renderer;
            if (renderer == null && fabric != null)
                renderer = fabric.GetComponent<Renderer>();
            float owned = GameAccess.Float(fabricPiece, "length", 0f);
            float cap = GameAccess.Float(room, "maximumFabricLength", 2f);
            float clothHeight = GameAccess.Float(room, "fabricHeight", 1.3f);
            float start = GameAccess.Float(GameAccess.Member(room, "FabricUsageCounter"), "FabricStartOffset", 0f);
            // Only part of a long bolt is open on the table at once (the game's own rule:
            // open = Min(remaining, maximumFabricLength)). Never plan past it.
            float open = owned > 0.2f ? Mathf.Min(owned, cap) : cap;
            if (owned > cap)
                _lengthCapped = true;
            const float margin = 0.002f;
            if (renderer != null && renderer.bounds.size.x > 0.2f && renderer.bounds.size.y > 0.2f)
            {
                Bounds bounds = renderer.bounds;
                left = Mathf.Max(bounds.min.x, start) + margin;
                float right = Mathf.Min(start + open, bounds.max.x) - margin;
                bottom = bounds.min.y + margin;
                length = right - left;
                height = bounds.size.y - margin * 2f;
            }
            else if (fabric != null)
            {
                left = start + margin;
                length = open - margin * 2f;
                bottom = fabric.transform.position.y - clothHeight * 0.5f + margin;
                height = clothHeight - margin * 2f;
            }
            return length > 0.15f && height > 0.15f;
        }

        static void ShowFabric(MonoBehaviour room, object fabric)
        {
            if (fabric == null)
                return;
            object current = AccessTools.Method(room.GetType(), "GetCurrentDisplayedFabricPiece")?.Invoke(room, null);
            if (ReferenceEquals(current, fabric))
            {
                AccessTools.Method(room.GetType(), "UpdateDisplayedFabric")?.Invoke(room, null);
                return;
            }
            GameAccess.SetMember(room, "_displayedFabricPiece", fabric);
            AccessTools.Method(room.GetType(), "UpdateDisplayedFabric")?.Invoke(room, null);
        }

        static void Unroll(MonoBehaviour room, object fabric, float used)
        {
            var cloth = GameAccess.Member(room, "Fabric") as GameObject;
            float origin = cloth != null ? cloth.transform.position.x : 0f;
            float start = GameAccess.Float(GameAccess.Member(room, "FabricUsageCounter"), "FabricStartOffset", origin);
            float owned = GameAccess.Float(fabric, "length", used);
            float maxExtent = Mathf.Max(0.25f, start + owned - origin);
            float extent = Mathf.Clamp(start + used + 0.08f - origin, 0.25f, maxExtent);
            GameAccess.SetMember(room, "_unrollTargetExtent", extent);
            GameAccess.SetMember(room, "_unrollCurrentExtent", extent);
            GameAccess.SetMember(room, "_unrolling", true);
        }

        public static void ReturnPieces(List<object> patterns)
        {
            if (patterns == null)
                return;
            foreach (object panel in patterns)
                if (IsUncutPattern(panel))
                    ReturnToMenu(panel);
            _notice = "";
        }

        static void ReturnToMenu(object panel)
        {
            if (panel == null)
                return;
            AutoMeasure.InvokeRequired(panel, "ShowRepresentation2D", -1);
            if (GameAccess.Bool(panel, "IsWorld3D"))
                throw new InvalidOperationException("A pattern could not be returned to the menu.");
        }

        public static void Remember(List<object> patterns, object selected)
        {
            var live = new HashSet<int>();
            foreach (object panel in patterns)
            {
                int id = Id(panel);
                live.Add(id);
                if (Decided.Contains(id))
                    continue;
                Assign[id] = selected;
                Decided.Add(id);
            }
            var stale = new List<int>();
            foreach (int id in Decided)
            {
                if (!live.Contains(id))
                    stale.Add(id);
            }
            foreach (int id in stale)
            {
                Decided.Remove(id);
                Assign.Remove(id);
            }
        }

        static bool IsUncutPattern(object panel)
        {
            return panel != null && !GameAccess.Bool(panel, "HasBeenCut")
                && GameAccess.Member(panel, "cutOutFrom") == null
                && !GameAccess.Bool(panel, "beingCut")
                && !GameAccess.Bool(panel, "VisibleOnMannequin")
                && !GameAccess.Bool(panel, "PlacedOnMannequin");
        }

        static List<object> UncutPatterns(List<object> patterns)
        {
            var live = new List<object>();
            if (patterns != null)
                foreach (object panel in patterns)
                    if (IsUncutPattern(panel))
                        live.Add(panel);
            return live;
        }

        public static List<object> Patterns(object game)
        {
            var list = new List<object>();
            object panels = GameAccess.Member(GameAccess.Member(game, "activeDress"), "PanelPieces");
            if (!(panels is IEnumerable items))
                return list;
            foreach (object panel in items)
            {
                if (!IsUncutPattern(panel))
                    continue;
                list.Add(panel);
            }
            return list;
        }

        public static List<object> Fabrics()
        {
            var list = new List<object>();
            object owned = GameAccess.Member(GameAccess.Static("PlayerProgress", "Current"), "ownedFabricPieces");
            if (owned is IEnumerable items)
            {
                foreach (object piece in items)
                {
                    if (piece != null)
                        list.Add(piece);
                }
            }

            object sidebar = GameAccess.Find("SidebarInventory");
            if (GameAccess.Member(sidebar, "_fabricItems") is IEnumerable rows)
            {
                foreach (object row in rows)
                {
                    object piece = GameAccess.Member(row, "FabricPiece");
                    if (piece != null && !ContainsFabric(list, piece))
                        list.Add(piece);
                }
            }

            var room = GameAccess.Find("CuttingRoom") as MonoBehaviour;
            object shown = room == null ? null : AccessTools.Method(room.GetType(), "GetCurrentDisplayedFabricPiece")?.Invoke(room, null);
            if (shown != null && !ContainsFabric(list, shown))
                list.Insert(0, shown);
            return list;
        }

        public static object OpenFabric(MonoBehaviour room)
        {
            object shown = room == null ? null : AccessTools.Method(room.GetType(), "GetCurrentDisplayedFabricPiece")?.Invoke(room, null);
            if (shown != null)
                return shown;
            var fabrics = Fabrics();
            return fabrics.Count > 0 ? fabrics[0] : null;
        }

        public static bool ContainsFabric(List<object> fabrics, object fabric)
        {
            foreach (object piece in fabrics)
            {
                if (ReferenceEquals(piece, fabric))
                    return true;
            }
            return false;
        }

        static int Id(object panel)
        {
            return panel is UnityEngine.Object unity ? unity.GetInstanceID() : panel.GetHashCode();
        }

        public static string NameOf(object panel)
        {
            try
            {
                string name = GameAccess.Member(panel, "DisplayName") as string;
                return string.IsNullOrWhiteSpace(name) ? "Pattern" : name;
            }
            catch (Exception)
            {
                return "Pattern";
            }
        }

        public static string FabricLabel(object piece)
        {
            return ShortFabric(piece) + "   " + GameAccess.Float(piece, "length", 0f).ToString("0.0") + "m";
        }

        public static string ShortFabric(object piece)
        {
            object fabric = GameAccess.Member(piece, "fabric");
            string name = GameAccess.Member(fabric, "PrettyName") as string;
            return string.IsNullOrWhiteSpace(name) ? "Fabric" : name;
        }

        public static bool IsStarred(object piece)
        {
            object fabric = GameAccess.Member(piece, "fabric");
            if (fabric == null)
                return false;
            object quest = GameAccess.Member(GameAccess.Static("PlayerProgress", "Current"), "currentQuestState");
            if (!(GameAccess.Member(quest, "sketchbookPlacements") is IEnumerable placements))
                return false;
            foreach (object placement in placements)
            {
                if (placement != null && ReferenceEquals(GameAccess.Member(placement, "fabric"), fabric))
                    return true;
            }
            return false;
        }
        
    }
    // A hidden object only AutoSew uses, so the game can't stop our coroutines.
    sealed class AutoSewRunner : MonoBehaviour { }
}
