cmake_minimum_required(VERSION 3.16)
project(ens_lte LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_C_STANDARD 99)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# --- srsRAN_4G (PHY + RRC ASN.1 only) ---
set(SRSRAN_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../samples/srsRAN_4G" CACHE PATH "srsRAN_4G tree")
if(NOT EXISTS "${SRSRAN_ROOT}/CMakeLists.txt")
  message(FATAL_ERROR "srsRAN_4G not found at ${SRSRAN_ROOT}")
endif()

set(ENABLE_SRSUE OFF CACHE BOOL "" FORCE)
set(ENABLE_SRSENB OFF CACHE BOOL "" FORCE)
set(ENABLE_SRSEPC OFF CACHE BOOL "" FORCE)
set(ENABLE_GUI OFF CACHE BOOL "" FORCE)
set(ENABLE_RF_PLUGINS OFF CACHE BOOL "" FORCE)
set(ENABLE_UHD OFF CACHE BOOL "" FORCE)
set(ENABLE_BLADERF OFF CACHE BOOL "" FORCE)
set(ENABLE_SOAPYSDR OFF CACHE BOOL "" FORCE)
set(ENABLE_ZEROMQ OFF CACHE BOOL "" FORCE)
set(ENABLE_HARDSIM OFF CACHE BOOL "" FORCE)
set(ENABLE_TTCN3 OFF CACHE BOOL "" FORCE)
set(ENABLE_WERROR OFF CACHE BOOL "" FORCE)
set(ENABLE_ALL_TEST OFF CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(ENABLE_TIMEPROF OFF CACHE BOOL "" FORCE)
set(ENS_LTE_HOST_BUILD ON CACHE BOOL "" FORCE)
set(ENS_LTE_COMPAT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/compat")
# Keep SSE/AVX; avoid AVX2 (32-byte posix_memalign vs free on MinGW).
set(AUTO_DETECT_ISA OFF CACHE BOOL "" FORCE)
set(HAVE_SSE ON CACHE BOOL "" FORCE)
set(HAVE_AVX ON CACHE BOOL "" FORCE)
set(HAVE_AVX2 OFF CACHE BOOL "" FORCE)
set(HAVE_FMA OFF CACHE BOOL "" FORCE)
set(HAVE_AVX2_FMA OFF CACHE BOOL "" FORCE)

# Disable RF hardware in srsRAN (we feed IQ buffers ourselves).
add_compile_definitions(DISABLE_RF ENS_LTE_HOST_BUILD)
include_directories(BEFORE "${ENS_LTE_COMPAT_DIR}")
include_directories("${SRSRAN_ROOT}") # for srsenb/hdr/... includes from common
add_compile_options(-include "${ENS_LTE_COMPAT_DIR}/srsran_win_compat.h")
if(MINGW OR WIN32)
  add_compile_definitions(_WIN32_WINNT=0x0601 WIN32_LEAN_AND_MEAN)
  add_compile_options($<$<COMPILE_LANGUAGE:CXX>:-fpermissive>)
endif()

add_subdirectory("${SRSRAN_ROOT}" "${CMAKE_BINARY_DIR}/srsran" EXCLUDE_FROM_ALL)

add_library(ens_lte SHARED
  src/ens_lte_bridge.cpp
)
# .NET DllImport("ens_lte") expects ens_lte.dll, not libens_lte.dll
set_target_properties(ens_lte PROPERTIES PREFIX "" OUTPUT_NAME "ens_lte")
target_compile_definitions(ens_lte PRIVATE ENS_LTE_EXPORTS DISABLE_RF)
target_include_directories(ens_lte
  PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
  PRIVATE
    ${SRSRAN_ROOT}/lib/include
    ${CMAKE_BINARY_DIR}/srsran/lib/include
)
target_link_libraries(ens_lte PRIVATE
  srsran_phy
  rrc_asn1
  srsran_common
)
if(MINGW OR WIN32)
  target_link_libraries(ens_lte PRIVATE ws2_32)
  # Runtime DLLs next to ens_lte for NeuroSDR
  set(_mingw_bin "C:/msys64/mingw64/bin")
  foreach(_dll libgcc_s_seh-1.dll libstdc++-6.dll libwinpthread-1.dll libfftw3f-3.dll
               libmbedcrypto-16.dll libmbedcrypto.dll)
    if(EXISTS "${_mingw_bin}/${_dll}")
      add_custom_command(TARGET ens_lte POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${_mingw_bin}/${_dll}"
                "${CMAKE_CURRENT_SOURCE_DIR}/../NeuroSDR/native/win-x64/"
      )
    endif()
  endforeach()
endif()

# Install / copy helpers
install(TARGETS ens_lte RUNTIME DESTINATION bin LIBRARY DESTINATION lib)
install(FILES include/ens_lte.h DESTINATION include)

add_custom_command(TARGET ens_lte POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E make_directory
          "${CMAKE_CURRENT_SOURCE_DIR}/../NeuroSDR/native/win-x64"
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
          $<TARGET_FILE:ens_lte>
          "${CMAKE_CURRENT_SOURCE_DIR}/../NeuroSDR/native/win-x64/"
  COMMENT "Copy ens_lte.dll → NeuroSDR/native/win-x64"
)
