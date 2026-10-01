/**
 * ENFilter.dll entry - exports nr_engine API for C# / external plugin hosts.
 * Core logic lives in ENFilterCore (platform-independent).
 */
#include "nr_engine.h"

#if defined(_WIN32)
#include <windows.h>

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved) {
  (void)module;
  (void)reserved;
  switch (reason) {
  case DLL_PROCESS_ATTACH:
  case DLL_THREAD_ATTACH:
  case DLL_THREAD_DETACH:
  case DLL_PROCESS_DETACH:
    break;
  }
  return TRUE;
}
#endif
