// IDLE MASTER - the WizTree tree view: the drive as a folder tree with columns.
//
// The cleanup window's own tree is organised by VERDICT - temp files, caches,
// crash dumps, the things the scan decided are junk. That is the right shape
// for "what can I safely delete", and it is deliberately not the shape for
// "where did 400 GB go", because a curated list only shows what the scan
// thought to look for.
//
// This is the other shape: the whole drive, biggest first, every folder ranked
// against its parent, with the counts beside it - so you walk down the fat
// branch until you reach the thing that is actually costing you, whether or not
// the scan had a category for it.
//
// It reads the DiskTree the scan already built. No second walk, no file
// handles, nothing deleted - the right-click verdicts are the cleanup window's
// own, reached through SelectedTag() exactly like the main tree.
//
// Columns are what the scan actually collected. WizTree also shows Allocated,
// Modified and Attributes; this scan stores none of those - Bytes is logical
// size, and neither the MFT read nor the walker keeps timestamps - so they are
// not here rather than being guessed. Files and Folders are counted here in one
// post-order pass, because DiskTree keeps only the combined Items total.
//
// Same compiler rules as the rest: C# 5, in-box .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace IdleMaster
{
    internal sealed class WizTreeView : Control
    {
        private const int HeadHeight = 22;
        private const int RowHeight = 20;
        private const int MaxKids = 400;        // rows added per expanded level

        // Column widths, right-aligned, measured in from the right edge. The
        // folder name takes whatever is left.
        private const int WPct = 78;
        private const int WSize = 92;
        private const int WItems = 78;
        private const int WFiles = 70;
        private const int WFolders = 70;

        private readonly CleanTree tree;
        private readonly Control head;
        private readonly Font mono, small;

        private DiskTree data;
        private int rootNode = -1;
        private int[] files, folders;       // per node, recursive; built once
        private DiskTree countedFor;

        private int sortCol = 1;            // 0 name, 1 size (WizTree's default)
        private bool sortDown = true;

        public event EventHandler SelectionChanged;

        public WizTreeView()
        {
            SetStyle(ControlStyles.ContainerControl, true);
            BackColor = Theme.Bg;
            mono = Theme.Mono();
            small = Theme.Small();

            head = new Control();
            head.Height = HeadHeight;
            head.Dock = DockStyle.Top;
            head.BackColor = Theme.Panel;
            head.Paint += PaintHead;
            head.MouseDown += HeadClick;
            head.Cursor = Cursors.Hand;
            Controls.Add(head);

            tree = new CleanTree();
            tree.Dock = DockStyle.Fill;
            tree.CheckBoxes = false;
            tree.ShowLines = false;
            tree.ShowPlusMinus = true;
            tree.ShowRootLines = false;
            tree.FullRowSelect = true;
            tree.HideSelection = false;
            tree.BorderStyle = BorderStyle.FixedSingle;
            tree.BackColor = Theme.Input;
            tree.ForeColor = Theme.Fg;
            tree.ItemHeight = RowHeight;
            tree.Indent = 18;
            tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            tree.DrawNode += DrawNode;
            tree.BeforeExpand += BeforeExpand;
            tree.AfterSelect += delegate
            {
                tree.Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            };
            tree.NodeMouseClick += delegate(object s, TreeNodeMouseClickEventArgs a)
            { if (a.Button == MouseButtons.Right) tree.SelectedNode = a.Node; };
            tree.Resize += delegate { head.Invalidate(); };
            Controls.Add(tree);
            tree.BringToFront();
        }

        public TreeView Inner { get { return tree; } }

        public FsRef Selected
        {
            get { return tree.SelectedNode == null ? null : tree.SelectedNode.Tag as FsRef; }
        }

        public new ContextMenuStrip ContextMenuStrip
        {
            get { return tree.ContextMenuStrip; }
            set { tree.ContextMenuStrip = value; }
        }

        // ---- filling

        public void Show(DiskTree t, int node)
        {
            data = t;
            rootNode = node;
            Count(t);
            Rebuild();
        }

        public bool HasData { get { return data != null && rootNode >= 0; } }

        private void Rebuild()
        {
            tree.BeginUpdate();
            try
            {
                tree.Nodes.Clear();
                if (data == null || rootNode < 0) return;
                TreeNode r = Node(rootNode);
                r.Text = data.Root;
                // Node() hangs a lazy-load stub under anything with children.
                // The root is filled in immediately, so its stub has to go -
                // left in place it shows up as a phantom first row.
                r.Nodes.Clear();
                tree.Nodes.Add(r);
                Populate(r, rootNode);
                r.Expand();
            }
            finally { tree.EndUpdate(); }
        }

        private TreeNode Node(int n)
        {
            TreeNode t = new TreeNode(data.Name[n] ?? "");
            t.Tag = new FsRef(data, n);
            if (data.IsDir(n) && !data.IsReparse(n) && data.FirstChild[n] >= 0)
                t.Nodes.Add(new TreeNode("..."));      // the lazy-load stub
            return t;
        }

        private static bool IsStub(TreeNode n)
        {
            return n.Nodes.Count == 1 && n.Nodes[0].Tag == null;
        }

        private void BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (!IsStub(e.Node)) return;
            FsRef f = e.Node.Tag as FsRef;
            if (f == null) return;
            tree.BeginUpdate();
            try
            {
                e.Node.Nodes.Clear();
                Populate(e.Node, f.Node);
            }
            finally { tree.EndUpdate(); }
        }

        private void Populate(TreeNode into, int parent)
        {
            List<int> kids = new List<int>();
            try
            {
                for (int c = data.FirstChild[parent]; c >= 0; c = data.NextSibling[c])
                    if (data.Name[c] != null) kids.Add(c);
            }
            catch (Exception) { }

            kids.Sort(Compare);
            int n = Math.Min(kids.Count, MaxKids);
            for (int i = 0; i < n; i++) into.Nodes.Add(Node(kids[i]));
            if (kids.Count > n)
            {
                TreeNode more = new TreeNode("... " + (kids.Count - n)
                    + " more, smaller than these");
                into.Nodes.Add(more);       // Tag stays null: not selectable as a path
            }
        }

        private int Compare(int a, int b)
        {
            int r;
            if (sortCol == 0)
                r = string.Compare(data.Name[a], data.Name[b], StringComparison.OrdinalIgnoreCase);
            else if (sortCol == 3) r = data.Items[a].CompareTo(data.Items[b]);
            else if (sortCol == 4) r = Files(a).CompareTo(Files(b));
            else if (sortCol == 5) r = Folders(a).CompareTo(Folders(b));
            else r = data.Bytes[a].CompareTo(data.Bytes[b]);
            if (r == 0) r = data.Bytes[a].CompareTo(data.Bytes[b]);
            if (r == 0) r = string.Compare(data.Name[a], data.Name[b],
                StringComparison.OrdinalIgnoreCase);
            return sortDown ? -r : r;
        }

        // ---- the two counts DiskTree does not keep
        //
        // Items is files+folders combined. Splitting it needs one post-order
        // pass, done iteratively: a drive can nest deeper than the stack likes,
        // and this runs on whatever tree the scan produced.
        private void Count(DiskTree t)
        {
            if (t == null || t == countedFor) return;
            countedFor = t;
            int n = t.Name.Length;
            files = new int[n];
            folders = new int[n];

            List<int> order = new List<int>(n);
            Stack<int> stack = new Stack<int>();
            if (t.RootNode >= 0) stack.Push(t.RootNode);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                order.Add(cur);
                for (int c = t.FirstChild[cur]; c >= 0; c = t.NextSibling[c])
                    if (t.Name[c] != null) stack.Push(c);
            }
            // Reverse pre-order is a valid post-order: every child is seen
            // before its parent, which is all the roll-up needs.
            for (int i = order.Count - 1; i >= 0; i--)
            {
                int cur = order[i];
                int p = t.Parent[cur];
                if (p < 0) continue;
                if (t.IsDir(cur)) folders[p] += folders[cur] + 1;
                else files[p] += 1;
                files[p] += t.IsDir(cur) ? files[cur] : 0;
            }
        }

        private int Files(int n) { return files != null && n < files.Length ? files[n] : 0; }
        private int Folders(int n) { return folders != null && n < folders.Length ? folders[n] : 0; }

        // ---- columns

        private int[] Edges()
        {
            int right = tree.ClientSize.Width - 2;
            int[] e = new int[6];
            e[5] = right;                       // Folders right edge
            e[4] = e[5] - WFolders;
            e[3] = e[4] - WFiles;
            e[2] = e[3] - WItems;
            e[1] = e[2] - WSize;
            e[0] = e[1] - WPct;                 // % of parent right edge is e[1]
            return e;
        }

        private void PaintHead(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Panel))
                g.FillRectangle(b, head.ClientRectangle);

            int[] x = Edges();
            Rectangle r = new Rectangle(6, 0, x[0] - 10, HeadHeight);
            Head(g, "Folder", r, TextFormatFlags.Left, 0);
            Head(g, "% of parent", Cell(x[0], x[1]), TextFormatFlags.Right, -1);
            Head(g, "Size", Cell(x[1], x[2]), TextFormatFlags.Right, 1);
            Head(g, "Items", Cell(x[2], x[3]), TextFormatFlags.Right, 3);
            Head(g, "Files", Cell(x[3], x[4]), TextFormatFlags.Right, 4);
            Head(g, "Folders", Cell(x[4], x[5]), TextFormatFlags.Right, 5);

            using (Pen p = new Pen(Theme.Track))
                g.DrawLine(p, 0, HeadHeight - 1, head.ClientSize.Width, HeadHeight - 1);
        }

        private static Rectangle Cell(int from, int to)
        {
            return new Rectangle(from + 4, 0, to - from - 8, HeadHeight);
        }

        private void Head(Graphics g, string text, Rectangle r, TextFormatFlags align, int col)
        {
            bool on = col == sortCol;
            string s = on ? text + (sortDown ? "  ▾" : "  ▴") : text;
            TextRenderer.DrawText(g, s, small, r, on ? Theme.Accent : Theme.Dim,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void HeadClick(object sender, MouseEventArgs e)
        {
            int[] x = Edges();
            int col;
            if (e.X < x[0]) col = 0;
            else if (e.X < x[1]) return;            // % of parent is just size again
            else if (e.X < x[2]) col = 1;
            else if (e.X < x[3]) col = 3;
            else if (e.X < x[4]) col = 4;
            else col = 5;

            if (col == sortCol) sortDown = !sortDown;
            else { sortCol = col; sortDown = col != 0; }
            head.Invalidate();
            Rebuild();
        }

        // ---- rows

        private void DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            e.DrawDefault = false;
            Graphics g = e.Graphics;
            Rectangle row = new Rectangle(0, e.Bounds.Y, tree.ClientSize.Width, e.Bounds.Height);

            bool selected = e.Node == tree.SelectedNode;
            using (SolidBrush b = new SolidBrush(selected ? Theme.Neutral : Theme.Input))
                g.FillRectangle(b, row);

            FsRef f = e.Node.Tag as FsRef;
            int[] x = Edges();

            if (f == null)
            {
                // The "... n more" tail. It is a note, not a folder.
                TextRenderer.DrawText(g, e.Node.Text, small,
                    new Rectangle(e.Bounds.X, row.Y, x[0] - e.Bounds.X, row.Height),
                    Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }

            int n = f.Node;
            bool dir = data.IsDir(n);

            // The expander, drawn rather than left to the system. The row is
            // filled edge to edge for the columns, which paints over whatever
            // the themed TreeView put in the glyph cell - so it goes back by
            // hand, and brighter than the theme drew it. ShowPlusMinus stays
            // on, so the cell is still hit-tested and a click still toggles.
            if (e.Node.Nodes.Count > 0)
                TextRenderer.DrawText(g, e.Node.IsExpanded ? "▾" : "▸",
                    tree.Font, new Rectangle(e.Bounds.X - 17, row.Y, 15, row.Height),
                    e.Node.IsExpanded ? Theme.Accent : Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.NoPadding);

            TextRenderer.DrawText(g, e.Node.Text, tree.Font,
                new Rectangle(e.Bounds.X, row.Y, Math.Max(10, x[0] - e.Bounds.X - 6), row.Height),
                dir ? Theme.Fg : Theme.ListFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            Bar(g, Cell(x[0], x[1]), row, Share(n));
            Num(g, CleanupScanner.Nice(data.Bytes[n]), Cell(x[1], x[2]), row, Theme.Fg);
            Num(g, dir ? Thousands(data.Items[n]) : "", Cell(x[2], x[3]), row, Theme.Dim);
            Num(g, dir ? Thousands(Files(n)) : "", Cell(x[3], x[4]), row, Theme.Dim);
            Num(g, dir ? Thousands(Folders(n)) : "", Cell(x[4], x[5]), row, Theme.Dim);

            if (selected)
                using (Pen p = new Pen(Theme.Accent))
                    g.DrawRectangle(p, row.X, row.Y, row.Width - 1, row.Height - 1);
        }

        private void Num(Graphics g, string s, Rectangle cell, Rectangle row, Color c)
        {
            if (s.Length == 0) return;
            TextRenderer.DrawText(g, s, mono,
                new Rectangle(cell.X, row.Y, cell.Width, row.Height), c,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        // The share bar. WizTree paints this red; here it is the same blue the
        // RAM gauge uses, because in this app a filling bar already means "this
        // much of the whole" and red already means "destroying things".
        private void Bar(Graphics g, Rectangle cell, Rectangle row, double share)
        {
            if (share < 0) return;
            if (share > 1) share = 1;
            Rectangle bar = new Rectangle(cell.X, row.Y + 3, cell.Width, row.Height - 6);
            if (bar.Width < 6) return;

            using (SolidBrush b = new SolidBrush(Theme.Track)) g.FillRectangle(b, bar);
            int w = (int)(bar.Width * share);
            if (w > 0)
                using (SolidBrush b = new SolidBrush(share > 0.5 ? Theme.GaugeOk : Theme.Good))
                    g.FillRectangle(b, new Rectangle(bar.X, bar.Y, w, bar.Height));

            TextRenderer.DrawText(g, (share * 100).ToString(
                    share >= 0.995 ? "0" : "0.0", CultureInfo.InvariantCulture) + " %",
                small, bar, Theme.Fg,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // What fraction of the folder above it this one is. The drive root has
        // no parent inside the tree, so it measures against the drive.
        private double Share(int n)
        {
            try
            {
                if (n == data.RootNode) return 1;
                long parent = data.Bytes[data.Parent[n]];
                return parent > 0 ? (double)data.Bytes[n] / parent : 0;
            }
            catch (Exception) { return 0; }
        }

        private static string Thousands(int v)
        {
            return v.ToString("#,0", CultureInfo.InvariantCulture);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { mono.Dispose(); } catch (Exception) { }
                try { small.Dispose(); } catch (Exception) { }
            }
            base.Dispose(disposing);
        }
    }
}
