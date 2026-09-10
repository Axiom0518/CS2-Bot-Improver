using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace BotState;

// P5b — map-once nav AABB graph. Portals ≈ shared / near-touching area edges.
// No .nav parse, no GetAllNavAreas per tick.
public partial class BotState
{
    private struct NavAreaBox
    {
        public uint Id;
        public float MinX, MinY, MinZ;
        public float MaxX, MaxY, MaxZ;
        public float Cx, Cy, Cz;
    }

    private struct NavPortal
    {
        public int AreaIndexA;
        public int AreaIndexB;
        public float MidX, MidY, MidZ;
        // Unit XY from A center → B center (look push direction when standing in A).
        public float DirAtoBX, DirAtoBY;
    }

    private NavAreaBox[] _navBoxes = Array.Empty<NavAreaBox>();
    private readonly Dictionary<uint, int> _navIdToIndex = new();
    // Per-area list of portal indices that touch this area.
    private List<int>[] _navPortalsByArea = Array.Empty<List<int>>();
    private NavPortal[] _navPortals = Array.Empty<NavPortal>();
    // Spatial hash kept after build — point queries without GetClosestNavArea.
    private Dictionary<long, List<int>> _navGrid = new();
    private int _navWorkFrame;
    private bool _navGraphReady;
    private string _navGraphMap = "";
    private int _navGraphBuildMs;
    private float _navNextEnsureAt;
    private int _navEnsureAttempts;
    private string _navLastStatus = "never";

    private const float NavPortalSeamXy = 18f;
    private const float NavPortalSeamZ = 80f;
    private const float NavPortalMinContact = 24f;
    private const float NavGridCell = 256f;
    private const float NavEnsureRetrySeconds = 3.0f;

    private void ClearNavGraph()
    {
        _navBoxes = Array.Empty<NavAreaBox>();
        _navIdToIndex.Clear();
        _navPortalsByArea = Array.Empty<List<int>>();
        _navPortals = Array.Empty<NavPortal>();
        _navGrid = new Dictionary<long, List<int>>();
        _navGraphReady = false;
        _navGraphMap = "";
        _navGraphBuildMs = 0;
    }

    // Safe to call anytime (lookdebug / preaim / delayed Load). Throttled while failing.
    private bool EnsureNavGraph(bool force = false)
    {
        if (_navGraphReady && !force)
            return true;

        float now = Server.CurrentTime;
        if (!force && now < _navNextEnsureAt)
            return false;

        _navNextEnsureAt = now + NavEnsureRetrySeconds;
        _navEnsureAttempts++;

        string map = Server.MapName;
        if (string.IsNullOrEmpty(map))
            map = _navGraphMap;
        if (string.IsNullOrEmpty(map))
            map = "unknown";

        try
        {
            RebuildNavPortalGraph(map);
        }
        catch (Exception ex)
        {
            ClearNavGraph();
            _navLastStatus = "exception:" + ex.GetType().Name;
            Logger.LogWarning(ex, "[Smarter-Bot] nav portal graph build failed");
            Console.WriteLine($"[Smarter-Bot] nav portal graph FAILED: {ex.Message}");
            return false;
        }

        return _navGraphReady;
    }

    private void OnNavMapStart(string mapName)
    {
        _navNextEnsureAt = 0f;
        _navEnsureAttempts = 0;
        try
        {
            RebuildNavPortalGraph(string.IsNullOrEmpty(mapName) ? "unknown" : mapName);
        }
        catch (Exception ex)
        {
            ClearNavGraph();
            _navLastStatus = "exception:" + ex.GetType().Name;
            Logger.LogWarning(ex, "[Smarter-Bot] nav portal graph build failed");
            Console.WriteLine($"[Smarter-Bot] nav portal graph FAILED: {ex.Message}");
        }
    }

    private void RebuildNavPortalGraph(string mapName)
    {
        string begin =
            $"[Smarter-Bot] nav portal build begin map={mapName} attempt={_navEnsureAttempts}";
        Logger.LogInformation("{Msg}", begin);
        Console.WriteLine(begin);

        ClearNavGraph();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<CCSNavArea> areas;
        try
        {
            areas = CCSNavArea.GetAllNavAreas();
        }
        catch (Exception ex)
        {
            _navLastStatus = "GetAllNavAreas_throw:" + ex.GetType().Name;
            Logger.LogWarning(ex, "[Smarter-Bot] GetAllNavAreas unavailable");
            return;
        }

        int rawCount = areas?.Count ?? 0;
        if (areas == null || rawCount == 0)
        {
            _navLastStatus = $"empty attempt={_navEnsureAttempts}";
            string emptyMsg =
                $"[Smarter-Bot] nav portal graph: 0 areas (GetAllNavAreas empty; " +
                $"attempt={_navEnsureAttempts} map={mapName})";
            Logger.LogWarning("{Msg}", emptyMsg);
            Console.WriteLine(emptyMsg);
            return;
        }

        var boxes = new List<NavAreaBox>(areas.Count);
        for (int i = 0; i < areas.Count; i++)
        {
            var a = areas[i];
            if (a == null) continue;
            Vector? min = a.Min;
            Vector? max = a.Max;
            Vector? c = a.Center;
            if (min == null || max == null || c == null) continue;

            var box = new NavAreaBox
            {
                Id = a.Id,
                MinX = min.X, MinY = min.Y, MinZ = min.Z,
                MaxX = max.X, MaxY = max.Y, MaxZ = max.Z,
                Cx = c.X, Cy = c.Y, Cz = c.Z,
            };
            // Degenerate / tiny crumbs — skip.
            if (box.MaxX - box.MinX < 4f && box.MaxY - box.MinY < 4f)
                continue;
            _navIdToIndex[box.Id] = boxes.Count;
            boxes.Add(box);
        }

        _navBoxes = boxes.ToArray();
        if (_navBoxes.Length == 0) return;

        // Spatial hash: cell → area indices.
        var grid = new Dictionary<long, List<int>>(boxes.Count);
        for (int i = 0; i < _navBoxes.Length; i++)
        {
            ref readonly var b = ref _navBoxes[i];
            int x0 = (int)MathF.Floor(b.MinX / NavGridCell);
            int x1 = (int)MathF.Floor(b.MaxX / NavGridCell);
            int y0 = (int)MathF.Floor(b.MinY / NavGridCell);
            int y1 = (int)MathF.Floor(b.MaxY / NavGridCell);
            for (int gx = x0; gx <= x1; gx++)
            for (int gy = y0; gy <= y1; gy++)
            {
                long key = ((long)gx << 32) ^ (uint)gy;
                if (!grid.TryGetValue(key, out var list))
                {
                    list = new List<int>(4);
                    grid[key] = list;
                }
                list.Add(i);
            }
        }

        var portals = new List<NavPortal>(boxes.Count * 2);
        var seenPairs = new HashSet<ulong>();

        for (int i = 0; i < _navBoxes.Length; i++)
        {
            ref readonly var a = ref _navBoxes[i];
            int x0 = (int)MathF.Floor((a.MinX - NavPortalSeamXy) / NavGridCell);
            int x1 = (int)MathF.Floor((a.MaxX + NavPortalSeamXy) / NavGridCell);
            int y0 = (int)MathF.Floor((a.MinY - NavPortalSeamXy) / NavGridCell);
            int y1 = (int)MathF.Floor((a.MaxY + NavPortalSeamXy) / NavGridCell);

            for (int gx = x0; gx <= x1; gx++)
            for (int gy = y0; gy <= y1; gy++)
            {
                long key = ((long)gx << 32) ^ (uint)gy;
                if (!grid.TryGetValue(key, out var list)) continue;
                for (int n = 0; n < list.Count; n++)
                {
                    int j = list[n];
                    if (j <= i) continue;

                    uint lo = _navBoxes[i].Id;
                    uint hi = _navBoxes[j].Id;
                    if (lo > hi) (lo, hi) = (hi, lo);
                    ulong pair = ((ulong)lo << 32) | hi;
                    if (!seenPairs.Add(pair)) continue;

                    if (!TryBuildPortal(i, j, out var portal))
                        continue;
                    portals.Add(portal);
                }
            }
        }

        _navGrid = grid;
        _navPortals = portals.ToArray();
        _navPortalsByArea = new List<int>[_navBoxes.Length];
        for (int i = 0; i < _navBoxes.Length; i++)
            _navPortalsByArea[i] = new List<int>(4);

        for (int p = 0; p < _navPortals.Length; p++)
        {
            _navPortalsByArea[_navPortals[p].AreaIndexA].Add(p);
            _navPortalsByArea[_navPortals[p].AreaIndexB].Add(p);
        }

        sw.Stop();
        _navGraphBuildMs = (int)sw.ElapsedMilliseconds;
        _navGraphMap = mapName;
        _navGraphReady = _navPortals.Length > 0;
        _navLastStatus = _navGraphReady
            ? $"ok areas={_navBoxes.Length} portals={_navPortals.Length}"
            : $"built_no_portals areas={_navBoxes.Length}";

        string msg =
            $"[Smarter-Bot] nav portals map={mapName} rawAreas={rawCount} " +
            $"areas={_navBoxes.Length} portals={_navPortals.Length} " +
            $"buildMs={_navGraphBuildMs} ready={_navGraphReady}";
        Logger.LogInformation("{Msg}", msg);
        Console.WriteLine(msg);
    }

    private bool TryBuildPortal(int i, int j, out NavPortal portal)
    {
        portal = default;
        ref readonly var a = ref _navBoxes[i];
        ref readonly var b = ref _navBoxes[j];

        float ox0 = Math.Max(a.MinX, b.MinX);
        float ox1 = Math.Min(a.MaxX, b.MaxX);
        float oy0 = Math.Max(a.MinY, b.MinY);
        float oy1 = Math.Min(a.MaxY, b.MaxY);
        float oz0 = Math.Max(a.MinZ, b.MinZ);
        float oz1 = Math.Min(a.MaxZ, b.MaxZ);

        float ox = ox1 - ox0;
        float oy = oy1 - oy0;
        float oz = oz1 - oz0;

        // Allow a small seam (negative overlap = gap).
        if (ox < -NavPortalSeamXy || oy < -NavPortalSeamXy || oz < -NavPortalSeamZ)
            return false;

        bool xContact = ox >= -NavPortalSeamXy && ox <= NavPortalMinContact;
        bool yContact = oy >= -NavPortalSeamXy && oy <= NavPortalMinContact;
        bool xOverlap = ox > 0f;
        bool yOverlap = oy > 0f;

        // Doorway-like: thin contact on one XY axis, real overlap on the other.
        // Also accept shallow 2D overlap with Z compatibility (open rooms).
        bool doorway = (xContact && yOverlap && oy >= NavPortalMinContact)
                    || (yContact && xOverlap && ox >= NavPortalMinContact);
        bool shallow = xOverlap && yOverlap && (ox <= 96f || oy <= 96f);
        if (!doorway && !shallow)
            return false;

        float midX, midY, midZ;
        if (xContact && yOverlap)
        {
            midX = 0.5f * (Math.Min(a.MaxX, b.MaxX) + Math.Max(a.MinX, b.MinX));
            // Prefer the tighter of the two face mids when overlapping slightly.
            if (ox <= 0f)
                midX = (a.Cx <= b.Cx) ? a.MaxX : a.MinX;
            midY = 0.5f * (oy0 + oy1);
        }
        else if (yContact && xOverlap)
        {
            midY = 0.5f * (Math.Min(a.MaxY, b.MaxY) + Math.Max(a.MinY, b.MinY));
            if (oy <= 0f)
                midY = (a.Cy <= b.Cy) ? a.MaxY : a.MinY;
            midX = 0.5f * (ox0 + ox1);
        }
        else
        {
            midX = 0.5f * (ox0 + ox1);
            midY = 0.5f * (oy0 + oy1);
        }

        if (oz > 0f)
            midZ = 0.5f * (oz0 + oz1);
        else
            midZ = 0.5f * (a.Cz + b.Cz);

        float dx = b.Cx - a.Cx;
        float dy = b.Cy - a.Cy;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return false;
        dx /= len;
        dy /= len;

        portal = new NavPortal
        {
            AreaIndexA = i,
            AreaIndexB = j,
            MidX = midX,
            MidY = midY,
            MidZ = midZ,
            DirAtoBX = dx,
            DirAtoBY = dy,
        };
        return true;
    }

    private const float NavQueryMaxDist = 220f;

    private bool TryGetNavAreaIndex(float x, float y, float z, out int index)
    {
        index = -1;
        if (!_navGraphReady || _navBoxes.Length == 0 || _navGrid.Count == 0)
            return false;

        int gx = (int)MathF.Floor(x / NavGridCell);
        int gy = (int)MathF.Floor(y / NavGridCell);

        int bestContain = -1;
        float bestContainArea = float.MaxValue;
        int bestNear = -1;
        float bestNearD = NavQueryMaxDist * NavQueryMaxDist;

        for (int ix = gx - 1; ix <= gx + 1; ix++)
        for (int iy = gy - 1; iy <= gy + 1; iy++)
        {
            long key = ((long)ix << 32) ^ (uint)iy;
            if (!_navGrid.TryGetValue(key, out var list)) continue;
            for (int n = 0; n < list.Count; n++)
            {
                int i = list[n];
                ref readonly var b = ref _navBoxes[i];
                if (z < b.MinZ - NavPortalSeamZ || z > b.MaxZ + NavPortalSeamZ)
                    continue;

                if (x >= b.MinX && x <= b.MaxX && y >= b.MinY && y <= b.MaxY)
                {
                    float area = (b.MaxX - b.MinX) * (b.MaxY - b.MinY);
                    if (area < bestContainArea)
                    {
                        bestContainArea = area;
                        bestContain = i;
                    }
                    continue;
                }

                float dx = x - b.Cx;
                float dy = y - b.Cy;
                float d2 = dx * dx + dy * dy;
                if (d2 < bestNearD)
                {
                    bestNearD = d2;
                    bestNear = i;
                }
            }
        }

        index = bestContain >= 0 ? bestContain : bestNear;
        return index >= 0;
    }
}
