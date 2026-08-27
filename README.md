# armcli

> dotnet aot application for ARM64 Create template 

### Homebrew Formula 작성 3대 규칙

1. **파일명 규칙**: 설치할 CLI 명령어 이름과 완전히 똑같이 `소문자.rb`로 만들어야 함.
* 예: `armcli` 명령어 → **`armcli.rb`**

2. **클래스명 규칙**: 파일 이름의 스네이크 케이스/소문자를 파스칼 케이스(CamelCase)로 변환하여 `class 클래스명 < Formula`로 작성해야 함.
* `armcli` → **`class Armcli < Formula`**
* `my-tool` → **`class MyTool < Formula`**
* `super_cli` → **`class SuperCli < Formula`**

3. **저장소(Tap) 이름 규칙**: Homebrew Tap 레포지토리 이름은 반드시 **`homebrew-<단어>`** 형태여야 `brew tap 유저명/단어`로 깔끔하게 호출할 수 있음.
* 레포명: `homebrew-armcli` → 명령어: `brew tap ViVaKR/armcli`

---
