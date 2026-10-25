using System.Globalization;
using System.Text;
using RaidExtractor;

namespace RaidActions.Game.Memory;

/// <summary>
/// Read-only diagnostics for working out Unity's native layouts: hex dumps read as pointers and floats. Used to find (and, after a
/// game update, re-check) the hand-worked native offsets. Never writes.
/// </summary>
public static class Probe
{
    public static string Dump(ProcessMemory m, ulong address, int length)
    {
        var buf = new byte[length];
        if (!m.TryRead(address, buf)) return $"read failed at 0x{address:X}";
        var sb = new StringBuilder();
        for (var i = 0; i + 8 <= length; i += 8)
        {
            var q = BitConverter.ToUInt64(buf, i);
            var f1 = BitConverter.ToSingle(buf, i);
            var f2 = BitConverter.ToSingle(buf, i + 4);
            var i1 = BitConverter.ToInt32(buf, i);
            var i2 = BitConverter.ToInt32(buf, i + 4);
            sb.Append(CultureInfo.InvariantCulture, $"+0x{i:X3}  {q:X16}  i32 {i1,11} {i2,11}  f32 {F(f1),12} {F(f2),12}");
            sb.Append("  |").Append(new string(buf.Skip(i).Take(8).Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray())).Append('|');
            if (Il2CppReader.IsPlausiblePointer(q)) sb.Append("  ptr");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string F(float f) =>
        float.IsFinite(f) && (f == 0 || Math.Abs(f) is > 1e-4f and < 1e7f) ? f.ToString("0.####", CultureInfo.InvariantCulture) : "-";
}
