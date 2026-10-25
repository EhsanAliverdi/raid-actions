using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace RaidActions.Game.Memory;

/// <summary>A rectangle in window client pixels (origin top left), as floats.</summary>
public readonly record struct ClientRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public (float X, float Y) Center => (X + Width / 2, Y + Height / 2);
    public override string ToString() => $"{Width:0}x{Height:0} at ({X:0},{Y:0})";
}

/// <param name="ManagedClass">The script's managed class (UnityEngine.UI.Button, Client.View...), null for native components.</param>
public sealed record UiComponent(int TypeIndex, ulong Native, ulong Managed, string? ManagedClass);

/// <param name="Context">The view-model context bound to this element (a BaseView's Context): its address and class.</param>
public sealed record UiContext(ulong Address, string Class);

/// <param name="Screen">The element's rectangle in client pixels; null when it is not a RectTransform or the canvas is not
/// screen-space overlay (see <see cref="UiTree.ScreenProblem"/>).</param>
/// <param name="Interactable">Selectable.m_Interactable when the element has a Selectable (Button, Toggle, ...); null otherwise.</param>
public sealed record UiNode(int Index, int Parent, int Depth, string Name, string Path, bool ActiveSelf, bool ActiveInHierarchy,
    ClientRect? Screen, IReadOnlyList<UiComponent> Components, UiContext? Context, bool? Interactable)
{
    public bool Has(string managedClass) => Components.Any(c => c.ManagedClass == managedClass);
}

/// <param name="ScreenProblem">Why there are no screen rectangles (canvas not screen-space overlay), or null.</param>
public sealed record UiTree(string View, IReadOnlyList<UiNode> Nodes, int ClientWidth, int ClientHeight, string? ScreenProblem, float CanvasScale);

/// <summary>
/// Reads Unity UI from memory (read-only): a view's GameObject subtree with names, active flags, the managed scripts on each
/// element, the bound view-model context, Selectable.interactable, and each RectTransform's rectangle in window client pixels.
/// Screen mapping: world matrices composed up the transform hierarchy; for a Screen Space - Overlay canvas the world
/// coordinates are screen pixels (origin bottom left), so client y = height - world y. Other canvas modes are refused.
/// </summary>
public sealed class UnityUi(INativeMemory mem, NativeLayout L, IGameObjects g)
{
    private const string BaseView = "Client.MVVM.Base.View.BaseView";
    private const string Selectable = "UnityEngine.UI.Selectable";
    private const int MaxNodes = 20000, MaxComponents = 64;

    /// <summary>The subtree of a managed Unity component (e.g. a RaidView), for a client area of the given size.</summary>
    public UiTree Read(ulong managedComponent, string viewName, int clientWidth, int clientHeight)
    {
        var native = g.Ref(managedComponent, "UnityEngine.Object", "m_CachedPtr");
        if (native == 0) throw new UiReadException($"{viewName}: no native object (destroyed?).");
        var transform = TransformOfComponent(native);
        var h = U64(transform + (ulong)L.TransformHierarchy);
        var root = I32(transform + (ulong)L.TransformIndex);
        var capacity = (int)U32(h + (ulong)L.HCapacity);
        if (root < 0 || root >= capacity) throw new UiReadException($"{viewName}: transform index {root} outside the hierarchy ({capacity}).");
        var arrays = new Arrays(U64(h + (ulong)L.HLocalTransforms), U64(h + (ulong)L.HParents), U64(h + (ulong)L.HDeepChildCount), U64(h + (ulong)L.HTransforms));
        // Whole arrays, read once: the subtree is a depth-first walk along nextIndices, not a contiguous range.
        var allParents = ReadInts(arrays.Parents, capacity);
        var allNext = ReadInts(U64(h + (ulong)L.HNextIndices), capacity);
        if (U64(arrays.Transforms + (ulong)root * 8) != transform) throw new UiReadException($"{viewName}: hierarchy.transforms[{root}] is not this transform.");

        var count = I32(arrays.Deep + (ulong)root * 4);
        if (count < 1 || count > MaxNodes || root + count > capacity) throw new UiReadException($"{viewName}: implausible subtree size {count}.");

        // World matrices: the ancestors (root of the hierarchy down to the view), then the subtree in depth-first order.
        var world = new Dictionary<int, Matrix4x4>();
        var chain = new List<int>();
        for (var p = I32(arrays.Parents + (ulong)root * 4); p >= 0; p = I32(arrays.Parents + (ulong)p * 4))
        {
            if (chain.Count > 256) throw new UiReadException($"{viewName}: parent chain too long.");
            chain.Add(p);
        }
        chain.Reverse();
        foreach (var i in chain) world[i] = Local(arrays, i) * (world.TryGetValue(I32(arrays.Parents + (ulong)i * 4), out var pw) ? pw : Matrix4x4.Identity);

        // The canvas: the topmost RectTransform above (or at) the view.
        var canvas = chain.Append(root).FirstOrDefault(i => IsRect(U64(arrays.Transforms + (ulong)i * 8)), -1);
        var order = new int[count];
        order[0] = root;
        for (var k = 1; k < count; k++)
        {
            var next = allNext[order[k - 1]];
            if (next < 0 || next >= capacity) throw new UiReadException($"{viewName}: depth-first walk left the hierarchy at step {k}.");
            order[k] = next;
        }
        var nodes = new List<UiNode>(count);
        var depth = new Dictionary<int, int>();
        var paths = new Dictionary<int, string>();
        string? problem = null;
        float scale = 0;
        for (var k = 0; k < count; k++)
        {
            var i = order[k];
            var parent = allParents[i];
            world[i] = Local(arrays, i) * (world.TryGetValue(parent, out var pw) ? pw : Matrix4x4.Identity);
            if (k == 0) (problem, scale) = CanvasCheck(canvas < 0 ? null : world[canvas], clientWidth, clientHeight);
            depth[i] = k == 0 ? 0 : depth.GetValueOrDefault(parent) + 1;
            ulong t, go;
            string name;
            List<UiComponent> components;
            try
            {
                t = U64(arrays.Transforms + (ulong)i * 8);
                if (t == 0) throw new UiReadException("no transform in the hierarchy slot");
                go = U64(t + (ulong)L.ComponentGameObject);
                name = Name(go);
                components = Components(go);
            }
            catch (UiReadException e)
            {
                // Recorded, not fatal for the tree: a target on such an element refuses (no rectangle, not active).
                paths[i] = $"{paths.GetValueOrDefault(parent, "?")}/<unreadable #{i}>";
                nodes.Add(new UiNode(i, parent, depth[i], $"<unreadable #{i}: {e.Message}>", paths[i], false, false, null, [], null, null));
                continue;
            }
            paths[i] = k == 0 ? name : $"{paths.GetValueOrDefault(parent, "?")}/{name}";
            var isRect = components.Count > 0 && components[0].TypeIndex == L.RectTransformTypeIndex;
            ClientRect? screen = null;
            if (isRect && problem is null)
            {
                var r = F32s(t + (ulong)L.RectRect, 4);
                screen = ToClient(world[i], r[0], r[1], r[2], r[3], clientHeight);
            }
            UiContext? context = null;
            bool? interactable = null;
            foreach (var c in components.Where(c => c.Managed != 0))
            {
                var chainNames = g.ClassChain(c.Managed);
                if (context is null && chainNames.Contains(BaseView) && g.Ref(c.Managed, BaseView, "Context") is var ctx and not 0)
                    context = new UiContext(ctx, g.ClassName(ctx));
                if (interactable is null && chainNames.Contains(Selectable)) interactable = g.Bool(c.Managed, Selectable, "m_Interactable");
            }
            nodes.Add(new UiNode(i, parent, depth[i], name, paths[i], Byte(go + (ulong)L.GoActiveSelf) == 1, Byte(go + (ulong)L.GoActiveInHierarchy) == 1,
                screen, components, context, interactable));
        }
        return new UiTree(viewName, nodes, clientWidth, clientHeight, problem, scale);
    }

    private sealed record Arrays(ulong Locals, ulong Parents, ulong Deep, ulong Transforms);

    /// <summary>A Screen Space - Overlay canvas root sits at the client centre with a uniform scale and no rotation.</summary>
    public static (string? Problem, float Scale) CanvasCheck(Matrix4x4? canvas, int w, int h)
    {
        if (canvas is not { } m) return ("No canvas (RectTransform) above the view.", 0);
        Matrix4x4.Decompose(m, out var s, out var rot, out var pos);
        var centred = Math.Abs(pos.X - w / 2f) <= 1 && Math.Abs(pos.Y - h / 2f) <= 1;
        var uniform = s.X > 0 && Math.Abs(s.X - s.Y) < 1e-3;
        var upright = Math.Abs(rot.X) + Math.Abs(rot.Y) + Math.Abs(rot.Z) < 1e-3;
        return centred && uniform && upright
            ? (null, s.X)
            : ($"The canvas is not screen-space overlay for a {w}x{h} client (root at {pos.X:0.#},{pos.Y:0.#}, scale {s.X:0.###}/{s.Y:0.###}); no screen rectangles.", 0);
    }

    /// <summary>Corners of a local rect through the world matrix, as client pixels (y flipped: Unity's screen origin is bottom left).</summary>
    public static ClientRect ToClient(Matrix4x4 world, float x, float y, float width, float height, int clientHeight)
    {
        var corners = new[] { new Vector2(x, y), new Vector2(x + width, y), new Vector2(x, y + height), new Vector2(x + width, y + height) }
            .Select(c => Vector3.Transform(new Vector3(c, 0), world)).ToArray();
        var minX = corners.Min(c => c.X);
        var maxX = corners.Max(c => c.X);
        var minY = corners.Min(c => c.Y);
        var maxY = corners.Max(c => c.Y);
        return new ClientRect(minX, clientHeight - maxY, maxX - minX, maxY - minY);
    }

    /// <summary>Unity's TRS for one hierarchy entry, as a System.Numerics row-vector matrix (scale, then rotation, then translation).</summary>
    private Matrix4x4 Local(Arrays a, int i)
    {
        var f = F32s(a.Locals + (ulong)i * (ulong)L.HStride, 12);
        var q = new Quaternion(f[4], f[5], f[6], f[7]);
        if (Math.Abs(q.LengthSquared() - 1) > 0.01) throw new UiReadException($"transform {i}: rotation is not a unit quaternion.");
        return Matrix4x4.CreateScale(f[8], f[9], f[10]) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(f[0], f[1], f[2]);
    }

    private ulong TransformOfComponent(ulong native)
    {
        var go = U64(native + (ulong)L.ComponentGameObject);
        var first = U64(U64(go + (ulong)L.GoComponents) + 8);
        if (U64(first + (ulong)L.ComponentGameObject) != go) throw new UiReadException("component[0] does not point back to its GameObject.");
        return first;
    }

    private bool IsRect(ulong transform)
    {
        var go = U64(transform + (ulong)L.ComponentGameObject);
        return I32(U64(go + (ulong)L.GoComponents)) == L.RectTransformTypeIndex;
    }

    private List<UiComponent> Components(ulong go)
    {
        var n = (int)U64(go + (ulong)L.GoComponentCount);
        if (n < 1 || n > MaxComponents) throw new UiReadException($"GameObject 0x{go:X}: implausible component count {n}.");
        var raw = new byte[n * 16];
        Read(U64(go + (ulong)L.GoComponents), raw);
        var list = new List<UiComponent>(n);
        for (var k = 0; k < n; k++)
        {
            var type = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(k * 16));
            var native = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(k * 16 + 8));
            ulong managed = 0;
            string? cls = null;
            if (k > 0 && TryU64(native + (ulong)L.ComponentManagedSlot, out var slot) && slot != 0 && TryU64(slot, out var m) && m != 0)
            {
                try
                {
                    // Only a managed object that points back to this native component is the component's script.
                    if (g.Ref(m, "UnityEngine.Object", "m_CachedPtr") == native)
                    {
                        managed = m;
                        cls = g.ClassName(m);
                    }
                }
                catch (Exception e) when (e is UiReadException or RaidExtractor.ReadCheckException or KeyNotFoundException) { }
            }
            list.Add(new UiComponent(type, native, managed, cls));
        }
        return list;
    }

    private string Name(ulong go)
    {
        var p = U64(go + (ulong)L.GoName);
        if (p == 0) return "?";
        // Shorter reads when a long one would cross into unreadable memory (a name near the end of a page).
        foreach (var size in new[] { 128, 64, 32, 16 })
        {
            var buf = new byte[size];
            if (!mem.TryRead(p, buf)) continue;
            var end = Array.IndexOf(buf, (byte)0);
            return Encoding.UTF8.GetString(buf, 0, end < 0 ? buf.Length : end);
        }
        return "?";
    }

    private void Read(ulong address, Span<byte> buf)
    {
        if (!mem.TryRead(address, buf)) throw new UiReadException($"read failed at 0x{address:X}");
    }

    private ulong U64(ulong a) { Span<byte> b = stackalloc byte[8]; Read(a, b); return BinaryPrimitives.ReadUInt64LittleEndian(b); }
    private bool TryU64(ulong a, out ulong v) { Span<byte> b = stackalloc byte[8]; var ok = mem.TryRead(a, b); v = ok ? BinaryPrimitives.ReadUInt64LittleEndian(b) : 0; return ok; }
    private uint U32(ulong a) { Span<byte> b = stackalloc byte[4]; Read(a, b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
    private int I32(ulong a) => (int)U32(a);
    private byte Byte(ulong a) { Span<byte> b = stackalloc byte[1]; Read(a, b); return b[0]; }

    private int[] ReadInts(ulong a, int n)
    {
        var raw = new byte[n * 4];
        Read(a, raw);
        var r = new int[n];
        for (var i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4));
        return r;
    }

    private float[] F32s(ulong a, int n)
    {
        var raw = new byte[n * 4];
        Read(a, raw);
        var r = new float[n];
        for (var i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4));
        if (r.Any(f => !float.IsFinite(f))) throw new UiReadException($"non-finite float at 0x{a:X}");
        return r;
    }
}
