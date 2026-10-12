# napari fenster als jpeg ueber PrintWindow, nur windows, eigener thread

from __future__ import annotations

import ctypes
import io
import os
import sys
from ctypes import wintypes

from PIL import Image, ImageDraw

AVAILABLE = sys.platform == "win32"
# breiter wird verkleinert, sonst entpackt die brille zu lange
MAX_WIDTH = 2560
JPEG_QUALITY = 75
# mauszeiger gehoert nicht zum fensterinhalt, also selbst einzeichnen. pfeil in pixeln bei 1280 breite
CURSOR_SHAPE = [(0, 0), (0, 26), (7, 20), (12, 31), (16, 29), (11, 19), (19, 19)]

SRCCOPY = 0x00CC0020
HALFTONE = 4
PW_CLIENTONLY = 1
# ohne das bleiben opengl flaechen wie die napari ansicht schwarz
PW_RENDERFULLCONTENT = 2


class _BitmapInfoHeader(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG), ("biHeight", wintypes.LONG),
                ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
                ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
                ("biClrImportant", wintypes.DWORD)]


if AVAILABLE:
    _user32 = ctypes.WinDLL("user32")
    _gdi32 = ctypes.WinDLL("gdi32")
    # ohne typen schneidet ctypes die 64 bit handles ab
    _handle = ctypes.c_void_p
    _enum_callback = ctypes.WINFUNCTYPE(wintypes.BOOL, _handle, wintypes.LPARAM)
    _user32.GetDC.restype = _handle
    _user32.GetDC.argtypes = [_handle]
    _user32.ReleaseDC.argtypes = [_handle, _handle]
    _user32.IsIconic.argtypes = [_handle]
    _user32.IsWindowVisible.argtypes = [_handle]
    _user32.GetClientRect.argtypes = [_handle, ctypes.POINTER(wintypes.RECT)]
    _user32.GetWindowRect.argtypes = [_handle, ctypes.POINTER(wintypes.RECT)]
    _user32.ClientToScreen.argtypes = [_handle, ctypes.POINTER(wintypes.POINT)]
    _user32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
    _user32.PrintWindow.argtypes = [_handle, _handle, wintypes.UINT]
    _user32.EnumWindows.argtypes = [_enum_callback, wintypes.LPARAM]
    _user32.GetWindowThreadProcessId.argtypes = [_handle, ctypes.POINTER(wintypes.DWORD)]
    _gdi32.CreateCompatibleDC.restype = _handle
    _gdi32.CreateCompatibleDC.argtypes = [_handle]
    _gdi32.CreateCompatibleBitmap.restype = _handle
    _gdi32.CreateCompatibleBitmap.argtypes = [_handle, ctypes.c_int, ctypes.c_int]
    _gdi32.SelectObject.restype = _handle
    _gdi32.SelectObject.argtypes = [_handle, _handle]
    _gdi32.DeleteObject.argtypes = [_handle]
    _gdi32.DeleteDC.argtypes = [_handle]
    _gdi32.BitBlt.argtypes = [_handle, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                              _handle, ctypes.c_int, ctypes.c_int, wintypes.DWORD]
    _gdi32.StretchBlt.argtypes = [_handle, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                  _handle, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.DWORD]
    _gdi32.SetStretchBltMode.argtypes = [_handle, ctypes.c_int]
    _gdi32.GetDIBits.argtypes = [_handle, _handle, wintypes.UINT, wintypes.UINT, ctypes.c_void_p,
                                 ctypes.POINTER(_BitmapInfoHeader), wintypes.UINT]


class _Canvas:
    # bitmap mit eigenem dc zum reinzeichnen

    def __init__(self, screen_dc, width: int, height: int):
        self.width, self.height = width, height
        self.dc = _gdi32.CreateCompatibleDC(screen_dc)
        self.bitmap = _gdi32.CreateCompatibleBitmap(screen_dc, width, height)
        self._previous = _gdi32.SelectObject(self.dc, self.bitmap)

    def pixels(self):
        # inhalt als bgrx, zeilen von oben nach unten
        data = ctypes.create_string_buffer(self.width * self.height * 4)
        # GetDIBits will die bitmap nicht in einem dc ausgewaehlt haben
        _gdi32.SelectObject(self.dc, self._previous)
        header = _BitmapInfoHeader(ctypes.sizeof(_BitmapInfoHeader), self.width, -self.height, 1, 32,
                                   0, 0, 0, 0, 0, 0)
        rows = _gdi32.GetDIBits(self.dc, self.bitmap, 0, self.height, data, ctypes.byref(header), 0)
        return data if rows == self.height else None

    def close(self) -> None:
        _gdi32.DeleteObject(self.bitmap)
        _gdi32.DeleteDC(self.dc)


def _other_windows(main: int) -> list[int]:
    # sichtbare fenster desselben prozesses ausser dem hauptfenster, das unterste zuerst
    process = os.getpid()
    found = []

    def visit(hwnd, _):
        if hwnd != main and _user32.IsWindowVisible(hwnd):
            owner = wintypes.DWORD()
            _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value == process:
                found.append(hwnd)
        return True

    # EnumWindows liefert von oben nach unten
    _user32.EnumWindows(_enum_callback(visit), 0)
    return found[::-1]


def grab(hwnd: int, pointer: tuple[float, float] | None = None) -> bytes | None:
    # fenster samt menues und zeiger als jpeg, None wenn minimiert, pointer ersetzt die echte maus
    if _user32.IsIconic(hwnd):
        return None
    rect = wintypes.RECT()
    origin = wintypes.POINT(0, 0)
    _user32.GetClientRect(hwnd, ctypes.byref(rect))
    _user32.ClientToScreen(hwnd, ctypes.byref(origin))
    full_width, full_height = rect.right, rect.bottom
    if full_width <= 0 or full_height <= 0:
        return None
    factor = min(MAX_WIDTH / full_width, 1.0)
    width, height = round(full_width * factor), round(full_height * factor)

    screen_dc = _user32.GetDC(None)
    canvases = [_Canvas(screen_dc, full_width, full_height)]
    full = canvases[0]
    try:
        _user32.PrintWindow(hwnd, full.dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT)
        for other in _other_windows(hwnd):
            _user32.GetWindowRect(other, ctypes.byref(rect))
            other_width, other_height = rect.right - rect.left, rect.bottom - rect.top
            if other_width <= 0 or other_height <= 0:
                continue
            popup = _Canvas(screen_dc, other_width, other_height)
            canvases.append(popup)
            if _user32.PrintWindow(other, popup.dc, PW_RENDERFULLCONTENT):
                _gdi32.BitBlt(full.dc, rect.left - origin.x, rect.top - origin.y, other_width, other_height,
                              popup.dc, 0, 0, SRCCOPY)
        result = full
        if factor < 1.0:
            # schon hier verkleinern, das ist schneller als danach und kostet kein gil
            result = _Canvas(screen_dc, width, height)
            canvases.append(result)
            _gdi32.SetStretchBltMode(result.dc, HALFTONE)
            _gdi32.StretchBlt(result.dc, 0, 0, width, height, full.dc, 0, 0, full_width, full_height, SRCCOPY)
        pixels = result.pixels()
    finally:
        for canvas in canvases:
            canvas.close()
        _user32.ReleaseDC(None, screen_dc)
    if pixels is None:
        return None

    image = Image.frombuffer("RGB", (width, height), pixels, "raw", "BGRX", 0, 1)
    cursor = wintypes.POINT()
    _user32.GetCursorPos(ctypes.byref(cursor))
    x, y = (cursor.x - origin.x) * factor, (cursor.y - origin.y) * factor
    if pointer is not None:
        x, y = pointer[0] * width, pointer[1] * height
    size = image.width / 1280
    ImageDraw.Draw(image).polygon([(x + px * size, y + py * size) for px, py in CURSOR_SHAPE],
                                  fill="white", outline="black", width=max(round(2 * size), 1))

    buffer = io.BytesIO()
    image.save(buffer, "JPEG", quality=JPEG_QUALITY)
    return buffer.getvalue()
