using System;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.Runtime;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Injection.Hooks
{
    internal unsafe class Class_FromName_Hook : Hook<Class_FromName_Hook.MethodDelegate>
    {
        public override string TargetMethodName => "Class::FromName";
        public override MethodDelegate GetDetour() => Hook;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate Il2CppClass* MethodDelegate(Il2CppImage* image, IntPtr _namespace, IntPtr name);

        private static int s_VerboseHits;

        private Il2CppClass* Hook(Il2CppImage* image, IntPtr _namespace, IntPtr name)
        {
            InjectorHelpers.LogHookHit(TargetMethodName);
            Il2CppClass* classPtr = Original(image, _namespace, name);

            var hit = System.Threading.Interlocked.Increment(ref s_VerboseHits);
            var verbose = hit <= 80;

            if (classPtr == null)
            {
                string namespaze = Marshal.PtrToStringUTF8(_namespace);
                string className = Marshal.PtrToStringUTF8(name);
                InjectorHelpers.s_ClassNameLookup.TryGetValue((namespaze, className, (IntPtr)image), out IntPtr injectedClass);
                classPtr = (Il2CppClass*)injectedClass;
                if (verbose)
                    Logger.Instance.LogInformation("[fromname] #{Hit} {Ns}.{Name} -> native=null injected=0x{Injected:X}",
                        hit, namespaze, className, injectedClass.ToInt64());
            }
            else if (verbose)
            {
                Logger.Instance.LogInformation("[fromname] #{Hit} {Ns}.{Name} -> native",
                    hit, Marshal.PtrToStringUTF8(_namespace), Marshal.PtrToStringUTF8(name));
            }

            return classPtr;
        }

        public override IntPtr FindTargetMethod()
        {
            var classFromNameAPI = InjectorHelpers.GetIl2CppExport(nameof(IL2CPP.il2cpp_class_from_name));
            Logger.Instance.LogTrace("il2cpp_class_from_name: 0x{ClassFromNameApiAddress}", classFromNameAPI.ToInt64().ToString("X2"));

            return XrefScannerLowLevel.JumpTargets(classFromNameAPI).Single();
        }
    }
}
