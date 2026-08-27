using System.Reflection;
using System.Runtime.InteropServices;

// ---------------------------------------------------------------------
// --version / -v
// ---------------------------------------------------------------------
if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
{
  var version = Assembly.GetExecutingAssembly()
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                        .InformationalVersion ?? "0.1.0";
  Console.WriteLine($"armcli version {version}");
  return;
}

// ---------------------------------------------------------------------
// list — 사용 가능한 템플릿 종류 보여주기
// ---------------------------------------------------------------------
if (args.Length > 0 && args[0] == "list")
{
  Console.WriteLine("사용 가능한 템플릿:");
  foreach (var (key, desc) in Templates.Descriptions)
  {
    Console.WriteLine($"  {key,-10} {desc}");
  }
  return;
}

if (args.Length < 2 || args[0] != "new")
{
  PrintUsage();
  return;
}

string fileNameArg = args[1];

// -----------------------------------------------------------------------
// 옵션 파싱
//   -n <label>        : 라벨 이름 (기본값: 파일 이름)
//   -t <template>      : 템플릿 종류 (bare | function | loop | neon), 기본값 function
//   --os <macos|linux>  : 심볼 규칙 (기본값: 현재 실행 중인 OS 자동 감지)
//   --force               : 기존 파일 있어도 덮어쓰기
//   --stdout               : 파일로 쓰지 않고 터미널에만 출력
// -----------------------------------------------------------------------
string labelName = Path.GetFileNameWithoutExtension(fileNameArg);
string templateKey = "function";
string? osOverride = null;
bool force = false;
bool toStdout = false;

for (int i = 2; i < args.Length; i++)
{
  switch (args[i])
  {
    case "-n" when i + 1 < args.Length:
      labelName = args[++i];
      break;
    case "-t" when i + 1 < args.Length:
      templateKey = args[++i].ToLowerInvariant();
      break;
    case "--os" when i + 1 < args.Length:
      osOverride = args[++i].ToLowerInvariant();
      break;
    case "--force":
      force = true;
      break;
    case "--stdout":
      toStdout = true;
      break;
  }
}

if (!Templates.Descriptions.ContainsKey(templateKey))
{
  Console.WriteLine($"Error: 알 수 없는 템플릿 '{templateKey}'. 'armcli list'로 확인해줘.");
  return;
}

// -----------------------------------------------------------------------
// 파일 확장자 결정
//   .s / .S 를 명시하면 그대로 존중 (전처리기 통과 여부는 사용자 선택)
//   확장자 없이 치면 기존 관례대로 .S (전처리기 통과 버전)를 기본값으로 유지
// -----------------------------------------------------------------------
string fileName = fileNameArg.EndsWith(".S") || fileNameArg.EndsWith(".s")
    ? fileNameArg
    : $"{fileNameArg}.S";

// -----------------------------------------------------------------------
// OS별 심볼 규칙 결정
//   macOS(Mach-O)는 C 심볼에 언더스코어(_) 접두사가 붙는 전통(ABI 관례)이 있고
//   Linux(ELF)는 접두사가 붙지 않는다.
//   --os로 명시하지 않으면 armcli가 지금 실행되는 OS를 기준으로 자동 판단한다.
// -----------------------------------------------------------------------
bool useUnderscorePrefix = osOverride switch
{
  "macos" => true,
  "linux" => false,
  _ => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) // 자동 감지
};
string symbolPrefix = useUnderscorePrefix ? "_" : "";
string osLabel = useUnderscorePrefix ? "macOS" : "Linux";

if (!force && !toStdout && File.Exists(fileName))
{
  Console.WriteLine($"Error: {fileName} 파일이 이미 존재합니다. (--force 로 덮어쓸 수 있어)");
  return;
}

string template = Templates.Render(templateKey, fileName, labelName, symbolPrefix, osLabel);

if (toStdout)
{
  Console.WriteLine(template);
  return;
}

File.WriteAllText(fileName, template);
Console.WriteLine($"Created: {fileName} (Label: {symbolPrefix}{labelName}, OS: {osLabel}, Template: {templateKey})");
return;


// =========================================================================
static void PrintUsage()
{
  Console.WriteLine("""
    Usage:
      armcli new <filename> [-n <label>] [-t <template>] [--os macos|linux] [--force] [--stdout]
      armcli list
      armcli --version

    예시:
      armcli new hello                      # hello.S, function 템플릿, 현재 OS 기준 자동 판단
      armcli new hello.s -t bare             # 전처리기 안 거치는 최소형 템플릿
      armcli new loop -t loop --os linux      # Linux ELF 심볼 규칙(언더스코어 없음)으로 생성
      armcli new vec -t neon --stdout          # 파일로 안 쓰고 터미널에 미리보기만
    """);
}

// =========================================================================
// 템플릿 정의
// =========================================================================
static class Templates
{
  public static readonly Dictionary<string, string> Descriptions = new()
  {
    ["bare"] = "프롤로그/에필로그 없는 최소형 (가장 기초 단계용)",
    ["function"] = "표준 함수 골격 — 스택 프레임 저장/복원 포함 (기본값)",
    ["loop"] = "카운터 기반 루프 골격이 잡힌 템플릿",
    ["neon"] = "NEON 벡터 레지스터(V) 설정 예제가 들어간 템플릿",
  };

  public static string Render(string key, string fileName, string labelName, string prefix, string osLabel)
  {
    string label = $"{prefix}{labelName}";
    string header = $"""
      //-----------------------------------------------------
      // ARM64 Assembly Template: {fileName}
      // Target OS: {osLabel}  (심볼 접두사: '{prefix}' {(prefix == "_" ? "— Mach-O 관례" : "— ELF 관례, 접두사 없음")})
      //-----------------------------------------------------
      """;

    return key switch
    {
      "bare" => $"""
        {header}
        .global {label}
        .align 2

        {label}:
            // 프롤로그/에필로그 없는 최소형 — 스택을 건드리지 않는 짧은 코드에 적합
            mov     w0, #0
            ret
        """,

      "loop" => $"""
        {header}
        .global {label}
        .align 2

        {label}:
            // --- Prologue ---
            stp     x29, x30, [sp, #-16]!
            mov     x29, sp

            mov     x9, #0              // 카운터 초기화
            mov     x10, #10            // 반복 횟수 (예시)

        loop_start:
            cmp     x9, x10
            b.ge    loop_end            // 카운터 >= 반복 횟수 이면 종료

            // --- 루프 본문 ---
            add     x9, x9, #1
            b       loop_start

        loop_end:
            mov     w0, #0

            // --- Epilogue ---
            ldp     x29, x30, [sp], #16
            ret
        """,

      "neon" => $"""
        {header}
        .global {label}
        .align 2

        {label}:
            // --- Prologue ---
            stp     x29, x30, [sp, #-16]!
            mov     x29, sp

            // --- NEON 벡터 레지스터(V1) 설정 예제: [10, 20, 30, 40] ---
            mov     w6, #10
            mov     w7, #20
            mov     w8, #30
            mov     w9, #40
            ins     v1.s[0], w6
            ins     v1.s[1], w7
            ins     v1.s[2], w8
            ins     v1.s[3], w9        // V1.4S = [10, 20, 30, 40]

            mov     w0, #0

            // --- Epilogue ---
            ldp     x29, x30, [sp], #16
            ret
        """,

      _ => $"""
        {header}
        .global {label}
        .align 2

        {label}:
            // --- Prologue ---
            stp     x29, x30, [sp, #-16]!   // Frame pointer, Link register 저장
            mov     x29, sp                 // Frame pointer 설정

            // --- Main Logic ---
            mov     w0, #0                  // Return value (0)

            // --- Epilogue ---
            ldp     x29, x30, [sp], #16     // Frame pointer, Link register 복원
            ret                             // Return
        """,
    };
  }
}
