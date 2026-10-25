using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Extensions;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.Injection.Hooks;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Assembly;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.FieldInfo;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Image;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;
using Il2CppInterop.Runtime.Startup;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Injection
{
    internal static unsafe class InjectorHelpers
    {
        internal static Assembly Il2CppMscorlib = typeof(Il2CppSystem.Type).Assembly;
        internal static INativeAssemblyStruct InjectedAssembly;
        internal static INativeImageStruct InjectedImage;
        internal static ProcessModule Il2CppModule = Process.GetCurrentProcess()
            .Modules.OfType<ProcessModule>()
            .Single((x) => x.ModuleName is "GameAssembly.dll" or "GameAssembly.so" or "UserAssembly.dll");

        internal static IntPtr Il2CppHandle = NativeLibrary.Load("GameAssembly", typeof(InjectorHelpers).Assembly, null);

        internal static readonly Dictionary<Type, OpCode> StIndOpcodes = new()
        {
            [typeof(byte)] = OpCodes.Stind_I1,
            [typeof(sbyte)] = OpCodes.Stind_I1,
            [typeof(bool)] = OpCodes.Stind_I1,
            [typeof(short)] = OpCodes.Stind_I2,
            [typeof(ushort)] = OpCodes.Stind_I2,
            [typeof(int)] = OpCodes.Stind_I4,
            [typeof(uint)] = OpCodes.Stind_I4,
            [typeof(long)] = OpCodes.Stind_I8,
            [typeof(ulong)] = OpCodes.Stind_I8,
            [typeof(float)] = OpCodes.Stind_R4,
            [typeof(double)] = OpCodes.Stind_R8
        };

        /// <summary>
        /// Unity 2022.2+ widened the prologue of <c>Object::New</c>: before allocating it evaluates
        /// <c>[klass->image->assembly + 0x60]</c> and raises an <c>ExecutionEngineException</c> when
        /// that word is non-zero. Unity 2021.3 has no such check, which is why injected classes could
        /// still be instantiated there. The struct Il2CppInterop allocates for the injected assembly
        /// stops at 0x58 bytes, so on 2022.2+ that read lands on unrelated heap memory. Give the
        /// allocation a zeroed tail so the check observes what a real assembly reports.
        /// </summary>
        private const int InjectedAssemblyAllocationSize = 0x80;

        private static void CreateInjectedAssembly()
        {
            unsafe
            {
                var assemblyMemory = Marshal.AllocHGlobal(InjectedAssemblyAllocationSize);
                for (var i = 0; i < InjectedAssemblyAllocationSize; i++)
                    ((byte*)assemblyMemory)[i] = 0;

                InjectedAssembly = UnityVersionHandler.Wrap((Il2CppAssembly*)assemblyMemory);
            }

            InjectedImage = UnityVersionHandler.NewImage();

            InjectedAssembly.Name.Name = Marshal.StringToCoTaskMemUTF8("InjectedMonoTypes");

            // A real Il2CppAssembly always points back at its image; this one used to be left NULL.
            // Anything that walks the assembly -> image chain (HybridCLR registering hotfix metadata,
            // for one) dereferences it, so keep the two structures mutually consistent.
            InjectedAssembly.Image = InjectedImage.ImagePointer;

            InjectedImage.Assembly = InjectedAssembly.AssemblyPointer;
            InjectedImage.Dynamic = 1;
            InjectedImage.Name = InjectedAssembly.Name.Name;
            if (InjectedImage.HasNameNoExt)
                InjectedImage.NameNoExt = InjectedAssembly.Name.Name;
        }

        private static readonly GenericMethod_GetMethod_Hook GenericMethodGetMethodHook = new();
        private static readonly GenericMethod_GetMethod_Unity6_Hook GenericMethodGetMethodHook_Unity6 = new();
        private static readonly MetadataCache_GetTypeInfoFromTypeDefinitionIndex_Hook GetTypeInfoFromTypeDefinitionIndexHook = new();
        private static readonly Class_GetFieldDefaultValue_Hook GetFieldDefaultValueHook = new();
        private static readonly Class_FromIl2CppType_Hook FromIl2CppTypeHook = new();
        private static readonly Class_FromName_Hook FromNameHook = new();
        internal static void Setup()
        {
            Logger.Instance.LogInformation("[startup] InjectorHelpers.Setup: begin (skips: {Skips})",
                s_Skips.Count == 0 ? "none" : string.Join(",", s_Skips));

            if (Skip("all"))
            {
                Logger.Instance.LogInformation("[startup] Setup skipped entirely via il2cppinterop.skip.txt");
                return;
            }

            if (Skip("typedump")) DumpAssemblyTypes();
            if (s_Skips.Any(token => token.StartsWith("typedump2", StringComparison.OrdinalIgnoreCase)))
                DumpTypesByToken();

            if (InjectedAssembly == null && !Skip("assembly")) CreateInjectedAssembly();
            Logger.Instance.LogInformation("[startup] injected assembly/image created");

            if (!SkipHooks("genericmethod"))
            {
                if (Il2CppInteropRuntime.Instance.UnityVersion.Major >= 6000)
                    GenericMethodGetMethodHook_Unity6.ApplyHook();
                else
                    GenericMethodGetMethodHook.ApplyHook();
            }
            Logger.Instance.LogInformation("[startup] GenericMethod::GetMethod step done");

            if (!SkipHooks("metadata")) GetTypeInfoFromTypeDefinitionIndexHook.ApplyHook();
            Logger.Instance.LogInformation("[startup] MetadataCache::GetTypeInfoFromTypeDefinitionIndex step done");

            if (!SkipHooks("fielddefault")) GetFieldDefaultValueHook.ApplyHook();
            Logger.Instance.LogInformation("[startup] Class::GetFieldDefaultValue step done");

            if (!Skip("classinit")) ClassInit ??= FindClassInit();
            Logger.Instance.LogInformation("[startup] Class::Init = 0x{Address}",
                ClassInitAddress.ToInt64().ToString("X2"));

            if (!SkipHooks("fromtype")) FromIl2CppTypeHook.ApplyHook();
            Logger.Instance.LogInformation("[startup] Class::FromIl2CppType step done");

            if (!SkipHooks("fromname")) FromNameHook.ApplyHook();
            Logger.Instance.LogInformation("[startup] Class::FromName step done; Setup complete");
        }

        /// <summary>
        /// Diagnostic escape hatch: <c>MelonLoader/net6/il2cppinterop.skip.txt</c> holds one token per
        /// line. Lets a broken startup be bisected without rebuilding.
        /// Tokens: <c>all</c>, <c>assembly</c>, <c>hooks</c>, <c>genericmethod</c>, <c>metadata</c>,
        /// <c>fielddefault</c>, <c>classinit</c>, <c>fromtype</c>, <c>fromname</c>.
        /// </summary>
        private static readonly HashSet<string> s_Skips = LoadSkips();

        private static HashSet<string> LoadSkips()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dir = Path.GetDirectoryName(typeof(InjectorHelpers).Assembly.Location);
                if (string.IsNullOrEmpty(dir)) return set;

                var path = Path.Combine(dir, "il2cppinterop.skip.txt");
                if (!File.Exists(path)) return set;

                foreach (var raw in File.ReadAllLines(path))
                {
                    var token = raw.Trim();
                    if (token.Length == 0 || token.StartsWith("#")) continue;
                    set.Add(token);
                }
            }
            catch
            {
                // A malformed skip file must never break startup.
            }

            return set;
        }

        private static bool Skip(string name) => s_Skips.Contains(name);

        /// <summary>
        /// Diagnostic action (enable with the <c>typedump</c> token): walks every loaded assembly and
        /// calls <see cref="Assembly.GetTypes"/>, recording each name to its own file before the call.
        ///
        /// UniverseLib caches types across all assemblies at startup and can die inside
        /// <c>GetTypes</c> with an uncatchable CLR fatal error, which leaves no clue as to which
        /// assembly is responsible. Writing the name first turns that into a plain last-line answer.
        /// The file is flushed per line on purpose - MelonLoader's log buffer does not survive a
        /// fatal error.
        /// </summary>
        private static void DumpAssemblyTypes()
        {
            string path = null;
            try
            {
                var dir = Path.GetDirectoryName(typeof(InjectorHelpers).Assembly.Location);
                if (!string.IsNullOrEmpty(dir))
                    path = Path.Combine(dir, "il2cppinterop.typedump.txt");
            }
            catch
            {
                // Fall through with path == null and skip the file, still logging normally.
            }

            void Line(string text)
            {
                Logger.Instance.LogInformation("[typedump] {Text}", text);
                if (path == null) return;
                try
                {
                    File.AppendAllText(path, text + Environment.NewLine);
                }
                catch
                {
                    // Ignore: the log line above is the fallback.
                }
            }

            try
            {
                if (path != null) File.WriteAllText(path, string.Empty);

                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                Line($"assemblies={assemblies.Length}");

                foreach (var assembly in assemblies)
                {
                    var name = assembly.GetName().Name ?? "<unnamed>";
                    Line($"BEGIN {name}");

                    try
                    {
                        var types = assembly.GetTypes();
                        Line($"  ok types={types.Length}");
                    }
                    catch (Exception ex)
                    {
                        Line($"  FAILED {ex.GetType().Name}: {ex.Message}");
                    }
                }

                Line("DONE");
            }
            catch (Exception ex)
            {
                Line($"typedump aborted: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Diagnostic action (enable with <c>typedump2</c>, or <c>typedump2:NameA,NameB</c> to pick the
        /// assemblies): resolves one metadata token at a time and flushes a <c>TRY</c> line before each
        /// resolve, so a CLR fatal error leaves the offending TypeDef token as the last line of the file.
        ///
        /// <c>Assembly.GetTypes</c> is all-or-nothing - a single bad TypeDef kills the process and tells
        /// us nothing. Walking 0x02000001, 0x02000002, ... turns that into an exact answer. The token is
        /// written before the resolve on purpose: the process is already dead when the next line would
        /// have been written.
        /// </summary>
        private static void DumpTypesByToken()
        {
            string path = null;
            StreamWriter writer = null;
            try
            {
                var dir = Path.GetDirectoryName(typeof(InjectorHelpers).Assembly.Location);
                if (!string.IsNullOrEmpty(dir))
                    path = Path.Combine(dir, "il2cppinterop.typedump2.txt");
            }
            catch
            {
                // Fall through with path == null; only the logger remains.
            }

            void FileLine(string text)
            {
                if (writer == null) return;
                try
                {
                    writer.WriteLine(text);
                }
                catch
                {
                    // Ignore: losing a line is better than breaking startup.
                }
            }

            void Line(string text)
            {
                Logger.Instance.LogInformation("[typedump2] {Text}", text);
                FileLine(text);
            }

            var targets = new List<string>();
            foreach (var token in s_Skips)
            {
                if (!token.StartsWith("typedump2", StringComparison.OrdinalIgnoreCase)) continue;
                var rest = token.Length > "typedump2".Length
                    ? token.Substring("typedump2".Length).TrimStart(':')
                    : string.Empty;
                foreach (var part in rest.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length != 0) targets.Add(trimmed);
                }
            }
            if (targets.Count == 0) targets.Add("UnityEngine.CoreModule");

            const BindingFlags allDeclared = BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.Instance | BindingFlags.Static |
                                             BindingFlags.DeclaredOnly;

            try
            {
                // AutoFlush keeps every line on disk before the next runtime call - a CLR fatal error
                // never unwinds, so buffered output would be lost exactly when it matters.
                writer = path == null
                    ? null
                    : new StreamWriter(path, false) { AutoFlush = true };

                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                Line($"assemblies={assemblies.Length} targets={string.Join(",", targets)}");

                foreach (var assembly in assemblies)
                {
                    var name = assembly.GetName().Name ?? "<unnamed>";
                    if (!targets.Any(target => string.Equals(target, name, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    Line($"BEGIN {name}");

                    Module[] modules;
                    try
                    {
                        modules = assembly.GetModules(false);
                    }
                    catch (Exception ex)
                    {
                        Line($"  MODULES FAILED {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    foreach (var module in modules)
                    {
                        Line($"  MODULE {module.Name}");
                        var consecutiveMisses = 0;
                        for (var index = 1; index <= 16384; index++)
                        {
                            var metadataToken = 0x02000000 | index;
                            FileLine($"  TRY 0x{metadataToken:X8}");
                            try
                            {
                                var type = module.ResolveType(metadataToken);
                                FileLine($"  RES 0x{metadataToken:X8} {type.FullName}");
                                var fieldCount = type.GetFields(allDeclared).Length;
                                FileLine($"  FLD 0x{metadataToken:X8} {fieldCount}");
                                var methodCount = type.GetMethods(allDeclared).Length;
                                FileLine($"  OK  0x{metadataToken:X8} methods={methodCount}");
                                consecutiveMisses = 0;
                            }
                            catch (ArgumentException)
                            {
                                // Past the end of the TypeDef table; give it a few tries in case of holes.
                                if (++consecutiveMisses >= 8)
                                {
                                    Line($"  end of TypeDef table at 0x{metadataToken:X8}");
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Line($"  ERR 0x{metadataToken:X8} {ex.GetType().Name}: {ex.Message}");
                                consecutiveMisses = 0;
                            }
                        }
                    }
                }

                Line("DONE");
            }
            catch (Exception ex)
            {
                Line($"typedump2 aborted: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try
                {
                    writer?.Flush();
                    writer?.Dispose();
                }
                catch
                {
                }
            }
        }

        private static bool SkipHooks(string name) => s_Skips.Contains("hooks") || s_Skips.Contains(name);

        private static readonly ConcurrentDictionary<string, int> s_HookHits = new();

        /// <summary>
        /// Diagnostic aid: records how often a detour is entered and logs the first few calls, so a
        /// silent crash can be attributed to the patched function the engine was in the middle of.
        /// Disable with the <c>nohitlog</c> token.
        /// </summary>
        internal static void LogHookHit(string name)
        {
            if (s_Skips.Contains("nohitlog")) return;

            var hits = s_HookHits.AddOrUpdate(name, 1, (_, previous) => previous + 1);
            if (hits <= 3 || hits % 500 == 0)
                Logger.Instance.LogInformation("[hook] {Name} entered (#{Hits})", name, hits);
        }

        internal static long CreateClassToken(IntPtr classPointer)
        {
            long newToken = Interlocked.Decrement(ref s_LastInjectedToken);
            s_InjectedClasses[newToken] = classPointer;
            return newToken;
        }

        internal static void AddTypeToLookup<T>(IntPtr typePointer) where T : class => AddTypeToLookup(typeof(T), typePointer);
        internal static void AddTypeToLookup(Type type, IntPtr typePointer)
        {
            string klass = type.Name;
            if (klass == null) return;
            string namespaze = type.Namespace ?? string.Empty;
            var attribute = Attribute.GetCustomAttribute(type, typeof(Il2CppInterop.Runtime.Attributes.ClassInjectionAssemblyTargetAttribute)) as Il2CppInterop.Runtime.Attributes.ClassInjectionAssemblyTargetAttribute;

            foreach (IntPtr image in (attribute is null) ? IL2CPP.GetIl2CppImages() : attribute.GetImagePointers())
            {
                s_ClassNameLookup.Add((namespaze, klass, image), typePointer);
            }
        }

        internal static IntPtr GetIl2CppExport(string name)
        {
            if (!TryGetIl2CppExport(name, out var address))
            {
                throw new NotSupportedException($"Couldn't find {name} in {Il2CppModule.ModuleName}'s exports");
            }

            return address;
        }

        internal static bool TryGetIl2CppExport(string name, out IntPtr address)
        {
            return NativeLibrary.TryGetExport(Il2CppHandle, name, out address);
        }

        internal static IntPtr GetIl2CppMethodPointer(MethodBase proxyMethod)
        {
            if (proxyMethod == null) return IntPtr.Zero;

            FieldInfo methodInfoPointerField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(proxyMethod);
            if (methodInfoPointerField == null)
                throw new ArgumentException($"Couldn't find the generated method info pointer for {proxyMethod.Name}");

            // Il2CppClassPointerStore calls the static constructor for the type
            Il2CppClassPointerStore.GetNativeClassPointer(proxyMethod.DeclaringType);

            IntPtr methodInfoPointer = (IntPtr)methodInfoPointerField.GetValue(null);
            if (methodInfoPointer == IntPtr.Zero)
                throw new ArgumentException($"Generated method info pointer for {proxyMethod.Name} doesn't point to any il2cpp method info");
            INativeMethodInfoStruct methodInfo = UnityVersionHandler.Wrap((Il2CppMethodInfo*)methodInfoPointer);
            return methodInfo.MethodPointer;
        }

        private static long s_LastInjectedToken = -2;
        internal static readonly ConcurrentDictionary<long, IntPtr> s_InjectedClasses = new();
        /// <summary> (namespace, class, image) : class </summary>
        internal static readonly Dictionary<(string _namespace, string _class, IntPtr imagePtr), IntPtr> s_ClassNameLookup = new();

        #region Class::Init
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void d_ClassInit(Il2CppClass* klass);
        internal static d_ClassInit ClassInit;

        /// <summary>Address <see cref="ClassInit"/> was resolved to; kept for diagnostics.</summary>
        internal static IntPtr ClassInitAddress;

        private static readonly MemoryUtils.SignatureDefinition[] s_ClassInitSignatures =
        {
            new MemoryUtils.SignatureDefinition
            {
                pattern = "\xE8\x00\x00\x00\x00\x0F\xB7\x47\x28\x83",
                mask = "x????xxxxx",
                xref = true
            },
            new MemoryUtils.SignatureDefinition
            {
                pattern = "\xE8\x00\x00\x00\x00\x0F\xB7\x47\x48\x48",
                mask = "x????xxxxx",
                xref = true
            }
        };

        private static d_ClassInit FindClassInit()
        {
            static nint GetClassInitSubstitute()
            {
                if (TryGetIl2CppExport("mono_class_instance_size", out nint classInit))
                {
                    Logger.Instance.LogTrace("Picked mono_class_instance_size as a Class::Init substitute");
                    return classInit;
                }
                if (TryGetIl2CppExport("mono_class_setup_vtable", out classInit))
                {
                    Logger.Instance.LogTrace("Picked mono_class_setup_vtable as a Class::Init substitute");
                    return classInit;
                }
                if (TryGetIl2CppExport(nameof(IL2CPP.il2cpp_class_has_references), out classInit))
                {
                    Logger.Instance.LogTrace("Picked il2cpp_class_has_references as a Class::Init substitute");
                    return classInit;
                }

                Logger.Instance.LogTrace("GameAssembly.dll: 0x{Il2CppModuleAddress}", Il2CppModule.BaseAddress.ToInt64().ToString("X2"));
                throw new NotSupportedException("Failed to use signature for Class::Init and a substitute cannot be found, please create an issue and report your unity version & game");
            }
            nint pClassInit = s_ClassInitSignatures
                .Select(s => MemoryUtils.FindSignatureInModule(Il2CppModule, s))
                .FirstOrDefault(p => p != 0);

            if (pClassInit != 0)
                Logger.Instance.LogInformation("[startup] Class::Init from byte signature");

            if (pClassInit == 0)
                pClassInit = FindClassInitFromExport();

            if (pClassInit == 0)
            {
                Logger.Instance.LogWarning("Class::Init signatures have been exhausted, using a substitute!");
                pClassInit = GetClassInitSubstitute();
            }

            Logger.Instance.LogTrace("Class::Init: 0x{PClassInitAddress}", pClassInit.ToString("X2"));
            ClassInitAddress = pClassInit;

            return Marshal.GetDelegateForFunctionPointer<d_ClassInit>(pClassInit);
        }

        /// <summary>
        /// In every il2cpp build <c>il2cpp_class_has_references</c> is
        /// <c>if (!klass->size_inited) Class::Init(klass); return klass->has_references;</c>,
        /// so the first call inside that export is Class::Init. The byte signatures above only
        /// match the exact instruction sequences of the builds they were written against; a
        /// runtime that was recompiled or patched changes the surrounding instructions while the
        /// call itself stays. This anchors on the export instead of on code shape.
        /// </summary>
        private static nint FindClassInitFromExport()
        {
            if (!TryGetIl2CppExport(nameof(IL2CPP.il2cpp_class_has_references), out var exportAddress) ||
                exportAddress == IntPtr.Zero)
                return 0;

            var moduleStart = Il2CppModule.BaseAddress.ToInt64();
            var moduleEnd = moduleStart + Il2CppModule.ModuleMemorySize;

            // Large il2cpp builds routinely export a bare `jmp` stub instead of the function body
            // (Azur Promilia's GameAssembly.dll does exactly that), so scanning at the export
            // address finds no `call` at all. Follow the stub first.
            var bodyAddress = FollowJumpStub(exportAddress, moduleStart, moduleEnd);
            if (bodyAddress == IntPtr.Zero)
                return 0;

            var code = (byte*)bodyAddress;

            // The call sits within the first handful of instructions; don't wander into the next function.
            for (var offset = 0; offset < 0x60; offset++)
            {
                if (code[offset] != 0xE8) continue; // call rel32
                var target = (long)bodyAddress + offset + 5 + *(int*)(code + offset + 1);
                if (target <= moduleStart || target >= moduleEnd) continue;
                if (!LooksLikeFunctionStart((byte*)target)) continue;

                Logger.Instance.LogTrace("Class::Init resolved from il2cpp_class_has_references: 0x{Address}",
                    target.ToString("X2"));
                return (nint)target;
            }

            return 0;
        }

        /// <summary>
        /// Resolves the common MSVC export stub shapes to the function body: <c>jmp rel32</c>,
        /// <c>jmp qword ptr [rip+disp32]</c> and <c>mov rax, imm64; jmp rax</c>. Returns zero when
        /// the chain leaves the module or does not terminate within a few hops.
        /// </summary>
        private static nint FollowJumpStub(nint address, long moduleStart, long moduleEnd)
        {
            var current = (long)address;

            for (var hop = 0; hop < 4; hop++)
            {
                if (current < moduleStart || current + 16 > moduleEnd)
                    return IntPtr.Zero;

                var code = (byte*)current;
                long next;

                if (code[0] == 0xE9) // jmp rel32
                {
                    next = current + 5 + *(int*)(code + 1);
                }
                else if (code[0] == 0xFF && code[1] == 0x25) // jmp qword ptr [rip + disp32]
                {
                    var slot = current + 6 + *(int*)(code + 2);
                    if (slot < moduleStart || slot + sizeof(long) > moduleEnd) return IntPtr.Zero;
                    next = *(long*)slot;
                }
                else if (code[0] == 0x48 && code[1] == 0xB8 && code[10] == 0xFF && code[11] == 0xE0)
                {
                    next = *(long*)(code + 2); // mov rax, imm64; jmp rax
                }
                else
                {
                    return (nint)current; // already at the body
                }

                if (next < moduleStart || next >= moduleEnd)
                    return IntPtr.Zero;

                current = next;
            }

            return IntPtr.Zero;
        }

        private static bool LooksLikeFunctionStart(byte* code)
        {
            // il2cpp prologues start with a push (0x40/0x48/0x4C REX-prefixed push, 0x53 push rbx,
            // 0x55 push rbp, 0x56/0x57 push rsi/rdi) or a REX.W mov.
            return code[0] is 0x40 or 0x48 or 0x4C or 0x53 or 0x55 or 0x56 or 0x57;
        }
        #endregion
    }
}
