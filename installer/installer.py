"""
SAPAccess Installer

Installs MelonLoader (from the latest LavaGang/MelonLoader release) and the
SuperAutoAccessibility mod DLL (from the latest GreenBeanGravy/SuperAutoAccessibility
release) into a Steam install of Super Auto Pets.

The legacy DLL filename was SuperAutoPetsMod.dll; the current filename is
SuperAutoAccessibility.dll. The installer removes any leftover legacy DLL after
installing the new one so MelonLoader does not load both copies of the mod.
"""

import ctypes
import hashlib
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import winreg
import zipfile
from ctypes import wintypes

import requests
import wx


STEAM_APP_ID = "1714040"
GAME_EXE_NAME = "Super Auto Pets.exe"
MELON_LOADER_FOLDER = "MelonLoader"
MODS_FOLDER = "Mods"

MOD_DLL_NAME = "SuperAutoAccessibility.dll"
LEGACY_DLL_NAME = "SuperAutoPetsMod.dll"
MOD_DLL_URL = (
    "https://github.com/GreenBeanGravy/SuperAutoAccessibility/releases/latest/download/"
    "SuperAutoAccessibility.dll"
)

GITHUB_API_URL = "https://api.github.com/repos/LavaGang/MelonLoader/releases/latest"
MELON_ZIP_ASSET_NAME = "MelonLoader.x64.zip"

SELF_RELEASE_API_URL = (
    "https://api.github.com/repos/GreenBeanGravy/SuperAutoAccessibility/releases/latest"
)
INSTALLER_ASSET_NAME = "SAPAccess Installer.exe"

DEFAULT_GAME_PATH = r"C:\Program Files (x86)\Steam\steamapps\common\Super Auto Pets"

WINDOW_TITLE = "SAPAccess Installer"
WINDOW_W = 620
WINDOW_H = 460


def _try_reg(hive, path, value):
    """Return a registry string value, or None on any error."""
    try:
        with winreg.OpenKey(hive, path) as k:
            val, _ = winreg.QueryValueEx(k, value)
            return str(val)
    except OSError:
        return None


def parse_libraryfolders_vdf(steam_root: str) -> list:
    """
    Parse Steam's libraryfolders.vdf and return a list of all library paths.
    Uses simple line scanning - no external VDF library needed.
    """
    vdf_path = os.path.join(steam_root, "steamapps", "libraryfolders.vdf")
    if not os.path.isfile(vdf_path):
        return []
    paths = []
    with open(vdf_path, "r", encoding="utf-8") as f:
        for line in f:
            stripped = line.strip()
            if stripped.startswith('"path"'):
                parts = stripped.split('"')
                if len(parts) >= 4:
                    paths.append(parts[3].replace("\\\\", "\\"))
    return paths


def find_game_path() -> str | None:
    """
    Try several sources to locate the Super Auto Pets installation folder.
    Returns the first valid path found, or None.
    """
    candidates = []

    uninstall_key = (
        f"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Steam App {STEAM_APP_ID}"
    )
    uninstall_key_wow = (
        f"SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Steam App {STEAM_APP_ID}"
    )
    for key in [uninstall_key, uninstall_key_wow]:
        val = _try_reg(winreg.HKEY_LOCAL_MACHINE, key, "InstallLocation")
        if val:
            candidates.append(val)

    steam_roots = []
    for hive, key, value in [
        (winreg.HKEY_CURRENT_USER, "SOFTWARE\\Valve\\Steam", "SteamPath"),
        (winreg.HKEY_LOCAL_MACHINE, "SOFTWARE\\Wow6432Node\\Valve\\Steam", "InstallPath"),
    ]:
        root = _try_reg(hive, key, value)
        if root:
            steam_roots.append(root.replace("/", "\\"))

    for root in steam_roots:
        candidates.append(os.path.join(root, "steamapps", "common", "Super Auto Pets"))
        for lib in parse_libraryfolders_vdf(root):
            candidates.append(os.path.join(lib, "steamapps", "common", "Super Auto Pets"))

    candidates.append(DEFAULT_GAME_PATH)

    for path in candidates:
        if path and validate_game_dir(path):
            return path
    return None


def validate_game_dir(path: str) -> bool:
    return bool(path) and os.path.isfile(os.path.join(path, GAME_EXE_NAME))


def is_melon_loader_installed(game_dir: str) -> bool:
    return os.path.isdir(os.path.join(game_dir, MELON_LOADER_FOLDER))


def _read_file_version(path: str) -> tuple | None:
    """
    Return the Windows FileVersion of a PE file as a (major, minor, build, private)
    tuple, or None if the file is missing / has no version info.
    """
    if not os.path.isfile(path):
        return None
    try:
        version_dll = ctypes.WinDLL("version.dll")
        GetFileVersionInfoSizeW = version_dll.GetFileVersionInfoSizeW
        GetFileVersionInfoSizeW.argtypes = [wintypes.LPCWSTR, wintypes.LPDWORD]
        GetFileVersionInfoSizeW.restype = wintypes.DWORD

        GetFileVersionInfoW = version_dll.GetFileVersionInfoW
        GetFileVersionInfoW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID]
        GetFileVersionInfoW.restype = wintypes.BOOL

        VerQueryValueW = version_dll.VerQueryValueW
        VerQueryValueW.argtypes = [wintypes.LPVOID, wintypes.LPCWSTR, ctypes.POINTER(wintypes.LPVOID), ctypes.POINTER(wintypes.UINT)]
        VerQueryValueW.restype = wintypes.BOOL

        dummy = wintypes.DWORD(0)
        size = GetFileVersionInfoSizeW(path, ctypes.byref(dummy))
        if size == 0:
            return None
        buf = (ctypes.c_byte * size)()
        if not GetFileVersionInfoW(path, 0, size, buf):
            return None
        ptr = wintypes.LPVOID(0)
        length = wintypes.UINT(0)
        if not VerQueryValueW(buf, "\\", ctypes.byref(ptr), ctypes.byref(length)):
            return None

        class FFI(ctypes.Structure):
            _fields_ = [
                ("dwSignature", ctypes.c_uint32),
                ("dwStrucVersion", ctypes.c_uint32),
                ("dwFileVersionMS", ctypes.c_uint32),
                ("dwFileVersionLS", ctypes.c_uint32),
                ("dwProductVersionMS", ctypes.c_uint32),
                ("dwProductVersionLS", ctypes.c_uint32),
                ("dwFileFlagsMask", ctypes.c_uint32),
                ("dwFileFlags", ctypes.c_uint32),
                ("dwFileOS", ctypes.c_uint32),
                ("dwFileType", ctypes.c_uint32),
                ("dwFileSubtype", ctypes.c_uint32),
                ("dwFileDateMS", ctypes.c_uint32),
                ("dwFileDateLS", ctypes.c_uint32),
            ]

        ffi = ctypes.cast(ptr, ctypes.POINTER(FFI)).contents
        return (
            (ffi.dwFileVersionMS >> 16) & 0xFFFF,
            ffi.dwFileVersionMS & 0xFFFF,
            (ffi.dwFileVersionLS >> 16) & 0xFFFF,
            ffi.dwFileVersionLS & 0xFFFF,
        )
    except Exception:
        return None


def get_installed_melon_loader_version(game_dir: str) -> tuple | None:
    """Return the installed MelonLoader's FileVersion tuple, or None if unknown."""
    return _read_file_version(os.path.join(game_dir, MELON_LOADER_FOLDER, "net6", "MelonLoader.dll"))


def parse_release_tag(tag: str) -> tuple:
    """Convert a release tag like 'v0.7.3' or 'v0.7.1 Open-Beta' into a comparable tuple."""
    nums = re.findall(r"\d+", tag or "")
    return tuple(int(n) for n in nums) if nums else (0,)


def _version_lt(a: tuple, b: tuple) -> bool:
    """Compare two version tuples padding the shorter one with zeros."""
    if a is None:
        return True
    n = max(len(a), len(b))
    pa = a + (0,) * (n - len(a))
    pb = b + (0,) * (n - len(b))
    return pa < pb


def format_version(v: tuple | None) -> str:
    return "unknown" if not v else ".".join(str(x) for x in v)


def uninstall_melon_loader(game_dir: str) -> None:
    """
    Remove the MelonLoader install (MelonLoader/ folder + the version.dll proxy)
    so a fresh extract lands cleanly. Leaves Mods/ and UserData/ alone.
    """
    ml_dir = os.path.join(game_dir, MELON_LOADER_FOLDER)
    if os.path.isdir(ml_dir):
        shutil.rmtree(ml_dir, ignore_errors=True)
    for proxy in ("version.dll", "winmm.dll", "dobby.dll"):
        p = os.path.join(game_dir, proxy)
        if os.path.isfile(p):
            try:
                os.remove(p)
            except OSError:
                pass


def get_latest_melon_loader_asset() -> tuple:
    """
    Fetch the latest MelonLoader release from GitHub and return
    (download_url, version_tag) for MelonLoader.x64.zip.
    """
    headers = {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    resp = requests.get(GITHUB_API_URL, headers=headers, timeout=15)
    resp.raise_for_status()
    data = resp.json()
    tag = data["tag_name"]
    for asset in data["assets"]:
        if asset["name"] == MELON_ZIP_ASSET_NAME:
            return asset["browser_download_url"], tag
    raise RuntimeError(
        f"Asset '{MELON_ZIP_ASSET_NAME}' not found in release {tag}. "
        "Check https://github.com/LavaGang/MelonLoader/releases manually."
    )


def download_file(url: str, dest_path: str, progress_cb=None) -> None:
    """
    Stream-download url to dest_path.
    progress_cb(float) is called with values 0.0-1.0 as data arrives.
    """
    resp = requests.get(url, stream=True, timeout=60, allow_redirects=True)
    resp.raise_for_status()
    total = int(resp.headers.get("content-length", 0))
    downloaded = 0
    with open(dest_path, "wb") as f:
        for chunk in resp.iter_content(chunk_size=65536):
            if chunk:
                f.write(chunk)
                downloaded += len(chunk)
                if progress_cb and total:
                    progress_cb(downloaded / total)


def install_melon_loader(game_dir: str, zip_path: str) -> None:
    """
    Extract MelonLoader.x64.zip directly into game_dir.
    The zip contains top-level entries (version.dll, MelonLoader/, etc.)
    that map 1-to-1 onto the game directory - no path rewriting needed.
    """
    with zipfile.ZipFile(zip_path, "r") as zf:
        zf.extractall(game_dir)


def install_mod_dll(game_dir: str, progress_cb=None) -> None:
    """Create Mods folder if needed, download the mod DLL, remove any legacy DLL."""
    mods_dir = os.path.join(game_dir, MODS_FOLDER)
    os.makedirs(mods_dir, exist_ok=True)
    dest = os.path.join(mods_dir, MOD_DLL_NAME)
    download_file(MOD_DLL_URL, dest, progress_cb)

    legacy = os.path.join(mods_dir, LEGACY_DLL_NAME)
    if os.path.isfile(legacy):
        try:
            os.remove(legacy)
        except OSError:
            pass


def run_install(game_dir: str, frame: "InstallerFrame") -> None:
    """
    Run all install steps on a background thread.
    Posts UI updates via wx.CallAfter so they execute on the main thread.
    """
    def log(msg):
        wx.CallAfter(frame.log, msg)

    def prog(frac):
        wx.CallAfter(frame.set_progress, frac)

    def status(msg):
        wx.CallAfter(frame.set_status, msg)

    def done(ok):
        wx.CallAfter(frame.on_done, ok)

    tmp_dir = None
    try:
        status("Validating game folder...")
        if not validate_game_dir(game_dir):
            raise RuntimeError(
                f"'{GAME_EXE_NAME}' not found in:\n{game_dir}\n\n"
                "Use Browse to select the correct Super Auto Pets folder."
            )
        log(f"Game folder: {game_dir}")
        prog(0.05)

        status("Fetching MelonLoader release info...")
        log("Contacting GitHub API for latest MelonLoader release...")
        asset_url, tag = get_latest_melon_loader_asset()
        latest_version = parse_release_tag(tag)
        log(f"Latest MelonLoader: {tag}")
        prog(0.1)

        installed_version = (
            get_installed_melon_loader_version(game_dir) if is_melon_loader_installed(game_dir) else None
        )
        needs_install = installed_version is None or _version_lt(installed_version, latest_version)

        if not needs_install:
            log(f"MelonLoader {format_version(installed_version)} is up to date - skipping.")
            prog(0.45)
        else:
            if installed_version is not None:
                log(f"Installed MelonLoader {format_version(installed_version)} is older than {tag} - updating.")
                status("Removing old MelonLoader install...")
                uninstall_melon_loader(game_dir)

            tmp_dir = tempfile.mkdtemp(prefix="sapaccess_ml_")
            zip_path = os.path.join(tmp_dir, MELON_ZIP_ASSET_NAME)

            status(f"Downloading MelonLoader {tag}...")
            log(f"Downloading {MELON_ZIP_ASSET_NAME}...")

            def ml_prog(frac):
                prog(0.1 + frac * 0.3)

            download_file(asset_url, zip_path, ml_prog)
            log("Download complete.")
            prog(0.4)

            status("Extracting MelonLoader into game folder...")
            log("Extracting MelonLoader...")
            install_melon_loader(game_dir, zip_path)
            log("MelonLoader extracted successfully.")
            prog(0.45)

        status("Downloading SAPAccess mod DLL...")
        log(f"Downloading {MOD_DLL_NAME}...")

        def mod_prog(frac):
            prog(0.45 + frac * 0.5)

        install_mod_dll(game_dir, mod_prog)
        log(f"{MOD_DLL_NAME} installed to Mods folder.")
        prog(1.0)

        status("Installation complete!")
        log("")
        log("=" * 50)
        log("SAPAccess installed successfully!")
        log("Launch Super Auto Pets to use the mod.")
        log("=" * 50)
        done(True)

    except Exception as e:
        log(f"\nERROR: {e}")
        status("Installation failed.")
        done(False)

    finally:
        if tmp_dir and os.path.isdir(tmp_dir):
            try:
                shutil.rmtree(tmp_dir)
            except Exception:
                pass


class InstallerFrame(wx.Frame):
    def __init__(self):
        super().__init__(
            None,
            title=WINDOW_TITLE,
            size=(WINDOW_W, WINDOW_H),
            style=wx.DEFAULT_FRAME_STYLE & ~(wx.RESIZE_BORDER | wx.MAXIMIZE_BOX),
        )
        self._installing = False
        self._build_ui()
        self._auto_detect()
        self.Centre()
        self.Show()

    def _build_ui(self):
        panel = wx.Panel(self)
        outer = wx.BoxSizer(wx.VERTICAL)

        title_font = wx.Font(14, wx.FONTFAMILY_DEFAULT, wx.FONTSTYLE_NORMAL, wx.FONTWEIGHT_BOLD)
        title = wx.StaticText(panel, label="SAPAccess Installer")
        title.SetFont(title_font)

        subtitle_font = wx.Font(9, wx.FONTFAMILY_DEFAULT, wx.FONTSTYLE_ITALIC, wx.FONTWEIGHT_NORMAL)
        subtitle = wx.StaticText(panel, label="Accessibility mod for Super Auto Pets")
        subtitle.SetFont(subtitle_font)

        outer.Add(title, 0, wx.ALIGN_CENTER | wx.TOP, 14)
        outer.Add(subtitle, 0, wx.ALIGN_CENTER | wx.BOTTOM, 10)

        path_sizer = wx.BoxSizer(wx.HORIZONTAL)
        path_label = wx.StaticText(panel, label="Game folder:")
        self._path_ctrl = wx.TextCtrl(panel, style=wx.TE_PROCESS_ENTER, size=(380, -1))
        browse_btn = wx.Button(panel, label="Browse...", size=(80, -1))
        browse_btn.Bind(wx.EVT_BUTTON, self._on_browse)
        path_sizer.Add(path_label, 0, wx.ALIGN_CENTER_VERTICAL | wx.RIGHT, 6)
        path_sizer.Add(self._path_ctrl, 1, wx.ALIGN_CENTER_VERTICAL | wx.RIGHT, 6)
        path_sizer.Add(browse_btn, 0, wx.ALIGN_CENTER_VERTICAL)
        outer.Add(path_sizer, 0, wx.EXPAND | wx.LEFT | wx.RIGHT | wx.BOTTOM, 12)

        status_font = wx.Font(9, wx.FONTFAMILY_DEFAULT, wx.FONTSTYLE_ITALIC, wx.FONTWEIGHT_NORMAL)
        self._status_ctrl = wx.StaticText(panel, label="Ready.")
        self._status_ctrl.SetFont(status_font)
        outer.Add(self._status_ctrl, 0, wx.LEFT | wx.RIGHT | wx.BOTTOM, 12)

        log_font = wx.Font(9, wx.FONTFAMILY_TELETYPE, wx.FONTSTYLE_NORMAL, wx.FONTWEIGHT_NORMAL)
        self._log_ctrl = wx.TextCtrl(
            panel,
            style=wx.TE_MULTILINE | wx.TE_READONLY | wx.TE_RICH2 | wx.HSCROLL,
            size=(-1, 160),
        )
        self._log_ctrl.SetFont(log_font)
        outer.Add(self._log_ctrl, 1, wx.EXPAND | wx.LEFT | wx.RIGHT | wx.BOTTOM, 12)

        self._gauge = wx.Gauge(panel, range=100, size=(-1, 16))
        outer.Add(self._gauge, 0, wx.EXPAND | wx.LEFT | wx.RIGHT | wx.BOTTOM, 12)

        self._install_btn = wx.Button(panel, label="Install SAPAccess", size=(-1, 44))
        btn_font = wx.Font(11, wx.FONTFAMILY_DEFAULT, wx.FONTSTYLE_NORMAL, wx.FONTWEIGHT_BOLD)
        self._install_btn.SetFont(btn_font)
        self._install_btn.SetBackgroundColour(wx.Colour(42, 138, 62))
        self._install_btn.SetForegroundColour(wx.Colour(255, 255, 255))
        self._install_btn.Bind(wx.EVT_BUTTON, self._on_install)
        outer.Add(self._install_btn, 0, wx.EXPAND | wx.LEFT | wx.RIGHT | wx.BOTTOM, 12)

        panel.SetSizer(outer)

    def _auto_detect(self):
        path = find_game_path()
        if path:
            self._path_ctrl.SetValue(path)
            self.log(f"Auto-detected: {path}")
        else:
            self.log("Could not auto-detect game path. Use Browse to locate it.")

    def _on_browse(self, _evt):
        current = self._path_ctrl.GetValue().strip() or DEFAULT_GAME_PATH
        dlg = wx.DirDialog(
            self,
            message="Select Super Auto Pets folder",
            defaultPath=current,
            style=wx.DD_DEFAULT_STYLE,
        )
        if dlg.ShowModal() == wx.ID_OK:
            self._path_ctrl.SetValue(dlg.GetPath())
        dlg.Destroy()

    def _on_install(self, _evt):
        if self._installing:
            return
        game_dir = self._path_ctrl.GetValue().strip()
        if not game_dir:
            wx.MessageBox(
                "Please enter or browse for the game folder.",
                "No Path",
                wx.OK | wx.ICON_ERROR,
                self,
            )
            return
        self._installing = True
        self._install_btn.Disable()
        self._install_btn.SetLabel("Installing...")
        self._gauge.SetValue(0)
        threading.Thread(target=run_install, args=(game_dir, self), daemon=True).start()

    def log(self, msg: str):
        self._log_ctrl.AppendText(msg + "\n")

    def set_status(self, msg: str):
        self._status_ctrl.SetLabel(msg)

    def set_progress(self, frac: float):
        self._gauge.SetValue(int(frac * 100))

    def on_done(self, success: bool):
        self._installing = False
        if success:
            self._install_btn.SetLabel("Installed!")
            self._install_btn.SetBackgroundColour(wx.Colour(80, 80, 80))
            wx.MessageBox(
                "SAPAccess has been installed successfully!\n\n"
                "Launch Super Auto Pets to use the mod.",
                "Installation Complete",
                wx.OK | wx.ICON_INFORMATION,
                self,
            )
        else:
            self._install_btn.Enable()
            self._install_btn.SetLabel("Retry Install")
            self._install_btn.SetBackgroundColour(wx.Colour(192, 57, 43))


class InstallerApp(wx.App):
    def OnInit(self):
        InstallerFrame()
        return True


def _sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def check_self_update() -> None:
    """
    When running as the bundled installer EXE, fetch the latest release of
    GreenBeanGravy/SuperAutoAccessibility, locate the installer asset, and
    SHA-256-compare its bytes against the running EXE. On mismatch, prompt
    the user and hand off to a PowerShell script that replaces the EXE after
    this process exits and relaunches.

    Silently no-ops when running from source (not frozen) or on any error.
    """
    if "--skip-update" in sys.argv:
        return
    if not getattr(sys, "frozen", False):
        return  # running from source

    exe = sys.executable
    if not os.path.isfile(exe):
        return

    try:
        local_sha = _sha256_file(exe)

        resp = requests.get(
            SELF_RELEASE_API_URL,
            headers={
                "User-Agent": "SAPAccess-Installer",
                "Accept": "application/vnd.github+json",
            },
            timeout=15,
        )
        resp.raise_for_status()
        data = resp.json()

        asset_url = None
        remote_sha = None
        for asset in data.get("assets", []):
            if asset.get("name") == INSTALLER_ASSET_NAME:
                asset_url = asset.get("browser_download_url")
                digest = asset.get("digest") or ""
                if digest.lower().startswith("sha256:"):
                    remote_sha = digest.split(":", 1)[1].lower()
                break
        if not asset_url or not remote_sha:
            return  # release does not ship the installer asset (or digest) yet

        if local_sha == remote_sha:
            return  # already up to date

        # Only now download the new EXE (we know we need it).
        r2 = requests.get(
            asset_url,
            headers={"User-Agent": "SAPAccess-Installer"},
            timeout=120,
            allow_redirects=True,
        )
        r2.raise_for_status()
        remote_bytes = r2.content

        MB_YESNO = 0x04
        MB_ICONINFORMATION = 0x40
        IDYES = 6
        result = ctypes.windll.user32.MessageBoxW(
            None,
            "A new version of the SAPAccess Installer is available.\n\n"
            "Update now? The installer will restart automatically.",
            "Installer Update Available",
            MB_YESNO | MB_ICONINFORMATION,
        )
        if result != IDYES:
            return

        exe_dir = os.path.dirname(exe)
        update_path = os.path.join(exe_dir, INSTALLER_ASSET_NAME + ".update")
        with open(update_path, "wb") as f:
            f.write(remote_bytes)

        script_path = os.path.join(tempfile.gettempdir(), "sapaccess_installer_update.ps1")
        pid = os.getpid()
        script_content = (
            f"Start-Sleep -Seconds 2\n"
            f"while (Get-Process -Id {pid} -ErrorAction SilentlyContinue) {{ Start-Sleep -Seconds 1 }}\n"
            f"Copy-Item -Path '{update_path}' -Destination '{exe}' -Force\n"
            f"Remove-Item -Path '{update_path}' -ErrorAction SilentlyContinue\n"
            f"Start-Process -FilePath '{exe}'\n"
            f"Remove-Item -Path '{script_path}' -ErrorAction SilentlyContinue\n"
        )
        with open(script_path, "w", encoding="utf-8") as f:
            f.write(script_content)

        DETACHED_PROCESS = 0x00000008
        CREATE_NEW_PROCESS_GROUP = 0x00000200
        subprocess.Popen(
            [
                "powershell.exe",
                "-ExecutionPolicy", "Bypass",
                "-WindowStyle", "Hidden",
                "-File", script_path,
            ],
            close_fds=True,
            creationflags=DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP,
        )
        sys.exit(0)
    except Exception:
        return


def require_admin() -> None:
    """Re-launch this process with administrator privileges if needed."""
    if ctypes.windll.shell32.IsUserAnAdmin():
        return
    exe = sys.executable
    params = " ".join(sys.argv[1:])
    ret = ctypes.windll.shell32.ShellExecuteW(None, "runas", exe, params, None, 1)
    if ret > 32:
        sys.exit(0)


def main():
    require_admin()
    check_self_update()
    app = InstallerApp(redirect=False)
    app.MainLoop()


if __name__ == "__main__":
    main()
