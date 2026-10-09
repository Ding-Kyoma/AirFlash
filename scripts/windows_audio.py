"""Core Audio mute guard for finite qualification; no playback dependency."""

import ctypes
import os
import uuid


def _check(result):
    if result < 0:
        raise OSError(f"Core Audio failed (HRESULT 0x{result & 0xFFFFFFFF:08x})")


def _guid(value):
    return ctypes.create_string_buffer(uuid.UUID(value).bytes_le, 16)


def _invoke(pointer, slot, types, *arguments):
    table = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    method = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, *types)(table[slot])
    _check(method(pointer, *arguments))


class _DefaultEndpoint:
    def __init__(self):
        if os.name != "nt":
            raise OSError("Core Audio qualification requires Windows")
        self._pointers = []
        self._initialized = False
        self._ole = ctypes.OleDLL("ole32")
        self._ole.CoInitializeEx.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        self._ole.CoInitializeEx.restype = ctypes.c_long
        self._ole.CoUninitialize.argtypes = []
        self._ole.CoUninitialize.restype = None
        try:
            result = self._ole.CoInitializeEx(None, 0)
            # An existing STA is usable; only balance COM initialization we own.
            if result != -2147417850:  # RPC_E_CHANGED_MODE
                _check(result)
                self._initialized = True
            enumerator = ctypes.c_void_p()
            self._ole.CoCreateInstance.argtypes = [
                ctypes.c_void_p,
                ctypes.c_void_p,
                ctypes.c_ulong,
                ctypes.c_void_p,
                ctypes.POINTER(ctypes.c_void_p),
            ]
            self._ole.CoCreateInstance.restype = ctypes.c_long
            _check(
                self._ole.CoCreateInstance(
                    _guid("BCDE0395-E52F-467C-8E3D-C4579291692E"),
                    None,
                    23,
                    _guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
                    ctypes.byref(enumerator),
                )
            )
            self._pointers.append(enumerator)
            device = ctypes.c_void_p()
            _invoke(
                enumerator,
                4,
                [ctypes.c_int, ctypes.c_int, ctypes.POINTER(ctypes.c_void_p)],
                0,
                0,
                ctypes.byref(device),
            )  # eRender, eConsole
            self._pointers.append(device)
            volume = ctypes.c_void_p()
            _invoke(
                device,
                3,
                [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)],
                _guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
                23,
                None,
                ctypes.byref(volume),
            )
            self._pointers.append(volume)
            self._volume = volume
        except BaseException:
            self.close()
            raise

    @property
    def muted(self):
        value = ctypes.c_int()
        _invoke(self._volume, 15, [ctypes.POINTER(ctypes.c_int)], ctypes.byref(value))
        return bool(value.value)

    @muted.setter
    def muted(self, value):
        _invoke(self._volume, 14, [ctypes.c_int, ctypes.c_void_p], int(value), None)

    def close(self):
        for pointer in reversed(self._pointers):
            table = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p)))
            release = ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(table.contents[2])
            release(pointer)
        self._pointers.clear()
        if self._initialized:
            self._ole.CoUninitialize()
            self._initialized = False


class MutedOutput:
    """Restore the exact endpoint originally muted, even if the default changes."""

    def __enter__(self):
        self._endpoint = _DefaultEndpoint()
        try:
            self._previous = self._endpoint.muted
            self._endpoint.muted = True
            if not self._endpoint.muted:
                raise OSError("Cannot mute local output for an unambiguous acoustic test")
        except BaseException:
            try:
                if hasattr(self, "_previous"):
                    self._endpoint.muted = self._previous
            finally:
                self._endpoint.close()
            raise
        return self

    def __exit__(self, *exception):
        try:
            self._endpoint.muted = self._previous
        finally:
            self._endpoint.close()
