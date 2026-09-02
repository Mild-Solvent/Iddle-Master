// IDLE MASTER - the WizTree view: the drive drawn as area instead of as text.
//
// The cleanup tree answers "what is in this folder". It cannot answer "where
// did 400 GB go" in one look, because a list of names all the same height
// gives a 2 KB shortcut the same weight as a 60 GB game. A treemap gives every
// node area in proportion to its bytes, so the answer is whatever is biggest
// on screen - you find the hog by looking, not by expanding.
//
// The layout is the squarified algorithm (Bruls, Huizing, van Wijk 2000): take
// children biggest-first and keep adding them to the current row for as long
// as that improves the worst aspect ratio in the row; when it stops improving,
// close the row and start another across the remaining space. The point is
// rectangles you can actually see and click, rather than the slivers a naive
// slice-and-dice produces once one child dwarfs its siblings.
//
// Nothing here can delete anything. It is a view onto the DiskTree the scan
// already built - no second walk, no file handles - and every action it offers
// is routed back through the cleanup window's own verdicts.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace IdleMaster
{
    // One laid-out rectangle: which node it is, where it landed, and how deep
    // in the drill-down it sits. Built fresh on every relayout.
    internal sealed class MapCell
    {
        public DiskTree Tree;
        public int Node;
        public RectangleF Bounds;
        public int Depth;           // 0 = an immediate child of the view root
        public int Group;           // index of the depth-0 ancestor, for colour
        public bool IsDir;
    }

    internal sealed class TreeMapView : Control
    {
        // Below this a rectangle is not worth laying out: it cannot be read,
        // cannot be clicked, and the recursion into it costs more than the
        // pixel it would paint.
        private const float MinSide = 3f;
        private const float LabelMinW = 44f;
        private const float LabelMinH = 15f;
        private const int MaxDepth = 4;
        private const int MaxCells = 12000;

        // Muted, evenly spaced hues that still sit next to the Ice palette. A
        // treemap has to be legible before it is tasteful: adjacent blocks have
        // to be told apart at a glance, which one accent colour cannot do.
        private static readonly Color[] Palette = new Color[]
        {
            Color.FromArgb( 61, 126, 191),   // steel blue (the house accent)
            Color.FromArgb( 74, 140, 130),   // teal
            Color.FromArgb(140, 110, 168),   // muted violet
            Color.FromArgb(176,  96,  96),   // dusty red
            Color.FromArgb(150, 138,  84),   // olive
            Color.FromArgb( 86, 122, 160),   // slate
            Color.FromArgb(120, 152, 100),   // sage
            Color.FromArgb(170, 126,  84),   // clay
            Color.FromArgb( 96, 116, 172),   // indigo
            Color.FromArgb(148, 108, 128),   // mauve
        };

        private readonly List<MapCell> cells = new List<MapCell>();
        private readonly Font labelFont = new Font("Segoe UI", 8f);
        private readonly Font crumbFont = new Font("Segoe UI", 9f);

        private DiskTree tree;
        private int rootNode = -1;
        private MapCell hot;
        private MapCell chosen;
        private bool dirty = true;

        // Where the drill-down has been, so Back has somewhere to go.
        private readonly List<int> trail = new List<int>();

        public event EventHandler<MapCellEventArgs> CellChosen;      // single click
        public event EventHandler<MapCellEventArgs> CellActivated;   // double click
        public event EventHandler RootChanged;

        public TreeMapView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Input;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public DiskTree Tree { get { return tree; } }
        public int RootNode { get { return rootNode; } }
        public bool CanGoUp { get { return trail.Count > 0; } }

        public MapCell Chosen { get { return chosen; } }

        // The crumb the window prints above the map.
        public string RootPath
        {
            get
            {
                if (tree == null || rootNode < 0) return "";
                try { return tree.PathOf(rootNode); }
                catch (Exception) { return ""; }
            }
        }

        public long RootBytes
        {
            get
            {
                if (tree == null || rootNode < 0) return 0;
                try { return tree.Bytes[rootNode]; }
                catch (Exception) { return 0; }
            }
        }

        public void Show(DiskTree t, int node, bool keepTrail)
        {
            if (!keepTrail) trail.Clear();
            tree = t;
            rootNode = node;
            hot = null;
            chosen = null;
            dirty = true;
            Invalidate();
            if (RootChanged != null) RootChanged(this, EventArgs.Empty);
        }

        public void Down(int node)
        {
            if (tree == null || node < 0 || !tree.IsDir(node)) return;
            trail.Add(rootNode);
            Show(tree, node, true);
        }

        public void Up()
        {
            if (trail.Count == 0) return;
            int back = trail[trail.Count - 1];
            trail.RemoveAt(trail.Count - 1);
            Show(tree, back, true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            dirty = true;
        }

        // ---- layout

        private void Layout_()
        {
            cells.Clear();
            dirty = false;
            if (tree == null || rootNode < 0) return;

            RectangleF area = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
            if (area.Width < 8 || area.Height < 8) return;

            Squarify(rootNode, area, 0, -1);
        }

        // Lays one node's children into the rectangle it owns, then recurses.
        //
        // Group is the colour bucket, and -1 means "not decided yet - give each
        // child its own". The decision is deferred until a level that actually
        // branches: a folder with a single child passes -1 down instead of its
        // own colour, because \.git\objects\... painted in one flat blue tells
        // you nothing. Once a level does branch, its colour is inherited all the
        // way down, so a subtree stays one recognisable block.
        //
        // The bucket is the child's index among its siblings, which are sorted
        // by size - so a folder keeps the same colour across relayouts. Keying
        // it off the running cell count instead made colours jump whenever a
        // sibling's subtree happened to produce a different number of cells.
        private void Squarify(int parent, RectangleF area, int depth, int group)
        {
            if (depth > MaxDepth || cells.Count >= MaxCells) return;
            if (area.Width < MinSide || area.Height < MinSide) return;

            List<int> kids = ChildrenBySize(parent);
            if (kids.Count == 0) return;

            long total = 0;
            foreach (int k in kids) total += Bytes(k);
            if (total <= 0) return;

            int at = 0;
            RectangleF free = area;

            while (at < kids.Count && free.Width >= MinSide && free.Height >= MinSide)
            {
                // How many bytes are still waiting for the space that is left.
                long remaining = 0;
                for (int i = at; i < kids.Count; i++) remaining += Bytes(kids[i]);
                if (remaining <= 0) break;

                bool horizontal = free.Width >= free.Height;
                float side = horizontal ? free.Height : free.Width;
                double scale = (free.Width * (double)free.Height) / remaining;

                // Grow the row while the worst rectangle in it keeps getting
                // squarer. The moment adding one more makes the worst ratio
                // worse, the row is as good as it is going to get.
                int count = 0;
                long rowSum = 0;
                double best = double.MaxValue;
                while (at + count < kids.Count)
                {
                    long next = Bytes(kids[at + count]);
                    if (next <= 0) { count++; continue; }
                    double ratio = Worst(rowSum + next, rowSum == 0 ? next : Math.Min(MinOf(kids, at, count), next),
                        Math.Max(MaxOf(kids, at, count), next), side, scale);
                    if (count > 0 && ratio > best) break;
                    best = ratio;
                    rowSum += next;
                    count++;
                }
                if (count == 0) break;

                float thickness = (float)(rowSum * scale / side);
                if (thickness < 0.5f) break;
                if (horizontal && thickness > free.Width) thickness = free.Width;
                if (!horizontal && thickness > free.Height) thickness = free.Height;

                float along = 0;
                for (int i = 0; i < count; i++)
                {
                    int node = kids[at + i];
                    long b = Bytes(node);
                    float extent = rowSum > 0 ? (float)(side * (b / (double)rowSum)) : 0;

                    RectangleF r = horizontal
                        ? new RectangleF(free.X, free.Y + along, thickness, extent)
                        : new RectangleF(free.X + along, free.Y, extent, thickness);
                    along += extent;

                    if (r.Width < MinSide || r.Height < MinSide) continue;

                    int bucket = group < 0 ? (at + i) % Palette.Length : group;
                    MapCell cell = new MapCell();
                    cell.Tree = tree;
                    cell.Node = node;
                    cell.Bounds = r;
                    cell.Depth = depth;
                    cell.Group = bucket;
                    cell.IsDir = tree.IsDir(node);
                    cells.Add(cell);
                    if (cells.Count >= MaxCells) return;

                    // Folders get their insides drawn too, inset by the border
                    // and the caption strip so the parent stays readable.
                    if (cell.IsDir && !tree.IsReparse(node))
                    {
                        RectangleF inner = r;
                        float pad = 1f;
                        float cap = r.Height > LabelMinH + 8 && r.Width > LabelMinW ? LabelMinH : 0;
                        inner.X += pad; inner.Y += pad + cap;
                        inner.Width -= pad * 2; inner.Height -= pad * 2 + cap;
                        // Keep looking for a level that branches. "Branches"
                        // has to be measured in area, not in child count: .git
                        // has a dozen children but \objects is 99% of it, and
                        // the other eleven never get a visible rectangle - so a
                        // count test says "branched" and paints the whole drive
                        // one flat blue. Once a colour IS chosen it is
                        // inherited all the way down, so a subtree never
                        // changes hue halfway.
                        bool dominates = r.Width * (double)r.Height
                                       >= area.Width * (double)area.Height * 0.9;
                        if (inner.Width >= MinSide && inner.Height >= MinSide)
                            Squarify(node, inner, depth + 1,
                                group < 0 && dominates ? -1 : bucket);
                    }
                }

                if (horizontal) { free.X += thickness; free.Width -= thickness; }
                else { free.Y += thickness; free.Height -= thickness; }
                at += count;
            }
        }

        private long MinOf(List<int> kids, int at, int count)
        {
            long m = long.MaxValue;
            for (int i = 0; i < count; i++) m = Math.Min(m, Bytes(kids[at + i]));
            return m == long.MaxValue ? 0 : m;
        }

        private long MaxOf(List<int> kids, int at, int count)
        {
            long m = 0;
            for (int i = 0; i < count; i++) m = Math.Max(m, Bytes(kids[at + i]));
            return m;
        }

        // The worst aspect ratio a row would have if it held these bytes.
        private static double Worst(long sum, long min, long max, float side, double scale)
        {
            if (sum <= 0 || side <= 0) return double.MaxValue;
            double s = sum * scale;
            double w = s / side;              // thickness the row would take
            if (w <= 0) return double.MaxValue;
            double hMax = max * scale / w;
            double hMin = min * scale / w;
            if (hMin <= 0) return double.MaxValue;
            return Math.Max(hMax / w, w / hMin);
        }

        private long Bytes(int node)
        {
            try { return tree.Bytes[node]; }
            catch (Exception) { return 0; }
        }

        private List<int> ChildrenBySize(int parent)
        {
            List<int> kids = new List<int>();
            try
            {
                for (int c = tree.FirstChild[parent]; c >= 0; c = tree.NextSibling[c])
                {
                    if (tree.Name[c] == null) continue;
                    if (tree.Bytes[c] <= 0) continue;
                    kids.Add(c);
                    if (kids.Count > 4000) break;      // one folder, sanely capped
                }
            }
            catch (Exception) { }
            kids.Sort(delegate(int a, int b) { return Bytes(b).CompareTo(Bytes(a)); });
            return kids;
        }

        // ---- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Input))
                g.FillRectangle(b, ClientRectangle);

            if (tree == null || rootNode < 0)
            {
                TextRenderer.DrawText(g, "Scan a drive, then the map fills in.",
                    crumbFont, ClientRectangle, Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            if (dirty) Layout_();
            if (cells.Count == 0)
            {
                TextRenderer.DrawText(g, "Nothing big enough to draw in here.",
                    crumbFont, ClientRectangle, Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            foreach (MapCell c in cells)
            {
                Rectangle r = Rectangle.Round(c.Bounds);
                if (r.Width < 1 || r.Height < 1) continue;

                Color face = Shade(Palette[c.Group % Palette.Length], c.Depth, c.IsDir);
                using (SolidBrush b = new SolidBrush(face)) g.FillRectangle(b, r);

                if (r.Width > 2 && r.Height > 2)
                    using (Pen p = new Pen(Color.FromArgb(c.Depth == 0 ? 150 : 70, 0, 0, 0)))
                        g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);

                Label(g, c, r);
            }

            if (chosen != null) Outline(g, chosen, Theme.Fg, 2);
            if (hot != null && hot != chosen) Outline(g, hot, Theme.Accent, 1);
        }

        private void Outline(Graphics g, MapCell c, Color colour, int width)
        {
            Rectangle r = Rectangle.Round(c.Bounds);
            if (r.Width < 2 || r.Height < 2) return;
            using (Pen p = new Pen(colour, width))
                g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        // Deeper is darker, so nesting reads as depth, and a file sits a shade
        // brighter than the folder holding it.
        private static Color Shade(Color c, int depth, bool isDir)
        {
            double k = 1.0 - depth * 0.16;
            if (!isDir) k += 0.10;
            if (k < 0.30) k = 0.30;
            if (k > 1.15) k = 1.15;
            return Color.FromArgb(
                Clamp((int)(c.R * k)), Clamp((int)(c.G * k)), Clamp((int)(c.B * k)));
        }

        private static int Clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        private void Label(Graphics g, MapCell c, Rectangle r)
        {
            if (r.Width < LabelMinW || r.Height < LabelMinH) return;

            string name;
            try { name = tree.Name[c.Node] ?? ""; }
            catch (Exception) { return; }
            if (name.Length == 0) return;

            Rectangle text = new Rectangle(r.X + 3, r.Y + 1, r.Width - 6, (int)LabelMinH);
            // Black under the caption, not white over it: half these blocks are
            // light and half are dark, and only the shadow works on both.
            TextRenderer.DrawText(g, name, labelFont,
                new Rectangle(text.X + 1, text.Y + 1, text.Width, text.Height),
                Color.FromArgb(120, 0, 0, 0),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, name, labelFont, text, Color.FromArgb(240, 255, 255, 255),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // The size only goes in when it will not crowd the name out.
            if (r.Height >= LabelMinH * 2 + 4 && r.Width >= 80)
            {
                Rectangle sz = new Rectangle(r.X + 3, r.Y + (int)LabelMinH, r.Width - 6, (int)LabelMinH);
                TextRenderer.DrawText(g, CleanupScanner.Nice(Bytes(c.Node)), labelFont, sz,
                    Color.FromArgb(200, 235, 245, 255),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        // ---- pointer

        // Last cell wins: children are added after their parent, so the
        // deepest rectangle under the cursor is the one the user means.
        private MapCell At(Point p)
        {
            MapCell found = null;
            foreach (MapCell c in cells)
                if (c.Bounds.Contains(p)) found = c;
            return found;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            MapCell was = hot;
            hot = At(e.Location);
            if (was != hot) Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hot != null) { hot = null; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            MapCell c = At(e.Location);
            chosen = c;
            Invalidate();
            if (c != null && CellChosen != null) CellChosen(this, new MapCellEventArgs(c));
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            MapCell c = At(e.Location);
            if (c == null) return;
            if (CellActivated != null) CellActivated(this, new MapCellEventArgs(c));
        }

        protected override bool IsInputKey(Keys key)
        {
            if (key == Keys.Back || key == Keys.Enter) return true;
            return base.IsInputKey(key);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Back) { Up(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && chosen != null && chosen.IsDir)
            { Down(chosen.Node); e.Handled = true; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { labelFont.Dispose(); } catch (Exception) { }
                try { crumbFont.Dispose(); } catch (Exception) { }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class MapCellEventArgs : EventArgs
    {
        public readonly MapCell Cell;
        public MapCellEventArgs(MapCell c) { Cell = c; }
    }
}
