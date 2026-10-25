using System;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Host;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.XrefScans;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Startup;

public record RuntimeConfiguration
{
    public Version UnityVersion { get; init; }
    public IDetourProvider DetourProvider { get; init; }
}

public sealed class Il2CppInteropRuntime : BaseHost
{
    private Il2CppInteropRuntime()
    {
    }

    public static Il2CppInteropRuntime Instance => GetInstance<Il2CppInteropRuntime>();

    public Version UnityVersion { get; private init; }

    public IDetourProvider DetourProvider { get; private init; }

    public static Il2CppInteropRuntime Create(RuntimeConfiguration configuration)
    {
        var res = new Il2CppInteropRuntime
        {
            UnityVersion = configuration.UnityVersion,
            DetourProvider = configuration.DetourProvider
        };
        SetInstance(res);
        res.AddXrefScanner<Il2CppInteropRuntime, XrefScanImpl>();
        return res;
    }

    public override void Start()
    {
        CrashGuard.Install();
        Logger.Instance.LogInformation("[startup] Il2CppInteropRuntime.Start: recalculating handlers");
        UnityVersionHandler.RecalculateHandlers();
        Logger.Instance.LogInformation("[startup] handlers ready; starting host components");
        base.Start();
        Logger.Instance.LogInformation("[startup] Il2CppInteropRuntime.Start: complete");
    }
}
