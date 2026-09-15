#!/usr/bin/env bash
# CORDON-LINUX installer (user level, no root required).
#
#   ./install.sh              # install into ~/.local (recommended)
#   ./install.sh --system     # install into /usr/local (needs sudo)
#   ./install.sh --portable DIR   # also create the portable launcher next to the app data
#
# The script only touches:
#   <prefix>/lib/cordon-linux        the application (a private venv)
#   <prefix>/bin/cordon, cordon-gui  launcher entry points
#   <prefix>/share/applications      desktop entry
#   <prefix>/share/icons/hicolor     icon
set -euo pipefail

PREFIX="${HOME}/.local"
SYSTEM=0
PORTABLE=""
GUI=1
PYTHON_BIN="${PYTHON_BIN:-python3}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --system) SYSTEM=1; PREFIX="/usr/local"; shift ;;
    --prefix) PREFIX="$2"; shift 2 ;;
    --portable) PORTABLE="$2"; shift 2 ;;
    --no-gui) GUI=0; shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "Неизвестный параметр: $1" >&2; exit 1 ;;
  esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="${PREFIX}/lib/cordon-linux"
VENV="${APP_DIR}/venv"

# Guard against the most common mistake: running the script from a checkout of `main`, which
# only carries the original Windows launcher (no pyproject.toml, no src/cordon).
if [[ ! -f "${ROOT}/pyproject.toml" || ! -d "${ROOT}/src/cordon" ]]; then
  # note the "|| true": with `set -o pipefail` a failing git call would abort the script
  # before it can explain what went wrong
  BRANCH="$(git -C "${ROOT}" rev-parse --abbrev-ref HEAD 2>/dev/null || true)"
  if [[ -z "${BRANCH}" || "${BRANCH}" == "HEAD" ]]; then
    BRANCH="не определена"
  fi
  cat >&2 <<MSG
Ошибка: это дерево не содержит Linux-порта CORDON.
  каталог: ${ROOT}
  ветка:   ${BRANCH}
  ожидалось: pyproject.toml и src/cordon

Похоже, рабочая копия устарела (порт влит в основную ветку). Обновите её:

  git -C "${ROOT}" pull            # если ветка уже есть
  # или склонируйте заново:
  git clone https://github.com/defaultdj/CORDON-LINUX.git
  cd CORDON-LINUX && ./install.sh
MSG
  exit 1
fi

echo "==> CORDON-LINUX: установка в ${PREFIX}"

if ! command -v "${PYTHON_BIN}" >/dev/null 2>&1; then
  echo "Ошибка: не найден ${PYTHON_BIN}. Установите Python 3.11 или новее." >&2
  exit 1
fi

version="$("${PYTHON_BIN}" -c 'import sys;print("%d.%d"%sys.version_info[:2])')"
major="${version%%.*}"; minor="${version##*.}"
if (( major < 3 || (major == 3 && minor < 10) )); then
  echo "Ошибка: нужен Python 3.10 или новее (найден ${version})." >&2
  exit 1
fi
if (( major == 3 && minor < 11 )) && [[ ${GUI} -eq 1 ]]; then
  echo "Предупреждение: найден Python ${version}; для PySide6 лучше 3.11+." >&2
fi

need_sudo=""
if [[ ${SYSTEM} -eq 1 && ! -w "${PREFIX}" ]]; then
  need_sudo="sudo"
fi

${need_sudo} mkdir -p "${APP_DIR}" "${PREFIX}/bin" "${PREFIX}/share/applications" \
  "${PREFIX}/share/icons/hicolor/scalable/apps"

echo "==> Python: создание виртуального окружения (${version})"
${need_sudo} "${PYTHON_BIN}" -m venv --system-site-packages "${VENV}"

echo "==> Установка пакета (это может занять минуту: PySide6 ~100 МБ)"
if [[ ${GUI} -eq 1 ]]; then
  ${need_sudo} "${VENV}/bin/pip" install --upgrade pip >/dev/null
  if ! ${need_sudo} "${VENV}/bin/pip" install "${ROOT}[gui]"; then
    echo "Предупреждение: не удалось поставить PySide6, ставлю ядро без графического интерфейса." >&2
    ${need_sudo} "${VENV}/bin/pip" install "${ROOT}"
    GUI=0
  fi
else
  ${need_sudo} "${VENV}/bin/pip" install "${ROOT}"
fi

echo "==> Точки входа"
for tool in cordon; do
  ${need_sudo} ln -sf "../lib/cordon-linux/venv/bin/${tool}" "${PREFIX}/bin/${tool}"
done
if [[ ${GUI} -eq 1 ]]; then
  ${need_sudo} ln -sf "../lib/cordon-linux/venv/bin/cordon-gui" "${PREFIX}/bin/cordon-gui"
fi

echo "==> Значок и пункт меню"
${need_sudo} cp "${ROOT}/packaging/cordon-linux.svg" \
  "${PREFIX}/share/icons/hicolor/scalable/apps/cordon-linux.svg"
${need_sudo} sed "s|^Exec=.*|Exec=${PREFIX}/bin/cordon-gui %u|; s|^TryExec=.*|TryExec=${PREFIX}/bin/cordon-gui|" \
  "${ROOT}/packaging/cordon-linux.desktop" > "${PREFIX}/share/applications/cordon-linux.desktop" 2>/dev/null || \
  ${need_sudo} cp "${ROOT}/packaging/cordon-linux.desktop" "${PREFIX}/share/applications/cordon-linux.desktop"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "${PREFIX}/share/applications" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -q "${PREFIX}/share/icons/hicolor" >/dev/null 2>&1 || true
fi

echo
echo "Готово."
echo "  CLI : ${PREFIX}/bin/cordon --help"
if [[ ${GUI} -eq 1 ]]; then
  echo "  GUI : ${PREFIX}/bin/cordon-gui   (или пункт «CORDON-LINUX» в меню приложений)"
else
  echo "  GUI : не установлен (нет PySide6). Поставьте вручную: ${VENV}/bin/pip install PySide6-Essentials"
fi
if [[ -n "${PORTABLE}" ]]; then
  echo "  Портативный режим: ${PREFIX}/bin/cordon --portable \"${PORTABLE}\""
fi
echo
echo "Проверка окружения:  ${PREFIX}/bin/cordon tools"
echo "Создание профиля:    ${PREFIX}/bin/cordon new \"Моя сборка\" --game ~/games/anomaly"
cat <<'HINT'

Полезно поставить системные утилиты (по возможности):
  Debian/Ubuntu: sudo apt install fuse3 fuse-overlayfs p7zip-full libarchive-tools unrar
  Arch:          sudo pacman -S fuse3 fuse-overlayfs p7zip libarchive unrar
  Fedora:        sudo dnf install fuse3 fuse-overlayfs p7zip p7zip-plugins unrar
HINT
