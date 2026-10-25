using System.Globalization;
using System.Text.Json.Nodes;

namespace RaidActions.Game.Memory;

/// <summary>Raw read-only memory (live: Core's ProcessMemory, PROCESS_VM_READ only; tests: a fake heap).</summary>
public interface INativeMemory
{
    bool TryRead(ulong address, Span<byte> buffer);
}

/// <summary>Unity's native layouts (config/ui-native.json), hand-worked; see the file for how each was established.</summary>
public sealed record NativeLayout(
    int ComponentGameObject, int ComponentManagedSlot,
    int GoComponents, int GoComponentCount, int GoActiveSelf, int GoActiveInHierarchy, int GoName,
    int TransformHierarchy, int TransformIndex, int RectTransformTypeIndex,
    int RectRect, int RectAnchorMin, int RectPivot,
    int HCapacity, int HLocalTransforms, int HStride, int HParents, int HDeepChildCount, int HTransforms, int HNextIndices,
    string GameVersion, IReadOnlyList<string> Unverified)
{
    public static NativeLayout Load(string path)
    {
        var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var unverified = new List<string>();
        int H(string section, string name, string key = "offset")
        {
            var n = o[section]![name]!;
            if ((string?)n["status"] != "verified") unverified.Add($"{section}.{name}");
            var s = (string)n[key]!;
            return int.Parse(s[2..], NumberStyles.HexNumber);
        }
        return new NativeLayout(
            H("component", "gameObject"), H("component", "managedSlot"),
            H("gameObject", "components"), H("gameObject", "componentCount"), H("gameObject", "activeSelf"), H("gameObject", "activeInHierarchy"), H("gameObject", "name"),
            H("transform", "hierarchy"), H("transform", "index"), H("transform", "rectTransformTypeIndex", "value"),
            H("rectTransform", "rect"), H("rectTransform", "anchorMin"), H("rectTransform", "pivot"),
            H("hierarchy", "capacity"), H("hierarchy", "localTransforms"), (int)o["hierarchy"]!["localTransforms"]!["stride"]!,
            H("hierarchy", "parentIndices"), H("hierarchy", "deepChildCount"), H("hierarchy", "transforms"), H("hierarchy", "nextIndices"),
            (string)o["build"]!["gameVersion"]!, unverified);
    }
}
