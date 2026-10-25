# Il2CppInterop — AzurPromilia (CB2)

[![Release](https://img.shields.io/github/v/release/Yoshino-Loli/Il2CppInterop-AzurPromilia-CB2)](https://github.com/Yoshino-Loli/Il2CppInterop-AzurPromilia-CB2/releases)
[![License: LGPL-3.0](https://img.shields.io/badge/license-LGPL--3.0-blue)](LICENSE)

让 [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) 能在《蓝色星原：旅谣》(AzurPromilia) 上正常工作。

验证环境：**Unity 2022.3.62f3** / MelonLoader 0.7.3 / .NET 6.0.36，目标是让 UnityExplorer 能正常注入。

> 补丁基于上游 `f03c8f4`（`1.5.1`，即 MelonLoader 0.7.3 自带的那份）。

---

## 快速安装（用现成的 DLL）

1. 到 [Releases](https://github.com/Yoshino-Loli/Il2CppInterop-AzurPromilia-CB2/releases) 下载 `Il2CppInterop.Runtime.dll` 和 `il2cppinterop.skip.txt`
2. 备份 `<Game>/MelonLoader/net6/Il2CppInterop.Runtime.dll`，然后覆盖它
3. 把 `il2cppinterop.skip.txt` 放到 `<Game>/MelonLoader/net6/`（**这一步不能省**，见下）
4. **再跑一次程序集补丁**（见下一节，这一步也不能省）

三个步骤缺一不可：DLL 修运行时，skip 文件避开 HybridCLR 的 failfast，补丁脚本修程序集元数据。

## HybridCLR 游戏必须加 skip 文件

这游戏用 [HybridCLR](https://github.com/focus-creative-games/hybridclr) 做热更新（`CodeRes/HybridCLR/HotfixDlls/AzurLoader.dll.bytes`）。不加 skip 文件的话，游戏会在**加载热更 DLL 的那一刻**直接消失，日志、crash.txt、exceptions.txt 里什么都没有：

```
Fatal error. Invalid Program: attempted to call a UnmanagedCallersOnly method from managed code.
   at MetadataCache_GetTypeInfoFromTypeDefinitionIndex_Hook.Hook(IntPtr)
   at Class_FromIl2CppType_Hook.Hook(Il2CppType*, Boolean)
```

调用链是 `managed → native → managed` 的重入：

```
managed   Class_FromIl2CppType_Hook.Hook
            └─ Original(type, throwOnError)          回到原生蹦床
native    Class::FromIl2CppType
            └─ 内部调用 MetadataCache::GetTypeInfoFromTypeDefinitionIndex
                                                      这个地址已被打 detour
managed   MetadataCache_GetTypeInfoFromTypeDefinitionIndex_Hook.Hook
```

JIT 在编译第二层托管 hook 时判定其中有一次调用目标是 `[UnmanagedCallersOnly]` 方法，直接 failfast。detour provider 由 MelonLoader 提供，插件侧改不了，只能避开这条重入路径。

`il2cppinterop.skip.txt` 的内容就是一行 `metadata`，即不装 `MetadataCache::GetTypeInfoFromTypeDefinitionIndex` 的 hook。**不影响功能** —— 那个 hook 只用于「用 typeDefinitionIndex 查注入类」，而注入类的 token 是负数，实际走 `Class::FromIl2CppType`。

> 注意不要把 `hooks` 全部跳过。那样连 `Class::FromIl2CppType` 也没了，注入类型彻底无法解析，会在 `[-4+8]` 上直接 AV。

## 程序集后处理（必做）

MelonLoader 生成的 `MelonLoader/Il2CppAssemblies/*.dll` 里有 3 处会让 `Assembly.GetTypes()` 失败。离线看只是可捕获的 `ReflectionTypeLoadException`，但在装好 Il2CppInterop detour 的进程里会升级成**不可捕获的 failfast**，进程直接消失、日志什么都没有。

```
python tools/patch_typeflags.py       <Game>/MelonLoader/Il2CppAssemblies
python tools/patch_il2cpp_typedefs.py <Game>/MelonLoader/Il2CppAssemblies
```

两个脚本都支持 `--dry-run` 预览、传单文件、自动留 `.orig.bak` 备份，并且是幂等的（重复跑无副作用）。

修完之后离线校验应该全绿：

```
OK=186  RTL=0  FAIL=0
```

> **重要**：只要 MelonLoader 重新生成了程序集（换 MelonLoader 版本、游戏更新、删掉 `Il2CppAssemblies`），补丁就会丢，必须重跑。

### 到底修了什么

| 程序集 | 类型 | 缺陷 | 处理 |
|---|---|---|---|
| `UnityEngine.CoreModule` | `LightProbesQueryDisposeJob` (RID 793) | 可见性位声明为 `NestedPublic`，但 `NestedClass` 表里没有对应记录 | 降为 `Public` |
| `UnityEngine.PropertiesModule` | RID 71 / 89 | 同上 | 同上 |
| `UnityEngine.UIElementsModule` | RID 754 | 同上 | 同上 |
| `Il2Cppmscorlib` | `ComTypes.ITypeInfo` / `ITypeLib` (RID 1113/1114) | 非接口类型却带 `TypeAttributes.Import` (0x1000)，CLR 报 *cannot extend from any other type* | 清掉 `Import` 位 |
| `Il2Cpp__Generated` | `Il2Cpp.IActivationFactory` (RID 2) | 普通托管程序集里声明 `TypeAttributes.WindowsRuntime` (0x4000)，CLR 报 *Failure has occurred while loading a type* | 清掉 `WindowsRuntime` 位 |

顺带一提：终末地的 `Il2Cpp__Generated` 里也有完全相同的 `Il2Cpp.IActivationFactory`，离线同样加载失败，只是那边容忍了可捕获异常。

## 运行时修复

### 1. Unity 2022.2+ 的 `Object::New` 会拒绝注入类

这是最隐蔽的一个。`GameObject.AddComponent<T>()` 一路走下去，最后 Unity 只打印一句：

```
The script 'UniverseLib.UniversalBehaviour' could not be instantiated!
```

反汇编 `GameAssembly.dll` 的 `Object::New` 才看到，2022.2+ 在分配对象前多了一段检查：

```asm
mov rax, [rcx]                  ; klass->image
mov rdx, [rax + 0x10]           ; image->assembly
cmp qword ptr [rdx + 0x60], 0
jne  <raise ExecutionEngineException>     ; 非 0 就拒绝
call Class::Init
```

而 Unity 2021.3 的 `Object::New` 完全没有这段，直接 `call Class::Init` —— 所以同一个 DLL 在终末地上好好的。

Il2CppInterop 给注入 assembly 只分配 `0x58` 字节（`Il2CppAssembly_24_2` 的大小），读 `+0x60` 是**越界读到堆垃圾**，非 0 就被判死。

修复：`InjectorHelpers.CreateInjectedAssembly` 改成自己分配 `0x80` 字节全零内存再 `Wrap`，让 `+0x60` 稳定读到 0。对 2021.3 无影响。

### 2. 注入 assembly 的 `image` 字段是 NULL

`CreateInjectedAssembly` 只设了 `InjectedImage.Assembly = InjectedAssembly`，**从来没设反向的 `InjectedAssembly.Image`**。真实 il2cpp 里这两个结构是互相指的：

```
[dump] injected   assembly.image = 0x0             ← NULL
[dump] reference  assembly.image = 0x109C023DA10   ← 非空，回指自己的 image
```

而 `ClassInjector` 给每个注入类都设了 `klass->image = InjectedImage`，所以任何遍历 `assembly → image` 链的代码（HybridCLR 注册热更元数据时就会）碰到注入 assembly 就是空指针。补上 `InjectedAssembly.Image = InjectedImage.ImagePointer`。

### 3. 注入类终结器在空句柄上会炸

`ClassInjector.Finalize` 原来无条件 `GCHandle.FromIntPtr(gcHandle).Free()`。经由 `il2cpp_object_new` 直接分配、没有托管包装的对象，尾部 `InjectedClassData` 是全零，`gcHandle == 0`，在终结器线程上抛异常就是 failfast。加了判空。

## 诊断能力

### stderr 重定向（最重要的一条）

.NET 的 failfast 文本由 runtime 直接 `WriteFile(GetStdHandle(STD_ERROR_HANDLE))`，**既不进 MelonLoader 日志，也不触发 `UnhandledException` 或 VEH**。所以这类崩溃的表现是「日志干净地断掉，什么都没有」。

`CrashGuard` 启动时把 `STD_ERROR_HANDLE` 换成文件句柄：

```csharp
private const int StdErrorHandle = -12;
s_stderrStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
SetStdHandle(StdErrorHandle, s_stderrStream.SafeFileHandle.DangerousGetHandle());
```

于是 failfast 的原文会落在 `<Game>/MelonLoader/net6/il2cppinterop.stderr.txt`。本文档里 HybridCLR 那个 `Fatal error` 就是这样拿到的 —— 之前绕了很多弯路，全是因为看不到这句话。

### 其它

| 文件 / 前缀 | 作用 |
|---|---|
| `il2cppinterop.stderr.txt` | **CLR failfast 原文**（最关键） |
| `il2cppinterop.crash.txt` | 原生异常（VEH 捕获），含 `code` / `at=模块+RVA` / `op=read/write` / `target` |
| `il2cppinterop.exceptions.txt` | 托管首轮异常（含被上层吞掉的） |
| `[inject]` | 每个注入类的 `Il2CppClass` 关键字段 |
| `[hook]` | 各 hook 的命中次数（只打前 3 次） |

觉得 `[hook]` 太吵就在 `il2cppinterop.skip.txt` 里加一行 `nohitlog`。

## 从源码构建

```bash
git clone https://github.com/BepInEx/Il2CppInterop.git
cd Il2CppInterop && git checkout f03c8f4
git apply /path/to/il2cppinterop-azurpromilia.patch
dotnet build -c Release Il2CppInterop.Runtime/Il2CppInterop.Runtime.csproj
# 产物: bin/Il2CppInterop.Runtime/net6.0/Il2CppInterop.Runtime.dll
```

也可以直接 `cp -r src/* Il2CppInterop/` 覆盖（`src/` 里是完整的改动后文件，不只是 diff）。

## 目录

```
src/                       改动后的完整源文件（覆盖到上游仓库同名路径即可）
tools/                     程序集后处理脚本 + 元数据检查器
  mdcheck.py               CLI 元数据解析/完整性校验（其它脚本的依赖）
  patch_typeflags.py       TypeDef 可见性 / NestedClass 一致性
  patch_il2cpp_typedefs.py Import 位 / WindowsRuntime 位
  re/                      排查用的 PE 工具（字符串 xref、.pdata 函数边界、调用者扫描）
il2cppinterop.skip.txt     放到 MelonLoader/net6/，避开 HybridCLR 的 failfast（必做）
il2cppinterop-azurpromilia.patch   git apply 用的补丁
```

## 已知限制

- **依赖 MelonLoader 的 detour provider**：HybridCLR 那条重入路径的根因在 detour 跳板的实现里，插件侧只能靠 skip 文件绕开。如果以后 MelonLoader 修了这个问题，`metadata` 那行就可以去掉。
- **程序集补丁会被覆盖**：MelonLoader 重新生成 `Il2CppAssemblies` 后（换版本 / 游戏更新 / 删目录）必须重跑两个 patch 脚本。
- **不兼容原版 exe**：本仓库只负责 il2cpp 层。要让客户端连私服还得配合 Evergreen 之类的 exe 补丁，那是另一件事。

## 许可

与上游一致，LGPL-3.0-only。见 [LICENSE](LICENSE)。
