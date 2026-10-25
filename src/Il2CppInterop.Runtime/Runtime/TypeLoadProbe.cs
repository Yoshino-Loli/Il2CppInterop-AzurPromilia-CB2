using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Common;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Runtime;

/// <summary>
/// UniverseLib calls <c>Assembly.GetTypes()</c> on every loaded assembly while building its type
/// cache, and on some setups the CLR aborts the process part way through with an uncatchable
/// "Invalid Program" failfast - no managed exception, nothing in the MelonLoader log, so the
/// offending assembly is impossible to identify afterwards.
///
/// Walking the same list here, logging each name *before* the call, turns that abort into a
/// pointer: the last name without a matching result line is the assembly that killed the process.
/// </summary>
internal static class TypeLoadProbe
{
    private static int s_ran;

    public static void RunOnce()
    {
        if (System.Threading.Interlocked.Exchange(ref s_ran, 1) != 0) return;
        Run();
    }

    public static void Run()
    {
        Assembly[] assemblies;
        try
        {
            assemblies = AppDomain.CurrentDomain.GetAssemblies();
        }
        catch (Exception ex)
        {
            Logger.Instance.LogWarning("[typeprobe] could not enumerate assemblies: {Message}", ex.Message);
            return;
        }

        Logger.Instance.LogInformation("[typeprobe] probing {Count} loaded assemblies", assemblies.Length);

        foreach (var assembly in assemblies)
        {
            string name;
            try
            {
                name = assembly.GetName().Name ?? "<unnamed>";
            }
            catch
            {
                name = "<unnamed>";
            }

            // Logged before the call on purpose: if the CLR aborts inside GetTypes, this line is the
            // only record of what was being loaded.
            Logger.Instance.LogInformation("[typeprobe] > {Name}", name);

            var result = ProbeTypes(assembly);
            Logger.Instance.LogInformation("[typeprobe] < {Name} {Result}", name, result);
        }

        Logger.Instance.LogInformation("[typeprobe] all assemblies survived GetTypes");
    }

    // Kept out of line so the JIT compiles this method (not the loop above) as the caller of
    // GetTypes; if the call is what the runtime rejects, the loop still gets its log line out first.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ProbeTypes(Assembly assembly)
    {
        try
        {
            return $"{assembly.GetTypes().Length} types";
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaded = 0;
            foreach (var type in ex.Types)
                if (type != null) loaded++;

            return $"ReflectionTypeLoadException {loaded}/{ex.Types.Length} types (catchable)";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
