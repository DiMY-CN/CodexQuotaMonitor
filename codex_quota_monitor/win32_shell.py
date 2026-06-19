from __future__ import annotations

import ctypes
import logging
import sys
import tkinter as tk
from typing import Callable

from .constants import APP_NAME, TRAY_CALLBACK_MESSAGE, TRAY_ICON_ID
from .utils import truncate_text

LOGGER = logging.getLogger("codex_quota_monitor")

class RECT(ctypes.Structure):
    _fields_ = [
        ("left", ctypes.c_long),
        ("top", ctypes.c_long),
        ("right", ctypes.c_long),
        ("bottom", ctypes.c_long),
    ]


class APPBARDATA(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_ulong),
        ("hWnd", ctypes.c_void_p),
        ("uCallbackMessage", ctypes.c_uint),
        ("uEdge", ctypes.c_uint),
        ("rc", RECT),
        ("lParam", ctypes.c_longlong),
    ]


class POINT(ctypes.Structure):
    _fields_ = [
        ("x", ctypes.c_long),
        ("y", ctypes.c_long),
    ]


class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", ctypes.c_ulong),
        ("Data2", ctypes.c_ushort),
        ("Data3", ctypes.c_ushort),
        ("Data4", ctypes.c_ubyte * 8),
    ]


class NOTIFYICONDATAW(ctypes.Structure):
    _fields_ = [
        ("cbSize", ctypes.c_ulong),
        ("hWnd", ctypes.c_void_p),
        ("uID", ctypes.c_uint),
        ("uFlags", ctypes.c_uint),
        ("uCallbackMessage", ctypes.c_uint),
        ("hIcon", ctypes.c_void_p),
        ("szTip", ctypes.c_wchar * 128),
        ("dwState", ctypes.c_ulong),
        ("dwStateMask", ctypes.c_ulong),
        ("szInfo", ctypes.c_wchar * 256),
        ("uTimeoutOrVersion", ctypes.c_uint),
        ("szInfoTitle", ctypes.c_wchar * 64),
        ("dwInfoFlags", ctypes.c_ulong),
        ("guidItem", GUID),
        ("hBalloonIcon", ctypes.c_void_p),
    ]


def sh_appbar_message(message: int, data: APPBARDATA) -> int:
    shell32 = ctypes.windll.shell32
    shell32.SHAppBarMessage.argtypes = [ctypes.c_uint, ctypes.POINTER(APPBARDATA)]
    shell32.SHAppBarMessage.restype = ctypes.c_size_t
    return int(shell32.SHAppBarMessage(message, ctypes.byref(data)))


def get_taskbar_rect() -> tuple[int, RECT] | None:
    if sys.platform != "win32":
        return None
    data = APPBARDATA()
    data.cbSize = ctypes.sizeof(APPBARDATA)
    result = sh_appbar_message(5, data)
    if not result:
        return None
    return data.uEdge, data.rc


def get_cursor_position() -> tuple[int, int]:
    if sys.platform != "win32":
        return 0, 0
    point = POINT()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(point))
    return int(point.x), int(point.y)


def set_window_topmost(hwnd: int) -> bool:
    if sys.platform != "win32" or not hwnd:
        return False
    hwnd = get_root_window_handle(hwnd)
    hwnd_topmost = ctypes.c_void_p(-1)
    swp_nosize = 0x0001
    swp_nomove = 0x0002
    swp_noactivate = 0x0010
    swp_showwindow = 0x0040
    swp_noownerzorder = 0x0200
    flags = swp_nomove | swp_nosize | swp_noactivate | swp_showwindow | swp_noownerzorder
    user32 = ctypes.windll.user32
    user32.SetWindowPos.argtypes = [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_uint,
    ]
    user32.SetWindowPos.restype = ctypes.c_bool
    return bool(user32.SetWindowPos(ctypes.c_void_p(hwnd), hwnd_topmost, 0, 0, 0, 0, flags))


def set_window_position_topmost(hwnd: int, x: int, y: int, width: int, height: int) -> bool:
    if sys.platform != "win32" or not hwnd:
        return False
    hwnd = get_root_window_handle(hwnd)
    hwnd_topmost = ctypes.c_void_p(-1)
    swp_noactivate = 0x0010
    swp_showwindow = 0x0040
    swp_noownerzorder = 0x0200
    flags = swp_noactivate | swp_showwindow | swp_noownerzorder
    user32 = ctypes.windll.user32
    user32.SetWindowPos.argtypes = [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_int,
        ctypes.c_uint,
    ]
    user32.SetWindowPos.restype = ctypes.c_bool
    return bool(user32.SetWindowPos(ctypes.c_void_p(hwnd), hwnd_topmost, x, y, width, height, flags))


def get_root_window_handle(hwnd: int) -> int:
    if sys.platform != "win32" or not hwnd:
        return hwnd
    ga_root = 2
    user32 = ctypes.windll.user32
    user32.GetAncestor.argtypes = [ctypes.c_void_p, ctypes.c_uint]
    user32.GetAncestor.restype = ctypes.c_void_p
    root = user32.GetAncestor(ctypes.c_void_p(hwnd), ga_root)
    return int(root) if root else hwnd


def get_window_long_ptr(hwnd: int, index: int) -> int:
    user32 = ctypes.windll.user32
    result_type = ctypes.c_ssize_t
    if ctypes.sizeof(ctypes.c_void_p) == 8:
        getter = user32.GetWindowLongPtrW
    else:
        getter = user32.GetWindowLongW
    getter.argtypes = [ctypes.c_void_p, ctypes.c_int]
    getter.restype = result_type
    return int(getter(ctypes.c_void_p(hwnd), index))


def set_window_long_ptr(hwnd: int, index: int, value: int) -> int:
    user32 = ctypes.windll.user32
    result_type = ctypes.c_ssize_t
    if ctypes.sizeof(ctypes.c_void_p) == 8:
        setter = user32.SetWindowLongPtrW
    else:
        setter = user32.SetWindowLongW
    setter.argtypes = [ctypes.c_void_p, ctypes.c_int, result_type]
    setter.restype = result_type
    return int(setter(ctypes.c_void_p(hwnd), index, result_type(value)))


def apply_overlay_window_styles(hwnd: int) -> bool:
    if sys.platform != "win32" or not hwnd:
        return False
    hwnd = get_root_window_handle(hwnd)
    gwl_exstyle = -20
    ws_ex_toolwindow = 0x00000080
    ws_ex_appwindow = 0x00040000
    ws_ex_noactivate = 0x08000000
    swp_nomove = 0x0002
    swp_nosize = 0x0001
    swp_nozorder = 0x0004
    swp_noactivate = 0x0010
    swp_framechanged = 0x0020
    try:
        current = get_window_long_ptr(hwnd, gwl_exstyle)
        updated = (current | ws_ex_toolwindow | ws_ex_noactivate) & ~ws_ex_appwindow
        if updated != current:
            set_window_long_ptr(hwnd, gwl_exstyle, updated)
            ctypes.windll.user32.SetWindowPos(
                ctypes.c_void_p(hwnd),
                None,
                0,
                0,
                0,
                0,
                swp_nomove | swp_nosize | swp_nozorder | swp_noactivate | swp_framechanged,
            )
        return True
    except Exception:
        return False


class SingleInstanceGuard:
    ERROR_ALREADY_EXISTS = 183

    def __init__(self, name: str) -> None:
        self.name = name
        self.handle: int | None = None

    def acquire(self) -> bool:
        if sys.platform != "win32":
            return True
        kernel32 = ctypes.windll.kernel32
        kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p]
        kernel32.CreateMutexW.restype = ctypes.c_void_p
        kernel32.GetLastError.restype = ctypes.c_ulong
        handle = kernel32.CreateMutexW(None, False, self.name)
        if not handle:
            return True
        self.handle = int(handle)
        if int(kernel32.GetLastError()) == self.ERROR_ALREADY_EXISTS:
            self.release()
            return False
        return True

    def release(self) -> None:
        if not self.handle:
            return
        kernel32 = ctypes.windll.kernel32
        kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
        kernel32.CloseHandle.restype = ctypes.c_bool
        kernel32.CloseHandle(ctypes.c_void_p(self.handle))
        self.handle = None


def find_windows_by_title_prefix(prefix: str) -> list[int]:
    if sys.platform != "win32":
        return []
    user32 = ctypes.windll.user32
    windows: list[int] = []

    enum_proc_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    user32.IsWindowVisible.argtypes = [ctypes.c_void_p]
    user32.IsWindowVisible.restype = ctypes.c_bool
    user32.GetWindowTextLengthW.argtypes = [ctypes.c_void_p]
    user32.GetWindowTextLengthW.restype = ctypes.c_int
    user32.GetWindowTextW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
    user32.GetWindowTextW.restype = ctypes.c_int

    def callback(hwnd: int, _lparam: int) -> bool:
        if not hwnd or not user32.IsWindowVisible(ctypes.c_void_p(hwnd)):
            return True
        length = int(user32.GetWindowTextLengthW(ctypes.c_void_p(hwnd)))
        if length <= 0:
            return True
        buffer = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(ctypes.c_void_p(hwnd), buffer, length + 1)
        if buffer.value.startswith(prefix):
            windows.append(int(hwnd))
        return True

    callback_ref = enum_proc_type(callback)
    user32.EnumWindows.argtypes = [enum_proc_type, ctypes.c_void_p]
    user32.EnumWindows.restype = ctypes.c_bool
    user32.EnumWindows(callback_ref, None)
    return windows


def activate_existing_app_window() -> bool:
    if sys.platform != "win32":
        return False
    user32 = ctypes.windll.user32
    user32.ShowWindow.argtypes = [ctypes.c_void_p, ctypes.c_int]
    user32.ShowWindow.restype = ctypes.c_bool
    sw_shownoactivate = 4
    for hwnd in find_windows_by_title_prefix(APP_NAME):
        user32.ShowWindow(ctypes.c_void_p(hwnd), sw_shownoactivate)
        apply_overlay_window_styles(hwnd)
        set_window_topmost(hwnd)
        return True
    return False


class Win32TrayIcon:
    NIM_ADD = 0
    NIM_MODIFY = 1
    NIM_DELETE = 2
    NIM_SETVERSION = 4
    NIF_MESSAGE = 0x00000001
    NIF_ICON = 0x00000002
    NIF_TIP = 0x00000004
    NOTIFYICON_VERSION_4 = 4
    IDI_APPLICATION = 32512
    WM_LBUTTONUP = 0x0202
    WM_RBUTTONUP = 0x0205
    WM_CONTEXTMENU = 0x007B
    GWLP_WNDPROC = -4

    def __init__(
        self,
        root: tk.Tk,
        tooltip: str,
        on_left_click: Callable[[], None],
        on_right_click: Callable[[int, int], None],
    ) -> None:
        self.root = root
        self.hwnd = get_root_window_handle(int(root.winfo_id()))
        self.tooltip = tooltip
        self.on_left_click = on_left_click
        self.on_right_click = on_right_click
        self._added = False
        self._old_wndproc: int | None = None
        self._wndproc_ref: object | None = None

    def install(self) -> bool:
        if sys.platform != "win32" or not self.hwnd:
            return False
        self._subclass_window()
        data = self._data(self.NIF_MESSAGE | self.NIF_ICON | self.NIF_TIP)
        if not self._notify(self.NIM_ADD, data):
            self._restore_window()
            return False
        version_data = self._data(0)
        version_data.uTimeoutOrVersion = self.NOTIFYICON_VERSION_4
        self._notify(self.NIM_SETVERSION, version_data)
        self._added = True
        return True

    def uninstall(self) -> None:
        if self._added:
            self._notify(self.NIM_DELETE, self._data(0))
            self._added = False
        self._restore_window()

    def set_tooltip(self, text: str) -> None:
        self.tooltip = truncate_text(text, 120)
        if self._added:
            self._notify(self.NIM_MODIFY, self._data(self.NIF_TIP))

    def _data(self, flags: int) -> NOTIFYICONDATAW:
        data = NOTIFYICONDATAW()
        data.cbSize = ctypes.sizeof(NOTIFYICONDATAW)
        data.hWnd = ctypes.c_void_p(self.hwnd)
        data.uID = TRAY_ICON_ID
        data.uFlags = flags
        data.uCallbackMessage = TRAY_CALLBACK_MESSAGE
        data.szTip = truncate_text(self.tooltip, 120)
        if flags & self.NIF_ICON:
            user32 = ctypes.windll.user32
            user32.LoadIconW.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
            user32.LoadIconW.restype = ctypes.c_void_p
            data.hIcon = user32.LoadIconW(None, ctypes.c_void_p(self.IDI_APPLICATION))
        return data

    def _notify(self, message: int, data: NOTIFYICONDATAW) -> bool:
        shell32 = ctypes.windll.shell32
        shell32.Shell_NotifyIconW.argtypes = [ctypes.c_ulong, ctypes.POINTER(NOTIFYICONDATAW)]
        shell32.Shell_NotifyIconW.restype = ctypes.c_bool
        return bool(shell32.Shell_NotifyIconW(message, ctypes.byref(data)))

    def _subclass_window(self) -> None:
        if self._old_wndproc is not None:
            return
        result_type = ctypes.c_ssize_t
        wndproc_type = ctypes.WINFUNCTYPE(
            result_type,
            ctypes.c_void_p,
            ctypes.c_uint,
            ctypes.c_size_t,
            ctypes.c_ssize_t,
        )
        self._wndproc_ref = wndproc_type(self._window_proc)
        new_proc = ctypes.cast(self._wndproc_ref, ctypes.c_void_p).value
        user32 = ctypes.windll.user32
        if ctypes.sizeof(ctypes.c_void_p) == 8:
            setter = user32.SetWindowLongPtrW
        else:
            setter = user32.SetWindowLongW
        setter.argtypes = [ctypes.c_void_p, ctypes.c_int, result_type]
        setter.restype = result_type
        self._old_wndproc = int(setter(ctypes.c_void_p(self.hwnd), self.GWLP_WNDPROC, result_type(new_proc)))

    def _restore_window(self) -> None:
        if self._old_wndproc is None:
            return
        result_type = ctypes.c_ssize_t
        user32 = ctypes.windll.user32
        if ctypes.sizeof(ctypes.c_void_p) == 8:
            setter = user32.SetWindowLongPtrW
        else:
            setter = user32.SetWindowLongW
        setter.argtypes = [ctypes.c_void_p, ctypes.c_int, result_type]
        setter.restype = result_type
        setter(ctypes.c_void_p(self.hwnd), self.GWLP_WNDPROC, result_type(self._old_wndproc))
        self._old_wndproc = None
        self._wndproc_ref = None

    def _window_proc(self, hwnd: int, message: int, wparam: int, lparam: int) -> int:
        if message == TRAY_CALLBACK_MESSAGE:
            event = int(lparam)
            if event == self.WM_LBUTTONUP:
                self.root.after(0, self.on_left_click)
                return 0
            if event in (self.WM_RBUTTONUP, self.WM_CONTEXTMENU):
                x, y = get_cursor_position()
                self.root.after(0, lambda: self.on_right_click(x, y))
                return 0
        user32 = ctypes.windll.user32
        user32.CallWindowProcW.argtypes = [
            ctypes.c_void_p,
            ctypes.c_void_p,
            ctypes.c_uint,
            ctypes.c_size_t,
            ctypes.c_ssize_t,
        ]
        user32.CallWindowProcW.restype = ctypes.c_ssize_t
        return int(user32.CallWindowProcW(ctypes.c_void_p(self._old_wndproc or 0), hwnd, message, wparam, lparam))
