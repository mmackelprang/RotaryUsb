# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""
The HID report descriptor is duplicated in three places that must agree:
the C++ firmware, boot.py's descriptor, and boot.py's report-length tuples.

They agreed only by review until this test existed. This is also the only
automated guard the C++ firmware has at all.
"""

import ast
import os
import re
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "firmware"))

from reports import POSITION_REPORT_SIZE, DIAG_REPORT_SIZE

REPO = os.path.join(os.path.dirname(__file__), "..")
BOOT_PY = os.path.join(REPO, "firmware", "boot.py")
MAIN_CPP = os.path.join(REPO, "firmware-cpp", "main_generic_hid.cpp")


def _strip_comments(text, token):
    return "\n".join(line.split(token, 1)[0] for line in text.splitlines())


def extract_descriptor_bytes(path, pattern, comment_token):
    """
    Pull a descriptor's byte literals out of source.

    Comments MUST be stripped before scanning for hex: both files contain values
    like 0xFF00 and "Report ID 0x01" inside comments, which would otherwise be
    scraped as descriptor bytes.
    """
    with open(path, encoding="utf-8") as f:
        text = f.read()
    match = re.search(pattern, text, re.S)
    assert match is not None, f"descriptor not found in {path}"
    body = _strip_comments(match.group(1), comment_token)
    return [int(h, 16) for h in re.findall(r"0x([0-9A-Fa-f]{2})\b", body)]


def python_descriptor():
    return extract_descriptor_bytes(
        BOOT_PY, r"GENERIC_HID_REPORT_DESCRIPTOR\s*=\s*bytes\(\[(.*?)\]\)", "#")


def cpp_descriptor():
    return extract_descriptor_bytes(
        MAIN_CPP, r"hid_report_descriptor\[\]\s*=\s*\{(.*?)\n\};", "//")


def walk_descriptor(desc):
    """
    Walk HID short items and derive payload size in bytes per (direction, report_id).

    Only short-item encoding is handled; every item in these descriptors is short.
    """
    sizes = {}
    report_id = 0
    report_size = 0
    report_count = 0
    i = 0
    while i < len(desc):
        prefix = desc[i]
        length = prefix & 0x03
        if length == 3:
            length = 4
        tag = prefix & 0xFC
        data = 0
        for k in range(length):
            data |= desc[i + 1 + k] << (8 * k)

        if tag == 0x84:      # Report ID (global)
            report_id = data
        elif tag == 0x74:    # Report Size (global)
            report_size = data
        elif tag == 0x94:    # Report Count (global)
            report_count = data
        elif tag == 0x80:    # Input (main)
            key = ("in", report_id)
            sizes[key] = sizes.get(key, 0) + report_size * report_count
        elif tag == 0x90:    # Output (main)
            key = ("out", report_id)
            sizes[key] = sizes.get(key, 0) + report_size * report_count

        i += 1 + length

    for key, bits in sizes.items():
        assert bits % 8 == 0, f"{key} is not a whole number of bytes: {bits} bits"
    return {key: bits // 8 for key, bits in sizes.items()}


def boot_py_tuple(name):
    """Parse a literal tuple assigned in the usb_hid.Device(...) call in boot.py."""
    with open(BOOT_PY, encoding="utf-8") as f:
        text = _strip_comments(f.read(), "#")
    match = re.search(name + r"\s*=\s*(\([^)]*\))", text)
    assert match is not None, f"{name} not found in boot.py"
    return ast.literal_eval(match.group(1))


# ---- The parity guarantee ----

def test_descriptors_are_byte_identical():
    py = python_descriptor()
    cpp = cpp_descriptor()
    assert py == cpp, (
        f"descriptor divergence: boot.py has {len(py)} bytes, "
        f"main_generic_hid.cpp has {len(cpp)}"
    )


def test_descriptor_is_not_trivially_empty():
    """Guard the extraction itself: a broken regex must not silently pass."""
    assert len(cpp_descriptor()) > 50


# ---- Derived sizes agree with every other declaration of them ----

@pytest.mark.xfail(reason="descriptor still declares the 21-byte report 0x01; Task 3 resizes it")
def test_derived_report_sizes():
    sizes = walk_descriptor(cpp_descriptor())
    assert sizes[("in", 1)] == POSITION_REPORT_SIZE
    assert sizes[("in", 2)] == 106
    assert sizes[("in", 4)] == DIAG_REPORT_SIZE
    assert sizes[("out", 2)] == 106
    assert sizes[("out", 3)] == 2


def test_boot_py_report_ids_cover_descriptor():
    sizes = walk_descriptor(python_descriptor())
    declared = set(boot_py_tuple("report_ids"))
    used = {rid for _, rid in sizes}
    assert used <= declared, f"descriptor uses report IDs not declared: {used - declared}"


def test_boot_py_in_report_lengths_match_descriptor():
    sizes = walk_descriptor(python_descriptor())
    report_ids = boot_py_tuple("report_ids")
    lengths = boot_py_tuple("in_report_lengths")
    assert len(lengths) == len(report_ids)
    for rid, declared in zip(report_ids, lengths):
        assert declared == sizes.get(("in", rid), 0), f"in_report_lengths wrong for ID {rid}"


def test_boot_py_out_report_lengths_match_descriptor():
    sizes = walk_descriptor(python_descriptor())
    report_ids = boot_py_tuple("report_ids")
    lengths = boot_py_tuple("out_report_lengths")
    assert len(lengths) == len(report_ids)
    for rid, declared in zip(report_ids, lengths):
        assert declared == sizes.get(("out", rid), 0), f"out_report_lengths wrong for ID {rid}"
