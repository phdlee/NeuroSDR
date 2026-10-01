cmake_minimum_required(VERSION 3.16)
project(ENFilter LANGUAGES C)

set(CMAKE_C_STANDARD 17)

add_library(ENFilter SHARED
  enfilter_dll.c
  src/nr_engine.c
  src/nr_biquad.c
  src/nr_stages.c
  src/nr_cw.c
  rnnoise/src/celt_lpc.c
  rnnoise/src/denoise.c
  rnnoise/src/kiss_fft.c
  rnnoise/src/pitch.c
  rnnoise/src/rnn.c
  rnnoise/src/rnn_data.c
  rnnoise/src/rnn_reader.c
)
set_target_properties(ENFilter PROPERTIES PREFIX "" OUTPUT_NAME "ENFilter")
target_compile_definitions(ENFilter PRIVATE
  NR_ENGINE_DLL
  NR_ENGINE_BUILD
  _CRT_SECURE_NO_WARNINGS
  _USE_MATH_DEFINES
)
target_include_directories(ENFilter PRIVATE
  ${CMAKE_CURRENT_SOURCE_DIR}/include
  ${CMAKE_CURRENT_SOURCE_DIR}/rnnoise/include
  ${CMAKE_CURRENT_SOURCE_DIR}/rnnoise/src
)
if(MSVC)
  target_compile_options(ENFilter PRIVATE /utf-8 /wd4244 /wd4305)
  set_source_files_properties(ENFilter.def PROPERTIES HEADER_FILE_ONLY TRUE)
  target_link_options(ENFilter PRIVATE /DEF:${CMAKE_CURRENT_SOURCE_DIR}/ENFilter.def)
endif()
