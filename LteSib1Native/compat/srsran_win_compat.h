#pragma once
/* Force-included on MinGW Windows builds of srsRAN / ens_lte.
 * Do NOT include <stdio.h> here — MinGW macros for stdout/stderr break
 * srslog enum class sink_stream_type { stdout, stderr }. */
#include <string.h>
#include <stdlib.h>
#include <math.h>
#include <errno.h>
#include <stdint.h>
#include <time.h>
#include <arpa/inet.h>

#ifndef bzero
#define bzero(p, n) memset((p), 0, (size_t)(n))
#endif
#ifndef bcopy
#define bcopy(src, dst, n) memmove((dst), (src), (size_t)(n))
#endif
#ifndef __cplusplus
#ifndef index
#define index(s, c) strchr((s), (c))
#endif
#ifndef rindex
#define rindex(s, c) strrchr((s), (c))
#endif
#endif

#ifdef ERROR
#undef ERROR
#endif

#ifndef uint
typedef unsigned int uint;
#endif

/* x64 CRT malloc is 16-byte aligned; ENS build disables AVX2 so we stay ≤16. */
static inline int posix_memalign(void** memptr, size_t alignment, size_t size)
{
  if (!memptr || alignment == 0 || (alignment & (alignment - 1)) != 0) {
    return EINVAL;
  }
  (void)alignment;
  void* p = malloc(size ? size : 1);
  if (!p) {
    return ENOMEM;
  }
  *memptr = p;
  return 0;
}

#ifndef TIME_UTC
#define TIME_UTC 1
#endif

static inline int ens_timespec_get(struct timespec* ts, int base)
{
  if (!ts || base != TIME_UTC) {
    return 0;
  }
#if defined(CLOCK_REALTIME)
  if (clock_gettime(CLOCK_REALTIME, ts) == 0) {
    return base;
  }
#endif
  ts->tv_sec  = time(NULL);
  ts->tv_nsec = 0;
  return base;
}

#define timespec_get ens_timespec_get

#ifndef rand_r
static inline int rand_r(unsigned int* seed)
{
  if (!seed) {
    return (int)(rand() & 0x7fff);
  }
  *seed = *seed * 1103515245u + 12345u;
  return (int)((*seed >> 16) & 0x7fff);
}
#endif

#ifndef F_LOCK
#define F_LOCK 1
#endif
#ifndef F_ULOCK
#define F_ULOCK 0
#endif
#ifndef lockf
static inline int lockf(int fd, int cmd, long len)
{
  (void)fd;
  (void)cmd;
  (void)len;
  return 0;
}
#endif

/* POSIX signals missing on MinGW — stubs for support/signal_handler.cc */
#ifndef SIGHUP
#define SIGHUP 1
#endif
#ifndef SIGALRM
#define SIGALRM 14
#endif
#ifndef SIGKILL
#define SIGKILL 9
#endif
#ifndef SIGPIPE
#define SIGPIPE 13
#endif
#ifndef alarm
static inline unsigned alarm(unsigned seconds)
{
  (void)seconds;
  return 0;
}
#endif

static inline struct tm* ens_localtime_r(const time_t* timep, struct tm* result)
{
  if (!timep || !result) {
    return NULL;
  }
  if (localtime_s(result, timep) != 0) {
    return NULL;
  }
  return result;
}
#ifndef localtime_r
#define localtime_r ens_localtime_r
#endif

static inline struct tm* ens_gmtime_r(const time_t* timep, struct tm* result)
{
  if (!timep || !result) {
    return NULL;
  }
  if (gmtime_s(result, timep) != 0) {
    return NULL;
  }
  return result;
}
#ifndef gmtime_r
#define gmtime_r ens_gmtime_r
#endif
