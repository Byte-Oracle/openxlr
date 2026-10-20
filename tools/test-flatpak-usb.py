#!/usr/bin/env python3
"""Read-only XLR Dock MK.1 acceptance inside the installed Flatpak.

Stop other OpenXLR daemons first. This never writes a hardware control.
It refuses an ambiguous selection when more than one MK.1 Dock is attached.
"""
import argparse
import ctypes as c
from pathlib import Path


def main():
    docks = []
    for vendor in Path('/sys/bus/usb/devices').glob('*/idVendor'):
        try:
            if vendor.read_text().strip() == '0fd9' and vendor.with_name('idProduct').read_text().strip() == '00a6':
                docks.append(vendor.parent)
        except OSError:
            continue
    if len(docks) != 1:
        raise SystemExit(f'Expected exactly one XLR Dock MK.1, found {len(docks)}')
    usb = c.CDLL('libusb-1.0.so.0')
    signatures = {
        'libusb_init': (c.c_int, [c.POINTER(c.c_void_p)]),
        'libusb_exit': (None, [c.c_void_p]),
        'libusb_open_device_with_vid_pid': (c.c_void_p, [c.c_void_p, c.c_ushort, c.c_ushort]),
        'libusb_claim_interface': (c.c_int, [c.c_void_p, c.c_int]),
        'libusb_release_interface': (c.c_int, [c.c_void_p, c.c_int]),
        'libusb_close': (None, [c.c_void_p]),
        'libusb_control_transfer': (c.c_int, [c.c_void_p, c.c_ubyte, c.c_ubyte, c.c_ushort, c.c_ushort, c.c_void_p, c.c_ushort, c.c_uint]),
    }
    for name, (result, args) in signatures.items():
        function = getattr(usb, name); function.restype, function.argtypes = result, args
    context = c.c_void_p()
    if usb.libusb_init(c.byref(context)) != 0:
        raise RuntimeError('libusb initialization failed')
    handle, claimed = None, False
    try:
        handle = usb.libusb_open_device_with_vid_pid(context, 0x0fd9, 0x00a6)
        if not handle:
            raise RuntimeError('Cannot open the Dock; check the host udev rule and USB permission')
        result = usb.libusb_claim_interface(handle, 3)
        if result != 0:
            raise RuntimeError(f'Cannot claim interface 3 ({result}); stop the other daemon first')
        claimed = True
        blocks = []
        for _ in range(2):
            block = c.create_string_buffer(64)
            result = usb.libusb_control_transfer(handle, 0xa1, 0x85, 0, 0x3303, block, 64, 2000)
            if result != 64:
                raise RuntimeError(f'Dock configuration read returned {result}, expected 64')
            blocks.append(bytes(block))
        for offset, size in ((0, 2), (4, 1), (6, 1), (9, 2), (33, 1)):
            if blocks[0][offset:offset+size] != blocks[1][offset:offset+size]:
                raise AssertionError('Hardware controls changed during the read-only check')
        print('PASS: XLR Dock MK.1 opened and claimed; two 64-byte reads, controls unchanged')
    finally:
        if claimed:
            usb.libusb_release_interface(handle, 3)
        if handle:
            usb.libusb_close(handle)
        usb.libusb_exit(context)


if __name__ == '__main__':
    argparse.ArgumentParser(description=__doc__).parse_args()
    main()
