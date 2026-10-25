using System;
using System.Linq;
using System.Runtime.InteropServices;
using Iced.Intel;
using Il2CppInterop.Common;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Startup;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Injection.Hooks
{
    internal unsafe class MetadataCache_GetTypeInfoFromTypeDefinitionIndex_Hook :
        Hook<MetadataCache_GetTypeInfoFromTypeDefinitionIndex_Hook.MethodDelegate>
    {
        public override string TargetMethodName => "MetadataCache::GetTypeInfoFromTypeDefinitionIndex";
        public override MethodDelegate GetDetour() => Hook;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate Il2CppClass* MethodDelegate(IntPtr index);

        private Il2CppClass* Hook(IntPtr index)
        {
            InjectorHelpers.LogHookHit(TargetMethodName);

            // The parameter is IntPtr rather than int on purpose. The xref heuristic below can land
            // on MetadataCache::GetTypeInfoFromHandle instead of GetTypeInfoFromTypeDefinitionIndex;
            // the former takes a 64-bit metadata handle, so declaring this as `int` truncated it and
            // the pass-through dereferenced a bogus pointer (seen on Azur Promilia, faulting on
            // handle & 0xFFFFFFFF + 8). Keeping the register width makes the pass-through lossless
            // either way; the index-based lookup below still only uses the low 32 bits, which is
            // exactly what injected class tokens are keyed by.
            if (InjectorHelpers.s_InjectedClasses.TryGetValue((int)index, out IntPtr classPtr))
                return (Il2CppClass*)classPtr;

            return Original(index);
        }

        private IntPtr FindGetTypeInfoFromTypeDefinitionIndex(bool forceICallMethod = false)
        {
            IntPtr getTypeInfoFromTypeDefinitionIndex = IntPtr.Zero;

            // il2cpp_image_get_class is added in 2018.3.0f1
            if (Il2CppInteropRuntime.Instance.UnityVersion < new Version(2018, 3, 0) || forceICallMethod)
            {
                // (Kasuromi): RuntimeHelpers.InitializeArray calls an il2cpp icall, proxy function does some magic before it invokes it
                // https://github.com/Unity-Technologies/mono/blob/unity-2018.2/mcs/class/corlib/System.Runtime.CompilerServices/RuntimeHelpers.cs#L53-L54
                IntPtr runtimeHelpersInitializeArray = InjectorHelpers.GetIl2CppMethodPointer(
                    typeof(Il2CppSystem.Runtime.CompilerServices.RuntimeHelpers)
                        .GetMethod("InitializeArray", new Type[] { typeof(Il2CppSystem.Array), typeof(IntPtr) })
                );
                Logger.Instance.LogTrace("Il2CppSystem.Runtime.CompilerServices.RuntimeHelpers::InitializeArray: 0x{RuntimeHelpersInitializeArrayAddress}", runtimeHelpersInitializeArray.ToInt64().ToString("X2"));

                var runtimeHelpersInitializeArrayICall = XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArray).Last();
                if (XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArrayICall).Count() == 1)
                {
                    // is a thunk function
                    Logger.Instance.LogTrace("RuntimeHelpers::thunk_InitializeArray: 0x{RuntimeHelpersInitializeArrayICallAddress}", runtimeHelpersInitializeArrayICall.ToInt64().ToString("X2"));
                    runtimeHelpersInitializeArrayICall = XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArrayICall).Single();
                }

                Logger.Instance.LogTrace("RuntimeHelpers::InitializeArray: 0x{RuntimeHelpersInitializeArrayICallAddress}", runtimeHelpersInitializeArrayICall.ToInt64().ToString("X2"));

                var typeGetUnderlyingType = XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArrayICall).ElementAt(1);
                Logger.Instance.LogTrace("Type::GetUnderlyingType: 0x{TypeGetUnderlyingTypeAddress}", typeGetUnderlyingType.ToInt64().ToString("X2"));

                getTypeInfoFromTypeDefinitionIndex = XrefScannerLowLevel.JumpTargets(typeGetUnderlyingType).First();
            }
            else
            {
                var imageGetClassAPI = InjectorHelpers.GetIl2CppExport(nameof(IL2CPP.il2cpp_image_get_class));
                Logger.Instance.LogTrace("il2cpp_image_get_class: 0x{ImageGetClassApiAddress}", imageGetClassAPI.ToInt64().ToString("X2"));

                var imageGetType = XrefScannerLowLevel.JumpTargets(imageGetClassAPI).First();
                Logger.Instance.LogTrace("Image::GetType: 0x{ImageGetTypeAddress}", imageGetType.ToInt64().ToString("X2"));

                var imageGetTypeXrefs = XrefScannerLowLevel.JumpTargets(imageGetType).ToArray();

                if (imageGetTypeXrefs.Length == 0)
                {
                    // (Kasuromi): Image::GetType appears to be inlined in il2cpp_image_get_class on some occasions,
                    // if the unconditional xrefs are 0 then we are in the correct method (seen on unity 2019.3.15)
                    getTypeInfoFromTypeDefinitionIndex = imageGetType;
                }
                else getTypeInfoFromTypeDefinitionIndex = imageGetTypeXrefs[0];
                if ((getTypeInfoFromTypeDefinitionIndex.ToInt64() & 0xF) != 0)
                {
                    Logger.Instance.LogTrace("Image::GetType xref wasn't aligned, attempting to resolve from icall");
                    return FindGetTypeInfoFromTypeDefinitionIndex(true);
                }
                if (imageGetTypeXrefs.Count() > 1 && UnityVersionHandler.IsMetadataV29OrHigher)
                {
                    // (Kasuromi): metadata v29 introduces handles and adds extra calls, a check for unity versions might be necessary in the future

                    Logger.Instance.LogTrace($"imageGetTypeXrefs.Length: {imageGetTypeXrefs.Length}");

                    // If the game is built as IL2CPP Master, GetAssemblyTypeHandle is inlined, xrefs length is 3 and it's the first function call,
                    // if not, it's the last call.
                    var getTypeInfoFromHandle = imageGetTypeXrefs.Length == 2 ? imageGetTypeXrefs.Last() : imageGetTypeXrefs.First();

                    Logger.Instance.LogTrace($"getTypeInfoFromHandle: {getTypeInfoFromHandle:X2}");

                    var getTypeInfoFromHandleXrefs = XrefScannerLowLevel.JumpTargets(getTypeInfoFromHandle).ToArray();

                    // If getTypeInfoFromHandle xrefs is not a single call, it's the function we want, if not, we keep xrefing until we find it
                    if (getTypeInfoFromHandleXrefs.Length != 1)
                    {
                        getTypeInfoFromTypeDefinitionIndex = getTypeInfoFromHandle;
                        Logger.Instance.LogTrace($"Xrefs length was not 1, getTypeInfoFromTypeDefinitionIndex: {getTypeInfoFromTypeDefinitionIndex:X2}");
                    }
                    else
                    {
                        // Two calls, second one (GetIndexForTypeDefinitionInternal) is inlined
                        getTypeInfoFromTypeDefinitionIndex = getTypeInfoFromHandleXrefs.Single();
                        // Xref scanner is sometimes confused about getTypeInfoFromHandle so we walk all the thunks until we hit the big method we need
                        while (XrefScannerLowLevel.JumpTargets(getTypeInfoFromTypeDefinitionIndex).ToArray().Length == 1)
                        {
                            getTypeInfoFromTypeDefinitionIndex = XrefScannerLowLevel.JumpTargets(getTypeInfoFromTypeDefinitionIndex).Single();
                        }
                    }
                }
            }

            return getTypeInfoFromTypeDefinitionIndex;
        }

        public override IntPtr FindTargetMethod()
        {
            var candidate = FindGetTypeInfoFromTypeDefinitionIndex();
            var resolved = SkipTailCallWrapper(candidate);

            if (resolved != candidate)
                Logger.Instance.LogTrace(
                    "GetTypeInfoFromTypeDefinitionIndex: 0x{From:X} is a tail-call wrapper, using 0x{To:X}",
                    candidate.ToInt64(), resolved.ToInt64());

            return resolved;
        }

        /// <summary>
        /// The xref walk can stop on <c>MetadataCache::GetTypeInfoFromHandle</c>, a thin wrapper that
        /// converts a 64-bit metadata handle to a definition index and tail-jumps into
        /// <c>GetTypeInfoFromTypeDefinitionIndex</c>. Hooking the wrapper is wrong twice over: its
        /// parameter is a pointer where we expect an index, and index-based engine callers bypass it.
        ///
        /// A wrapper is recognised structurally - it contains no conditional branch and no return
        /// before its trailing unconditional jump - so a real lookup function (which always branches
        /// on the invalid index) is left untouched.
        /// </summary>
        private static IntPtr SkipTailCallWrapper(IntPtr candidate)
        {
            var moduleStart = InjectorHelpers.Il2CppModule.BaseAddress.ToInt64();
            var moduleEnd = moduleStart + InjectorHelpers.Il2CppModule.ModuleMemorySize;

            for (var hop = 0; hop < 2; hop++)
            {
                var decoder = XrefScanner.DecoderForAddress(candidate);
                IntPtr tailTarget = IntPtr.Zero;
                var isRealFunction = false;

                for (var i = 0; i < 40; i++)
                {
                    decoder.Decode(out var instruction);
                    if (decoder.LastError == DecoderError.NoMoreBytes) break;
                    if (instruction.Mnemonic == Mnemonic.Int3) break;

                    var flow = instruction.FlowControl;
                    if (flow == FlowControl.Next || flow == FlowControl.Call) continue;

                    if (flow == FlowControl.UnconditionalBranch && !isRealFunction)
                    {
                        if (!instruction.IsJmpShort) tailTarget = (IntPtr)instruction.NearBranch64;
                        break;
                    }

                    isRealFunction = true;
                    break;
                }

                if (isRealFunction || tailTarget == IntPtr.Zero) return candidate;

                var target = tailTarget.ToInt64();
                if (target < moduleStart || target >= moduleEnd) return candidate;

                candidate = tailTarget;
            }

            return candidate;
        }
    }
}
