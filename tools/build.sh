#!/usr/bin/env bash
# WaterCalcMod 构建脚本（Windows + csc；在 MSYS/Git Bash 里跑）
#
#   tools/build.sh            # 发布版（Release）→ dist/WaterCalcMod/
#   tools/build.sh dev        # 开发版（DEV，带 ctl 调试通道与 log.txt）
#
# 环境变量：
#   GAME_DIR  游戏安装根目录（其下有 "Probably Stolen_Data/Managed"）
#             不设则自动在常见 Steam 路径里找
#   CSC       csc.exe 路径，默认 .NET Framework 4.x 自带那个
#
# 依赖：游戏自己的 Managed 程序集（不随仓库分发，必须自备游戏）。
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/src"
OUT="$ROOT/dist/WaterCalcMod"
LIB="$ROOT/lib"

TAG="release"
[ "${1:-}" = "dev" ] && TAG="dev"

# ---------- 找游戏目录 ----------
find_game() {
  [ -n "${GAME_DIR:-}" ] && { echo "$GAME_DIR"; return; }
  local c
  for c in \
    "/d/steam/steamapps/common/Probably Stolen" \
    "/c/Program Files (x86)/Steam/steamapps/common/Probably Stolen" \
    "/c/Program Files/Steam/steamapps/common/Probably Stolen" \
    "/e/steam/steamapps/common/Probably Stolen" \
    "$HOME/Steam/steamapps/common/Probably Stolen"
  do
    [ -d "$c/Probably Stolen_Data/Managed" ] && { echo "$c"; return; }
  done
}
GAME="$(find_game || true)"
if [ -z "${GAME:-}" ]; then
  echo "找不到游戏目录。请设置 GAME_DIR，例如：" >&2
  echo "  GAME_DIR='D:/steam/steamapps/common/Probably Stolen' tools/build.sh" >&2
  exit 2
fi
MANAGED="$GAME/Probably Stolen_Data/Managed"
[ -d "$MANAGED" ] || { echo "缺少 Managed 目录：$MANAGED" >&2; exit 2; }

CSC="${CSC:-/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe}"
[ -f "$CSC" ] || { echo "找不到 csc.exe：$CSC" >&2; exit 2; }

# csc 需要 Windows 风格路径；MSYS 的 cygpath -m 给反斜杠转正斜杠
winpath() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
MANAGED_W="$(winpath "$MANAGED")"
OUT_W="$(winpath "$OUT")"
LIB_W="$(winpath "$LIB")"

mkdir -p "$OUT"

args=(/nologo /target:library /platform:x64 /langversion:5 "/out:$OUT_W/WaterCalcMod.dll")
[ "$TAG" = "dev" ] && args+=(/define:DEV)

refs=(
  0Harmony.dll
  UnityEngine.dll UnityEngine.CoreModule.dll UnityEngine.IMGUIModule.dll
  UnityEngine.InputLegacyModule.dll UnityEngine.TextRenderingModule.dll
  UnityEngine.UIModule.dll UnityEngine.UI.dll UnityEngine.ImageConversionModule.dll
  Unity.TextMeshPro.dll Assembly-CSharp.dll netstandard.dll
)
for r in "${refs[@]}"; do
  if [ -f "$MANAGED/$r" ]; then
    args+=("/r:$MANAGED_W/$r")
  elif [ -f "$LIB/$r" ]; then
    args+=("/r:$LIB_W/$r")
  else
    echo "警告：引用缺失 $r（跳过，可能导致编译失败）" >&2
  fi
done

srcs=(); f
for f in "$SRC"/*.cs; do srcs+=("$(basename "$f")"); done

echo "=== 构建 $TAG（${#srcs[@]} 个源文件） ==="
echo "    游戏目录：$GAME"
cd "$SRC" || exit 1
"$CSC" "${args[@]}" "${srcs[@]}" 2>&1 | tee "/tmp/csc_$TAG.log"
rc=${PIPESTATUS[0]}

if grep -q "error CS" "/tmp/csc_$TAG.log"; then
  echo "!!! 编译报错，产物不可信（不要拿去发布）" >&2
  exit 1
fi

# 清单：Release 用空能力清单，DEV 用本地调试清单
if [ "$TAG" = "dev" ]; then
  cp -f "$ROOT/manifest.dev.xml" "$OUT/manifest.xml"
  cp -f "$OUT/WaterCalcMod.dll" "$ROOT/dist/WaterCalcMod.dev.dll"
else
  cp -f "$ROOT/manifest.release.xml" "$OUT/manifest.xml"
fi

echo "=== 完成：$OUT ==="
ls -l "$OUT"
md5sum "$OUT/WaterCalcMod.dll" 2>/dev/null || true
echo "（把 dist/WaterCalcMod 整个文件夹放进 游戏目录/Mods/ 即可安装）"
