using System;
using System.Collections.Generic;
using System.Linq;

// Where a screen dragged in the layout editor snaps to, along one axis (millimetres).
static class Snap
{
    // pos/len: the dragged screen; spans: the other screens as (start, length) on the same axis.
    // Snaps when within tol of one of these, nearest wins:
    //  - either edge onto an edge of another screen (aligned, or touching it),
    //  - its centre onto another screen's centre,
    //  - its centre onto a seam where two other screens meet (e.g. centred under a pair),
    //  - its centre onto the centre of all other screens together.
    // guide is the line to draw for the snap that won, null when nothing snapped.
    public static double Axis(double pos, double len, IList<double[]> spans, double tol, out double? guide)
    {
        guide = null;
        var cands = new List<double[]>(); // { position for pos, guide line }
        foreach (var n in spans)
        {
            double a = n[0], b = n[0] + n[1], c = n[0] + n[1] / 2;
            cands.Add(new[] { a, a }); cands.Add(new[] { a - len, a });
            cands.Add(new[] { b, b }); cands.Add(new[] { b - len, b });
            cands.Add(new[] { c - len / 2, c });
            foreach (var o in spans)
                if (o != n && Math.Abs(o[0] - b) < 0.5) cands.Add(new[] { b - len / 2, b }); // seam
        }
        if (spans.Count > 1)
        {
            double g = (spans.Min(n => n[0]) + spans.Max(n => n[0] + n[1])) / 2;
            cands.Add(new[] { g - len / 2, g });
        }
        double best = pos, bestD = tol;
        foreach (var k in cands)
            if (Math.Abs(k[0] - pos) < bestD) { best = k[0]; bestD = Math.Abs(k[0] - pos); guide = k[1]; }
        return best;
    }
}
