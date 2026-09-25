// =========================================================================
// ProjectTemplates.cs
// armcli init 이 생성하는 파일들의 템플릿 모음
// =========================================================================

internal static class ZigTemplates
{
    public static string BuildZig(string projectName, bool withRust, bool withGo, bool withDotnet, bool isMacOS)
    {
        string dotnetRid = isMacOS ? "osx-arm64" : "linux-arm64";
        // macOS에서는 접두사 없는 "DotnetLibs.dylib"로 나오는 걸 hun-build.cs로 확인함.
        // Linux는 .NET Native AOT NativeLib=Shared 게시물이 배포판/버전에 따라 "libDotnetLibs.so"로
        // 나올 수 있으니, 실제 publish 결과 파일명을 확인하고 다르면 이 값만 바꿔주면 된다.
        string dotnetLibFile = isMacOS ? "DotnetLibs.dylib" : "libDotnetLibs.so";
        // zig 0.16: Compile.addRPath()는 사라지고 root_module.addRPathSpecial(토큰)으로 대체됨.
        // 이 토큰은 "실행 파일 자신을 기준으로" 찾으라는 뜻이라, 라이브러리를 zig-out/bin 에
        // 실제로 복사해둬야 동작한다 (아래 install_dotnet_lib 스텝이 그 역할).
        string rpathToken = isMacOS ? "@executable_path" : "$ORIGIN";

        string rustBlock = !withRust ? "" : $$"""

        // --- Rust 라이브러리 (app/RustLibs/rust_core) ---
        const rust_build = b.addSystemCommand(&.{
            "cargo",
            "build",
            "--release",
            "--manifest-path",
            "app/RustLibs/rust_core/Cargo.toml",
        });
        exe.step.dependOn(&rust_build.step);
        exe.root_module.addObjectFile(b.path("app/RustLibs/rust_core/target/release/librust_core.a"));
    """;

        string goBlock = !withGo ? "" : $$"""

        // --- Go 라이브러리 (app/GoLibs) — c-archive 정적 라이브러리로 빌드 ---
        // NOTE: go build 는 cwd 기준으로 go.mod 를 찾으므로, app/GoLibs 안에서 실행해야 한다.
        const go_out_dir = "app/GoLibs/out";
        const go_build = b.addSystemCommand(&.{
            "go",
            "build",
            "-buildmode=c-archive",
            "-o",
            "out/libgolibs.a",
            ".",
            });
        go_build.setCwd(b.path("app/GoLibs"));
        exe.step.dependOn(&go_build.step);
        exe.root_module.addObjectFile(b.path(go_out_dir ++ "/libgolibs.a"));
    """;

        // macOS 전용: .NET Native AOT dylib의 기본 install name(LC_ID_DYLIB)엔 @rpath 접두사가
        // 없어서 그대로면 dyld가 rpath 검색을 안 한다. hun-build.cs의 install_name_tool 보정과 동일한 처리.
        string dotnetLastStep = isMacOS ? "dotnet_fix_install_name" : "dotnet_build";
        string dotnetInstallNameFixDecl = !isMacOS ? "" : $$"""

        const dotnet_fix_install_name = b.addSystemCommand(&.{
            "install_name_tool", "-id", "@rpath/{{dotnetLibFile}}", "{{dotnetLibFile}}",
        });
        dotnet_fix_install_name.setCwd(b.path(dotnet_publish_dir));
        dotnet_fix_install_name.step.dependOn(&dotnet_build.step);
    """;

        string dotnetBlock = !withDotnet ? "" : $$"""

        // --- .NET Native AOT 라이브러리 (app/DotnetLibs) ---
        // NOTE: 대상 RID(osx-arm64/linux-arm64)에 맞춰 게시 경로가 달라진다.
        //       파일명({{dotnetLibFile}})이 실제 publish 결과와 다르면 여기만 고쳐주면 된다.
        const dotnet_build = b.addSystemCommand(&.{
            "dotnet",
            "publish",
            "app/DotnetLibs/DotnetLibs.csproj",
            "-c",
            "Release",
            "-r",
            "{{dotnetRid}}",
        });

        const dotnet_publish_dir = "app/DotnetLibs/bin/Release/net10.0/{{dotnetRid}}/publish";
        const dotnet_lib_path = b.path(dotnet_publish_dir ++ "/{{dotnetLibFile}}");
    {{dotnetInstallNameFixDecl}}
        exe.step.dependOn(&{{dotnetLastStep}}.step);
        exe.root_module.addObjectFile(dotnet_lib_path);
        // 실행 파일과 같은 폴더(zig-out/bin)에서 라이브러리를 찾도록 rpath 토큰 등록
        exe.root_module.addRPathSpecial("{{rpathToken}}");

        // 설치 시 dylib/so 를 실행 파일과 같은 폴더로 복사 (위 rpath는 실행 파일 기준 상대경로라 필수)
        const install_dotnet_lib = b.addInstallFileWithDir(dotnet_lib_path, .bin, "{{dotnetLibFile}}");
        install_dotnet_lib.step.dependOn(&{{dotnetLastStep}}.step);
        b.getInstallStep().dependOn(&install_dotnet_lib.step);
    """;

        return $$"""
    const std = @import("std");

    // {{projectName}} — Zig 오케스트레이터
    // 어셈블리(src/Main.S)를 뼈대로 삼고, 필요한 언어별 라이브러리를 빌드해 링크한다.
    // (armcli init 으로 생성됨 — zig 0.16 기준, 다른 버전에서는 API가 다를 수 있으니 확인해줘)

    pub fn build(b: *std.Build) void {
        const target = b.standardTargetOptions(.{});
        const optimize = b.standardOptimizeOption(.{});

        const exe = b.addExecutable(.{
            .name = "{{projectName}}",
            .root_module = b.createModule(.{
                .target = target,
                .optimize = optimize,
            }),
        });

        // --- 어셈블리 진입점 (src/Main.S) ---
        exe.root_module.addCSourceFile(.{
            .file = b.path("src/Main.S"),
            .flags = &.{},
        });
        exe.root_module.link_libc = true;
    {{rustBlock}}{{goBlock}}{{dotnetBlock}}

        b.installArtifact(exe);

        const run_cmd = b.addRunArtifact(exe);
        run_cmd.step.dependOn(b.getInstallStep());
        if (b.args) |args| {
            run_cmd.addArgs(args);
        }

        const run_step = b.step("run", "{{projectName}} 실행");
        run_step.dependOn(&run_cmd.step);
    }
    """;
    }
}

internal static class RustTemplates
{
    public static string CargoToml(string projectName) => $"""
    [package]
    name = "rust_core"
    version = "0.1.0"
    edition = "2021"
    description = "{projectName} 용 Rust 코어 라이브러리 (armcli init 생성)"

    [lib]
    name = "rust_core"
    crate-type = ["staticlib"]

    [dependencies]
    """;

    public static string LibRs() => """
    mod console;
    pub use console::*;

    /// 두 정수를 더한 합계를 반환한다.
    /// ARM64 호출 규약: x0 = a, x1 = b 로 전달받고, 결과를 x0 으로 반환한다.
    #[no_mangle]
    pub extern "C" fn add_two_numbers(a: i64, b: i64) -> i64 {
        a + b
    }
    """;

    public static string ConsoleRs() => """
    use std::ffi::CStr;
    use std::os::raw::c_char;

    /// 기본 인사 함수 — 프로젝트 스캐폴딩이 잘 연결됐는지 확인용
    #[no_mangle]
    pub extern "C" fn rust_hello() {
        println!("Hello from Rust (rust_core)!");
    }

    /// 어셈블리/다른 언어에서 넘어온 널 종단 C 문자열을 println! 으로 출력한다.
    /// 안전하지 않은 포인터 역참조이므로 msg 가 유효한 C 문자열이어야 한다.
    #[no_mangle]
    pub extern "C" fn rust_println(msg: *const c_char) {
        if msg.is_null() {
            println!();
            return;
        }
        let c_str = unsafe { CStr::from_ptr(msg) };
        match c_str.to_str() {
            Ok(s) => println!("{s}"),
            Err(_) => println!("<invalid utf-8>"),
        }
    }
    """;
}

internal static class GoTemplates
{
    public static string GoMod(string projectName) => $"""
    module {projectName.ToLowerInvariant()}golibs

    go 1.22
    """;

    public static string MainGo() => """
    package main

    // #include <stdlib.h>
    import "C"
    import "fmt"

    //export go_hello
    func go_hello() {
    	fmt.Println("Hello from Go (GoLibs)!")
    }

    //export add_two_numbers_go
    func add_two_numbers_go(a, b int64) int64 {
    	return a + b
    }

    // c-archive 빌드 모드는 main 패키지 + main 함수를 요구하지만
    // 실제로는 호출되지 않는다 (라이브러리로만 사용됨).
    func main() {}
    """;
}

internal static class DotnetTemplates
{
    public static string Csproj() => """
    <Project Sdk="Microsoft.NET.Sdk">

      <PropertyGroup>
        <OutputType>Library</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>

        <!-- Native AOT: 어셈블리/다른 언어에서 직접 호출 가능한 네이티브 라이브러리로 게시 -->
        <PublishAot>true</PublishAot>
        <!-- 🆕 이게 핵심! 이게 없으면 UnmanagedCallersOnly 메서드가 dylib으로 export 안 됨 -->
        <NativeLib>Shared</NativeLib>
        <InvariantGlobalization>true</InvariantGlobalization>
      </PropertyGroup>

    </Project>
    """;


    public static string BridgeCs() => """
    using System.Runtime.InteropServices;

    namespace DotnetLibs;

    public static class Bridge
    {
        [UnmanagedCallersOnly(EntryPoint = "dotnet_hello")]
        public static void Hello()
        {
            Console.WriteLine("Hello from .NET (DotnetLibs)!");
        }
    }
    """;

    public static string CalculateLibCs() => """
    using System.Runtime.InteropServices;

    namespace DotnetLibs;

    public static class CalculateLib
    {
        /// <summary>
        /// 두 정수를 더한 합계를 반환한다. (ARM64 호출 규약: x0=a, x1=b, 반환값=x0)
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "add_two_numbers_dotnet")]
        public static long AddTwoNumbers(long a, long b) => a + b;

        /// <summary>
        /// 정수 하나를 받아 .NET Console로 출력한다. (ARM64 호출 규약: x0=n, 반환값 없음)
        /// UnmanagedCallersOnly 경계를 넘는 메서드는 예외를 밖으로 던지면 안 되므로 반드시 try/catch로 감싼다.
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "print_number_dotnet")]
        public static void PrintNumber(long n)
        {
            try
            {
                Console.WriteLine($"[.NET] 결과 값 → {n}");
                Console.Out.Flush(); // 다른 런타임(Rust/어셈블리)의 출력과 순서가 엇갈리지 않도록 즉시 플러시
            }
            catch
            {
                // UnmanagedCallersOnly 경계 밖으로 예외가 새어나가면 크래시로 이어지므로 절대 던지지 않는다.
            }
        }
    }
    """;


    public static string HunBuildCs()
    {
        return """
#!/usr/bin/env -S dotnet --
// =========================================================================
// 👑 [Hun-ASM] .NET Native AOT File-based 턱시도 오케스트레이터 (hun-build.cs)
// =========================================================================
#:property TargetFramework=net10.0
#:property PublishAot=true
#:property OptimizationPreference=Speed

using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "Hun-ASM 단일 파일 기반 스크립트 턱시도 환경")]

Console.WriteLine("👑 [Hun-ASM] .NET Native AOT 턱시도 사격 통제 기어 가동! 👑");

var excludeFolders = new List<string> {
"bin", "build", "obj", "artifacts", "assets", "node_modules", ".git", ".vscode"
};

var currentDir = Directory.GetCurrentDirectory();
var asmFiles = Directory.GetFiles(currentDir, "*.s", SearchOption.AllDirectories)
    .Concat(Directory.GetFiles(currentDir, "*.S", SearchOption.AllDirectories))
    .Select(f => Path.GetRelativePath(currentDir, f))
    .Where(f => !excludeFolders.ByAnySegmentMatch(f))
    .Distinct(StringComparer.OrdinalIgnoreCase) // 맥OS 대소문자 중복 파일 방어 장갑
    .ToList();

if (asmFiles.Count == 0)
{
    Console.WriteLine("🛑 어이 친구, 이 벌판엔 사격할 어셈블리 파일(.s)이 한 개도 없구만! 하하하.");
    return;
}

string targetBaseName = "hun-bin";
foreach (var file in asmFiles)
{
    try
    {
        string content = File.ReadAllText(file);
        if (content.Contains("_main:") || content.Contains("main:"))
        {
            targetBaseName = Path.GetFileNameWithoutExtension(file).ToLower();
            break;
        }
    }
    catch { }
}

string binDir = Path.Combine(currentDir, "bin");
if (!Directory.Exists(binDir)) Directory.CreateDirectory(binDir);
string outputFile = Path.Combine("bin", targetBaseName);

// 🆕 0단계-A: Rust(rust_core) 무장 준비 (있으면 자동으로 함께 사격!)
string? rustLib = null;
string rustManifest = Path.Combine(currentDir, "app", "RustLibs", "rust_core", "Cargo.toml");
if (File.Exists(rustManifest))
{
    Console.WriteLine("\n👑 0단계-A: Rust(rust_core) 무장 준비...");
    var psiCargo = new ProcessStartInfo(
        "cargo",
        $"build --release --manifest-path \"{rustManifest}\"")
    { UseShellExecute = false };
    using var procCargo = Process.Start(psiCargo);
    procCargo?.WaitForExit();

    string srcRustLib = Path.Combine(currentDir, "app", "RustLibs", "rust_core", "target", "release", "librust_core.a");
    if (File.Exists(srcRustLib))
    {
        rustLib = srcRustLib;
        Console.WriteLine($"    ✅ Rust 무장 완료: {rustLib}");
    }
    else
    {
        Console.WriteLine("    ⚠️ librust_core.a 를 못 찾았네. cargo build 로그를 확인해줘.");
    }
}

// 🆕 0단계-B: Go(GoLibs) 무장 준비 (있으면 자동으로 함께 사격!)
string? goLib = null;
string goMod = Path.Combine(currentDir, "app", "GoLibs", "go.mod");
if (File.Exists(goMod))
{
    Console.WriteLine("\n👑 0단계-B: Go(GoLibs) 무장 준비...");
    string goLibsDir = Path.Combine(currentDir, "app", "GoLibs");
    string goOutDir = Path.Combine(goLibsDir, "out");
    if (!Directory.Exists(goOutDir)) Directory.CreateDirectory(goOutDir);

    var psiGo = new ProcessStartInfo("go", "build -buildmode=c-archive -o out/libgolibs.a .")
    {
        UseShellExecute = false,
        WorkingDirectory = goLibsDir
    };
    using var procGo = Process.Start(psiGo);
    procGo?.WaitForExit();

    string srcGoLib = Path.Combine(goOutDir, "libgolibs.a");
    if (File.Exists(srcGoLib))
    {
        goLib = srcGoLib;
        Console.WriteLine($"    ✅ Go 무장 완료: {goLib}");
    }
    else
    {
        Console.WriteLine("    ⚠️ libgolibs.a 를 못 찾았네. go build 로그를 확인해줘.");
    }
}

// 🆕 0단계-C: .NET Native AOT(DotnetLibs) 사전 정찰 및 무장 준비 (있으면 자동으로 함께 사격!)
string? dotnetDylib = null;
string dotnetProj = Path.Combine(currentDir, "app", "DotnetLibs", "DotnetLibs.csproj");
if (File.Exists(dotnetProj))
{
    Console.WriteLine("\n👑 0단계-C: .NET Native AOT(DotnetLibs) 사전 정찰 및 무장 준비...");
    var psiDotnet = new ProcessStartInfo("dotnet", $"publish \"{dotnetProj}\" -c Release -r osx-arm64")
    {
        UseShellExecute = false
    };
    using var procDotnet = Process.Start(psiDotnet);
    procDotnet?.WaitForExit();

    string publishDir = Path.Combine(currentDir, "app", "DotnetLibs", "bin", "Release", "net10.0", "osx-arm64", "publish");
    string srcDylib = Path.Combine(publishDir, "DotnetLibs.dylib");

    if (File.Exists(srcDylib))
    {
        dotnetDylib = Path.Combine(binDir, "DotnetLibs.dylib");
        File.Copy(srcDylib, dotnetDylib, true);

        // 실행 파일과 같은 폴더(bin/)에서 찾도록 install name을 @rpath 기준으로 재설정
        var psiInstallName = new ProcessStartInfo(
            "install_name_tool",
            $"-id @rpath/DotnetLibs.dylib \"{dotnetDylib}\"")
        { UseShellExecute = false };
        using var procInstallName = Process.Start(psiInstallName);
        procInstallName?.WaitForExit();

        Console.WriteLine($"    ✅ .NET 무장 완료: {dotnetDylib}");
    }
    else
    {
        Console.WriteLine("    ⚠️ DotnetLibs.dylib를 못 찾았네. .csproj에 <NativeLib>Shared</NativeLib> 설정이 있는지 확인해줘!");
    }
}

string sdkPath = "/Library/Developer/CommandLineTools/SDKs/MacOSX.sdk";
try
{
    var psiSdk = new ProcessStartInfo("xcrun", "--show-sdk-path") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    using var procSdk = Process.Start(psiSdk);
    string output = procSdk?.StandardOutput.ReadToEnd() ?? "";
    procSdk?.WaitForExit();
    if (procSdk?.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)) sdkPath = output.Trim();
}
catch { }

var buildStart = Stopwatch.StartNew();
List<string> objFiles = new List<string>();

Console.WriteLine("\n⚔️ 1단계: as(어셈블러) 정밀 개별 사격 개시...");
foreach (var srcFile in asmFiles)
{
    string objFile = Path.Combine(binDir, Path.GetFileNameWithoutExtension(srcFile) + ".o");
    objFiles.Add(objFile);
    string asArgs = $"-arch arm64 -g -o \"{objFile}\" \"{srcFile}\"";
    var psiAs = new ProcessStartInfo("as", asArgs) { UseShellExecute = false };
    using var procAs = Process.Start(psiAs);
    procAs?.WaitForExit();
}

Console.WriteLine("\n⚔️ 2단계: ld(링커) 통합 통령 링킹 개시...");
string objectsClause = string.Join(" ", objFiles.Select(o => $"\"{o}\""));
string ldArgs = $"-arch arm64 -syslibroot \"{sdkPath}\" -lSystem -o \"{outputFile}\" {objectsClause}";
if (rustLib != null)
{
    // Rust 정적 라이브러리(.a) — 별도 rpath 필요 없이 그냥 오브젝트처럼 편입
    ldArgs += $" \"{rustLib}\"";
}
if (goLib != null)
{
    // Go 정적 라이브러리(.a, c-archive) — 이것도 마찬가지로 그냥 편입
    ldArgs += $" \"{goLib}\"";
}
if (dotnetDylib != null)
{
    // .NET 무기(dylib)를 링크 목록에 편입 + 실행 파일과 같은 폴더에서 찾도록 rpath 등록
    ldArgs += $" \"{dotnetDylib}\" -rpath @executable_path";
}
var psiLd = new ProcessStartInfo("ld", ldArgs) { UseShellExecute = false };
using var procLd = Process.Start(psiLd);
procLd?.WaitForExit();
buildStart.Stop();

foreach (var obj in objFiles) { try { File.Delete(obj); } catch { } }
Console.WriteLine($"✨ 사격 성공! 완벽한 디버그 기계어 바이너리 탄생함. ({buildStart.ElapsedMilliseconds}ms) -> ./{outputFile}");

Console.WriteLine("\n⚡ 즉시 실행 타격 감행!\n------------------------------------------------");
var psiRun = new ProcessStartInfo(Path.Combine(".", outputFile)) { UseShellExecute = false };
using var procRun = Process.Start(psiRun);
procRun?.WaitForExit();
Console.WriteLine("------------------------------------------------\n🏁 작전 종료 완료!");

public static class FolderFilterExtensions
{
    public static bool ByAnySegmentMatch(this List<string> excludes, string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(seg => excludes.Contains(seg, StringComparer.OrdinalIgnoreCase));
    }
}
""";
    }

}

internal static class AsmTemplates
{
    public static string MainS(string projectName, string symbolPrefix, string osLabel, bool withDotnet = false)
    {
        string label = $"{symbolPrefix}main";

        // withDotnet 이 켜져 있으면 extern 선언과 bl 호출을 기본으로 활성화한다.
        string dotnetExtern = withDotnet
            ? $".extern {symbolPrefix}dotnet_hello"
            : $"// .extern {symbolPrefix}dotnet_hello";

        string dotnetCall = withDotnet
            ? $"// .NET(Native AOT, DotnetLibs) 기본 데모 호출 — armcli init --dotnet 으로 자동 활성화됨\n        bl      {symbolPrefix}dotnet_hello\n\n        "
            : "";

        // withDotnet 일 때, 계산 결과를 .NET Console로 바로 출력하는 조합 예시를 함께 안내한다.
        string dotnetCalcExample = withDotnet
            ? $"""

        // 예) .NET(DotnetLibs)으로 두 수의 합 구하기 + 결과 출력까지 위임하기:
        //   mov     x0, #5
        //   mov     x1, #4
        //   bl      {symbolPrefix}add_two_numbers_dotnet
        //   mov     x19, x0                    // 결과 백업
        //   mov     x0, x19
        //   bl      {symbolPrefix}print_number_dotnet   // [.NET] 결과 값 → 9
        """
            : "";

        return $"""
    //-----------------------------------------------------
    // {projectName} — Entry Point (src/Main.S)
    // Target OS: {osLabel}  (프로그램 시작 심볼: '{label}')
    // armcli init 으로 생성됨
    //-----------------------------------------------------

    // 🆕 [대제독의 권고] 무기고 인프라를 인클루드하여 FUNC_START_FULL 등을 즉시 사격해 보시게나!
    .include "src/includes/hun.macros.inc"

    .global {label}
    .align 2

    {label}:
        // --- Prologue ---
        stp     x29, x30, [sp, #-16]!  // Frame pointer, Link register 저장
        mov     x29, sp                // Frame pointer 설정

        // --- Main Logic ---
        {dotnetCall}
        // 예) Rust 라이브러리 함수 호출:
        //   bl      {symbolPrefix}rust_hello
        //
        // 예) 두 수의 합 구하기 (x0=3, x1=4 전달 후 x0으로 결과 수신):
        //   mov     x0, #3
        //   mov     x1, #4
        //   bl      {symbolPrefix}add_two_numbers
        {dotnetCalcExample}
        mov     x0, #0                 // Return value (0)

        // --- Epilogue ---
        ldp     x29, x30, [sp], #16    // Frame pointer, Link register 복원
        ret                            // Return
    """;
    }

    public static string HunMacrosInc()
    {
        return """
    // =================================================
    //  제목: 섹션 선언 및 함수 제어 매크로 모음 (hun.macros.inc)
    //  목적: 장문의 지시문을 압축하고 ARM64 최적화 규칙을 강제함
    // =================================================
    .ifndef SECTION_MACROS_INC
    .set    SECTION_MACROS_INC, 1

    // ------------------------------------------------------
    // [코드 구역] __TEXT,__text, regular, pure_instructions
    // 명령어는 무조건 4바이트 (2^2) 정렬
    // 실제 기계어 명령어가 위치. pure_instructions = 순수 명령어 구역
    // ------------------------------------------------------
    .macro   CODE_SECTION
    .section __TEXT, __text, regular, pure_instructions
    .align 2
    .endmacro
    // 사용 예)
    //   CODE_SECTION
    //   _add_one:
    //       add     w0, w0, #1      // x0(인자) + 1 을 x0(반환값)에 저장
    //       ret

    // -----------------------------------------------------
    // [초기화된 변수] __DATA,__data
    // 선언과 동시에 값이 존재하며, 실행 중 수정 가능한 전역/정적 변수 영역
    // 일반 전역 변수 구역 안전하게 8바이트 정렬
    // -----------------------------------------------------
    .macro   DATA_SECTION
    .section __DATA, __data
    .align 3
    .endmacro
    // 사용 예)
    //   DATA_SECTION
    //   counter: .quad 0        // 8바이트 정수, 초기값 0 (실행 중 값이 바뀌는 전역 변수)
    //   hp:      .word 100      // 4바이트 정수, 초기값 100

    // -------------------------------------------------------
    // [읽기전용 변수/포인터 테이블] __DATA,__const
    // 동적 링킹 시점에 주소가 확정된 후 읽기 전용으로 보호되는 변수 구역
    // 주소 재배치 후 읽기 전용으로 보호되는 섹션
    // 함수 포인터 테이블, 델리게이트 리스트 등에 최적
    // 64비트 주소값(.quad)들의 배열이므로 무조건 8바이트(2^3) 정렬
    // -------------------------------------------------------
    .macro   CONST_DATA_SECTION
    .section __DATA, __const
    .align 3
    .endmacro
    // 사용 예)
    //   CONST_DATA_SECTION
    //   handler_table:
    //       .quad handle_north      // 함수 포인터 테이블 (링킹 후 주소 고정, 읽기 전용)
    //       .quad handle_south
    //       .quad handle_east
    //       .quad handle_west

    // -----------------------------------------------------
    // [읽기전용 상수 데이터] __TEXT,__const
    // 룩업 테이블이나 배열을 담으므로 8바이트(2^3) 고정이 안전함
    // 문자열이 아닌 일반 상수 (룩업 테이블, 상수 배열 등) - 재배치 불필요
    // -----------------------------------------------------
    .macro   CONST_SECTION
    .section __TEXT, __const
    .align 3
    .endmacro
    // 사용 예)
    //   CONST_SECTION
    //   fib_table: .quad 0, 1, 1, 2, 3, 5, 8, 13   // 피보나치 룩업 테이블 (재배치 불필요한 순수 상수)

    // -----------------------------------------------------
    // [C-스타일 문자열 리터럴] __TEXT,__cstring,cstring_literals
    // printf 포맷 스트링 등 널 종료(null-terminated)
    // 문자열 상수 전용 (읽기 전용)
    // 정렬 없음 : 바이트 스트림
    // ------------------------------------------------------
    .macro   CSTRING_SECTION
    .section __TEXT, __cstring, cstring_literals
    .align 3 // Read-Only 문자열 캐시라인 히트율 극대화
    .endmacro
    // 사용 예)
    //   CSTRING_SECTION
    //   fmt_hello: .asciz "안녕, %s!\n"     // printf 포맷 문자열 (널 종단)
    //   msg_bye:   .asciz "잘 가시게.\n"

    // -------------------------------------------------------
    // [0으로 초기화된 변수] __DATA,__bss
    // 초기값이 없거나 0인 변수. 바이너리 용량을 차지하지 않고 실행 시 0 할당
    // 안전하게 8바이트 정렬
    // -------------------------------------------------------
    .macro   BSS_SECTION
    .section __DATA, __bss
    .align 3
    .endmacro
    // 사용 예)
    //   BSS_SECTION
    //   input_buffer: .skip 256    // 256바이트 버퍼, 0으로 초기화 (바이너리 용량 안 차지함)
    //   ready_flag:   .skip 8

    // -------------------------------------------------------------------
    // [공용/잠정 정의 심볼] __DATA,__common
    // 여러 오브젝트 간 중복 선언된 전역 변수를 링커가 단일화해주는 구역
    // .globl _shared_value  ; 1. _sharted_value 라는 이름으로 외부에 공개할 건데,
    // .comm _shared_value, 4, 2 ; 4바이트 짜리 임시 공용 예약 공간
    // -------------------------------------------------------------------
    .macro   COMMON_SECTION
    .section __DATA, __common
    .endmacro
    // 사용 예)
    //   COMMON_SECTION
    //   .globl _shared_counter
    //   .comm  _shared_counter, 4, 2   // 4바이트 공용 예약 공간, 여러 .o 파일이 하나로 단일화됨

    // ----------------------------------------------------
    // [4바이트 실수 상수] __TEXT,__literal4
    // float(32비트) 리터럴 전용 이므로 무조건 4바이트(2^2) 정렬 고정
    // ----------------------------------------------------
    .macro   LITERAL4_SECTION
    .section __TEXT, __literal4, 4byte_literals
    .align 2
    .endmacro
    // 사용 예)
    //   LITERAL4_SECTION
    //   pi_f: .float 3.14159265    // 32비트 단정도(float) 실수 상수

    // ---------------------------------------------------
    // [8바이트 실수 상수] __TEXT,__literal8
    // double(64비트) 리터럴 전용이므로 무조건 8바이트(2^3) 정렬 고정
    // ---------------------------------------------------
    .macro   LITERAL8_SECTION
    .section __TEXT, __literal8, 8byte_literals
    .align 3
    .endmacro
    // 사용 예)
    //   LITERAL8_SECTION
    //   pi_d: .double 3.141592653589793    // 64비트 배정도(double) 실수 상수

    // --- [우아한 함수 프롤로그 / 에필로그 제어] --- //

    // ========================================================================
    // [풀 스펙] 전원 참전형 함수 프롤로그 (독서실(callee) 레지스터 x19~x28 전원 백업)
    // ARM64 규칙에 맞춰 2개씩 짝지어 안전하게 보관하네.
    // 최소 stack_size는 독서실 10개(80바이트) + 프레임(16바이트) = 96바이트 이상이어야 함!
    // ========================================================================
    .macro FUNC_START_FULL name, stack_size
        .if    \stack_size < 96
            .error "FUNC_START_FULL: 모든 독서실을 쓰려면 stack_size는 최소 96 이상이어야 합니다!"
        .endif
        .if    (\stack_size % 16) != 0
            .error "FUNC_START_FULL: stack_size는 16의 배수여야 합니다. ARM64 SP 정렬 규칙"
        .endif
    .global _\name
    .align 2
    _\name:
    	stp x29, x30, [sp, #-\stack_size]!
    	mov x29, sp
    	stp x19, x20, [sp, #16]
    	stp x21, x22, [sp, #32]
    	stp x23, x24, [sp, #48]
    	stp x25, x26, [sp, #64]
    	stp x27, x28, [sp, #80]
    .endmacro

    .macro FUNC_EXIT_FULL stack_size
    	ldp x19, x20, [sp, #16]
    	ldp x21, x22, [sp, #32]
    	ldp x23, x24, [sp, #48]
    	ldp x25, x26, [sp, #64]
    	ldp x27, x28, [sp, #80]
    	ldp x29, x30, [sp], #\stack_size
        ret
    .endmacro
    .endif
    """;
    }

}

internal static class ReadmeTemplates
{
    public static string ProjectReadme(string projectName, bool withRust, bool withGo, bool withDotnet)
    {
        var libs = new List<string>();
        if (withRust) libs.Add("- **Rust** — `app/RustLibs/rust_core` (staticlib, `add_two_numbers`, `rust_hello`)");
        if (withGo) libs.Add("- **Go** — `app/GoLibs` (c-archive, `add_two_numbers_go`, `go_hello`)");
        if (withDotnet) libs.Add("- **.NET (Native AOT)** — `app/DotnetLibs` (`add_two_numbers_dotnet`, `print_number_dotnet`, `dotnet_hello`)");
        string libsSection = libs.Count > 0 ? string.Join("\n", libs) : "- (언어 라이브러리 없음 — 순수 어셈블리 프로젝트)";

        var tools = new List<string> { "- **Zig** — `zig build run` 에 필요 (선택한 언어의 툴체인만 있으면 됨, .NET SDK 불필요)" };
        if (withRust) tools.Add("- **Rust (cargo)** — Rust 라이브러리 빌드에 필요");
        if (withGo) tools.Add("- **Go** — Go 라이브러리 빌드에 필요");
        if (withDotnet) tools.Add("- **.NET SDK 10** — .NET 라이브러리 빌드에 필요 (`zig build run` 사용 시)");
        string toolsSection = string.Join("\n", tools);

        return $"""
    # {projectName}

    `armcli init` 으로 생성된 프로젝트. Zig(`build.zig`)가 오케스트레이터 역할을 하며,
    어셈블리 진입점(`src/Main.S`)에서 각 언어 라이브러리의 함수를 `bl` 로 호출하는 구조.

    ## 구성

    {libsSection}

    ## 디렉토리

    ```
    {projectName}/
    ├── build.zig          # 오케스트레이터 — 언어별 라이브러리 빌드 후 exe 링크
    ├── app/
    │   ├── RustLibs/rust_core/
    │   ├── GoLibs/
    │   └── DotnetLibs/
    └── src/
        ├── Main.S          # 진입점 (_main / main)
        ├── constants/
        ├── data/
        ├── includes/
        └── libs/
    ```

    ## 필요한 도구

    {toolsSection}

    ## 빌드 & 실행

    **권장 — Zig 오케스트레이터** (선택한 언어의 툴체인만 있으면 됨, .NET SDK 없어도 무방):
    ```bash
    zig build run
    ```

    **대안 — .NET 오케스트레이터** (`hun-build.cs`, 빠른 로컬 macOS 전용 즉석 실행기):
    ```bash
    dotnet ./hun-build.cs
    ```
    ⚠️ `hun-build.cs`는 이 프로젝트가 `--dotnet` 옵션 없이 생성됐어도 **.NET SDK 10이 항상 필요**해 (스크립트 자체가 .NET file-based app이기 때문). .NET SDK가 없다면 `zig build run`을 사용해줘.

    ## 다음 단계

    1. `src/Main.S` 의 주석 처리된 `bl` 호출 예시를 풀어서 실제로 라이브러리 함수를 호출해보기
    2. `build.zig` 에서 사용하지 않는 언어 블록은 지우거나 필요에 맞게 조정하기
    3. `constants/`, `data/`, `includes/`, `libs/` 폴더에 프로젝트 성격에 맞는 내용 채우기
    """;
    }
}
