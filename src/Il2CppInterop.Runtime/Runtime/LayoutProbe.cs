using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Runtime;

/// <summary>
/// Some games ship an il2cpp runtime that is patched relative to stock Unity while still reporting
/// a stock Unity version. Il2CppInterop picks its struct handlers from that reported version, so a
/// fork silently gets the wrong layout and every field past the insertion point reads garbage.
///
/// This probe measures the real layout of the running module instead of trusting the version
/// string: it asks the engine for a class/method through the il2cpp exports (which always use the
/// true offsets) and compares the answer with what the stock struct offsets yield.
///
/// Two things are measured:
///
/// 1. <b>Which layout family the runtime is built on.</b> The 2021.x family puts Il2CppClass.flags
///    at 0x114, the 2022.x family puts it at 0x118 (that struct gained stack_slot_size and lost
///    naturalAligment). This matters twice: the padding probe below is anchored on the family's
///    stock offsets, and the padded handler we ship models the 2021.x struct.
///
/// 2. <b>How many bytes the runtime inserts.</b> Layouts confirmed against two shipped games:
///
///      Endfield      (Unity 2021.3.34f5, metadata 29) - reports the 2021.x family
///      Azur Promilia (Unity 2022.3.62f3, metadata 31) - <i>also</i> reports the 2021.x family,
///                                                       even though its Unity version string
///                                                       makes Il2CppInterop select the 2022.x
///                                                       handlers
///
///    Both insert the same amount:
///      Il2CppClass      - two extra bytes after `token`      (method_count 0x11C -> 0x11E)
///      Il2CppMethodInfo - one extra byte after `slot`        (parameters_count 0x52 -> 0x53)
///
/// So the measured padding, not the Unity version, is what decides whether the padded handlers apply.
/// </summary>
internal static class Il2CppLayoutProbe
{
    /// <summary>Il2CppClass.flags in the 2021.x (metadata 29) struct.</summary>
    private const int ClassFlagsOffset2021 = 0x114;

    /// <summary>Il2CppClass.flags in the 2022.x (metadata 31) struct.</summary>
    private const int ClassFlagsOffset2022 = 0x118;

    private const int ClassMethodCountOffset2021 = 0x11C;
    private const int ClassMethodCountOffset2022 = 0x120;

    private const int MethodInfoParametersCountOffset = 0x52;

    private static bool s_ran;

    /// <summary>Which stock struct the running runtime is built on.</summary>
    public enum LayoutFamily
    {
        Unknown = 0,
        V2021 = 1,
        V2022 = 2
    }

    public static LayoutFamily Family { get; private set; }

    /// <summary>Extra bytes between `token` and `method_count` in <c>Il2CppClass</c>.</summary>
    public static int ClassStructPadding { get; private set; }

    /// <summary>Extra bytes between `slot` and `parameters_count` in <c>Il2CppMethodInfo</c>.</summary>
    public static int MethodInfoStructPadding { get; private set; }

    public static void Run()
    {
        if (s_ran) return;
        s_ran = true;

        try
        {
            var klass = FindProbeClass();
            if (klass == IntPtr.Zero)
            {
                Logger.Instance.LogWarning("il2cpp layout probe: no probe class found, assuming stock layout");
                return;
            }

            ProbeFamily(klass);
            ProbeClassLayout(klass);
            ProbeMethodInfoLayout(klass);

            Logger.Instance.LogInformation(
                "il2cpp layout probe: {Family} structs, Il2CppClass +{ClassPad} byte(s), Il2CppMethodInfo +{MethodPad} byte(s)",
                Family, ClassStructPadding, MethodInfoStructPadding);
        }
        catch (Exception ex)
        {
            Logger.Instance.LogWarning("il2cpp layout probe failed, assuming stock layout: {Message}", ex.Message);
        }
    }

    private static IntPtr FindProbeClass()
    {
        var corlib = IL2CPP.il2cpp_get_corlib();
        if (corlib == IntPtr.Zero) return IntPtr.Zero;

        string[] candidates = { "String", "Object", "Exception", "Int32" };
        foreach (var name in candidates)
        {
            var klass = IL2CPP.il2cpp_class_from_name(corlib, "System", name);
            if (klass != IntPtr.Zero) return klass;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// The engine always reads flags from the offset its own struct uses, so comparing that value
    /// against both candidate offsets identifies the family without guessing.
    /// </summary>
    private static void ProbeFamily(IntPtr klass)
    {
        var engineFlags = unchecked((uint)IL2CPP.il2cpp_class_get_flags(klass));

        var at2021 = ReadUInt32(klass + ClassFlagsOffset2021);
        var at2022 = ReadUInt32(klass + ClassFlagsOffset2022);

        if (at2021 == engineFlags)
        {
            Family = LayoutFamily.V2021;
        }
        else if (at2022 == engineFlags)
        {
            Family = LayoutFamily.V2022;
        }
        else
        {
            Family = LayoutFamily.Unknown;
            Logger.Instance.LogWarning(
                "il2cpp layout probe: Il2CppClass.flags is at neither 0x{Off2021:X} nor 0x{Off2022:X} (engine says 0x{Flags:X}, found 0x{A:X} / 0x{B:X})",
                ClassFlagsOffset2021, ClassFlagsOffset2022, engineFlags, at2021, at2022);
        }
    }

    private static void ProbeClassLayout(IntPtr klass)
    {
        var counted = CountMethods(klass);
        if (counted <= 0) return;

        var stockOffset = Family == LayoutFamily.V2022
            ? ClassMethodCountOffset2022
            : ClassMethodCountOffset2021;

        var atStock = ReadUInt16(klass + stockOffset);
        var atShifted = ReadUInt16(klass + stockOffset + 2);

        if (atStock == counted) ClassStructPadding = 0;
        else if (atShifted == counted) ClassStructPadding = 2;
        else
            Logger.Instance.LogWarning(
                "il2cpp layout probe: neither 0x{Stock:X} nor 0x{Shifted:X} matches the engine's method count {Count}",
                atStock, atShifted, counted);
    }

    private static void ProbeMethodInfoLayout(IntPtr klass)
    {
        int stockVotes = 0, shiftedVotes = 0;
        var iter = IntPtr.Zero;

        for (var i = 0; i < 400; i++)
        {
            var method = IL2CPP.il2cpp_class_get_methods(klass, ref iter);
            if (method == IntPtr.Zero) break;

            var paramCount = (int)IL2CPP.il2cpp_method_get_param_count(method);
            if (paramCount == 0 || paramCount > 32) continue; // 0 is ambiguous often enough to skip

            if (ReadByte(method + MethodInfoParametersCountOffset) == paramCount) stockVotes++;
            if (ReadByte(method + MethodInfoParametersCountOffset + 1) == paramCount) shiftedVotes++;
        }

        if (stockVotes == 0 && shiftedVotes == 0) return;

        MethodInfoStructPadding = shiftedVotes > stockVotes ? 1 : 0;

        if (shiftedVotes == stockVotes)
            Logger.Instance.LogWarning(
                "il2cpp layout probe: ambiguous Il2CppMethodInfo layout (stock={Stock} shifted={Shifted})",
                stockVotes, shiftedVotes);
    }

    private static int CountMethods(IntPtr klass)
    {
        var count = 0;
        var iter = IntPtr.Zero;
        while (true)
        {
            var method = IL2CPP.il2cpp_class_get_methods(klass, ref iter);
            if (method == IntPtr.Zero) break;
            if (++count > 10000) break;
        }

        return count;
    }

    private static ushort ReadUInt16(IntPtr address) => unchecked((ushort)Marshal.ReadInt16(address));

    private static uint ReadUInt32(IntPtr address) => unchecked((uint)Marshal.ReadInt32(address));

    private static byte ReadByte(IntPtr address) => Marshal.ReadByte(address);
}
