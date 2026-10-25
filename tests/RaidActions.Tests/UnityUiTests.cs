using System.Buffers.Binary;
using System.Numerics;
using RaidActions.Game.Memory;

namespace RaidActions.Tests;

/// <summary>
/// The Unity UI reader on a fake native heap laid out as config/ui-native.json says (no game): hierarchy walk along nextIndices,
/// world matrices, the overlay-canvas check, rectangles at two window sizes, scripts, contexts and interactable.
/// </summary>
public class UnityUiTests
{
    private static readonly NativeLayout L = NativeLayout.Load(Path.Combine(AppPathsForTests.Config, "ui-native.json"));

    private sealed class Heap : INativeMemory
    {
        private readonly SortedDictionary<ulong, byte[]> _blocks = [];
        private ulong _next = 0x10_0000_0000;

        public ulong Alloc(int size)
        {
            var a = _next;
            _next += (ulong)((size + 0xFFF) & ~0xFFF) + 0x1000;
            _blocks[a] = new byte[size];
            return a;
        }

        private (byte[] Block, int Offset) At(ulong address)
        {
            foreach (var (start, block) in _blocks)
                if (address >= start && address < start + (ulong)block.Length) return (block, (int)(address - start));
            throw new InvalidOperationException($"unmapped 0x{address:X}");
        }

        public void U64(ulong a, ulong v) { var (b, o) = At(a); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v); }
        public void I32(ulong a, int v) { var (b, o) = At(a); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v); }
        public void F32(ulong a, params float[] v) { var (b, o) = At(a); for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + i * 4), v[i]); }
        public void Byte(ulong a, byte v) { var (b, o) = At(a); b[o] = v; }

        public bool TryRead(ulong address, Span<byte> buffer)
        {
            foreach (var (start, block) in _blocks)
                if (address >= start && address + (ulong)buffer.Length <= start + (ulong)block.Length)
                {
                    block.AsSpan((int)(address - start), buffer.Length).CopyTo(buffer);
                    return true;
                }
            return false;
        }
    }

    /// <summary>Managed objects: class chains, m_CachedPtr, BaseView.Context, Selectable.m_Interactable.</summary>
    private sealed class Managed : IGameObjects
    {
        public readonly Dictionary<ulong, (string[] Chain, Dictionary<string, object> Fields)> Objects = [];
        private ulong _next = 0x20_0000_0000;

        public ulong Add(string[] chain, Dictionary<string, object> fields)
        {
            var a = _next += 0x100;
            Objects[a] = (chain, fields);
            return a;
        }

        public ulong Root(string name, string expectedType) => throw new NotSupportedException();
        public string ClassName(ulong obj) => Objects[obj].Chain[0];
        public IReadOnlyList<string> ClassChain(ulong obj) => Objects[obj].Chain;
        public ulong Ref(ulong obj, string type, string member) => Objects.TryGetValue(obj, out var o) && o.Fields.TryGetValue(member, out var v) ? (ulong)v : 0;
        public int Int(ulong obj, string type, string member) => (int)Objects[obj].Fields[member];
        public bool Bool(ulong obj, string type, string member) => (bool)Objects[obj].Fields[member];
        public int? NullableInt(ulong obj, string type, string member) => (int?)Objects[obj].Fields[member];
        public IReadOnlyList<ulong> RefList(ulong list, int max) => throw new NotSupportedException();
        public string? EnumName(string enumType, int value) => null;
    }

    private const int Capacity = 32;
    private const int TransformTypeIndex = 0x10;

    /// <summary>
    /// A scene: 0 scene root (Transform) → 1 canvas (RectTransform at the client centre, scale w/1920) → 5 view (full screen,
    /// bound to a context) → 9 button (Selectable, 100x40 at canvas (100, 200)) and 3 list item (context with id 42, 120x120 at
    /// (-400, 0), inactive when <paramref name="itemActive"/> is false). Indices are deliberately out of order.
    /// </summary>
    private static (UnityUi Ui, ulong View, Managed M) Scene(int w, int h, bool itemActive = true, float canvasX = float.NaN)
    {
        var heap = new Heap();
        var m = new Managed();
        var hier = heap.Alloc(0x100);
        var locals = heap.Alloc(Capacity * L.HStride);
        var parents = heap.Alloc(Capacity * 4);
        var deep = heap.Alloc(Capacity * 4);
        var transforms = heap.Alloc(Capacity * 8);
        var next = heap.Alloc(Capacity * 4);
        heap.I32(hier + (ulong)L.HCapacity, Capacity);
        heap.U64(hier + (ulong)L.HLocalTransforms, locals);
        heap.U64(hier + (ulong)L.HParents, parents);
        heap.U64(hier + (ulong)L.HDeepChildCount, deep);
        heap.U64(hier + (ulong)L.HTransforms, transforms);
        heap.U64(hier + (ulong)L.HNextIndices, next);
        for (var i = 0; i < Capacity; i++) heap.I32(next + (ulong)i * 4, -1);

        var scale = w / 1920f;
        ulong viewManaged = 0;
        (ulong Go, ulong Transform) Node(int index, int parent, int deepCount, int nextIndex, string name, bool rect, Vector3 pos, float s, float[]? localRect, bool active = true)
        {
            heap.I32(parents + (ulong)index * 4, parent);
            heap.I32(deep + (ulong)index * 4, deepCount);
            heap.I32(next + (ulong)index * 4, nextIndex);
            heap.F32(locals + (ulong)(index * L.HStride), pos.X, pos.Y, pos.Z, 0, 0, 0, 0, 1, s, s, s, 0);
            var t = heap.Alloc(0x100);
            var go = heap.Alloc(0x80);
            var comps = heap.Alloc(16 * 8);
            var nm = heap.Alloc(64);
            var bytes = System.Text.Encoding.ASCII.GetBytes(name);
            for (var b = 0; b < bytes.Length; b++) heap.Byte(nm + (ulong)b, bytes[b]);
            heap.U64(t + (ulong)L.ComponentGameObject, go);
            heap.U64(t + (ulong)L.TransformHierarchy, hier);
            heap.I32(t + (ulong)L.TransformIndex, index);
            if (localRect is not null) heap.F32(t + (ulong)L.RectRect, localRect);
            heap.U64(transforms + (ulong)index * 8, t);
            heap.U64(go + (ulong)L.GoComponents, comps);
            heap.U64(go + (ulong)L.GoComponentCount, 1);
            heap.Byte(go + (ulong)L.GoActiveSelf, (byte)(active ? 1 : 0));
            heap.Byte(go + (ulong)L.GoActiveInHierarchy, (byte)(active ? 1 : 0));
            heap.U64(go + (ulong)L.GoName, nm);
            heap.I32(comps, rect ? L.RectTransformTypeIndex : TransformTypeIndex);
            heap.U64(comps + 8, t);
            return (go, t);
        }

        ulong Script(ulong go, string[] chain, Dictionary<string, object> fields)
        {
            var comps = BinaryPrimitives.ReadUInt64LittleEndian(Read(heap, go + (ulong)L.GoComponents, 8));
            var count = (int)BinaryPrimitives.ReadUInt64LittleEndian(Read(heap, go + (ulong)L.GoComponentCount, 8));
            var native = heap.Alloc(0x40);
            var slot = heap.Alloc(8);
            var managed = m.Add(chain, new Dictionary<string, object>(fields) { ["m_CachedPtr"] = native });
            heap.U64(native + (ulong)L.ComponentGameObject, go);
            heap.U64(native + (ulong)L.ComponentManagedSlot, slot);
            heap.U64(slot, managed);
            heap.I32(comps + (ulong)count * 16, 0x42);
            heap.U64(comps + (ulong)count * 16 + 8, native);
            heap.U64(go + (ulong)L.GoComponentCount, (ulong)count + 1);
            return managed;
        }

        Node(0, -1, 5, 1, "SceneRoot", false, Vector3.Zero, 1, null);
        Node(1, 0, 4, 5, "Canvas", true, new Vector3(float.IsNaN(canvasX) ? w / 2f : canvasX, h / 2f, 0), scale, [-960, -540, 1920, 1080]);
        var view = Node(5, 1, 3, 9, "[DV] Test", true, Vector3.Zero, 1, [-960, -540, 1920, 1080]);
        var button = Node(9, 5, 1, 3, "EquipButton", true, new Vector3(100, 200, 0), 1, [-50, -20, 100, 40]);
        var item = Node(3, 5, 1, -1, "HeroTile", true, new Vector3(-400, 0, 0), 1, [-60, -60, 120, 120], itemActive);

        viewManaged = Script(view.Go, ["Client.View.Views.DialogView", "Client.MVVM.Base.View.BaseView", "UnityEngine.MonoBehaviour"],
            new() { ["Context"] = m.Add(["Client.ViewModel.TestDialogContext"], []) });
        Script(button.Go, ["UnityEngine.UI.Button", "UnityEngine.UI.Selectable", "UnityEngine.MonoBehaviour"], new() { ["m_Interactable"] = true });
        Script(item.Go, ["Client.View.ItemView", "Client.MVVM.Base.View.BaseView"], new() { ["Context"] = m.Add(["Client.ViewModel.HeroItemContext"], new() { ["_heroId"] = 42 }) });
        Script(item.Go, ["UnityEngine.UI.Toggle", "UnityEngine.UI.Selectable"], new() { ["m_Interactable"] = false });
        return (new UnityUi(heap, L, m), viewManaged, m);
    }

    private static byte[] Read(Heap h, ulong a, int n)
    {
        var b = new byte[n];
        Assert.True(h.TryRead(a, b));
        return b;
    }

    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1280, 720)]
    public void Rectangles_come_out_in_client_pixels_at_any_size(int w, int h)
    {
        var (ui, view, _) = Scene(w, h);
        var tree = ui.Read(view, "Test", w, h);
        Assert.Null(tree.ScreenProblem);
        Assert.Equal(w / 1920f, tree.CanvasScale, 4);
        var s = w / 1920f;

        var full = tree.Nodes.Single(n => n.Name == "[DV] Test").Screen!.Value;
        Assert.Equal(0, full.X, 2);
        Assert.Equal(0, full.Y, 2);
        Assert.Equal(w, full.Width, 2);
        Assert.Equal(h, full.Height, 2);

        // The button: centre at canvas (100, 200) → client (w/2 + 100s, h/2 - 200s); 100x40 canvas units.
        var b = tree.Nodes.Single(n => n.Name == "EquipButton").Screen!.Value;
        Assert.Equal(w / 2f + 100 * s, b.Center.X, 2);
        Assert.Equal(h / 2f - 200 * s, b.Center.Y, 2);
        Assert.Equal(100 * s, b.Width, 2);
        Assert.Equal(40 * s, b.Height, 2);
    }

    [Fact]
    public void The_subtree_follows_the_depth_first_links_not_the_index_order()
    {
        var (ui, view, _) = Scene(1600, 900);
        var tree = ui.Read(view, "Test", 1600, 900);
        Assert.Equal(["[DV] Test", "EquipButton", "HeroTile"], tree.Nodes.Select(n => n.Name));
        Assert.Equal(["[DV] Test", "[DV] Test/EquipButton", "[DV] Test/HeroTile"], tree.Nodes.Select(n => n.Path));
        Assert.Equal([0, 1, 1], tree.Nodes.Select(n => n.Depth));
    }

    [Fact]
    public void Scripts_contexts_and_interactable_are_read()
    {
        var (ui, view, m) = Scene(1600, 900);
        var tree = ui.Read(view, "Test", 1600, 900);
        var button = tree.Nodes.Single(n => n.Name == "EquipButton");
        Assert.True(button.Interactable);
        Assert.True(button.Has("UnityEngine.UI.Button"));
        var tile = tree.Nodes.Single(n => n.Name == "HeroTile");
        Assert.False(tile.Interactable);
        Assert.Equal("Client.ViewModel.HeroItemContext", tile.Context!.Class);
        Assert.Equal(42, m.Int(tile.Context.Address, "", "_heroId"));
        Assert.Equal("Client.ViewModel.TestDialogContext", tree.Nodes[0].Context!.Class);
    }

    [Fact]
    public void Inactive_elements_are_reported()
    {
        var (ui, view, _) = Scene(1600, 900, itemActive: false);
        var tile = ui.Read(view, "Test", 1600, 900).Nodes.Single(n => n.Name == "HeroTile");
        Assert.False(tile.ActiveInHierarchy);
        Assert.False(tile.ActiveSelf);
    }

    [Fact]
    public void A_canvas_that_is_not_screen_space_overlay_gives_no_rectangles()
    {
        var (ui, view, _) = Scene(1600, 900, canvasX: 316.5f);
        var tree = ui.Read(view, "Test", 1600, 900);
        Assert.Contains("not screen-space overlay", tree.ScreenProblem);
        Assert.All(tree.Nodes, n => Assert.Null(n.Screen));
        // The same scene read for the wrong window size is refused too (rectangles never come from a guessed size).
        var (ui2, view2, _) = Scene(1600, 900);
        Assert.NotNull(ui2.Read(view2, "Test", 1280, 720).ScreenProblem);
    }

    [Fact]
    public void Rotation_and_scale_compose_through_the_parents()
    {
        // 90° about z, scale 2: the local rect (0,0)-(10,20) becomes 40 wide and 20 high around the origin.
        var world = Matrix4x4.CreateScale(2) * Matrix4x4.CreateFromYawPitchRoll(0, 0, MathF.PI / 2) * Matrix4x4.CreateTranslation(500, 500, 0);
        var r = UnityUi.ToClient(world, 0, 0, 10, 20, 1000);
        Assert.Equal(40, r.Width, 3);
        Assert.Equal(20, r.Height, 3);
        Assert.Equal(460, r.X, 3);
        Assert.Equal(480, r.Y, 3);
    }

    [Fact]
    public void Only_verified_offsets_are_marked_verified()
    {
        Assert.Contains("hierarchy.nextIndices", L.Unverified);
        Assert.Contains("transform.rectTransformTypeIndex", L.Unverified);
        Assert.DoesNotContain("rectTransform.rect", L.Unverified);
    }
}

public class CrossCheckTests
{
    [Fact]
    public void Memory_and_template_agree_when_they_overlap()
    {
        var r = CrossCheck.Compare(new ClientRect(100, 100, 50, 50), new ClientRect(104, 98, 50, 52));
        Assert.Equal(CrossCheckOutcome.Agrees, r.Outcome);
        Assert.False(r.Refuses);
    }

    [Fact]
    public void A_template_elsewhere_refuses_the_step()
    {
        var r = CrossCheck.Compare(new ClientRect(100, 100, 50, 50), new ClientRect(400, 100, 50, 50));
        Assert.Equal(CrossCheckOutcome.Mismatch, r.Outcome);
        Assert.True(r.Refuses);
        Assert.Equal(0, r.Overlap);
    }

    [Fact]
    public void A_template_that_does_not_match_alone_does_not_refuse()
    {
        var r = CrossCheck.Compare(new ClientRect(100, 100, 50, 50), null);
        Assert.Equal(CrossCheckOutcome.NoTemplate, r.Outcome);
        Assert.False(r.Refuses);
    }
}
