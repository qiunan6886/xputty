using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace XPutty
{
    /// <summary>Preset split layouts offered by the tab bar's layout menu.</summary>
    public enum LayoutPreset
    {
        Single,
        TwoColumns,     // left | right
        TwoRows,        // top / bottom
        ThreeColumns,   // 1x3
        ThreeRows,      // 3x1
        Quad            // 2x2
    }

    /// <summary>
    /// One node of the split tree.  Leaves own a PaneView; inner nodes own
    /// two or more children laid out side by side (Vertical = true means the
    /// children are arranged in columns, i.e. the divider is vertical) or
    /// stacked (Vertical = false means rows).
    /// </summary>
    public class SplitNode
    {
        public bool IsLeaf;
        public PaneView Pane;
        public bool Vertical;
        public float Ratio = 0.5f;          // used for two-child nodes
        public List<SplitNode> Children = new List<SplitNode>();
        public SplitNode Parent;

        // Authoritative child weights for layouts with more than two children
        // (equal shares until the user drags a divider).
        internal List<float> Weights;

        public static SplitNode MakeLeaf(PaneView pane)
        {
            var n = new SplitNode();
            n.IsLeaf = true;
            n.Pane = pane;
            return n;
        }

        public static SplitNode MakeGroup(bool vertical, SplitNode first, SplitNode second)
        {
            var n = new SplitNode();
            n.IsLeaf = false;
            n.Vertical = vertical;
            n.Ratio = 0.5f;
            n.Children.Add(first);
            n.Children.Add(second);
            first.Parent = n;
            second.Parent = n;
            n.Weights = new List<float> { 0.5f, 0.5f };
            return n;
        }

        internal List<float> CurrentWeights()
        {
            int n = Children.Count;
            if (n <= 0) return new List<float>();
            if (n == 2)
            {
                float r = Math.Max(0.05f, Math.Min(0.95f, Ratio));
                Weights = new List<float> { r, 1f - r };
                return Weights;
            }
            if (Weights == null || Weights.Count != n)
            {
                Weights = new List<float>(n);
                for (int i = 0; i < n; i++) Weights.Add(1f / n);
            }
            return Weights;
        }

        internal void NormalizeWeights()
        {
            var w = CurrentWeights();
            float sum = 0;
            for (int i = 0; i < w.Count; i++) sum += w[i];
            if (sum <= 0.0001f) return;
            for (int i = 0; i < w.Count; i++) w[i] = w[i] / sum;
            if (Children.Count == 2) Ratio = w[0];
        }
    }

    /// <summary>Absolute placement of one pane inside the split host.</summary>
    public class PanePlacement
    {
        public PaneView Pane;
        public Rectangle Bounds;
    }

    /// <summary>One draggable divider.</summary>
    public class SplitDivider
    {
        public Rectangle Bounds;        // 1px visual line (the layout gap)
        public Rectangle HitBounds;     // inflated hot zone
        public bool Vertical;           // true = vertical line, drags horizontally
        public SplitNode Node;
        public int Index;               // divider sits after child [Index]

        // Geometry cached at layout time so dragging is O(1).
        public Rectangle NodeBounds;
        public float Avail;             // usable pixels along the split axis
        public float CumulativeStart;   // sum of weights before child [Index]
        public float PairTotal;         // weight[Index] + weight[Index + 1]
    }

    /// <summary>
    /// The split tree of one tab.  Supports arbitrary n-ary splits, removal,
    /// active pane tracking, recursive layout and a lightweight JSON dump for
    /// future layout persistence.
    /// </summary>
    public class SplitTree
    {
        public SplitNode Root;
        public PaneView ActivePane;

        public SplitTree() { }

        public SplitTree(PaneView firstPane)
        {
            if (firstPane != null) Root = SplitNode.MakeLeaf(firstPane);
            ActivePane = firstPane;
        }

        public bool IsEmpty { get { return Root == null; } }

        public List<PaneView> EnumeratePanes()
        {
            var list = new List<PaneView>();
            Collect(Root, list);
            return list;
        }

        private static void Collect(SplitNode node, List<PaneView> list)
        {
            if (node == null) return;
            if (node.IsLeaf)
            {
                if (node.Pane != null && !list.Contains(node.Pane)) list.Add(node.Pane);
                return;
            }
            for (int i = 0; i < node.Children.Count; i++) Collect(node.Children[i], list);
        }

        public SplitNode FindLeaf(PaneView pane)
        {
            return FindLeaf(Root, pane);
        }

        private static SplitNode FindLeaf(SplitNode node, PaneView pane)
        {
            if (node == null) return null;
            if (node.IsLeaf) return node.Pane == pane ? node : null;
            for (int i = 0; i < node.Children.Count; i++)
            {
                var found = FindLeaf(node.Children[i], pane);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Split the leaf that owns <paramref name="target"/> and return the new
        /// pane's node.  Same-orientation splits grow the parent into an n-ary
        /// group; otherwise the leaf becomes a two-child group.
        /// </summary>
        public SplitNode Split(PaneView target, bool vertical, PaneView newPane)
        {
            if (newPane == null) return null;
            SplitNode leaf = FindLeaf(target);
            if (leaf == null)
            {
                if (Root == null)
                {
                    Root = SplitNode.MakeLeaf(newPane);
                    ActivePane = newPane;
                    return Root;
                }
                return null;
            }

            SplitNode parent = leaf.Parent;
            if (parent != null && parent.Vertical == vertical &&
                !parent.IsLeaf && parent.Children.Count >= 2)
            {
                int index = parent.Children.IndexOf(leaf);
                var newNode = SplitNode.MakeLeaf(newPane);
                parent.Children.Insert(index + 1, newNode);
                newNode.Parent = parent;
                parent.Weights = null;   // recompute equal shares
                parent.CurrentWeights();
                ActivePane = newPane;
                return newNode;
            }

            var group = new SplitNode();
            group.IsLeaf = false;
            group.Vertical = vertical;
            group.Ratio = 0.5f;
            group.Weights = new List<float> { 0.5f, 0.5f };
            var added = SplitNode.MakeLeaf(newPane);

            if (parent == null)
            {
                group.Children.Add(Root);
                Root.Parent = group;
                group.Children.Add(added);
                added.Parent = group;
                Root = group;
            }
            else
            {
                int index = parent.Children.IndexOf(leaf);
                parent.Children[index] = group;
                group.Parent = parent;
                group.Children.Add(leaf);
                leaf.Parent = group;
                group.Children.Add(added);
                added.Parent = group;
            }
            ActivePane = newPane;
            return added;
        }

        /// <summary>Remove a pane; collapses single-child groups.  True when the tree is now empty.</summary>
        public bool Remove(PaneView pane)
        {
            SplitNode leaf = FindLeaf(pane);
            if (leaf == null) return Root == null;

            SplitNode parent = leaf.Parent;
            if (parent == null)
            {
                Root = null;
                ActivePane = null;
                return true;
            }

            parent.Children.Remove(leaf);
            leaf.Parent = null;
            parent.Weights = null;

            if (parent.Children.Count == 1)
            {
                SplitNode only = parent.Children[0];
                SplitNode grand = parent.Parent;
                only.Parent = grand;
                if (grand == null) Root = only;
                else
                {
                    int gi = grand.Children.IndexOf(parent);
                    grand.Children[gi] = only;
                    grand.Weights = null;
                }
                parent.Children.Clear();
            }
            else if (parent.Children.Count == 0)
            {
                // Should not happen for inner nodes, but be safe.
                if (parent.Parent == null) Root = null;
                else parent.Parent.Children.Remove(parent);
            }

            if (ActivePane == pane)
            {
                var remaining = EnumeratePanes();
                ActivePane = remaining.Count > 0 ? remaining[0] : null;
            }
            if (Root != null) Root.NormalizeWeights();
            return Root == null;
        }

        /// <summary>Collapse the tree to a single pane (single-pane layout preset).</summary>
        public void CollapseTo(PaneView pane)
        {
            if (pane == null) return;
            Root = SplitNode.MakeLeaf(pane);
            ActivePane = pane;
        }

        /// <summary>
        /// Recursively compute the rectangle of every pane and the divider
        /// geometry.  <paramref name="gap"/> is the visual gap between panes
        /// (1px in the design); dividers are drawn inside that gap.
        /// </summary>
        public void Layout(Rectangle bounds, int gap, List<PanePlacement> panes,
                           List<SplitDivider> dividers, int dividerHitInflate)
        {
            if (panes == null) throw new ArgumentNullException("panes");
            panes.Clear();
            if (dividers != null) dividers.Clear();
            if (Root == null) return;
            LayoutNode(Root, bounds, gap, panes, dividers, dividerHitInflate);
        }

        private static void LayoutNode(SplitNode node, Rectangle rect, int gap,
                                       List<PanePlacement> panes, List<SplitDivider> dividers,
                                       int hitInflate)
        {
            if (node == null) return;
            if (node.IsLeaf)
            {
                if (node.Pane != null)
                {
                    var p = new PanePlacement();
                    p.Pane = node.Pane;
                    p.Bounds = rect;
                    panes.Add(p);
                }
                return;
            }

            int n = node.Children.Count;
            if (n == 0) return;
            if (n == 1)
            {
                LayoutNode(node.Children[0], rect, gap, panes, dividers, hitInflate);
                return;
            }

            var weights = node.CurrentWeights();
            node.NormalizeWeights();

            int totalGap = gap * (n - 1);
            int extent = node.Vertical ? rect.Width : rect.Height;
            int avail = Math.Max(0, extent - totalGap);
            float cumulative = 0f;
            int cursor = node.Vertical ? rect.X : rect.Y;
            int used = 0;

            for (int i = 0; i < n; i++)
            {
                int size;
                if (i == n - 1) size = Math.Max(0, avail - used);
                else
                {
                    size = (int)Math.Round(avail * weights[i]);
                    size = Math.Max(0, size);
                }

                Rectangle childRect = node.Vertical
                    ? new Rectangle(cursor, rect.Y, size, rect.Height)
                    : new Rectangle(rect.X, cursor, rect.Width, size);

                LayoutNode(node.Children[i], childRect, gap, panes, dividers, hitInflate);

                if (i < n - 1 && dividers != null)
                {
                    var d = new SplitDivider();
                    d.Vertical = node.Vertical;
                    d.Node = node;
                    d.Index = i;
                    d.NodeBounds = rect;
                    d.Avail = avail;
                    d.CumulativeStart = cumulative;
                    d.PairTotal = weights[i] + weights[i + 1];

                    Rectangle bar;
                    if (node.Vertical)
                        bar = new Rectangle(cursor + size, rect.Y, gap, rect.Height);
                    else
                        bar = new Rectangle(rect.X, cursor + size, rect.Width, gap);
                    d.Bounds = bar;
                    Rectangle hit = bar;
                    if (hitInflate > 0)
                    {
                        if (node.Vertical) hit.Inflate(hitInflate, 0);
                        else hit.Inflate(0, hitInflate);
                    }
                    d.HitBounds = hit;
                    dividers.Add(d);
                }

                cumulative += weights[i];
                if (node.Vertical) cursor += size + gap;
                else cursor += size + gap;
                used += size;
            }
        }

        /// <summary>Reset one divider back to 1:1.</summary>
        public void ResetDivider(SplitDivider d)
        {
            if (d == null || d.Node == null) return;
            int n = d.Node.Children.Count;
            if (n == 2)
            {
                d.Node.Ratio = 0.5f;
                d.Node.Weights = new List<float> { 0.5f, 0.5f };
            }
            else if (d.Node.Weights != null && d.Index + 1 < d.Node.Weights.Count)
            {
                float total = d.Node.Weights[d.Index] + d.Node.Weights[d.Index + 1];
                d.Node.Weights[d.Index] = total * 0.5f;
                d.Node.Weights[d.Index + 1] = total * 0.5f;
            }
        }

        /// <summary>Move a divider to an absolute pixel offset along the node axis.</summary>
        public void DragDivider(SplitDivider d, int offsetInNode, int minSize)
        {
            if (d == null || d.Node == null || d.Avail <= 1f) return;
            var node = d.Node;
            int n = node.Children.Count;
            float share = (float)offsetInNode / d.Avail;
            share = Math.Max(0.02f, Math.Min(0.98f, share));
            float cumStart = d.CumulativeStart;
            float pairTotal = d.PairTotal;
            if (pairTotal <= 0.0001f) pairTotal = 0.5f;

            // Minimum share so that neither pane collapses below minSize.
            float minShare = d.Avail > 0 ? (float)minSize / d.Avail : 0.05f;
            minShare = Math.Max(0.02f, Math.Min(0.45f, minShare));

            float newFirst = share - cumStart;
            newFirst = Math.Max(minShare, Math.Min(pairTotal - minShare, newFirst));
            if (newFirst < 0.01f) newFirst = 0.01f;

            if (n == 2)
            {
                node.Ratio = Math.Max(0.1f, Math.Min(0.9f, newFirst));
                node.Weights = new List<float> { node.Ratio, 1f - node.Ratio };
            }
            else
            {
                if (node.Weights == null || node.Weights.Count != n)
                    node.CurrentWeights();
                node.Weights[d.Index] = newFirst;
                node.Weights[d.Index + 1] = pairTotal - newFirst;
            }
        }

        // ------------------------------------------------------------------
        // Layout presets
        // ------------------------------------------------------------------

        /// <summary>Rebuild the tree for a preset layout using the supplied
        /// panes; <paramref name="factory"/> is only called when more panes are
        /// needed than are available.</summary>
        public static SplitTree BuildPreset(LayoutPreset preset, List<PaneView> available,
                                            Func<int, PaneView> factory)
        {
            var tree = new SplitTree();
            if (available == null || available.Count == 0)
            {
                tree.Root = null;
                return tree;
            }

            int count;
            bool vertical = true;     // columns
            switch (preset)
            {
                case LayoutPreset.TwoColumns: count = 2; vertical = true; break;
                case LayoutPreset.TwoRows: count = 2; vertical = false; break;
                case LayoutPreset.ThreeColumns: count = 3; vertical = true; break;
                case LayoutPreset.ThreeRows: count = 3; vertical = false; break;
                case LayoutPreset.Quad: count = 4; vertical = true; break;
                default: count = 1; break;
            }

            if (count == 1)
            {
                tree.Root = SplitNode.MakeLeaf(available[0]);
                tree.ActivePane = available[0];
                return tree;
            }

            var panes = new List<PaneView>();
            for (int i = 0; i < count; i++)
            {
                PaneView p = null;
                if (i < available.Count) p = available[i];
                else if (factory != null) p = factory(i);
                if (p == null) break;
                panes.Add(p);
            }
            if (panes.Count == 1)
            {
                tree.Root = SplitNode.MakeLeaf(panes[0]);
                tree.ActivePane = panes[0];
                return tree;
            }

            if (preset == LayoutPreset.Quad && panes.Count >= 4)
            {
                var col1 = SplitNode.MakeGroup(false,
                    SplitNode.MakeLeaf(panes[0]), SplitNode.MakeLeaf(panes[1]));
                var col2 = SplitNode.MakeGroup(false,
                    SplitNode.MakeLeaf(panes[2]), SplitNode.MakeLeaf(panes[3]));
                var root = SplitNode.MakeGroup(true, col1, col2);
                col1.Parent = root;
                col2.Parent = root;
                tree.Root = root;
            }
            else
            {
                var root = new SplitNode();
                root.IsLeaf = false;
                root.Vertical = vertical;
                for (int i = 0; i < panes.Count; i++)
                {
                    var leaf = SplitNode.MakeLeaf(panes[i]);
                    leaf.Parent = root;
                    root.Children.Add(leaf);
                }
                root.Weights = null;
                root.CurrentWeights();
                tree.Root = root;
            }

            tree.ActivePane = panes[0];
            return tree;
        }

        /// <summary>Depth-first count of leaves.</summary>
        public int PaneCount { get { return EnumeratePanes().Count; } }

        // ------------------------------------------------------------------
        // Serialisation (in-memory clone / debug JSON; layout persistence is v2)
        // ------------------------------------------------------------------

        public SplitTree CloneShallow()
        {
            var t = new SplitTree();
            t.Root = CloneNode(Root);
            t.ActivePane = ActivePane;
            return t;
        }

        private static SplitNode CloneNode(SplitNode node)
        {
            if (node == null) return null;
            var copy = new SplitNode();
            copy.IsLeaf = node.IsLeaf;
            copy.Pane = node.Pane;
            copy.Vertical = node.Vertical;
            copy.Ratio = node.Ratio;
            if (node.Weights != null) copy.Weights = new List<float>(node.Weights);
            for (int i = 0; i < node.Children.Count; i++)
            {
                var child = CloneNode(node.Children[i]);
                child.Parent = copy;
                copy.Children.Add(child);
            }
            return copy;
        }

        /// <summary>Small JSON description of the layout (session ids only).</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            WriteJson(Root, sb);
            return sb.ToString();
        }

        private static void WriteJson(SplitNode node, StringBuilder sb)
        {
            if (node == null) { sb.Append("null"); return; }
            if (node.IsLeaf)
            {
                string sid = node.Pane != null && node.Pane.Session != null
                    ? node.Pane.Session.Id : "";
                sb.Append("{\"session\":\"").Append(sid).Append("\"}");
                return;
            }
            sb.Append("{\"vertical\":").Append(node.Vertical ? "true" : "false");
            sb.Append(",\"ratio\":").Append(node.Ratio.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"children\":[");
            for (int i = 0; i < node.Children.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteJson(node.Children[i], sb);
            }
            sb.Append("]}");
        }
    }
}
