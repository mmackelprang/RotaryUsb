# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""
The C++ firmware must normalise report IDs before dispatching host-to-device reports.

Why this test is a source scan rather than a behavioural one: the C++ firmware has no host-side
test harness, and test_descriptor_parity.py already establishes that scanning the source is the
only automated guard it has. This checks the one thing whose absence is invisible from the host.

The defect it guards against, observed on hardware 2026-09-02: the device declares an interrupt
OUT endpoint, so a host write arrives through TinyUSB with report_id == 0 and the real report ID
as buffer[0], not through the SET_REPORT control path that populates report_id. The callback
dispatched solely on the parameter, so every config write and every command was silently dropped.
The host cannot detect this -- write() succeeds, the transfer is delivered, and the device simply
ignores it. It presented as "command 0x05 Reset diagnostics leaves the counters untouched".

The CircuitPython firmware is not affected: it calls get_last_received_report(2) / (3), which
resolves the report ID at the CircuitPython layer instead of relying on a callback parameter.
"""

import os
import re

REPO = os.path.join(os.path.dirname(__file__), "..")
MAIN_CPP = os.path.join(REPO, "firmware-cpp", "main_generic_hid.cpp")


def _callback_body():
    with open(MAIN_CPP, encoding="utf-8") as f:
        text = f.read()

    start = text.index("void tud_hid_set_report_cb(")
    # Up to the next top-level function definition.
    end = text.index(chr(10) + "uint16_t tud_hid_get_report_cb(", start)
    return text[start:end]


def test_callback_normalises_report_id_from_the_buffer():
    """report_id == 0 must be resolved from buffer[0] before dispatch."""
    body = _callback_body()

    assert re.search(r"report_id\s*==\s*0", body), (
        "tud_hid_set_report_cb does not handle report_id == 0. Reports delivered over the "
        "interrupt OUT endpoint arrive that way, and without this every host-to-device report "
        "is silently dropped."
    )
    assert re.search(r"report_id\s*=\s*buffer\[0\]", body), (
        "report_id is not recovered from buffer[0]. On the OUT-endpoint path the real report ID "
        "is the first byte of the buffer."
    )


def test_callback_adjusts_buffer_and_length_together():
    """
    Advancing past the ID without shortening the length leaves every guard one byte too generous.

    The dispatch guards are length checks (bufsize >= FULL_CONFIG_SIZE, bufsize >= 2), so a
    stale length is not cosmetic -- it would accept a short config write as valid.
    """
    body = _callback_body()

    assert re.search(r"buffer\s*\+\+|buffer\s*\+=\s*1", body), (
        "buffer is not advanced past the report ID byte."
    )
    assert re.search(r"bufsize\s*--|bufsize\s*-=\s*1", body), (
        "bufsize is not decremented after advancing past the report ID byte."
    )


def test_normalisation_precedes_dispatch():
    """The normalisation is useless after the branches it is meant to feed."""
    body = _callback_body()

    normalise = body.index("buffer[0]")
    first_dispatch = body.index("report_id == 0x02")

    assert normalise < first_dispatch, (
        "report-ID normalisation must run before the dispatch branches, not after."
    )
