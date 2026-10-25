using System;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.Runtime;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Injection.Hooks
{
    internal unsafe class Class_FromIl2CppType_Hook : Hook<Class_FromIl2CppType_Hook.MethodDelegate>
    {
        public override string TargetMethodName => "Class::FromIl2CppType";
        public override MethodDelegate GetDetour() => Hook;

        /// Common version of the Il2CppType, the only thing that changed between unity version are the bitfields values that we don't use
        internal readonly struct Il2CppType
        {
            public readonly void* data;
            public readonly ushort attrs;
            public readonly Il2CppTypeEnum type;
            private readonly byte _bitfield;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate Il2CppClass* MethodDelegate(Il2CppType* type, bool throwOnError);

        private static int s_VerboseHits;

        private Il2CppClass* Hook(Il2CppType* type, bool throwOnError)
        {
            InjectorHelpers.LogHookHit(TargetMethodName);

            var verbose = System.Threading.Interlocked.Increment(ref s_VerboseHits) <= 40;
            var data = (nint)type->data;

            if (data < 0 && (type->type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS || type->type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE))
            {
                if (InjectorHelpers.s_InjectedClasses.TryGetValue(data, out var classPointer))
                {
                    if (verbose)
                        Logger.Instance.LogInformation("[fromtype] #{Hit} data=0x{Data:X} type={Type} -> injected 0x{Klass:X}",
                            s_VerboseHits, (long)data, type->type, classPointer.ToInt64());
                    return (Il2CppClass*)classPointer;
                }

                // Falling through to null here is silent and makes AddComponent report
                // "The script 'X' could not be instantiated!" with no further trace.
                Logger.Instance.LogWarning("[fromtype] data=0x{Data:X} type={Type} is not a known injected token (known: {Known})",
                    (long)data, type->type, string.Join(",", InjectorHelpers.s_InjectedClasses.Keys));
                return null;
            }

            if (verbose)
                Logger.Instance.LogInformation("[fromtype] #{Hit} data=0x{Data:X} type={Type} -> native",
                    s_VerboseHits, (long)data, type->type);

            return Original(type, throwOnError);
        }

        public override IntPtr FindTargetMethod()
        {
            var classFromTypeAPI = InjectorHelpers.GetIl2CppExport(nameof(IL2CPP.il2cpp_class_from_il2cpp_type));
            Logger.Instance.LogTrace("il2cpp_class_from_il2cpp_type: 0x{ClassFromTypeApiAddress}", classFromTypeAPI.ToInt64().ToString("X2"));

            return XrefScannerLowLevel.JumpTargets(classFromTypeAPI).Single();
        }
    }
}
