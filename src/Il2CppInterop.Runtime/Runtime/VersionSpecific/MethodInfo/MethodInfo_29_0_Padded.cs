using System;
using System.Runtime.InteropServices;
namespace Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo
{
    /// <summary>
    /// 2021.2+ (metadata v29) <see cref="Il2CppMethodInfo"/> layout for runtimes that carry an
    /// extra byte between <c>slot</c> and <c>parameters_count</c>.
    ///
    /// Stock Unity puts <c>parameters_count</c> at 0x52 and the bitfield at 0x53; the shipped
    /// Endfield runtime puts them at 0x53 / 0x54, as read straight out of GameAssembly.dll:
    ///
    ///   il2cpp_method_get_param_count -> movzx eax, byte ptr [rcx+0x53]
    ///   il2cpp_method_is_generic      -> movzx eax, byte ptr [rcx+0x54]; and al, 1
    ///   il2cpp_method_get_token       -> mov eax, dword ptr [rcx+0x48]   (unchanged)
    ///
    /// Everything up to and including <c>slot</c> is identical to stock, and the struct still
    /// rounds up to 0x58 bytes, so allocation sizes are unchanged.
    /// Selected by <see cref="UnityVersionHandler"/> after probing the live module, not through
    /// <see cref="ApplicableToUnityVersionsSinceAttribute"/>.
    /// </summary>
    public unsafe class NativeMethodInfoStructHandler_29_0_Padded : INativeMethodInfoStructHandler
    {
        public int Size() => sizeof(Il2CppMethodInfo_29_0_Padded);

        public INativeMethodInfoStruct CreateNewStruct()
        {
            IntPtr ptr = Marshal.AllocHGlobal(Size());
            Il2CppMethodInfo_29_0_Padded* _ = (Il2CppMethodInfo_29_0_Padded*)ptr;
            *_ = default;
            return new NativeStructWrapper(ptr);
        }

        public INativeMethodInfoStruct Wrap(Il2CppMethodInfo* ptr)
        {
            if (ptr == null) return null;
            return new NativeStructWrapper((IntPtr)ptr);
        }

        internal unsafe struct Il2CppMethodInfo_29_0_Padded
        {
            public void* methodPointer;
            public void* virtualMethodPointer;
            public void* invoker_method;
            public byte* name;
            public Il2CppClass* klass;
            public Il2CppTypeStruct* return_type;
            public Il2CppTypeStruct** parameters;
            public void* runtime_data;
            public void* generic_data;
            public uint token;
            public ushort flags;
            public ushort iflags;
            public ushort slot;
            // The byte that stock Unity does not have.
            public byte extra_field_after_slot;
            public byte parameters_count;
            public Bitfield0 _bitfield0;

            internal enum Bitfield0 : byte
            {
                BIT_is_generic = 0,
                is_generic = (1 << BIT_is_generic),
                BIT_is_inflated = 1,
                is_inflated = (1 << BIT_is_inflated),
                BIT_wrapper_type = 2,
                wrapper_type = (1 << BIT_wrapper_type),
                BIT_has_full_generic_sharing_signature = 3,
                has_full_generic_sharing_signature = (1 << BIT_has_full_generic_sharing_signature),
                BIT_indirect_call_via_invokers = 4,
                indirect_call_via_invokers = (1 << BIT_indirect_call_via_invokers),
            }
        }

        internal class NativeStructWrapper : INativeMethodInfoStruct
        {
            public NativeStructWrapper(IntPtr ptr) => Pointer = ptr;

            private static readonly int _bitfield0offset = Marshal.OffsetOf<Il2CppMethodInfo_29_0_Padded>(nameof(Il2CppMethodInfo_29_0_Padded._bitfield0)).ToInt32();

            public IntPtr Pointer { get; }
            private Il2CppMethodInfo_29_0_Padded* _ => (Il2CppMethodInfo_29_0_Padded*)Pointer;
            public Il2CppMethodInfo* MethodInfoPointer => (Il2CppMethodInfo*)Pointer;
            public ref IntPtr Name => ref *(IntPtr*)&_->name;
            public ref ushort Slot => ref _->slot;
            public ref IntPtr MethodPointer => ref *(IntPtr*)&_->methodPointer;
            public ref IntPtr VirtualMethodPointer => ref *(IntPtr*)&_->virtualMethodPointer;
            public ref Il2CppClass* Class => ref _->klass;
            public ref IntPtr InvokerMethod => ref *(IntPtr*)&_->invoker_method;
            public ref Il2CppTypeStruct* ReturnType => ref _->return_type;
            public ref Il2CppMethodFlags Flags => ref *(Il2CppMethodFlags*)&_->flags;
            public ref byte ParametersCount => ref _->parameters_count;
            public ref Il2CppParameterInfo* Parameters => ref *(Il2CppParameterInfo**)&_->parameters;
            public ref uint Token => ref _->token;

            public bool IsGeneric
            {
                get => this.CheckBit(_bitfield0offset, (int)Il2CppMethodInfo_29_0_Padded.Bitfield0.BIT_is_generic);
                set => this.SetBit(_bitfield0offset, (int)Il2CppMethodInfo_29_0_Padded.Bitfield0.BIT_is_generic, value);
            }

            public bool IsInflated
            {
                get => this.CheckBit(_bitfield0offset, (int)Il2CppMethodInfo_29_0_Padded.Bitfield0.BIT_is_inflated);
                set => this.SetBit(_bitfield0offset, (int)Il2CppMethodInfo_29_0_Padded.Bitfield0.BIT_is_inflated, value);
            }

            public bool IsMarshalledFromNative
            {
                get => false;
                set { }
            }
        }
    }
}
