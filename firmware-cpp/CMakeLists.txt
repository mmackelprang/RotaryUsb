cmake_minimum_required(VERSION 3.13)

# Initialize the Pico SDK
include($ENV{PICO_SDK_PATH}/external/pico_sdk_import.cmake)

project(rotary_usb C CXX ASM)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

# Initialize the Raspberry Pi Pico SDK
pico_sdk_init()

# Firmware personality. Both entry points share encoder.cpp and are mutually
# exclusive — each defines its own main() and its own TinyUSB descriptor callbacks.
#
#   generic_hid -> main_generic_hid.cpp   vendor HID, runtime config, diagnostics
#   keyboard    -> main.cpp               F1-F12 keyboard HID
#
# NOTE: the default changed from 'keyboard' to 'generic_hid'. A bare `cmake ..`
# now builds the Generic HID firmware. This replaces the old (and error-prone)
# "copy main_generic_hid.cpp over main.cpp" step.
#
# CMake caches this value, so switching modes means re-running cmake with the
# flag — not passing it on every build:
#     cmake -DFIRMWARE_MODE=keyboard ..
set(FIRMWARE_MODE "generic_hid" CACHE STRING
    "Firmware personality: generic_hid or keyboard")
set_property(CACHE FIRMWARE_MODE PROPERTY STRINGS generic_hid keyboard)

if(FIRMWARE_MODE STREQUAL "generic_hid")
    set(FIRMWARE_MAIN main_generic_hid.cpp)
elseif(FIRMWARE_MODE STREQUAL "keyboard")
    set(FIRMWARE_MAIN main.cpp)
else()
    message(FATAL_ERROR
        "FIRMWARE_MODE must be 'generic_hid' or 'keyboard', got '${FIRMWARE_MODE}'")
endif()

message(STATUS "RotaryUsb firmware mode: ${FIRMWARE_MODE} (${FIRMWARE_MAIN})")

add_executable(rotary_usb
    ${FIRMWARE_MAIN}
    encoder.cpp
)

# Make tusb_config.h findable by TinyUSB
target_include_directories(rotary_usb PRIVATE ${CMAKE_CURRENT_LIST_DIR})

# Pull in common dependencies
target_link_libraries(rotary_usb
    pico_stdlib
    tinyusb_device
    tinyusb_board
    hardware_flash
)

# Enable USB output, disable UART output
pico_enable_stdio_usb(rotary_usb 0)
pico_enable_stdio_uart(rotary_usb 1)

# Create map/bin/hex/uf2 files
pico_add_extra_outputs(rotary_usb)
