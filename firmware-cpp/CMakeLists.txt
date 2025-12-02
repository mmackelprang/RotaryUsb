cmake_minimum_required(VERSION 3.13)

# Initialize the Pico SDK
include($ENV{PICO_SDK_PATH}/external/pico_sdk_import.cmake)

project(rotary_usb C CXX ASM)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

# Initialize the Raspberry Pi Pico SDK
pico_sdk_init()

add_executable(rotary_usb
    main.cpp
    encoder.cpp
)

# Pull in common dependencies
target_link_libraries(rotary_usb
    pico_stdlib
    tinyusb_device
    tinyusb_board
)

# Enable USB output, disable UART output
pico_enable_stdio_usb(rotary_usb 0)
pico_enable_stdio_uart(rotary_usb 1)

# Create map/bin/hex/uf2 files
pico_add_extra_outputs(rotary_usb)
