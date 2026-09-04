#!/usr/bin/env zsh

# release.sh — armcli 릴리스 자동화 스크립트
# RealManMastery (상남자마스터코스) 강좌용 도구
#
# 전제:
#   - 이 스크립트는 개발 레포(armcli) 루트에서 실행한다.
#   - 옆(형제) 디렉토리에 homebrew-armcli 레포가 클론되어 있다고 가정한다.
#     디렉토리 구조 예시:
#       GitWorkspace/
#         ├── armcli/              (개발 레포, 이 스크립트가 여기서 실행됨)
#         └── homebrew-armcli/     (탭 레포)
#   - gh(GitHub CLI)가 설치되어 있고 로그인되어 있어야 GitHub Release 생성이 됨.
#     없으면 tar.gz 파일만 만들고, 릴리스 업로드는 수동으로 안내한다.
#
# 사용법:
#   ./release.sh 0.3.0

set -euo pipefail

if [[ $# -lt 1 ]]; then
    echo "사용법: $0 <새_버전>   예) $0 0.2.0" >&2
    exit 1
fi

NEW_VERSION="$1"
TAG="v${NEW_VERSION}"
RID="osx-arm64"
DEV_REPO_DIR="$(pwd)"
TAP_REPO_DIR="$(cd "${DEV_REPO_DIR}/../homebrew-armcli" && pwd)"
ASSET_NAME="armcli-${TAG}-${RID}.tar.gz"
GITHUB_REPO="ViVaKR/armcli"          # 바이너리를 릴리스로 올릴 개발 레포
TAP_GITHUB_REPO="ViVaKR/homebrew-armcli"

echo "════════════════════════════════════════"
echo " armcli 릴리스: ${TAG}"
echo " 개발 레포:   ${DEV_REPO_DIR}"
echo " 탭 레포:     ${TAP_REPO_DIR}"
echo "════════════════════════════════════════"

# -----------------------------------------------------------------------
# 1. 개발 레포: 버전 번호 갱신 (csproj)
# -----------------------------------------------------------------------
CSPROJ="${DEV_REPO_DIR}/armcli.csproj"
if [[ ! -f "$CSPROJ" ]]; then
    echo "!! ${CSPROJ} 를 못 찾았네. DEV_REPO_DIR 확인해줘." >&2
    exit 1
fi

echo "==> 1. csproj 버전을 ${NEW_VERSION} 으로 갱신"
sed -i '' "s#<Version>.*</Version>#<Version>${NEW_VERSION}</Version>#" "$CSPROJ"
grep '<Version>' "$CSPROJ"

# -----------------------------------------------------------------------
# 2. AOT 게시(publish) — 단일 바이너리 생성
# -----------------------------------------------------------------------
echo "==> 2. dotnet publish (AOT, ${RID})"
cd "$DEV_REPO_DIR"
rm -rf bin obj
dotnet publish -c Release -r "$RID" -o "publish_out"

BIN_PATH="publish_out/armcli"
if [[ ! -f "$BIN_PATH" ]]; then
    echo "!! ${BIN_PATH} 가 생성되지 않았네. publish 로그를 확인해줘." >&2
    exit 1
fi

# -----------------------------------------------------------------------
# 3. tar.gz 압축 + sha256 계산
# -----------------------------------------------------------------------
echo "==> 3. 압축 및 sha256 계산"
STAGE_DIR="$(mktemp -d)"
cp "$BIN_PATH" "${STAGE_DIR}/armcli"
chmod +x "${STAGE_DIR}/armcli"
tar -czf "${DEV_REPO_DIR}/${ASSET_NAME}" -C "$STAGE_DIR" armcli
rm -rf "$STAGE_DIR"

SHA256=$(shasum -a 256 "${DEV_REPO_DIR}/${ASSET_NAME}" | awk '{print $1}')
echo "    자산 파일: ${ASSET_NAME}"
echo "    sha256:    ${SHA256}"

# -----------------------------------------------------------------------
# 4. 개발 레포: 커밋 + 태그 + push
# -----------------------------------------------------------------------
echo "==> 4. 개발 레포 커밋/태그/push"
git add "$CSPROJ"
git commit -m "release: v${NEW_VERSION}

- armcli.csproj 버전을 ${NEW_VERSION} 으로 갱신
- ${RID} 대상 AOT 바이너리 게시 준비"

git tag -a "$TAG" -m "armcli ${TAG}"
git push origin main
git push origin "$TAG"

# -----------------------------------------------------------------------
# 5. GitHub Release 생성 + 바이너리 업로드 (gh CLI 있을 때만)
# -----------------------------------------------------------------------
if command -v gh >/dev/null 2>&1; then
    echo "==> 5. GitHub Release 생성 및 자산 업로드"
    gh release create "$TAG" "${DEV_REPO_DIR}/${ASSET_NAME}" \
        --repo "$GITHUB_REPO" \
        --title "armcli ${TAG}" \
        --notes "armcli ${TAG} — 자동 릴리스 (release.sh)"
else
    echo "!! gh CLI가 없어서 릴리스 업로드는 수동으로 해줘:"
    echo "   1) https://github.com/${GITHUB_REPO}/releases/new 에서 태그 ${TAG} 선택"
    echo "   2) ${DEV_REPO_DIR}/${ASSET_NAME} 파일을 자산으로 첨부 후 게시"
    read -q "REPLY?   업로드 완료했으면 Enter, 아니면 Ctrl+C 로 중단: "
    echo
fi

# -----------------------------------------------------------------------
# 6. 탭 레포: armcli.rb 갱신
# -----------------------------------------------------------------------
echo "==> 6. homebrew-armcli 레포의 armcli.rb 갱신"
if [[ ! -d "$TAP_REPO_DIR" ]]; then
    echo "!! ${TAP_REPO_DIR} 를 못 찾았네. 탭 레포 경로를 확인해줘." >&2
    exit 1
fi

RB_FILE="${TAP_REPO_DIR}/armcli.rb"
NEW_URL="https://github.com/${GITHUB_REPO}/releases/download/${TAG}/${ASSET_NAME}"

cat > "$RB_FILE" << EOF
class Armcli < Formula
  desc "ARM64 Assembly Template Generator CLI for Students"
  homepage "https://github.com/${GITHUB_REPO}"
  url "${NEW_URL}"
  sha256 "${SHA256}"
  version "${NEW_VERSION}"

  def install
    bin.install "armcli"
  end
end
EOF

echo "    ${RB_FILE} 갱신 완료:"
cat "$RB_FILE"

# -----------------------------------------------------------------------
# 7. 탭 레포: 커밋 + push
# -----------------------------------------------------------------------
echo "==> 7. 탭 레포 커밋/push"
cd "$TAP_REPO_DIR"
git add armcli.rb
git commit -m "chore: bump armcli to ${TAG}

- url/sha256/version 을 ${TAG} 릴리스 자산 기준으로 갱신
- 자산: ${ASSET_NAME}
- sha256: ${SHA256}"
git push origin main

# -----------------------------------------------------------------------
# 8. 정리 + 검증 안내
# -----------------------------------------------------------------------
rm -f "${DEV_REPO_DIR}/${ASSET_NAME}"

echo "════════════════════════════════════════"
echo " 릴리스 ${TAG} 완료!"
echo ""
echo " 검증:"
echo "   brew uninstall armcli 2>/dev/null; brew untap ${TAP_GITHUB_REPO} 2>/dev/null"
echo "   brew tap ${TAP_GITHUB_REPO}"
echo "   brew install armcli"
echo "   armcli --version   # ${NEW_VERSION} 이 나오는지 확인"
echo "════════════════════════════════════════"
