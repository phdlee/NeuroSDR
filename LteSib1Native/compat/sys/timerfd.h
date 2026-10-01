#pragma once
/* Stub timerfd for MinGW — periodic timers unused in ens_lte SIB1 path. */
#include <errno.h>
#include <time.h>

#ifndef TFD_NONBLOCK
#define TFD_NONBLOCK 0x800
#endif
#ifndef TFD_CLOEXEC
#define TFD_CLOEXEC 0x80000
#endif

static inline int timerfd_create(int clockid, int flags)
{
  (void)clockid;
  (void)flags;
  errno = ENOSYS;
  return -1;
}

static inline int timerfd_settime(int fd, int flags, const struct itimerspec* new_value, struct itimerspec* old_value)
{
  (void)fd;
  (void)flags;
  (void)new_value;
  (void)old_value;
  errno = ENOSYS;
  return -1;
}

static inline int timerfd_gettime(int fd, struct itimerspec* curr_value)
{
  (void)fd;
  (void)curr_value;
  errno = ENOSYS;
  return -1;
}
