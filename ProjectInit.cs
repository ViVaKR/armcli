using System.Runtime.InteropServices;
using System.Text;

// =========================================================================
// ProjectInit.cs
// `armcli init` — 어셈블리 + (Rust/Go/.NET) 라이브러리 + Zig 오케스트레이터를
// 하나로 묶은 완전한 학습용 프로젝트 골격을 생성한다.
// =========================================================================
internal static class ProjectInit
{
  public static void Run(string[] args)
  {
    string? projectNameArg = null;
    string outputDir = ".";
    bool force = false;
    bool withGo = false;
    bool withDotnet = false;
    bool withRust = true; // 기본으로 켜짐 — 강좌 기본 언어
    string? osOverride = null;

    for (int i = 1; i < args.Length; i++)
    {
      switch (args[i])
      {
        case "-n" when i + 1 < args.Length:
          projectNameArg = args[++i];
          break;
        case "-o" when i + 1 < args.Length:
          outputDir = args[++i];
          break;
        case "--os" when i + 1 < args.Length:
          osOverride = args[++i].ToLowerInvariant();
          break;
        case "--force":
          force = true;
          break;
        case "--go":
          withGo = true;
          break;
        case "--dotnet":
          withDotnet = true;
          break;
        case "--rust":
          withRust = true;
          break;
        case "--no-rust":
          withRust = false;
          break;
      }
    }

    if (string.IsNullOrWhiteSpace(projectNameArg))
    {
      Console.WriteLine("Error: 프로젝트 이름이 필요해. -n <ProjectName> 으로 지정해줘.");
      Console.WriteLine("예) armcli init -n HelloWorld -o .");
      return;
    }

    string pascalName = ToPascalCase(projectNameArg);
    string root = Path.Combine(outputDir, pascalName);

    if (Directory.Exists(root) && !force)
    {
      Console.WriteLine($"Error: {root} 디렉토리가 이미 존재해. --force 로 덮어써줘.");
      return;
    }

    bool useUnderscorePrefix = osOverride switch
    {
      "macos" => true,
      "linux" => false,
      _ => RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
    };
    string symbolPrefix = useUnderscorePrefix ? "_" : "";
    string osLabel = useUnderscorePrefix ? "macOS" : "Linux";

    Console.WriteLine("════════════════════════════════════════");
    Console.WriteLine($" armcli init: {pascalName}");
    Console.WriteLine($" 위치: {Path.GetFullPath(root)}");
    Console.WriteLine($" 대상 OS: {osLabel}");
    Console.WriteLine($" 구성: Zig 오케스트레이터 + Assembly(src/Main.S)"
      + (withRust ? " + Rust(RustLibs/rust_core)" : "")
      + (withGo ? " + Go(GoLibs)" : "")
      + (withDotnet ? " + .NET(DotnetLibs)" : ""));
    Console.WriteLine("════════════════════════════════════════");

    CreateDirectories(root, withRust, withGo, withDotnet);

    // --- build.zig (오케스트레이터) ---
    File.WriteAllText(
      Path.Combine(root, "build.zig"),
      ZigTemplates.BuildZig(pascalName, withRust, withGo, withDotnet, useUnderscorePrefix));

    // --- Rust ---
    if (withRust)
    {
      string rustCore = Path.Combine(root, "app", "RustLibs", "rust_core");
      File.WriteAllText(Path.Combine(rustCore, "Cargo.toml"), RustTemplates.CargoToml(pascalName));
      File.WriteAllText(Path.Combine(rustCore, "src", "lib.rs"), RustTemplates.LibRs());
      File.WriteAllText(Path.Combine(rustCore, "src", "console.rs"), RustTemplates.ConsoleRs());
    }

    // --- Go ---
    if (withGo)
    {
      string goLibs = Path.Combine(root, "app", "GoLibs");
      File.WriteAllText(Path.Combine(goLibs, "go.mod"), GoTemplates.GoMod(pascalName));
      File.WriteAllText(Path.Combine(goLibs, "main.go"), GoTemplates.MainGo());
    }

    // --- .NET ---
    if (withDotnet)
    {
      string dotnetLibs = Path.Combine(root, "app", "DotnetLibs");
      File.WriteAllText(Path.Combine(dotnetLibs, "DotnetLibs.csproj"), DotnetTemplates.Csproj());
      File.WriteAllText(Path.Combine(dotnetLibs, "Bridge.cs"), DotnetTemplates.BridgeCs());
      File.WriteAllText(Path.Combine(dotnetLibs, "CalculateLib.cs"), DotnetTemplates.CalculateLibCs());
    }

    // --- 어셈블리 진입점 ---
    File.WriteAllText(
      Path.Combine(root, "src", "Main.S"),
      AsmTemplates.MainS(pascalName, symbolPrefix, osLabel));

    // --- 빈 디렉토리 자리 표시(.gitkeep) ---
    foreach (var dir in new[] { "contants", "data", "includes", "libs" })
    {
      File.WriteAllText(Path.Combine(root, "src", dir, ".gitkeep"), "");
    }

    // --- README ---
    File.WriteAllText(
      Path.Combine(root, "README.md"),
      ReadmeTemplates.ProjectReadme(pascalName, withRust, withGo, withDotnet));

    Console.WriteLine("생성 완료!");
    Console.WriteLine();
    Console.WriteLine("다음 단계:");
    Console.WriteLine($"  cd {root}");
    Console.WriteLine("  zig build run");
  }

  private static void CreateDirectories(string root, bool withRust, bool withGo, bool withDotnet)
  {
    Directory.CreateDirectory(root);

    Directory.CreateDirectory(Path.Combine(root, "app", "scripts"));

    if (withRust)
      Directory.CreateDirectory(Path.Combine(root, "app", "RustLibs", "rust_core", "src"));

    if (withGo)
      Directory.CreateDirectory(Path.Combine(root, "app", "GoLibs"));

    if (withDotnet)
      Directory.CreateDirectory(Path.Combine(root, "app", "DotnetLibs"));

    Directory.CreateDirectory(Path.Combine(root, "src"));
    foreach (var dir in new[] { "contants", "data", "includes", "libs" })
    {
      Directory.CreateDirectory(Path.Combine(root, "src", dir));
    }
  }

  /// <summary>
  /// "hello-world", "hello_world", "hello world" 등을 "HelloWorld" 형태의
  /// 파스칼 케이스로 변환한다. 이미 파스칼/카멜 케이스인 입력은 그대로 유지한다.
  /// </summary>
  private static string ToPascalCase(string input)
  {
    var parts = input.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return input;

    var sb = new StringBuilder();
    foreach (var part in parts)
    {
      if (part.Length == 0) continue;
      sb.Append(char.ToUpperInvariant(part[0]));
      if (part.Length > 1) sb.Append(part[1..]);
    }
    return sb.ToString();
  }
}
