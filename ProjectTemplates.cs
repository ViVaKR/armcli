// =========================================================================
// ProjectTemplates.cs
// armcli init 이 생성하는 파일들의 템플릿 모음
// =========================================================================

internal static class ZigTemplates
{
  public static string BuildZig(string projectName, bool withRust, bool withGo, bool withDotnet, bool isMacOS)
  {
    string dotnetRid = isMacOS ? "osx-arm64" : "linux-arm64";

    string rustBlock = !withRust ? "" : $$"""

        // --- Rust 라이브러리 (app/RustLibs/rust_core) ---
        const rust_build = b.addSystemCommand(&.{
            "cargo", "build", "--release",
            "--manifest-path", "app/RustLibs/rust_core/Cargo.toml",
        });
        exe.step.dependOn(&rust_build.step);
        exe.root_module.addObjectFile(b.path("app/RustLibs/rust_core/target/release/librust_core.a"));
    """;

    string goBlock = !withGo ? "" : $$"""

        // --- Go 라이브러리 (app/GoLibs) — c-archive 정적 라이브러리로 빌드 ---
        // NOTE: go build 는 cwd 기준으로 go.mod 를 찾으므로, app/GoLibs 안에서 실행해야 한다.
        const go_out_dir = "app/GoLibs/out";
        const go_build = b.addSystemCommand(&.{
            "go", "build",
            "-buildmode=c-archive",
            "-o", "out/libgolibs.a",
            ".",
        });
        go_build.setCwd(b.path("app/GoLibs"));
        exe.step.dependOn(&go_build.step);
        exe.root_module.addObjectFile(b.path(go_out_dir ++ "/libgolibs.a"));
    """;

    string dotnetBlock = !withDotnet ? "" : $$"""

        // --- .NET Native AOT 라이브러리 (app/DotnetLibs) ---
        // NOTE: 대상 RID(예: osx-arm64/linux-arm64)에 맞춰 결과물(.dylib/.so) 경로가 달라지니
        //       실제 프로젝트에 맞게 rid/경로를 확인해서 조정해줘.
        const dotnet_build = b.addSystemCommand(&.{
            "dotnet", "publish", "app/DotnetLibs/DotnetLibs.csproj",
            "-c", "Release",
            "-r", "{{dotnetRid}}",
        });
        exe.step.dependOn(&dotnet_build.step);
        // exe.root_module.addObjectFile(b.path("app/DotnetLibs/bin/Release/net10.0/{{dotnetRid}}/publish/DotnetLibs.dylib"));
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
        <InvariantGlobalization>true</InvariantGlobalization>
      </PropertyGroup>

    </Project>
    """;

  public static string BridgeCs() => """
    using System.Runtime.InteropServices;

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

    public static class CalculateLib
    {
        /// <summary>
        /// 두 정수를 더한 합계를 반환한다. (ARM64 호출 규약: x0=a, x1=b, 반환값=x0)
        /// </summary>
        [UnmanagedCallersOnly(EntryPoint = "add_two_numbers_dotnet")]
        public static long AddTwoNumbers(long a, long b) => a + b;
    }
    """;
}

internal static class AsmTemplates
{
  public static string MainS(string projectName, string symbolPrefix, string osLabel)
  {
    string label = $"{symbolPrefix}main";
    return $"""
    //-----------------------------------------------------
    // {projectName} — Entry Point (src/Main.S)
    // Target OS: {osLabel}  (프로그램 시작 심볼: '{label}')
    // armcli init 으로 생성됨
    //-----------------------------------------------------
    .global {label}
    .align 2

    // 다른 언어 라이브러리에서 넘어오는 함수는 여기에 선언하고 bl 로 호출한다.
    // (armcli init 옵션에 맞춰 링크된 함수만 주석을 풀어줘)
    // .extern {symbolPrefix}rust_hello
    // .extern {symbolPrefix}add_two_numbers
    // .extern {symbolPrefix}go_hello
    // .extern {symbolPrefix}add_two_numbers_go
    // .extern {symbolPrefix}dotnet_hello
    // .extern {symbolPrefix}add_two_numbers_dotnet

    {label}:
        // --- Prologue ---
        stp     x29, x30, [sp, #-16]!  // Frame pointer, Link register 저장
        mov     x29, sp                // Frame pointer 설정

        // --- Main Logic ---
        // 예) Rust 라이브러리 함수 호출:
        //   bl      {symbolPrefix}rust_hello
        //
        // 예) 두 수의 합 구하기 (x0=3, x1=4 전달 후 x0으로 결과 수신):
        //   mov     x0, #3
        //   mov     x1, #4
        //   bl      {symbolPrefix}add_two_numbers

        mov     w0, #0                 // Return value (0)

        // --- Epilogue ---
        ldp     x29, x30, [sp], #16    // Frame pointer, Link register 복원
        ret                            // Return
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
    if (withDotnet) libs.Add("- **.NET (Native AOT)** — `app/DotnetLibs` (`add_two_numbers_dotnet`, `dotnet_hello`)");
    string libsSection = libs.Count > 0 ? string.Join("\n", libs) : "- (언어 라이브러리 없음 — 순수 어셈블리 프로젝트)";

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
        ├── contants/
        ├── data/
        ├── includes/
        └── libs/
    ```

    ## 빌드 & 실행

    ```bash
    zig build run
    ```

    ## 다음 단계

    1. `src/Main.S` 의 주석 처리된 `bl` 호출 예시를 풀어서 실제로 라이브러리 함수를 호출해보기
    2. `build.zig` 에서 사용하지 않는 언어 블록은 지우거나 필요에 맞게 조정하기
    3. `contants/`, `data/`, `includes/`, `libs/` 폴더에 프로젝트 성격에 맞는 내용 채우기
    """;
  }
}
